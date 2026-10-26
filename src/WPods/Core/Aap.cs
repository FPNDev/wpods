using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace WPods.Core;

/// <summary>Learned per device over AAP opcode 0x31; used to resolve and decrypt proximity adverts.</summary>
public sealed record ProximityKeys(byte[] Irk, byte[] EncKey);

/// <summary>AAP packet bytes and the inbound parser.</summary>
internal static class Aap
{
    // Control-setting ids (opcode 0x09).
    public const byte MicSideId = 0x01;
    public const byte EarDetectionId = 0x0A;
    public const byte ListeningModeId = 0x0D;
    public const byte PressSpeedId = 0x17;
    public const byte HoldDurationId = 0x18;
    /// <summary>Stem press-and-hold cycle bitmask (<see cref="StemModes"/>).</summary>
    public const byte StemModesId = 0x1A;
    public const byte OneBudAncId = 0x1B;
    public const byte ToneVolumeId = 0x1F;
    public const byte PersonalizedVolumeId = 0x26;
    public const byte ConversationAwarenessId = 0x28;
    public const byte HearingAidId = 0x2C;
    public const byte AdaptiveStrengthId = 0x2E;
    public const byte HeartRateStateId = 0x30;
    public const byte HearingAssistId = 0x33;
    public const byte AllowOffId = 0x34;

    // Battery components (0x04 packet); 0x08 = case.
    public const byte HeadphoneComponent = 0x01;
    public const byte RightComponent = 0x02;
    public const byte LeftComponent = 0x04;

    public const byte HeartRateService = 0x54;
    public const byte HeartRateLegacyService = 0x13;

    private const uint Header = 0x00040004; // 04 00 04 00
    private const ushort KeysOpcode = 0x31;

    public static readonly byte[] Handshake = [0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    public static readonly byte[] FeatureFlags = [0x04, 0x00, 0x04, 0x00, 0x4D, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    /// <summary>
    /// 0x0F notification filter. Byte 3 (packet byte 8) tells the buds the host's Ear detection setting like Apple
    /// hosts do: FF = on, FD = off (LibrePods AACPManager: "third byte is 0xfd when ear detection is disabled").
    /// </summary>
    public static byte[] RequestNotifications(bool earDetection) =>
        [0x04, 0x00, 0x04, 0x00, 0x0F, 0x00, 0xFF, 0xFF, earDetection ? (byte)0xFF : (byte)0xFD, 0xFF];
    /// <summary>Key mask 0x0005 = IRK | ENC_KEY; reply opcode 0x31.</summary>
    public static readonly byte[] RequestKeys = [0x04, 0x00, 0x04, 0x00, 0x30, 0x00, 0x05, 0x00];

    public static readonly byte[] HeartRateConnectService0 = [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    public static readonly byte[] HeartRateCapabilitiesService0 = [0x04, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00];
    public static readonly byte[] HeartRateConnectService4 = [0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    public static readonly byte[] HeartRateCapabilitiesService4 = [0x04, 0x00, 0x04, 0x00, 0x01, 0x00, 0x00];
    public static readonly byte[] HeartRatePowerOn = [0x04, 0x00, 0x04, 0x00, 0x09, 0x00, 0x30, 0x01, 0x00, 0x00, 0x00];

    public static byte Bool(bool on) => on ? (byte)0x01 : (byte)0x02;

    /// <summary>Only Off / NC / Transparency / Adaptive bits, at least two of them.</summary>
    public static bool IsValidStemModes(StemModes modes) =>
        (modes & ~(StemModes.Off | StemModes.NoiseCancellation | StemModes.Transparency | StemModes.Adaptive)) == 0
        && BitOperations.PopCount((uint)modes) >= 2;

    public static byte[] Setting(byte id, byte value, byte extra = 0x00) => [0x04, 0x00, 0x04, 0x00, 0x09, 0x00, id, value, extra, 0x00, 0x00];

    /// <summary>0x1F: byte 8 = 0x50, as AirPods Pro 2 report it (live: 09 00 1F 50 50 00 00).</summary>
    public static byte[] ToneVolume(byte volume) => Setting(ToneVolumeId, volume, 0x50);

    /// <summary>0x2C: byte 7 = enrolled (always 01 when sent), byte 8 = enabled.</summary>
    public static byte[] HearingAid(bool on) => Setting(HearingAidId, 0x01, Bool(on));

    public static byte[] HeartRateStream(byte sequence, byte service, uint periodUs) =>
    [
        0x04, 0x00, 0x04, 0x00, 0x17, 0x00, 0x00, 0x00, 0x10, 0x00, 0x11, 0x00,
        0x08, (byte)(sequence & 0x7F), 0x10, 0x02, 0x42, 0x0B, 0x08, service, 0x10, 0x02, 0x1A, 0x05, 0x01,
        (byte)periodUs, (byte)(periodUs >> 8), (byte)(periodUs >> 16), (byte)(periodUs >> 24),
    ];

    /// <summary>True for a 0x09 control-setting packet.</summary>
    public static bool IsSetting(ReadOnlySpan<byte> p) =>
        p.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(p) == Header && p[4] == 0x09 && p[5] == 0x00;

    /// <summary>Applies one inbound packet to <paramref name="state"/>; returns what changed.</summary>
    public static DeviceChange Apply(ReadOnlySpan<byte> p, DeviceState state)
    {
        if (p.Length < 6 || BinaryPrimitives.ReadUInt32LittleEndian(p) != Header)
        {
            return DeviceChange.None;
        }
        switch (p[4] | (p[5] << 8))
        {
            case 0x04:
                return p.Length >= 7 ? ApplyBattery(p, state) : DeviceChange.None;
            case 0x06:
                return p.Length >= 8 ? ApplyEar(p, state) : DeviceChange.None;
            case 0x08:
                // Bud role (apple-wireshark AACP dissector): 01 = left is primary, 02 = right is primary.
                return p.Length >= 7 && p[6] is 0x01 or 0x02
                    ? state.SetPrimaryPod(p[6] == 0x01 ? LeftComponent : RightComponent, fromBudRole: true)
                    : DeviceChange.None;
            case 0x09:
                if (p.Length < 8)
                {
                    return DeviceChange.None;
                }
                DeviceChange setting = p[6] == ListeningModeId
                    ? (state.ReportListeningMode(p[7]) ? DeviceChange.ListeningMode : DeviceChange.None)
                    : ApplySetting(p, state);
                return setting | state.Observe(CapabilityOf(p[6], p[7]));
            case 0x17:
                return ApplyHeartRate(p, state);
            case 0x1D:
                return p.Length >= 12 ? ApplyDeviceInfo(p, state) : DeviceChange.None;
            case 0x4B:
                if (p.Length < 10 || state.ConversationLevel == p[9])
                {
                    return DeviceChange.None;
                }
                state.ConversationLevel = p[9];
                return DeviceChange.ConversationLevel;
            default:
                return DeviceChange.None;
        }
    }

    /// <summary>Parses the 0x31 reply: count, then {type u16, length u16, key} entries.</summary>
    public static bool TryParseKeys(ReadOnlySpan<byte> p, out ProximityKeys? keys)
    {
        keys = null;
        if (p.Length < 7 || BinaryPrimitives.ReadUInt32LittleEndian(p) != Header || BinaryPrimitives.ReadUInt16LittleEndian(p[4..]) != KeysOpcode)
        {
            return false;
        }
        ReadOnlySpan<byte> irk = default;
        ReadOnlySpan<byte> encKey = default;
        int offset = 7;
        for (int i = 0; i < p[6] && offset + 4 <= p.Length; i++)
        {
            int type = BinaryPrimitives.ReadUInt16LittleEndian(p[offset..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(p[(offset + 2)..]);
            offset += 4;
            if (length > p.Length - offset)
            {
                break;
            }
            ReadOnlySpan<byte> key = p.Slice(offset, length);
            offset += length;
            if (length != 16)
            {
                continue;
            }
            if (type == 0x0001)
            {
                irk = key;
            }
            else if (type == 0x0004)
            {
                encKey = key;
            }
        }
        if (!irk.IsEmpty && !encKey.IsEmpty)
        {
            keys = new ProximityKeys(irk.ToArray(), encKey.ToArray());
        }
        return true;
    }

    private static DeviceChange ApplyBattery(ReadOnlySpan<byte> p, DeviceState state)
    {
        bool changed = false;
        // LibrePods: the first pod component in the packet is the primary bud.
        byte firstPod = 0;
        int count = p[6];
        for (int i = 0; i < count; i++)
        {
            int offset = 7 + (i * 5);
            if (offset + 4 >= p.Length)
            {
                break;
            }
            byte level = p[offset + 2];
            // Level FF = component absent; status 04 = disconnected (level meaningless); 01/05 = charging.
            Battery battery = level == 0xFF
                ? default
                : p[offset + 3] switch
                {
                    0x04 => new Battery(null, BatteryStatus.Disconnected),
                    0x01 or 0x05 => new Battery(Math.Min(level, (byte)100), BatteryStatus.Charging),
                    0x02 => new Battery(Math.Min(level, (byte)100), BatteryStatus.Discharging),
                    _ => new Battery(Math.Min(level, (byte)100), BatteryStatus.Unknown),
                };
            changed |= state.SetBattery(p[offset], battery);
            if (firstPod == 0 && p[offset] is LeftComponent or RightComponent or HeadphoneComponent)
            {
                firstPod = p[offset];
            }
        }
        DeviceChange change = changed ? DeviceChange.Battery : DeviceChange.None;
        return firstPod == 0 ? change : change | state.SetPrimaryPod(firstPod, fromBudRole: false);
    }

    private static DeviceChange ApplyEar(ReadOnlySpan<byte> p, DeviceState state)
    {
        EarStatus? primary = Ear(p[6], state.PrimaryEar);
        EarStatus? secondary = Ear(p[7], state.SecondaryEar);
        if (primary == state.PrimaryEar && secondary == state.SecondaryEar)
        {
            return DeviceChange.None;
        }
        state.PrimaryEar = primary;
        state.SecondaryEar = secondary;
        return DeviceChange.Ear;
    }

    // 04 = bud in motion (transitional): keep the previous state.
    private static EarStatus? Ear(byte value, EarStatus? previous) => value switch
    {
        0x00 => EarStatus.InEar,
        0x01 => EarStatus.OutOfEar,
        0x02 => EarStatus.InCase,
        0x03 => EarStatus.Disconnected,
        0x04 => previous,
        _ => EarStatus.Unknown,
    };

    /// <summary>Applies a 0x09 setting packet (inbound, or one WPods wrote) without recording capabilities.</summary>
    public static DeviceChange ApplySetting(ReadOnlySpan<byte> p, DeviceState state)
    {
        byte id = p[6];
        byte value = p[7];
        switch (id)
        {
            case ListeningModeId:
                return state.SetSetting(id, value) ? DeviceChange.ListeningMode : DeviceChange.None;
            case HearingAidId:
                if (p.Length < 9)
                {
                    return DeviceChange.None;
                }
                return state.SetSetting(id, value) | state.SetHearingAidEnabled(p[8]) ? DeviceChange.Settings : DeviceChange.None;
            case MicSideId or EarDetectionId or PressSpeedId or HoldDurationId or OneBudAncId or ToneVolumeId
                or PersonalizedVolumeId or ConversationAwarenessId or HearingAssistId or AllowOffId or StemModesId:
                return state.SetSetting(id, value) ? DeviceChange.Settings : DeviceChange.None;
            default:
                return DeviceChange.None;
        }
    }

    // Setting ids per LibrePods AACPManager.ControlCommandIdentifiers (0x2E AUTO_ANC_STRENGTH, 0x30 HRM_STATE).
    private static DeviceCapabilities CapabilityOf(byte id, byte value) => id switch
    {
        ListeningModeId when value == (byte)ListeningMode.Adaptive => DeviceCapabilities.ListeningMode | DeviceCapabilities.Adaptive,
        ListeningModeId => DeviceCapabilities.ListeningMode,
        AdaptiveStrengthId => DeviceCapabilities.Adaptive,
        EarDetectionId => DeviceCapabilities.EarDetection,
        ConversationAwarenessId => DeviceCapabilities.ConversationAwareness,
        OneBudAncId => DeviceCapabilities.OneBudAnc,
        PersonalizedVolumeId => DeviceCapabilities.PersonalizedVolume,
        AllowOffId => DeviceCapabilities.AllowOff,
        MicSideId => DeviceCapabilities.MicSide,
        PressSpeedId => DeviceCapabilities.PressSpeed,
        HoldDurationId => DeviceCapabilities.HoldDuration,
        ToneVolumeId => DeviceCapabilities.ToneVolume,
        HearingAidId => DeviceCapabilities.HearingAid,
        HeartRateStateId => DeviceCapabilities.HeartRate,
        _ => DeviceCapabilities.None,
    };

    private static DeviceChange ApplyDeviceInfo(ReadOnlySpan<byte> p, DeviceState state)
    {
        // Strings start at offset 11 (bytes 6..10 undecoded): name, model number, manufacturer, serial, ...
        ReadOnlySpan<byte> rest = p[11..];
        string name = NextString(ref rest);
        string modelNumber = NextString(ref rest);
        if (name == state.Name && modelNumber == state.ModelNumber)
        {
            return DeviceChange.None;
        }
        state.Name = name;
        state.ModelNumber = modelNumber;
        AirPodsModel model = Models.FromModelNumber(modelNumber) ?? Models.Unknown;
        state.ModelName = model.Name;
        return DeviceChange.Identity | state.SetBaseline(model.Capabilities);
    }

    private static string NextString(ref ReadOnlySpan<byte> rest)
    {
        int end = rest.IndexOf((byte)0);
        if (end < 0)
        {
            end = rest.Length;
        }
        string value = Encoding.UTF8.GetString(rest[..end]).Trim();
        rest = end < rest.Length ? rest[(end + 1)..] : default;
        return value;
    }

    // ---- Heart rate (0x17 RTBuddy frame with a protobuf payload) ----

    private const int MaxProtoLength = 16 * 1024;
    private const int MaxProtoFields = 96;
    private const int MaxProtoFieldNumber = 4096;
    private const int MaxDepth = 3;

    private static DeviceChange ApplyHeartRate(ReadOnlySpan<byte> p, DeviceState state)
    {
        if (!TryParseHeartRate(p, out byte bpm, out byte confidence))
        {
            return DeviceChange.None;
        }
        state.HeartRateBpm = bpm;
        state.HeartRateConfidence = confidence;
        state.HeartRateAvailable = true;
        if (state.HeartRate != HeartRateStatus.Off)
        {
            state.HeartRate = HeartRateStatus.Streaming;
        }
        return DeviceChange.HeartRate | state.Observe(DeviceCapabilities.HeartRate);
    }

    private static bool TryParseHeartRate(ReadOnlySpan<byte> p, out byte bpm, out byte confidence)
    {
        bpm = 0;
        confidence = 0;
        if (p.Length < 12 || p[8] != 0x10 || p[9] != 0x00)
        {
            return false;
        }
        int length = p[10] | (p[11] << 8);
        if (length > p.Length - 12)
        {
            return false;
        }
        ReadOnlySpan<byte> message = p.Slice(12, length);
        if (!IsMessage(message))
        {
            return false;
        }
        ulong logType = 0;
        int pos = 0;
        while (ReadField(message, ref pos, out int number, out int wire, out ulong scalar, out _))
        {
            if (number == 2 && wire == 0)
            {
                logType = scalar;
                break;
            }
        }
        if (logType is not (1 or 3))
        {
            return false;
        }
        pos = 0;
        while (ReadField(message, ref pos, out _, out int wire, out _, out ReadOnlySpan<byte> bytes))
        {
            if (wire == 2 && FindCommand(bytes, 0, out bpm, out confidence))
            {
                return true;
            }
        }
        return false;
    }

    // A message whose field 1 is a heart-rate service (19, 20, 84) carrying the reading in field 3.
    private static bool FindCommand(ReadOnlySpan<byte> data, int depth, out byte bpm, out byte confidence)
    {
        bpm = 0;
        confidence = 0;
        if (depth > MaxDepth || !IsMessage(data))
        {
            return false;
        }
        int pos = 0;
        while (ReadField(data, ref pos, out int number, out int wire, out ulong service, out _))
        {
            if (number == 1 && wire == 0)
            {
                if (service is 19 or 20 or 84)
                {
                    int inner = 0;
                    while (ReadField(data, ref inner, out int n, out int w, out _, out ReadOnlySpan<byte> payload))
                    {
                        if (n == 3 && w == 2 && FindPayload(payload, 0, out bpm, out confidence))
                        {
                            return true;
                        }
                    }
                }
                break;
            }
        }
        pos = 0;
        while (ReadField(data, ref pos, out _, out int wire, out _, out ReadOnlySpan<byte> bytes))
        {
            if (wire == 2 && FindCommand(bytes, depth + 1, out bpm, out confidence))
            {
                return true;
            }
        }
        return false;
    }

    private static bool FindPayload(ReadOnlySpan<byte> data, int depth, out byte bpm, out byte confidence)
    {
        if (data.Length == 18 && IsReading(data, out bpm, out confidence))
        {
            return true;
        }
        bpm = 0;
        confidence = 0;
        if (depth >= MaxDepth || !IsMessage(data))
        {
            return false;
        }
        int pos = 0;
        while (ReadField(data, ref pos, out _, out int wire, out _, out ReadOnlySpan<byte> bytes))
        {
            if (wire == 2 && FindPayload(bytes, depth + 1, out bpm, out confidence))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsReading(ReadOnlySpan<byte> data, out byte bpm, out byte confidence)
    {
        bpm = data[1];
        confidence = data[2];
        if (bpm is < 30 or > 220 || confidence < 0x80)
        {
            return false;
        }
        byte a = data[15];
        byte b = data[16];
        byte c = data[17];
        return (a == 0x10 && b == 0x00 && c is 0x00 or 0x80)
            || (a == 0x20 && b == 0x00 && c == 0x00)
            || (a == 0x20 && b == 0x80 && c == 0x00)
            || (a == 0x20 && b == 0x02 && c == 0x80)
            || (a == 0x20 && b == 0x82 && c == 0x80);
    }

    private static bool IsMessage(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxProtoLength)
        {
            return false;
        }
        int pos = 0;
        int fields = 0;
        while (pos < data.Length)
        {
            if (fields++ >= MaxProtoFields || !ReadField(data, ref pos, out _, out _, out _, out _))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Reads one protobuf field at <paramref name="pos"/>; false at the end or on malformed data.</summary>
    private static bool ReadField(ReadOnlySpan<byte> data, ref int pos, out int number, out int wire, out ulong scalar, out ReadOnlySpan<byte> bytes)
    {
        number = 0;
        wire = 0;
        scalar = 0;
        bytes = default;
        if (pos >= data.Length || !ReadVarint(data, ref pos, out ulong key))
        {
            return false;
        }
        number = (int)Math.Min(key >> 3, int.MaxValue);
        wire = (int)(key & 7);
        if (number is 0 or > MaxProtoFieldNumber)
        {
            return false;
        }
        switch (wire)
        {
            case 0:
                return ReadVarint(data, ref pos, out scalar);
            case 2:
                if (!ReadVarint(data, ref pos, out ulong length) || length > (ulong)(data.Length - pos))
                {
                    return false;
                }
                bytes = data.Slice(pos, (int)length);
                pos += (int)length;
                return true;
            case 1 or 5:
                int size = wire == 1 ? 8 : 4;
                if (data.Length - pos < size)
                {
                    return false;
                }
                bytes = data.Slice(pos, size);
                pos += size;
                return true;
            default:
                return false;
        }
    }

    private static bool ReadVarint(ReadOnlySpan<byte> data, ref int pos, out ulong value)
    {
        value = 0;
        for (int shift = 0; pos < data.Length && shift < 64; shift += 7)
        {
            byte current = data[pos++];
            if (shift == 63 && current > 1)
            {
                return false;
            }
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return true;
            }
        }
        return false;
    }
}
