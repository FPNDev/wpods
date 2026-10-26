using WPods.Audio.Dsp;

namespace WPods.Audio.Enhance;

/// <summary>
/// Split-band de-esser: the side chain is the 5-9 kHz band (high-pass + low-pass), followed by a peak envelope
/// (1 ms attack, 50 ms release); above the threshold the part of the signal above the 5 kHz split
/// (signal - low-pass 5 kHz, so the bands always sum back to the input) is turned down by (1 - 1/ratio) dB per dB,
/// at most the max reduction. Everything below 5 kHz is untouched. IIR, no latency.
/// </summary>
internal sealed class DeEsser : IMicStage
{
    private readonly Biquad _split = Biquad.LowPass(Level.SampleRate, 5000f, 0.7071f);
    private readonly Biquad _sideHighPass = Biquad.HighPass(Level.SampleRate, 5000f, 0.7071f);
    private readonly Biquad _sideLowPass = Biquad.LowPass(Level.SampleRate, 9000f, 0.7071f);
    private readonly float[] _low = new float[Denoiser.FrameSize];
    private readonly float[] _side = new float[Denoiser.FrameSize];
    private readonly float _thresholdDb;
    // Envelope (amplitude) at the threshold; at or below it nothing is reduced.
    private readonly float _thresholdLin;
    private readonly float _slope;
    private readonly float _maxReductionDb;
    private readonly float _attack = Level.Coefficient(1f);
    private readonly float _release = Level.Coefficient(50f);
    private float _envelope;

    public DeEsser(float thresholdDb, float ratio = 4f, float maxReductionDb = 6f)
    {
        _thresholdDb = thresholdDb;
        _thresholdLin = Level.DbToGain(thresholdDb);
        _slope = 1f - 1f / ratio;
        _maxReductionDb = maxReductionDb;
    }

    public void Process(Span<float> frame)
    {
        Span<float> low = _low.AsSpan(0, frame.Length);
        Span<float> side = _side.AsSpan(0, frame.Length);
        frame.CopyTo(low);
        _split.Process(low);
        frame.CopyTo(side);
        _sideHighPass.Process(side);
        _sideLowPass.Process(side);
        for (int i = 0; i < frame.Length; i++)
        {
            float magnitude = MathF.Abs(side[i]);
            _envelope = magnitude + (magnitude > _envelope ? _attack : _release) * (_envelope - magnitude);
            if (_envelope <= _thresholdLin)
            {
                continue;
            }
            float overDb = Level.PowerDbPerLog2 * MathF.Log2(_envelope * _envelope + 1e-12f) - _thresholdDb;
            if (overDb > 0f)
            {
                float gain = float.Exp2(-MathF.Min(overDb * _slope, _maxReductionDb) * Level.Log2PerDb);
                frame[i] = low[i] + (frame[i] - low[i]) * gain;
            }
        }
    }
}
