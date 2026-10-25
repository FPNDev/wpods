using System.Numerics;
using System.Runtime.InteropServices;

namespace WPods.Audio.Dsp;

/// <summary>
/// Linear-phase low-pass shared by <see cref="Downsampler48To16"/> and <see cref="Upsampler16To48"/>: Kaiser-windowed
/// sinc at 48 kHz, 6 * 45 + 1 = 271 taps (delay 135 samples at 48 kHz = 45 at 16 kHz), cutoff 7.5 kHz, Kaiser beta
/// 8.96 (about 90 dB stopband): passband to about 7 kHz, stopband from about 8 kHz.
/// </summary>
internal static class Resampler
{
    public const int Factor = 3;
    public const int HalfOrder16k = 45;
    public const int Taps = 6 * HalfOrder16k + 1;
    public const int MaxInput48k = 480;

    private const double CutoffHz = 7500;
    private const double SampleRate = 48000;
    private const double KaiserBeta = 8.96;

    /// <summary>Filter taps with DC gain 1 (symmetric, so also its own reverse).</summary>
    public static readonly float[] Filter = Design();

    /// <summary>Dot product of two equal-length spans (SIMD).</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var acc = Vector<float>.Zero;
        for (int i = 0; i < va.Length; i++)
        {
            acc += va[i] * vb[i];
        }
        float sum = Vector.Sum(acc);
        for (int i = va.Length * Vector<float>.Count; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    private static float[] Design()
    {
        const int center = Taps / 2;
        double fc = CutoffHz / SampleRate;
        double i0Beta = BesselI0(KaiserBeta);
        var taps = new double[Taps];
        double total = 0;
        for (int n = 0; n < Taps; n++)
        {
            int t = n - center;
            double sinc = t == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * t) / (Math.PI * t);
            double r = (double)t / center;
            taps[n] = sinc * BesselI0(KaiserBeta * Math.Sqrt(1 - r * r)) / i0Beta;
            total += taps[n];
        }
        var filter = new float[Taps];
        for (int n = 0; n < Taps; n++)
        {
            filter[n] = (float)(taps[n] / total);
        }
        return filter;
    }

    private static double BesselI0(double x)
    {
        double sum = 1;
        double term = 1;
        for (int k = 1; k < 50; k++)
        {
            double f = x / (2 * k);
            term *= f * f;
            sum += term;
        }
        return sum;
    }
}

/// <summary>
/// Streaming 48 kHz to 16 kHz decimator (mono float): filter with <see cref="Resampler.Filter"/>, keep every third
/// sample. Input length a multiple of 3 up to 480 (one 10 ms frame), output one third of it.
/// Delay <see cref="LatencySamples"/> = 45 samples at 16 kHz (2.8125 ms). No allocation per call; one caller at a time.
/// </summary>
internal sealed class Downsampler48To16
{
    /// <summary>Output delay in 16 kHz samples.</summary>
    public const int LatencySamples = Resampler.HalfOrder16k;

    private const int History = Resampler.Taps - 1;

    private readonly float[] _buffer = new float[History + Resampler.MaxInput48k];

    public void Process(ReadOnlySpan<float> input48k, Span<float> output16k)
    {
        int count = input48k.Length;
        if (count > Resampler.MaxInput48k || count != output16k.Length * Resampler.Factor)
        {
            throw new ArgumentException("Input must be 3 x output samples, at most 480.", nameof(input48k));
        }
        input48k.CopyTo(_buffer.AsSpan(History));
        ReadOnlySpan<float> filter = Resampler.Filter;
        for (int i = 0; i < output16k.Length; i++)
        {
            output16k[i] = Resampler.Dot(filter, _buffer.AsSpan(Resampler.Factor * i, Resampler.Taps));
        }
        _buffer.AsSpan(count, History).CopyTo(_buffer);
    }
}

/// <summary>
/// Streaming 16 kHz to 48 kHz interpolator (mono float): polyphase form of zero-stuffing by 3 and filtering with
/// <see cref="Resampler.Filter"/> (gain 3). Input length up to 160 (one 10 ms frame), output three times it.
/// Delay <see cref="LatencySamples"/> = 135 samples at 48 kHz (2.8125 ms). No allocation per call; one caller at a time.
/// </summary>
internal sealed class Upsampler16To48
{
    /// <summary>Output delay in 48 kHz samples.</summary>
    public const int LatencySamples = Resampler.HalfOrder16k * Resampler.Factor;

    private const int PhaseTaps = (Resampler.Taps + Resampler.Factor - 1) / Resampler.Factor;
    private const int History = PhaseTaps - 1;
    private const int MaxInput = Resampler.MaxInput48k / Resampler.Factor;

    private static readonly float[][] Phases = BuildPhases();

    private readonly float[] _buffer = new float[History + MaxInput];

    public void Process(ReadOnlySpan<float> input16k, Span<float> output48k)
    {
        int count = input16k.Length;
        if (count > MaxInput || output48k.Length != count * Resampler.Factor)
        {
            throw new ArgumentException("Output must be 3 x input samples, input at most 160.", nameof(output48k));
        }
        input16k.CopyTo(_buffer.AsSpan(History));
        for (int n = 0; n < count; n++)
        {
            ReadOnlySpan<float> window = _buffer.AsSpan(n, PhaseTaps);
            for (int r = 0; r < Resampler.Factor; r++)
            {
                output48k[Resampler.Factor * n + r] = Resampler.Dot(Phases[r], window);
            }
        }
        _buffer.AsSpan(count, History).CopyTo(_buffer);
    }

    /// <summary>
    /// y[3n + r] = 3 sum_k h[3k + r] x[n - k]; stored reversed (oldest input first) so each output is one dot
    /// product with the history window ending at x[n].
    /// </summary>
    private static float[][] BuildPhases()
    {
        var phases = new float[Resampler.Factor][];
        for (int r = 0; r < Resampler.Factor; r++)
        {
            phases[r] = new float[PhaseTaps];
            for (int k = 0; k < PhaseTaps; k++)
            {
                int tap = Resampler.Factor * k + r;
                phases[r][PhaseTaps - 1 - k] = tap < Resampler.Taps ? Resampler.Factor * Resampler.Filter[tap] : 0f;
            }
        }
        return phases;
    }
}
