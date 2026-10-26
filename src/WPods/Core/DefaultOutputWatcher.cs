using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace WPods.Core;

/// <summary>
/// Default-output follow: with several AirPods pairs linked, the session goes to the pair that is the default Windows
/// playback device (wpods_native wpn_audio_is_default_output: the default console render endpoint's container ID
/// equals the pair's Bluetooth devnode container ID) and switches when Windows switches. A default output that is not
/// a linked pair keeps the current session; a pair the user disconnected is never attached
/// (<see cref="ConnectionManager.FollowDefaultOutputAsync"/>). Never pages. The IMMNotificationClient callback only
/// flags the change and starts one thread-pool worker, which waits <see cref="CoalesceDelay"/> (Windows raises one
/// callback per role for each change) and then decides once. Create before <see cref="ConnectionManager.Start"/>;
/// dispose on exit (same thread).
/// </summary>
public sealed partial class DefaultOutputWatcher : IDisposable
{
    private const int ClsctxInprocServer = 0x1;
    private const int RenderFlow = 0; // eRender
    private const int ConsoleRole = 0; // eConsole
    private const int MultimediaRole = 1; // eMultimedia

    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(250);
    private static readonly Guid MmDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MmDeviceEnumeratorInterface = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    private readonly ConnectionManager _connections;
    private readonly Lock _lock = new();
    // Set by the constructor.
    private unsafe void* _enumerator;
    private unsafe void* _client;
    // Under _lock.
    private bool _pending;
    private bool _running;
    private bool _disposed;

    public unsafe DefaultOutputWatcher(ConnectionManager connections)
    {
        _connections = connections;
        void* enumerator;
        int hr = CoCreateInstance(MmDeviceEnumeratorClass, null, ClsctxInprocServer, MmDeviceEnumeratorInterface, &enumerator);
        if (hr < 0)
        {
            Log.Warn("follow", $"default output not followed: MMDeviceEnumerator error 0x{hr:X8}");
            return;
        }
        void* client = ComInterfaceMarshaller<IMMNotificationClient>.ConvertToUnmanaged(new NotificationClient(this));
        // IMMDeviceEnumerator::RegisterEndpointNotificationCallback
        hr = ((delegate* unmanaged<void*, void*, int>)Slot(enumerator, 6))(enumerator, client);
        if (hr < 0)
        {
            ComInterfaceMarshaller<IMMNotificationClient>.Free(client);
            Release(enumerator);
            Log.Warn("follow", $"default output not followed: register error 0x{hr:X8}");
            return;
        }
        _enumerator = enumerator;
        _client = client;
        Log.Info("follow", "following the default output");
    }

    public unsafe void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        if (_enumerator is null)
        {
            return;
        }
        // IMMDeviceEnumerator::UnregisterEndpointNotificationCallback
        int hr = ((delegate* unmanaged<void*, void*, int>)Slot(_enumerator, 7))(_enumerator, _client);
        ComInterfaceMarshaller<IMMNotificationClient>.Free(_client);
        Release(_enumerator);
        Log.Info("follow", $"stopped, unregister 0x{hr:X8}");
    }

    /// <summary>1 if the pair is the default console render endpoint, 0 if not, &lt;0 HRESULT. Synchronous; not on the UI thread.</summary>
    [LibraryImport("wpods_native.dll")]
    internal static partial int wpn_audio_is_default_output(ulong btAddress);

    // On an audio service thread: flag and return at once.
    private void OnDefaultDeviceChanged(int flow, int role)
    {
        if (flow != RenderFlow || (role != ConsoleRole && role != MultimediaRole))
        {
            return;
        }
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _pending = true;
            if (_running)
            {
                return;
            }
            _running = true;
        }
        Log.Info("follow", $"default render device changed (role {role})");
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            await Task.Delay(CoalesceDelay).ConfigureAwait(false);
            while (TakePending())
            {
                await FollowAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // A change that came after the last pass (or after a failed one) starts a new worker.
            bool again;
            lock (_lock)
            {
                again = _pending && !_disposed;
                _running = again;
            }
            if (again)
            {
                _ = Task.Run(RunAsync);
            }
        }
    }

    private bool TakePending()
    {
        lock (_lock)
        {
            if (!_pending || _disposed)
            {
                return false;
            }
            _pending = false;
            return true;
        }
    }

    private async Task FollowAsync()
    {
        ulong attached = _connections.Session?.Address ?? 0;
        string attachedText = attached == 0 ? "none" : Mac.Format(attached);
        foreach (KnownDevice device in _connections.GetDevices())
        {
            if (!device.LinkUp)
            {
                continue;
            }
            int result = wpn_audio_is_default_output(device.Address);
            if (result < 0)
            {
                Log.Warn("follow", $"{Mac.Format(device.Address)}: default output check error 0x{result:X8}");
                continue;
            }
            if (result == 0)
            {
                continue;
            }
            if (device.Address == attached)
            {
                Log.Info("follow", $"default output is the attached pair {attachedText}, nothing to do");
                return;
            }
            Log.Info("follow", $"default output is {Mac.Format(device.Address)}, session={attachedText}: following");
            await _connections.FollowDefaultOutputAsync(device.Address).ConfigureAwait(false);
            return;
        }
        Log.Info("follow", $"default output is not a linked pair, keeping session={attachedText}");
    }

    private static unsafe void* Slot(void* instance, int slot) => (*(void***)instance)[slot];

    private static unsafe void Release(void* instance) => ((delegate* unmanaged<void*, uint>)Slot(instance, 2))(instance);

    [LibraryImport("ole32.dll")]
    private static unsafe partial int CoCreateInstance(in Guid classId, void* outer, int context, in Guid interfaceId, void** instance);

    // PROPERTYKEY, passed by value; never read.
    internal readonly struct PropertyKey
    {
        public Guid FormatId { get; init; }
        public uint PropertyId { get; init; }
    }

    [GeneratedComInterface]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    internal partial interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged(nint deviceId, uint newState);

        [PreserveSig]
        int OnDeviceAdded(nint deviceId);

        [PreserveSig]
        int OnDeviceRemoved(nint deviceId);

        [PreserveSig]
        int OnDefaultDeviceChanged(int flow, int role, nint defaultDeviceId);

        [PreserveSig]
        int OnPropertyValueChanged(nint deviceId, PropertyKey key);
    }

    [GeneratedComClass]
    private sealed partial class NotificationClient(DefaultOutputWatcher owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(nint deviceId, uint newState) => 0;

        public int OnDeviceAdded(nint deviceId) => 0;

        public int OnDeviceRemoved(nint deviceId) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, nint defaultDeviceId)
        {
            owner.OnDefaultDeviceChanged(flow, role);
            return 0;
        }

        public int OnPropertyValueChanged(nint deviceId, PropertyKey key) => 0;
    }
}
