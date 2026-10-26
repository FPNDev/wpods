namespace WPods.Audio.Dsp;

/// <summary>
/// Real FFT of an even size whose half factors into 2, 3 and 5 (e.g. 960, 1024, 512, 480). Managed mixed-radix
/// Stockham complex FFT of size/2 (radix 4, 2, 3, 5) plus the usual split step for real input.
/// Spectrum layout: <see cref="Bins"/> = size/2 + 1 complex bins, interleaved (re, im), so size + 2 floats.
/// <see cref="Forward"/> is unnormalized (X[k] = sum x[n] e^(-2 pi i k n / N)); <see cref="Inverse"/> scales by 1/N,
/// so Inverse(Forward(x)) == x. The imaginary parts of bin 0 and bin N/2 are ignored by Inverse.
/// No allocation per call; one caller at a time (holds scratch buffers).
/// </summary>
internal sealed unsafe class RealFft
{
    private const float Sin60 = 0.866025403784438647f;
    private const float Cos72 = 0.309016994374947424f;
    private const float Cos144 = -0.809016994374947424f;
    private const float Sin72 = 0.951056516295153572f;
    private const float Sin144 = 0.587785252292473129f;

    private readonly int _half;
    private readonly int[] _radix;
    private readonly int[] _twiddleOffset;
    private readonly float[] _twiddles;
    private readonly float[] _split;
    private readonly float[] _bufferA;
    private readonly float[] _bufferB;

    public RealFft(int size)
    {
        if (size < 2 || size % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "FFT size must be even and at least 2.");
        }
        Size = size;
        _half = size / 2;

        var radix = new List<int>();
        int rest = _half;
        foreach (int p in (ReadOnlySpan<int>)[4, 2, 3, 5])
        {
            while (rest % p == 0)
            {
                radix.Add(p);
                rest /= p;
            }
        }
        if (rest != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "FFT size / 2 must factor into 2, 3 and 5.");
        }
        _radix = [.. radix];

        // Stage twiddles: for stage length len and radix p (m = len / p), w^(r * j) with w = e^(-2 pi i / len),
        // stored per j as r = 1..p-1, interleaved (re, im).
        _twiddleOffset = new int[_radix.Length];
        int count = 0;
        int len = _half;
        for (int stage = 0; stage < _radix.Length; stage++)
        {
            _twiddleOffset[stage] = count;
            count += 2 * (len - len / _radix[stage]);
            len /= _radix[stage];
        }
        _twiddles = new float[count];
        len = _half;
        for (int stage = 0; stage < _radix.Length; stage++)
        {
            int p = _radix[stage];
            int m = len / p;
            int at = _twiddleOffset[stage];
            for (int j = 0; j < m; j++)
            {
                for (int r = 1; r < p; r++)
                {
                    double angle = -2.0 * Math.PI * r * j / len;
                    _twiddles[at++] = (float)Math.Cos(angle);
                    _twiddles[at++] = (float)Math.Sin(angle);
                }
            }
            len = m;
        }

        // Split step twiddles e^(-2 pi i k / N), k = 0..N/2-1.
        _split = new float[2 * _half];
        for (int k = 0; k < _half; k++)
        {
            double angle = -2.0 * Math.PI * k / size;
            _split[2 * k] = (float)Math.Cos(angle);
            _split[2 * k + 1] = (float)Math.Sin(angle);
        }
        _bufferA = new float[2 * _half];
        _bufferB = new float[2 * _half];
    }

    public int Size { get; }

    public int Bins => _half + 1;

    /// <summary>Transforms <see cref="Size"/> samples into <see cref="Bins"/> interleaved complex bins.</summary>
    public void Forward(ReadOnlySpan<float> input, Span<float> spectrum)
    {
        if (input.Length != Size || spectrum.Length != Size + 2)
        {
            throw new ArgumentException("Forward needs Size input samples and Size + 2 spectrum floats.");
        }
        int h = _half;
        fixed (float* s = spectrum, a = _bufferA, b = _bufferB, w = _split)
        {
            // z[k] = x[2k] + i x[2k+1] is exactly the interleaved input.
            input.CopyTo(_bufferA);
            float* z = Transform(a, b);
            s[0] = z[0] + z[1];
            s[1] = 0f;
            s[2 * h] = z[0] - z[1];
            s[2 * h + 1] = 0f;
            for (int k = 1; k < h; k++)
            {
                float zr = z[2 * k], zi = z[2 * k + 1];
                float cr = z[2 * (h - k)], ci = -z[2 * (h - k) + 1];
                // Fe = (Z[k] + conj Z[h-k]) / 2, Fo = (Z[k] - conj Z[h-k]) / 2i, X = Fe + w Fo.
                float er = 0.5f * (zr + cr), ei = 0.5f * (zi + ci);
                float or = 0.5f * (zi - ci), oi = -0.5f * (zr - cr);
                float wr = w[2 * k], wi = w[2 * k + 1];
                s[2 * k] = er + wr * or - wi * oi;
                s[2 * k + 1] = ei + wr * oi + wi * or;
            }
        }
    }

    /// <summary>Transforms <see cref="Bins"/> interleaved complex bins back into <see cref="Size"/> samples (1/N).</summary>
    public void Inverse(ReadOnlySpan<float> spectrum, Span<float> output)
    {
        if (output.Length != Size || spectrum.Length != Size + 2)
        {
            throw new ArgumentException("Inverse needs Size + 2 spectrum floats and Size output samples.");
        }
        int h = _half;
        fixed (float* s = spectrum, y = output, a = _bufferA, b = _bufferB, w = _split)
        {
            // Z[k] = Fe + i Fo with Fe = (X[k] + conj X[h-k]) / 2, Fo = conj(w) (X[k] - conj X[h-k]) / 2.
            // The inverse complex FFT runs as conj(FFT(conj Z)), so conj Z is stored here. Bins 0 and N/2 are real.
            a[0] = 0.5f * (s[0] + s[2 * h]);
            a[1] = -0.5f * (s[0] - s[2 * h]);
            for (int k = 1; k < h; k++)
            {
                float xr = s[2 * k], xi = s[2 * k + 1];
                float cr = s[2 * (h - k)], ci = -s[2 * (h - k) + 1];
                float er = 0.5f * (xr + cr), ei = 0.5f * (xi + ci);
                float dr = 0.5f * (xr - cr), di = 0.5f * (xi - ci);
                float wr = w[2 * k], wi = -w[2 * k + 1];
                float or = wr * dr - wi * di, oi = wr * di + wi * dr;
                a[2 * k] = er - oi;
                a[2 * k + 1] = -(ei + or);
            }
            float* z = Transform(a, b);
            float scale = 1f / h;
            for (int k = 0; k < h; k++)
            {
                y[2 * k] = z[2 * k] * scale;
                y[2 * k + 1] = -z[2 * k + 1] * scale;
            }
        }
    }

    /// <summary>Forward complex FFT of size N/2 on x (interleaved), using y as scratch; returns the result buffer.</summary>
    private float* Transform(float* x, float* y)
    {
        int len = _half;
        int stride = 1;
        fixed (float* twiddles = _twiddles)
        {
            for (int stage = 0; stage < _radix.Length; stage++)
            {
                int p = _radix[stage];
                int m = len / p;
                float* tw = twiddles + _twiddleOffset[stage];
                switch (p)
                {
                    case 4:
                        Radix4(x, y, stride, m, tw);
                        break;
                    case 2:
                        Radix2(x, y, stride, m, tw);
                        break;
                    case 3:
                        Radix3(x, y, stride, m, tw);
                        break;
                    default:
                        Radix5(x, y, stride, m, tw);
                        break;
                }
                float* swap = x;
                x = y;
                y = swap;
                len = m;
                stride *= p;
            }
        }
        return x;
    }

    // Stockham stage: inputs x[q + s (j + k m)], outputs y[q + s (p j + r)] = DFT_p(inputs)[r] * w^(r j).

    private static void Radix2(float* x, float* y, int s, int m, float* tw)
    {
        int sm = s * m;
        for (int j = 0; j < m; j++)
        {
            float w1r = tw[2 * j], w1i = tw[2 * j + 1];
            float* i0 = x + 2 * s * j;
            float* o0 = y + 2 * s * 2 * j;
            for (int q = 0; q < s; q++)
            {
                float* a = i0 + 2 * q;
                float* o = o0 + 2 * q;
                float a0r = a[0], a0i = a[1];
                float a1r = a[2 * sm], a1i = a[2 * sm + 1];
                o[0] = a0r + a1r;
                o[1] = a0i + a1i;
                float dr = a0r - a1r, di = a0i - a1i;
                o[2 * s] = dr * w1r - di * w1i;
                o[2 * s + 1] = dr * w1i + di * w1r;
            }
        }
    }

    private static void Radix3(float* x, float* y, int s, int m, float* tw)
    {
        int sm = s * m;
        for (int j = 0; j < m; j++)
        {
            float w1r = tw[4 * j], w1i = tw[4 * j + 1];
            float w2r = tw[4 * j + 2], w2i = tw[4 * j + 3];
            float* i0 = x + 2 * s * j;
            float* o0 = y + 2 * s * 3 * j;
            for (int q = 0; q < s; q++)
            {
                float* a = i0 + 2 * q;
                float* o = o0 + 2 * q;
                float a0r = a[0], a0i = a[1];
                float a1r = a[2 * sm], a1i = a[2 * sm + 1];
                float a2r = a[4 * sm], a2i = a[4 * sm + 1];
                float t1r = a1r + a2r, t1i = a1i + a2i;
                float t2r = a1r - a2r, t2i = a1i - a2i;
                float m1r = a0r - 0.5f * t1r, m1i = a0i - 0.5f * t1i;
                float m2r = Sin60 * t2i, m2i = -Sin60 * t2r;
                o[0] = a0r + t1r;
                o[1] = a0i + t1i;
                float y1r = m1r + m2r, y1i = m1i + m2i;
                float y2r = m1r - m2r, y2i = m1i - m2i;
                o[2 * s] = y1r * w1r - y1i * w1i;
                o[2 * s + 1] = y1r * w1i + y1i * w1r;
                o[4 * s] = y2r * w2r - y2i * w2i;
                o[4 * s + 1] = y2r * w2i + y2i * w2r;
            }
        }
    }

    private static void Radix4(float* x, float* y, int s, int m, float* tw)
    {
        int sm = s * m;
        for (int j = 0; j < m; j++)
        {
            float w1r = tw[6 * j], w1i = tw[6 * j + 1];
            float w2r = tw[6 * j + 2], w2i = tw[6 * j + 3];
            float w3r = tw[6 * j + 4], w3i = tw[6 * j + 5];
            float* i0 = x + 2 * s * j;
            float* o0 = y + 2 * s * 4 * j;
            for (int q = 0; q < s; q++)
            {
                float* a = i0 + 2 * q;
                float* o = o0 + 2 * q;
                float a0r = a[0], a0i = a[1];
                float a1r = a[2 * sm], a1i = a[2 * sm + 1];
                float a2r = a[4 * sm], a2i = a[4 * sm + 1];
                float a3r = a[6 * sm], a3i = a[6 * sm + 1];
                float t0r = a0r + a2r, t0i = a0i + a2i;
                float t1r = a0r - a2r, t1i = a0i - a2i;
                float t2r = a1r + a3r, t2i = a1i + a3i;
                float t3r = a1i - a3i, t3i = a3r - a1r; // (a1 - a3) * -i
                o[0] = t0r + t2r;
                o[1] = t0i + t2i;
                float y1r = t1r + t3r, y1i = t1i + t3i;
                float y2r = t0r - t2r, y2i = t0i - t2i;
                float y3r = t1r - t3r, y3i = t1i - t3i;
                o[2 * s] = y1r * w1r - y1i * w1i;
                o[2 * s + 1] = y1r * w1i + y1i * w1r;
                o[4 * s] = y2r * w2r - y2i * w2i;
                o[4 * s + 1] = y2r * w2i + y2i * w2r;
                o[6 * s] = y3r * w3r - y3i * w3i;
                o[6 * s + 1] = y3r * w3i + y3i * w3r;
            }
        }
    }

    private static void Radix5(float* x, float* y, int s, int m, float* tw)
    {
        int sm = s * m;
        for (int j = 0; j < m; j++)
        {
            float* w = tw + 8 * j;
            float* i0 = x + 2 * s * j;
            float* o0 = y + 2 * s * 5 * j;
            for (int q = 0; q < s; q++)
            {
                float* a = i0 + 2 * q;
                float* o = o0 + 2 * q;
                float a0r = a[0], a0i = a[1];
                float a1r = a[2 * sm], a1i = a[2 * sm + 1];
                float a2r = a[4 * sm], a2i = a[4 * sm + 1];
                float a3r = a[6 * sm], a3i = a[6 * sm + 1];
                float a4r = a[8 * sm], a4i = a[8 * sm + 1];
                float t1r = a1r + a4r, t1i = a1i + a4i;
                float t2r = a2r + a3r, t2i = a2i + a3i;
                float t3r = a1r - a4r, t3i = a1i - a4i;
                float t4r = a2r - a3r, t4i = a2i - a3i;
                float m1r = a0r + Cos72 * t1r + Cos144 * t2r, m1i = a0i + Cos72 * t1i + Cos144 * t2i;
                float m2r = a0r + Cos144 * t1r + Cos72 * t2r, m2i = a0i + Cos144 * t1i + Cos72 * t2i;
                // n = -i (u): (ur, ui) -> (ui, -ur)
                float u1r = Sin72 * t3r + Sin144 * t4r, u1i = Sin72 * t3i + Sin144 * t4i;
                float u2r = Sin144 * t3r - Sin72 * t4r, u2i = Sin144 * t3i - Sin72 * t4i;
                float n1r = u1i, n1i = -u1r;
                float n2r = u2i, n2i = -u2r;
                o[0] = a0r + t1r + t2r;
                o[1] = a0i + t1i + t2i;
                float y1r = m1r + n1r, y1i = m1i + n1i;
                float y2r = m2r + n2r, y2i = m2i + n2i;
                float y3r = m2r - n2r, y3i = m2i - n2i;
                float y4r = m1r - n1r, y4i = m1i - n1i;
                o[2 * s] = y1r * w[0] - y1i * w[1];
                o[2 * s + 1] = y1r * w[1] + y1i * w[0];
                o[4 * s] = y2r * w[2] - y2i * w[3];
                o[4 * s + 1] = y2r * w[3] + y2i * w[2];
                o[6 * s] = y3r * w[4] - y3i * w[5];
                o[6 * s + 1] = y3r * w[5] + y3i * w[4];
                o[8 * s] = y4r * w[6] - y4i * w[7];
                o[8 * s + 1] = y4r * w[7] + y4i * w[6];
            }
        }
    }
}
