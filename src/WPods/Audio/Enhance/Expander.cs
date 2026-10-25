namespace WPods.Audio.Enhance;

/// <summary>
/// Downward expander (noise floor guard): an RMS detector (10 ms) feeds an envelope in dB with instant attack, a hold
/// and a release at a fixed dB/s rate; below the threshold the signal is pushed down by (ratio - 1) dB per dB, never
/// more than the floor. Speech above the threshold passes untouched; the hiss between words sinks by up to the floor
/// depth. No latency.
/// </summary>
internal sealed class Expander : IMicStage
{
    // dB of a power from its log2 (10 * log10(2)); log2 of a gain from its dB (log2(10) / 20).
    private const float PowerDbPerLog2 = 3.01029996f;
    private const float Log2PerDb = 0.166096404f;

    private readonly float _thresholdDb;
    private readonly float _ratioMinusOne;
    private readonly float _floorDb;
    private readonly int _holdSamples;
    private readonly float _releaseDbPerSample;
    private readonly float _detect = Level.Coefficient(10f);
    private float _power;
    private float _envelopeDb = -120f;
    // Detector power (+ floor) the envelope was last set from; the envelope stays at its dB while holding.
    private float _envelopePower;
    private int _hold;

    public Expander(float thresholdDb, float ratio = 2f, float floorDb = -15f, float holdMs = 100f, float releaseDbPerSecond = 100f)
    {
        _thresholdDb = thresholdDb;
        _ratioMinusOne = ratio - 1f;
        _floorDb = floorDb;
        _holdSamples = (int)(holdMs * Level.SampleRate / 1000f);
        _releaseDbPerSample = releaseDbPerSecond / Level.SampleRate;
    }

    public void Process(Span<float> frame)
    {
        for (int i = 0; i < frame.Length; i++)
        {
            float x = frame[i];
            _power = x * x + _detect * (_power - x * x);
            float power = _power + 1e-12f;
            if (_hold > 0 && power < _envelopePower)
            {
                // Holding below the envelope: the level in dB is not needed.
                _hold--;
            }
            else
            {
                // Holding at or above the envelope power means the level in dB is at or above the envelope (attack).
                float levelDb = PowerDbPerLog2 * MathF.Log2(power);
                if (levelDb >= _envelopeDb)
                {
                    _envelopeDb = levelDb;
                    _envelopePower = power;
                    _hold = _holdSamples;
                }
                else
                {
                    _envelopeDb = MathF.Max(levelDb, _envelopeDb - _releaseDbPerSample);
                }
            }
            float underDb = _envelopeDb - _thresholdDb;
            if (underDb < 0f)
            {
                frame[i] = x * float.Exp2(MathF.Max(underDb * _ratioMinusOne, _floorDb) * Log2PerDb);
            }
        }
    }

    public void Dispose()
    {
    }
}
