using WPods.Audio.Dsp;

namespace WPods.Audio.Enhance;

/// <summary>
/// Voice enhancements built from the enhancement blocks: fixed chains, run after the mode's model on the x4 signal
/// like every stage. Thresholds are tuned for the measured AirPods speech level (about -36 dBFS RMS at x1, so
/// about -24 dBFS after x4). Every chain is loudness matched to enhancement Off at that level: chains without
/// leveller end in a fixed make-up <see cref="Gain"/>; chains with leveller level speech to the loudness of Off and
/// end in a make-up gain for the compression after the leveller.
/// All blocks are IIR or envelope based: no latency.
/// </summary>
internal static class VoiceEnhancements
{
    // Without leveller.
    public static IMicStage CleanEq() => new MicChain(CleanEqFilters(), new Gain(0.5f));

    // With leveller: the target puts speech at the loudness of Off; a trailing make-up gain restores what the
    // compressor after it takes off (raising the target instead would only push the compressor harder).
    public static IMicStage AppleLike() => new MicChain(
        new Equalizer(Hp(90f), Peak(300f, 1f, -2f), Peak(3000f, 0.7f, 2f), HighShelf(10000f, 1.5f)),
        new Leveller(LevellerTargetDb),
        new DeEsser(DeEsserThresholdDb),
        new Compressor(CompressorThresholdDb, 2.5f, 8f, 5f, 150f, makeUpDb: 0f),
        new Expander(ExpanderThresholdDb - 3f, 2f, -10f),
        new Gain(3.9f));

    private const float LevellerTargetDb = -23.6f;
    private const float CompressorThresholdDb = -28f;
    private const float DeEsserThresholdDb = -42f;
    private const float ExpanderThresholdDb = -45f;

    private static Equalizer CleanEqFilters() => new(Hp(80f), Peak(275f, 1f, -3f), Peak(3500f, 0.9f, 2.5f));

    private static Biquad Hp(float hz) => Biquad.HighPass(Level.SampleRate, hz, 0.7071f);

    private static Biquad Peak(float hz, float q, float gainDb) => Biquad.Peaking(Level.SampleRate, hz, q, gainDb);

    private static Biquad HighShelf(float hz, float gainDb) => Biquad.HighShelf(Level.SampleRate, hz, 0.7071f, gainDb);
}
