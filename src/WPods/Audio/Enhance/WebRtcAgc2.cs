using System.Runtime.InteropServices;

namespace WPods.Audio.Enhance;

/// <summary>
/// WebRTC AGC2 (AudioProcessing GainController2 in webrtc_apm.dll = webrtc-audio-processing 2.1 + WPods C wrapper):
/// optional high-pass filter, then the adaptive digital controller (RNN voice activity detector, speech level
/// estimator, saturation protector, noise level guard; gain only raises speech, 0 to max gain, towards
/// -(headroom + saturation margin) dBFS), then a fixed digital gain and the AGC2 limiter. No noise suppression.
/// 480-sample 48 kHz frames, floats in -1..1, in place, no latency. The constructor throws when webrtc_apm.dll is
/// missing or the config is invalid. One caller at a time.
/// </summary>
internal sealed unsafe partial class WebRtcAgc2 : IMicStage
{
    private void* _state;

    /// <param name="adaptive">Adaptive digital controller on.</param>
    /// <param name="headroomDb">Adaptive: target margin below 0 dBFS on top of the saturation margin (WebRTC 5).</param>
    /// <param name="maxGainDb">Adaptive: largest gain (WebRTC 50).</param>
    /// <param name="initialGainDb">Adaptive: gain before the first speech (WebRTC 15).</param>
    /// <param name="maxGainChangeDbPerSecond">Adaptive: gain slew (WebRTC 6).</param>
    /// <param name="maxOutputNoiseDb">Adaptive: highest output noise floor, dBFS (WebRTC -50).</param>
    /// <param name="fixedGainDb">Fixed digital gain before the limiter, 0..90 dB.</param>
    /// <param name="highPass">WebRTC high-pass filter first.</param>
    public WebRtcAgc2(bool adaptive, float headroomDb = 5f, float maxGainDb = 50f, float initialGainDb = 15f,
        float maxGainChangeDbPerSecond = 6f, float maxOutputNoiseDb = -50f, float fixedGainDb = 0f, bool highPass = false)
    {
        _state = wapm_create_agc2(highPass ? 1 : 0, adaptive ? 1 : 0, headroomDb, maxGainDb, initialGainDb,
            maxGainChangeDbPerSecond, maxOutputNoiseDb, fixedGainDb);
        if (_state is null)
        {
            throw new InvalidOperationException("WebRTC AGC2 could not be initialized.");
        }
    }

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
    private static partial void* wapm_create_agc2(int highPass, int adaptive, float headroomDb, float maxGainDb,
        float initialGainDb, float maxGainChangeDbPerSecond, float maxOutputNoiseLevelDbfs, float fixedGainDb);

    [LibraryImport("webrtc_apm")]
    private static partial int wapm_process(void* state, float* frame);

    [LibraryImport("webrtc_apm")]
    private static partial void wapm_destroy(void* state);
}
