using System.Runtime.InteropServices;
using WPods.Core;

namespace WPods.Audio;

/// <summary>
/// Conversation awareness on the host, relative to the default console render endpoint's master volume at the start
/// of the conversation (the "start" volume). The AirPods report a 0x4B level (Apple captures: 1/2 = person started
/// speaking, lowest volume; intermediate values = intermediate volume; 8/9 = normal): level 1 or 2 starts an episode
/// and sets start x DuckPercent / 100 (the pair's "Lower media to" host setting, read at the episode start); levels
/// 3..7 step back up toward the start volume (<see cref="Recovery"/>); 8/9 restore the start volume. Every change after
/// the first, and the restore, happen only while the volume is still the one WPods set; a volume the user changed ends
/// the episode and is kept. Also restores when the feature is turned off, the session detaches and on
/// <see cref="Dispose"/>. Create before <see cref="ConnectionManager.Start"/>.
/// </summary>
public sealed unsafe partial class ConversationDucker : IDisposable
{
    private const int ClsctxInprocServer = 0x1;
    private const int ClsctxAll = 0x17;
    private const int RenderFlow = 0; // eRender
    private const int ConsoleRole = 0; // eConsole
    private const int NoRequest = 0;
    private const int RestoreRequest = -1;
    private const int LowestLevel = 2;
    private const int LastStepLevel = 7;
    private const float SameVolumeTolerance = 0.005f;

    private static readonly Guid MmDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MmDeviceEnumeratorInterface = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid AudioEndpointVolumeInterface = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private readonly SettingsStore _settings;
    private readonly Lock _lock = new();
    private AapSession? _session;
    private int _pending; // a level 1..7, RestoreRequest or NoRequest
    // Under _lock (COM work runs on thread-pool threads, which are MTA).
    private void* _enumerator;
    private void* _volume;
    private float _start;
    private float _ducked;
    private float _set = float.NaN; // last volume WPods set in this episode
    private volatile bool _active;

    public ConversationDucker(ConnectionManager connections)
    {
        _settings = connections.Settings;
        connections.Attached += OnAttached;
        connections.Detached += OnDetached;
    }

    /// <summary>
    /// Share of the way from the ducked volume back to the start volume, indexed by level 0..7 (0 unused). Levels 1-2
    /// are the lowest; Apple's captures only say intermediate values are intermediate volumes, so the five values between
    /// the floor (2) and normal (8) get equal steps of 1/6: every reported step is the same small rise, so the return
    /// sounds gradual (paced by the AirPods' own level reports) and never jumps. Linear in the endpoint's scalar volume,
    /// which already follows Windows' audio taper (the volume slider position).
    /// </summary>
    private static ReadOnlySpan<float> Recovery => [0f, 0f, 0f, 1f / 6, 2f / 6, 3f / 6, 4f / 6, 5f / 6];

    /// <summary>Restores the volume synchronously (call on exit).</summary>
    public void Dispose()
    {
        Volatile.Write(ref _pending, RestoreRequest);
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
        Post(RestoreRequest);
    }

    private void OnChanged(DeviceChange change)
    {
        if (Volatile.Read(ref _session) is not { } session)
        {
            return;
        }
        if ((change & DeviceChange.ConversationLevel) != 0)
        {
            byte? level = session.State.ConversationLevel;
            Log.Info("ducker", $"conversation level {level}");
            Post(level switch
            {
                >= 1 and <= LastStepLevel => level.Value,
                8 or 9 => RestoreRequest,
                _ => NoRequest,
            });
        }
        if ((change & DeviceChange.Settings) != 0 && _active && session.State.ConversationAwareness == false)
        {
            Log.Info("ducker", "conversation awareness turned off");
            Post(RestoreRequest);
        }
    }

    // Latest request wins; Changed may run on the caller's (UI) thread, so COM work is moved to the pool.
    private void Post(int request)
    {
        if (request == NoRequest)
        {
            return;
        }
        Volatile.Write(ref _pending, request);
        ThreadPool.UnsafeQueueUserWorkItem(static d => d.Run(), this, preferLocal: false);
    }

    private void Run()
    {
        lock (_lock)
        {
            int request = Interlocked.Exchange(ref _pending, NoRequest);
            if (request == RestoreRequest)
            {
                Restore();
            }
            else if (request != NoRequest)
            {
                Apply(request);
            }
        }
    }

    private void Restore()
    {
        if (!_active)
        {
            return;
        }
        if (IsStillOurs())
        {
            SetVolume(_start);
            Log.Info("ducker", $"restore to {_start:0.00}");
        }
        else
        {
            Log.Info("ducker", "restore skipped: volume changed by the user while ducked");
        }
        EndEpisode();
    }

    // Level 1/2 starts an episode (saves the start volume, keeps the endpoint until the restore); 3..7 only step a
    // running episode.
    private void Apply(int level)
    {
        if (!_active)
        {
            if (level > LowestLevel || Volatile.Read(ref _session) is not { } session)
            {
                return;
            }
            if (!TryOpenEndpoint())
            {
                Log.Warn("ducker", "duck skipped: default render endpoint not available");
                return;
            }
            if (!TryGetVolume(out float start))
            {
                Log.Warn("ducker", "duck skipped: volume not readable");
                EndEpisode();
                return;
            }
            int percent = _settings.Get(session.Address).DuckPercent;
            _start = start;
            _ducked = start * percent / 100f;
            _active = true;
            Log.Info("ducker", $"episode start: volume {start:0.00}, lower to {percent} % = {_ducked:0.00}");
        }
        else if (!IsStillOurs())
        {
            Log.Info("ducker", $"level {level}: volume changed by the user while ducked, kept; episode ended");
            EndEpisode();
            return;
        }
        float volume = _ducked + (_start - _ducked) * Recovery[level];
        SetVolume(volume);
        Log.Info("ducker", $"level {level}: volume {volume:0.00} (start {_start:0.00})");
    }

    // The endpoint stores volume in dB steps, so a scalar read back can differ slightly from the one set.
    private bool IsStillOurs() =>
        !float.IsNaN(_set) && TryGetVolume(out float current) && MathF.Abs(current - _set) <= SameVolumeTolerance;

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
        _set = float.NaN;
        _active = false;
    }

    // IAudioEndpointVolume::GetMasterVolumeLevelScalar
    private bool TryGetVolume(out float level)
    {
        float value;
        bool ok = ((delegate* unmanaged<void*, float*, int>)Slot(_volume, 9))(_volume, &value) >= 0;
        level = value;
        return ok;
    }

    // IAudioEndpointVolume::SetMasterVolumeLevelScalar
    private void SetVolume(float level)
    {
        if (((delegate* unmanaged<void*, float, Guid*, int>)Slot(_volume, 7))(_volume, level, null) >= 0)
        {
            _set = level;
        }
    }

    private static void* Slot(void* instance, int slot) => (*(void***)instance)[slot];

    private static void Release(void* instance) => ((delegate* unmanaged<void*, uint>)Slot(instance, 2))(instance);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, void* outer, int context, in Guid interfaceId, void** instance);
}
