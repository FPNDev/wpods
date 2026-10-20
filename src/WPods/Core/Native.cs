using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WPods.Core;

internal static unsafe partial class Native
{
    public const int ErrorSuccess = 0;
    public const int ErrorSharingViolation = 32;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorMoreData = 234;
    public const int ErrorNoMoreItems = 259;
    public const int ErrorServiceDoesNotExist = 1060;
    public const int ErrorDeviceNotConnected = 1167;
    public const int ErrorNotFound = 1168;
    public const uint EInvalidArg = 0x80070057;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;

    private const uint CrSuccess = 0;
    private const uint CrBufferSmall = 0x1A;

    public static readonly Guid NtapdInterface = new("904e6502-b92f-464d-8817-108623e33703");
    public static readonly Guid RadioInterface = new("0850302A-B344-4FDA-9BE9-90576B8D46F0");
    public static readonly Guid HciEvent = new("FC240062-1541-49BE-B463-84C4DCD7BF7F");

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    /// <summary>Opens an NTAPD interface for overlapped I/O (exclusive; never pages per the driver contract).</summary>
    public static SafeFileHandle OpenNtapd(string path, out int error)
    {
        SafeFileHandle handle = CreateFile(path, GenericRead | GenericWrite, 0, 0, OpenExisting, FileFlagOverlapped, 0);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : ErrorSuccess;
        return handle;
    }

    // ---- Configuration manager ----

    public const int CmNotifyFilterTypeDeviceInterface = 0;
    public const int CmNotifyFilterTypeDeviceHandle = 1;
    public const int CmNotifyActionInterfaceArrival = 0;
    public const int CmNotifyActionInterfaceRemoval = 1;
    public const int CmNotifyActionDeviceRemoveComplete = 5;
    public const int CmNotifyActionDeviceCustomEvent = 6;

    [StructLayout(LayoutKind.Explicit, Size = 416)]
    public struct CmNotifyFilter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(8)] public int FilterType;
        [FieldOffset(16)] public Guid ClassGuid;
        [FieldOffset(16)] public nint Handle;
    }

    // CM_NOTIFY_EVENT_DATA (x64): FilterType @0, ClassGuid/EventGuid @8, SymbolicLink @24 (interface),
    // NameOffset @24, DataSize @28, Data @32 (handle).
    public const int EventGuidOffset = 8;
    public const int SymbolicLinkOffset = 24;
    public const int EventDataSizeOffset = 28;
    public const int EventDataOffset = 32;

    [LibraryImport("cfgmgr32.dll")]
    public static partial uint CM_Register_Notification(
        CmNotifyFilter* filter,
        nint context,
        delegate* unmanaged[Stdcall]<nint, nint, int, byte*, uint, uint> callback,
        out nint notify);

    [LibraryImport("cfgmgr32.dll")]
    public static partial uint CM_Unregister_Notification(nint notify);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static partial uint CM_Get_Device_Interface_List_Size(out uint length, in Guid interfaceClass, char* deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static partial uint CM_Get_Device_Interface_List(in Guid interfaceClass, char* deviceId, char* buffer, uint length, uint flags);

    /// <summary>Symbolic links of the present interfaces of a class.</summary>
    public static List<string> GetInterfaces(in Guid interfaceClass)
    {
        var paths = new List<string>();
        while (true)
        {
            if (CM_Get_Device_Interface_List_Size(out uint length, interfaceClass, null, 0) != CrSuccess || length <= 1)
            {
                return paths;
            }
            char[] buffer = new char[length];
            uint result;
            fixed (char* p = buffer)
            {
                result = CM_Get_Device_Interface_List(interfaceClass, null, p, length, 0);
            }
            if (result == CrBufferSmall)
            {
                continue;
            }
            if (result != CrSuccess)
            {
                return paths;
            }
            ReadOnlySpan<char> rest = buffer;
            while (true)
            {
                int end = rest.IndexOf('\0');
                if (end <= 0)
                {
                    return paths;
                }
                paths.Add(new string(rest[..end]));
                rest = rest[(end + 1)..];
            }
        }
    }

    /// <summary>Registers for custom events (HCI link changes) of the device behind <paramref name="path"/>.</summary>
    public static nint RegisterHandleNotification(string path, nint context, delegate* unmanaged[Stdcall]<nint, nint, int, byte*, uint, uint> callback)
    {
        using SafeFileHandle handle = CreateFile(path, FileReadAttributes, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            return 0;
        }
        var filter = new CmNotifyFilter
        {
            Size = (uint)sizeof(CmNotifyFilter),
            FilterType = CmNotifyFilterTypeDeviceHandle,
            Handle = handle.DangerousGetHandle(),
        };
        // The handle may be closed once registration returns (CM_NOTIFY_FILTER docs).
        return CM_Register_Notification(&filter, context, callback, out nint notify) == CrSuccess ? notify : 0;
    }

    public static nint RegisterInterfaceNotification(in Guid interfaceClass, nint context, delegate* unmanaged[Stdcall]<nint, nint, int, byte*, uint, uint> callback)
    {
        var filter = new CmNotifyFilter
        {
            Size = (uint)sizeof(CmNotifyFilter),
            FilterType = CmNotifyFilterTypeDeviceInterface,
            ClassGuid = interfaceClass,
        };
        return CM_Register_Notification(&filter, context, callback, out nint notify) == CrSuccess ? notify : 0;
    }

    // ---- Bluetooth (BluetoothApis.dll, the implementation behind bthprops.cpl) ----

    [StructLayout(LayoutKind.Sequential)]
    public struct BluetoothDeviceInfo
    {
        public uint Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected;
        public int Remembered;
        public int Authenticated;
        private fixed ushort _lastSeenAndLastUsed[16];
        public fixed char Name[248];
    }

    [LibraryImport("BluetoothApis.dll", SetLastError = true)]
    private static partial nint BluetoothFindFirstRadio(in uint parameters, out nint radio);

    [LibraryImport("BluetoothApis.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BluetoothFindNextRadio(nint find, out nint radio);

    [LibraryImport("BluetoothApis.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BluetoothFindRadioClose(nint find);

    [LibraryImport("BluetoothApis.dll")]
    private static partial uint BluetoothGetDeviceInfo(nint radio, ref BluetoothDeviceInfo info);

    [LibraryImport("BluetoothApis.dll")]
    public static partial uint BluetoothEnumerateInstalledServices(nint radio, in BluetoothDeviceInfo info, ref uint count, Guid* services);

    [LibraryImport("BluetoothApis.dll")]
    public static partial uint BluetoothSetServiceState(nint radio, in BluetoothDeviceInfo info, in Guid service, uint flags);

    /// <summary>
    /// Finds a paired device on any local radio (no inquiry, no paging). On success the caller owns
    /// <paramref name="radio"/> and must close it.
    /// </summary>
    public static bool TryFindPairedDevice(ulong address, out nint radio, out BluetoothDeviceInfo info)
    {
        info = default;
        uint parameters = sizeof(uint);
        nint find = BluetoothFindFirstRadio(parameters, out radio);
        if (find == 0)
        {
            radio = 0;
            return false;
        }
        try
        {
            while (true)
            {
                info = new BluetoothDeviceInfo { Size = (uint)sizeof(BluetoothDeviceInfo), Address = address };
                if (BluetoothGetDeviceInfo(radio, ref info) == ErrorSuccess)
                {
                    return true;
                }
                CloseHandle(radio);
                if (!BluetoothFindNextRadio(find, out radio))
                {
                    radio = 0;
                    return false;
                }
            }
        }
        finally
        {
            BluetoothFindRadioClose(find);
        }
    }

    // ---- DPAPI ----

    private struct DataBlob
    {
        public uint Size;
        public byte* Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(DataBlob* input, char* description, DataBlob* entropy, nint reserved, nint prompt, uint flags, DataBlob* output);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(DataBlob* input, char** description, DataBlob* entropy, nint reserved, nint prompt, uint flags, DataBlob* output);

    private const uint CryptProtectUiForbidden = 0x1;

    /// <summary>DPAPI (CurrentUser) protect or unprotect; null on failure.</summary>
    public static byte[]? Dpapi(ReadOnlySpan<byte> data, bool protect)
    {
        fixed (byte* p = data)
        {
            var input = new DataBlob { Size = (uint)data.Length, Data = p };
            DataBlob output = default;
            bool ok = protect
                ? CryptProtectData(&input, null, null, 0, 0, CryptProtectUiForbidden, &output)
                : CryptUnprotectData(&input, null, null, 0, 0, CryptProtectUiForbidden, &output);
            if (!ok)
            {
                return null;
            }
            byte[] result = new ReadOnlySpan<byte>(output.Data, (int)output.Size).ToArray();
            new Span<byte>(output.Data, (int)output.Size).Clear();
            LocalFree((nint)output.Data);
            return result;
        }
    }

    // ---- wpods_native.dll ----

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int wpn_bt_disconnect_audio(ulong btAddress);
}
