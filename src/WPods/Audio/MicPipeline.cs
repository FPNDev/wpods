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

/// <summary>
/// AirPods mic -> \\.\NTA2DPVM. Starts the AirPods mic stream the first time an app captures in a session and
/// never stops it; 0x58 packets are dropped unread while nothing captures. Decodes on the session read loop, in float
/// until the end: AAC-ELD -> 48 kHz mono float -> make-up gain -> mode processing -> soft limiter + the only 16-bit
/// conversion -> WriteFile. While capturing it logs the input (decoded, before gain) and output levels once per second.
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
    private const int LevelIntervalMs = 100;
    private const float LevelDecay = 0.6f;
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
    private VoiceProcessor? _voice;
    // Written under _lock.
    private bool _nativeFailed;
    private int _fill;
    private int _errors;
    private float _peak;
    private long _levelTick;
    private LevelMeter _inputMeter;
    private LevelMeter _outputMeter;
    private long _levelLogTick;
    private long _lastDecoderFlushLog;

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
    private volatile MicMode _mode;
    private volatile MicStatus _status;
    private float _level;

    public MicPipeline(ConnectionManager connections)
    {
        _connections = connections;
        _settings = connections.Settings;
        _timer = new Timer(static s => ((MicPipeline)s!).OnTimer(), this, Timeout.Infinite, Timeout.Infinite);
        _mic = new VirtualMic(OnCaptureChanged);
        connections.Attached += OnAttached;
        connections.Detached += OnDetached;
        _settings.HostSettingsChanged += OnHostSettingsChanged;
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

    public MicStatus Status => _status;

    /// <summary>
    /// Why the status is <see cref="MicStatus.Problem"/> while an app captures: "M1" the decoder or voice processor
    /// failed to load, "M2" a START write failed; null otherwise. Set before <see cref="StatusChanged"/> is raised, so
    /// read it from that handler.
    /// </summary>
    public string? FaultCode => _faultCode;

    /// <summary>An app captures from the virtual mic.</summary>
    public bool Capturing => _capturing;

    /// <summary>Smoothed output peak 0..1, updated at most every 100 ms while <see cref="MicStatus.Live"/>; 0 otherwise.</summary>
    public float Level => Volatile.Read(ref _level);

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
            Log.Info("mic", $"session {Mac.Format(session.Address)} attached, mode={_mode}, capturing={_capturing}");
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
        _voice?.Dispose();
        _voice = null;
        _fill = 0;
        lock (_lock)
        {
            _nativeFailed = false;
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
            if (_session?.Address == address)
            {
                _mode = settings.MicMode;
                Log.Info("mic", $"mode -> {settings.MicMode}");
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
            _voice = new VoiceProcessor();
            Log.Info("mic", "decoder and voice processor created");
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            Log.Error("mic", "decoder/denoiser init failed", e);
            // Problem (M1) until the next session, which retries.
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

    // Wide Spectrum writes everything at once; the other modes write whole 480-sample frames.
    private void Emit(long now)
    {
        MicMode mode = _mode;
        int ready = 0;
        if (mode == MicMode.WideSpectrum)
        {
            ready = _fill;
        }
        else
        {
            for (; _fill - ready >= Denoiser.FrameSize; ready += Denoiser.FrameSize)
            {
                _voice!.Process(_pcm.AsSpan(ready, Denoiser.FrameSize), mode);
            }
            if (ready == 0)
            {
                return;
            }
        }
        float peak = _peak;
        for (int i = 0; i < ready; i++)
        {
            short sample = SoftLimiter.ToPcm16(_pcm[i]);
            _output[i] = sample;
            float level = sample / 32768f;
            _outputMeter.Add(level);
            peak = MathF.Max(peak, MathF.Abs(level));
        }
        UpdateLevel(peak, now);
        _mic.Write(_output.AsSpan(0, ready));
        _fill -= ready;
        _pcm.AsSpan(ready, _fill).CopyTo(_pcm);
        if (now - _levelLogTick >= LevelLogIntervalMs)
        {
            _levelLogTick = now;
            Log.Info("mic", $"level in {_inputMeter.Take()} | out {_outputMeter.Take()} | mode {mode}, gain {MakeUpGain:0.00}");
        }
    }

    private void UpdateLevel(float peak, long now)
    {
        if (now - _levelTick < LevelIntervalMs)
        {
            _peak = peak;
            return;
        }
        _levelTick = now;
        _peak = 0;
        Volatile.Write(ref _level, MathF.Max(peak, Volatile.Read(ref _level) * LevelDecay));
    }

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
        if (status != MicStatus.Live)
        {
            Volatile.Write(ref _level, 0f);
        }
        StatusChanged?.Invoke(status);
    }

    private static bool IsReady(AapSession session) => session.State.Status == SessionStatus.Ready;

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
