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
    Ear = 1 << 3,
    ListeningMode = 1 << 4,
    /// <summary>Any device-side setting other than the listening mode.</summary>
    Settings = 1 << 5,
    ConversationLevel = 1 << 6,
    HeartRate = 1 << 7,
    /// <summary>Proximity keys were received and differed from the stored ones.</summary>
    Keys = 1 << 8,
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

    public SessionStatus Status { get; internal set; }

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

    public ListeningMode? ListeningMode => _settings[Aap.ListeningModeId] is >= 1 and <= 4 and var v ? (ListeningMode)v : null;
    public bool? EarDetection => Flag(Aap.EarDetectionId);
    public bool? ConversationAwareness => Flag(Aap.ConversationAwarenessId);
    public bool? PersonalizedVolume => Flag(Aap.PersonalizedVolumeId);
    public bool? AllowOff => Flag(Aap.AllowOffId);
    public bool? OneBudAnc => Flag(Aap.OneBudAncId);
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

    /// <summary>Heart rate may be started: an AirPods Pro 3 model number, or a reading already seen.</summary>
    public bool HeartRateSupported =>
        HeartRateAvailable
        || (ModelNumber is { } model
            && (model.Contains("A3063", StringComparison.OrdinalIgnoreCase)
                || model.Contains("A3064", StringComparison.OrdinalIgnoreCase)
                || model.Contains("A3065", StringComparison.OrdinalIgnoreCase)));

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
