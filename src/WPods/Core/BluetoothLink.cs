using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WPods.Core;

/// <summary>A paired device with an NTAPD interface, as Windows reports it.</summary>
internal readonly record struct LinkDevice(ulong Address, string Name, bool Up);

/// <summary>
/// Paired AirPods (devices exposing the NTAPD interface) and their ACL link state, event-driven:
/// GUID_BLUETOOTH_HCI_EVENT per radio (CM_Register_Notification), radio and NTAPD interface arrival/removal.
/// Initial state from BluetoothGetDeviceInfo. No polling. Events fire on CM notification or thread-pool threads.
/// </summary>
internal sealed unsafe class BluetoothLink : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ulong, Entry> _devices = [];
    private readonly Dictionary<string, ulong> _addressByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, nint> _radioByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, string> _radioByNotify = [];
    private GCHandle _self;
    private nint _radioClassNotify;
    private nint _ntapdClassNotify;
    private bool _disposed;

    /// <summary>ACL link of a known device went up (true) or down (false).</summary>
    public event Action<ulong, bool>? LinkChanged;

    /// <summary>A device was paired/unpaired (NTAPD interface arrived or was removed).</summary>
    public event Action? DevicesChanged;

    /// <summary>Registers notifications first, then reads the initial state (no events for it).</summary>
    public void Start()
    {
        _self = GCHandle.Alloc(this);
        nint context = GCHandle.ToIntPtr(_self);
        _radioClassNotify = Native.RegisterInterfaceNotification(Native.RadioInterface, context, &OnNotify);
        _ntapdClassNotify = Native.RegisterInterfaceNotification(Native.NtapdInterface, context, &OnNotify);
        foreach (string path in Native.GetInterfaces(Native.RadioInterface))
        {
            AddRadio(path);
        }
        foreach (string path in Native.GetInterfaces(Native.NtapdInterface))
        {
            AddDevice(path, raiseEvents: false);
        }
        Log.Info("link", $"start: radio notify={_radioClassNotify != 0}, NTAPD notify={_ntapdClassNotify != 0}");
    }

    public bool IsUp(ulong address)
    {
        lock (_lock)
        {
            return _devices.TryGetValue(address, out Entry? entry) && entry.Up;
        }
    }

    public string? GetPath(ulong address)
    {
        lock (_lock)
        {
            return _devices.TryGetValue(address, out Entry? entry) ? entry.Path : null;
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

    public List<LinkDevice> Snapshot()
    {
        lock (_lock)
        {
            var list = new List<LinkDevice>(_devices.Count);
            foreach ((ulong address, Entry entry) in _devices)
            {
                list.Add(new LinkDevice(address, entry.Name, entry.Up));
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
        Unregister(_ntapdClassNotify);
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
            case Native.CmNotifyActionInterfaceArrival or Native.CmNotifyActionInterfaceRemoval:
                bool radio = Unsafe.ReadUnaligned<Guid>(data + Native.EventGuidOffset) == Native.RadioInterface;
                string path = new((char*)(data + Native.SymbolicLinkOffset));
                bool arrival = action == Native.CmNotifyActionInterfaceArrival;
                // Device queries and registrations are blocking: keep them off the notification thread.
                if (radio && arrival)
                {
                    ThreadPool.QueueUserWorkItem(_ => self.AddRadio(path));
                }
                else if (!radio && arrival)
                {
                    ThreadPool.QueueUserWorkItem(_ => self.AddDevice(path, raiseEvents: true));
                }
                else if (!radio)
                {
                    ThreadPool.QueueUserWorkItem(_ => self.RemoveDevice(path));
                }
                break;
            case Native.CmNotifyActionDeviceRemoveComplete:
                ThreadPool.QueueUserWorkItem(_ => self.RemoveRadio(notify));
                break;
        }
        return 0;
    }

    private void OnAcl(ulong address, bool connected)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(address, out Entry? entry) || entry.Up == connected)
            {
                Log.Info("link", $"HCI ACL {Mac.Format(address)} {(connected ? "up" : "down")} ({(entry is null ? "not an NTAPD device" : "no change")})");
                return;
            }
            entry.Up = connected;
        }
        Log.Info("link", $"HCI ACL {Mac.Format(address)} {(connected ? "up" : "down")}");
        LinkChanged?.Invoke(address, connected);
    }

    private void AddRadio(string path)
    {
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
            Log.Info("link", $"radio added {path}, HCI notify registered={notify != 0}");
        }
    }

    // A radio went away: drop its registration and re-check which links are still up.
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

    private void AddDevice(string path, bool raiseEvents)
    {
        if (!Mac.TryParseInterfacePath(path, out ulong address))
        {
            Log.Warn("link", $"NTAPD interface without an address: {path}");
            return;
        }
        bool up;
        // Queried under the lock: an HCI event racing with startup waits and is applied after the insert.
        lock (_lock)
        {
            if (_disposed || _devices.ContainsKey(address))
            {
                return;
            }
            up = Query(address, out string name);
            _devices[address] = new Entry(path, name) { Up = up };
            _addressByPath[path] = address;
            Log.Info("link", $"NTAPD {(raiseEvents ? "added" : "present")} {Mac.Format(address)} \"{name}\", link {(up ? "up" : "down")}");
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

    private void RemoveDevice(string path)
    {
        Entry? entry;
        ulong address;
        lock (_lock)
        {
            if (!_addressByPath.Remove(path, out address) || !_devices.Remove(address, out entry))
            {
                return;
            }
        }
        Log.Info("link", $"NTAPD removed {Mac.Format(address)}, link was {(entry.Up ? "up" : "down")}");
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

    private sealed class Entry(string path, string name)
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public bool Up { get; set; }
    }
}
