namespace WPods.Audio.Enhance;

/// <summary>
/// Automatic speech leveller in the style of WebRTC AGC2 adaptive digital (own code): an RNNoise voice probability per
/// frame keys a speech level estimate (RMS of voiced frames, ~0.5 s average) and a noise floor estimate (frames with
/// almost no voice: falls fast, rises at most 5 dB/s so keyboard clicks between words do not lift it; starts at
/// -60 dBFS). Gain = target - speech level, limited to
/// [<see cref="MinGainDb"/>, max gain] and capped so the noise floor never comes out above the noise ceiling (AGC2
/// max_output_noise_level_dbfs). The gain moves at most 6 dB/s up (AGC2 max_gain_change_db_per_second) and 20 dB/s
/// down, ramped per sample across the frame. Runs after the pipeline's fixed x4 (like every stage), starts at 0 dB
/// and sets the final speech level itself; the gain range is relative to that x4 signal.
/// The audio does not pass through RNNoise: no latency. The constructor throws when rnnoise.dll is missing.
/// One caller at a time.
/// </summary>
internal sealed unsafe class Leveller : IMicStage
{
    private const int FrameSize = Denoiser.FrameSize;
    private const float RnNoiseScale = 32768f;
    private const float InitialGainDb = 0f;
    private const float MinGainDb = -24f;
    private const float SpeechThreshold = 0.90f; // voice probability from which a frame counts as speech
    private const float NoiseThreshold = 0.20f;
    private const float SpeechAlpha = 0.02f; // per voiced frame: ~0.5 s
    private const float InitialNoiseDb = -60f;
    private const float NoiseRiseDbPerFrame = 5f / 100f;
    private const float NoiseFallAlpha = 0.5f;
    private const float MaxUpDbPerFrame = 6f / 100f;
    private const float MaxDownDbPerFrame = 20f / 100f;

    private readonly Denoiser _vad = new();
    private readonly float _targetDb;
    private readonly float _maxGainDb;
    private readonly float _noiseCeilingDb;
    private float _speechPower;
    private float _noiseDb = InitialNoiseDb;
    private float _gainDb = InitialGainDb;

    /// <param name="targetDb">Speech RMS level to reach, dBFS.</param>
    /// <param name="maxGainDb">Largest gain on top of the x4, dB (AGC2 max_gain_db 30 at x1 = 18 after x4).</param>
    /// <param name="noiseCeilingDb">Highest output noise floor, dBFS (AGC2 max_output_noise_level_dbfs).</param>
    public Leveller(float targetDb, float maxGainDb = 18f, float noiseCeilingDb = -50f)
    {
        _targetDb = targetDb;
        _maxGainDb = maxGainDb;
        _noiseCeilingDb = noiseCeilingDb;
        // Start as if the speech level were already measured at the level that gives the initial gain.
        _speechPower = MathF.Pow(10f, (targetDb - InitialGainDb) / 10f);
    }

    public void Process(Span<float> frame)
    {
        float* input = stackalloc float[FrameSize];
        float* unused = stackalloc float[FrameSize];
        float power = 0f;
        for (int i = 0; i < FrameSize; i++)
        {
            float x = frame[i];
            power += x * x;
            input[i] = x * RnNoiseScale;
        }
        power /= FrameSize;
        float voice = _vad.Process(unused, input);
        if (voice >= SpeechThreshold)
        {
            _speechPower += SpeechAlpha * (power - _speechPower);
        }
        else if (voice < NoiseThreshold)
        {
            float frameDb = Level.PowerToDb(power);
            _noiseDb = frameDb < _noiseDb
                ? _noiseDb + NoiseFallAlpha * (frameDb - _noiseDb)
                : MathF.Min(frameDb, _noiseDb + NoiseRiseDbPerFrame);
        }
        float targetGainDb = Math.Clamp(_targetDb - Level.PowerToDb(_speechPower), MinGainDb, _maxGainDb);
        targetGainDb = MathF.Min(targetGainDb, _noiseCeilingDb - _noiseDb);
        float startGain = Level.DbToGain(_gainDb);
        _gainDb += Math.Clamp(targetGainDb - _gainDb, -MaxDownDbPerFrame, MaxUpDbPerFrame);
        float step = (Level.DbToGain(_gainDb) - startGain) / FrameSize;
        for (int i = 0; i < FrameSize; i++)
        {
            frame[i] *= startGain + step * (i + 1);
        }
    }

    public void Dispose() => _vad.Dispose();
}
