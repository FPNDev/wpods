using WPods.Core;
using Timer = System.Threading.Timer;

namespace WPods.Audio;

public enum MicStatus
{
    /// <summary>No app captures from "Headset (A2DP)".</summary>
    NotActive,
    /// <summary>An app captures and the mic stream was requested; no audio yet.</summary>
    Starting,
    /// <summary>Decoded audio is flowing.</summary>
    Live,
    /// <summary>
    /// An app captures but no audio arrived for 3 s (including no AirPods attached), or the virtual mic device
    /// cannot be opened or read.
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
    private const int ProblemAfterMs = 3000;
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

    private readonly SettingsStore _settings;
    private readonly VirtualMic _mic;
    private readonly Lock _lock = new();
    private readonly Timer _timer;

    // Read-loop state (one session at a time; reset on detach after its read loop ended).
    private readonly float[] _pcm = GC.AllocateArray<float>(PcmCapacity, pinned: true);
    private readonly short[] _output = GC.AllocateArray<short>(PcmCapacity, pinned: true);
    private AacDecoder? _decoder;
    private VoiceProcessor? _voice;
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
    private long _lastStart;
    private long _lastAudio;
    private volatile bool _capturing;
    private volatile bool _flush;
    private volatile MicMode _mode;
    private volatile MicStatus _status;
    private float _level;

    public MicPipeline(ConnectionManager connections)
    {
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

    public MicStatus Status => _status;

    /// <summary>Smoothed output peak 0..1, updated at most every 100 ms while <see cref="MicStatus.Live"/>; 0 otherwise.</summary>
    public float Level => Volatile.Read(ref _level);

    private void OnAttached(AapSession session)
    {
        lock (_lock)
        {
            _session = session;
            _startSent = false;
            _mode = _settings.Get(session.Address).MicMode;
            Log.Info("mic", $"session {Mac.Format(session.Address)} attached, mode={_mode}, capturing={_capturing}");
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
            }
        }
        Log.Info("mic", $"session {Mac.Format(session.Address)} detached");
        _decoder?.Dispose();
        _decoder = null;
        _voice?.Dispose();
        _voice = null;
        _nativeFailed = false;
        _fill = 0;
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
            _capturing = state == CaptureState.Capturing;
            if (!_capturing)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
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

    // Stall checks only; re-armed for the next deadline, so it fires about every 3 s while audio flows.
    private void OnTimer()
    {
        lock (_lock)
        {
            if (!_capturing)
            {
                return;
            }
            long now = Environment.TickCount64;
            long silent = now - Volatile.Read(ref _lastAudio);
            if (silent < ProblemAfterMs)
            {
                ArmLocked(ProblemAfterMs - silent);
                return;
            }
            SetStatusLocked(MicStatus.Problem, $"no audio for {silent} ms");
            if (_session is not { } session || !IsReady(session))
            {
                // Attach/ready restarts the wait.
                return;
            }
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
                if (_capturing)
                {
                    SetStatusLocked(MicStatus.Live, "decoded audio");
                    ArmLocked(ProblemAfterMs);
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
            // No audio -> Problem after 3 s; retried on the next session.
            Log.Error("mic", "decoder/denoiser init failed", e);
            _decoder?.Dispose();
            _decoder = null;
            _nativeFailed = true;
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
        SetStatusLocked(MicStatus.Starting, reason);
        ArmLocked(ProblemAfterMs);
    }

    private void SendStartLocked(AapSession session, long now, string reason)
    {
        Log.Info("mic", $"START sent to {Mac.Format(session.Address)} ({reason})");
        _startSent = true;
        _lastStart = now;
        _ = SendStartAsync(session);
    }

    // A failed write is covered by the stall re-send.
    private static async Task SendStartAsync(AapSession session)
    {
        if (!await session.WriteAsync(StartMicrophone).ConfigureAwait(false))
        {
            Log.Warn("mic", "START write failed");
        }
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
