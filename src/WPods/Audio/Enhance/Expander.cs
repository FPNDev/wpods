namespace WPods.Audio.Enhance;

/// <summary>
/// Downward expander (noise floor guard): an RMS detector (10 ms) feeds an envelope in dB with instant attack, a hold
/// and a release at a fixed dB/s rate; below the threshold the signal is pushed down by (ratio - 1) dB per dB, never
/// more than the floor. Speech above the threshold passes untouched; the hiss between words sinks by up to the floor
/// depth. No latency.
/// </summary>
internal sealed class Expander : IMicStage
{
    private readonly float _thresholdDb;
    private readonly float _ratioMinusOne;
    private readonly float _floorDb;
    private readonly int _holdSamples;
    private readonly float _releaseDbPerSample;
    private readonly float _detect = Level.Coefficient(10f);
    private float _power;
    private float _envelopeDb = -120f;
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
            float levelDb = Level.PowerToDb(_power);
            if (levelDb >= _envelopeDb)
            {
                _envelopeDb = levelDb;
                _hold = _holdSamples;
            }
            else if (_hold > 0)
            {
                _hold--;
            }
            else
            {
                _envelopeDb = MathF.Max(levelDb, _envelopeDb - _releaseDbPerSample);
            }
            float underDb = _envelopeDb - _thresholdDb;
            if (underDb < 0f)
            {
                frame[i] = x * Level.DbToGain(MathF.Max(underDb * _ratioMinusOne, _floorDb));
            }
        }
    }

    public void Dispose()
    {
    }
}
