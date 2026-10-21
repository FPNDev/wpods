namespace WPods.Core;

/// <summary>What a single <see cref="AapSession.Changed"/> notification changed.</summary>
[Flags]
public enum DeviceChange
{
    None = 0,
    /// <summary><see cref="DeviceState.Status"/> changed.</summary>
    Connection = 1 << 0,
    /// <summary>Name / model (AAP 0x1D).</summary>
    Identity = 1 << 1,
    Battery = 1 << 2,
    /// <summary>Primary/secondary ear status or the left/right mapping (<see cref="DeviceState.LeftEar"/>, <see cref="DeviceState.RightEar"/>).</summary>
    Ear = 1 << 3,
    ListeningMode = 1 << 4,
    /// <summary>Any device-side setting other than the listening mode.</summary>
    Settings = 1 << 5,
    ConversationLevel = 1 << 6,
    HeartRate = 1 << 7,
    /// <summary>Proximity keys were received and differed from the stored ones.</summary>
    Keys = 1 << 8,
    /// <summary><see cref="DeviceState.Capabilities"/> changed.</summary>
    Capabilities = 1 << 9,
    /// <summary>The initial report window after attach ended (<see cref="DeviceState.InitialReportDone"/> is now true).</summary>
    InitialReport = 1 << 10,
}

/// <summary>
/// Features of an AirPods pair: reported over AAP (setting ids in 0x09 packets, heart-rate readings) or in the
/// baseline of its model (<see cref="AirPodsModel.Capabilities"/>). Features in neither are not shown.
/// </summary>
[Flags]
public enum DeviceCapabilities
{
    None = 0,
    /// <summary>Listening mode 0x0D: Off, Noise Cancellation, Transparency.</summary>
    ListeningMode = 1 << 0,
    /// <summary>Adaptive listening mode: adaptive strength 0x2E reported, or listening mode 4 seen.</summary>
    Adaptive = 1 << 1,
    EarDetection = 1 << 2,
    ConversationAwareness = 1 << 3,
    OneBudAnc = 1 << 4,
    PersonalizedVolume = 1 << 5,
    AllowOff = 1 << 6,
    MicSide = 1 << 7,
    PressSpeed = 1 << 8,
    HoldDuration = 1 << 9,
    ToneVolume = 1 << 10,
    /// <summary>Hearing aid 0x2C reported (enrollment is <see cref="DeviceState.HearingAidEnrolled"/>).</summary>
    HearingAid = 1 << 11,
    /// <summary>Heart-rate state 0x30 reported, or a heart-rate reading arrived.</summary>
    HeartRate = 1 << 12,
}

public enum SessionStatus
{
    Initializing,
    Ready,
    /// <summary>The device did not answer the handshake within 10 s.</summary>
    NoResponse,
}

public enum ListeningMode : byte
{
    Off = 1,
    NoiseCancellation = 2,
    Transparency = 3,
    Adaptive = 4,
}

public enum MicSide : byte
{
    Automatic = 0,
    Right = 1,
    Left = 2,
}

/// <summary>Press speed (setting 0x17).</summary>
public enum PressSpeed : byte
{
    Default = 0,
    Slower = 1,
    Slowest = 2,
}

/// <summary>Press-and-hold duration (setting 0x18).</summary>
public enum HoldDuration : byte
{
    Default = 0,
    Shorter = 1,
    Shortest = 2,
}

/// <summary>Listening modes the stem press-and-hold cycles through (setting 0x1A bitmask; at least two).</summary>
[Flags]
public enum StemModes : byte
{
    None = 0,
    Off = 0x01,
    NoiseCancellation = 0x02,
    Transparency = 0x04,
    Adaptive = 0x08,
}

public enum EarStatus : byte
{
    InEar,
    OutOfEar,
    InCase,
    Disconnected,
    Unknown,
}

public enum BatteryStatus : byte
{
    Unknown,
    Charging,
    Discharging,
    Disconnected,
}

/// <summary>One battery component; <c>default</c> = not reported / absent.</summary>
public readonly record struct Battery(byte? Percent, BatteryStatus Status);

public enum HeartRateStatus
{
    Off,
    Starting,
    Streaming,
    /// <summary>No sensor reading after every start attempt.</summary>
    NoReading,
    /// <summary>A start packet could not be written.</summary>
    Failed,
}

/// <summary>
/// Device-reported state of one attached session. Written only under the session's lock (read loop or
/// command completion); every field read is atomic, so consumers may read it from any thread.
/// </summary>
public sealed class DeviceState
{
    private const byte NotReported = 0xFF;
    private const int Left = 0;
    private const int Right = 1;
    private const int Case = 2;
    private const int Headphone = 3;

    // Indexed by AAP control-setting id (0x09 packets); NotReported until the device reports it.
    private readonly byte[] _settings = CreateSettings();
    private readonly Battery[] _batteries = new Battery[4];
    private byte _hearingAidEnabled = NotReported;
    private DeviceCapabilities _observed;
    private DeviceCapabilities _baseline;
    // AAP battery component of the primary bud (Aap.LeftComponent / RightComponent / HeadphoneComponent); 0 = unknown.
    private byte _primaryPod;
    private bool _budRoleSeen;
    // Listening-mode hold (anti-flicker): the mode WPods last requested, until the device reports it or the hold
    // expires; device reports of other modes are not applied meanwhile. 0 = no hold.
    private byte _heldMode;
    private long _heldUntil;
    private int _holdSerial;
    // Latest listening mode the device reported (applied or not); 0 = none yet.
    private byte _deviceMode;

    /// <param name="remembered">Reported on the last attach.</param>
    /// <param name="baseline">Baseline of the last known model.</param>
    internal DeviceState(DeviceCapabilities remembered, DeviceCapabilities baseline)
    {
        Reported = remembered;
        _baseline = baseline;
        Capabilities = remembered | baseline;
    }

    public SessionStatus Status { get; internal set; }

    /// <summary><see cref="Reported"/> plus the baseline of the model.</summary>
    public DeviceCapabilities Capabilities { get; private set; }

    /// <summary>
    /// Until the initial report ends: the set remembered from the last attach plus what this attach reported so far.
    /// Afterwards: exactly what this attach reported (growing if the device reports more later). This part is persisted.
    /// </summary>
    internal DeviceCapabilities Reported { get; private set; }

    /// <summary>The 1 s window after attach in which the device reports its state has ended.</summary>
    public bool InitialReportDone { get; private set; }

    public string? Name { get; internal set; }
    public string? ModelNumber { get; internal set; }
    public string? ModelName { get; internal set; }

    public Battery LeftBattery => _batteries[Left];
    public Battery RightBattery => _batteries[Right];
    public Battery CaseBattery => _batteries[Case];
    /// <summary>Over-ear models (AirPods Max).</summary>
    public Battery HeadphoneBattery => _batteries[Headphone];

    public EarStatus? PrimaryEar { get; internal set; }
    public EarStatus? SecondaryEar { get; internal set; }

    /// <summary>Ear status of the left bud: primary or secondary, by which bud is primary; null while unknown or for over-ear models.</summary>
    public EarStatus? LeftEar => _primaryPod switch
    {
        Aap.LeftComponent => PrimaryEar,
        Aap.RightComponent => SecondaryEar,
        _ => null,
    };

    /// <summary>Ear status of the right bud; see <see cref="LeftEar"/>.</summary>
    public EarStatus? RightEar => _primaryPod switch
    {
        Aap.LeftComponent => SecondaryEar,
        Aap.RightComponent => PrimaryEar,
        _ => null,
    };

    public ListeningMode? ListeningMode => _settings[Aap.ListeningModeId] is >= 1 and <= 4 and var v ? (ListeningMode)v : null;
    public bool? EarDetection => Flag(Aap.EarDetectionId);
    public bool? ConversationAwareness => Flag(Aap.ConversationAwarenessId);
    public bool? PersonalizedVolume => Flag(Aap.PersonalizedVolumeId);
    public bool? AllowOff => Flag(Aap.AllowOffId);
    public bool? OneBudAnc => Flag(Aap.OneBudAncId);
    /// <summary>Stem press-and-hold cycle (0x1A) as last written by WPods or reported by the device; null until then.</summary>
    public StemModes? StemModes => _settings[Aap.StemModesId] is var v && v != NotReported ? (StemModes)v : null;
    public MicSide? MicSide => _settings[Aap.MicSideId] is <= 2 and var v ? (MicSide)v : null;
    public PressSpeed? PressSpeed => _settings[Aap.PressSpeedId] is <= 2 and var v ? (PressSpeed)v : null;
    public HoldDuration? HoldDuration => _settings[Aap.HoldDurationId] is <= 2 and var v ? (HoldDuration)v : null;
    public byte? ToneVolume => _settings[Aap.ToneVolumeId] is var v && v != NotReported ? Math.Min(v, (byte)100) : null;
    public bool? HearingAidEnrolled => Flag(Aap.HearingAidId);
    /// <summary>Hearing aid (0x2C enabled byte) and Hearing Assist (0x33) both on.</summary>
    public bool HearingAidEnabled => _hearingAidEnabled == 1 && _settings[Aap.HearingAssistId] == 1;

    /// <summary>Conversation-awareness level (0x4B byte 9).</summary>
    public byte? ConversationLevel { get; internal set; }

    public HeartRateStatus HeartRate { get; internal set; }
    public byte? HeartRateBpm { get; internal set; }
    public byte HeartRateConfidence { get; internal set; }
    /// <summary>A heart-rate reading arrived at least once in this session.</summary>
    public bool HeartRateAvailable { get; internal set; }

    /// <summary>Heart rate may be started: reported by the device or in its model baseline.</summary>
    public bool HeartRateSupported => (Capabilities & DeviceCapabilities.HeartRate) != 0;

    internal DeviceChange Observe(DeviceCapabilities capabilities)
    {
        _observed |= capabilities;
        Reported = InitialReportDone ? _observed : Reported | capabilities;
        return UpdateCapabilities();
    }

    /// <summary>Drops remembered capabilities this attach did not report.</summary>
    internal DeviceChange FinishInitialReport()
    {
        InitialReportDone = true;
        Reported = _observed;
        return UpdateCapabilities();
    }

    internal DeviceChange SetBaseline(DeviceCapabilities baseline)
    {
        _baseline = baseline;
        return UpdateCapabilities();
    }

    internal bool SetSetting(byte id, byte value)
    {
        if (_settings[id] == value)
        {
            return false;
        }
        _settings[id] = value;
        return true;
    }

    internal bool SetHearingAidEnabled(byte value)
    {
        if (_hearingAidEnabled == value)
        {
            return false;
        }
        _hearingAidEnabled = value;
        return true;
    }

    /// <summary>
    /// Sets the primary bud (AAP battery component). The 0x08 bud role, once seen, wins over the 0x04 battery order.
    /// Returns Ear when <see cref="LeftEar"/> or <see cref="RightEar"/> changed.
    /// </summary>
    internal DeviceChange SetPrimaryPod(byte component, bool fromBudRole)
    {
        if (!fromBudRole && _budRoleSeen)
        {
            if (component != _primaryPod)
            {
                Log.Warn("aap", $"0x04 battery order says primary={PodName(component)}, bud role 0x08 says {PodName(_primaryPod)}; keeping the bud role");
            }
            return DeviceChange.None;
        }
        _budRoleSeen |= fromBudRole;
        if (component == _primaryPod)
        {
            return DeviceChange.None;
        }
        EarStatus? left = LeftEar;
        EarStatus? right = RightEar;
        Log.Info("aap", $"primary bud {PodName(_primaryPod)} -> {PodName(component)} (source: {(fromBudRole ? "0x08 bud role" : "first pod in 0x04 battery packet")})");
        _primaryPod = component;
        return left == LeftEar && right == RightEar ? DeviceChange.None : DeviceChange.Ear;
    }

    /// <summary>Starts (or replaces) the listening-mode hold for <paramref name="mode"/>; returns its serial.</summary>
    internal int HoldListeningMode(byte mode, TimeSpan duration)
    {
        _heldMode = mode;
        _heldUntil = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        return ++_holdSerial;
    }

    /// <summary>
    /// A listening-mode report from the device. Not applied while a hold for another mode is active; the held mode
    /// itself ends the hold. Returns true when <see cref="ListeningMode"/> changed.
    /// </summary>
    internal bool ReportListeningMode(byte value)
    {
        _deviceMode = value;
        if (_heldMode != 0)
        {
            if (value != _heldMode && Environment.TickCount64 < _heldUntil)
            {
                Log.Info("aap", $"listening mode report {value} ignored: waiting for requested {_heldMode}");
                return false;
            }
            Log.Info("aap", value == _heldMode
                ? $"listening mode {value} confirmed by the device, hold ended"
                : $"listening mode hold for {_heldMode} expired, device reports {value}");
            _heldMode = 0;
        }
        return SetSetting(Aap.ListeningModeId, value);
    }

    /// <summary>
    /// Ends hold <paramref name="serial"/> if it is still the active one (timeout or failed write) and applies the
    /// device's latest reported mode. Returns true when <see cref="ListeningMode"/> changed.
    /// </summary>
    internal bool ReleaseListeningMode(int serial)
    {
        if (serial != _holdSerial || _heldMode == 0)
        {
            return false;
        }
        Log.Info("aap", $"listening mode hold for {_heldMode} released, device last reported {(_deviceMode == 0 ? "nothing" : _deviceMode.ToString())}");
        _heldMode = 0;
        return _deviceMode != 0 && SetSetting(Aap.ListeningModeId, _deviceMode);
    }

    /// <summary>True when hold <paramref name="serial"/> is still the latest request.</summary>
    internal bool IsCurrentHold(int serial) => serial == _holdSerial;

    /// <summary>Stores a battery entry for AAP component 01/02/04/08; false if unknown or unchanged.</summary>
    internal bool SetBattery(byte component, Battery battery)
    {
        int slot = component switch
        {
            0x01 => Headphone,
            0x02 => Right,
            0x04 => Left,
            0x08 => Case,
            _ => -1,
        };
        if (slot < 0 || _batteries[slot] == battery)
        {
            return false;
        }
        _batteries[slot] = battery;
        return true;
    }

    private DeviceChange UpdateCapabilities()
    {
        DeviceCapabilities capabilities = Reported | _baseline;
        if (Capabilities == capabilities)
        {
            return DeviceChange.None;
        }
        Capabilities = capabilities;
        return DeviceChange.Capabilities;
    }

    private static string PodName(byte component) => component switch
    {
        Aap.LeftComponent => "left",
        Aap.RightComponent => "right",
        Aap.HeadphoneComponent => "headphone",
        _ => "unknown",
    };

    private bool? Flag(byte id) => _settings[id] switch
    {
        NotReported => null,
        1 => true,
        _ => false,
    };

    private static byte[] CreateSettings()
    {
        byte[] settings = new byte[0x40];
        Array.Fill(settings, NotReported);
        return settings;
    }
}
