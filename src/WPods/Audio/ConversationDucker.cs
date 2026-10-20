using System.Runtime.InteropServices;
using WPods.Core;

namespace WPods.Audio;

/// <summary>
/// Conversation awareness on the host: lowers the default console render endpoint's master volume while the
/// AirPods report a conversation (level 1 -> 25 %, 2 -> 15 %, 3 -> at most 25 %, 4/6/7/8/9 -> restore) and restores
/// it when the level ends, the feature is turned off, the session detaches or on <see cref="Dispose"/>.
/// Create before <see cref="ConnectionManager.Start"/>.
/// </summary>
public sealed unsafe partial class ConversationDucker : IDisposable
{
    private const int ClsctxInprocServer = 0x1;
    private const int ClsctxAll = 0x17;
    private const int RenderFlow = 0; // eRender
    private const int ConsoleRole = 0; // eConsole
    private const float FirstLevelVolume = 0.25f;
    private const float SecondLevelVolume = 0.15f;

    private static readonly Guid MmDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MmDeviceEnumeratorInterface = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid AudioEndpointVolumeInterface = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private readonly Lock _lock = new();
    private AapSession? _session;
    private int _pending;
    // Under _lock (COM work runs on thread-pool threads, which are MTA).
    private void* _enumerator;
    private void* _volume;
    private float _original;
    private volatile bool _active;

    public ConversationDucker(ConnectionManager connections)
    {
        connections.Attached += OnAttached;
        connections.Detached += OnDetached;
    }

    private enum Request
    {
        None,
        Restore,
        DuckFirst,
        DuckSecond,
        Cap,
    }

    /// <summary>Restores the volume synchronously (call on exit).</summary>
    public void Dispose()
    {
        Volatile.Write(ref _pending, (int)Request.Restore);
        // Waited on a thread-pool thread: the COM objects were created in the MTA.
        Task.Run(Run).Wait();
    }

    private void OnAttached(AapSession session)
    {
        Volatile.Write(ref _session, session);
        session.Changed += OnChanged;
    }

    private void OnDetached(AapSession session)
    {
        session.Changed -= OnChanged;
        Interlocked.CompareExchange(ref _session, null, session);
        Post(Request.Restore);
    }

    private void OnChanged(DeviceChange change)
    {
        if (Volatile.Read(ref _session) is not { } session)
        {
            return;
        }
        if ((change & DeviceChange.ConversationLevel) != 0)
        {
            Post(session.State.ConversationLevel switch
            {
                1 => Request.DuckFirst,
                2 => Request.DuckSecond,
                3 => Request.Cap,
                4 or 6 or 7 or 8 or 9 => Request.Restore,
                _ => Request.None,
            });
        }
        if ((change & DeviceChange.Settings) != 0 && _active && session.State.ConversationAwareness == false)
        {
            Post(Request.Restore);
        }
    }

    // Latest request wins; Changed may run on the caller's (UI) thread, so COM work is moved to the pool.
    private void Post(Request request)
    {
        if (request == Request.None)
        {
            return;
        }
        Volatile.Write(ref _pending, (int)request);
        ThreadPool.UnsafeQueueUserWorkItem(static d => d.Run(), this, preferLocal: false);
    }

    private void Run()
    {
        lock (_lock)
        {
            switch ((Request)Interlocked.Exchange(ref _pending, (int)Request.None))
            {
                case Request.Restore:
                    if (_active)
                    {
                        SetVolume(_original);
                        EndEpisode();
                    }
                    break;
                case Request.DuckFirst:
                    Duck(FirstLevelVolume);
                    break;
                case Request.DuckSecond:
                    Duck(SecondLevelVolume);
                    break;
                case Request.Cap:
                    if (_active)
                    {
                        SetVolume(Math.Min(_original, FirstLevelVolume));
                    }
                    break;
            }
        }
    }

    // The first duck of an episode saves the volume and keeps the endpoint until the restore.
    private void Duck(float target)
    {
        if (!_active)
        {
            if (!TryOpenEndpoint())
            {
                return;
            }
            float original;
            // IAudioEndpointVolume::GetMasterVolumeLevelScalar
            if (((delegate* unmanaged<void*, float*, int>)Slot(_volume, 9))(_volume, &original) < 0)
            {
                EndEpisode();
                return;
            }
            _original = original;
            _active = true;
        }
        if (_original > target)
        {
            SetVolume(target);
        }
    }

    private bool TryOpenEndpoint()
    {
        if (_enumerator is null)
        {
            void* enumerator;
            if (CoCreateInstance(MmDeviceEnumeratorClass, null, ClsctxInprocServer, MmDeviceEnumeratorInterface, &enumerator) < 0)
            {
                return false;
            }
            _enumerator = enumerator;
        }
        void* device;
        // IMMDeviceEnumerator::GetDefaultAudioEndpoint
        if (((delegate* unmanaged<void*, int, int, void**, int>)Slot(_enumerator, 4))(_enumerator, RenderFlow, ConsoleRole, &device) < 0)
        {
            return false;
        }
        void* volume;
        Guid iid = AudioEndpointVolumeInterface;
        // IMMDevice::Activate
        int hr = ((delegate* unmanaged<void*, Guid*, int, void*, void**, int>)Slot(device, 3))(device, &iid, ClsctxAll, null, &volume);
        Release(device);
        if (hr < 0)
        {
            return false;
        }
        _volume = volume;
        return true;
    }

    private void EndEpisode()
    {
        Release(_volume);
        _volume = null;
        _active = false;
    }

    // IAudioEndpointVolume::SetMasterVolumeLevelScalar
    private void SetVolume(float level) =>
        ((delegate* unmanaged<void*, float, Guid*, int>)Slot(_volume, 7))(_volume, level, null);

    private static void* Slot(void* instance, int slot) => (*(void***)instance)[slot];

    private static void Release(void* instance) => ((delegate* unmanaged<void*, uint>)Slot(instance, 2))(instance);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, void* outer, int context, in Guid interfaceId, void** instance);
}
