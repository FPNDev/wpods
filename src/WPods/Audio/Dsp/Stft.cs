namespace WPods.Audio.Dsp;

/// <summary>Analysis and synthesis window of an <see cref="Stft"/> (periodic, the same shape on both sides).</summary>
internal enum StftWindow
{
    /// <summary>sqrt(Hann): power-complementary at 50 % overlap.</summary>
    SqrtHann,

    /// <summary>Vorbis power-complementary window (DeepFilterNet's choice): sin(pi/2 sin^2(pi (n + 0.5) / W)).</summary>
    Vorbis,
}

/// <summary>
/// Streaming STFT / inverse STFT (weighted overlap-add). Each <see cref="Analyze"/> call takes <see cref="Hop"/> new
/// samples and returns the spectrum of the last <see cref="WindowSize"/> samples (windowed, zero-padded to
/// the FFT size, unnormalized <see cref="RealFft"/> layout: <see cref="Bins"/> interleaved re/im pairs).
/// Each <see cref="Synthesize"/> call takes one spectrum and returns <see cref="Hop"/> samples. The synthesis window
/// is divided by the overlap-added product of both windows, so an unmodified spectrum reconstructs the input exactly,
/// delayed by <see cref="LatencySamples"/> = WindowSize - Hop. Analysis and synthesis states are independent.
/// No allocation per call; one caller at a time.
/// </summary>
internal sealed class Stft
{
    private readonly RealFft _fft;
    private readonly float[] _analysisWindow;
    private readonly float[] _synthesisWindow;
    private readonly float[] _input;
    private readonly float[] _frame;
    private readonly float[] _overlap;

    /// <param name="fftSize">FFT size (even, half factors into 2, 3, 5).</param>
    /// <param name="hop">Samples per call, 1..windowSize.</param>
    /// <param name="window">Window shape.</param>
    /// <param name="windowSize">Window length, at most fftSize; 0 means fftSize.</param>
    public Stft(int fftSize, int hop, StftWindow window = StftWindow.SqrtHann, int windowSize = 0)
    {
        if (windowSize == 0)
        {
            windowSize = fftSize;
        }
        if (windowSize < 1 || windowSize > fftSize)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "Window size must be 1..fftSize.");
        }
        if (hop < 1 || hop > windowSize)
        {
            throw new ArgumentOutOfRangeException(nameof(hop), hop, "Hop must be 1..windowSize.");
        }
        _fft = new RealFft(fftSize);
        Hop = hop;
        WindowSize = windowSize;

        _analysisWindow = new float[windowSize];
        for (int n = 0; n < windowSize; n++)
        {
            double hann = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / windowSize);
            double sine = Math.Sin(Math.PI * (n + 0.5) / windowSize);
            _analysisWindow[n] = (float)(window switch
            {
                StftWindow.SqrtHann => Math.Sqrt(hann),
                _ => Math.Sin(0.5 * Math.PI * sine * sine),
            });
        }

        // Output sample n of a frame is summed with the frames hop, 2 hop, ... away: divide by that window sum.
        _synthesisWindow = new float[windowSize];
        for (int n = 0; n < windowSize; n++)
        {
            double sum = 0;
            for (int j = n % hop; j < windowSize; j += hop)
            {
                sum += (double)_analysisWindow[j] * _analysisWindow[j];
            }
            if (sum <= 1e-9)
            {
                throw new ArgumentException("Window and hop do not overlap-add to a usable sum.", nameof(hop));
            }
            _synthesisWindow[n] = (float)(_analysisWindow[n] / sum);
        }

        _input = new float[windowSize];
        _frame = new float[fftSize];
        _overlap = new float[windowSize];
    }

    public int Hop { get; }

    public int WindowSize { get; }

    /// <summary>Complex bins per spectrum (FftSize / 2 + 1); a spectrum span holds 2 * Bins floats.</summary>
    public int Bins => _fft.Bins;

    /// <summary>Input-to-output delay of an analysis + synthesis round trip, in samples.</summary>
    public int LatencySamples => WindowSize - Hop;

    /// <summary>Takes <see cref="Hop"/> new samples and writes 2 * <see cref="Bins"/> floats.</summary>
    public void Analyze(ReadOnlySpan<float> input, Span<float> spectrum)
    {
        if (input.Length != Hop)
        {
            throw new ArgumentException("Analyze needs Hop input samples.", nameof(input));
        }
        int keep = WindowSize - Hop;
        _input.AsSpan(Hop).CopyTo(_input);
        input.CopyTo(_input.AsSpan(keep));
        for (int n = 0; n < WindowSize; n++)
        {
            _frame[n] = _input[n] * _analysisWindow[n];
        }
        _frame.AsSpan(WindowSize).Clear();
        _fft.Forward(_frame, spectrum);
    }

    /// <summary>Takes 2 * <see cref="Bins"/> floats and writes <see cref="Hop"/> output samples.</summary>
    public void Synthesize(ReadOnlySpan<float> spectrum, Span<float> output)
    {
        if (output.Length != Hop)
        {
            throw new ArgumentException("Synthesize writes Hop output samples.", nameof(output));
        }
        _fft.Inverse(spectrum, _frame);
        for (int n = 0; n < WindowSize; n++)
        {
            _overlap[n] += _frame[n] * _synthesisWindow[n];
        }
        _overlap.AsSpan(0, Hop).CopyTo(output);
        _overlap.AsSpan(Hop).CopyTo(_overlap);
        _overlap.AsSpan(WindowSize - Hop).Clear();
    }
}
