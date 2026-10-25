namespace WPods.Audio.Dsp;

/// <summary>
/// Second-order IIR filter, RBJ Audio EQ Cookbook coefficients, transposed direct form II. Shelves take Q like the
/// other shapes (Q = 0.7071 equals the cookbook's shelf slope S = 1). Process works in place on any block length.
/// No allocation per call; one caller at a time.
/// </summary>
internal sealed class Biquad
{
    private readonly float _b0;
    private readonly float _b1;
    private readonly float _b2;
    private readonly float _a1;
    private readonly float _a2;
    private float _z1;
    private float _z2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = (float)(b0 / a0);
        _b1 = (float)(b1 / a0);
        _b2 = (float)(b2 / a0);
        _a1 = (float)(a1 / a0);
        _a2 = (float)(a2 / a0);
    }

    public static Biquad LowPass(float sampleRate, float frequency, float q)
    {
        (double cos, double alpha) = Angle(sampleRate, frequency, q);
        return new Biquad((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static Biquad HighPass(float sampleRate, float frequency, float q)
    {
        (double cos, double alpha) = Angle(sampleRate, frequency, q);
        return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static Biquad Peaking(float sampleRate, float frequency, float q, float gainDb)
    {
        (double cos, double alpha) = Angle(sampleRate, frequency, q);
        double a = Math.Pow(10, gainDb / 40.0);
        return new Biquad(1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a);
    }

    public static Biquad HighShelf(float sampleRate, float frequency, float q, float gainDb)
    {
        (double cos, double alpha) = Angle(sampleRate, frequency, q);
        double a = Math.Pow(10, gainDb / 40.0);
        double k = 2 * Math.Sqrt(a) * alpha;
        return new Biquad(
            a * ((a + 1) + (a - 1) * cos + k),
            -2 * a * ((a - 1) + (a + 1) * cos),
            a * ((a + 1) + (a - 1) * cos - k),
            (a + 1) - (a - 1) * cos + k,
            2 * ((a - 1) - (a + 1) * cos),
            (a + 1) - (a - 1) * cos - k);
    }

    /// <summary>Filters the samples in place.</summary>
    public void Process(Span<float> samples)
    {
        float z1 = _z1, z2 = _z2;
        for (int i = 0; i < samples.Length; i++)
        {
            float x = samples[i];
            float y = _b0 * x + z1;
            z1 = _b1 * x - _a1 * y + z2;
            z2 = _b2 * x - _a2 * y;
            samples[i] = y;
        }
        // Flush denormals once per block (digital silence after a gate would otherwise decay into them).
        _z1 = MathF.Abs(z1) < 1e-25f ? 0f : z1;
        _z2 = MathF.Abs(z2) < 1e-25f ? 0f : z2;
    }

    private static (double Cos, double Alpha) Angle(float sampleRate, float frequency, float q)
    {
        double w0 = 2 * Math.PI * frequency / sampleRate;
        return (Math.Cos(w0), Math.Sin(w0) / (2 * q));
    }
}
