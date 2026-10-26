using WPods.Audio.Enhance;
using WPods.Audio.Modes;
using WPods.Audio.Onnx;
using WPods.Core;

namespace WPods.Audio;

/// <summary>
/// One entry of a mic picker. <paramref name="Mmacs"/> is the model's compute in MMAC/s (millions of multiply-adds
/// per second of audio), 0 for DSP-only stages; <paramref name="Weight"/> follows from it: "light", "medium", "heavy"
/// or "veryHeavy" (&lt;500, 500-2500, 2500-10000, &gt;10000 MMAC/s; DSP-only stages are "light").
/// <paramref name="LatencyMs"/> the added delay. <paramref name="Create"/> throws when a DLL or model is missing; null
/// only for "raw" (no stage).
/// </summary>
internal sealed record MicOption(string Id, string Name, string Weight, int Threads, int Mmacs, float LatencyMs, Func<IMicStage>? Create);

/// <summary>
/// The models of each mic mode and the voice enhancements, in picker order (the user's preference order). Ids are
/// stored in settings.json and sent to the UI; names are technical English (not localized). MMAC/s is counted from
/// each ONNX graph (onnx-tool profile, in-model FFTs excluded) times the model's calls per second; DFN3 is its three
/// graphs together. Delay is the engine agents' measurement.
/// </summary>
internal static class MicCatalog
{
    private const string Light = "light";
    private const string Medium = "medium";
    private const string Heavy = "heavy";
    private const string VeryHeavy = "veryHeavy";

    public static readonly MicOption[] Standard =
    [
        new("gtcrn-vd", "GTCRN-vd 16k + HB gain", Light, 1, 55, 35.6f, () => new Spectrum16kStage(Spectrum16kStage.VdSpecFile("gtcrn"))),
        new("gtcrn-hbpass", "GTCRN 16k + HB passthrough", Light, 1, 62, 35.6f, () => new Spectrum16kStage(Spectrum16kStage.GtcrnFile, HighBand.Pass)),
    ];

    public static readonly MicOption[] VoiceIsolation =
    [
        new("dpdfnet8-t4", "DPDFNet-8 48k, 4 threads", Heavy, 4, 8224, 50, () => new DpdfNetStage(DpdfNetStage.Dpdf8File, 4)),
        new("dpdfnet8", "DPDFNet-8 48k", Heavy, 1, 8224, 50, () => new DpdfNetStage(DpdfNetStage.Dpdf8File)),
        new("dpdfnet2", "DPDFNet-2 48k", Heavy, 1, 2689, 50, () => new DpdfNetStage(DpdfNetStage.Dpdf2File)),
        new("nsnet2", "NSNet2 48k", Medium, 1, 592, 19.3f, () => new NsNet2Stage()),
        new("fastenhancer-l", "FastEnhancer-L 48k", VeryHeavy, 1, 37549, 20.5f, () => new FastEnhancerStage(FastEnhancerStage.L48kFile)),
        new("fastenhancer-m", "FastEnhancer-M 48k", Heavy, 1, 9628, 18, () => new FastEnhancerStage(FastEnhancerStage.M48kFile)),
        new("fastenhancer-s", "FastEnhancer-S 48k", Medium, 1, 2302, 20.7f, () => new FastEnhancerStage(FastEnhancerStage.S48kFile)),
        new("fastenhancer-b", "FastEnhancer-B 48k", Medium, 1, 1016, 20.7f, () => new FastEnhancerStage(FastEnhancerStage.B48kFile)),
        new("fastenhancer-t", "FastEnhancer-T 48k", Light, 1, 289, 20.7f, () => new FastEnhancerStage(FastEnhancerStage.T48kFile)),
        new("dfn3-residual", "DFN3 + residual filter", Light, 1, 359, 40, () => new ResidualFilterStage(new DfnStage(postFilterBeta: 0.02f), DfnStage.StandardLatencySamples)),
    ];

    public static readonly MicOption[] WideSpectrum =
    [
        new("webrtc-ns", "WebRTC HPF + NS very high", Light, 1, 0, 7, WebRtcApm.NoiseSuppressor),
        new("wiener", "Residual filter (Wiener)", Light, 1, 0, 10, () => new ResidualFilterStage()),
        new("raw", "Raw", Light, 1, 0, 0, null),
    ];

    public static readonly MicOption[] Enhancements =
    [
        new("appleInspired", "Apple-inspired", Light, 1, 0, 0, VoiceEnhancements.AppleInspired),
        new("clearAuto", "Clear voice, auto level", Light, 1, 0, 0, WebRtcEnhancements.Agc2CleanEq),
        new("clearFull", "Clear voice, full-range level", Light, 1, 0, 0, WebRtcEnhancements.Agc2FullCleanEq),
    ];

    private static readonly Dictionary<string, MicOption> StandardById = Standard.ToDictionary(o => o.Id, StringComparer.Ordinal);
    private static readonly Dictionary<string, MicOption> VoiceIsolationById = VoiceIsolation.ToDictionary(o => o.Id, StringComparer.Ordinal);
    private static readonly Dictionary<string, MicOption> WideSpectrumById = WideSpectrum.ToDictionary(o => o.Id, StringComparer.Ordinal);
    private static readonly Dictionary<string, MicOption> EnhancementById = Enhancements.ToDictionary(o => o.Id, StringComparer.Ordinal);

    /// <summary>The model a mode uses until the user picks another.</summary>
    public static string DefaultModel(MicMode mode) => mode switch
    {
        MicMode.Standard => "gtcrn-vd",
        MicMode.VoiceIsolation => "dpdfnet8",
        _ => "webrtc-ns",
    };

    /// <summary>The model <paramref name="id"/> of <paramref name="mode"/>'s list, or null.</summary>
    public static MicOption? FindModel(MicMode mode, string id) => (mode switch
    {
        MicMode.Standard => StandardById,
        MicMode.VoiceIsolation => VoiceIsolationById,
        _ => WideSpectrumById,
    }).GetValueOrDefault(id);

    public static MicOption? FindEnhancement(string id) => EnhancementById.GetValueOrDefault(id);
}
