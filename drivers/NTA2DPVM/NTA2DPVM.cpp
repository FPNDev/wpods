// NTA2DPVM: root-enumerated ACX capture device "Airpods A2DP" with the endpoint "Headset (A2DP)".
// User mode writes mono 48 kHz s16 PCM to \\.\NTA2DPVM; running capture streams play it out in
// real time from a small ring. IOCTL_NTA2DPVM_WAIT_CAPTURE_STATE reports whether anyone captures.
#include <ntddk.h>
#include <windef.h>
#include <wdf.h>
#include <acx.h>
#include <ks.h>
#include <mmsystem.h>
#include <ksmedia.h>
#include <ntstrsafe.h>
#include "..\Public.h"

namespace {

constexpr ULONG PoolTag = '2ATN';
constexpr ULONG SampleRate = 48000;
constexpr ULONG RingSamples = 8192; // power of two
// Samples older than this are dropped on write: bounds capture latency to ~85 ms.
constexpr ULONG MaxBacklogSamples = 4096;
constexpr size_t MaxWriteBytes = 65536;
constexpr ULONG MaxPackets = 2;
// RT packet size comes from the client: cap it at 1 s of audio.
constexpr ULONG MaxPacketBytes = SampleRate * sizeof(SHORT);

static_assert((RingSamples & (RingSamples - 1)) == 0, "ring size must be a power of two");
static_assert(MaxBacklogSamples * 2 <= RingSamples, "a write must never overwrite unread samples");

// The app's control device is separate from the audio device so only it carries this ACL.
DECLARE_CONST_UNICODE_STRING(ControlLink, L"\\DosDevices\\Global\\NTA2DPVM");
DECLARE_CONST_UNICODE_STRING(ControlSddl, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)");
DECLARE_CONST_UNICODE_STRING(CircuitName, L"Nta2dpvm0"); // INF interface reference string

// {5c57b105-5ecf-4e87-a448-b83f20739d61}
const GUID CaptureComponentId =
    { 0x5c57b105, 0x5ecf, 0x4e87, { 0xa4, 0x48, 0xb8, 0x3f, 0x20, 0x73, 0x9d, 0x61 } };
// {17a330b3-1a96-463f-841f-679f7a9a3db8}
const GUID MicrophonePinName =
    { 0x17a330b3, 0x1a96, 0x463f, { 0x84, 0x1f, 0x67, 0x9f, 0x7a, 0x9a, 0x3d, 0xb8 } };

KSDATAFORMAT_WAVEFORMATEXTENSIBLE Pcm48000Mono = {
    {
        sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE), 0, 0, 0,
        STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
        STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
        STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
    },
    {
        { WAVE_FORMAT_EXTENSIBLE, 1, SampleRate, SampleRate * 2, 2, 16,
          sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX) },
        16,
        KSAUDIO_SPEAKER_MONO,
        STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM)
    }
};

LONGLONG QpcFrequency;
LONG ControlCount; // numbers control device names

struct DEVICE_CONTEXT {
    KSPIN_LOCK Lock; // guards the ring, RunningStreams and every stream's position fields
    ULONG ReadPos;   // free-running sample counters
    ULONG WritePos;
    ULONG RunningStreams;
    WDFDEVICE Control;  // null when another instance owns the control device
    WDFQUEUE WaitQueue; // on Control
    ACXCIRCUIT Circuit;
    SHORT Ring[RingSamples];
};
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetDeviceContext)

struct CONTROL_CONTEXT {
    KSPIN_LOCK Lock;        // held while Device is used, so the audio device's cleanup waits for it
    DEVICE_CONTEXT* Device; // null once the audio device is gone
};
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(CONTROL_CONTEXT, GetControlContext)

struct STREAM_CONTEXT {
    DEVICE_CONTEXT* Device;
    WDFTIMER Timer;
    PSHORT Packet[MaxPackets];
    ULONG PacketCount;
    ULONG PacketSamples;
    // Guarded by Device->Lock.
    bool Running;
    ULONG CurrentPacket;        // packet being filled
    ULONG BasePacket;           // CurrentPacket at the last Run
    LONGLONG BaseQpc;           // QPC at the last Run
    LONGLONG CurrentPacketQpc;  // start of CurrentPacket
    LONGLONG LastPacketQpc;     // start of CurrentPacket - 1
};
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(STREAM_CONTEXT, GetStreamContext)

LONGLONG SamplesToQpc(ULONGLONG Samples)
{
    return static_cast<LONGLONG>((Samples / SampleRate) * QpcFrequency +
        (Samples % SampleRate) * QpcFrequency / SampleRate);
}

// Absolute end of packet N, so timer latency never accumulates into drift.
LONGLONG PacketEndQpc(const STREAM_CONTEXT* S, ULONG Packet)
{
    return S->BaseQpc + SamplesToQpc(static_cast<ULONGLONG>(Packet + 1 - S->BasePacket) * S->PacketSamples);
}

void ArmTimer(const STREAM_CONTEXT* S, LONGLONG DueQpc, LONGLONG NowQpc)
{
    LONGLONG hns = (DueQpc - NowQpc) * 10'000'000 / QpcFrequency;
    WdfTimerStart(S->Timer, -(hns > 0 ? hns : 1));
}

void WriteRing(DEVICE_CONTEXT* D, const SHORT* Samples, ULONG Count)
{
    if (Count > MaxBacklogSamples) {
        Samples += Count - MaxBacklogSamples;
        Count = MaxBacklogSamples;
    }

    KIRQL irql;
    KeAcquireSpinLock(&D->Lock, &irql);
    ULONG at = D->WritePos & (RingSamples - 1);
    ULONG first = min(Count, RingSamples - at);
    RtlCopyMemory(&D->Ring[at], Samples, first * sizeof(SHORT));
    RtlCopyMemory(D->Ring, Samples + first, (Count - first) * sizeof(SHORT));
    D->WritePos += Count;
    if (D->WritePos - D->ReadPos > MaxBacklogSamples) {
        D->ReadPos = D->WritePos - MaxBacklogSamples;
    }
    KeReleaseSpinLock(&D->Lock, irql);
}

// Caller holds D->Lock. Missing samples become silence.
void ReadRingLocked(DEVICE_CONTEXT* D, SHORT* Out, ULONG Count)
{
    ULONG n = min(Count, D->WritePos - D->ReadPos);
    ULONG at = D->ReadPos & (RingSamples - 1);
    ULONG first = min(n, RingSamples - at);
    RtlCopyMemory(Out, &D->Ring[at], first * sizeof(SHORT));
    RtlCopyMemory(Out + first, D->Ring, (n - first) * sizeof(SHORT));
    RtlZeroMemory(Out + n, (Count - n) * sizeof(SHORT));
    D->ReadPos += n;
}

// Caller holds Device->Lock. Returns true when the device-wide capture state flips.
bool SetRunningLocked(STREAM_CONTEXT* S, bool Running)
{
    if (S->Running == Running) {
        return false;
    }
    S->Running = Running;
    if (Running) {
        return ++S->Device->RunningStreams == 1;
    }
    return --S->Device->RunningStreams == 0;
}

void CompleteWait(WDFREQUEST Request, ULONG State)
{
    PVOID out;
    NTSTATUS status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG), &out, nullptr);
    if (NT_SUCCESS(status)) {
        *static_cast<PULONG>(out) = State;
    }
    WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? sizeof(ULONG) : 0);
}

void CompleteWaiters(DEVICE_CONTEXT* D, ULONG State)
{
    if (D->WaitQueue == nullptr) {
        return;
    }
    WDFREQUEST request;
    while (NT_SUCCESS(WdfIoQueueRetrieveNextRequest(D->WaitQueue, &request))) {
        CompleteWait(request, State);
    }
}

void StopStream(STREAM_CONTEXT* S, bool ResetPosition)
{
    KIRQL irql;
    KeAcquireSpinLock(&S->Device->Lock, &irql);
    bool changed = SetRunningLocked(S, false);
    if (ResetPosition) {
        S->CurrentPacket = 0;
        S->CurrentPacketQpc = 0;
        S->LastPacketQpc = 0;
    }
    KeReleaseSpinLock(&S->Device->Lock, irql);
    WdfTimerStop(S->Timer, TRUE);
    if (changed) {
        CompleteWaiters(S->Device, 0);
    }
}

void FreePackets(PACX_RTPACKET Packets, ULONG Count)
{
    for (ULONG i = 0; i < Count; i++) {
        PMDL mdl = Packets[i].RtPacketBuffer.u.MdlType.Mdl;
        if ((mdl->MdlFlags & MDL_MAPPED_TO_SYSTEM_VA) != 0) {
            MmUnmapLockedPages(mdl->MappedSystemVa, mdl);
        }
        MmFreePagesFromMdl(mdl);
        ExFreePool(mdl);
    }
    ExFreePoolWithTag(Packets, PoolTag);
}

} // namespace

extern "C" DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD EvtDeviceAdd;
EVT_WDF_DEVICE_PREPARE_HARDWARE EvtDevicePrepareHardware;
EVT_WDF_OBJECT_CONTEXT_CLEANUP EvtDeviceCleanup;
EVT_WDF_FILE_CLEANUP EvtFileCleanup;
EVT_WDF_IO_QUEUE_IO_WRITE EvtIoWrite;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL EvtIoDeviceControl;
EVT_ACX_CIRCUIT_CREATE_STREAM EvtCircuitCreateStream;
EVT_ACX_STREAM_PREPARE_HARDWARE EvtStreamPrepareHardware;
EVT_ACX_STREAM_RELEASE_HARDWARE EvtStreamReleaseHardware;
EVT_ACX_STREAM_RUN EvtStreamRun;
EVT_ACX_STREAM_PAUSE EvtStreamPause;
EVT_ACX_STREAM_GET_HW_LATENCY EvtStreamGetHwLatency;
EVT_ACX_STREAM_ALLOCATE_RTPACKETS EvtStreamAllocateRtPackets;
EVT_ACX_STREAM_FREE_RTPACKETS EvtStreamFreeRtPackets;
EVT_ACX_STREAM_GET_CURRENT_PACKET EvtStreamGetCurrentPacket;
EVT_ACX_STREAM_GET_CAPTURE_PACKET EvtStreamGetCapturePacket;
EVT_ACX_STREAM_GET_PRESENTATION_POSITION EvtStreamGetPresentationPosition;
EVT_WDF_TIMER EvtPacketTimer;

static NTSTATUS CreateCaptureCircuit(WDFDEVICE Device, ACXCIRCUIT* Circuit)
{
    PACXCIRCUIT_INIT init = AcxCircuitInitAllocate(Device);
    if (init == nullptr) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    AcxCircuitInitSetComponentId(init, &CaptureComponentId);
    AcxCircuitInitSetCircuitType(init, AcxCircuitTypeCapture);
    NTSTATUS status = AcxCircuitInitAssignName(init, &CircuitName);
    if (NT_SUCCESS(status)) {
        status = AcxCircuitInitAssignAcxCreateStreamCallback(init, EvtCircuitCreateStream);
    }
    if (NT_SUCCESS(status)) {
        WDF_OBJECT_ATTRIBUTES attributes;
        WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
        status = AcxCircuitCreate(Device, &attributes, &init, Circuit);
    }
    if (!NT_SUCCESS(status)) {
        if (init != nullptr) {
            AcxCircuitInitFree(init);
        }
        return status;
    }
    ACXCIRCUIT circuit = *Circuit;

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = circuit;
    ACXPIN pins[2];
    ACX_PIN_CONFIG pinConfig;

    // Streaming pin: capture clients' streams are created on it.
    ACX_PIN_CONFIG_INIT(&pinConfig);
    pinConfig.Type = AcxPinTypeSource;
    pinConfig.Communication = AcxPinCommunicationSink;
    pinConfig.Category = &KSCATEGORY_AUDIO;
    pinConfig.MaxStreams = 1;
    status = AcxPinCreate(circuit, &attributes, &pinConfig, &pins[0]);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    // Bridge pin: the microphone endpoint.
    ACX_PIN_CONFIG_INIT(&pinConfig);
    pinConfig.Type = AcxPinTypeSink;
    pinConfig.Communication = AcxPinCommunicationNone;
    pinConfig.Category = &KSNODETYPE_MICROPHONE;
    pinConfig.Name = &MicrophonePinName;
    status = AcxPinCreate(circuit, &attributes, &pinConfig, &pins[1]);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    ACX_JACK_CONFIG jackConfig;
    ACX_JACK_CONFIG_INIT(&jackConfig);
    jackConfig.Description.ChannelMapping = KSAUDIO_SPEAKER_MONO;
    jackConfig.Description.ConnectionType = AcxConnTypeAtapiInternal;
    jackConfig.Description.GeoLocation = AcxGeoLocFront;
    jackConfig.Description.GenLocation = AcxGenLocPrimaryBox;
    jackConfig.Description.PortConnection = AcxPortConnIntegratedDevice;
    WDF_OBJECT_ATTRIBUTES jackAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&jackAttributes);
    jackAttributes.ParentObject = pins[1];
    ACXJACK jack;
    status = AcxJackCreate(pins[1], &jackAttributes, &jackConfig, &jack);
    if (NT_SUCCESS(status)) {
        status = AcxPinAddJacks(pins[1], &jack, 1);
    }
    if (!NT_SUCCESS(status)) {
        return status;
    }

    ACX_DATAFORMAT_CONFIG formatConfig;
    ACX_DATAFORMAT_CONFIG_INIT_KS(&formatConfig, &Pcm48000Mono);
    ACXDATAFORMAT format;
    status = AcxDataFormatCreate(Device, &attributes, &formatConfig, &format);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    ACXDATAFORMATLIST formatList = AcxPinGetRawDataFormatList(pins[0]);
    if (formatList == nullptr) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    status = AcxDataFormatListAddDataFormat(formatList, format);
    if (NT_SUCCESS(status)) {
        status = AcxDataFormatListAssignDefaultDataFormat(formatList, format);
    }
    if (!NT_SUCCESS(status)) {
        return status;
    }

    return AcxCircuitAddPins(circuit, pins, RTL_NUMBER_OF(pins));
}

// Serves \\.\NTA2DPVM for the app. Lives until the audio device's cleanup deletes it.
static NTSTATUS CreateControlDevice(WDFDRIVER Driver, WDFDEVICE Device)
{
    PAGED_CODE();

    PWDFDEVICE_INIT init = WdfControlDeviceInitAllocate(Driver, &ControlSddl);
    if (init == nullptr) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    WdfDeviceInitSetIoType(init, WdfDeviceIoBuffered);
    // Unique per creation: a deleted control device lingers while the app still holds a handle.
    WCHAR nameBuffer[32];
    UNICODE_STRING name;
    RtlInitEmptyUnicodeString(&name, nameBuffer, sizeof(nameBuffer));
    NTSTATUS status = RtlUnicodeStringPrintf(&name, L"\\Device\\NTA2DPVM%ld", InterlockedIncrement(&ControlCount));
    if (NT_SUCCESS(status)) {
        status = WdfDeviceInitAssignName(init, &name);
    }
    if (!NT_SUCCESS(status)) {
        WdfDeviceInitFree(init);
        return status;
    }

    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, WDF_NO_EVENT_CALLBACK, WDF_NO_EVENT_CALLBACK, EvtFileCleanup);
    WdfDeviceInitSetFileObjectConfig(init, &fileConfig, WDF_NO_OBJECT_ATTRIBUTES);

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, CONTROL_CONTEXT);
    WDFDEVICE control;
    status = WdfDeviceCreate(&init, &attributes, &control);
    if (!NT_SUCCESS(status)) {
        WdfDeviceInitFree(init);
        return status;
    }
    CONTROL_CONTEXT* c = GetControlContext(control);
    KeInitializeSpinLock(&c->Lock);
    DEVICE_CONTEXT* d = GetDeviceContext(Device);
    c->Device = d;

    // Control device queues cannot be power-managed.
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.PowerManaged = WdfFalse;
    queueConfig.EvtIoWrite = EvtIoWrite;
    queueConfig.EvtIoDeviceControl = EvtIoDeviceControl;
    status = WdfIoQueueCreate(control, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    WDFQUEUE waitQueue = nullptr;
    if (NT_SUCCESS(status)) {
        WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchManual);
        queueConfig.PowerManaged = WdfFalse;
        status = WdfIoQueueCreate(control, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &waitQueue);
    }
    // Not WdfDeviceCreateSymbolicLink: WDF removes that link only once the last handle closes,
    // which would block the next instance's link. EvtDeviceCleanup removes this one.
    if (NT_SUCCESS(status)) {
        status = IoCreateSymbolicLink(const_cast<PUNICODE_STRING>(&ControlLink), &name);
    }
    if (!NT_SUCCESS(status)) {
        WdfObjectDelete(control);
        // Root-enumerated, so normally one instance: any further instance runs without a control device.
        return status == STATUS_OBJECT_NAME_COLLISION ? STATUS_SUCCESS : status;
    }
    d->WaitQueue = waitQueue;
    d->Control = control;
    WdfControlFinishInitializing(control);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    LARGE_INTEGER frequency;
    KeQueryPerformanceCounter(&frequency);
    QpcFrequency = frequency.QuadPart;

    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    WDFDRIVER driver;
    NTSTATUS status = WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, &driver);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    ACX_DRIVER_CONFIG acxConfig;
    ACX_DRIVER_CONFIG_INIT(&acxConfig);
    return AcxDriverInitialize(driver, &acxConfig);
}

_Use_decl_annotations_
NTSTATUS EvtDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT DeviceInit)
{
    ACX_DEVICEINIT_CONFIG acxInit;
    ACX_DEVICEINIT_CONFIG_INIT(&acxInit);
    NTSTATUS status = AcxDeviceInitInitialize(DeviceInit, &acxInit);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_PNPPOWER_EVENT_CALLBACKS pnpCallbacks;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpCallbacks);
    pnpCallbacks.EvtDevicePrepareHardware = EvtDevicePrepareHardware;
    WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnpCallbacks);

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    attributes.EvtCleanupCallback = EvtDeviceCleanup;
    WDFDEVICE device;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    DEVICE_CONTEXT* d = GetDeviceContext(device);
    KeInitializeSpinLock(&d->Lock);

    ACX_DEVICE_CONFIG acxConfig;
    ACX_DEVICE_CONFIG_INIT(&acxConfig);
    status = AcxDeviceInitialize(device, &acxConfig);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    return CreateControlDevice(Driver, device);
}

// Detaches and deletes the control device. Runs at PASSIVE_LEVEL after the streams, its children, are gone.
_Use_decl_annotations_
VOID EvtDeviceCleanup(WDFOBJECT Object)
{
    PAGED_CODE();

    DEVICE_CONTEXT* d = GetDeviceContext(Object);
    if (d->Control == nullptr) {
        return;
    }
    CONTROL_CONTEXT* c = GetControlContext(d->Control);
    KIRQL irql;
    // Waits out any handler using d; later ones see null and fail.
    KeAcquireSpinLock(&c->Lock, &irql);
    c->Device = nullptr;
    KeReleaseSpinLock(&c->Lock, irql);
    (VOID)IoDeleteSymbolicLink(const_cast<PUNICODE_STRING>(&ControlLink));
    WdfIoQueuePurgeSynchronously(d->WaitQueue);
    WdfObjectDelete(d->Control);
}

// The static circuit is created and added on the first start and stays for the device's lifetime.
_Use_decl_annotations_
NTSTATUS EvtDevicePrepareHardware(WDFDEVICE Device, WDFCMRESLIST ResourcesRaw, WDFCMRESLIST ResourcesTranslated)
{
    UNREFERENCED_PARAMETER(ResourcesRaw);
    UNREFERENCED_PARAMETER(ResourcesTranslated);

    DEVICE_CONTEXT* d = GetDeviceContext(Device);
    if (d->Circuit != nullptr) {
        return STATUS_SUCCESS;
    }
    ACXCIRCUIT circuit;
    NTSTATUS status = CreateCaptureCircuit(Device, &circuit);
    if (NT_SUCCESS(status)) {
        status = AcxDeviceAddCircuit(Device, circuit);
    }
    if (NT_SUCCESS(status)) {
        d->Circuit = circuit;
    }
    return status;
}

_Use_decl_annotations_
VOID EvtFileCleanup(WDFFILEOBJECT FileObject)
{
    CONTROL_CONTEXT* c = GetControlContext(WdfFileObjectGetDevice(FileObject));
    for (;;) {
        // Once detached, the audio device's cleanup has purged the wait queue.
        NTSTATUS status = STATUS_DEVICE_NOT_CONNECTED;
        WDFREQUEST request = nullptr;
        KIRQL irql;
        KeAcquireSpinLock(&c->Lock, &irql);
        if (c->Device != nullptr) {
            status = WdfIoQueueRetrieveRequestByFileObject(c->Device->WaitQueue, FileObject, &request);
        }
        KeReleaseSpinLock(&c->Lock, irql);
        if (!NT_SUCCESS(status)) {
            break;
        }
        WdfRequestComplete(request, STATUS_CANCELLED);
    }
}

_Use_decl_annotations_
VOID EvtIoWrite(WDFQUEUE Queue, WDFREQUEST Request, size_t Length)
{
    NTSTATUS status = STATUS_INVALID_BUFFER_SIZE;
    if (Length <= MaxWriteBytes && Length % sizeof(SHORT) == 0) {
        PVOID buffer;
        status = WdfRequestRetrieveInputBuffer(Request, Length, &buffer, nullptr);
        if (NT_SUCCESS(status)) {
            CONTROL_CONTEXT* c = GetControlContext(WdfIoQueueGetDevice(Queue));
            KIRQL irql;
            KeAcquireSpinLock(&c->Lock, &irql);
            if (c->Device != nullptr) {
                WriteRing(c->Device, static_cast<const SHORT*>(buffer), static_cast<ULONG>(Length / sizeof(SHORT)));
            } else {
                status = STATUS_DEVICE_NOT_CONNECTED;
            }
            KeReleaseSpinLock(&c->Lock, irql);
        }
    }
    WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? Length : 0);
}

_Use_decl_annotations_
VOID EvtIoDeviceControl(WDFQUEUE Queue, WDFREQUEST Request, size_t OutputBufferLength,
    size_t InputBufferLength, ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    if (IoControlCode != IOCTL_NTA2DPVM_WAIT_CAPTURE_STATE) {
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        return;
    }

    PVOID in;
    PVOID out;
    NTSTATUS status = WdfRequestRetrieveInputBuffer(Request, sizeof(ULONG), &in, nullptr);
    if (NT_SUCCESS(status)) {
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG), &out, nullptr);
    }
    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
        return;
    }

    CONTROL_CONTEXT* c = GetControlContext(WdfIoQueueGetDevice(Queue));
    ULONG lastKnown = *static_cast<PULONG>(in);
    ULONG state = lastKnown;
    KIRQL irql;
    KeAcquireSpinLock(&c->Lock, &irql);
    DEVICE_CONTEXT* d = c->Device;
    if (d == nullptr) {
        status = STATUS_DEVICE_NOT_CONNECTED;
    } else {
        // Checked and queued under the lock that guards state changes, so no change is missed.
        KeAcquireSpinLockAtDpcLevel(&d->Lock);
        state = d->RunningStreams != 0 ? 1 : 0;
        if (state == lastKnown) {
            status = WdfRequestForwardToIoQueue(Request, d->WaitQueue);
        }
        KeReleaseSpinLockFromDpcLevel(&d->Lock);
    }
    KeReleaseSpinLock(&c->Lock, irql);

    if (state != lastKnown) {
        CompleteWait(Request, state);
    } else if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
    }
}

_Use_decl_annotations_
NTSTATUS EvtCircuitCreateStream(WDFDEVICE Device, ACXCIRCUIT Circuit, ACXPIN Pin, PACXSTREAM_INIT StreamInit,
    ACXDATAFORMAT StreamFormat, const GUID* SignalProcessingMode, ACXOBJECTBAG VarArguments)
{
    UNREFERENCED_PARAMETER(Pin);
    UNREFERENCED_PARAMETER(StreamFormat);
    UNREFERENCED_PARAMETER(SignalProcessingMode);
    UNREFERENCED_PARAMETER(VarArguments);

    ACX_STREAM_CALLBACKS streamCallbacks;
    ACX_STREAM_CALLBACKS_INIT(&streamCallbacks);
    streamCallbacks.EvtAcxStreamPrepareHardware = EvtStreamPrepareHardware;
    streamCallbacks.EvtAcxStreamReleaseHardware = EvtStreamReleaseHardware;
    streamCallbacks.EvtAcxStreamRun = EvtStreamRun;
    streamCallbacks.EvtAcxStreamPause = EvtStreamPause;
    NTSTATUS status = AcxStreamInitAssignAcxStreamCallbacks(StreamInit, &streamCallbacks);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    ACX_RT_STREAM_CALLBACKS rtCallbacks;
    ACX_RT_STREAM_CALLBACKS_INIT(&rtCallbacks);
    rtCallbacks.EvtAcxStreamGetHwLatency = EvtStreamGetHwLatency;
    rtCallbacks.EvtAcxStreamAllocateRtPackets = EvtStreamAllocateRtPackets;
    rtCallbacks.EvtAcxStreamFreeRtPackets = EvtStreamFreeRtPackets;
    rtCallbacks.EvtAcxStreamGetCurrentPacket = EvtStreamGetCurrentPacket;
    rtCallbacks.EvtAcxStreamGetCapturePacket = EvtStreamGetCapturePacket;
    rtCallbacks.EvtAcxStreamGetPresentationPosition = EvtStreamGetPresentationPosition;
    status = AcxStreamInitAssignAcxRtStreamCallbacks(StreamInit, &rtCallbacks);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    AcxStreamInitSetAcxRtStreamSupportsNotifications(StreamInit);

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, STREAM_CONTEXT);
    ACXSTREAM stream;
    status = AcxRtStreamCreate(Device, Circuit, &attributes, &StreamInit, &stream);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    GetStreamContext(stream)->Device = GetDeviceContext(Device);
    return STATUS_SUCCESS;
}

// The packet timer is created on the first prepare and lives as long as the stream.
_Use_decl_annotations_
NTSTATUS EvtStreamPrepareHardware(ACXSTREAM Stream)
{
    STREAM_CONTEXT* s = GetStreamContext(Stream);
    if (s->Timer != nullptr) {
        return STATUS_SUCCESS;
    }
    WDF_TIMER_CONFIG timerConfig;
    WDF_TIMER_CONFIG_INIT(&timerConfig, EvtPacketTimer);
    timerConfig.AutomaticSerialization = FALSE;
    timerConfig.UseHighResolutionTimer = WdfTrue;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = Stream;
    attributes.ExecutionLevel = WdfExecutionLevelDispatch; // high-resolution timers fire at DISPATCH_LEVEL
    return WdfTimerCreate(&timerConfig, &attributes, &s->Timer);
}

_Use_decl_annotations_
NTSTATUS EvtStreamRun(ACXSTREAM Stream)
{
    STREAM_CONTEXT* s = GetStreamContext(Stream);
    DEVICE_CONTEXT* d = s->Device;
    LONGLONG now = KeQueryPerformanceCounter(nullptr).QuadPart;
    KIRQL irql;
    KeAcquireSpinLock(&d->Lock, &irql);
    // Audio written while nobody captured is stale.
    d->ReadPos = d->WritePos;
    s->BaseQpc = now;
    s->BasePacket = s->CurrentPacket;
    s->CurrentPacketQpc = now;
    bool changed = SetRunningLocked(s, true);
    ArmTimer(s, PacketEndQpc(s, s->CurrentPacket), now);
    KeReleaseSpinLock(&d->Lock, irql);
    if (changed) {
        CompleteWaiters(d, 1);
    }
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS EvtStreamPause(ACXSTREAM Stream)
{
    StopStream(GetStreamContext(Stream), false);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS EvtStreamReleaseHardware(ACXSTREAM Stream)
{
    StopStream(GetStreamContext(Stream), true);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
VOID EvtPacketTimer(WDFTIMER Timer)
{
    ACXSTREAM stream = static_cast<ACXSTREAM>(WdfTimerGetParentObject(Timer));
    STREAM_CONTEXT* s = GetStreamContext(stream);
    DEVICE_CONTEXT* d = s->Device;
    LONGLONG now = KeQueryPerformanceCounter(nullptr).QuadPart;
    bool completed = false;
    ULONG lastPacket = 0;
    LONGLONG lastEnd = 0;

    // Re-armed under the lock so Pause, which clears Running under it, cannot race the re-arm.
    KeAcquireSpinLockAtDpcLevel(&d->Lock);
    if (s->Running) {
        for (;;) {
            LONGLONG end = PacketEndQpc(s, s->CurrentPacket);
            if (end > now) {
                ArmTimer(s, end, now);
                break;
            }
            ReadRingLocked(d, s->Packet[s->CurrentPacket & (s->PacketCount - 1)], s->PacketSamples);
            completed = true;
            lastPacket = s->CurrentPacket++;
            lastEnd = end;
            s->LastPacketQpc = s->CurrentPacketQpc;
            s->CurrentPacketQpc = end;
        }
    }
    KeReleaseSpinLockFromDpcLevel(&d->Lock);

    if (completed) {
        (VOID)AcxRtStreamNotifyPacketComplete(stream, lastPacket, static_cast<ULONGLONG>(lastEnd));
    }
}

_Use_decl_annotations_
NTSTATUS EvtStreamGetHwLatency(ACXSTREAM Stream, PULONG FifoSize, PULONG Delay)
{
    UNREFERENCED_PARAMETER(Stream);
    *FifoSize = 0;
    *Delay = 0;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS EvtStreamAllocateRtPackets(ACXSTREAM Stream, ULONG PacketCount, ULONG PacketSize, PACX_RTPACKET* Packets)
{
    if (PacketCount == 0 || PacketCount > MaxPackets || PacketSize == 0 || PacketSize > MaxPacketBytes ||
        PacketSize % sizeof(SHORT) != 0) {
        return STATUS_INVALID_PARAMETER;
    }

    auto packets = static_cast<PACX_RTPACKET>(
        ExAllocatePool2(POOL_FLAG_NON_PAGED, PacketCount * sizeof(ACX_RTPACKET), PoolTag));
    if (packets == nullptr) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    STREAM_CONTEXT* s = GetStreamContext(Stream);
    SIZE_T allocationSize = ROUND_TO_PAGES(static_cast<SIZE_T>(PacketSize));
    PHYSICAL_ADDRESS low = {};
    PHYSICAL_ADDRESS high;
    high.QuadPart = MAXLONGLONG;
    PHYSICAL_ADDRESS skip = {};
    for (ULONG i = 0; i < PacketCount; i++) {
        ACX_RTPACKET_INIT(&packets[i]);
        // Whole pages, never shared with pool allocations: ACX maps them into the audio engine.
        PMDL mdl = MmAllocatePagesForMdlEx(low, high, skip, allocationSize, MmCached, MM_ALLOCATE_FULLY_REQUIRED);
        if (mdl == nullptr) {
            FreePackets(packets, i);
            return STATUS_INSUFFICIENT_RESOURCES;
        }
        WDF_MEMORY_DESCRIPTOR_INIT_MDL(&packets[i].RtPacketBuffer, mdl, static_cast<ULONG>(allocationSize));
        auto va = static_cast<PUCHAR>(MmGetSystemAddressForMdlSafe(mdl, NormalPagePriority | MdlMappingNoExecute));
        if (va == nullptr) {
            FreePackets(packets, i + 1);
            return STATUS_INSUFFICIENT_RESOURCES;
        }
        // With two packets, packet 0 ends at its page boundary so both are contiguous in the client mapping.
        packets[i].RtPacketOffset = (PacketCount == 2 && i == 0) ? static_cast<ULONG>(allocationSize) - PacketSize : 0;
        packets[i].RtPacketSize = PacketSize;
        s->Packet[i] = reinterpret_cast<PSHORT>(va + packets[i].RtPacketOffset);
    }

    s->PacketCount = PacketCount;
    s->PacketSamples = PacketSize / sizeof(SHORT);
    *Packets = packets;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
VOID EvtStreamFreeRtPackets(ACXSTREAM Stream, PACX_RTPACKET Packets, ULONG PacketCount)
{
    UNREFERENCED_PARAMETER(Stream);
    FreePackets(Packets, PacketCount);
}

_Use_decl_annotations_
NTSTATUS EvtStreamGetCurrentPacket(ACXSTREAM Stream, PULONG CurrentPacket)
{
    *CurrentPacket = ReadULongNoFence(&GetStreamContext(Stream)->CurrentPacket);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS EvtStreamGetCapturePacket(ACXSTREAM Stream, PULONG LastCapturePacket, PULONGLONG QPCPacketStart,
    PBOOLEAN MoreData)
{
    STREAM_CONTEXT* s = GetStreamContext(Stream);
    KIRQL irql;
    KeAcquireSpinLock(&s->Device->Lock, &irql);
    *LastCapturePacket = s->CurrentPacket - 1;
    *QPCPacketStart = static_cast<ULONGLONG>(s->LastPacketQpc);
    KeReleaseSpinLock(&s->Device->Lock, irql);
    *MoreData = FALSE;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS EvtStreamGetPresentationPosition(ACXSTREAM Stream, PULONGLONG PositionInBlocks, PULONGLONG QPCPosition)
{
    STREAM_CONTEXT* s = GetStreamContext(Stream);
    KIRQL irql;
    KeAcquireSpinLock(&s->Device->Lock, &irql);
    *PositionInBlocks = static_cast<ULONGLONG>(s->CurrentPacket) * s->PacketSamples;
    *QPCPosition = static_cast<ULONGLONG>(s->CurrentPacketQpc);
    KeReleaseSpinLock(&s->Device->Lock, irql);
    return STATUS_SUCCESS;
}
