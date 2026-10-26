namespace WPods.Audio.Enhance;

/// <summary>Level conversions and envelope time constants shared by the enhancement blocks (48 kHz).</summary>
internal static class Level
{
    public const float SampleRate = 48000f;

    // dB of a power from its log2 (10 * log10(2)); log2 of a gain from its dB (log2(10) / 20).
    public const float PowerDbPerLog2 = 3.01029996f;
    public const float Log2PerDb = 0.166096404f;

    public static float DbToGain(float db) => MathF.Pow(10f, db / 20f);

    /// <summary>Power (mean square) to dBFS, floored at -120 dBFS.</summary>
    public static float PowerToDb(float power) => 10f * MathF.Log10(power + 1e-12f);

    /// <summary>One-pole smoothing coefficient for a time constant in ms at 48 kHz per sample.</summary>
    public static float Coefficient(float ms) => MathF.Exp(-1f / (ms * 0.001f * SampleRate));
}
