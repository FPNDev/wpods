using System.Buffers.Binary;
using System.IO.Pipes;

namespace WPods.Core;

/// <summary>Raw inbound AAP packet; the span is valid only during the call.</summary>
public delegate void PacketHandler(ReadOnlySpan<byte> packet);

internal enum SessionEnd
{
    /// <summary>Closed by WPods.</summary>
    Closed,
    /// <summary>The service ended the channel with 1167: the ACL link is gone.</summary>
    LinkLost,
    /// <summary>Any other channel end (closed by the AirPods, other error), read error or end of stream.</summary>
    Failed,
    /// <summary>Closed by the session itself: no handshake reply, init writes failed, or the initial 0x09 report never came.</summary>
    NoInitialReport,
}

/// <summary>
/// One open AAP channel through WPods.Service. PacketReceived fires on the read loop, Changed on thread-pool threads
/// serialized under the state lock. Handlers must not block.
/// </summary>
public sealed class AapSession : IAsyncDisposable
{
    private const int ReadBufferSize = 1691;
    // An L2CAP write normally completes in milliseconds; 5 s means the channel is stuck. The write is then
    // cancelled (CancelIoEx through the token) and its completion awaited, so no buffer outlives it.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KeysTimeout = TimeSpan.FromSeconds(3);
    // The 0x09 settings report has no end marker and may come right after the handshake or over 30 s later. It counts
    // as done 1 s after the first 0x09; without one in time the session closes itself and is reopened.
    private static readonly TimeSpan InitialReportWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InitialReportTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartRateFirstReadingTimeout = TimeSpan.FromSeconds(12);
    private const int HeartRateAttempts = 3;
    private const uint HeartRatePeriodUs = 1_000_000;

    // The open AAP pipe (message mode): one packet per message out; in, a kind byte, then a packet or the channel end.
    private readonly NamedPipeClientStream _stream;
    private readonly SettingsStore _settings;
    private readonly byte[] _readBuffer = new byte[1 + ReadBufferSize];
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    // Serializes stem-cycle (0x1A) writes from attach, SetStemModesAsync and Allow Off.
    private readonly SemaphoreSlim _stemGate = new(1, 1);
    // Serializes SetHeartRateAsync (user toggles and the restart after a re-attach).
    private readonly SemaphoreSlim _heartRateGate = new(1, 1);
    // Serializes Hearing Aid and Media Assist changes: each computes the shared Hearing Assist (0x33) from the state.
    private readonly SemaphoreSlim _hearingGate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly Lock _stateLock = new();
    private TaskCompletionSource? _firstPacket;
    private TaskCompletionSource? _firstSetting;
    private TaskCompletionSource? _keysReply;
    private TaskCompletionSource? _heartRateReading;
    private CancellationTokenSource? _heartRateCts;
    private Task _heartRateTask = Task.CompletedTask;
    private Task<SessionEnd> _readLoop = Task.FromResult(SessionEnd.Closed);
    private byte _streamSequence;
    // Reported capability set last queued for settings (read loop and initialization).
    private DeviceCapabilities _savedReported;
    // Pod-saved settings last queued for settings; guarded by _stateLock.
    private PodSettings _savedPodSettings;
    // True while WatchListeningModesAsync runs; guarded by _stateLock.
    private bool _watchingModes;
    private bool _callAudio;
    // Settings writes run on the save task, never on the read loop (which also carries mic audio) or under _stateLock.
    // One pending save per kind, so a burst is one write and the last value wins. Guarded by _saveLock.
    private readonly Lock _saveLock = new();
    private Dictionary<string, Action> _pendingSaves = [];
    private bool _saveRunning;
    private Task _saves = Task.CompletedTask;
    // Set before _life is cancelled so the read loop ends with NoInitialReport.
    private bool _noInitialReport;
    // Heart rate ran in the session this one replaced (CarryOver): started again once Ready.
    private bool _resumeHeartRate;
    // Read loop: 0x58 mic audio packets are logged as one count per second.
    private int _audioCount;
    private long _audioWindowStart;

    /// <param name="productId">The pair's Bluetooth product ID (<see cref="LinkDevice.ProductId"/>), 0 when unknown.</param>
    internal AapSession(ulong address, NamedPipeClientStream stream, SettingsStore settings, ushort productId)
    {
        Address = address;
        _stream = stream;
        _settings = settings;
        // The AirPods send 0x1D (model) only once this PC is their active device, which can take half a minute; until
        // then the stored or product-ID model is used but not saved.
        string? modelNumber = settings.GetModel(address)?.Number ?? Models.ModelNumberOf(productId);
        DeviceCapabilities baseline = Models.FromModelNumber(modelNumber)?.Capabilities ?? DeviceCapabilities.None;
        _savedPodSettings = settings.GetPodSettings(address);
        State = new DeviceState(settings.GetCapabilities(address), baseline, _savedPodSettings) { ModelNumber = modelNumber };
        _savedReported = State.Reported;
        // The AirPods never report 0x0A, 0x01 or 0x34, so the stored values are the starting state and are written on
        // attach; a device report of 0x0A or 0x01 still replaces them.
        State.SetSetting(Aap.EarDetectionId, Aap.Bool(settings.GetEarDetection(address)));
        State.SetSetting(Aap.MicSideId, (byte)settings.GetMicSide(address));
        State.SetSetting(Aap.AllowOffId, Aap.Bool(settings.GetAllowOff(address)));
    }

    public ulong Address { get; }

    public DeviceState State { get; }

    /// <summary>Every inbound packet (including 0x58 mic audio), before it is parsed.</summary>
    public event PacketHandler? PacketReceived;

    /// <summary>At most one per inbound packet or command; never raised when nothing changed.</summary>
    public event Action<DeviceChange>? Changed;

    /// <summary>Call audio (Hands-Free) starts being set up on this link (true) or ends; raised on the read loop.</summary>
    public event Action<bool>? CallAudioChanged;

    /// <summary>Call audio is being set up or is up on this link.</summary>
    public bool CallAudio => Volatile.Read(ref _callAudio);

    internal Task<SessionEnd> Completion => _readLoop;

    /// <summary>
    /// Automatic re-attach, before <see cref="Start"/>: shows the previous session's battery and ear state until this
    /// one reports its own, and restarts heart rate once Ready if it was on.
    /// </summary>
    internal void CarryOver(AapSession previous)
    {
        State.CarryOver(previous.State);
        _resumeHeartRate = previous.State.HeartRate != HeartRateStatus.Off;
    }

    internal void Start()
    {
        _readLoop = ReadLoopAsync();
        _ = InitializeAsync();
    }

    /// <summary>False when the session is closed or the write failed or timed out; throws only when <paramref name="ct"/> is cancelled.</summary>
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
            timeout.CancelAfter(WriteTimeout);
            await _stream.WriteAsync(packet, timeout.Token).ConfigureAwait(false);
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

    /// <summary>The newest request stays shown until reported back, so echoes of earlier requests cause no flicker.</summary>
    public async ValueTask<bool> SetListeningModeAsync(ListeningMode mode, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        int id;
        lock (_stateLock)
        {
            id = State.PushListeningMode((byte)mode);
            if (!_watchingModes)
            {
                _watchingModes = true;
                _ = WatchListeningModesAsync();
            }
        }
        Log.Info("aap", $"listening mode {mode} requested");
        byte[] packet = Aap.Setting(Aap.ListeningModeId, (byte)mode);
        bool written;
        try
        {
            written = await WriteAsync(packet, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DropListeningMode(id);
            throw;
        }
        if (!written)
        {
            DropListeningMode(id);
            return false;
        }
        lock (_stateLock)
        {
            // Optimistic update, unless a newer request was made meanwhile.
            if (State.IsNewestListeningMode(id) && Aap.ApplySetting(packet, State) is var change && change != DeviceChange.None)
            {
                Changed?.Invoke(change);
                SavePodSettings(change);
            }
        }
        return true;
    }

    /// <summary>Writes 0x0A; once written it is stored and written again on every attach.</summary>
    public async ValueTask<bool> SetEarDetectionAsync(bool on, CancellationToken ct = default)
    {
        if (!await SendSettingAsync(Aap.Setting(Aap.EarDetectionId, Aap.Bool(on)), ct).ConfigureAwait(false))
        {
            return false;
        }
        SaveEarDetection(on);
        return true;
    }

    public ValueTask<bool> SetConversationAwarenessAsync(bool on, CancellationToken ct = default) =>
        SendSettingAsync(Aap.Setting(Aap.ConversationAwarenessId, Aap.Bool(on)), ct);

    public ValueTask<bool> SetPersonalizedVolumeAsync(bool on, CancellationToken ct = default) =>
        SendSettingAsync(Aap.Setting(Aap.PersonalizedVolumeId, Aap.Bool(on)), ct);

    public ValueTask<bool> SetOneBudAncAsync(bool on, CancellationToken ct = default) =>
        SendSettingAsync(Aap.Setting(Aap.OneBudAncId, Aap.Bool(on)), ct);

    /// <summary>
    /// Writes 0x34; once written it is stored and written again on every attach. Off also drops Off from the stem cycle
    /// and moves an Off listening mode to Transparency.
    /// </summary>
    public async ValueTask<bool> SetAllowOffAsync(bool on, CancellationToken ct = default)
    {
        if ((State.Capabilities & DeviceCapabilities.AllowOff) == 0)
        {
            throw new InvalidOperationException("This AirPods device has no Allow Off setting.");
        }
        if (!await SendSettingAsync(Aap.Setting(Aap.AllowOffId, Aap.Bool(on)), ct).ConfigureAwait(false))
        {
            return false;
        }
        SaveAllowOff(on);
        if (!on)
        {
            await EnforceAllowOffAsync(ct).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Writes 0x01; once written it is stored and written again on every attach.</summary>
    public async ValueTask<bool> SetMicSideAsync(MicSide side, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(side))
        {
            throw new ArgumentOutOfRangeException(nameof(side));
        }
        if (!await SendSettingAsync(Aap.Setting(Aap.MicSideId, (byte)side), ct).ConfigureAwait(false))
        {
            return false;
        }
        SaveMicSide(side);
        return true;
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

    /// <summary>Writes the stem press-and-hold cycle (0x1A); once written it is stored and written again on every attach.</summary>
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

    public Task<bool> SetHearingAidAsync(bool on, CancellationToken ct = default) => SetHearingModesAsync(on, null, null, ct);

    public Task<bool> SetMediaAssistCallsAsync(bool on, CancellationToken ct = default) => SetHearingModesAsync(null, on, null, ct);

    public Task<bool> SetMediaAssistMediaAsync(bool on, CancellationToken ct = default) => SetHearingModesAsync(null, null, on, ct);

    /// <summary>
    /// Hearing Aid (0x2C) and Media Assist (0x53) share Hearing Assist (0x33), on while any is on; null keeps the shown
    /// value. Turning 0x33 on writes the others first, turning it off writes 0x33 first, 300 ms apart.
    /// </summary>
    private async Task<bool> SetHearingModesAsync(bool? hearingAid, bool? calls, bool? media, CancellationToken ct)
    {
        if (State.HearingAidEnrolled != true)
        {
            throw new InvalidOperationException("Hearing Aid has not been reported as configured on this AirPods device.");
        }
        await _hearingGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DeviceState s = State;
            byte[]? record = s.MediaAssistRecord;
            if (record is null && (calls is not null || media is not null))
            {
                throw new InvalidOperationException("Media Assist has not been reported by this AirPods device.");
            }
            bool aid = hearingAid ?? s.HearingAidEnabled;
            bool assistCalls = calls ?? s.MediaAssistCalls == true;
            bool assistMedia = media ?? s.MediaAssistMedia == true;
            List<byte[]> modes = [];
            if (s.HearingAidEnabledByte != Aap.Bool(aid))
            {
                modes.Add(Aap.HearingAid(aid));
            }
            if (record is not null
                && (record[Aap.MediaAssistCallsOffset] != Aap.Bool(assistCalls) || record[Aap.MediaAssistMediaOffset] != Aap.Bool(assistMedia)))
            {
                modes.Add(Aap.MediaAssist(record, assistCalls, assistMedia));
            }
            bool assist = aid || assistCalls || assistMedia;
            Log.Info("aap", $"hearing modes requested: hearing aid {aid}, media assist calls {assistCalls}, media {assistMedia}");
            if (s.HearingAssist == assist)
            {
                return await SendHearingModesAsync(modes, ct).ConfigureAwait(false);
            }
            byte[] hearingAssist = Aap.Setting(Aap.HearingAssistId, Aap.Bool(assist));
            if (assist)
            {
                if (!await SendHearingModesAsync(modes, ct).ConfigureAwait(false))
                {
                    return false;
                }
                if (modes.Count > 0)
                {
                    await Task.Delay(300, ct).ConfigureAwait(false);
                }
                return await SendSettingAsync(hearingAssist, ct).ConfigureAwait(false);
            }
            if (!await SendSettingAsync(hearingAssist, ct).ConfigureAwait(false))
            {
                return false;
            }
            if (modes.Count > 0)
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            return await SendHearingModesAsync(modes, ct).ConfigureAwait(false);
        }
        finally
        {
            _hearingGate.Release();
        }
    }

    // 0x2C settings and 0x53 records in order; false at the first one not written.
    private async ValueTask<bool> SendHearingModesAsync(List<byte[]> packets, CancellationToken ct)
    {
        foreach (byte[] packet in packets)
        {
            bool written = Aap.IsSetting(packet)
                ? await SendSettingAsync(packet, ct).ConfigureAwait(false)
                : await SendMediaAssistAsync(packet, ct).ConfigureAwait(false);
            if (!written)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Starts (3 attempts of 12 s each for a first reading) or stops the heart-rate stream. Calls are serialized and a
    /// stop awaits any running start, so the last call wins.
    /// </summary>
    public async Task<bool> SetHeartRateAsync(bool on)
    {
        if (on && !State.HeartRateSupported)
        {
            throw new InvalidOperationException("Heart-rate streaming is only enabled for a device that reports AirPods Pro 3 support.");
        }
        await _heartRateGate.WaitAsync().ConfigureAwait(false);
        try
        {
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
        finally
        {
            _heartRateGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Log.Info("aap", $"close session {Mac.Format(Address)}");
        try
        {
            await _life.CancelAsync().ConfigureAwait(false);
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Error("aap", "read loop failed", e);
            }
            if (_audioCount > 0)
            {
                Log.Info("aap", $"RX 0x58 audio x{_audioCount} in the last {Environment.TickCount64 - _audioWindowStart} ms");
                _audioCount = 0;
            }
            try
            {
                await _heartRateTask.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Error("aap", "heart rate task failed", e);
            }
            // Queued settings writes finish before the session is gone (RunSaves logs its own errors).
            Task saves;
            lock (_saveLock)
            {
                saves = _saves;
            }
            await saves.ConfigureAwait(false);
        }
        finally
        {
            // Always: the service allows one client per channel (170), a leaked pipe fails every re-attach.
            _heartRateCts?.Dispose();
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
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
                if (_readBuffer[0] == ServicePipes.AapEnded)
                {
                    // The service closes the pipe after this message.
                    if (count < 5)
                    {
                        Log.Warn("aap", "read: channel ended, no error code");
                        return SessionEnd.Failed;
                    }
                    int error = BinaryPrimitives.ReadInt32LittleEndian(_readBuffer.AsSpan(1));
                    if (error == Native.ErrorDeviceNotConnected)
                    {
                        Log.Warn("aap", "read: channel ended, 1167 (device not connected)");
                        return SessionEnd.LinkLost;
                    }
                    Log.Warn("aap", $"read: channel ended, Win32 error {error}{(error == 0 ? " (closed by the AirPods)" : "")}");
                    return SessionEnd.Failed;
                }
                if (_readBuffer[0] == ServicePipes.AapCallAudio && count >= 2)
                {
                    bool on = _readBuffer[1] != 0;
                    Volatile.Write(ref _callAudio, on);
                    Log.Info("aap", $"call audio {(on ? "starting" : "ended")} on the link");
                    try
                    {
                        CallAudioChanged?.Invoke(on);
                    }
                    catch (Exception e)
                    {
                        Log.Error("aap", "call audio handler failed", e);
                    }
                    continue;
                }
                if (_readBuffer[0] != ServicePipes.AapPacket)
                {
                    Log.Warn("aap", $"read: unknown message kind 0x{_readBuffer[0]:X2} ({count} bytes), ignored");
                    continue;
                }
                try
                {
                    OnPacket(new ReadOnlySpan<byte>(_readBuffer, 1, count - 1));
                }
                catch (Exception e)
                {
                    // A throwing handler must not end the session with its stream still open.
                    Log.Error("aap", "packet handler failed", e);
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return _noInitialReport ? SessionEnd.NoInitialReport : SessionEnd.Closed;
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
        bool isSetting = Aap.IsSetting(packet);
        _firstPacket?.TrySetResult();
        if (isSetting)
        {
            _firstSetting?.TrySetResult();
        }
        if (Aap.TryParseKeys(packet, out ProximityKeys? keys))
        {
            OnKeys(keys);
        }
        DeviceChange change;
        lock (_stateLock)
        {
            change = Aap.Apply(packet, State);
            if (change != DeviceChange.None)
            {
                Changed?.Invoke(change);
                SavePodSettings(change);
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
        if (isSetting && packet[6] == Aap.EarDetectionId && packet[7] is 0x01 or 0x02)
        {
            Log.Info("aap", $"device reports ear detection {(packet[7] == 0x01 ? "on" : "off")}; stored");
            SaveEarDetection(packet[7] == 0x01);
        }
        // Mic side (0x01): 00 Automatic, 01 Right, 02 Left.
        if (isSetting && packet[6] == Aap.MicSideId && packet[7] <= 0x02)
        {
            Log.Info("aap", $"device reports mic side {(MicSide)packet[7]}; stored");
            SaveMicSide((MicSide)packet[7]);
        }
    }

    // DeviceChange.Keys is raised by the save, once the store has the keys.
    private void OnKeys(ProximityKeys? keys)
    {
        _keysReply?.TrySetResult();
        if (keys is null)
        {
            Log.Warn("aap", "keys reply without IRK and ENC_KEY");
            return;
        }
        QueueSave("keys", () =>
        {
            bool stored = _settings.SetKeys(Address, keys);
            Log.Info("aap", stored ? "keys reply: stored (new or changed)" : "keys reply: same as stored");
            if (stored)
            {
                lock (_stateLock)
                {
                    Changed?.Invoke(DeviceChange.Keys);
                }
            }
        });
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

    // Handshake -> any reply -> 100 ms -> feature flags -> notifications (filter carries the stored ear detection)
    // -> 1 s -> allow off -> stem modes -> ear detection -> mic side -> proximity keys (one retry).
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
                Log.Warn("aap", "init: handshake not written or no packet within 10 s; closing the session");
                SetStatus(SessionStatus.NoResponse);
                await CloseWithoutInitialReportAsync(ct).ConfigureAwait(false);
                return;
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
            if (!await WriteAsync(Aap.FeatureFlags, ct).ConfigureAwait(false)
                || !await WriteAsync(Aap.RequestNotifications(_settings.GetEarDetection(Address)), ct).ConfigureAwait(false))
            {
                Log.Warn("aap", "init: feature flags / notification request not written; closing the session");
                await CloseWithoutInitialReportAsync(ct).ConfigureAwait(false);
                return;
            }
            SetStatus(SessionStatus.Ready);
            _ = FinishInitialReportAsync(firstSetting.Task, ct);
            if (_resumeHeartRate)
            {
                Log.Info("aap", "init: starting heart rate again (it ran in the session this one replaced)");
                try
                {
                    await SetHeartRateAsync(true).ConfigureAwait(false);
                }
                catch (InvalidOperationException e)
                {
                    Log.Warn("aap", $"init: heart rate not started: {e.Message}");
                }
            }
            await Task.Delay(InitialReportWindow, ct).ConfigureAwait(false);
            if ((State.Capabilities & DeviceCapabilities.AllowOff) != 0)
            {
                bool allowOff = _settings.GetAllowOff(Address);
                Log.Info("aap", $"init: writing allow off {(allowOff ? "on" : "off")}");
                if (!await SendSettingAsync(Aap.Setting(Aap.AllowOffId, Aap.Bool(allowOff)), ct).ConfigureAwait(false))
                {
                    Log.Warn("aap", "init: allow off not written");
                }
            }
            await WriteStemModesOnAttachAsync(ct).ConfigureAwait(false);
            bool earDetection = _settings.GetEarDetection(Address);
            Log.Info("aap", $"init: writing ear detection {(earDetection ? "on" : "off")}");
            if (!await SendSettingAsync(Aap.Setting(Aap.EarDetectionId, Aap.Bool(earDetection)), ct).ConfigureAwait(false))
            {
                Log.Warn("aap", "init: ear detection not written");
            }
            MicSide micSide = _settings.GetMicSide(Address);
            Log.Info("aap", $"init: writing mic side {micSide}");
            if (!await SendSettingAsync(Aap.Setting(Aap.MicSideId, (byte)micSide), ct).ConfigureAwait(false))
            {
                Log.Warn("aap", "init: mic side not written");
            }
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

    // Ends the initial report ("checking" off) InitialReportWindow after the first 0x09 setting, at the earliest that
    // long after Ready. Without one within InitialReportTimeout the session closes itself.
    private async Task FinishInitialReportAsync(Task firstSetting, CancellationToken ct)
    {
        try
        {
            long start = Environment.TickCount64;
            if (!await WaitAsync(firstSetting, InitialReportTimeout, ct).ConfigureAwait(false))
            {
                Log.Warn("aap", $"init: no 0x09 setting within {InitialReportTimeout.TotalSeconds:0} s; closing the session");
                await CloseWithoutInitialReportAsync(ct).ConfigureAwait(false);
                return;
            }
            Log.Info("aap", $"init: first 0x09 setting {Environment.TickCount64 - start} ms after Ready");
            await Task.Delay(InitialReportWindow, ct).ConfigureAwait(false);
            lock (_stateLock)
            {
                State.FinishInitialReport();
                Changed?.Invoke(DeviceChange.InitialReport);
                SavePodSettings(DeviceChange.InitialReport);
            }
            SaveCapabilities();
        }
        catch (OperationCanceledException)
        {
            // Session closed.
        }
    }

    // The read loop then ends with NoInitialReport and the session is reopened; a session already closing stays Closed.
    private async Task CloseWithoutInitialReportAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _noInitialReport = true;
        await _life.CancelAsync().ConfigureAwait(false);
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

    // Timing: init packets with 180/220 ms gaps, power-on, 120 ms, stream 0x54, 120 ms, stream 0x13.
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

    // Drops sent listening modes after DeviceState.ListeningModeQuietMs without a request or report.
    private async Task WatchListeningModesAsync()
    {
        while (true)
        {
            long wait;
            lock (_stateLock)
            {
                (bool changed, wait) = State.ExpireListeningModes();
                if (changed)
                {
                    Changed?.Invoke(DeviceChange.ListeningMode);
                    SavePodSettings(DeviceChange.ListeningMode);
                }
                if (wait <= 0)
                {
                    _watchingModes = false;
                    return;
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(wait), _life.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Session closed.
                return;
            }
        }
    }

    private void DropListeningMode(int id)
    {
        lock (_stateLock)
        {
            if (State.DropListeningMode(id))
            {
                Changed?.Invoke(DeviceChange.ListeningMode);
                SavePodSettings(DeviceChange.ListeningMode);
            }
        }
    }

    // iOS writes 0x1A on every reconnect and the AirPods never report it on attach, so the stored mask is written.
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
            // Allow Off already reported off: Off leaves the cycle. Not reported yet: the report fixes it later.
            bool dropOff = (modes & StemModes.Off) != 0 && (capabilities & DeviceCapabilities.AllowOff) != 0 && State.AllowOff == false;
            if (dropOff)
            {
                modes = WithoutOff(modes);
                Log.Info("aap", $"init: allow off is off; Off dropped from stem modes {stored}");
            }
            Log.Info("aap", $"init: writing stem modes {modes} (0x{(byte)modes:X2})");
            if (!await WriteStemModesAsync(modes, ct).ConfigureAwait(false))
            {
                Log.Warn("aap", "init: stem modes not written");
            }
            else if (dropOff)
            {
                SaveStemModes(modes);
            }
        }
        finally
        {
            _stemGate.Release();
        }
    }

    // Writes 0x1A only; 0x34 (allow Off) is its own setting (SetAllowOffAsync), never written with the cycle.
    private ValueTask<bool> WriteStemModesAsync(StemModes modes, CancellationToken ct) =>
        SendSettingAsync(Aap.Setting(Aap.StemModesId, (byte)modes), ct);

    // Allow Off is off: Off leaves the cycle; one mode left gets Transparency, else Noise Cancellation.
    private static StemModes WithoutOff(StemModes modes)
    {
        modes &= ~StemModes.Off;
        if (!Aap.IsValidStemModes(modes))
        {
            modes |= (modes & StemModes.Transparency) == 0 ? StemModes.Transparency : StemModes.NoiseCancellation;
        }
        return modes;
    }

    private async Task EnforceAllowOffAsync(CancellationToken ct)
    {
        await _stemGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StemModes stored = _settings.GetStemModes(Address);
            if ((stored & StemModes.Off) != 0)
            {
                StemModes modes = WithoutOff(stored);
                Log.Info("aap", $"allow off is off; stem modes {stored} -> {modes} (0x{(byte)modes:X2})");
                if (await WriteStemModesAsync(modes, ct).ConfigureAwait(false))
                {
                    SaveStemModes(modes);
                }
                else
                {
                    Log.Warn("aap", "allow off: stem modes not written");
                }
            }
        }
        finally
        {
            _stemGate.Release();
        }
        if (State.ListeningMode == ListeningMode.Off && !await SetListeningModeAsync(ListeningMode.Transparency, ct).ConfigureAwait(false))
        {
            Log.Warn("aap", "allow off: listening mode Transparency not written");
        }
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
                SavePodSettings(change);
            }
        }
        return true;
    }

    // Writes a 0x53 record, then applies it as the device report would (optimistic update; the AirPods do not echo it).
    private async ValueTask<bool> SendMediaAssistAsync(byte[] packet, CancellationToken ct)
    {
        if (!await WriteAsync(packet, ct).ConfigureAwait(false))
        {
            return false;
        }
        lock (_stateLock)
        {
            DeviceChange change = Aap.Apply(packet, State);
            if (change != DeviceChange.None)
            {
                Changed?.Invoke(change);
                SavePodSettings(change);
            }
        }
        return true;
    }

    private void SaveEarDetection(bool on) => QueueSave("ear detection", () => _settings.SetEarDetection(Address, on));

    private void SaveAllowOff(bool on) => QueueSave("allow off", () => _settings.SetAllowOff(Address, on));

    private void SaveMicSide(MicSide side) => QueueSave("mic side", () => _settings.SetMicSide(Address, side));

    // Stores a cycle the session changed on its own and raises DeviceChange.StemModes.
    private void SaveStemModes(StemModes modes)
    {
        try
        {
            _settings.SetStemModes(Address, modes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The device has it; the next attach writes the previously stored mask again.
            Log.Warn("aap", $"stem modes not saved: {e.Message}");
            return;
        }
        lock (_stateLock)
        {
            Changed?.Invoke(DeviceChange.StemModes);
        }
    }

    // Under _stateLock. Remembers pod settings for display on the next attach, only once the initial report ended
    // (earlier values may be partial).
    private void SavePodSettings(DeviceChange change)
    {
        if (!State.InitialReportDone || (change & (DeviceChange.ListeningMode | DeviceChange.Settings | DeviceChange.InitialReport)) == 0)
        {
            return;
        }
        PodSettings settings = State.GetPodSettings(_savedPodSettings);
        if (settings == _savedPodSettings)
        {
            return;
        }
        _savedPodSettings = settings;
        QueueSave("pod settings", () => _settings.SetPodSettings(Address, settings));
    }

    // Refreshes the pair's remembered (reported) capabilities; SettingsStore skips the write when unchanged.
    private void SaveCapabilities()
    {
        DeviceCapabilities reported = State.Reported;
        _savedReported = reported;
        QueueSave("capabilities", () => _settings.SetCapabilities(Address, reported));
    }

    private void QueueSave(string what, Action save)
    {
        lock (_saveLock)
        {
            _pendingSaves[what] = save;
            if (!_saveRunning)
            {
                _saveRunning = true;
                _saves = Task.Run(RunSaves);
            }
        }
    }

    // One run at a time, so saves of one kind stay in order; never throws (DisposeAsync awaits it). A failed value
    // stays in SettingsStore memory and goes out with its next file write.
    private void RunSaves()
    {
        while (true)
        {
            Dictionary<string, Action> saves;
            lock (_saveLock)
            {
                if (_pendingSaves.Count == 0)
                {
                    _saveRunning = false;
                    return;
                }
                saves = _pendingSaves;
                _pendingSaves = [];
            }
            foreach ((string what, Action save) in saves)
            {
                try
                {
                    save();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("aap", $"{what} not saved: {e.Message}");
                }
                catch (Exception e)
                {
                    Log.Error("aap", $"{what} not saved", e);
                }
            }
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
