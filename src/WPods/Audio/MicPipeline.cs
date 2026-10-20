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
    /// <summary>An app captures but no audio arrived for 3 s (including no AirPods attached).</summary>
    Problem,
}

/// <summary>
/// AirPods mic -> \\.\NTA2DPVM. Starts the AirPods mic stream the first time an app captures in a session and
/// never stops it; 0x58 packets are dropped unread while nothing captures. Decodes on the session read loop:
/// AAC-ELD -> 48 kHz mono s16 -> mode processing -> WriteFile. Create before <see cref="ConnectionManager.Start"/>.
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
    // Standard mode keeps this share of what RNNoise removed (~ -12 dB residual noise).
    private const float StandardResidual = 0.25f;

    private static readonly byte[] StartMicrophone = [0x04, 0x00, 0x04, 0x00, 0x58, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x01, 0x82, 0x00, 0x00, 0x00, 0x04, 0x96, 0x00];

    private readonly SettingsStore _settings;
    private readonly VirtualMic _mic;
    private readonly Lock _lock = new();
    private readonly Timer _timer;

    // Read-loop state (one session at a time; reset on detach after its read loop ended).
    private readonly short[] _pcm = GC.AllocateArray<short>(PcmCapacity, pinned: true);
    private float[] _input = GC.AllocateArray<float>(Denoiser.FrameSize, pinned: true);
    private float[] _previousInput = GC.AllocateArray<float>(Denoiser.FrameSize, pinned: true);
    private AacDecoder? _decoder;
    private Denoiser? _denoiser;
    private bool _nativeFailed;
    private int _fill;
    private int _errors;
    private int _peak;
    private long _levelTick;

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
        _decoder?.Dispose();
        _decoder = null;
        _denoiser?.Dispose();
        _denoiser = null;
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
                BeginWaitLocked(now);
                SendStartLocked(session, now);
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
            }
        }
    }

    private void OnCaptureChanged(bool capturing)
    {
        lock (_lock)
        {
            _capturing = capturing;
            if (!capturing)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                SetStatusLocked(MicStatus.NotActive);
                return;
            }
            _flush = true;
            long now = Environment.TickCount64;
            BeginWaitLocked(now);
            if (!_startSent && _session is { } session && IsReady(session))
            {
                SendStartLocked(session, now);
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
            SetStatusLocked(MicStatus.Problem);
            if (_session is not { } session || !IsReady(session))
            {
                // Attach/ready restarts the wait.
                return;
            }
            long sinceStart = now - _lastStart;
            if (silent >= StallAfterMs && sinceStart >= RestartEveryMs)
            {
                SendStartLocked(session, now);
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
                    SetStatusLocked(MicStatus.Live);
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
            _denoiser = new Denoiser();
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            // No audio -> Problem after 3 s; retried on the next session.
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
            }
            return false;
        }
        _errors = 0;
        if (samples == 0)
        {
            return false;
        }
        _fill += samples;
        Emit(now);
        return true;
    }

    // Wide Spectrum writes everything at once; the RNNoise modes write whole 480-sample frames.
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
                Denoise(_pcm.AsSpan(ready, Denoiser.FrameSize), mode == MicMode.Standard);
            }
            if (ready == 0)
            {
                return;
            }
        }
        ReadOnlySpan<short> output = _pcm.AsSpan(0, ready);
        UpdateLevel(output, now);
        _mic.Write(output);
        _fill -= ready;
        _pcm.AsSpan(ready, _fill).CopyTo(_pcm);
    }

    private unsafe void Denoise(Span<short> frame, bool standard)
    {
        float* denoised = stackalloc float[Denoiser.FrameSize];
        fixed (float* input = _input)
        fixed (float* previous = _previousInput)
        {
            for (int i = 0; i < Denoiser.FrameSize; i++)
            {
                input[i] = frame[i];
            }
            _denoiser!.Process(denoised, input);
            for (int i = 0; i < Denoiser.FrameSize; i++)
            {
                float wet = denoised[i];
                // RNNoise output lags one frame, so the dry signal mixed back is the previous input frame.
                float value = standard ? wet + StandardResidual * (previous[i] - wet) : wet;
                frame[i] = (short)Math.Clamp(MathF.Round(value), short.MinValue, short.MaxValue);
            }
        }
        (_input, _previousInput) = (_previousInput, _input);
    }

    private void UpdateLevel(ReadOnlySpan<short> output, long now)
    {
        int peak = _peak;
        foreach (short sample in output)
        {
            peak = Math.Max(peak, Math.Abs((int)sample));
        }
        if (now - _levelTick < LevelIntervalMs)
        {
            _peak = peak;
            return;
        }
        _levelTick = now;
        _peak = 0;
        Volatile.Write(ref _level, Math.Max(peak / 32768f, Volatile.Read(ref _level) * LevelDecay));
    }

    private void BeginWaitLocked(long now)
    {
        Volatile.Write(ref _lastAudio, now);
        SetStatusLocked(MicStatus.Starting);
        ArmLocked(ProblemAfterMs);
    }

    private void SendStartLocked(AapSession session, long now)
    {
        _startSent = true;
        _lastStart = now;
        _ = SendStartAsync(session);
    }

    // A failed write is covered by the stall re-send.
    private static async Task SendStartAsync(AapSession session) =>
        await session.WriteAsync(StartMicrophone).ConfigureAwait(false);

    private void ArmLocked(long dueMs) => _timer.Change(Math.Max(dueMs, 1), Timeout.Infinite);

    private void SetStatusLocked(MicStatus status)
    {
        if (_status == status)
        {
            return;
        }
        _status = status;
        if (status != MicStatus.Live)
        {
            Volatile.Write(ref _level, 0f);
        }
        StatusChanged?.Invoke(status);
    }

    private static bool IsReady(AapSession session) => session.State.Status == SessionStatus.Ready;
}
