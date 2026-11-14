using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WPods.Service;

/// <summary>
/// Finds the Bluetooth adapter and switches it between the in-box WinUSB driver and its native driver. The adapter
/// taken over is stored in the registry, as Windows no longer lists it as a radio once it runs on WinUSB.
/// </summary>
internal static unsafe partial class RadioDriver
{
    public const string StateKey = @"SOFTWARE\WPods\Service";
    private const string EnumKey = @"SYSTEM\CurrentControlSet\Enum";
    private static readonly Guid RadioInterface = new("0850302A-B344-4FDA-9BE9-90576B8D46F0");
    private static readonly Guid UsbDeviceClass = new("88bae032-5a81-49f0-bc3d-a4ff138216d6");
    private static readonly Guid HubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
    private const string UsbDeviceInterface = "{a5dcbf10-6530-11d2-901f-00c04fb951ed}";
    private const string AapPrefix = @"BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}";

    public static string? StoredInstance
    {
        get => Registry.LocalMachine.OpenSubKey(StateKey)?.GetValue("RadioInstanceId") as string;
        set
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(StateKey);
            if (value is null)
            {
                key.DeleteValue("RadioInstanceId", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("RadioInstanceId", value);
            }
        }
    }

    /// <summary>Every adapter instance WPods bound to WinUSB, so each one gets its native driver back.</summary>
    public static string[] BoundInstances
    {
        get
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(StateKey);
            return key?.GetValue("BoundInstances") as string[] ?? [];
        }
    }

    public static void RememberBound(string instanceId)
    {
        string[] bound = BoundInstances;
        if (Array.Exists(bound, b => b.Equals(instanceId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(StateKey);
        key.SetValue("BoundInstances", (string[])[.. bound, instanceId], RegistryValueKind.MultiString);
    }

    /// <summary>The instance has WinUSB as its driver, also while it is unplugged (read from its registry entry).</summary>
    public static bool BoundToWinUsb(string instanceId)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\{instanceId}");
        return string.Equals(key?.GetValue("Service") as string, "WinUSB", StringComparison.OrdinalIgnoreCase);
    }

    // WinUSB interface for a combined device's function, as the hub publishes UsbDeviceInterface only for whole devices.
    private const string FunctionInterface = "{7b3c9a52-6e1d-4f0b-9c2a-5d8e1f4a6b37}";

    /// <summary>WinUSB device path of a USB device instance (or of a combined device's function).</summary>
    public static string DevicePath(string instanceId) =>
        @"\\?\" + instanceId.Replace('\\', '#') + "#" + (IsFunction(instanceId) ? FunctionInterface : UsbDeviceInterface);

    public static bool IsPresent(string instanceId) => CM_Locate_DevNode(out _, instanceId, 0) == 0;

    /// <summary>Turned off in Device Manager (CM_PROB_DISABLED).</summary>
    public static bool IsDisabled(string instanceId) =>
        CM_Locate_DevNode(out uint dev, instanceId, 0) == 0
        && CM_Get_DevNode_Status(out _, out uint problem, dev, 0) == 0
        && problem == 22;

    public static bool IsOnWinUsb(string instanceId) =>
        string.Equals(GetService(instanceId), "WinUSB", StringComparison.OrdinalIgnoreCase);

    /// <summary>New driver installed but not running (DN_NEED_RESTART), as something still has the adapter open.</summary>
    public static bool NeedsRestart(string instanceId) =>
        CM_Locate_DevNode(out uint dev, instanceId, 0) == 0
        && CM_Get_DevNode_Status(out uint status, out _, dev, 0) == 0
        && (status & 0x00000100 /* DN_NEED_RESTART */) != 0;

    /// <summary>Find's reason when no adapter can be taken over: only adapters not connected over USB (UART, SDIO).</summary>
    public const string NotUsb = "not-usb";

    /// <summary>Find's reason when there is no Bluetooth adapter at all.</summary>
    public const string NoAdapter = "none";

    /// <summary>
    /// The stored adapter if present, else a USB Bluetooth radio that is not a usbip-win2 device, preferring the one the
    /// AirPods are paired on. Null when there is none, with <see cref="NotUsb"/> or <see cref="NoAdapter"/> as the reason.
    /// </summary>
    public static string? Find(out string reason)
    {
        reason = "";
        if (StoredInstance is { } stored && IsPresent(stored))
        {
            return stored;
        }
        // A previously bound adapter that is back runs on WinUSB, so Windows does not list it as a radio.
        foreach (string bound in BoundInstances)
        {
            if (IsPresent(bound) && IsOnWinUsb(bound))
            {
                return bound;
            }
        }
        List<string> candidates = [];
        reason = NoAdapter;
        foreach (string path in GetInterfaces(RadioInterface))
        {
            if (GetInterfaceInstance(path) is not { } id)
            {
                continue;
            }
            if (!id.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("radio", $"adapter {id} is not connected over USB");
                reason = NotUsb;
                continue;
            }
            if (IsVirtual(id))
            {
                continue;
            }
            candidates.Add(id);
        }
        if (candidates.Count == 0)
        {
            return null;
        }
        // Whole USB devices first; a combined device's Bluetooth function only when there is no other.
        candidates.Sort((a, b) => IsFunction(a).CompareTo(IsFunction(b)));
        foreach (string id in candidates)
        {
            if (HasDescendant(id, AapPrefix, depth: 3))
            {
                return id;
            }
        }
        return candidates[0];
    }

    /// <summary>The adapter is one function of a combined (composite) USB device: USB\VID_x&amp;PID_y&amp;MI_nn\...</summary>
    public static bool IsFunction(string instanceId) => instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Re-plugs the adapter by cycling its hub port, so it starts on its installed driver even while something has it
    /// open. False when the hub refused.
    /// </summary>
    public static bool CyclePort(string instanceId)
    {
        const uint IoctlUsbHubCyclePort = 0x00220444;
        if (CM_Locate_DevNode(out uint dev, instanceId, 0) != 0)
        {
            return false;
        }
        // A function sits below its combined device (usbccgp), which owns the hub port: the whole device is re-plugged.
        if (IsFunction(instanceId) && CM_Get_Parent(out uint composite, dev, 0) == 0)
        {
            dev = composite;
        }
        if (CM_Get_Parent(out uint hub, dev, 0) != 0 || GetInstanceId(hub) is not { } hubId)
        {
            return false;
        }
        uint port = 0;
        uint size = sizeof(uint);
        if (CM_Get_DevNode_Registry_Property(dev, 0x1D /* CM_DRP_ADDRESS: the hub port */, out _, &port, ref size, 0) != 0 || port == 0)
        {
            return false;
        }
        foreach (string path in GetInterfaces(HubInterface, hubId))
        {
            using SafeFileHandle handle = CreateFile(path, 0x40000000 /* GENERIC_WRITE */, 3, 0, 3, 0, 0);
            if (handle.IsInvalid)
            {
                continue;
            }
            // USB_CYCLE_PORT_PARAMS: ConnectionIndex, StatusReturned.
            uint* p = stackalloc uint[2];
            p[0] = port;
            p[1] = 0;
            bool ok = DeviceIoControl(handle, IoctlUsbHubCyclePort, p, 8, p, 8, out _, 0);
            Log.Info("radio", $"re-plugged {instanceId} (hub {hubId} port {port}): {(ok ? "ok" : $"Win32 error {Marshal.GetLastPInvokeError()}")}");
            return ok;
        }
        return false;
    }

    /// <summary>
    /// Forgets non-present nodes below <paramref name="instanceId"/>, as Windows numbers a new device ("2- AirPods Pro")
    /// while a node of the same name exists. <paramref name="audioOnly"/> keeps device, HID and LE nodes, whose removal
    /// can unpair them. Present nodes are never touched; returns how many were removed.
    /// </summary>
    public static int ForgetHiddenSubtree(string instanceId, bool includeRoot, bool audioOnly)
    {
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(EnumKey);
        if (root is null)
        {
            return 0;
        }
        // Every device instance id, once: children are found through their parent's ParentIdPrefix.
        var all = new List<string>();
        foreach (string enumerator in root.GetSubKeyNames())
        {
            using RegistryKey? e = root.OpenSubKey(enumerator);
            foreach (string device in e?.GetSubKeyNames() ?? [])
            {
                using RegistryKey? d = e!.OpenSubKey(device);
                foreach (string instance in d?.GetSubKeyNames() ?? [])
                {
                    all.Add($@"{enumerator}\{device}\{instance}");
                }
            }
        }
        var found = new List<string>();
        Collect(instanceId, !audioOnly);
        if (includeRoot)
        {
            found.Add(instanceId);
        }
        int removed = 0;
        foreach (string id in found)
        {
            // Hidden only: a normal locate fails, a phantom locate succeeds.
            if (CM_Locate_DevNode(out _, id, 0) != 0 && CM_Locate_DevNode(out uint dev, id, 1 /* CM_LOCATE_DEVNODE_PHANTOM */) == 0
                && CM_Uninstall_DevNode(dev, 0) == 0)
            {
                removed++;
            }
        }
        return removed;

        // Children first, so a parent is removed after its subtree; `take` marks a subtree to forget.
        void Collect(string id, bool take)
        {
            using RegistryKey? key = root.OpenSubKey(id);
            if (key?.GetValue("ParentIdPrefix") is not string prefix)
            {
                return;
            }
            foreach (string child in all)
            {
                if (child.AsSpan(child.LastIndexOf('\\') + 1).StartsWith(prefix + "&", StringComparison.OrdinalIgnoreCase))
                {
                    bool takeChild = take || IsAudioProfile(child);
                    Collect(child, takeChild);
                    if (takeChild)
                    {
                        found.Add(child);
                    }
                }
            }
        }
    }

    private const string PairedDevicesKey = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";

    /// <summary>
    /// Copies the Device ID of LE devices paired on the virtual adapter from their node names into the pairing, where
    /// Windows stores it only for the native adapter; without it their HID part fails to start. Returns how many.
    /// </summary>
    public static int CarryLeDeviceIds()
    {
        using RegistryKey? services = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\BTHLEDevice");
        using RegistryKey? paired = Registry.LocalMachine.OpenSubKey(PairedDevicesKey, writable: true);
        if (services is null || paired is null)
        {
            return 0;
        }
        int carried = 0;
        foreach (string name in services.GetSubKeyNames())
        {
            Match m = LeDeviceId().Match(name);
            if (!m.Success)
            {
                continue;
            }
            using RegistryKey? device = paired.OpenSubKey(m.Groups["address"].Value, writable: true);
            if (device is null || device.GetValue("VID") is not null)
            {
                continue;
            }
            device.SetValue("VIDType", Hex(m, "source"), RegistryValueKind.DWord);
            device.SetValue("VID", Hex(m, "vid"), RegistryValueKind.DWord);
            device.SetValue("PID", Hex(m, "pid"), RegistryValueKind.DWord);
            device.SetValue("Version", Hex(m, "rev"), RegistryValueKind.DWord);
            carried++;
        }
        return carried;

        static int Hex(Match m, string group) => int.Parse(m.Groups[group].ValueSpan, System.Globalization.NumberStyles.HexNumber);
    }

    [GeneratedRegex(@"_Dev_VID&(?<source>[0-9a-f]{2})(?<vid>[0-9a-f]{4})_PID&(?<pid>[0-9a-f]{4})_REV&(?<rev>[0-9a-f]{4})_(?<address>[0-9a-f]{12})$", RegexOptions.IgnoreCase)]
    private static partial Regex LeDeviceId();

    /// <summary>
    /// Forgets the hidden twins on the other adapter of a device removed in Settings, which would keep its pairing alive.
    /// Ignores devices that are only not present; returns how many nodes were forgotten.
    /// </summary>
    public static int ForgetTwinsOfRemoved(string removedId)
    {
        int slash = removedId.LastIndexOf('\\');
        string device = removedId[..slash];
        if (!device.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase)
            && !device.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        using RegistryKey? instances = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\{device}");
        if (instances is null)
        {
            return 0;
        }
        using (RegistryKey? still = instances.OpenSubKey(removedId[(slash + 1)..]))
        {
            if (still is not null)
            {
                return 0;
            }
        }
        int forgotten = 0;
        foreach (string instance in instances.GetSubKeyNames())
        {
            string twin = $@"{device}\{instance}";
            if (!IsPresent(twin))
            {
                forgotten += ForgetHiddenSubtree(twin, includeRoot: true, audioOnly: false);
            }
        }
        return forgotten;
    }

    /// <summary>
    /// Calls <paramref name="onRemoved"/> on a thread-pool thread for every Bluetooth device node Windows removes.
    /// </summary>
    public static IDisposable WatchRemovedBluetoothDevices(Action<string> onRemoved)
    {
        var watch = new InstanceWatch(onRemoved);
        watch.Self = GCHandle.Alloc(watch);
        // CM_NOTIFY_FILTER: cbSize, Flags (CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES), FilterType (2 = DEVICEINSTANCE).
        const int size = 416;
        byte* filter = stackalloc byte[size];
        new Span<byte>(filter, size).Clear();
        *(uint*)filter = size;
        *(uint*)(filter + 4) = 0x2;
        *(uint*)(filter + 8) = 2;
        uint cr = CM_Register_Notification(filter, GCHandle.ToIntPtr(watch.Self), &OnInstanceNotify, out watch.Notify);
        if (cr != 0)
        {
            Log.Warn("radio", $"removed Bluetooth devices not watched: CONFIGRET {cr}");
            watch.Dispose();
        }
        return watch;
    }

    [UnmanagedCallersOnly]
    private static uint OnInstanceNotify(nint notify, nint context, int action, nint data, uint size)
    {
        // CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED; CM_NOTIFY_EVENT_DATA has the instance id at offset 8.
        if (action == 9)
        {
            string id = new((char*)(data + 8));
            if (id.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase))
            {
                var watch = (InstanceWatch)GCHandle.FromIntPtr(context).Target!;
                ThreadPool.QueueUserWorkItem(_ => watch.Fire(id));
            }
        }
        return 0;
    }

    private sealed class InstanceWatch(Action<string> onRemoved) : IDisposable
    {
        public GCHandle Self;
        public nint Notify;

        public void Fire(string id)
        {
            try
            {
                onRemoved(id);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Warn("radio", $"removed device {id}: {e.Message}");
            }
        }

        public void Dispose()
        {
            if (Notify != 0)
            {
                CM_Unregister_Notification(Notify);
                Notify = 0;
            }
            if (Self.IsAllocated)
            {
                Self.Free();
            }
        }
    }

    // Classic Bluetooth audio profiles: Windows creates one node per profile of a paired device,
    // BTHENUM\{<16-bit UUID>-0000-1000-8000-00805f9b34fb}_..., and the audio endpoints below them.
    private static readonly string[] AudioProfiles =
    [
        "0000110a", "0000110b", // A2DP source, sink
        "0000110c", "0000110e", "0000110f", // AVRCP target, remote control, controller
        "0000111e", "0000111f", // Hands-Free, Hands-Free gateway
        "00001108", "00001112", "00001131", // Headset, headset gateway, headset HS
    ];

    private static bool IsAudioProfile(string instanceId)
    {
        if (!instanceId.StartsWith(@"BTHENUM\{", StringComparison.OrdinalIgnoreCase) || instanceId.Length < 17)
        {
            return false;
        }
        ReadOnlySpan<char> uuid = instanceId.AsSpan(9, 8);
        foreach (string profile in AudioProfiles)
        {
            if (uuid.Equals(profile, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Other nodes of the adapter's USB model (for a function, the whole device's), e.g. the virtual adapter's.</summary>
    public static List<string> OtherInstances(string instanceId)
    {
        var ids = new List<string>();
        string device = instanceId[..instanceId.LastIndexOf('\\')];
        int function = device.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
        if (function >= 0)
        {
            device = device[..function];
        }
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\{device}");
        foreach (string instance in key?.GetSubKeyNames() ?? [])
        {
            string id = $@"{device}\{instance}";
            if (!id.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>Binds the in-box WinUSB driver. Returns whether Windows asked for a restart.</summary>
    public static bool BindWinUsb(string instanceId)
    {
        Log.Info("radio", $"binding WinUSB to {instanceId}");
        if (IsFunction(instanceId))
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey($@"{EnumKey}\{instanceId}\Device Parameters");
            key.SetValue("DeviceInterfaceGUIDs", new[] { FunctionInterface }, RegistryValueKind.MultiString);
        }
        return Install(instanceId, winUsb: true);
    }

    /// <summary>Installs the best-ranked compatible (native) driver again. Returns whether Windows asked for a restart.</summary>
    public static bool RestoreNative(string instanceId)
    {
        Log.Info("radio", $"restoring the native driver on {instanceId}");
        return Install(instanceId, winUsb: false);
    }

    /// <summary>Sets CONFIGFLAG_REINSTALL on an unplugged instance, so Windows installs its native driver on re-plug.</summary>
    public static bool MarkForReinstall(string instanceId)
    {
        const uint CmLocateDevNodePhantom = 1, CmDrpConfigFlags = 0x0B, ConfigFlagReinstall = 0x20;
        if (CM_Locate_DevNode(out uint dev, instanceId, CmLocateDevNodePhantom) != 0)
        {
            return false;
        }
        uint flags = 0;
        uint size = sizeof(uint);
        // No ConfigFlags value yet reads as 0.
        CM_Get_DevNode_Registry_Property(dev, CmDrpConfigFlags, out _, &flags, ref size, 0);
        flags |= ConfigFlagReinstall;
        return CM_Set_DevNode_Registry_Property(dev, CmDrpConfigFlags, &flags, sizeof(uint), 0) == 0;
    }

    private static bool Install(string instanceId, bool winUsb)
    {
        const int SpditClassDriver = 1, SpditCompatDriver = 2;
        const int DiEnumSingleInf = 0x00010000, DiFlagsExAllowExcludedDrvs = 0x00000800;
        nint set = SetupDiCreateDeviceInfoList(0, 0);
        if (set == -1)
        {
            throw new Win32Exception();
        }
        try
        {
            var dev = new SpDevinfoData { CbSize = sizeof(SpDevinfoData) };
            if (!SetupDiOpenDeviceInfo(set, instanceId, 0, 0, ref dev))
            {
                throw new Win32Exception();
            }
            var p = new SpDevinstallParams { CbSize = sizeof(SpDevinstallParams) };
            if (!SetupDiGetDeviceInstallParams(set, ref dev, ref p))
            {
                throw new Win32Exception();
            }
            int type = SpditCompatDriver;
            if (winUsb)
            {
                p.Flags |= DiEnumSingleInf;
                p.FlagsEx |= DiFlagsExAllowExcludedDrvs;
                p.SetDriverPath(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\INF\winusb.inf"));
                type = SpditClassDriver;
                // WinUSB is a class driver of setup class USBDevice; the adapter is class Bluetooth. Switch the class
                // first, as Device Manager's "pick from a list" does. The native driver brings its own class back.
                byte[] guid = System.Text.Encoding.Unicode.GetBytes(UsbDeviceClass.ToString("B") + "\0");
                fixed (byte* g = guid)
                {
                    if (!SetupDiSetDeviceRegistryProperty(set, ref dev, 0x8 /* SPDRP_CLASSGUID */, g, guid.Length))
                    {
                        throw new Win32Exception();
                    }
                }
                dev.ClassGuid = UsbDeviceClass;
            }
            if (!SetupDiSetDeviceInstallParams(set, ref dev, ref p) || !SetupDiBuildDriverInfoList(set, ref dev, type))
            {
                throw new Win32Exception();
            }
            if (winUsb)
            {
                SpDrvinfoData? pick = null;
                for (int i = 0; ; i++)
                {
                    var di = new SpDrvinfoData { CbSize = sizeof(SpDrvinfoData) };
                    if (!SetupDiEnumDriverInfo(set, ref dev, type, i, ref di))
                    {
                        break;
                    }
                    if (di.Description.Contains("WinUsb", StringComparison.OrdinalIgnoreCase))
                    {
                        pick = di;
                        break;
                    }
                }
                if (pick is not { } sel)
                {
                    throw new InvalidOperationException("winusb.inf offers no WinUsb Device driver");
                }
                if (!SetupDiSetSelectedDriver(set, ref dev, ref sel))
                {
                    throw new Win32Exception();
                }
            }
            else if (!SetupDiCallClassInstaller(0x17 /* DIF_SELECTBESTCOMPATDRV */, set, ref dev))
            {
                throw new Win32Exception();
            }
            var chosen = new SpDrvinfoData { CbSize = sizeof(SpDrvinfoData) };
            if (!SetupDiGetSelectedDriver(set, ref dev, ref chosen) || !DiInstallDevice(0, set, ref dev, ref chosen, 0, out int reboot))
            {
                throw new Win32Exception();
            }
            Log.Info("radio", $"installed \"{chosen.Description}\" on {instanceId}, restart needed: {reboot != 0}");
            return reboot != 0;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    // usbip-win2's virtual devices hang under its UDE host controller (service usbip2_ude).
    private static bool IsVirtual(string instanceId)
    {
        if (CM_Locate_DevNode(out uint dev, instanceId, 0) != 0)
        {
            return false;
        }
        for (int i = 0; i < 4 && CM_Get_Parent(out uint parent, dev, 0) == 0; i++, dev = parent)
        {
            if (string.Equals(GetService(parent), "usbip2_ude", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasDescendant(string instanceId, string prefix, int depth)
    {
        return CM_Locate_DevNode(out uint dev, instanceId, 0) == 0 && Walk(dev, depth);

        bool Walk(uint node, int left)
        {
            if (left == 0 || CM_Get_Child(out uint child, node, 0) != 0)
            {
                return false;
            }
            do
            {
                if ((GetInstanceId(child)?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false) || Walk(child, left - 1))
                {
                    return true;
                }
            }
            while (CM_Get_Sibling(out child, child, 0) == 0);
            return false;
        }
    }

    private static string? GetService(string instanceId) =>
        CM_Locate_DevNode(out uint dev, instanceId, 0) == 0 ? GetService(dev) : null;

    private static string? GetService(uint dev)
    {
        char* buf = stackalloc char[256];
        uint size = 256 * sizeof(char);
        return CM_Get_DevNode_Registry_Property(dev, 5 /* CM_DRP_SERVICE */, out _, buf, ref size, 0) == 0 ? new string(buf) : null;
    }

    private static string? GetInstanceId(uint dev)
    {
        char* buf = stackalloc char[201];
        return CM_Get_Device_ID(dev, buf, 201, 0) == 0 ? new string(buf) : null;
    }

    private static string? GetInterfaceInstance(string path)
    {
        var key = new DevPropKey { FmtId = new("78c34fc8-104a-4aca-9ea4-524d52996e57"), Pid = 256 };
        char* id = stackalloc char[201];
        uint size = 201 * sizeof(char);
        return CM_Get_Device_Interface_Property(path, key, out uint type, (byte*)id, ref size, 0) == 0 && type == 0x12 ? new string(id) : null;
    }

    private static List<string> GetInterfaces(in Guid cls, string? deviceId = null)
    {
        var paths = new List<string>();
        char[] buffer;
        fixed (char* id = deviceId)
        {
            if (CM_Get_Device_Interface_List_Size(out uint length, cls, id, 0) != 0 || length <= 1)
            {
                return paths;
            }
            buffer = new char[length];
            fixed (char* p = buffer)
            {
                if (CM_Get_Device_Interface_List(cls, id, p, length, 0) != 0)
                {
                    return paths;
                }
            }
        }
        foreach (string s in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            paths.Add(s);
        }
        return paths;
    }

    private struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public int CbSize;
        public Guid ClassGuid;
        public int DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinstallParams
    {
        public int CbSize;
        public int Flags;
        public int FlagsEx;
        public nint HwndParent;
        public nint InstallMsgHandler;
        public nint InstallMsgHandlerContext;
        public nint FileQueue;
        public nint ClassInstallReserved;
        public int Reserved;
        public fixed char DriverPath[260];

        public void SetDriverPath(string path)
        {
            for (int i = 0; i < 260; i++)
            {
                DriverPath[i] = i < path.Length ? path[i] : '\0';
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDrvinfoData
    {
        public int CbSize;
        public int DriverType;
        public nint Reserved;
        private fixed char _description[256];
        private fixed char _mfgName[256];
        private fixed char _providerName[256];
        public long DriverDate;
        public ulong DriverVersion;

        public string Description
        {
            get
            {
                fixed (char* p = _description)
                {
                    return new string(p);
                }
            }
        }
    }

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial nint SetupDiCreateDeviceInfoList(nint classGuid, nint hwnd);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint set);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInfoW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiOpenDeviceInfo(nint set, string instanceId, nint hwnd, int flags, ref SpDevinfoData data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstallParamsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstallParams(nint set, ref SpDevinfoData data, ref SpDevinstallParams p);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiSetDeviceInstallParamsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiSetDeviceInstallParams(nint set, ref SpDevinfoData data, ref SpDevinstallParams p);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiSetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiSetDeviceRegistryProperty(nint set, ref SpDevinfoData data, int property, byte* buffer, int size);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiBuildDriverInfoList(nint set, ref SpDevinfoData data, int type);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiEnumDriverInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDriverInfo(nint set, ref SpDevinfoData data, int type, int index, ref SpDrvinfoData info);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiSetSelectedDriverW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiSetSelectedDriver(nint set, ref SpDevinfoData data, ref SpDrvinfoData info);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetSelectedDriverW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetSelectedDriver(nint set, ref SpDevinfoData data, ref SpDrvinfoData info);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiCallClassInstaller(int function, nint set, ref SpDevinfoData data);

    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DiInstallDevice(nint hwnd, nint set, ref SpDevinfoData data, ref SpDrvinfoData info, int flags, out int reboot);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Uninstall_DevNode(uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Register_Notification(void* filter, nint context, delegate* unmanaged<nint, nint, int, nint, uint, uint> callback, out nint notify);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Unregister_Notification(nint notify);

    /// <summary>
    /// Runs <paramref name="onRemove"/> once when Windows wants to remove the device behind <paramref name="handle"/>. It
    /// must close the handle before returning, or Windows refuses the removal; dispose the watch only after that.
    /// </summary>
    public static IDisposable WatchRemoval(SafeFileHandle handle, Action onRemove)
    {
        var watch = new RemovalWatch(onRemove);
        watch.Self = GCHandle.Alloc(watch);
        // CM_NOTIFY_FILTER: cbSize, Flags, FilterType (1 = DEVICEHANDLE), Reserved, then the handle in the union.
        const int size = 416;
        byte* filter = stackalloc byte[size];
        new Span<byte>(filter, size).Clear();
        *(uint*)filter = size;
        *(uint*)(filter + 8) = 1;
        *(nint*)(filter + 16) = handle.DangerousGetHandle();
        uint cr = CM_Register_Notification(filter, GCHandle.ToIntPtr(watch.Self), &OnNotify, out watch.Notify);
        if (cr != 0)
        {
            Log.Warn("radio", $"removal of the adapter not watched: CONFIGRET {cr}");
            watch.Dispose();
        }
        return watch;
    }

    [UnmanagedCallersOnly]
    private static uint OnNotify(nint notify, nint context, int action, nint data, uint size)
    {
        // CM_NOTIFY_ACTION_DEVICEQUERYREMOVE (2) or DEVICEREMOVEPENDING (4, already unplugged).
        if (action is 2 or 4)
        {
            try
            {
                ((RemovalWatch)GCHandle.FromIntPtr(context).Target!).Fire();
            }
            catch (Exception e)
            {
                Log.Error("radio", "letting the adapter go failed", e);
            }
        }
        return 0;
    }

    private sealed class RemovalWatch(Action onRemove) : IDisposable
    {
        public GCHandle Self;
        public nint Notify;
        private int _fired;

        public void Fire()
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                onRemove();
            }
        }

        public void Dispose()
        {
            if (Notify != 0)
            {
                CM_Unregister_Notification(Notify);
                Notify = 0;
            }
            if (Self.IsAllocated)
            {
                Self.Free();
            }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Child(out uint child, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Sibling(out uint sibling, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static partial uint CM_Get_Device_ID(uint devInst, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static partial uint CM_Get_DevNode_Registry_Property(uint devInst, uint property, out uint type, void* buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Set_DevNode_Registry_PropertyW")]
    private static partial uint CM_Set_DevNode_Registry_Property(uint devInst, uint property, void* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_Property(string path, in DevPropKey key, out uint type, byte* buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static partial uint CM_Get_Device_Interface_List_Size(out uint length, in Guid interfaceClass, char* deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static partial uint CM_Get_Device_Interface_List(in Guid interfaceClass, char* deviceId, char* buffer, uint length, uint flags);
}
