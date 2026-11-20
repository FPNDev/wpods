using System.Collections.Frozen;

namespace WPods.Core;

/// <summary>
/// Art is the illustration key; SingleBattery means one battery (AirPods Max) instead of left, right and case.
/// StereoMic: the AAP 0x58 mic stream (Apple's studio-quality recording, H2 chip), which keeps audio in stereo.
/// </summary>
public sealed record AirPodsModel(string Name, string Art, DeviceCapabilities Capabilities, bool SingleBattery, bool StereoMic);

/// <summary>
/// Model table keyed by AAP 0x1D model number (per Apple support 109525) and by Bluetooth product ID
/// (proximity advert bytes 3-4, little-endian). Capabilities here are baselines merged into <see cref="DeviceState.Capabilities"/>.
/// </summary>
public static class Models
{
    /// <summary>Model not in the table: plain AirPods illustration, no baseline features; the stereo mic is tried.</summary>
    public static readonly AirPodsModel Unknown = new("AirPods", "airpods", DeviceCapabilities.None, SingleBattery: false, StereoMic: true);

    private static readonly FrozenDictionary<ushort, AirPodsModel> ByProximityId;
    private static readonly FrozenDictionary<ushort, string> NumberByProductId;
    private static readonly FrozenDictionary<string, AirPodsModel> ByModelNumber;

    static Models()
    {
        // Baselines per Apple's AirPods User Guide and Apple support 104979; EarDetection covers Max head detection.
        const DeviceCapabilities airPods12 = DeviceCapabilities.EarDetection | DeviceCapabilities.MicSide;
        const DeviceCapabilities controls = DeviceCapabilities.PressSpeed | DeviceCapabilities.HoldDuration | DeviceCapabilities.ToneVolume;
        const DeviceCapabilities airPods3 = airPods12 | controls;
        const DeviceCapabilities airPods4 = airPods3 | DeviceCapabilities.PersonalizedVolume;
        const DeviceCapabilities airPods4Anc = airPods4 | DeviceCapabilities.ListeningMode | DeviceCapabilities.Adaptive
            | DeviceCapabilities.ConversationAwareness | DeviceCapabilities.OneBudAnc;
        const DeviceCapabilities pro1 = airPods3 | DeviceCapabilities.ListeningMode | DeviceCapabilities.OneBudAnc;
        const DeviceCapabilities pro2 = airPods4Anc | DeviceCapabilities.AllowOff | DeviceCapabilities.HearingAid
            | DeviceCapabilities.MediaAssist;
        const DeviceCapabilities max1 = DeviceCapabilities.EarDetection | controls | DeviceCapabilities.ListeningMode;
        const DeviceCapabilities max2 = max1 | DeviceCapabilities.Adaptive | DeviceCapabilities.ConversationAwareness
            | DeviceCapabilities.PersonalizedVolume;

        (AirPodsModel Model, ushort[] Ids, string[] Numbers)[] table =
        [
            // StereoMic: H2 models only (Apple's studio-quality recording); W1/H1 models never answer the stream's START.
            (new("AirPods 1", "airpods", airPods12, false, false), [0x2002], ["A1523", "A1722"]),
            (new("AirPods 2", "airpods", airPods12, false, false), [0x200F], ["A2032", "A2031"]),
            (new("AirPods 3", "airpods", airPods3, false, false), [0x2013], ["A2565", "A2564"]),
            (new("AirPods 4", "airpods4", airPods4, false, true), [0x2019], ["A3053", "A3050", "A3054"]),
            (new("AirPods 4 (ANC)", "airpods4", airPods4Anc, false, true), [0x201B], ["A3056", "A3055", "A3057"]),
            (new("AirPods 5", "airpods4", airPods4Anc, false, true), [0x2030, 0x2036], ["A3531", "A3532", "A3533", "A3439", "A3440", "A3441"]),
            (new("AirPods Pro 1", "pro", pro1, false, false), [0x200E], ["A2084", "A2083"]),
            (new("AirPods Pro 2", "pro", pro2, false, true), [0x2014, 0x2024], ["A2931", "A2699", "A2698", "A3047", "A3048", "A3049"]),
            (new("AirPods Pro 3", "pro", pro2 | DeviceCapabilities.HeartRate, false, true), [0x2027], ["A3063", "A3064", "A3065"]),
            (new("AirPods Max 1", "max", max1, true, false), [0x200A, 0x201F], ["A2096", "A3184"]),
            (new("AirPods Max 2", "max", max2, true, true), [], ["A3454"]),
        ];
        var byId = new Dictionary<ushort, AirPodsModel>();
        var numberById = new Dictionary<ushort, string>();
        var byNumber = new Dictionary<string, AirPodsModel>(StringComparer.OrdinalIgnoreCase);
        foreach ((AirPodsModel model, ushort[] ids, string[] numbers) in table)
        {
            foreach (ushort id in ids)
            {
                byId.Add(id, model);
                numberById.Add(id, numbers[0]);
            }
            foreach (string number in numbers)
            {
                byNumber.Add(number, model);
            }
        }
        ByProximityId = byId.ToFrozenDictionary();
        NumberByProductId = numberById.ToFrozenDictionary();
        ByModelNumber = byNumber.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public static AirPodsModel? FromProximityId(ushort id) => ByProximityId.GetValueOrDefault(id);

    /// <summary>A model number for this product ID, standing in for 0x1D until the AirPods send it.</summary>
    public static string? ModelNumberOf(ushort productId) => NumberByProductId.GetValueOrDefault(productId);

    public static AirPodsModel? FromModelNumber(string? number) => number is null ? null : ByModelNumber.GetValueOrDefault(number);

    /// <summary>False only for a known model without the stereo mic stream; unknown models are tried.</summary>
    public static bool HasStereoMic(string? number) => (FromModelNumber(number) ?? Unknown).StereoMic;
}
