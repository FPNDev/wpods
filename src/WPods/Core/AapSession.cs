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
    // Live (AirPods Pro 2): the full 0x09 settings report (~17 settings in ~90 ms, no end marker) arrives within
    // ~90 ms of the handshake reply on some attaches, but 4-31 s later on others (log 2026-10-22). The initial
    // report ends 1 s after the first 0x09 (never before 1 s after the notification request), or after the timeout.
    private static readonly TimeSpan InitialReportWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InitialReportTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartRateFirstReadingTimeout = TimeSpan.FromSeconds(12);
    // After a listening-mode request, device reports of other modes (echoes of earlier requests) are ignored until
    // the device reports the requested mode or this passes (Round 17: ~1.5 s; observed resync after 200-300 ms).
    private static readonly TimeSpan ListeningModeHold = TimeSpan.FromSeconds(1.5);
    private const int HeartRateAttempts = 3;
    private const uint HeartRatePeriodUs = 1_000_000;

    // FileStream, not RandomAccess: RandomAccess rejects the NTAPD handle (not seekable) with NotSupportedException.
    private readonly FileStream _stream;
    private readonly SettingsStore _settings;
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    // Serializes the 0x34 + 0x1A pair (attach write and SetStemModesAsync) so they never interleave.
    private readonly SemaphoreSlim _stemGate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly Lock _stateLock = new();
    private CancellationTokenSource _writeTimeout = new();
    private TaskCompletionSource? _firstPacket;
    private TaskCompletionSource? _firstSetting;
    private TaskCompletionSource? _keysReply;
    private TaskCompletionSource? _heartRateReading;
    private CancellationTokenSource? _heartRateCts;
    private Task _heartRateTask = Task.CompletedTask;
    private Task<SessionEnd> _readLoop = Task.FromResult(SessionEnd.Closed);
    private byte _streamSequence;
    // Reported capability set last written to settings (read loop and initialization).
    private DeviceCapabilities _savedReported;
    // Read loop: 0x58 mic audio packets are logged as one count per second.
    private int _audioCount;
    private long _audioWindowStart;

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
                Log.Info("aap", "write skipped: session closed");
                return false;
            }
            Log.Packet(outbound: true, packet.Span);
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
            Log.Warn("aap", _life.IsCancellationRequested ? "write cancelled: session closing" : "write timed out after 5 s");
            return false;
        }
        catch (IOException e)
        {
            Log.Warn("aap", $"write failed: 0x{e.HResult:X8} {e.Message}");
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Requests <paramref name="mode"/>. Until the device reports it (or 1.5 s pass, then the device's latest report
    /// wins) device reports of other modes are ignored, so echoes of earlier requests cause no flicker. A newer
    /// request replaces the pending one.
    /// </summary>
    public async ValueTask<bool> SetListeningModeAsync(ListeningMode mode, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        int serial;
        lock (_stateLock)
        {
            serial = State.HoldListeningMode((byte)mode, ListeningModeHold);
        }
        Log.Info("aap", $"listening mode {mode} requested (hold {serial}); other reports ignored for up to {ListeningModeHold.TotalSeconds} s");
        _ = ReleaseListeningModeAsync(serial);
        byte[] packet = Aap.Setting(Aap.ListeningModeId, (byte)mode);
        bool written;
        try
        {
            written = await WriteAsync(packet, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ReleaseListeningMode(serial);
            throw;
        }
        if (!written)
        {
            ReleaseListeningMode(serial);
            return false;
        }
        lock (_stateLock)
        {
            // Optimistic update, unless a newer request already replaced this one.
            if (State.IsCurrentHold(serial) && Aap.ApplySetting(packet, State) is var change && change != DeviceChange.None)
            {
                Changed?.Invoke(change);
            }
        }
        return true;
    }

    public ValueTask<bool> SetEarDetectionAsync(bool on, CancellationToken ct = default) =>
        SendSettingAsync(Aap.Setting(Aap.EarDetectionId, Aap.Bool(on)), ct);

    public ValueTask<bool> SetConversationAwarenessAsync(bool on, CancellationToken ct = default) =>
        SendSettingAsync(Aap.Setting(Aap.ConversationAwarenessId, Aap.Bool(on)), ct);

    public ValueTask<bool> SetPersonalizedVolumeAsync(bool on, CancellationToken ct = default) =>
        SendSettingAsync(Aap.Setting(Aap.PersonalizedVolumeId, Aap.Bool(on)), ct);

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

    /// <summary>
    /// Sets the stem press-and-hold cycle: writes 0x34 (allow Off) = on when <see cref="StemModes.Off"/> is in
    /// <paramref name="modes"/>, else off, then 0x1A = <paramref name="modes"/>; stores it for the pair (written again on
    /// every attach) only when both writes succeeded. Throws <see cref="ArgumentOutOfRangeException"/> for fewer than
    /// two modes or unknown bits, <see cref="InvalidOperationException"/> when the pair lacks the listening-mode
    /// capability, or the Adaptive capability for a mask with Adaptive.
    /// </summary>
    public async ValueTask<bool> SetStemModesAsync(StemModes modes, CancellationToken ct = default)
    {
        if (!Aap.IsValidStemModes(modes))
        {
            throw new ArgumentOutOfRangeException(nameof(modes), modes, "Select at least two of Off, Noise Cancellation, Transparency and Adaptive.");
        }
        DeviceCapabilities capabilities = State.Capabilities;
        if ((capabilities & DeviceCapabilities.ListeningMode) == 0)
        {
            throw new InvalidOperationException("This AirPods device has no listening modes.");
        }
        if ((modes & StemModes.Adaptive) != 0 && (capabilities & DeviceCapabilities.Adaptive) == 0)
        {
            throw new InvalidOperationException("This AirPods device has no Adaptive listening mode.");
        }
        await _stemGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Log.Info("aap", $"stem modes {modes} (0x{(byte)modes:X2}) requested");
            if (!await WriteStemModesAsync(modes, ct).ConfigureAwait(false))
            {
                return false;
            }
            try
            {
                _settings.SetStemModes(Address, modes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The device has it; the next attach writes the previously stored mask again.
                Log.Warn("aap", $"stem modes not saved: {e.Message}");
            }
            return true;
        }
        finally
        {
            _stemGate.Release();
        }
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
        Log.Info("aap", $"close session {Mac.Format(Address)}");
        await _life.CancelAsync().ConfigureAwait(false);
        await _readLoop.ConfigureAwait(false);
        if (_audioCount > 0)
        {
            Log.Info("aap", $"RX 0x58 audio x{_audioCount} in the last {Environment.TickCount64 - _audioWindowStart} ms");
            _audioCount = 0;
        }
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
                    Log.Warn("aap", "read: end of stream");
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
            Log.Warn("aap", $"read failed: 0x{e.HResult:X8} (1167, device not connected)");
            return SessionEnd.LinkLost;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            Log.Warn("aap", $"read failed: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            return SessionEnd.Failed;
        }
    }

    private void OnPacket(ReadOnlySpan<byte> packet)
    {
        LogInbound(packet);
        PacketReceived?.Invoke(packet);
        if (_firstPacket is { } first)
        {
            _firstPacket = null;
            first.TrySetResult();
        }
        if (_firstSetting is { } setting && Aap.IsSetting(packet))
        {
            _firstSetting = null;
            setting.TrySetResult();
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
            if (keys is null)
            {
                Log.Warn("aap", "keys reply without IRK and ENC_KEY");
                return DeviceChange.None;
            }
            bool stored = _settings.SetKeys(Address, keys);
            Log.Info("aap", stored ? "keys reply: stored (new or changed)" : "keys reply: same as stored");
            return stored ? DeviceChange.Keys : DeviceChange.None;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("aap", "keys not saved", e);
            return DeviceChange.None;
        }
    }

    // Every inbound packet as hex, except 0x58 mic audio (count per second) and the 0x31 keys reply (length only).
    private void LogInbound(ReadOnlySpan<byte> packet)
    {
        int opcode = packet.Length >= 6 && packet[0] == 0x04 && packet[1] == 0x00 && packet[2] == 0x04 && packet[3] == 0x00
            ? packet[4] | (packet[5] << 8)
            : -1;
        long now = Environment.TickCount64;
        if (opcode == 0x58)
        {
            if (_audioCount++ == 0)
            {
                _audioWindowStart = now;
            }
        }
        else if (opcode == 0x31)
        {
            Log.Info("aap", $"RX 0x31 keys reply, {packet.Length} bytes (content not logged)");
        }
        else
        {
            Log.Packet(outbound: false, packet);
        }
        if (_audioCount > 0 && now - _audioWindowStart >= 1000)
        {
            Log.Info("aap", $"RX 0x58 audio x{_audioCount} in {now - _audioWindowStart} ms");
            _audioCount = 0;
        }
    }

    // Handshake -> (any reply) -> 100 ms -> feature flags -> notifications -> 1 s -> stem modes -> proximity keys
    // (one retry). The initial report ends separately (FinishInitialReportAsync).
    private async Task InitializeAsync()
    {
        CancellationToken ct = _life.Token;
        try
        {
            Log.Info("aap", $"init {Mac.Format(Address)}: handshake");
            TaskCompletionSource first = NewSignal();
            _firstPacket = first;
            TaskCompletionSource firstSetting = NewSignal();
            _firstSetting = firstSetting;
            if (!await WriteAsync(Aap.Handshake, ct).ConfigureAwait(false)
                || !await WaitAsync(first.Task, HandshakeTimeout, ct).ConfigureAwait(false))
            {
                Log.Warn("aap", "init: handshake not written or no packet within 10 s");
                SetStatus(SessionStatus.NoResponse);
                return;
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
            if (!await WriteAsync(Aap.FeatureFlags, ct).ConfigureAwait(false)
                || !await WriteAsync(Aap.RequestNotifications, ct).ConfigureAwait(false))
            {
                Log.Warn("aap", "init: feature flags / notification request not written; status stays " + State.Status);
                return;
            }
            SetStatus(SessionStatus.Ready);
            _ = FinishInitialReportAsync(firstSetting.Task, ct);
            await Task.Delay(InitialReportWindow, ct).ConfigureAwait(false);
            await WriteStemModesOnAttachAsync(ct).ConfigureAwait(false);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                TaskCompletionSource reply = NewSignal();
                _keysReply = reply;
                if (!await WriteAsync(Aap.RequestKeys, ct).ConfigureAwait(false)
                    || await WaitAsync(reply.Task, KeysTimeout, ct).ConfigureAwait(false))
                {
                    return;
                }
                Log.Warn("aap", $"init: no keys reply within 3 s (attempt {attempt + 1})");
            }
        }
        catch (OperationCanceledException)
        {
            // Session closed.
            Log.Info("aap", "init: stopped, session closed");
        }
    }

    // Ends the initial report (capabilities final, "checking" off) InitialReportWindow after the device's first 0x09
    // setting, counted from Ready when it came earlier; without one within InitialReportTimeout, ends it then.
    private async Task FinishInitialReportAsync(Task firstSetting, CancellationToken ct)
    {
        try
        {
            long start = Environment.TickCount64;
            if (await WaitAsync(firstSetting, InitialReportTimeout, ct).ConfigureAwait(false))
            {
                Log.Info("aap", $"init: first 0x09 setting {Environment.TickCount64 - start} ms after Ready");
                await Task.Delay(InitialReportWindow, ct).ConfigureAwait(false);
            }
            else
            {
                Log.Warn("aap", $"init: no 0x09 setting within {InitialReportTimeout.TotalSeconds:0} s; initial report ends without one");
            }
            lock (_stateLock)
            {
                Changed?.Invoke(State.FinishInitialReport() | DeviceChange.InitialReport);
            }
            SaveCapabilities();
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
            Log.Info("aap", "heart rate start sequence stopped");
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

    // Ends listening-mode hold <paramref name="serial"/> after ListeningModeHold unless the device confirmed it or a
    // newer request replaced it.
    private async Task ReleaseListeningModeAsync(int serial)
    {
        try
        {
            await Task.Delay(ListeningModeHold, _life.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Session closed.
            return;
        }
        ReleaseListeningMode(serial);
    }

    private void ReleaseListeningMode(int serial)
    {
        lock (_stateLock)
        {
            if (State.ReleaseListeningMode(serial))
            {
                Changed?.Invoke(DeviceChange.ListeningMode);
            }
        }
    }

    // iOS writes 0x1A on every reconnect and the AirPods never report it on attach (research-r17 section 2), so
    // the stored mask is written 1 s after the notification request. Adaptive is dropped for a pair without Adaptive.
    private async Task WriteStemModesOnAttachAsync(CancellationToken ct)
    {
        DeviceCapabilities capabilities = State.Capabilities;
        if ((capabilities & DeviceCapabilities.ListeningMode) == 0)
        {
            Log.Info("aap", "init: stem modes not written (no listening-mode capability)");
            return;
        }
        await _stemGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StemModes stored = _settings.GetStemModes(Address);
            StemModes modes = stored;
            if ((modes & StemModes.Adaptive) != 0 && (capabilities & DeviceCapabilities.Adaptive) == 0)
            {
                modes &= ~StemModes.Adaptive;
                if (!Aap.IsValidStemModes(modes))
                {
                    modes = SettingsStore.DefaultStemModes;
                }
                Log.Warn("aap", $"init: stored stem modes {stored} include Adaptive, which this pair lacks; writing {modes}");
            }
            Log.Info("aap", $"init: writing stem modes {modes} (0x{(byte)modes:X2})");
            if (!await WriteStemModesAsync(modes, ct).ConfigureAwait(false))
            {
                Log.Warn("aap", "init: stem modes not written");
            }
        }
        finally
        {
            _stemGate.Release();
        }
    }

    // Hidden coupling (Round 18): 0x34 allow-off follows the Off bit, written before 0x1A so the device honours it.
    private async ValueTask<bool> WriteStemModesAsync(StemModes modes, CancellationToken ct) =>
        await SendSettingAsync(Aap.Setting(Aap.AllowOffId, Aap.Bool((modes & StemModes.Off) != 0)), ct).ConfigureAwait(false)
        && await SendSettingAsync(Aap.Setting(Aap.StemModesId, (byte)modes), ct).ConfigureAwait(false);

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
            Log.Warn("aap", $"capabilities not saved: {e.Message}");
        }
    }

    private void SetStatus(SessionStatus status)
    {
        Log.Info("aap", $"status {State.Status} -> {status}");
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
            Log.Info("aap", $"heart rate {State.HeartRate} -> {status}");
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
