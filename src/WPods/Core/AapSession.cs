using Microsoft.Win32.SafeHandles;

namespace WPods.Core;

/// <summary>Raw inbound AAP packet; the span is valid only during the call.</summary>
public delegate void PacketHandler(ReadOnlySpan<byte> packet);

internal enum SessionEnd
{
    /// <summary>Closed by WPods.</summary>
    Closed,
    /// <summary>Driver reported 1167: the ACL link is gone.</summary>
    LinkLost,
    /// <summary>Any other read error or end of stream while the session was open.</summary>
    Failed,
}

/// <summary>
/// One open NTAPD channel. Threading: <see cref="PacketReceived"/> fires on the read loop (a thread-pool thread,
/// one packet at a time). <see cref="Changed"/> fires on a thread-pool thread (read loop, command or heart-rate
/// completion), serialized under the state lock. Handlers must not block (marshal to the UI with BeginInvoke).
/// </summary>
public sealed class AapSession : IAsyncDisposable
{
    private const int ReadBufferSize = 1691;
    // An L2CAP write normally completes in milliseconds; 5 s means the channel is stuck. The write is then
    // cancelled (CancelIoEx through the token) and its completion awaited, so no buffer outlives it.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KeysTimeout = TimeSpan.FromSeconds(3);
    // Live (AirPods Pro 2): the full 0x09 settings report arrives within ~90 ms of the handshake reply and the
    // re-report after the notification request within ~25 ms; no end marker exists. 1 s after the request covers both.
    private static readonly TimeSpan InitialReportWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartRateFirstReadingTimeout = TimeSpan.FromSeconds(12);
    private const int HeartRateAttempts = 3;
    private const uint HeartRatePeriodUs = 1_000_000;

    // FileStream, not RandomAccess: RandomAccess rejects the NTAPD handle (not seekable) with NotSupportedException.
    private readonly FileStream _stream;
    private readonly SettingsStore _settings;
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly Lock _stateLock = new();
    private CancellationTokenSource _writeTimeout = new();
    private TaskCompletionSource? _firstPacket;
    private TaskCompletionSource? _keysReply;
    private TaskCompletionSource? _heartRateReading;
    private CancellationTokenSource? _heartRateCts;
    private Task _heartRateTask = Task.CompletedTask;
    private Task<SessionEnd> _readLoop = Task.FromResult(SessionEnd.Closed);
    private byte _streamSequence;
    // Reported capability set last written to settings (read loop and initialization).
    private DeviceCapabilities _savedReported;

    internal AapSession(ulong address, SafeFileHandle handle, SettingsStore settings)
    {
        Address = address;
        _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
        _settings = settings;
        DeviceCapabilities baseline = Models.FromModelNumber(settings.GetModel(address)?.Number)?.Capabilities ?? DeviceCapabilities.None;
        State = new DeviceState(settings.GetCapabilities(address), baseline);
        _savedReported = State.Reported;
    }

    public ulong Address { get; }

    public DeviceState State { get; }

    /// <summary>Every inbound packet (including 0x58 mic audio), before it is parsed.</summary>
    public event PacketHandler? PacketReceived;

    /// <summary>At most one per inbound packet or command; never raised when nothing changed.</summary>
    public event Action<DeviceChange>? Changed;

    internal Task<SessionEnd> Completion => _readLoop;

    internal void Start()
    {
        _readLoop = ReadLoopAsync();
        _ = InitializeAsync();
    }

    /// <summary>
    /// Writes one packet through the serialized writer. False when the session is closed, the write failed or
    /// timed out; throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async ValueTask<bool> WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_life.IsCancellationRequested)
            {
                return false;
            }
            if (!_writeTimeout.TryReset())
            {
                _writeTimeout.Dispose();
                _writeTimeout = new CancellationTokenSource();
            }
            _writeTimeout.CancelAfter(WriteTimeout);
            using CancellationTokenRegistration caller = ct.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), _writeTimeout);
            using CancellationTokenRegistration life = _life.Token.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), _writeTimeout);
            await _stream.WriteAsync(packet, _writeTimeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask<bool> SetListeningModeAsync(ListeningMode mode, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return SendSettingAsync(Aap.Setting(Aap.ListeningModeId, (byte)mode), ct);
    }

    public ValueTask<bool> SetEarDetectionAsync(bool on, CancellationToken ct = default) =>
        SendSettingAsync(Aap.Setting(Aap.EarDetectionId, Aap.Bool(on)), ct);

    public ValueTask<bool> SetConversationAwarenessAsync(bool on, CancellationToken ct = default) =>
        SendSettingAsync(Aap.Setting(Aap.ConversationAwarenessId, Aap.Bool(on)), ct);

    public ValueTask<bool> SetPersonalizedVolumeAsync(bool on, CancellationToken ct = default) =>
        SendSettingAsync(Aap.Setting(Aap.PersonalizedVolumeId, Aap.Bool(on)), ct);

    /// <summary>Allow "Off" in the listening-mode cycle.</summary>
    public ValueTask<bool> SetAllowOffAsync(bool on, CancellationToken ct = default) =>
        SendSettingAsync(Aap.Setting(Aap.AllowOffId, Aap.Bool(on)), ct);

    public ValueTask<bool> SetOneBudAncAsync(bool on, CancellationToken ct = default) =>
        SendSettingAsync(Aap.Setting(Aap.OneBudAncId, Aap.Bool(on)), ct);

    public ValueTask<bool> SetMicSideAsync(MicSide side, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(side))
        {
            throw new ArgumentOutOfRangeException(nameof(side));
        }
        return SendSettingAsync(Aap.Setting(Aap.MicSideId, (byte)side), ct);
    }

    public ValueTask<bool> SetPressSpeedAsync(PressSpeed speed, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(speed))
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }
        return SendSettingAsync(Aap.Setting(Aap.PressSpeedId, (byte)speed), ct);
    }

    public ValueTask<bool> SetHoldDurationAsync(HoldDuration duration, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(duration))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        return SendSettingAsync(Aap.Setting(Aap.HoldDurationId, (byte)duration), ct);
    }

    /// <summary>0..100 (1-byte form).</summary>
    public ValueTask<bool> SetToneVolumeAsync(byte volume, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volume, (byte)100);
        return SendSettingAsync(Aap.ToneVolume(volume), ct);
    }

    /// <summary>On: 0x2C on, 300 ms, 0x33 on. Off: 0x33 off, 300 ms, 0x2C off. Requires enrollment.</summary>
    public async Task<bool> SetHearingAidAsync(bool on, CancellationToken ct = default)
    {
        if (State.HearingAidEnrolled != true)
        {
            throw new InvalidOperationException("Hearing Aid has not been reported as configured on this AirPods device.");
        }
        byte[] hearingAid = Aap.HearingAid(on);
        byte[] hearingAssist = Aap.Setting(Aap.HearingAssistId, Aap.Bool(on));
        if (!await SendSettingAsync(on ? hearingAid : hearingAssist, ct).ConfigureAwait(false))
        {
            return false;
        }
        await Task.Delay(300, ct).ConfigureAwait(false);
        return await SendSettingAsync(on ? hearingAssist : hearingAid, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts (up to 3 attempts, 12 s each for a first reading) or stops the heart-rate stream. Stopping cancels
    /// and awaits any running start sequence first, so it can never re-enable the stream. Call from one thread.
    /// </summary>
    public async Task<bool> SetHeartRateAsync(bool on)
    {
        if (on && !State.HeartRateSupported)
        {
            throw new InvalidOperationException("Heart-rate streaming is only enabled for a device that reports AirPods Pro 3 support.");
        }
        if (_heartRateCts is { } previous)
        {
            _heartRateCts = null;
            await previous.CancelAsync().ConfigureAwait(false);
            await _heartRateTask.ConfigureAwait(false);
            previous.Dispose();
        }
        if (on)
        {
            SetHeartRateStatus(HeartRateStatus.Starting, clearReading: false);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            _heartRateCts = cts;
            _heartRateTask = RunHeartRateAsync(cts.Token);
            return true;
        }
        SetHeartRateStatus(HeartRateStatus.Off, clearReading: true);
        return await WriteAsync(Aap.HeartRateStream(++_streamSequence, Aap.HeartRateService, 0)).ConfigureAwait(false)
            && await WriteAsync(Aap.HeartRateStream(++_streamSequence, Aap.HeartRateLegacyService, 0)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        await _readLoop.ConfigureAwait(false);
        await _heartRateTask.ConfigureAwait(false);
        _heartRateCts?.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<SessionEnd> ReadLoopAsync()
    {
        CancellationToken ct = _life.Token;
        try
        {
            while (true)
            {
                int count = await _stream.ReadAsync(_readBuffer, ct).ConfigureAwait(false);
                if (count == 0)
                {
                    return SessionEnd.Failed;
                }
                OnPacket(new ReadOnlySpan<byte>(_readBuffer, 0, count));
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return SessionEnd.Closed;
        }
        catch (IOException e) when ((e.HResult & 0xFFFF) == Native.ErrorDeviceNotConnected)
        {
            return SessionEnd.LinkLost;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return SessionEnd.Failed;
        }
    }

    private void OnPacket(ReadOnlySpan<byte> packet)
    {
        PacketReceived?.Invoke(packet);
        if (_firstPacket is { } first)
        {
            _firstPacket = null;
            first.TrySetResult();
        }
        DeviceChange change = Aap.TryParseKeys(packet, out ProximityKeys? keys) ? OnKeys(keys) : DeviceChange.None;
        lock (_stateLock)
        {
            change |= Aap.Apply(packet, State);
            if (change != DeviceChange.None)
            {
                Changed?.Invoke(change);
            }
        }
        if ((change & DeviceChange.HeartRate) != 0)
        {
            _heartRateReading?.TrySetResult();
        }
        if (State.InitialReportDone && State.Reported != _savedReported)
        {
            SaveCapabilities();
        }
    }

    private DeviceChange OnKeys(ProximityKeys? keys)
    {
        _keysReply?.TrySetResult();
        try
        {
            return keys is not null && _settings.SetKeys(Address, keys) ? DeviceChange.Keys : DeviceChange.None;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DeviceChange.None;
        }
    }

    // Handshake -> (any reply) -> 100 ms -> feature flags -> notifications -> 1 s -> capabilities final -> proximity keys (one retry).
    private async Task InitializeAsync()
    {
        CancellationToken ct = _life.Token;
        try
        {
            TaskCompletionSource first = NewSignal();
            _firstPacket = first;
            if (!await WriteAsync(Aap.Handshake, ct).ConfigureAwait(false)
                || !await WaitAsync(first.Task, HandshakeTimeout, ct).ConfigureAwait(false))
            {
                SetStatus(SessionStatus.NoResponse);
                return;
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
            if (!await WriteAsync(Aap.FeatureFlags, ct).ConfigureAwait(false)
                || !await WriteAsync(Aap.RequestNotifications, ct).ConfigureAwait(false))
            {
                return;
            }
            SetStatus(SessionStatus.Ready);
            await Task.Delay(InitialReportWindow, ct).ConfigureAwait(false);
            lock (_stateLock)
            {
                Changed?.Invoke(State.FinishInitialReport() | DeviceChange.InitialReport);
            }
            SaveCapabilities();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                TaskCompletionSource reply = NewSignal();
                _keysReply = reply;
                if (!await WriteAsync(Aap.RequestKeys, ct).ConfigureAwait(false)
                    || await WaitAsync(reply.Task, KeysTimeout, ct).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session closed.
        }
    }

    private async Task RunHeartRateAsync(CancellationToken ct)
    {
        try
        {
            for (int attempt = 0; attempt < HeartRateAttempts; attempt++)
            {
                TaskCompletionSource reading = NewSignal();
                _heartRateReading = reading;
                if (!await SendHeartRateStartAsync(ct).ConfigureAwait(false))
                {
                    SetHeartRateStatus(HeartRateStatus.Failed, clearReading: false);
                    return;
                }
                if (await WaitAsync(reading.Task, HeartRateFirstReadingTimeout, ct).ConfigureAwait(false))
                {
                    return;
                }
            }
            SetHeartRateStatus(HeartRateStatus.NoReading, clearReading: false);
        }
        catch (OperationCanceledException)
        {
            // Stopped or session closed.
        }
    }

    // NTPods timing: init packets with 180/220 ms gaps, power-on, 120 ms, stream 0x54, 120 ms, stream 0x13.
    private async Task<bool> SendHeartRateStartAsync(CancellationToken ct) =>
        await WriteAsync(Aap.HeartRateConnectService0, ct).ConfigureAwait(false)
        && await DelayTrue(180, ct).ConfigureAwait(false)
        && await WriteAsync(Aap.HeartRateCapabilitiesService0, ct).ConfigureAwait(false)
        && await DelayTrue(220, ct).ConfigureAwait(false)
        && await WriteAsync(Aap.HeartRateConnectService4, ct).ConfigureAwait(false)
        && await DelayTrue(180, ct).ConfigureAwait(false)
        && await WriteAsync(Aap.HeartRateCapabilitiesService4, ct).ConfigureAwait(false)
        && await DelayTrue(220, ct).ConfigureAwait(false)
        && await WriteAsync(Aap.HeartRatePowerOn, ct).ConfigureAwait(false)
        && await DelayTrue(120, ct).ConfigureAwait(false)
        && await WriteAsync(Aap.HeartRateStream(++_streamSequence, Aap.HeartRateService, HeartRatePeriodUs), ct).ConfigureAwait(false)
        && await DelayTrue(120, ct).ConfigureAwait(false)
        && await WriteAsync(Aap.HeartRateStream(++_streamSequence, Aap.HeartRateLegacyService, HeartRatePeriodUs), ct).ConfigureAwait(false);

    private static async Task<bool> DelayTrue(int milliseconds, CancellationToken ct)
    {
        await Task.Delay(milliseconds, ct).ConfigureAwait(false);
        return true;
    }

    // Writes a 0x09 setting, then applies the same bytes as the device echo would (optimistic update).
    private async ValueTask<bool> SendSettingAsync(byte[] packet, CancellationToken ct)
    {
        if (!await WriteAsync(packet, ct).ConfigureAwait(false))
        {
            return false;
        }
        lock (_stateLock)
        {
            DeviceChange change = Aap.ApplySetting(packet, State);
            if (change != DeviceChange.None)
            {
                Changed?.Invoke(change);
            }
        }
        return true;
    }

    // Refreshes the pair's remembered (reported) capabilities; SettingsStore skips the write when unchanged.
    private void SaveCapabilities()
    {
        DeviceCapabilities reported = State.Reported;
        try
        {
            _settings.SetCapabilities(Address, reported);
            _savedReported = reported;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Retried on the next change or attach.
        }
    }

    private void SetStatus(SessionStatus status)
    {
        lock (_stateLock)
        {
            State.Status = status;
            Changed?.Invoke(DeviceChange.Connection);
        }
    }

    private void SetHeartRateStatus(HeartRateStatus status, bool clearReading)
    {
        lock (_stateLock)
        {
            if (State.HeartRate == status && !clearReading)
            {
                return;
            }
            State.HeartRate = status;
            if (clearReading)
            {
                State.HeartRateBpm = null;
            }
            Changed?.Invoke(DeviceChange.HeartRate);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<bool> WaitAsync(Task signal, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await signal.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
