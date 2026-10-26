using WPods.Audio.Dsp;

namespace WPods.Audio.Modes;

/// <summary>
/// Residual spectral post-filter (Wiener-type) for noise a model lets through while speech is present (between
/// pitch harmonics, coarse high bands). Wraps the model stage: the noise power is tracked on the NOISY input (the model
/// output is near silent between words, so it carries no usable noise estimate), the per-bin gain
/// G = max(1 - a N / |Y|^2, floor) is computed from the noisy input delayed by the model latency, smoothed over time
/// and applied to the model output. Without a model it is a plain Wiener-type suppressor on its own input.
/// STFT 960 / 480 sqrt-Hann (50 Hz bins), adds 480 samples (10 ms) to the model latency.
/// Noise tracking: power smoothed over about 45 ms (0.8 per hop), continuous minimum that rises at most 5 dB/s.
/// One caller at a time, no allocation per frame.
/// </summary>
internal sealed class ResidualFilterStage : IMicStage
{
    private const int FftSize = 960;
    private const int Hop = Denoiser.FrameSize;
    private const float PowerSmoothing = 0.8f;
    private const float NoiseRisePerHop = 1.0116f; // 10^(5 dB/s * 0.01 s / 10)
    private const float PowerFloor = 1e-12f;
    private const float SilenceLevel = 1e-6f; // about -120 dBFS
    private const float Oversubtraction = 2f;
    private const float GainSmoothing = 0.7f;
    private static readonly float Floor = MathF.Pow(10f, -20f / 20f);

    private readonly IMicStage? _model;
    private readonly int _modelLatency;
    private readonly Stft _output = new(FftSize, Hop);
    private readonly Stft _reference = new(FftSize, Hop);
    private readonly float[] _delay;
    private readonly float[] _outputSpectrum;
    private readonly float[] _referenceSpectrum;
    private readonly float[] _power;
    private readonly float[] _noise;
    private readonly float[] _gain;
    private bool _started;

    /// <param name="model">Stage to post-filter (owned, disposed with this one); null = filter the input itself.</param>
    /// <param name="modelLatencySamples">Input-to-output delay of <paramref name="model"/> in samples.</param>
    public ResidualFilterStage(IMicStage? model = null, int modelLatencySamples = 0)
    {
        _model = model;
        _modelLatency = modelLatencySamples;
        _delay = new float[_modelLatency + Hop];
        _outputSpectrum = new float[2 * _output.Bins];
        _referenceSpectrum = new float[2 * _output.Bins];
        _power = new float[_output.Bins];
        _noise = new float[_output.Bins];
        _gain = new float[_output.Bins];
        Array.Fill(_gain, 1f);
    }

    public void Process(Span<float> frame)
    {
        ReadOnlySpan<float> reference = frame;
        if (_model is not null)
        {
            // Delay line: after the shift, _delay[0..Hop) is the input from _modelLatency samples ago.
            Array.Copy(_delay, Hop, _delay, 0, _modelLatency);
            frame.CopyTo(_delay.AsSpan(_modelLatency));
            reference = _delay.AsSpan(0, Hop);
        }
        // Digital silence (stream start, delay line still empty) would pin the minimum tracker at zero: such frames
        // keep the previous estimate and gains.
        bool track = false;
        foreach (float x in reference)
        {
            if (MathF.Abs(x) > SilenceLevel)
            {
                track = true;
                break;
            }
        }
        if (_model is null)
        {
            _output.Analyze(frame, _outputSpectrum);
            _outputSpectrum.CopyTo(_referenceSpectrum, 0);
        }
        else
        {
            _reference.Analyze(reference, _referenceSpectrum);
            _model.Process(frame);
            _output.Analyze(frame, _outputSpectrum);
        }
        if (track)
        {
            UpdateGains();
        }
        for (int k = 0; k < _gain.Length; k++)
        {
            _outputSpectrum[2 * k] *= _gain[k];
            _outputSpectrum[2 * k + 1] *= _gain[k];
        }
        _output.Synthesize(_outputSpectrum, frame);
    }

    public void Dispose() => _model?.Dispose();

    private void UpdateGains()
    {
        for (int k = 0; k < _gain.Length; k++)
        {
            float re = _referenceSpectrum[2 * k];
            float im = _referenceSpectrum[2 * k + 1];
            float p = re * re + im * im + PowerFloor;
            float power = _started ? PowerSmoothing * _power[k] + (1f - PowerSmoothing) * p : p;
            _power[k] = power;
            _noise[k] = _started ? MathF.Min(power, _noise[k] * NoiseRisePerHop) : power;
            float gain = MathF.Max(1f - Oversubtraction * _noise[k] / p, Floor);
            _gain[k] = GainSmoothing * _gain[k] + (1f - GainSmoothing) * gain;
        }
        _started = true;
    }
}
