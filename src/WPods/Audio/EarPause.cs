using System.Runtime.InteropServices;
using WPods.Core;
using Timer = System.Threading.Timer;

namespace WPods.Audio;

public sealed partial class EarPause
{
    private const int Unknown = -1;
    private static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(15);

    private readonly Lock _lock = new();
    // Under _lock.
    private AapSession? _session;
    private int _latest = Unknown;
    private bool _earDetectionOn;
    private bool _running;
    private bool _reset;
    private string? _forget;
    private Timer? _timer;
    private int _timerGeneration;
    // Worker only (at most one worker runs, guarded by _running).
    private int _applied = Unknown;
    // The native helper remembers paused sessions (wpn_media_pause_all paused at least one since the last resume/forget).
    private bool _pending;

    public EarPause(ConnectionManager connections)
    {
        connections.Attached += OnAttached;
        connections.Detached += OnDetached;
    }

    private void OnAttached(AapSession session)
    {
        lock (_lock)
        {
            _session = session;
            _latest = Unknown;
            _earDetectionOn = session.State.EarDetection == true;
            _reset = true;
            _forget = "session changed";
            Kick();
        }
        session.Changed += OnChanged;
    }

    private void OnDetached(AapSession session)
    {
        session.Changed -= OnChanged;
        lock (_lock)
        {
            if (_session == session)
            {
                _session = null;
                _latest = Unknown;
                _reset = true;
                _forget = "AirPods detached";
                Kick();
            }
        }
    }

    private void OnChanged(DeviceChange change)
    {
        if ((change & (DeviceChange.Ear | DeviceChange.Settings)) == 0)
        {
            return;
        }
        lock (_lock)
        {
            if (_session is not { } session)
            {
                return;
            }
            DeviceState state = session.State;
            bool on = state.EarDetection == true;
            if (_earDetectionOn && !on)
            {
                _forget = "ear detection turned off";
                Kick();
            }
            _earDetectionOn = on;
            if ((change & DeviceChange.Ear) != 0)
            {
                _latest = (state.PrimaryEar == EarStatus.InEar ? 1 : 0) + (state.SecondaryEar == EarStatus.InEar ? 1 : 0);
                Kick();
            }
        }
    }

    // Under _lock.
    private void Kick()
    {
        if (!_running)
        {
            _running = true;
            ThreadPool.UnsafeQueueUserWorkItem(static p => p.Run(), this, preferLocal: false);
        }
    }

    private void Run()
    {
        while (true)
        {
            AapSession? session;
            int count;
            bool reset;
            string? forget;
            lock (_lock)
            {
                reset = _reset;
                _reset = false;
                forget = _forget;
                _forget = null;
                session = _session;
                count = _latest;
                if (count > 0)
                {
                    // A bud is in ear: the 15 s window is over either way.
                    StopTimer();
                }
                if (!reset && forget is null && (session is null || count == _applied))
                {
                    _running = false;
                    return;
                }
            }
            if (reset)
            {
                _applied = Unknown;
            }
            if (forget is not null)
            {
                Forget(forget);
            }
            if (session is not null && count != _applied)
            {
                Apply(session, _applied, count);
                _applied = count;
            }
        }
    }

    private void Apply(AapSession session, int previous, int count)
    {
        string what = $"in ear {(previous == Unknown ? "?" : previous)} -> {count}";
        if (previous == Unknown)
        {
            Log.Info("earpause", $"{what}: first report, no action");
            return;
        }
        if (count < previous)
        {
            OnRemoved(session, what, count);
            return;
        }
        if (!_pending)
        {
            Log.Info("earpause", $"{what}: no resume (nothing paused by WPods)");
            return;
        }
        _pending = false;
        if (!CanAct(session, what))
        {
            wpn_media_forget();
            Log.Info("earpause", $"{what}: resume skipped, paused sessions forgotten");
            return;
        }
        int resumed = wpn_media_resume_paused();
        Log.Info("earpause", $"{what}: resume -> {Describe(resumed)}");
    }

    // Never plays: pauses whatever is playing, and at 0 in ear starts the resume window.
    private void OnRemoved(AapSession session, string what, int count)
    {
        if (CanAct(session, what))
        {
            int paused = wpn_media_pause_all();
            _pending |= paused > 0;
            Log.Info("earpause", $"{what}: pause all -> {(paused >= 0 ? $"{paused} session(s) paused" : $"error 0x{paused:X8}")}");
        }
        if (count != 0)
        {
            return;
        }
        if (!_pending)
        {
            Log.Info("earpause", $"{what}: no bud in ear, nothing to resume");
            return;
        }
        lock (_lock)
        {
            StartTimer();
        }
        Log.Info("earpause", $"{what}: no bud in ear; resume if one is back within {ResumeWindow.TotalSeconds:0} s");
    }

    private void Forget(string reason)
    {
        lock (_lock)
        {
            StopTimer();
        }
        if (!_pending)
        {
            return;
        }
        _pending = false;
        wpn_media_forget();
        Log.Info("earpause", $"{reason}: paused sessions forgotten (no resume)");
    }

    // Under _lock.
    private void StartTimer()
    {
        StopTimer();
        int generation = _timerGeneration;
        _timer = new Timer(_ => OnWindowExpired(generation), null, ResumeWindow, Timeout.InfiniteTimeSpan);
    }

    // Under _lock. A callback already queued sees a newer generation and does nothing.
    private void StopTimer()
    {
        if (_timer is null)
        {
            return;
        }
        _timer.Dispose();
        _timer = null;
        _timerGeneration++;
    }

    private void OnWindowExpired(int generation)
    {
        lock (_lock)
        {
            if (generation != _timerGeneration)
            {
                return;
            }
            StopTimer();
            _forget = $"no bud back in ear within {ResumeWindow.TotalSeconds:0} s";
            Kick();
        }
    }

    private static bool CanAct(AapSession session, string what)
    {
        if (session.State.EarDetection != true)
        {
            Log.Info("earpause", $"{what}: skipped, ear detection is {(session.State.EarDetection is null ? "unknown" : "off")}");
            return false;
        }
        int output = DefaultOutputWatcher.wpn_audio_is_default_output(session.Address);
        if (output != 1)
        {
            Log.Info("earpause", $"{what}: skipped, AirPods are not the default output{(output < 0 ? $" (error 0x{output:X8})" : "")}");
            return false;
        }
        return true;
    }

    private static string Describe(int resumed) => resumed switch
    {
        > 0 => $"{resumed} session(s) resumed",
        0 => "nothing resumed (forgotten after other playback, something is playing, or no session still paused)",
        _ => $"error 0x{resumed:X8}",
    };

    [LibraryImport("wpods_native.dll")]
    private static partial int wpn_media_pause_all();

    [LibraryImport("wpods_native.dll")]
    private static partial int wpn_media_resume_paused();

    [LibraryImport("wpods_native.dll")]
    private static partial void wpn_media_forget();
}
