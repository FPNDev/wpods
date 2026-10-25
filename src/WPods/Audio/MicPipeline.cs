using WPods.Core;
using Timer = System.Threading.Timer;

namespace WPods.Audio;

public enum MicStatus
{
    /// <summary>No app captures from "Headset (A2DP)", or one captures while no AirPods are attached.</summary>
    NotActive,
    /// <summary>An app captures and AirPods are attached; no START written yet in this session.</summary>
    Starting,
    /// <summary>
    /// An app captures and a START was written (or audio decoded) in this session. Silence stays Live: the AirPods
    /// send nothing while it is quiet.
    /// </summary>
    Live,
    /// <summary>
    /// A software fault: the virtual mic device cannot be opened or read, the decoder failed to load, or a START
    /// write failed.
    /// </summary>
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
/// AirPods mic -> \\.\NTA2DPVM. Starts the AirPods mic stream when an app starts capturing and stops it (0x58 STOP)
/// when capture stops and before a user Disconnect or exit (<see cref="StopStreamAsync"/>); 0x58 packets are
/// dropped unread while nothing captures. Decodes on the session read loop, in float
/// until the end: AAC-ELD -> 48 kHz mono float -> make-up gain -> model of the effective mic mode
/// (<see cref="MicConfig.EffectiveMode"/>) -> voice enhancement -> soft limiter + the only 16-bit conversion ->
/// WriteFile. Stages (<see cref="MicCatalog"/>) are created on the thread pool on attach, on the attached pair's mic mode
/// change and on a <see cref="MicConfig"/> change, and swapped in at a frame boundary; the replaced one is disposed on
/// the thread pool. While capturing it logs the input (decoded, before gain) and output
/// levels once per second.
/// Create before <see cref="ConnectionManager.Start"/>.
/// </summary>
public sealed class MicPipeline
{
    private const int HeaderLength = 22;
    private const int RecordHeaderLength = 5; // u32 LE timestamp + u8 AU length
    private const int PcmCapacity = 2048;
    private const int StallAfterMs = 8000;
    private const int RestartEveryMs = 5000;
    private const int MaxConsecutiveErrors = 8;
    private const int LevelLogIntervalMs = 1000;
    // Applied to the decoded signal before mode processing. Measured on AirPods Pro 2: normal speech
    // -33..-40 dBFS RMS, peaks -20..-26 dBFS; +12 dB puts speech near -25 dBFS RMS, peaks under the limiter knee.
    private const float MakeUpGain = 4f;

    private static readonly byte[] StartMicrophone = [0x04, 0x00, 0x04, 0x00, 0x58, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x01, 0x82, 0x00, 0x00, 0x00, 0x04, 0x96, 0x00];
    // From LibrePods (AACP_STOP_AUDIO).
    private static readonly byte[] StopMicrophone = [0x04, 0x00, 0x04, 0x00, 0x58, 0x00, 0x00, 0x00, 0x02, 0x00, 0x03, 0x01];

    private readonly ConnectionManager _connections;
    private readonly SettingsStore _settings;
    private readonly VirtualMic _mic;
    private readonly Lock _lock = new();
    private readonly Timer _timer;

    // Read-loop state (one session at a time; reset on detach after its read loop ended).
    private readonly float[] _pcm = GC.AllocateArray<float>(PcmCapacity, pinned: true);
    private readonly short[] _output = GC.AllocateArray<short>(PcmCapacity, pinned: true);
    private AacDecoder? _decoder;
    // Written under _lock.
    private bool _nativeFailed;
    private int _fill;
    private int _errors;
    private LevelMeter _inputMeter;
    private LevelMeter _outputMeter;
    private long _levelLogTick;
    private long _lastDecoderFlushLog;

    // One stage per slot (see StageSlot for which thread owns what).
    private readonly StageSlot _model;
    private readonly StageSlot _enhancement;

    // Shared state; written under _lock.
    private AapSession? _session;
    private bool _startSent;
    // A START write succeeded or audio was decoded in this session.
    private bool _started;
    // The latest START write failed.
    private bool _startFailed;
    private volatile string? _faultCode;
    private long _lastStart;
    private long _lastAudio;
    private volatile bool _capturing;
    private volatile bool _flush;
    // Saved mic mode of the attached pair.
    private MicMode _mode;
    private volatile MicStatus _status;

    public MicPipeline(ConnectionManager connections)
    {
        _connections = connections;
        _settings = connections.Settings;
        _model = new StageSlot("mode model", (id, load, code) => ReportLocked(MicSlot.Mode, id, load, code));
        _enhancement = new StageSlot("enhancement", (id, load, code) => ReportLocked(MicSlot.Enhancement, id, load, code));
        _timer = new Timer(static s => ((MicPipeline)s!).OnTimer(), this, Timeout.Infinite, Timeout.Infinite);
        _mic = new VirtualMic(OnCaptureChanged);
        connections.Attached += OnAttached;
        connections.Detached += OnDetached;
        _settings.HostSettingsChanged += OnHostSettingsChanged;
        _settings.MicConfigChanged += OnMicConfigChanged;
        _mic.Open();
    }

    /// <summary>
    /// Raised only on change, on the thread-pool thread that caused it (read loop, timer, capture-state wait or
    /// session event), under an internal lock: handlers must not block (marshal with BeginInvoke).
    /// </summary>
    public event Action<MicStatus>? StatusChanged;

    /// <summary>
    /// True when an app starts capturing from the virtual mic, false when it stops. Raised only on change, on the
    /// capture-state wait thread under internal locks: handlers must not block.
    /// </summary>
    public event Action<bool>? CaptureChanged;

    /// <summary>
    /// Load state of a slot's newest request (address of the attached pair, 0 after detach; option id or null = none).
    /// Raised in request order under the internal lock, on the thread that caused it: handlers must not block.
    /// The last argument is why a load failed: "M4" the model file is missing, unreadable or damaged, "M0" anything
    /// else; null otherwise (and for a load dropped by a detach).
    /// </summary>
    public event Action<ulong, MicSlot, string?, StageLoad, string?>? StageChanged;

    /// <summary>
    /// A running stage threw and was dropped (address of the attached pair); its slot passes audio through until the
    /// next request. Raised on the read loop under the internal lock: handlers must not block.
    /// </summary>
    public event Action<ulong>? StageStopped;

    public MicStatus Status => _status;

    /// <summary>
    /// Why the status is <see cref="MicStatus.Problem"/> while an app captures: "M1" the decoder or voice processor
    /// failed to load, "M2" a START write failed; null otherwise. Set before <see cref="StatusChanged"/> is raised, so
    /// read it from that handler.
    /// </summary>
    public string? FaultCode => _faultCode;

    /// <summary>An app captures from the virtual mic.</summary>
    public bool Capturing => _capturing;

    /// <summary>
    /// Stops the AirPods mic stream (0x58 STOP) when a START was sent in the attached session. Completes when the
    /// STOP was written, or at once when there is nothing to stop. Call before an intentional detach (user
    /// Disconnect, exit): AirPods cut off mid-stream may reconnect on their own.
    /// </summary>
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
        lock (_lock)
        {
            _session = session;
            _startSent = false;
            _started = false;
            _startFailed = false;
            _mode = _settings.Get(session.Address).MicMode;
            MicConfig config = _settings.Mic;
            Log.Info("mic", $"session {Mac.Format(session.Address)} attached, mode={_mode} (runs {config.EffectiveMode(_mode)}), capturing={_capturing}");
            RequestStagesLocked(config);
            UpdateStatusLocked("session attached");
        }
        session.Changed += OnSessionChanged;
        session.PacketReceived += OnPacket;
    }

    // Raised after the session's read loop ended, so the read-loop state is free.
    private void OnDetached(AapSession session)
    {
        session.PacketReceived -= OnPacket;
        session.Changed -= OnSessionChanged;
        lock (_lock)
        {
            if (_session == session)
            {
                _session = null;
                UpdateStatusLocked("session detached");
            }
        }
        Log.Info("mic", $"session {Mac.Format(session.Address)} detached");
        _decoder?.Dispose();
        _decoder = null;
        _fill = 0;
        lock (_lock)
        {
            _nativeFailed = false;
            _model.ResetLocked();
            _enhancement.ResetLocked();
        }
    }

    private void OnSessionChanged(DeviceChange change)
    {
        if ((change & DeviceChange.Connection) == 0)
        {
            return;
        }
        lock (_lock)
        {
            if (_capturing && !_startSent && _session is { } session && IsReady(session))
            {
                long now = Environment.TickCount64;
                BeginWaitLocked(now, "session ready while capturing");
                SendStartLocked(session, now, "session ready");
            }
        }
    }

    private void OnHostSettingsChanged(ulong address, HostSettings settings)
    {
        lock (_lock)
        {
            if (_session?.Address == address && _mode != settings.MicMode)
            {
                _mode = settings.MicMode;
                Log.Info("mic", $"mode -> {settings.MicMode}");
                RequestStagesLocked(_settings.Mic);
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

    // Called by VirtualMic under its lock; this lock is never held while calling into VirtualMic.
    private void OnCaptureChanged(CaptureState state)
    {
        lock (_lock)
        {
            Log.Info("mic", $"capture state {state}, session={(_session is { } s ? Mac.Format(s.Address) + " " + s.State.Status : "none")}, startSent={_startSent}");
            bool capturing = state == CaptureState.Capturing;
            if (capturing != _capturing)
            {
                _capturing = capturing;
                CaptureChanged?.Invoke(capturing);
            }
            if (!_capturing)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                if (_startSent && _session is { } attached)
                {
                    _ = StopLocked(attached, "capture stopped");
                }
                _faultCode = null;
                SetStatusLocked(state == CaptureState.Unavailable ? MicStatus.Problem : MicStatus.NotActive, $"capture state {state}");
                return;
            }
            _flush = true;
            long now = Environment.TickCount64;
            BeginWaitLocked(now, "app started capturing");
            if (!_startSent && _session is { } session && IsReady(session))
            {
                SendStartLocked(session, now, "capture started");
            }
        }
    }

    // Stall re-send of START only. Silence never changes the status: the AirPods send nothing while it is quiet.
    // Re-armed for the next deadline, so it fires about every 8 s while audio flows.
    private void OnTimer()
    {
        lock (_lock)
        {
            if (!_capturing || _session is not { } session || !IsReady(session))
            {
                // Capture start and session ready restart the wait.
                return;
            }
            long now = Environment.TickCount64;
            long silent = now - Volatile.Read(ref _lastAudio);
            long sinceStart = now - _lastStart;
            if (silent >= StallAfterMs && sinceStart >= RestartEveryMs)
            {
                SendStartLocked(session, now, $"stalled {silent} ms");
                sinceStart = 0;
            }
            ArmLocked(Math.Max(StallAfterMs - silent, RestartEveryMs - sinceStart));
        }
    }

    private void OnPacket(ReadOnlySpan<byte> packet)
    {
        if (!_capturing
            || packet.Length < HeaderLength
            || packet[0] != 0x04 || packet[2] != 0x04 || packet[4] != 0x58 || packet[6] != 0x01 || packet[7] != 0x00
            || !_mic.IsOpen)
        {
            return;
        }
        if (_flush)
        {
            _flush = false;
            _decoder?.Flush();
            _fill = 0;
            _errors = 0;
            _inputMeter = default;
            _outputMeter = default;
            _levelLogTick = Environment.TickCount64;
        }
        if (_decoder is null && !TryCreateNative())
        {
            return;
        }
        long now = Environment.TickCount64;
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
                if (_capturing && (!_started || _startFailed))
                {
                    _started = true;
                    _startFailed = false;
                    UpdateStatusLocked("decoded audio");
                }
            }
        }
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
            _decoder?.Dispose();
            _decoder = null;
            lock (_lock)
            {
                _nativeFailed = true;
                UpdateStatusLocked("decoder init failed");
            }
            return false;
        }
    }

    // A failed AU is skipped; repeated failures flush the decoder.
    private bool Decode(ReadOnlySpan<byte> au, long now)
    {
        int samples = _decoder!.Decode(au, _pcm.AsSpan(_fill));
        if (samples < 0)
        {
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
        foreach (ref float sample in _pcm.AsSpan(_fill, samples))
        {
            _inputMeter.Add(sample);
            sample *= MakeUpGain;
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
        }
        else
        {
            // Subnormals in the models' states take the slow path. The read loop runs on pool threads, so FTZ/DAZ is
            // set only around the stages and the previous MXCSR is restored.
            uint mxcsr = Native.wpn_fp_set_ftz_daz();
            try
            {
                for (; _fill - ready >= Denoiser.FrameSize; ready += Denoiser.FrameSize)
                {
                    Span<float> frame = _pcm.AsSpan(ready, Denoiser.FrameSize);
                    model = RunStage(_model, model, frame);
                    enhancement = RunStage(_enhancement, enhancement, frame);
                }
            }
            finally
            {
                Native.wpn_fp_restore(mxcsr);
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
        _mic.Write(_output.AsSpan(0, ready));
        _fill -= ready;
        _pcm.AsSpan(ready, _fill).CopyTo(_pcm);
        if (now - _levelLogTick >= LevelLogIntervalMs)
        {
            _levelLogTick = now;
            Log.Info("mic", $"level in {_inputMeter.Take()} | out {_outputMeter.Take()} | model {_model.ActiveOption?.Id ?? "none"}, enhancement {_enhancement.ActiveOption?.Id ?? "off"}, gain {MakeUpGain:0.00}");
        }
    }

    // Read loop. A stage that throws is dropped and the frame passes through as it is; returns the stage to keep running.
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
        _model.RequestLocked(MicCatalog.FindModel(mode, config.Models[mode]), _lock);
        _enhancement.RequestLocked(config.Enhancement is { } id ? MicCatalog.FindEnhancement(id) : null, _lock);
    }

    private void ReportLocked(MicSlot slot, string? id, StageLoad load, string? code) => StageChanged?.Invoke(_session?.Address ?? 0, slot, id, load, code);

    private void BeginWaitLocked(long now, string reason)
    {
        Volatile.Write(ref _lastAudio, now);
        ArmLocked(StallAfterMs);
        UpdateStatusLocked(reason);
    }

    private void SendStartLocked(AapSession session, long now, string reason)
    {
        Log.Info("mic", $"START sent to {Mac.Format(session.Address)} ({reason})");
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

    // While capturing: no session -> NotActive; a fault (M1 wins over M2) -> Problem; START written or audio
    // decoded -> Live; else Starting. While not capturing only OnCaptureChanged sets the status.
    private void UpdateStatusLocked(string reason)
    {
        if (!_capturing)
        {
            return;
        }
        string? fault = _session is null ? null : _nativeFailed ? "M1" : _startFailed ? "M2" : null;
        _faultCode = fault;
        SetStatusLocked(_session is null ? MicStatus.NotActive : fault is not null ? MicStatus.Problem : _started ? MicStatus.Live : MicStatus.Starting, reason);
    }

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
    /// One slot's stage. A request creates the new stage on the thread pool (model loads take up to
    /// about a second) and leaves it as the pending swap; the read loop takes it at its next frame boundary and
    /// disposes the replaced stage on the thread pool. A failed creation is logged and keeps the current stage. Every
    /// request is reported (loading, then ready or failed; a stale creation reports nothing) under the pipeline lock.
    /// The *Locked methods run under the pipeline lock; TakeSwap on the read loop (ResetLocked after it ended).
    /// </summary>
    private sealed class StageSlot(string kind, Action<string?, StageLoad, string?> report)
    {
        // Under the pipeline lock.
        private string? _requested;
        private string? _published;
        private int _generation;
        // Set under the pipeline lock, taken by the read loop.
        private Swap? _pending;
        // Read loop.
        private IMicStage? _active;

        /// <summary>The option whose stage runs now (read loop).</summary>
        public MicOption? ActiveOption { get; private set; }

        /// <summary>
        /// Starts creating the stage for <paramref name="option"/> (null = none; "raw" has no stage) unless it is
        /// already requested.
        /// </summary>
        public void RequestLocked(MicOption? option, Lock gate)
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
            _ = Task.Run(() => Create(option, generation, gate));
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

        /// <summary>Read loop, at a frame boundary: applies a pending swap; returns the stage to run (null = none).</summary>
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

        /// <summary>Read loop: the active stage threw; disposes it and runs none until the next swap. Returns its option id.</summary>
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

        private void Create(MicOption option, int generation, Lock gate)
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
                        // M4: the model file is missing, unreadable or damaged; M0: any other failure.
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

        // Returns the stage of a pending swap the read loop never took, for the caller to dispose.
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
