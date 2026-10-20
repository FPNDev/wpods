using System.Collections.Frozen;

namespace WPods.Core;

/// <summary>Features a model has beyond ear detection (which every model has). Features a model lacks are hidden in the UI.</summary>
[Flags]
public enum ModelCapabilities
{
    None = 0,
    /// <summary>Listening modes Off, Noise Cancellation, Transparency.</summary>
    NoiseControl = 1 << 0,
    /// <summary>Adaptive listening mode.</summary>
    Adaptive = 1 << 1,
    ConversationAwareness = 1 << 2,
    /// <summary>Noise control with one AirPod.</summary>
    OneBudAnc = 1 << 3,
    PersonalizedVolume = 1 << 4,
    AllowOff = 1 << 5,
    HeartRate = 1 << 6,
    HearingAid = 1 << 7,
    /// <summary>One battery (AirPods Max) instead of left, right and case.</summary>
    SingleBattery = 1 << 8,
}

/// <summary>A model family: display name, illustration key (pro, airpods4, airpods, max) and capabilities.</summary>
public sealed record AirPodsModel(string Name, string Art, ModelCapabilities Capabilities);

/// <summary>
/// Model lookup by proximity-advert model ID and by AAP model number (A-number, Apple support 109525).
/// The proximity ID is advert bytes 3-4 read little-endian (= Apple's Bluetooth product ID, e.g. 0x2014).
/// </summary>
public static class Models
{
    private const ModelCapabilities ProCapabilities = ModelCapabilities.NoiseControl | ModelCapabilities.Adaptive
        | ModelCapabilities.ConversationAwareness | ModelCapabilities.OneBudAnc | ModelCapabilities.PersonalizedVolume
        | ModelCapabilities.AllowOff | ModelCapabilities.HearingAid;

    /// <summary>Model not in the table: plain AirPods illustration, no extra features.</summary>
    public static readonly AirPodsModel Unknown = new("AirPods", "airpods", ModelCapabilities.None);

    private static readonly FrozenDictionary<ushort, AirPodsModel> ByProximityId;
    private static readonly FrozenDictionary<string, AirPodsModel> ByModelNumber;

    static Models()
    {
        var airPods1 = new AirPodsModel("AirPods (1st generation)", "airpods", ModelCapabilities.None);
        var airPods2 = new AirPodsModel("AirPods (2nd generation)", "airpods", ModelCapabilities.None);
        var airPods3 = new AirPodsModel("AirPods (3rd generation)", "airpods", ModelCapabilities.None);
        var airPods4 = new AirPodsModel("AirPods 4", "airpods4", ModelCapabilities.None);
        var airPods4Anc = new AirPodsModel("AirPods 4 (ANC)", "airpods4", ModelCapabilities.NoiseControl | ModelCapabilities.ConversationAwareness
            | ModelCapabilities.OneBudAnc | ModelCapabilities.PersonalizedVolume | ModelCapabilities.AllowOff);
        var airPods5 = new AirPodsModel("AirPods 5", "airpods4", ModelCapabilities.NoiseControl | ModelCapabilities.Adaptive
            | ModelCapabilities.ConversationAwareness | ModelCapabilities.OneBudAnc | ModelCapabilities.PersonalizedVolume | ModelCapabilities.AllowOff);
        var pro1 = new AirPodsModel("AirPods Pro", "pro", ModelCapabilities.NoiseControl | ModelCapabilities.AllowOff | ModelCapabilities.OneBudAnc);
        var pro2 = new AirPodsModel("AirPods Pro 2", "pro", ProCapabilities);
        var pro3 = new AirPodsModel("AirPods Pro 3", "pro", ProCapabilities | ModelCapabilities.HeartRate);
        var max = new AirPodsModel("AirPods Max", "max", ModelCapabilities.NoiseControl | ModelCapabilities.ConversationAwareness
            | ModelCapabilities.PersonalizedVolume | ModelCapabilities.AllowOff | ModelCapabilities.SingleBattery);
        var max2 = new AirPodsModel("AirPods Max 2", "max", ModelCapabilities.NoiseControl | ModelCapabilities.Adaptive
            | ModelCapabilities.ConversationAwareness | ModelCapabilities.PersonalizedVolume | ModelCapabilities.AllowOff
            | ModelCapabilities.SingleBattery);

        (AirPodsModel Model, ushort[] Ids, string[] Numbers)[] table =
        [
            (airPods1, [0x2002], ["A1523", "A1722"]),
            (airPods2, [0x200F], ["A2032", "A2031"]),
            (airPods3, [0x2013], ["A2565", "A2564"]),
            (airPods4, [0x2019], ["A3053", "A3050", "A3054"]),
            (airPods4Anc, [0x201B], ["A3056", "A3055", "A3057"]),
            (pro1, [0x200E], ["A2084", "A2083"]),
            (pro2, [0x2014, 0x2024], ["A2931", "A2699", "A2698", "A3047", "A3048", "A3049"]),
            (pro3, [0x2027], ["A3063", "A3064", "A3065"]),
            (airPods5, [0x2030, 0x2036], ["A3531", "A3532", "A3533", "A3439", "A3440", "A3441"]),
            (max, [0x200A, 0x201F], ["A2096", "A3184"]),
            (max2, [], ["A3454"]),
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
