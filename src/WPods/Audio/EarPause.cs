using System.Runtime.InteropServices;
using WPods.Core;

namespace WPods.Audio;

/// <summary>
/// Automatic ear detection pause/resume on the host, Apple rules (support.apple.com HT207009: "pause and resume
/// playback when you remove one of your AirPods, or stop playback - and won't resume - when you remove both"),
/// driven by the number of buds in ear:
/// - 2 -> 1: pause the current Windows media session if it is playing and remember that WPods paused it;
/// - back to 2: resume only if WPods paused it;
/// - any -> 0: pause if playing and forget the resume (no auto-resume); this also covers one-bud use (1 -> 0);
/// - more buds in ear otherwise (0 -> 1, 1 -> 2 without a WPods pause): nothing.
/// Acts only while the attached pair's Ear detection setting is on and the AirPods are the default Windows render
/// endpoint (wpods_native wpn_audio_is_default_output: the default console render endpoint's container ID equals the
/// pair's Bluetooth devnode container ID); a skipped resume also forgets it. The media calls are synchronous WinRT
/// calls, so the work runs on a thread-pool worker; the latest in-ear count wins (collapsing quick changes gives the
/// same end state). Create before <see cref="ConnectionManager.Start"/>.
/// </summary>
public sealed partial class EarPause
{
    private const int Unknown = -1;
    private const int Both = 2;

    private readonly Lock _lock = new();
    // Under _lock.
    private AapSession? _session;
    private int _latest = Unknown;
    private bool _running;
    private bool _reset;
    // Worker only (at most one worker runs, guarded by _running).
    private int _applied = Unknown;
    private bool _wePaused;

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
            _reset = true;
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
            }
        }
    }

    private void OnChanged(DeviceChange change)
    {
        if ((change & DeviceChange.Ear) == 0)
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
            _latest = (state.PrimaryEar == EarStatus.InEar ? 1 : 0) + (state.SecondaryEar == EarStatus.InEar ? 1 : 0);
            if (!_running)
            {
                _running = true;
                ThreadPool.UnsafeQueueUserWorkItem(static p => p.Run(), this, preferLocal: false);
            }
        }
    }

    private void Run()
    {
        while (true)
        {
            AapSession session;
            int count;
            lock (_lock)
            {
                if (_reset)
                {
                    _reset = false;
                    if (_wePaused)
                    {
                        Log.Info("earpause", "session changed: resume forgotten");
                    }
                    _applied = Unknown;
                    _wePaused = false;
                }
                if (_session is null || _latest == _applied)
                {
                    _running = false;
                    return;
                }
                session = _session;
                count = _latest;
            }
            Apply(session, _applied, count);
            _applied = count;
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
        if (count == Both)
        {
            if (!_wePaused)
            {
                Log.Info("earpause", $"{what}: no resume (WPods did not pause)");
                return;
            }
            _wePaused = false;
            if (CanAct(session, what))
            {
                int result = wpn_media_play();
                Log.Info("earpause", $"{what}: resume -> {Describe(result)}");
            }
            return;
        }
        if (count > previous)
        {
            Log.Info("earpause", $"{what}: no action");
            return;
        }
        // Fewer buds in ear: 2 -> 1 may resume later; reaching 0 never does.
        bool resumable = previous == Both && count == 1;
        if (_wePaused && !resumable)
        {
            _wePaused = false;
            Log.Info("earpause", $"{what}: resume forgotten");
        }
        if (!CanAct(session, what))
        {
            return;
        }
        int paused = wpn_media_pause_if_playing();
        _wePaused = paused == 1 && resumable;
        Log.Info("earpause", $"{what}: pause -> {Describe(paused)}{(paused == 1 ? resumable ? ", resume armed" : ", no auto-resume" : "")}");
    }

    private static bool CanAct(AapSession session, string what)
    {
        if (session.State.EarDetection != true)
        {
            Log.Info("earpause", $"{what}: skipped, ear detection is {(session.State.EarDetection is null ? "unknown" : "off")}");
            return false;
        }
        int output = wpn_audio_is_default_output(session.Address);
        if (output != 1)
        {
            Log.Info("earpause", $"{what}: skipped, AirPods are not the default output{(output < 0 ? $" (error 0x{output:X8})" : "")}");
            return false;
        }
        return true;
    }

    private static string Describe(int result) => result switch
    {
        1 => "done",
        0 => "nothing to do",
        _ => $"error 0x{result:X8}",
    };

    [LibraryImport("wpods_native.dll")]
    private static partial int wpn_media_pause_if_playing();

    [LibraryImport("wpods_native.dll")]
    private static partial int wpn_media_play();

    [LibraryImport("wpods_native.dll")]
    private static partial int wpn_audio_is_default_output(ulong btAddress);
}
