using WPods.Audio.Dsp;

namespace WPods.Audio.Onnx;

/// <summary>What a 16 kHz model stage does with the 8-24 kHz band it cannot see.</summary>
internal enum HighBand
{
    /// <summary>Scaled by the model's own gain in the 4-8 kHz band (enhanced / noisy energy, per 10 ms frame).</summary>
    GainTransfer,

    /// <summary>Passed through unchanged (delayed to stay aligned).</summary>
    Pass,
}

/// <summary>
/// Runs a 16 kHz model on the low band of the 48 kHz stream. <see cref="Split"/> turns a 480-sample frame into 160
/// samples at 16 kHz (<see cref="Downsampler48To16"/>) and keeps the rest (input minus the 16 kHz round trip, so low +
/// high = input exactly); the model processes those 160 samples with a fixed delay of modelLatency samples;
/// <see cref="Merge"/> upsamples the result and adds the high band, delayed to match and handled per
/// <see cref="HighBand"/>. Total delay = 270 + 3 x modelLatency at 48 kHz.
/// No allocation per call; one caller at a time.
/// </summary>
internal sealed class BandSplit16k
{
    private const int FrameSize = Denoiser.FrameSize;
    public const int Block = FrameSize / Resampler.Factor;

    private const int RoundTrip = Downsampler48To16.LatencySamples * Resampler.Factor + Upsampler16To48.LatencySamples;
    private const float BandFloor = 1e-10f;

    private readonly HighBand _mode;
    private readonly Downsampler48To16 _down = new();
    private readonly Upsampler16To48 _upEnhanced = new();
    private readonly Upsampler16To48 _upNoisy = new();
    private readonly Biquad _noisyBand = Biquad.HighPass(16000, 4000, 0.707f);
    private readonly Biquad _enhancedBand = Biquad.HighPass(16000, 4000, 0.707f);
    private readonly float[] _inputDelay = new float[RoundTrip + FrameSize];
    private readonly float[] _highDelay;
    private readonly float[] _noisyDelay;
    private readonly float[] _high = new float[FrameSize];
    private readonly float[] _lowUp = new float[FrameSize];
    private readonly float[] _scratch = new float[Block];
    private float _gain = 1f;
    private float _noisyEnergy;

    /// <param name="modelLatency">Delay of the 16 kHz model path in 16 kHz samples (including its own buffering).</param>
    /// <param name="mode">High-band handling.</param>
    public BandSplit16k(int modelLatency, HighBand mode)
    {
        _mode = mode;
        _highDelay = new float[Resampler.Factor * modelLatency + FrameSize];
        _noisyDelay = new float[modelLatency + Block];
    }

    /// <summary>Takes a 480-sample 48 kHz frame, writes its 160-sample 16 kHz low band for the model.</summary>
    public void Split(ReadOnlySpan<float> frame48k, Span<float> low16k)
    {
        _down.Process(frame48k, low16k);
        // High band = input delayed by the round trip minus the round trip itself.
        _upNoisy.Process(low16k, _lowUp);
        Delay(_inputDelay, frame48k, _high);
        for (int i = 0; i < FrameSize; i++)
        {
            _high[i] -= _lowUp[i];
        }
        Delay(_highDelay, _high, _high);
        if (_mode == HighBand.GainTransfer)
        {
            // Noisy 4-8 kHz energy of the block the model returns now (modelLatency samples ago).
            Delay(_noisyDelay, low16k, _scratch);
            _noisyBand.Process(_scratch);
            _noisyEnergy = SumSquares(_scratch);
        }
    }

    /// <summary>Takes the model's 160-sample output (the low band, delayed by modelLatency) and writes the 48 kHz frame.</summary>
    public void Merge(ReadOnlySpan<float> enhanced16k, Span<float> frame48k)
    {
        _upEnhanced.Process(enhanced16k, frame48k);
        float target = 1f;
        if (_mode == HighBand.GainTransfer)
        {
            enhanced16k.CopyTo(_scratch);
            _enhancedBand.Process(_scratch);
            float enhanced = SumSquares(_scratch);
            target = MathF.Min(1f, MathF.Sqrt((enhanced + BandFloor) / (_noisyEnergy + BandFloor)));
        }
        // Linear gain ramp across the frame avoids steps between frames.
        float step = (target - _gain) / FrameSize;
        for (int i = 0; i < FrameSize; i++)
        {
            _gain += step;
            frame48k[i] += _gain * _high[i];
        }
        _gain = target;
    }

    private static float SumSquares(ReadOnlySpan<float> x)
    {
        float sum = 0f;
        foreach (float v in x)
        {
            sum += v * v;
        }
        return sum;
    }

    /// <summary>Delay line: line holds (delay + block) samples; output may alias input.</summary>
    private static void Delay(float[] line, ReadOnlySpan<float> input, Span<float> output)
    {
        int delay = line.Length - input.Length;
        input.CopyTo(line.AsSpan(delay));
        line.AsSpan(0, output.Length).CopyTo(output);
        line.AsSpan(output.Length, delay).CopyTo(line);
    }
}
