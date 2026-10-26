using System.Runtime.InteropServices;

namespace WPods.Audio;

/// <summary>
/// WebRTC AudioProcessing capture path (webrtc_apm.dll = webrtc-audio-processing 2.1 + WPods C wrapper) in one of two
/// setups, see <see cref="NoiseSuppressor"/> and <see cref="Agc2"/>. 480-sample 48 kHz frames, floats in -1..1, in
/// place, no latency. The factories throw when webrtc_apm.dll is missing or the config is invalid. One caller at a time.
/// </summary>
internal sealed unsafe partial class WebRtcApm : IMicStage
{
    private void* _state;

    private WebRtcApm(void* state, string what)
    {
        if (state is null)
        {
            throw new InvalidOperationException($"{what} could not be initialized.");
        }
        _state = state;
    }

    /// <summary>
    /// WebRTC's high-pass filter, then the stationary noise suppressor at kVeryHigh (Wiener-type gain from a quantile
    /// noise estimate and a speech/noise likelihood model, computed on the 0-8 kHz band and applied to the upper bands
    /// too). No AEC, no AGC.
    /// </summary>
    public static WebRtcApm NoiseSuppressor() =>
        // Level: AudioProcessing::Config::NoiseSuppression::Level (kLow 0, kModerate 1, kHigh 2, kVeryHigh 3).
        new(wapm_create(3, 1), "WebRTC audio processing");

    /// <summary>
    /// WebRTC AGC2 (AudioProcessing GainController2): the adaptive digital controller (RNN voice activity detector,
    /// speech level estimator, saturation protector, noise level guard; gain only raises speech, 0 to max gain,
    /// towards -(headroom + saturation margin) dBFS), then the AGC2 limiter. No noise suppression.
    /// </summary>
    /// <param name="headroomDb">Target margin below 0 dBFS on top of the saturation margin (WebRTC 5).</param>
    /// <param name="initialGainDb">Gain before the first speech (WebRTC 15).</param>
    public static WebRtcApm Agc2(float headroomDb, float initialGainDb = 15f) =>
        // No high-pass, adaptive on; max gain 50 dB, gain slew 6 dB/s, max output noise -50 dBFS (the WebRTC
        // defaults); no fixed digital gain.
        new(wapm_create_agc2(0, 1, headroomDb, 50f, initialGainDb, 6f, -50f, 0f), "WebRTC AGC2");

    public void Process(Span<float> frame)
    {
        fixed (float* samples = frame)
        {
            _ = wapm_process(_state, samples);
        }
    }

    public void Dispose()
    {
        wapm_destroy(_state);
        _state = null;
    }

    [LibraryImport("webrtc_apm")]
    private static partial void* wapm_create(int noiseLevel, int highPass);

    [LibraryImport("webrtc_apm")]
    private static partial void* wapm_create_agc2(int highPass, int adaptive, float headroomDb, float maxGainDb,
        float initialGainDb, float maxGainChangeDbPerSecond, float maxOutputNoiseLevelDbfs, float fixedGainDb);

    [LibraryImport("webrtc_apm")]
    private static partial int wapm_process(void* state, float* frame);

    [LibraryImport("webrtc_apm")]
    private static partial void wapm_destroy(void* state);
}
