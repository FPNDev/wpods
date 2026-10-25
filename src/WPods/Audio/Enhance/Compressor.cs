namespace WPods.Audio.Enhance;

/// <summary>
/// Feed-forward downward compressor: RMS detector (10 ms), soft knee, gain smoothed in dB with separate attack and
/// release, make-up gain and a dry/wet mix (mix below 1 = parallel compression). No look-ahead, no latency.
/// </summary>
internal sealed class Compressor : IMicStage
{
    private readonly float _thresholdDb;
    private readonly float _slope;
    private readonly float _kneeDb;
    private readonly float _makeUp;
    private readonly float _mix;
    private readonly float _detect = Level.Coefficient(10f);
    private readonly float _attack;
    private readonly float _release;
    private float _power;
    private float _gainDb;

    public Compressor(float thresholdDb, float ratio, float kneeDb, float attackMs, float releaseMs, float makeUpDb, float mix = 1f)
    {
        _thresholdDb = thresholdDb;
        _slope = 1f - 1f / ratio;
        _kneeDb = kneeDb;
        _makeUp = Level.DbToGain(makeUpDb);
        _mix = mix;
        _attack = Level.Coefficient(attackMs);
        _release = Level.Coefficient(releaseMs);
    }

    public void Process(Span<float> frame)
    {
        for (int i = 0; i < frame.Length; i++)
        {
            frame[i] = Process(frame[i]);
        }
    }

    /// <summary>Compresses one sample.</summary>
    public float Process(float x)
    {
        _power = x * x + _detect * (_power - x * x);
        float targetDb = GainDb(Level.PowerToDb(_power));
        float coefficient = targetDb < _gainDb ? _attack : _release;
        _gainDb = targetDb + coefficient * (_gainDb - targetDb);
        float wet = x * Level.DbToGain(_gainDb) * _makeUp;
        return x + _mix * (wet - x);
    }

    public void Dispose()
    {
    }

    private float GainDb(float levelDb)
    {
        float over = levelDb - _thresholdDb;
        if (2f * over <= -_kneeDb)
        {
            return 0f;
        }
        if (2f * over < _kneeDb)
        {
            float x = over + _kneeDb / 2f;
            return -_slope * x * x / (2f * _kneeDb);
        }
        return -_slope * over;
    }
}
