namespace WPods.Audio;

/// <summary>
/// The single float -> 16-bit conversion at the end of the mic pipeline. Samples up to <see cref="Knee"/> (-3 dBFS)
/// pass unchanged; above it a tanh curve bends them smoothly towards full scale (value and slope match at the knee,
/// so no kink), and the output never reaches 0 dBFS, so decoder overshoot, make-up gain or denoiser peaks are never
/// hard-clipped. Memoryless: no latency, no pumping. Speech normally peaks below -3 dBFS, so it stays untouched.
/// </summary>
internal static class SoftLimiter
{
    private const float Knee = 0.70794578f; // -3 dBFS
    private const float Headroom = 1f - Knee;
    private const float FullScale = 32767f;

    public static short ToPcm16(float sample)
    {
        float magnitude = MathF.Abs(sample);
        if (magnitude > Knee)
        {
            sample = MathF.CopySign(Knee + Headroom * MathF.Tanh((magnitude - Knee) / Headroom), sample);
        }
        return (short)MathF.Round(sample * FullScale);
    }
}
