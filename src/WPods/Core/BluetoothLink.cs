using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WPods.Core;

/// <summary>A paired AirPods device; ProductId comes from its Device ID record (0 when absent), known before connecting.</summary>
internal readonly record struct LinkDevice(ulong Address, string Name, bool Up, ushort ProductId);

/// <summary>
/// Paired AirPods (devices with a present AAP service devnode) and their ACL link state, driven by HCI events, radio
/// arrival/removal and devnode notifications without polling. Events fire on CM notification or thread-pool threads.
/// </summary>
internal sealed unsafe class BluetoothLink : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ulong, Entry> _devices = [];
    private readonly Dictionary<string, nint> _radioByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, string> _radioByNotify = [];
    private GCHandle _self;
    private nint _radioClassNotify;
    private nint _deviceInstanceNotify;
    private bool _disposed;

    /// <summary>ACL link of a known device went up (true) or down (false).</summary>
    public event Action<ulong, bool>? LinkChanged;

    /// <summary>A device was paired/unpaired (its first AAP service devnode appeared or its last one was removed).</summary>
    public event Action? DevicesChanged;

    /// <summary>Registers notifications first, then reads the initial state (no events for it).</summary>
    public void Start()
    {
        _self = GCHandle.Alloc(this);
        nint context = GCHandle.ToIntPtr(_self);
        _radioClassNotify = Native.RegisterInterfaceNotification(Native.RadioInterface, context, &OnNotify);
        _deviceInstanceNotify = Native.RegisterDeviceInstanceNotification(context, &OnNotify);
        foreach (string path in Native.GetInterfaces(Native.RadioInterface))
        {
            AddRadio(path);
        }
        foreach (string instanceId in Native.GetPresentDeviceIds(Native.AapServicePrefix))
        {
            AddDevice(instanceId, raiseEvents: false);
        }
        Log.Info("link", $"start: radio notify={_radioClassNotify != 0}, AAP service device notify={_deviceInstanceNotify != 0}");
    }

    public bool IsUp(ulong address)
    {
        lock (_lock)
        {
            return _devices.TryGetValue(address, out Entry? entry) && entry.Up;
        }
    }

    /// <summary>The device has a present AAP service devnode.</summary>
    public bool IsListed(ulong address)
    {
        lock (_lock)
        {
            return _devices.ContainsKey(address);
        }
    }

    /// <summary>Any device whose link is up (startup choice when the last-used one is not up); 0 = none.</summary>
    public ulong FirstUp()
    {
        lock (_lock)
        {
            foreach ((ulong address, Entry entry) in _devices)
            {
                if (entry.Up)
                {
                    return address;
                }
            }
            return 0;
        }
    }

    /// <summary>Re-reads the connected flag, e.g. after AAP open fails with 1167 (an HCI disconnect can be lost over sleep).</summary>
    public void Recheck(ulong address)
    {
        bool up;
        lock (_lock)
        {
            if (!_devices.TryGetValue(address, out Entry? entry) || entry.Up == (up = Query(address, out _)))
            {
                return;
            }
            entry.Up = up;
        }
        Log.Info("link", $"recheck {Mac.Format(address)}: link actually {(up ? "up" : "down")}");
        LinkChanged?.Invoke(address, up);
    }

    /// <summary>The device's <see cref="LinkDevice.ProductId"/>; 0 when not listed or absent.</summary>
    public ushort ProductId(ulong address)
    {
        lock (_lock)
        {
            return _devices.TryGetValue(address, out Entry? entry) ? entry.ProductId : (ushort)0;
        }
    }

    public List<LinkDevice> Snapshot()
    {
        lock (_lock)
        {
            var list = new List<LinkDevice>(_devices.Count);
            foreach ((ulong address, Entry entry) in _devices)
            {
                list.Add(new LinkDevice(address, entry.Name, entry.Up, entry.ProductId));
            }
            return list;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        // Unregistering waits for running callbacks; never called from a callback.
        Unregister(_radioClassNotify);
        Unregister(_deviceInstanceNotify);
        foreach (nint notify in _radioByNotify.Keys)
        {
            Unregister(notify);
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnNotify(nint notify, nint context, int action, byte* data, uint size)
    {
        try
        {
            var self = (BluetoothLink)GCHandle.FromIntPtr(context).Target!;
            switch (action)
            {
                case Native.CmNotifyActionDeviceCustomEvent:
                    if (Unsafe.ReadUnaligned<Guid>(data + Native.EventGuidOffset) == Native.HciEvent
                        && Unsafe.ReadUnaligned<uint>(data + Native.EventDataSizeOffset) >= 10)
                    {
                        // BTH_HCI_EVENT_INFO: address u64 @0, connectionType @8 (1 = ACL), connected @9.
                        byte* hci = data + Native.EventDataOffset;
                        if (hci[8] == 1)
                        {
                            self.OnAcl(Unsafe.ReadUnaligned<ulong>(hci), hci[9] != 0);
                        }
                    }
                    break;
                case Native.CmNotifyActionInterfaceArrival
                    when Unsafe.ReadUnaligned<Guid>(data + Native.EventGuidOffset) == Native.RadioInterface:
                    string path = new((char*)(data + Native.SymbolicLinkOffset));
                    // Device queries and registrations are blocking: keep them off the notification thread.
                    QueueWork(() => self.AddRadio(path));
                    break;
                case Native.CmNotifyActionDeviceInstanceEnumerated or Native.CmNotifyActionDeviceInstanceStarted
                    or Native.CmNotifyActionDeviceInstanceRemoved:
                    // Raised for every device in the system: only AAP service devnodes are of interest.
                    ReadOnlySpan<char> id = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)(data + Native.InstanceIdOffset));
                    if (!id.StartsWith(Native.AapServicePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    string instanceId = new(id);
                    if (action == Native.CmNotifyActionDeviceInstanceRemoved)
                    {
                        QueueWork(() => self.RemoveDevice(instanceId));
                    }
                    else
                    {
                        QueueWork(() => self.AddDevice(instanceId, raiseEvents: true));
                    }
                    break;
                case Native.CmNotifyActionDeviceRemoveComplete:
                    QueueWork(() => self.RemoveRadio(notify));
                    break;
            }
        }
        catch (Exception e)
        {
            // An exception escaping an unmanaged callback ends the process unlogged.
            Log.Error("link", "device notification failed", e);
        }
        return 0;
    }

    // An unhandled exception in a thread-pool work item ends the process.
    private static void QueueWork(Action work) => ThreadPool.QueueUserWorkItem(_ =>
    {
        try
        {
            work();
        }
        catch (Exception e)
        {
            Log.Error("link", "device notification work failed", e);
        }
    });

    private void OnAcl(ulong address, bool connected)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(address, out Entry? entry) || entry.Up == connected)
            {
                Log.Info("link", $"HCI ACL {Mac.Format(address)} {(connected ? "up" : "down")} ({(entry is null ? "not an AAP service device" : "no change")})");
                return;
            }
            entry.Up = connected;
        }
        Log.Info("link", $"HCI ACL {Mac.Format(address)} {(connected ? "up" : "down")}");
        LinkChanged?.Invoke(address, connected);
    }

    // Links that came up before the radio's HCI registration raised no event, so they are re-checked here.
    private void AddRadio(string path)
    {
        List<ulong> up = [];
        lock (_lock)
        {
            if (_disposed || _radioByPath.ContainsKey(path))
            {
                return;
            }
            nint notify = Native.RegisterHandleNotification(path, GCHandle.ToIntPtr(_self), &OnNotify);
            if (notify != 0)
            {
                _radioByPath[path] = notify;
                _radioByNotify[notify] = path;
            }
            foreach ((ulong address, Entry entry) in _devices)
            {
                if (!entry.Up && Query(address, out _))
                {
                    entry.Up = true;
                    up.Add(address);
                }
            }
            Log.Info("link", $"radio added {path}, HCI notify registered={notify != 0}, links found up: {up.Count}");
        }
        foreach (ulong address in up)
        {
            LinkChanged?.Invoke(address, true);
        }
    }

    private void RemoveRadio(nint notify)
    {
        List<ulong> down = [];
        lock (_lock)
        {
            if (_disposed || !_radioByNotify.Remove(notify, out string? path))
            {
                return;
            }
            _radioByPath.Remove(path);
            foreach ((ulong address, Entry entry) in _devices)
            {
                if (entry.Up && !Query(address, out _))
                {
                    entry.Up = false;
                    down.Add(address);
                }
            }
            Log.Info("link", $"radio removed {path}, links now down: {down.Count}");
        }
        Unregister(notify);
        foreach (ulong address in down)
        {
            LinkChanged?.Invoke(address, false);
        }
    }

    private void AddDevice(string instanceId, bool raiseEvents)
    {
        if (!Mac.TryParseInterfacePath(instanceId, out ulong address))
        {
            Log.Warn("link", $"AAP service device without an address: {instanceId}");
            return;
        }
        bool up;
        // Queried under the lock: an HCI event racing with startup waits and is applied after the insert.
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            if (_devices.TryGetValue(address, out Entry? known))
            {
                // Another radio's devnode of a listed device, or the start after the enumeration: nothing changes.
                known.InstanceIds.Add(instanceId);
                return;
            }
            up = Query(address, out string name);
            _devices[address] = new Entry(instanceId, name) { Up = up };
            Log.Info("link", $"AAP service device {(raiseEvents ? "added" : "present")} {Mac.Format(address)} \"{name}\", link {(up ? "up" : "down")}");
        }
        if (raiseEvents)
        {
            DevicesChanged?.Invoke();
            if (up)
            {
                LinkChanged?.Invoke(address, true);
            }
        }
    }

    // The device is dropped only when its last devnode (one per radio) is gone.
    private void RemoveDevice(string instanceId)
    {
        if (!Mac.TryParseInterfacePath(instanceId, out ulong address))
        {
            return;
        }
        Entry? entry;
        lock (_lock)
        {
            if (!_devices.TryGetValue(address, out entry) || !entry.InstanceIds.Remove(instanceId) || entry.InstanceIds.Count > 0)
            {
                return;
            }
            _devices.Remove(address);
        }
        Log.Info("link", $"AAP service device removed {Mac.Format(address)}, link was {(entry.Up ? "up" : "down")}");
        DevicesChanged?.Invoke();
        if (entry.Up)
        {
            LinkChanged?.Invoke(address, false);
        }
    }

    private static bool Query(ulong address, out string name)
    {
        if (!Native.TryFindPairedDevice(address, out nint radio, out Native.BluetoothDeviceInfo info))
        {
            name = string.Empty;
            return false;
        }
        Native.CloseHandle(radio);
        name = new string(info.Name);
        return info.Connected != 0;
    }

    private static void Unregister(nint notify)
    {
        if (notify != 0)
        {
            Native.CM_Unregister_Notification(notify);
        }
    }

    // Windows names the service devnode after the Device ID record: "...}_VID&0001004c_PID&2024\..." (VID source 0001 =
    // Bluetooth SIG assigned, 004C = Apple). 0 for another vendor or no record ("_LOCALMFG&...").
    private static ushort AppleProductId(string instanceId)
    {
        const string marker = "_VID&0001004C_PID&";
        int at = instanceId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at >= 0 && ushort.TryParse(instanceId.AsSpan(at + marker.Length, Math.Min(4, instanceId.Length - at - marker.Length)),
            System.Globalization.NumberStyles.HexNumber, null, out ushort id) ? id : (ushort)0;
    }

    private sealed class Entry(string instanceId, string name)
    {
        // Present AAP service devnodes of the device (one per radio it is paired on).
        public HashSet<string> InstanceIds { get; } = new(StringComparer.OrdinalIgnoreCase) { instanceId };
        public string Name { get; } = name;
        public ushort ProductId { get; } = AppleProductId(instanceId);
        public bool Up { get; set; }
    }
}
