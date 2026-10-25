using WPods.Audio.Classic;
using WPods.Audio.Dsp;
using WPods.Audio.Enhance;
using WPods.Audio.Modes;
using WPods.Audio.Onnx;
using WPods.Core;

namespace WPods.Audio;

/// <summary>
/// One entry of a mic picker. <paramref name="Weight"/> is "light", "medium", "heavy" or "veryHeavy" (one core:
/// &lt;5 %, 5-25 %, 25-100 %, &gt;100 %; entries on several threads are "veryHeavy") or "notMeasured";
/// <paramref name="Cores"/> is the measured average CPU in cores (all threads), 0 = not measured;
/// <paramref name="LatencyMs"/> the added delay. <paramref name="Create"/> throws when a DLL or model is missing; null
/// only for "raw" (no stage).
/// </summary>
internal sealed record MicOption(string Id, string Name, string Weight, int Threads, float Cores, float LatencyMs, Func<IMicStage>? Create);

/// <summary>
/// The models of each mic mode and the voice enhancements, in picker order (the user's preference order). Ids are
/// stored in settings.json and sent to the UI; names are technical English (not localized). CPU and delay are the
/// engine agents' measurements (Release, x64; P-core where they differ).
/// </summary>
internal static class MicCatalog
{
    private const string Light = "light";
    private const string Medium = "medium";
    private const string Heavy = "heavy";
    private const string VeryHeavy = "veryHeavy";
    private const string NotMeasured = "notMeasured";

    public static readonly MicOption[] Standard =
    [
        new("gtcrn-vd", "GTCRN-vd 16k + HB gain", NotMeasured, 1, 0, 35.6f, () => new Spectrum16kStage(Spectrum16kStage.VdSpecFile("gtcrn"), StftWindow.SqrtHann)),
        new("gtcrn-hbpass", "GTCRN 16k + HB passthrough", Light, 1, 0.035f, 35.6f, () => new Spectrum16kStage(Spectrum16kStage.GtcrnFile, StftWindow.SqrtHann, HighBand.Pass)),
    ];

    public static readonly MicOption[] VoiceIsolation =
    [
        new("dpdfnet8-t4", "DPDFNet-8 48k, 4 threads", NotMeasured, 4, 0, 50, () => new DpdfNetStage(DpdfNetStage.Dpdf8File, 4)),
        new("dpdfnet8", "DPDFNet-8 48k", Heavy, 1, 0.33f, 50, () => new DpdfNetStage(DpdfNetStage.Dpdf8File)),
        new("dpdfnet2", "DPDFNet-2 48k", Medium, 1, 0.12f, 50, () => new DpdfNetStage(DpdfNetStage.Dpdf2File)),
        new("nsnet2", "NSNet2 48k", NotMeasured, 1, 0, 19.3f, () => new NsNet2Stage(NsNet2Stage.File48k)),
        new("fastenhancer-l", "FastEnhancer-L 48k", Heavy, 1, 0.62f, 20.5f, () => new FastEnhancerStage(FastEnhancerStage.L48kFile)),
        new("fastenhancer-m", "FastEnhancer-M 48k", Medium, 1, 0.187f, 18, () => new FastEnhancerStage(FastEnhancerStage.M48kFile)),
        new("fastenhancer-s", "FastEnhancer-S 48k", Medium, 1, 0.059f, 20.7f, () => new FastEnhancerStage(FastEnhancerStage.S48kFile)),
        new("fastenhancer-b", "FastEnhancer-B 48k", Light, 1, 0.039f, 20.7f, () => new FastEnhancerStage(FastEnhancerStage.B48kFile)),
        new("fastenhancer-t", "FastEnhancer-T 48k", Light, 1, 0.025f, 20.7f, () => new FastEnhancerStage(FastEnhancerStage.T48kFile)),
        new("dfn3-residual", "DFN3 + residual filter", Light, 1, 0.043f, 40, () => new ResidualFilterStage(new DfnStage(postFilterBeta: 0.02f), DfnStage.StandardLatencySamples)),
    ];

    public static readonly MicOption[] WideSpectrum =
    [
        new("webrtc-ns", "WebRTC HPF + NS very high", Light, 1, 0.0022f, 7, () => new WebRtcNoiseSuppressor(WebRtcNoiseLevel.VeryHigh, highPass: true)),
        new("wiener", "Residual filter (Wiener)", Light, 1, 0.001f, 10, () => new ResidualFilterStage()),
        new("raw", "Raw", Light, 1, 0, 0, null),
    ];

    public static readonly MicOption[] Enhancements =
    [
        new("appleLike", "Apple-like", Light, 1, 0.0070f, 0, VoiceEnhancements.AppleLike),
        new("clearAuto", "Clear voice, auto level", Light, 1, 0.0019f, 0, WebRtcEnhancements.Agc2CleanEq),
        new("clearFull", "Clear voice, full-range level", Light, 1, 0.0018f, 0, WebRtcEnhancements.Agc2FullCleanEq),
    ];

    private static readonly Dictionary<string, MicOption> StandardById = Index(Standard);
    private static readonly Dictionary<string, MicOption> VoiceIsolationById = Index(VoiceIsolation);
    private static readonly Dictionary<string, MicOption> WideSpectrumById = Index(WideSpectrum);
    private static readonly Dictionary<string, MicOption> EnhancementById = Index(Enhancements);

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

    private static Dictionary<string, MicOption> Index(MicOption[] options)
    {
        var byId = new Dictionary<string, MicOption>(options.Length, StringComparer.Ordinal);
        foreach (MicOption option in options)
        {
            byId.Add(option.Id, option);
        }
        return byId;
    }
}
