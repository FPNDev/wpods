using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using WPods.Core;
using Timer = System.Threading.Timer;

namespace WPods.Audio;

public enum MicStatus
{
    /// <summary>No app captures, or one captures while no AirPods are attached or Hands-Free has their mic.</summary>
    NotActive,
    /// <summary>An app captures and AirPods are attached; no START written yet in this session.</summary>
    Starting,
    /// <summary>A START was written or audio decoded; silence stays Live, as the AirPods send nothing while it is quiet.</summary>
    Live,
    /// <summary>The virtual mic is unavailable, the decoder failed to load, a START write failed or no audio came.</summary>
    Problem,
}

/// <summary>A stage <see cref="MicPipeline"/> loads: the effective mic mode's model, or the voice enhancement.</summary>
public enum MicSlot
{
    Mode,
    Enhancement,
}

/// <summary>Load state of the newest request of a <see cref="MicSlot"/>.</summary>
public enum StageLoad
{
    Loading,
    /// <summary>The stage (or none, for Off) runs, or waits for the next frame boundary to.</summary>
    Ready,
    /// <summary>Creation failed (or the session detached first); the previous stage keeps running.</summary>
    Failed,
}

/// <summary>
/// AirPods mic to virtual mic: streams 0x58 audio while an app captures or the Mixer plays it (in low latency mode all
/// the time, decoded only then). The session read loop only queues the audio packets; one real-time mic thread (MMCSS
/// Pro Audio, performance cores) decodes AAC-ELD and runs mode model, gain, enhancement and limiter in float before one
/// 16-bit conversion. Create before <see cref="ConnectionManager.Start"/>.
/// </summary>
public sealed class MicPipeline
{
    private const int HeaderLength = 22;
    private const int RecordHeaderLength = 5; // u32 LE timestamp + u8 AU length
    private const uint AuTicks = 180;
    private const int PcmCapacity = 2048;
    private const int StallAfterMs = 8000;
    private const int RestartEveryMs = 5000;
    // A START gets the AirPods' mic-on report within MicOnReportMs (seen 90-350 ms) unless they ignored it, then audio
    // within FirstAudioMs; it is resent otherwise, StartTries in all, then NoAudioGiveUpMs more before the mic is a problem.
    private const int MicOnReportMs = 400;
    private const int FirstAudioMs = 1000;
    private const int StartTries = 5;
    private const int NoAudioGiveUpMs = 5000;
    // Hands-Free mic check interval while the mic streams. The AirPods drop the link when Hands-Free starts while
    // their mic streams here, and they report Hands-Free call audio only after that.
    private const int HandsFreePollMs = 50;
    private const int MicNameWaitMs = 5000;
    // No START for this long after Hands-Free is enabled or starts being used: Windows lets it go and takes it again
    // within that time, and a START in between makes the AirPods drop the link.
    private const int HandsFreeHoldMs = 3000;
    private const int MaxConsecutiveErrors = 8;
    private const int LevelLogIntervalMs = 60_000;
    private const int TimingLogIntervalMs = 1000;
    // AirPods Pro 2 speech decodes at -33..-40 dBFS RMS, peaks -20..-26 dBFS; +12 dB puts it near -25 dBFS RMS
    // with peaks under the limiter knee. Applied after the mode model (loud speech would reach it above full scale).
    private const float MakeUpGain = 4f;
    // Audio queued longer than this is dropped, oldest first, so a stall does not add lasting delay.
    private const int MaxBacklogMs = 200;
    private static readonly long FrameBudgetTicks = Stopwatch.Frequency * Denoiser.FrameSize / 48_000;

    private static readonly byte[] StartMicrophone = [0x04, 0x00, 0x04, 0x00, 0x58, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x01, 0x82, 0x00, 0x00, 0x00, 0x04, 0x96, 0x00];
    // AACP_STOP_AUDIO.
    private static readonly byte[] StopMicrophone = [0x04, 0x00, 0x04, 0x00, 0x58, 0x00, 0x00, 0x00, 0x02, 0x00, 0x03, 0x01];

    private readonly ConnectionManager _connections;
    private readonly SettingsStore _settings;
    private readonly VirtualMic _mic;
    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private readonly Timer _handsFreePoll;
    // Falls back to the Windows name when the AirPods have not reported their own name soon after attach.
    private readonly Timer _micNameFallback;

    // Packets from the read loop to the mic thread; a Work with Done set is a detach reset.
    private readonly Lock _queueLock = new();
    private readonly Queue<Work> _queue = new();
    private readonly AutoResetEvent _queued = new(false);
    private int _droppedPackets;

    // Mic-thread state (one session at a time; reset on detach after its read loop ended).
    private readonly float[] _pcm = GC.AllocateArray<float>(PcmCapacity, pinned: true);
    private readonly short[] _output = GC.AllocateArray<short>(PcmCapacity, pinned: true);
    private AacDecoder? _decoder;
    // Written under _lock.
    private bool _nativeFailed;
    private int _fill;
    // Position on the AirPods' clock (48 kHz samples) of the next sample written, -1 until the first AU; and the AU
    // timestamp expected next (24 kHz units). Lost AUs move the position on, so the jitter buffer conceals them.
    private long _position = -1;
    private uint _nextTimestamp;
    private int _errors;
    private LevelMeter _inputMeter;
    private LevelMeter _outputMeter;
    private long _levelLogTick;
    private long _timingLogTick;
    private long _lastDecoderFlushLog;
    // Frames whose stages took longer than their 10 ms of audio, since the last timing check.
    private int _frames;
    private int _lateFrames;
    private long _slowestFrameTicks;

    // One stage per slot (see StageSlot for which thread owns what).
    private readonly StageSlot _model;
    private readonly StageSlot _enhancement;

    // Shared state; written under _lock.
    private AapSession? _session;
    // Name of the attached pair at attach (the Windows name unless the AirPods already reported theirs).
    private string _pairName = "";
    private bool _startSent;
    // A START write succeeded or audio was decoded in this session.
    private bool _started;
    // The latest START write failed.
    private bool _startFailed;
    // STARTs since the wait for audio began (BeginWaitLocked, or a stall re-send).
    private int _startTries;
    // No audio after StartTries STARTs, until audio comes or a new wait begins.
    private bool _noAudio;
    private volatile string? _faultCode;
    private long _lastStart;
    private long _lastAudio;
    // An app records the virtual mic (VirtualMic's state) or the Mixer plays this mic directly; either streams it.
    private volatile bool _capturing;
    private CaptureState _appCapture = CaptureState.Idle;
    private bool _directListener;
    // Low latency mode of the attached pair: the mic streams with nobody listening, so a recording starts at once.
    private bool _keepOn;
    // The AirPods' latest audio state report has their mic streaming.
    private bool _micOn;
    // Hands-Free has the AirPods mic during call audio (seen by the service or reported in 0x0E) or while an app records
    // from the Hands-Free mic. No START then: the AirPods' own mic stream running into it makes the controller fail.
    private bool _handsFreeAudio;
    private bool _reportedCallAudio;
    private bool _linkCallAudio;
    private bool _handsFreeMic;
    // Completed when both turn false or the session detaches.
    private TaskCompletionSource? _handsFreeEnded;
    // No START before this tick (HandsFreeHoldMs); kept across a reattach.
    private long _holdUntil;
    // Saved DisableHandsFree of the attached pair, to see Hands-Free being enabled.
    private bool _disableHandsFree;
    private volatile bool _flush;
    // Saved mic mode of the attached pair.
    private MicMode _mode;
    private volatile MicStatus _status;

    public MicPipeline(ConnectionManager connections)
    {
        _connections = connections;
        _settings = connections.Settings;
        _model = new StageSlot("mode model", _lock, (id, load, code) => ReportLocked(MicSlot.Mode, id, load, code));
        _enhancement = new StageSlot("enhancement", _lock, (id, load, code) => ReportLocked(MicSlot.Enhancement, id, load, code));
        _timer = new Timer(static s => ((MicPipeline)s!).OnTimer(), this, Timeout.Infinite, Timeout.Infinite);
        _handsFreePoll = new Timer(static s => ((MicPipeline)s!).OnHandsFreePoll(), this, Timeout.Infinite, Timeout.Infinite);
        _micNameFallback = new Timer(static s => ((MicPipeline)s!).SetMicDevice(allowPairName: true), this, Timeout.Infinite, Timeout.Infinite);
        _mic = new VirtualMic(OnCaptureChanged);
        new Thread(RunMicThread) { IsBackground = true, Name = "mic" }.Start();
        connections.Attached += OnAttached;
        connections.Detached += OnDetached;
        _settings.HostSettingsChanged += OnHostSettingsChanged;
        _settings.MicConfigChanged += OnMicConfigChanged;
        _mic.Open();
    }

    /// <summary>Raised on change, on the causing thread-pool thread under an internal lock; handlers must not block.</summary>
    public event Action<MicStatus>? StatusChanged;

    /// <summary><paramref name="position"/>: the block's first sample on the AirPods' clock, in 48 kHz samples.</summary>
    public delegate void PcmHandler(ReadOnlySpan<short> pcm, long position);

    /// <summary>An app started (true) or stopped capturing; raised under internal locks, handlers must not block.</summary>
    public event Action<bool>? CaptureChanged;

    /// <summary>Each block of mic PCM (mono 48 kHz) as written to the virtual mic; raised on the mic thread, must not block.</summary>
    public event PcmHandler? Written;

    /// <summary>
    /// Load state of a slot's newest request (pair address, 0 after detach; option id). The last argument is the failure
    /// code: "M4" model file missing, unreadable or damaged, "M0" anything else. Under the lock; must not block.
    /// </summary>
    public event Action<ulong, MicSlot, string?, StageLoad, string?>? StageChanged;

    /// <summary>
    /// A running stage threw and was dropped; its slot passes audio through until the next request. Raised on the
    /// mic thread under the internal lock.
    /// </summary>
    public event Action<ulong>? StageStopped;

    public MicStatus Status => _status;

    /// <summary>
    /// Problem cause: "M1" decoder or voice processor failed to load, "M2" START write failed, "M5" no audio after every
    /// START. Set before <see cref="StatusChanged"/> is raised.
    /// </summary>
    public string? FaultCode => _faultCode;

    public bool Capturing => _capturing;

    /// <summary>Call audio on the attached pair, or, while capturing, an app recording from its Hands-Free mic.</summary>
    public bool HandsFreeInUse
    {
        get
        {
            lock (_lock)
            {
                return _handsFreeAudio || _handsFreeMic;
            }
        }
    }

    /// <summary>Completes when Hands-Free is no longer in use or the session detaches.</summary>
    public Task HandsFreeEndedAsync()
    {
        lock (_lock)
        {
            if (!_handsFreeAudio && !_handsFreeMic)
            {
                return Task.CompletedTask;
            }
            _handsFreeEnded ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _handsFreeEnded.Task;
        }
    }

    /// <summary>Sends 0x58 STOP before an intentional detach: AirPods cut off mid-stream may reconnect on their own.</summary>
    public Task StopStreamAsync()
    {
        lock (_lock)
        {
            if (!_startSent || _session is not { } session)
            {
                return Task.CompletedTask;
            }
            return StopLocked(session, "before detach");
        }
    }

    private void OnAttached(AapSession session)
    {
        string pairName = _connections.GetDevices().Find(d => d.Address == session.Address)?.Name ?? "";
        lock (_lock)
        {
            _session = session;
            _pairName = pairName;
            _startSent = false;
            _started = false;
            _startFailed = false;
            _noAudio = false;
            _micOn = false;
            ResetHandsFreeLocked();
            HostSettings settings = _settings.Get(session.Address);
            _mode = settings.MicMode;
            _disableHandsFree = settings.DisableHandsFree;
            _keepOn = settings.LowLatency;
            MicConfig config = _settings.Mic;
            Log.Info("mic", $"session {Mac.Format(session.Address)} attached, mode={_mode} (runs {config.EffectiveMode(_mode)}), capturing={_capturing}, keep on={_keepOn}");
            RequestStagesLocked(config);
            UpdateStatusLocked("session attached");
            if (StreamingLocked)
            {
                _handsFreePoll.Change(0, Timeout.Infinite);
            }
        }
        session.Changed += OnSessionChanged;
        session.PacketReceived += OnPacket;
        session.CallAudioChanged += OnCallAudioChanged;
        // The service may have reported call audio before this subscription.
        OnCallAudioChanged(session.CallAudio);
        // The AirPods report their own name right after attach; sending the Windows name first would rename the mic
        // twice on every connect (and Windows gives a renamed mic a new endpoint).
        if (session.State.Name is null)
        {
            _micNameFallback.Change(MicNameWaitMs, Timeout.Infinite);
        }
        else
        {
            SetMicDevice(allowPairName: false);
        }
    }

    // Raised after the session's read loop ended, so no packet follows. Waits until the mic thread has processed the
    // queued packets and reset its state, so the next attach starts clean.
    private void OnDetached(AapSession session)
    {
        session.PacketReceived -= OnPacket;
        session.CallAudioChanged -= OnCallAudioChanged;
        session.Changed -= OnSessionChanged;
        lock (_lock)
        {
            if (_session == session)
            {
                _session = null;
                _keepOn = false;
                ResetHandsFreeLocked();
                UpdateStatusLocked("session detached");
            }
        }
        Log.Info("mic", $"session {Mac.Format(session.Address)} detached");
        using var done = new ManualResetEventSlim();
        Enqueue(new Work(null, 0, 0, done));
        done.Wait();
    }

    // Mic thread: OnDetached's reset.
    private void ResetMicThreadState()
    {
        _decoder?.Dispose();
        _decoder = null;
        _fill = 0;
        _position = -1;
        lock (_lock)
        {
            _nativeFailed = false;
            _model.ResetLocked();
            _enhancement.ResetLocked();
        }
    }

    private void OnSessionChanged(DeviceChange change)
    {
        if ((change & DeviceChange.Identity) != 0)
        {
            SetMicDevice(allowPairName: false);
        }
        if ((change & DeviceChange.Connection) == 0)
        {
            return;
        }
        lock (_lock)
        {
            if (StreamingLocked && !_startSent && !_handsFreeAudio && _session is { } session && IsReady(session))
            {
                TryStartLocked(session, "session ready");
            }
        }
    }

    // AAP-reported name, else (allowPairName) the Windows name. _lock is never held while calling into VirtualMic.
    private void SetMicDevice(bool allowPairName)
    {
        AapSession? session;
        string pairName;
        lock (_lock)
        {
            session = _session;
            pairName = _pairName;
        }
        if (session is null)
        {
            return;
        }
        if (session.State.Name is { } name)
        {
            _micNameFallback.Change(Timeout.Infinite, Timeout.Infinite);
            _mic.SetDevice(session.Address, name, reported: true);
        }
        else if (allowPairName)
        {
            _mic.SetDevice(session.Address, pairName, reported: false);
        }
    }

    private void OnHostSettingsChanged(ulong address, HostSettings settings)
    {
        lock (_lock)
        {
            if (_session?.Address != address)
            {
                return;
            }
            if (_mode != settings.MicMode)
            {
                _mode = settings.MicMode;
                Log.Info("mic", $"mode -> {settings.MicMode}");
                RequestStagesLocked(_settings.Mic);
            }
            if (_disableHandsFree && !settings.DisableHandsFree)
            {
                HoldLocked("Hands-Free enabled");
            }
            _disableHandsFree = settings.DisableHandsFree;
            if (_keepOn != settings.LowLatency)
            {
                _keepOn = settings.LowLatency;
                ApplyStreamingLocked(_keepOn ? "low latency on" : "low latency off");
            }
        }
    }

    private void OnMicConfigChanged(MicConfig config)
    {
        lock (_lock)
        {
            if (_session is not null)
            {
                RequestStagesLocked(config);
            }
        }
    }

    /// <summary>The Mixer plays this mic directly, not through the virtual mic: the AirPods mic streams meanwhile.</summary>
    public void SetDirectListener(bool on)
    {
        lock (_lock)
        {
            if (_directListener == on)
            {
                return;
            }
            _directListener = on;
            ApplyCaptureLocked(on ? "mixer plays the mic" : "mixer stopped playing the mic");
        }
    }

    // Called by VirtualMic under its lock; this lock is never held while calling into VirtualMic.
    private void OnCaptureChanged(CaptureState state)
    {
        lock (_lock)
        {
            _appCapture = state;
            ApplyCaptureLocked($"capture state {state}");
        }
    }

    private void ApplyCaptureLocked(string reason)
    {
        bool capturing = _appCapture == CaptureState.Capturing || _directListener;
        Log.Info("mic", $"{reason}: capturing={capturing}, session={(_session is { } s ? Mac.Format(s.Address) + " " + s.State.Status : "none")}, startSent={_startSent}");
        if (capturing != _capturing)
        {
            _capturing = capturing;
            // Windows puts a background tray app on efficiency cores, where the mode model's workers fall behind.
            Native.SetSpeedThrottling(!capturing);
            Native.SetPerformanceCoresOnly(capturing);
            CaptureChanged?.Invoke(capturing);
        }
        else if (capturing)
        {
            // Already streaming for the other listener.
            return;
        }
        if (!_capturing)
        {
            ApplyStreamingLocked("capture stopped");
            _faultCode = null;
            SetStatusLocked(_appCapture == CaptureState.Unavailable ? MicStatus.Problem : MicStatus.NotActive, reason);
            return;
        }
        _flush = true;
        if (_startSent)
        {
            // Streaming already (low latency mode): the timer checks at once that audio still comes.
            _startTries = 0;
            _noAudio = false;
            ArmLocked(0);
            UpdateStatusLocked("capture started");
        }
        else
        {
            BeginWaitLocked(Environment.TickCount64, "capture started");
        }
        ApplyStreamingLocked("capture started");
    }

    private bool StreamingLocked => _capturing || _keepOn;

    // The AirPods mic streams while someone listens or, in low latency mode, all the time.
    private void ApplyStreamingLocked(string reason)
    {
        if (!StreamingLocked)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _handsFreePoll.Change(Timeout.Infinite, Timeout.Infinite);
            _handsFreeMic = false;
            if (!_handsFreeAudio)
            {
                CompleteHandsFreeEndedLocked();
            }
            if (_startSent && _session is { } attached)
            {
                _ = StopLocked(attached, reason);
            }
            return;
        }
        if (_session is not { } session)
        {
            return;
        }
        _handsFreePoll.Change(0, Timeout.Infinite);
        if (!_startSent && !_handsFreeAudio && IsReady(session))
        {
            TryStartLocked(session, reason);
        }
    }

    // Sends a START held back by a Hands-Free hold and re-sends START on a stall. Silence never changes the status:
    // the AirPods send nothing while it is quiet.
    private void OnTimer()
    {
        lock (_lock)
        {
            if (!StreamingLocked || _handsFreeAudio || _handsFreeMic || _session is not { } session || !IsReady(session))
            {
                // Capture start, session ready and the end of Hands-Free use restart the wait.
                return;
            }
            long now = Environment.TickCount64;
            if (!_startSent)
            {
                TryStartLocked(session, "Hands-Free hold ended");
                return;
            }
            long lastAudio = Volatile.Read(ref _lastAudio);
            long silent = now - lastAudio;
            long sinceStart = now - _lastStart;
            if (lastAudio > _lastStart)
            {
                // Audio flowed since the last START: re-send only when it stalled.
                if (silent >= StallAfterMs && sinceStart >= RestartEveryMs)
                {
                    _startTries = 0;
                    SendStartLocked(session, now, $"stalled {silent} ms");
                    ArmLocked(FirstAudioMs);
                    return;
                }
                ArmLocked(Math.Max(StallAfterMs - silent, RestartEveryMs - sinceStart));
                return;
            }
            // The AirPods sometimes ignore a START (no mic-on report), so it is resent, StartTries in all, to avoid seconds
            // of initial silence. Nothing NoAudioGiveUpMs later is a mic problem until audio comes or capture restarts.
            if (_startTries < StartTries)
            {
                long wait = StartWaitMs;
                if (sinceStart >= wait)
                {
                    SendStartLocked(session, now, $"no audio {sinceStart} ms after START{(_micOn ? "" : ", mic not reported on")}");
                    sinceStart = 0;
                }
                ArmLocked(wait - sinceStart);
                return;
            }
            if (sinceStart < NoAudioGiveUpMs)
            {
                ArmLocked(NoAudioGiveUpMs - sinceStart);
                return;
            }
            Log.Warn("mic", $"no audio from the AirPods after {StartTries} STARTs, giving up until audio comes or capture starts again");
            _noAudio = true;
            UpdateStatusLocked("no audio from the AirPods");
        }
    }

    // Read loop: handles the audio state report and queues audio packets for the mic thread.
    private void OnPacket(ReadOnlySpan<byte> packet)
    {
        // Audio state: 04 00 04 00 0E 00, source address (6 bytes, zero when none), state bits.
        if (packet.Length == 13 && packet[0] == 0x04 && packet[2] == 0x04 && packet[4] == 0x0E)
        {
            bool callAudio = (packet[12] & 0x01) != 0;
            lock (_lock)
            {
                _micOn = (packet[12] & 0x20) != 0;
                if (callAudio != _reportedCallAudio && _session is not null)
                {
                    _reportedCallAudio = callAudio;
                    Log.Info("mic", $"Hands-Free call audio {(callAudio ? "started" : "ended")} (AirPods report)");
                    SetHandsFreeLocked(_reportedCallAudio || _linkCallAudio, _handsFreeMic, "Hands-Free call audio");
                }
            }
            return;
        }
        if (packet.Length < HeaderLength
            || packet[0] != 0x04 || packet[2] != 0x04 || packet[4] != 0x58 || packet[6] != 0x01 || packet[7] != 0x00)
        {
            return;
        }
        if (!_capturing)
        {
            // Low latency mode with nobody listening: not decoded, only seen arriving (for the stall check).
            Volatile.Write(ref _lastAudio, Environment.TickCount64);
            return;
        }
        byte[] data = ArrayPool<byte>.Shared.Rent(packet.Length);
        packet.CopyTo(data);
        Enqueue(new Work(data, packet.Length, Environment.TickCount64, null));
    }

    private void Enqueue(Work work)
    {
        lock (_queueLock)
        {
            _queue.Enqueue(work);
            // Behind real time: drop the oldest audio rather than play it ever later.
            while (_queue.Peek() is { Data: { } old } oldest && work.Tick - oldest.Tick > MaxBacklogMs)
            {
                _queue.Dequeue();
                ArrayPool<byte>.Shared.Return(old);
                _droppedPackets++;
            }
        }
        _queued.Set();
    }

    private void RunMicThread()
    {
        Native.MakeAudioThread();
        // Subnormals in the models' states take the slow path; this thread is the mic's alone, so FTZ/DAZ stays on.
        Native.wpn_fp_set_ftz_daz();
        while (true)
        {
            _queued.WaitOne();
            while (true)
            {
                Work work;
                lock (_queueLock)
                {
                    if (!_queue.TryDequeue(out work))
                    {
                        break;
                    }
                }
                if (work.Done is { } done)
                {
                    ResetMicThreadState();
                    done.Set();
                    continue;
                }
                try
                {
                    ProcessPacket(work.Data.AsSpan(0, work.Length), work.Tick);
                }
                catch (Exception e)
                {
                    Log.Error("mic", "audio packet failed", e);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(work.Data!);
                }
            }
        }
    }

    // Mic thread: one 0x58 audio packet, received at tick now.
    private void ProcessPacket(ReadOnlySpan<byte> packet, long now)
    {
        if (_flush)
        {
            _flush = false;
            _decoder?.Flush();
            _fill = 0;
            _position = -1;
            _errors = 0;
            _inputMeter = default;
            _outputMeter = default;
            _levelLogTick = _timingLogTick = Environment.TickCount64;
        }
        if (_decoder is null && !TryCreateNative())
        {
            return;
        }
        bool audio = false;
        int offset = HeaderLength;
        // Records: u32 timestamp, u8 length L, L bytes AU. A malformed record ends the packet.
        while (packet.Length - offset >= RecordHeaderLength)
        {
            int length = packet[offset + 4];
            int start = offset + RecordHeaderLength;
            if (length == 0 || start + length > packet.Length)
            {
                break;
            }
            TrackPosition(BinaryPrimitives.ReadUInt32LittleEndian(packet[offset..]));
            audio |= Decode(packet.Slice(start, length), now);
            offset = start + length;
        }
        if (!audio)
        {
            return;
        }
        Volatile.Write(ref _lastAudio, now);
        if (_status != MicStatus.Live)
        {
            lock (_lock)
            {
                if (_capturing && (!_started || _startFailed || _noAudio))
                {
                    if (_noAudio)
                    {
                        // The give-up stopped the timer: watch for stalls again.
                        ArmLocked(StallAfterMs);
                    }
                    _started = true;
                    _startFailed = false;
                    _noAudio = false;
                    UpdateStatusLocked("decoded audio");
                }
            }
        }
    }

    // The service saw call audio being set up (or up) on the attached pair's link, or end there.
    private void OnCallAudioChanged(bool on)
    {
        lock (_lock)
        {
            if (on == _linkCallAudio || _session is null)
            {
                return;
            }
            _linkCallAudio = on;
            Log.Info("mic", $"Hands-Free call audio {(on ? "starting" : "ended")} (call audio link)");
            SetHandsFreeLocked(_reportedCallAudio || _linkCallAudio, _handsFreeMic, "Hands-Free call audio link");
        }
    }

    // Timer thread, re-armed while the mic streams (one check at a time). The native check enumerates audio sessions,
    // so it runs outside the lock; a result for a pair that detached meanwhile is dropped.
    private void OnHandsFreePoll()
    {
        ulong address;
        lock (_lock)
        {
            if (!StreamingLocked || _session is not { } session)
            {
                return;
            }
            address = session.Address;
        }
        bool inUse = HandsFreeMicInUse(address);
        lock (_lock)
        {
            if (!StreamingLocked || _session?.Address != address)
            {
                return;
            }
            if (inUse != _handsFreeMic)
            {
                Log.Info("mic", $"Hands-Free mic {(inUse ? "opened" : "closed")} by an app");
                SetHandsFreeLocked(_handsFreeAudio, inUse, "Hands-Free mic");
            }
            _handsFreePoll.Change(HandsFreePollMs, Timeout.Infinite);
        }
    }

    // Enumerates audio sessions (a few ms); false while Hands-Free is disabled for the pair.
    private bool HandsFreeMicInUse(ulong address) =>
        !_settings.Get(address).DisableHandsFree && Native.wpn_bt_handsfree_mic_in_use(address) == 1;

    // Before every START: the poll may not have run yet (both mics opened together, or a capture restart cleared it),
    // and Windows retries call audio at once while an app still records from the Hands-Free mic.
    private void CheckHandsFreeMicLocked(AapSession session)
    {
        _handsFreeMic = HandsFreeMicInUse(session.Address);
        if (_handsFreeMic)
        {
            Log.Info("mic", "Hands-Free mic in use by an app, no START");
            UpdateStatusLocked("Hands-Free mic");
        }
    }

    // Hands-Free takes the AirPods mic: the stream stops while it is in use and restarts when it ends.
    private void SetHandsFreeLocked(bool audio, bool mic, string reason)
    {
        bool wasInUse = _handsFreeAudio || _handsFreeMic;
        _handsFreeAudio = audio;
        _handsFreeMic = mic;
        bool inUse = audio || mic;
        if (inUse == wasInUse || _session is not { } session)
        {
            return;
        }
        if (inUse)
        {
            HoldLocked(reason);
            UpdateStatusLocked(reason);
            return;
        }
        CompleteHandsFreeEndedLocked();
        if (StreamingLocked && !_startSent && IsReady(session))
        {
            TryStartLocked(session, $"{reason} ended");
        }
    }

    // Hands-Free is taking the mic: STOP now and no START for HandsFreeHoldMs (TryStartLocked).
    private void HoldLocked(string reason)
    {
        _holdUntil = Environment.TickCount64 + HandsFreeHoldMs;
        Log.Info("mic", $"no START for {HandsFreeHoldMs} ms ({reason})");
        if (_startSent && _session is { } session)
        {
            _ = StopLocked(session, reason);
            if (StreamingLocked && !_handsFreeAudio && !_handsFreeMic)
            {
                // Not in use, only enabled: the stream comes back when the hold ends (STOP stopped the timer).
                TryStartLocked(session, $"{reason}, hold ended");
            }
        }
    }

    // Sends START, or within a Hands-Free hold arms the timer to send it when the hold ends.
    private void TryStartLocked(AapSession session, string reason)
    {
        long now = Environment.TickCount64;
        if (now < _holdUntil)
        {
            UpdateStatusLocked(reason);
            ArmLocked(_holdUntil - now);
            return;
        }
        CheckHandsFreeMicLocked(session);
        if (_handsFreeMic)
        {
            return;
        }
        BeginWaitLocked(now, reason);
        SendStartLocked(session, now, reason);
    }

    private void ResetHandsFreeLocked()
    {
        _handsFreeAudio = false;
        _reportedCallAudio = false;
        _linkCallAudio = false;
        _handsFreeMic = false;
        CompleteHandsFreeEndedLocked();
    }

    private void CompleteHandsFreeEndedLocked()
    {
        _handsFreeEnded?.TrySetResult();
        _handsFreeEnded = null;
    }

    private bool TryCreateNative()
    {
        if (_nativeFailed)
        {
            return false;
        }
        try
        {
            _decoder = new AacDecoder();
            Log.Info("mic", "decoder created");
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            // Problem (M1) until the next session, which retries.
            Log.Error("mic", "decoder init failed", e);
            lock (_lock)
            {
                _nativeFailed = true;
                UpdateStatusLocked("decoder init failed");
            }
            return false;
        }
    }

    // AU timestamps count 24 kHz ticks, 180 per 7.5 ms AU; positions count 48 kHz samples. An AU later than expected
    // means the ones between were lost: the position skips them.
    private void TrackPosition(uint timestamp)
    {
        if (_position < 0)
        {
            _position = (long)timestamp * 2;
        }
        else
        {
            int lost = unchecked((int)(timestamp - _nextTimestamp));
            if (lost > 0)
            {
                _position += (long)lost * 2;
            }
        }
        _nextTimestamp = timestamp + AuTicks;
    }

    // A failed AU is skipped (counted as lost, so the position moves past it); repeated failures flush the decoder.
    private bool Decode(ReadOnlySpan<byte> au, long now)
    {
        int samples = _decoder!.Decode(au, _pcm.AsSpan(_fill));
        if (samples < 0)
        {
            _nextTimestamp -= AuTicks;
            if (++_errors >= MaxConsecutiveErrors)
            {
                _decoder.Flush();
                _errors = 0;
                if (now - _lastDecoderFlushLog >= 1000)
                {
                    _lastDecoderFlushLog = now;
                    Log.Warn("mic", $"{MaxConsecutiveErrors} AUs rejected in a row, decoder flushed");
                }
            }
            return false;
        }
        _errors = 0;
        if (samples == 0)
        {
            return false;
        }
        foreach (float sample in _pcm.AsSpan(_fill, samples))
        {
            _inputMeter.Add(sample);
        }
        _fill += samples;
        Emit(now);
        return true;
    }

    // Without stages everything is written at once; otherwise whole 480-sample frames are written.
    private void Emit(long now)
    {
        IMicStage? model = _model.TakeSwap();
        IMicStage? enhancement = _enhancement.TakeSwap();
        int ready = 0;
        if (model is null && enhancement is null)
        {
            ready = _fill;
            ApplyGain(_pcm.AsSpan(0, ready));
        }
        else
        {
            for (; _fill - ready >= Denoiser.FrameSize; ready += Denoiser.FrameSize)
            {
                Span<float> frame = _pcm.AsSpan(ready, Denoiser.FrameSize);
                long start = Stopwatch.GetTimestamp();
                model = RunStage(_model, model, frame);
                ApplyGain(frame);
                enhancement = RunStage(_enhancement, enhancement, frame);
                long took = Stopwatch.GetTimestamp() - start;
                _frames++;
                if (took > FrameBudgetTicks)
                {
                    _lateFrames++;
                }
                _slowestFrameTicks = Math.Max(_slowestFrameTicks, took);
            }
            if (ready == 0)
            {
                return;
            }
        }
        for (int i = 0; i < ready; i++)
        {
            short sample = SoftLimiter.ToPcm16(_pcm[i]);
            _output[i] = sample;
            _outputMeter.Add(sample / 32768f);
        }
        _mic.Write(_output.AsSpan(0, ready), _position);
        Written?.Invoke(_output.AsSpan(0, ready), _position);
        _position += ready;
        _fill -= ready;
        _pcm.AsSpan(ready, _fill).CopyTo(_pcm);
        if (now - _levelLogTick >= LevelLogIntervalMs)
        {
            _levelLogTick = now;
            Log.Info("mic", $"level in {_inputMeter.Take()} | out {_outputMeter.Take()} | model {_model.ActiveOption?.Id ?? "none"}, enhancement {_enhancement.ActiveOption?.Id ?? "off"}, gain {MakeUpGain:0.00}");
        }
        if (now - _timingLogTick >= TimingLogIntervalMs)
        {
            _timingLogTick = now;
            LogTiming();
        }
    }

    // Warns at most once a second when frames ran over their real time or queued audio had to be dropped.
    private void LogTiming()
    {
        int dropped;
        lock (_queueLock)
        {
            dropped = _droppedPackets;
            _droppedPackets = 0;
        }
        if (_lateFrames > 0 || dropped > 0)
        {
            double slowestMs = _slowestFrameTicks * 1000.0 / Stopwatch.Frequency;
            Log.Warn("mic", $"behind real time: {_lateFrames} of {_frames} frames over 10 ms (slowest {slowestMs:0.0} ms), {dropped} packets dropped");
        }
        _frames = 0;
        _lateFrames = 0;
        _slowestFrameTicks = 0;
    }

    private static void ApplyGain(Span<float> samples)
    {
        foreach (ref float sample in samples)
        {
            sample *= MakeUpGain;
        }
    }

    // Mic thread. A stage that throws is dropped and the frame passes through as it is; returns the stage to keep running.
    private IMicStage? RunStage(StageSlot slot, IMicStage? stage, Span<float> frame)
    {
        if (stage is null)
        {
            return null;
        }
        try
        {
            stage.Process(frame);
            return stage;
        }
        catch (Exception e)
        {
            string? id = slot.FailActive(e);
            lock (_lock)
            {
                slot.ForgetLocked(id);
                StageStopped?.Invoke(_session?.Address ?? 0);
            }
            return null;
        }
    }

    // The config holds only listed ids (checked on load and by the page commands).
    private void RequestStagesLocked(MicConfig config)
    {
        MicMode mode = config.EffectiveMode(_mode);
        _model.RequestLocked(MicCatalog.FindModel(mode, config.Models[mode]));
        _enhancement.RequestLocked(config.Enhancement is { } id ? MicCatalog.FindEnhancement(id) : null);
    }

    private void ReportLocked(MicSlot slot, string? id, StageLoad load, string? code) => StageChanged?.Invoke(_session?.Address ?? 0, slot, id, load, code);

    private void BeginWaitLocked(long now, string reason)
    {
        _startTries = 0;
        _noAudio = false;
        Volatile.Write(ref _lastAudio, now);
        ArmLocked(StartWaitMs);
        UpdateStatusLocked(reason);
    }

    private void SendStartLocked(AapSession session, long now, string reason)
    {
        Log.Info("mic", $"START sent to {Mac.Format(session.Address)} ({reason})");
        _startTries++;
        _startSent = true;
        _lastStart = now;
        _ = SendStartAsync(session);
    }

    // A write skipped or cancelled because the session is closing is a detach, not a failure. A real failure is
    // Problem (M2) until a later START write succeeds or audio is decoded; the stall re-send retries.
    private async Task SendStartAsync(AapSession session)
    {
        bool ok = await session.WriteAsync(StartMicrophone).ConfigureAwait(false);
        if (!ok)
        {
            Log.Warn("mic", "START write failed");
        }
        lock (_lock)
        {
            if (!_startSent || _session != session || (!ok &&(_connections.Session != session || session.Completion.IsCompleted)))
            {
                return;
            }
            if (ok)
            {
                _started = true;
            }
            _startFailed = !ok;
            UpdateStatusLocked(ok ? "START written" : "START write failed");
        }
    }

    // No status change: a detach after a Disconnect makes it NotActive; a later capture start sends START again.
    // Stops the stall timer so no START re-send follows the STOP.
    private Task StopLocked(AapSession session, string reason)
    {
        _startSent = false;
        _started = false;
        _startFailed = false;
        _noAudio = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        Log.Info("mic", $"STOP sent to {Mac.Format(session.Address)} ({reason})");
        return WriteStopAsync(session);
    }

    private static async Task WriteStopAsync(AapSession session)
    {
        if (!await session.WriteAsync(StopMicrophone).ConfigureAwait(false))
        {
            Log.Warn("mic", "STOP write failed");
        }
    }

    private void UpdateStatusLocked(string reason)
    {
        if (!_capturing)
        {
            return;
        }
        bool off = _session is null || _handsFreeAudio || _handsFreeMic;
        string? fault = off ? null : _nativeFailed ? "M1" : _startFailed ? "M2" : _noAudio ? "M5" : null;
        _faultCode = fault;
        SetStatusLocked(off ? MicStatus.NotActive : fault is not null ? MicStatus.Problem : _started ? MicStatus.Live : MicStatus.Starting, reason);
    }

    // How long a START may go without audio before it is resent.
    private long StartWaitMs => _micOn ? FirstAudioMs : MicOnReportMs;

    private void ArmLocked(long dueMs) => _timer.Change(Math.Max(dueMs, 1), Timeout.Infinite);

    private void SetStatusLocked(MicStatus status, string reason)
    {
        if (_status == status)
        {
            return;
        }
        Log.Info("mic", $"status {_status} -> {status} ({reason})");
        _status = status;
        StatusChanged?.Invoke(status);
    }

    private static bool IsReady(AapSession session) => session.State.Status == SessionStatus.Ready;

    /// <summary>
    /// One slot's stage: created on the thread pool (model loads take up to a second) and swapped in by the mic thread
    /// at a frame boundary. *Locked methods run under the pipeline lock, TakeSwap on the mic thread.
    /// </summary>
    private sealed class StageSlot(string kind, Lock gate, Action<string?, StageLoad, string?> report)
    {
        // Under the pipeline lock.
        private string? _requested;
        private string? _published;
        private int _generation;
        // Set under the pipeline lock, taken by the mic thread.
        private Swap? _pending;
        // Mic thread.
        private IMicStage? _active;

        /// <summary>The option whose stage runs now (mic thread).</summary>
        public MicOption? ActiveOption { get; private set; }

        /// <summary>Starts creating the stage unless already requested; null = none, and "raw" has no stage.</summary>
        public void RequestLocked(MicOption? option)
        {
            string? id = option?.Id;
            if (id == _requested)
            {
                return;
            }
            _requested = id;
            int generation = ++_generation;
            if (option?.Create is null)
            {
                DisposeLater(PublishLocked(new Swap(option, null)));
                report(id, StageLoad.Ready, null);
                return;
            }
            Log.Info("mic", $"{kind} {id} loading");
            report(id, StageLoad.Loading, null);
            _ = Task.Run(() => Create(option, generation));
        }

        /// <summary>Detach (read loop ended): disposes the active and pending stages, drops creations in flight.</summary>
        public void ResetLocked()
        {
            if (_requested != _published)
            {
                Log.Info("mic", $"{kind} {_requested} load dropped (detached)");
                report(_requested, StageLoad.Failed, null);
            }
            _generation++;
            _requested = null;
            _published = null;
            DisposeLater(Interlocked.Exchange(ref _pending, null)?.Stage);
            DisposeLater(_active);
            _active = null;
            ActiveOption = null;
        }

        /// <summary>Mic thread, at a frame boundary: applies a pending swap; returns the stage to run (null = none).</summary>
        public IMicStage? TakeSwap()
        {
            if (Volatile.Read(ref _pending) is null || Interlocked.Exchange(ref _pending, null) is not { } swap)
            {
                return _active;
            }
            DisposeLater(_active);
            _active = swap.Stage;
            ActiveOption = swap.Option;
            Log.Info("mic", $"{kind} -> {swap.Option?.Id ?? "off"}");
            return _active;
        }

        /// <summary>Mic thread: the active stage threw; disposes it and runs none until the next swap. Returns its option id.</summary>
        public string? FailActive(Exception e)
        {
            string? id = ActiveOption?.Id;
            Log.Error("mic", $"{kind} {id} failed while running, dropped", e);
            DisposeLater(_active);
            _active = null;
            ActiveOption = null;
            return id;
        }

        /// <summary>After <see cref="FailActive"/>: a later request for the dropped option creates it again.</summary>
        public void ForgetLocked(string? id)
        {
            if (_requested == id)
            {
                _requested = null;
            }
            if (_published == id)
            {
                _published = null;
            }
        }

        private void Create(MicOption option, int generation)
        {
            IMicStage stage;
            long start = Environment.TickCount64;
            try
            {
                stage = option.Create!();
            }
            catch (Exception e)
            {
                lock (gate)
                {
                    Log.Error("mic", $"{kind} {option.Id} could not be created, keeping {_published ?? "off"}", e);
                    if (generation == _generation)
                    {
                        _requested = _published;
                        bool file = e is IOException or UnauthorizedAccessException or InvalidDataException;
                        report(option.Id, StageLoad.Failed, file ? "M4" : "M0");
                    }
                }
                return;
            }
            Log.Info("mic", $"{kind} {option.Id} created in {Environment.TickCount64 - start} ms");
            IMicStage? unused;
            lock (gate)
            {
                if (generation == _generation)
                {
                    unused = PublishLocked(new Swap(option, stage));
                    report(option.Id, StageLoad.Ready, null);
                }
                else
                {
                    unused = stage;
                }
            }
            unused?.Dispose();
        }

        // Returns the stage of a pending swap the mic thread never took, for the caller to dispose.
        private IMicStage? PublishLocked(Swap swap)
        {
            _published = swap.Option?.Id;
            return Interlocked.Exchange(ref _pending, swap)?.Stage;
        }

        private static void DisposeLater(IMicStage? stage)
        {
            if (stage is not null)
            {
                _ = Task.Run(stage.Dispose);
            }
        }
    }

    private sealed record Swap(MicOption? Option, IMicStage? Stage);

    // An audio packet (pooled Data, Length bytes, received at Tick), or a detach reset when Done is set.
    private readonly record struct Work(byte[]? Data, int Length, long Tick, ManualResetEventSlim? Done);

    /// <summary>RMS and peak of the samples added since the last <see cref="Take"/>, in dBFS (full scale = 1).</summary>
    private struct LevelMeter
    {
        private double _sum;
        private float _peak;
        private int _count;

        public void Add(float sample)
        {
            _sum += sample * sample;
            _peak = MathF.Max(_peak, MathF.Abs(sample));
            _count++;
        }

        public string Take()
        {
            string text = _count == 0 ? "no samples" : $"rms {Dbfs(Math.Sqrt(_sum / _count)):0.0} dBFS, peak {Dbfs(_peak):0.0} dBFS";
            this = default;
            return text;
        }

        // Silence floors at -120 dBFS instead of -infinity.
        private static double Dbfs(double value) => 20 * Math.Log10(Math.Max(value, 1e-6));
    }
}
