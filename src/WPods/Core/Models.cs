using System.Collections.Frozen;

namespace WPods.Core;

/// <summary>
/// A model: display name, illustration key (pro, airpods4, airpods, max), baseline capabilities (features Apple documents
/// for it) and whether it has one battery (AirPods Max) instead of left, right and case.
/// </summary>
public sealed record AirPodsModel(string Name, string Art, DeviceCapabilities Capabilities, bool SingleBattery);

/// <summary>
/// The single model table: display name + illustration by AAP 0x1D model number (A-number, names and numbers per
/// Apple support 109525 "Identify your AirPods") and by proximity-advert model ID (advert bytes 3-4 read
/// little-endian = Apple's Bluetooth product ID, e.g. 0x2024). Which features to
/// show is <see cref="DeviceState.Capabilities"/>: what the device reported plus the baseline of its model here.
/// </summary>
public static class Models
{
    /// <summary>Model not in the table: plain AirPods illustration, no baseline features.</summary>
    public static readonly AirPodsModel Unknown = new("AirPods", "airpods", DeviceCapabilities.None, SingleBattery: false);

    private static readonly FrozenDictionary<ushort, AirPodsModel> ByProximityId;
    private static readonly FrozenDictionary<string, AirPodsModel> ByModelNumber;

    static Models()
    {
        // Baselines per Apple's AirPods User Guide (support.apple.com/guide/airpods, iOS 27) and Apple support 104979.
        // Ear detection = Automatic Ear Detection (AirPods Max: Automatic Head Detection).
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
            (new("AirPods 1", "airpods", airPods12, false), [0x2002], ["A1523", "A1722"]),
            (new("AirPods 2", "airpods", airPods12, false), [0x200F], ["A2032", "A2031"]),
            (new("AirPods 3", "airpods", airPods3, false), [0x2013], ["A2565", "A2564"]),
            (new("AirPods 4", "airpods4", airPods4, false), [0x2019], ["A3053", "A3050", "A3054"]),
            (new("AirPods 4 (ANC)", "airpods4", airPods4Anc, false), [0x201B], ["A3056", "A3055", "A3057"]),
            (new("AirPods 5", "airpods4", airPods4Anc, false), [0x2030, 0x2036], ["A3531", "A3532", "A3533", "A3439", "A3440", "A3441"]),
            (new("AirPods Pro 1", "pro", pro1, false), [0x200E], ["A2084", "A2083"]),
            (new("AirPods Pro 2", "pro", pro2, false), [0x2014, 0x2024], ["A2931", "A2699", "A2698", "A3047", "A3048", "A3049"]),
            (new("AirPods Pro 3", "pro", pro2 | DeviceCapabilities.HeartRate, false), [0x2027], ["A3063", "A3064", "A3065"]),
            (new("AirPods Max 1", "max", max1, true), [0x200A, 0x201F], ["A2096", "A3184"]),
            (new("AirPods Max 2", "max", max2, true), [], ["A3454"]),
        ];
        var byId = new Dictionary<ushort, AirPodsModel>();
        var byNumber = new Dictionary<string, AirPodsModel>(StringComparer.OrdinalIgnoreCase);
        foreach ((AirPodsModel model, ushort[] ids, string[] numbers) in table)
        {
            foreach (ushort id in ids)
            {
                byId.Add(id, model);
            }
            foreach (string number in numbers)
            {
                byNumber.Add(number, model);
            }
        }
        ByProximityId = byId.ToFrozenDictionary();
        ByModelNumber = byNumber.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public static AirPodsModel? FromProximityId(ushort id) => ByProximityId.GetValueOrDefault(id);

    public static AirPodsModel? FromModelNumber(string? number) => number is null ? null : ByModelNumber.GetValueOrDefault(number);
}
