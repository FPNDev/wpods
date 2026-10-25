// NTAPD: exposes the AirPods AAP L2CAP channel (PSM 0x1001) as a packet pipe.
// ReadFile returns one inbound SDU, WriteFile sends one outbound SDU.
#include <ntddk.h>
#include <wdf.h>
#include <initguid.h>
#include <bthdef.h>
#include <bthguid.h>
#include <bthioctl.h>
#include <bthddi.h>
#include "..\Public.h"

#define AAP_PSM 0x1001
#define AAP_INBOUND_MTU 1691
// Inbound SDUs land in these buffers; a slot goes back to bthport once user mode has read it.
#define AAP_READ_SLOTS 8

typedef enum _AAP_SLOT_STATE {
    AapSlotIdle,
    AapSlotPosted,
    AapSlotFilled
} AAP_SLOT_STATE;

typedef struct _AAP_SLOT {
    WDFREQUEST Request;
    BRB Brb;
    AAP_SLOT_STATE State;
    UCHAR Data[AAP_INBOUND_MTU];
} AAP_SLOT, *PAAP_SLOT;

typedef struct _AAP_DEVICE_CONTEXT {
    WDFIOTARGET IoTarget;
    WDFQUEUE ReadQueue;
    BTH_PROFILE_DRIVER_INTERFACE Bth;
    volatile LONG Opened;
    BTH_ADDR Address;
    L2CAP_CHANNEL_HANDLE Channel;
    USHORT OutMtu;
    KSPIN_LOCK Lock;
    // Lock guards: writes of Connected, Head, Posted, slot states.
    volatile BOOLEAN Connected;
    ULONG Head;
    ULONG Posted;
    KEVENT NothingPosted;
    AAP_SLOT Slots[AAP_READ_SLOTS];
    // Channel close: sent async from cleanup, CloseDone is set while none is outstanding.
    WDFREQUEST CloseRequest;
    BRB CloseBrb;
    KEVENT CloseDone;
} AAP_DEVICE_CONTEXT, *PAAP_DEVICE_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(AAP_DEVICE_CONTEXT, AapGetDeviceContext)

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(BRB, AapGetRequestBrb)

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD AapEvtDeviceAdd;
EVT_WDF_OBJECT_CONTEXT_CLEANUP AapEvtDeviceCleanup;
EVT_WDF_DEVICE_FILE_CREATE AapEvtFileCreate;
EVT_WDF_FILE_CLEANUP AapEvtFileCleanup;
EVT_WDF_FILE_CLOSE AapEvtFileClose;
EVT_WDF_IO_QUEUE_IO_READ AapEvtIoRead;
EVT_WDF_IO_QUEUE_IO_WRITE AapEvtIoWrite;
EVT_WDF_IO_QUEUE_IO_STOP AapEvtIoStop;
EVT_WDF_REQUEST_COMPLETION_ROUTINE AapEvtOpenComplete;
EVT_WDF_REQUEST_COMPLETION_ROUTINE AapEvtReadComplete;
EVT_WDF_REQUEST_COMPLETION_ROUTINE AapEvtCloseComplete;
EVT_WDF_REQUEST_COMPLETION_ROUTINE AapEvtWriteComplete;

static VOID AapPostSlot(_In_ PAAP_DEVICE_CONTEXT Ctx, _In_ PAAP_SLOT Slot);

// Sends a BRB without allocating: the request's next stack location is formatted in place.
static NTSTATUS
AapSendBrb(
    _In_ PAAP_DEVICE_CONTEXT Ctx,
    _In_ WDFREQUEST Request,
    _In_ PBRB Brb,
    _In_ PFN_WDF_REQUEST_COMPLETION_ROUTINE Completion,
    _In_opt_ WDFCONTEXT Context
    )
{
    IO_STACK_LOCATION stack;

    RtlZeroMemory(&stack, sizeof(stack));
    stack.MajorFunction = IRP_MJ_INTERNAL_DEVICE_CONTROL;
    stack.Parameters.DeviceIoControl.IoControlCode = IOCTL_INTERNAL_BTH_SUBMIT_BRB;
    stack.Parameters.Others.Argument1 = Brb;
    WdfRequestWdmFormatUsingStackLocation(Request, &stack);
    WdfRequestSetCompletionRoutine(Request, Completion, Context);
    if (!WdfRequestSend(Request, Ctx->IoTarget, WDF_NO_SEND_OPTIONS)) {
        return WdfRequestGetStatus(Request);
    }
    return STATUS_PENDING;
}

// Hands buffered packets to pending reads in arrival order. Once the link is gone and no
// packet is left, pending reads fail with STATUS_DEVICE_NOT_CONNECTED.
static VOID
AapPump(_In_ PAAP_DEVICE_CONTEXT Ctx)
{
    for (;;) {
        KIRQL irql;
        WDFREQUEST request;
        PVOID buffer;
        NTSTATUS status;
        ULONG length = 0;
        PAAP_SLOT repost = NULL;
        PAAP_SLOT slot;

        KeAcquireSpinLock(&Ctx->Lock, &irql);
        slot = &Ctx->Slots[Ctx->Head];
        if ((slot->State != AapSlotFilled && Ctx->Connected) ||
            !NT_SUCCESS(WdfIoQueueRetrieveNextRequest(Ctx->ReadQueue, &request))) {
            KeReleaseSpinLock(&Ctx->Lock, irql);
            return;
        }

        if (slot->State == AapSlotFilled) {
            length = slot->Brb.BrbL2caAclTransfer.BufferSize;
            // A buffer smaller than the packet fails with STATUS_BUFFER_TOO_SMALL; the packet stays queued.
            status = WdfRequestRetrieveOutputBuffer(request, length, &buffer, NULL);
            if (NT_SUCCESS(status)) {
                RtlCopyMemory(buffer, slot->Data, length);
                Ctx->Head = (Ctx->Head + 1) % AAP_READ_SLOTS;
                if (Ctx->Connected) {
                    slot->State = AapSlotPosted;
                    if (Ctx->Posted++ == 0) {
                        KeClearEvent(&Ctx->NothingPosted);
                    }
                    repost = slot;
                } else {
                    slot->State = AapSlotIdle;
                }
            } else {
                length = 0;
            }
        } else {
            status = STATUS_DEVICE_NOT_CONNECTED;
        }
        KeReleaseSpinLock(&Ctx->Lock, irql);

        WdfRequestCompleteWithInformation(request, status, length);
        if (repost != NULL) {
            AapPostSlot(Ctx, repost);
        }
    }
}

static VOID
AapReadDone(_In_ PAAP_DEVICE_CONTEXT Ctx, _In_ PAAP_SLOT Slot, _In_ NTSTATUS Status)
{
    KIRQL irql;

    KeAcquireSpinLock(&Ctx->Lock, &irql);
    if (NT_SUCCESS(Status)) {
        Slot->State = AapSlotFilled;
    } else {
        // A failed inbound transfer means the channel is gone.
        Slot->State = AapSlotIdle;
        Ctx->Connected = FALSE;
    }
    if (--Ctx->Posted == 0) {
        KeSetEvent(&Ctx->NothingPosted, IO_NO_INCREMENT, FALSE);
    }
    KeReleaseSpinLock(&Ctx->Lock, irql);
    AapPump(Ctx);
}

static VOID
AapPostSlot(_In_ PAAP_DEVICE_CONTEXT Ctx, _In_ PAAP_SLOT Slot)
{
    WDF_REQUEST_REUSE_PARAMS reuse;
    struct _BRB_L2CA_ACL_TRANSFER* brb = &Slot->Brb.BrbL2caAclTransfer;
    NTSTATUS status;
    KIRQL irql;
    BOOLEAN connected;

    WDF_REQUEST_REUSE_PARAMS_INIT(&reuse, WDF_REQUEST_REUSE_NO_FLAGS, STATUS_SUCCESS);
    status = WdfRequestReuse(Slot->Request, &reuse);
    if (NT_SUCCESS(status)) {
        Ctx->Bth.BthInitializeBrb(&Slot->Brb, BRB_L2CA_ACL_TRANSFER);
        brb->BtAddress = Ctx->Address;
        brb->ChannelHandle = Ctx->Channel;
        brb->TransferFlags = ACL_TRANSFER_DIRECTION_IN | ACL_SHORT_TRANSFER_OK;
        brb->BufferSize = AAP_INBOUND_MTU;
        brb->Buffer = Slot->Data;
        status = AapSendBrb(Ctx, Slot->Request, &Slot->Brb, AapEvtReadComplete, Slot);
    }
    if (status != STATUS_PENDING) {
        AapReadDone(Ctx, Slot, status);
        return;
    }

    // Cleanup clears Connected under the lock before it cancels posted slots, so a send that may have
    // missed its cancel sees FALSE here. Cancelled outside the lock: completion takes it.
    KeAcquireSpinLock(&Ctx->Lock, &irql);
    connected = Ctx->Connected;
    KeReleaseSpinLock(&Ctx->Lock, irql);
    if (!connected) {
        WdfRequestCancelSentRequest(Slot->Request);
    }
}

static VOID
AapIndication(
    _In_opt_ PVOID Context,
    _In_ INDICATION_CODE Indication,
    _In_ PINDICATION_PARAMETERS_ENHANCED Parameters
    )
{
    PAAP_DEVICE_CONTEXT ctx = (PAAP_DEVICE_CONTEXT)Context;
    KIRQL irql;

    UNREFERENCED_PARAMETER(Parameters);

    if (Indication != IndicationRemoteDisconnect) {
        return;
    }
    KeAcquireSpinLock(&ctx->Lock, &irql);
    ctx->Connected = FALSE;
    KeReleaseSpinLock(&ctx->Lock, irql);
    AapPump(ctx);
}

NTSTATUS
DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;

    WDF_DRIVER_CONFIG_INIT(&config, AapEvtDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS
AapEvtDeviceAdd(_In_ WDFDRIVER Driver, _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_IO_QUEUE_CONFIG queueConfig;
    PAAP_DEVICE_CONTEXT ctx;
    ULONG i;

    UNREFERENCED_PARAMETER(Driver);

    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, AapEvtFileCreate, AapEvtFileClose, AapEvtFileCleanup);
    WdfDeviceInitSetFileObjectConfig(DeviceInit, &fileConfig, WDF_NO_OBJECT_ATTRIBUTES);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, BRB);
    WdfDeviceInitSetRequestAttributes(DeviceInit, &attributes);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, AAP_DEVICE_CONTEXT);
    attributes.EvtCleanupCallback = AapEvtDeviceCleanup;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    ctx = AapGetDeviceContext(device);
    ctx->IoTarget = WdfDeviceGetIoTarget(device);
    KeInitializeSpinLock(&ctx->Lock);
    KeInitializeEvent(&ctx->NothingPosted, NotificationEvent, TRUE);
    KeInitializeEvent(&ctx->CloseDone, NotificationEvent, TRUE);

    status = WdfFdoQueryForInterface(
        device,
        &GUID_BTHDDI_PROFILE_DRIVER_INTERFACE,
        (PINTERFACE)&ctx->Bth,
        sizeof(ctx->Bth),
        BTHDDI_PROFILE_DRIVER_INTERFACE_VERSION_FOR_QI,
        NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoRead = AapEvtIoRead;
    queueConfig.EvtIoWrite = AapEvtIoWrite;
    queueConfig.EvtIoStop = AapEvtIoStop;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    // Not power managed so a link loss fails pending reads at once.
    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchManual);
    queueConfig.PowerManaged = WdfFalse;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &ctx->ReadQueue);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    for (i = 0; i < AAP_READ_SLOTS; i++) {
        WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
        attributes.ParentObject = device;
        status = WdfRequestCreate(&attributes, ctx->IoTarget, &ctx->Slots[i].Request);
        if (!NT_SUCCESS(status)) {
            return status;
        }
    }
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = device;
    status = WdfRequestCreate(&attributes, ctx->IoTarget, &ctx->CloseRequest);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    return WdfDeviceCreateDeviceInterface(device, &GUID_NTAPD_DEVICE_INTERFACE, NULL);
}

VOID
AapEvtDeviceCleanup(_In_ WDFOBJECT Object)
{
    PAAP_DEVICE_CONTEXT ctx = AapGetDeviceContext((WDFDEVICE)Object);

    if (ctx->Bth.Interface.InterfaceDereference != NULL) {
        ctx->Bth.Interface.InterfaceDereference(ctx->Bth.Interface.Context);
    }
}

VOID
AapEvtFileCreate(_In_ WDFDEVICE Device, _In_ WDFREQUEST Request, _In_ WDFFILEOBJECT FileObject)
{
    PAAP_DEVICE_CONTEXT ctx = AapGetDeviceContext(Device);
    PBRB brb = AapGetRequestBrb(Request);
    struct _BRB_L2CA_OPEN_ENHANCED_CHANNEL* open = &brb->BrbL2caOpenEnhancedChannel;
    BTH_DEVICE_INFO info;
    WDF_MEMORY_DESCRIPTOR descriptor;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(FileObject);

    if (InterlockedCompareExchange(&ctx->Opened, 1, 0) != 0) {
        WdfRequestComplete(Request, STATUS_SHARING_VIOLATION);
        return;
    }
    // A read or close left outstanding by a timed-out cleanup still owns its request; opening would reuse it.
    if (KeReadStateEvent(&ctx->NothingPosted) == 0 || KeReadStateEvent(&ctx->CloseDone) == 0) {
        InterlockedExchange(&ctx->Opened, 0);
        WdfRequestComplete(Request, STATUS_DEVICE_BUSY);
        return;
    }

    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&descriptor, &info, sizeof(info));
    status = WdfIoTargetSendInternalIoctlSynchronously(
        ctx->IoTarget, WDF_NO_HANDLE, IOCTL_INTERNAL_BTHENUM_GET_DEVINFO, NULL, &descriptor, NULL, NULL);
    // Opening a channel without an ACL link would page the AirPods; only attach to a live link.
    // BTHENUM's BTH_DEVICE_INFO.flags lacks BDIF_CONNECTED even on a live link, so ask bthport for the ACL mode.
    if (NT_SUCCESS(status)) {
        ctx->Bth.BthInitializeBrb(brb, BRB_ACL_GET_MODE);
        brb->BrbAclGetMode.BtAddress = info.address;
        WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&descriptor, brb, sizeof(brb->BrbAclGetMode));
        status = WdfIoTargetSendInternalIoctlOthersSynchronously(
            ctx->IoTarget, WDF_NO_HANDLE, IOCTL_INTERNAL_BTH_SUBMIT_BRB, &descriptor, NULL, NULL, NULL, NULL);
        if (!NT_SUCCESS(status) || brb->BrbAclGetMode.AclMode == ACL_DISCONNECTED) {
            status = STATUS_DEVICE_NOT_CONNECTED;
        }
    }
    if (NT_SUCCESS(status)) {
        ctx->Address = info.address;
        ctx->Bth.BthInitializeBrb(brb, BRB_L2CA_OPEN_ENHANCED_CHANNEL);
        open->BtAddress = info.address;
        open->Psm = AAP_PSM;
        open->ChannelFlags = CF_ROLE_EITHER;
        open->ConfigOut.Flags = CFG_ENHANCED | CFG_MTU;
        open->ConfigOut.ModeConfig.Flags = CM_BASIC;
        open->ConfigOut.Mtu.Min = L2CAP_MIN_MTU;
        open->ConfigOut.Mtu.Max = L2CAP_DEFAULT_MTU;
        open->ConfigOut.Mtu.Preferred = L2CAP_DEFAULT_MTU;
        open->ConfigIn.Flags = CFG_MTU;
        open->ConfigIn.Mtu.Min = L2CAP_MIN_MTU;
        open->ConfigIn.Mtu.Max = AAP_INBOUND_MTU;
        open->ConfigIn.Mtu.Preferred = AAP_INBOUND_MTU;
        open->CallbackFlags = CALLBACK_DISCONNECT;
        open->Callback = AapIndication;
        open->CallbackContext = ctx;
        open->ReferenceObject = WdfDeviceWdmGetDeviceObject(Device);
        status = AapSendBrb(ctx, Request, brb, AapEvtOpenComplete, NULL);
        if (status == STATUS_PENDING) {
            return;
        }
    }

    InterlockedExchange(&ctx->Opened, 0);
    WdfRequestComplete(Request, status);
}

VOID
AapEvtOpenComplete(
    _In_ WDFREQUEST Request,
    _In_ WDFIOTARGET Target,
    _In_ PWDF_REQUEST_COMPLETION_PARAMS Params,
    _In_ WDFCONTEXT Context
    )
{
    PAAP_DEVICE_CONTEXT ctx = AapGetDeviceContext(WdfIoTargetGetDevice(Target));
    struct _BRB_L2CA_OPEN_ENHANCED_CHANNEL* open = &AapGetRequestBrb(Request)->BrbL2caOpenEnhancedChannel;
    NTSTATUS status = Params->IoStatus.Status;
    KIRQL irql;
    ULONG i;

    UNREFERENCED_PARAMETER(Context);

    if (!NT_SUCCESS(status)) {
        InterlockedExchange(&ctx->Opened, 0);
        WdfRequestComplete(Request, status);
        return;
    }

    ctx->Channel = open->ChannelHandle;
    ctx->OutMtu = open->OutResults.Params.Mtu;

    KeAcquireSpinLock(&ctx->Lock, &irql);
    ctx->Connected = TRUE;
    ctx->Head = 0;
    ctx->Posted = AAP_READ_SLOTS;
    KeClearEvent(&ctx->NothingPosted);
    for (i = 0; i < AAP_READ_SLOTS; i++) {
        ctx->Slots[i].State = AapSlotPosted;
    }
    KeReleaseSpinLock(&ctx->Lock, irql);

    // bthport fills reads in submission order, so slot order is packet order.
    for (i = 0; i < AAP_READ_SLOTS; i++) {
        AapPostSlot(ctx, &ctx->Slots[i]);
    }
    WdfRequestComplete(Request, STATUS_SUCCESS);
}

VOID
AapEvtReadComplete(
    _In_ WDFREQUEST Request,
    _In_ WDFIOTARGET Target,
    _In_ PWDF_REQUEST_COMPLETION_PARAMS Params,
    _In_ WDFCONTEXT Context
    )
{
    UNREFERENCED_PARAMETER(Request);
    AapReadDone(AapGetDeviceContext(WdfIoTargetGetDevice(Target)), (PAAP_SLOT)Context, Params->IoStatus.Status);
}

VOID
AapEvtCloseComplete(
    _In_ WDFREQUEST Request,
    _In_ WDFIOTARGET Target,
    _In_ PWDF_REQUEST_COMPLETION_PARAMS Params,
    _In_ WDFCONTEXT Context
    )
{
    UNREFERENCED_PARAMETER(Request);
    UNREFERENCED_PARAMETER(Params);
    UNREFERENCED_PARAMETER(Context);
    KeSetEvent(&AapGetDeviceContext(WdfIoTargetGetDevice(Target))->CloseDone, IO_NO_INCREMENT, FALSE);
}

// State is read under the lock, the cancel runs after release: completion takes the lock.
static VOID
AapCancelPosted(_In_ PAAP_DEVICE_CONTEXT Ctx)
{
    KIRQL irql;
    ULONG i;
    BOOLEAN posted;

    for (i = 0; i < AAP_READ_SLOTS; i++) {
        KeAcquireSpinLock(&Ctx->Lock, &irql);
        posted = Ctx->Slots[i].State == AapSlotPosted;
        KeReleaseSpinLock(&Ctx->Lock, irql);
        if (posted) {
            WdfRequestCancelSentRequest(Ctx->Slots[i].Request);
        }
    }
}

VOID
AapEvtFileCleanup(_In_ WDFFILEOBJECT FileObject)
{
    PAAP_DEVICE_CONTEXT ctx = AapGetDeviceContext(WdfFileObjectGetDevice(FileObject));
    WDFREQUEST request;
    WDF_REQUEST_REUSE_PARAMS reuse;
    KIRQL irql;
    LARGE_INTEGER timeout;
    NTSTATUS wait;
    NTSTATUS status;

    while (NT_SUCCESS(WdfIoQueueRetrieveRequestByFileObject(ctx->ReadQueue, FileObject, &request))) {
        WdfRequestComplete(request, STATUS_CANCELLED);
    }

    // With Connected FALSE no slot is reposted; a repost decided earlier cancels itself in AapPostSlot.
    KeAcquireSpinLock(&ctx->Lock, &irql);
    ctx->Connected = FALSE;
    KeReleaseSpinLock(&ctx->Lock, irql);

    // All reads are gone before the channel closes, so none reaches bthport with a stale handle.
    AapCancelPosted(ctx);
    timeout.QuadPart = -20000000; // 2 s
    wait = KeWaitForSingleObject(&ctx->NothingPosted, Executive, KernelMode, FALSE, &timeout);

    // If bthport ignored the cancel, the close fails the reads still posted. The close is sent async with
    // its BRB in the device context, so a close bthport never completes cannot hold cleanup.
    KeClearEvent(&ctx->CloseDone);
    WDF_REQUEST_REUSE_PARAMS_INIT(&reuse, WDF_REQUEST_REUSE_NO_FLAGS, STATUS_SUCCESS);
    status = WdfRequestReuse(ctx->CloseRequest, &reuse);
    if (NT_SUCCESS(status)) {
        ctx->Bth.BthInitializeBrb(&ctx->CloseBrb, BRB_L2CA_CLOSE_CHANNEL);
        ctx->CloseBrb.BrbL2caCloseChannel.BtAddress = ctx->Address;
        ctx->CloseBrb.BrbL2caCloseChannel.ChannelHandle = ctx->Channel;
        status = AapSendBrb(ctx, ctx->CloseRequest, &ctx->CloseBrb, AapEvtCloseComplete, NULL);
    }
    if (status != STATUS_PENDING) {
        KeSetEvent(&ctx->CloseDone, IO_NO_INCREMENT, FALSE);
    } else if (KeWaitForSingleObject(&ctx->CloseDone, Executive, KernelMode, FALSE, &timeout) == STATUS_TIMEOUT) {
        WdfRequestCancelSentRequest(ctx->CloseRequest);
    }

    // Never waits forever: a read still posted after this keeps the next open out (AapEvtFileCreate).
    if (wait == STATUS_TIMEOUT) {
        AapCancelPosted(ctx);
        (VOID)KeWaitForSingleObject(&ctx->NothingPosted, Executive, KernelMode, FALSE, &timeout);
    }
}

VOID
AapEvtFileClose(_In_ WDFFILEOBJECT FileObject)
{
    InterlockedExchange(&AapGetDeviceContext(WdfFileObjectGetDevice(FileObject))->Opened, 0);
}

VOID
AapEvtIoRead(_In_ WDFQUEUE Queue, _In_ WDFREQUEST Request, _In_ size_t Length)
{
    PAAP_DEVICE_CONTEXT ctx = AapGetDeviceContext(WdfIoQueueGetDevice(Queue));
    NTSTATUS status;

    UNREFERENCED_PARAMETER(Length);

    status = WdfRequestForwardToIoQueue(Request, ctx->ReadQueue);
    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
        return;
    }
    AapPump(ctx);
}

VOID
AapEvtIoWrite(_In_ WDFQUEUE Queue, _In_ WDFREQUEST Request, _In_ size_t Length)
{
    PAAP_DEVICE_CONTEXT ctx = AapGetDeviceContext(WdfIoQueueGetDevice(Queue));
    PBRB brb = AapGetRequestBrb(Request);
    struct _BRB_L2CA_ACL_TRANSFER* transfer = &brb->BrbL2caAclTransfer;
    PVOID buffer;
    NTSTATUS status;

    if (!ctx->Connected) {
        status = STATUS_DEVICE_NOT_CONNECTED;
    } else if (Length > ctx->OutMtu) {
        status = STATUS_INVALID_BUFFER_SIZE;
    } else {
        status = WdfRequestRetrieveInputBuffer(Request, Length, &buffer, NULL);
        if (NT_SUCCESS(status)) {
            ctx->Bth.BthInitializeBrb(brb, BRB_L2CA_ACL_TRANSFER);
            transfer->BtAddress = ctx->Address;
            transfer->ChannelHandle = ctx->Channel;
            transfer->TransferFlags = ACL_TRANSFER_DIRECTION_OUT;
            transfer->BufferSize = (ULONG)Length;
            transfer->Buffer = buffer;
            status = AapSendBrb(ctx, Request, brb, AapEvtWriteComplete, NULL);
        }
    }
    if (status != STATUS_PENDING) {
        WdfRequestComplete(Request, status);
    }
}

VOID
AapEvtWriteComplete(
    _In_ WDFREQUEST Request,
    _In_ WDFIOTARGET Target,
    _In_ PWDF_REQUEST_COMPLETION_PARAMS Params,
    _In_ WDFCONTEXT Context
    )
{
    NTSTATUS status = Params->IoStatus.Status;
    ULONG written = 0;

    UNREFERENCED_PARAMETER(Context);

    if (NT_SUCCESS(status)) {
        written = AapGetRequestBrb(Request)->BrbL2caAclTransfer.BufferSize;
    } else if (!AapGetDeviceContext(WdfIoTargetGetDevice(Target))->Connected) {
        status = STATUS_DEVICE_NOT_CONNECTED;
    }
    WdfRequestCompleteWithInformation(Request, status, written);
}

// Writes in flight to bthport are cancelled on power-down and removal.
VOID
AapEvtIoStop(_In_ WDFQUEUE Queue, _In_ WDFREQUEST Request, _In_ ULONG ActionFlags)
{
    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(ActionFlags);
    WdfRequestCancelSentRequest(Request);
}
