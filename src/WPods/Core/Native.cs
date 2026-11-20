using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WPods.Core;

internal static unsafe partial class Native
{
    public const int ErrorSuccess = 0;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorMoreData = 234;
    public const int ErrorServiceDoesNotExist = 1060;
    public const int ErrorDeviceNotConnected = 1167;
    public const int ErrorNotFound = 1168;
    public const uint EInvalidArg = 0x80070057;

    private const uint FileReadAttributes = 0x80;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    private const uint CrSuccess = 0;
    private const uint CrBufferSmall = 0x1A;

    public static readonly Guid RadioInterface = new("0850302A-B344-4FDA-9BE9-90576B8D46F0");
    public static readonly Guid HciEvent = new("FC240062-1541-49BE-B463-84C4DCD7BF7F");
    // AAP service devnodes: Windows creates one per paired AirPods and radio, driver or not.
    // The instance id ends in "&" + address + "_C...".
    public const string AapServicePrefix = @"BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}";

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(nint process, int infoClass, ref PowerThrottlingState info, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    /// <summary>False opts the process out of EcoQoS throttling; true lets Windows decide again.</summary>
    public static void SetSpeedThrottling(bool allowed)
    {
        // ProcessPowerThrottling = 4. ControlMask 1 with StateMask 0 disables throttling; ControlMask 0 restores the default.
        var state = new PowerThrottlingState { Version = 1, ControlMask = allowed ? 0u : 1u, StateMask = 0 };
        if (!SetProcessInformation(GetCurrentProcess(), 4, ref state, (uint)sizeof(PowerThrottlingState)))
        {
            Log.Warn("native", $"power throttling {(allowed ? "default" : "off")} not set: Win32 error {Marshal.GetLastPInvokeError()}");
        }
    }

    // CPU set IDs of the highest efficiency class (performance cores); null when every core has the same class.
    private static readonly uint[]? PerformanceCpuSets = ReadPerformanceCpuSets();

    /// <summary>
    /// True restricts every thread of the process without its own CPU sets (thread pool, ONNX Runtime and DeepFilter
    /// workers) to performance cores; false lets Windows schedule them anywhere again. Nothing on CPUs without E-cores.
    /// </summary>
    public static void SetPerformanceCoresOnly(bool on)
    {
        if (PerformanceCpuSets is not { } ids)
        {
            return;
        }
        bool ok;
        fixed (uint* list = ids)
        {
            ok = on ? SetProcessDefaultCpuSets(GetCurrentProcess(), list, (uint)ids.Length) : SetProcessDefaultCpuSets(GetCurrentProcess(), null, 0);
        }
        if (!ok)
        {
            Log.Warn("native", $"performance cores {(on ? "only" : "default")} not set: Win32 error {Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>
    /// Makes the calling thread a real-time audio thread for its lifetime: MMCSS "Pro Audio" priority, no power
    /// throttling, and performance cores only.
    /// </summary>
    public static void MakeAudioThread()
    {
        uint taskIndex = 0;
        if (AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex) == 0)
        {
            Log.Warn("native", $"MMCSS Pro Audio not set: Win32 error {Marshal.GetLastPInvokeError()}");
        }
        // ThreadPowerThrottling = 3; ControlMask EXECUTION_SPEED with StateMask 0 = never throttled (HighQoS).
        var state = new PowerThrottlingState { Version = 1, ControlMask = 1, StateMask = 0 };
        if (!SetThreadInformation(GetCurrentThread(), 3, ref state, (uint)sizeof(PowerThrottlingState)))
        {
            Log.Warn("native", $"thread power throttling not disabled: Win32 error {Marshal.GetLastPInvokeError()}");
        }
        if (PerformanceCpuSets is not { } ids)
        {
            return;
        }
        fixed (uint* list = ids)
        {
            if (!SetThreadSelectedCpuSets(GetCurrentThread(), list, (uint)ids.Length))
            {
                Log.Warn("native", $"thread CPU sets not set: Win32 error {Marshal.GetLastPInvokeError()}");
            }
        }
    }

    // SYSTEM_CPU_SET_INFORMATION entries: Size u32 @0, Type u32 @4 (0 = CpuSetInformation), Id u32 @8,
    // EfficiencyClass u8 @18 (higher = faster).
    private static uint[]? ReadPerformanceCpuSets()
    {
        GetSystemCpuSetInformation(null, 0, out uint length, 0, 0);
        byte[] buffer = new byte[length];
        fixed (byte* data = buffer)
        {
            if (!GetSystemCpuSetInformation(data, length, out length, 0, 0))
            {
                Log.Warn("native", $"CPU sets not read: Win32 error {Marshal.GetLastPInvokeError()}");
                return null;
            }
        }
        var ids = new List<uint>();
        byte best = 0;
        bool mixed = false;
        for (int offset = 0; offset + 19 <= (int)length;)
        {
            int size = BitConverter.ToInt32(buffer, offset);
            if (size <= 0)
            {
                break;
            }
            if (BitConverter.ToInt32(buffer, offset + 4) == 0)
            {
                byte efficiency = buffer[offset + 18];
                if (ids.Count > 0 && efficiency != best)
                {
                    mixed = true;
                }
                if (ids.Count == 0 || efficiency > best)
                {
                    best = efficiency;
                    ids.Clear();
                }
                if (efficiency == best)
                {
                    ids.Add(BitConverter.ToUInt32(buffer, offset + 8));
                }
            }
            offset += size;
        }
        Log.Info("native", mixed ? $"performance cores: {ids.Count} CPU sets of efficiency class {best}" : "no efficiency cores");
        return mixed ? [.. ids] : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemCpuSetInformation(byte* information, uint bufferLength, out uint returnedLength, nint process, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDefaultCpuSets(nint process, uint* cpuSetIds, uint cpuSetIdCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadSelectedCpuSets(nint thread, uint* cpuSetIds, uint cpuSetIdCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadInformation(nint thread, int infoClass, ref PowerThrottlingState info, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    // ---- Configuration manager ----

    public const int CmNotifyFilterTypeDeviceInterface = 0;
    public const int CmNotifyFilterTypeDeviceHandle = 1;
    public const int CmNotifyFilterTypeDeviceInstance = 2;
    public const uint CmNotifyFilterFlagAllDeviceInstances = 0x2;
    public const int CmNotifyActionInterfaceArrival = 0;
    public const int CmNotifyActionInterfaceRemoval = 1;
    public const int CmNotifyActionDeviceRemoveComplete = 5;
    public const int CmNotifyActionDeviceCustomEvent = 6;
    public const int CmNotifyActionDeviceInstanceEnumerated = 7;
    public const int CmNotifyActionDeviceInstanceStarted = 8;
    public const int CmNotifyActionDeviceInstanceRemoved = 9;

    // CM_NOTIFY_FILTER (x64): cbSize @0, Flags @4, FilterType @8, Reserved @12, union @16 (ClassGuid, hTarget or
    // InstanceId[MAX_DEVICE_ID_LEN = 200 WCHAR]), 416 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    public struct CmNotifyFilter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public int FilterType;
        [FieldOffset(16)] public Guid ClassGuid;
        [FieldOffset(16)] public nint Handle;
    }

    // CM_NOTIFY_EVENT_DATA (x64): FilterType @0, ClassGuid/EventGuid @8, SymbolicLink @24 (interface),
    // NameOffset @24, DataSize @28, Data @32 (handle), InstanceId @8 (instance).
    public const int EventGuidOffset = 8;
    public const int SymbolicLinkOffset = 24;
    public const int EventDataSizeOffset = 28;
    public const int EventDataOffset = 32;
    public const int InstanceIdOffset = 8;

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

    /// <summary>Registers for custom events (HCI link changes) of the device at <paramref name="path"/>.</summary>
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
        // The handle may be closed once registration returns.
        return CM_Register_Notification(&filter, context, callback, out nint notify) == CrSuccess ? notify : 0;
    }

    private const uint CmGetIdListFilterEnumerator = 0x1;
    private const uint CmGetIdListFilterPresent = 0x100;
    private const uint CmLocateDevNodeNormal = 0;
    private const uint CmLocateDevNodePhantom = 1;
    private const uint DevPropTypeFileTime = 0x10;
    private const uint DevPropTypeString = 0x12;
    private const int MaxDeviceIdLength = 200;

    private struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    private static readonly DevPropKey FirstInstallDate = new() { FmtId = new("83da6326-97a6-4088-9453-a1923f573b29"), Pid = 101 };
    private static readonly DevPropKey InstallDate = new() { FmtId = new("83da6326-97a6-4088-9453-a1923f573b29"), Pid = 100 };
    private static readonly DevPropKey DeviceInstanceId = new() { FmtId = new("78c34fc8-104a-4aca-9ea4-524d52996e57"), Pid = 256 };

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_Size(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List(string filter, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_Property(string path, in DevPropKey key, out uint type, byte* buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint CM_Get_DevNode_Property(uint devInst, in DevPropKey key, out uint type, byte* buffer, ref uint size, uint flags);

    /// <summary>
    /// Install date of the device's present BTHENUM\DEV_&lt;MAC&gt; entry; re-pairing creates a new entry and date.
    /// With entries on several radios, uses the one of the connected radio; null when none applies or it has no date.
    /// </summary>
    public static DateTime? GetDeviceInstallDate(ulong address)
    {
        List<uint> entries = GetPresentDevNodes($"BTHENUM\\DEV_{address:X12}\\");
        if (entries.Count == 0)
        {
            return null;
        }
        uint entry = entries[0];
        if (entries.Count > 1)
        {
            if (GetConnectedEntry(address, entries) is not { } connected)
            {
                return null;
            }
            entry = connected;
        }
        return ReadFileTime(entry, FirstInstallDate) ?? ReadFileTime(entry, InstallDate);
    }

    /// <summary>Whether any of the device's entries, present or not (one per radio used), has install date <paramref name="date"/>.</summary>
    public static bool HasDeviceEntryDated(ulong address, DateTime date)
    {
        foreach (string id in GetDeviceIds($"BTHENUM\\DEV_{address:X12}\\", CmGetIdListFilterEnumerator))
        {
            if (CM_Locate_DevNode(out uint devInst, id, CmLocateDevNodePhantom) == CrSuccess
                && (ReadFileTime(devInst, FirstInstallDate) ?? ReadFileTime(devInst, InstallDate)) == date)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Present BTHENUM instance ids starting with <paramref name="prefix"/>, ignoring case.</summary>
    public static List<string> GetPresentDeviceIds(string prefix) => GetDeviceIds(prefix, CmGetIdListFilterEnumerator | CmGetIdListFilterPresent);

    private static List<string> GetDeviceIds(string prefix, uint flags)
    {
        var ids = new List<string>();
        while (true)
        {
            if (CM_Get_Device_ID_List_Size(out uint length, "BTHENUM", flags) != CrSuccess || length <= 1)
            {
                return ids;
            }
            char[] buffer = new char[length];
            uint result;
            fixed (char* p = buffer)
            {
                result = CM_Get_Device_ID_List("BTHENUM", p, length, flags);
            }
            if (result == CrBufferSmall)
            {
                continue;
            }
            if (result != CrSuccess)
            {
                return ids;
            }
            ReadOnlySpan<char> rest = buffer;
            int end;
            while ((end = rest.IndexOf('\0')) > 0)
            {
                if (rest[..end].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(new string(rest[..end]));
                }
                rest = rest[(end + 1)..];
            }
            return ids;
        }
    }

    private static List<uint> GetPresentDevNodes(string prefix)
    {
        var devNodes = new List<uint>();
        foreach (string id in GetPresentDeviceIds(prefix))
        {
            if (CM_Locate_DevNode(out uint devInst, id, CmLocateDevNodeNormal) == CrSuccess)
            {
                devNodes.Add(devInst);
            }
        }
        return devNodes;
    }

    // The entry whose grandparent (entry, BTHBRB bus, radio) is the radio the device is connected through.
    private static uint? GetConnectedEntry(ulong address, List<uint> entries)
    {
        foreach (string path in GetInterfaces(RadioInterface))
        {
            if (!IsConnectedThrough(path, address) || GetInterfaceDevNode(path) is not { } radio)
            {
                continue;
            }
            foreach (uint entry in entries)
            {
                if (CM_Get_Parent(out uint bus, entry, 0) == CrSuccess && CM_Get_Parent(out uint parent, bus, 0) == CrSuccess && parent == radio)
                {
                    return entry;
                }
            }
        }
        return null;
    }

    private static bool IsConnectedThrough(string radioPath, ulong address)
    {
        using SafeFileHandle radio = CreateFile(radioPath, FileReadAttributes, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (radio.IsInvalid)
        {
            return false;
        }
        var info = new BluetoothDeviceInfo { Size = (uint)sizeof(BluetoothDeviceInfo), Address = address };
        return BluetoothGetDeviceInfo(radio.DangerousGetHandle(), ref info) == ErrorSuccess && info.Connected != 0;
    }

    private static uint? GetInterfaceDevNode(string path)
    {
        char* id = stackalloc char[MaxDeviceIdLength + 1];
        uint size = (MaxDeviceIdLength + 1) * sizeof(char);
        if (CM_Get_Device_Interface_Property(path, DeviceInstanceId, out uint type, (byte*)id, ref size, 0) != CrSuccess
            || type != DevPropTypeString)
        {
            return null;
        }
        return CM_Locate_DevNode(out uint devInst, new string(id), CmLocateDevNodeNormal) == CrSuccess ? devInst : null;
    }

    private static DateTime? ReadFileTime(uint devInst, in DevPropKey key)
    {
        long value;
        uint size = sizeof(long);
        return CM_Get_DevNode_Property(devInst, key, out uint type, (byte*)&value, ref size, 0) == CrSuccess
            && type == DevPropTypeFileTime && size == sizeof(long)
            ? DateTime.FromFileTimeUtc(value)
            : null;
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

    /// <summary>
    /// Registers for events of every device instance (ALL_DEVICE_INSTANCES requires an empty instance id).
    /// Callers filter by the id at <see cref="InstanceIdOffset"/>.
    /// </summary>
    public static nint RegisterDeviceInstanceNotification(nint context, delegate* unmanaged[Stdcall]<nint, nint, int, byte*, uint, uint> callback)
    {
        var filter = new CmNotifyFilter
        {
            Size = (uint)sizeof(CmNotifyFilter),
            Flags = CmNotifyFilterFlagAllDeviceInstances,
            FilterType = CmNotifyFilterTypeDeviceInstance,
        };
        return CM_Register_Notification(&filter, context, callback, out nint notify) == CrSuccess ? notify : 0;
    }

    // ---- Bluetooth (BluetoothApis.dll) ----

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

    /// <summary>Finds a paired device on any radio without paging; on success the caller must close <paramref name="radio"/>.</summary>
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

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int wpn_bt_disconnect_handsfree(ulong btAddress);

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int wpn_bt_handsfree_mic_in_use(ulong btAddress);

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial void wpn_fp_set_ftz_daz();

    // LC3 (HD voice): mono float PCM in [-1, 1] at pcmRate; a null frame conceals a lost one.

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint wpn_lc3_decoder_create(int frameUs, int sampleRate, int pcmRate);

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int wpn_lc3_decode(nint decoder, byte* frame, int nbytes, float* pcm);

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial void wpn_lc3_decoder_free(nint decoder);

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial nint wpn_lc3_encoder_create(int frameUs, int sampleRate, int pcmRate);

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int wpn_lc3_encode(nint encoder, float* pcm, int nbytes, byte* frame);

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial void wpn_lc3_encoder_free(nint encoder);

    // Loopback capture (HD voice downlink): what Windows plays to a pair, mono float 48 kHz.
    // Read returns samples, 0 after timeoutMs without audio, or a negative HRESULT (reopen).

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int wpn_loopback_open(ulong btAddress, out nint capture, out uint period);

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int wpn_loopback_read(nint capture, float* output, uint capacity, uint timeoutMs);

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial void wpn_loopback_close(nint capture);
}
