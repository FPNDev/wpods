using System.Runtime.InteropServices;

namespace WPods.Audio.Classic;

/// <summary>
/// Suppression strength of the WebRTC noise suppressor (AudioProcessing::Config::NoiseSuppression::Level: kLow 0,
/// kModerate 1, kHigh 2, kVeryHigh 3; only the level in use is listed).
/// </summary>
internal enum WebRtcNoiseLevel
{
    VeryHigh = 3,
}

/// <summary>
/// WebRTC AudioProcessing capture path (webrtc_apm.dll = webrtc-audio-processing 2.1 + WPods C wrapper): stationary
/// noise suppressor (Wiener-type gain from a quantile noise estimate and a speech/noise likelihood model, computed on
/// the 0-8 kHz band and applied to the upper bands too), optionally preceded by WebRTC's high-pass filter. No AEC, no
/// AGC. 480-sample 48 kHz frames, floats in -1..1, in place. One caller at a time.
/// </summary>
internal sealed unsafe partial class WebRtcNoiseSuppressor : IMicStage
{
    private void* _state;

    public WebRtcNoiseSuppressor(WebRtcNoiseLevel level, bool highPass = false)
    {
        _state = wapm_create((int)level, highPass ? 1 : 0);
        if (_state is null)
        {
            throw new InvalidOperationException("WebRTC audio processing could not be initialized.");
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
    private static partial void* wapm_create(int noiseLevel, int highPass);

    [LibraryImport("webrtc_apm")]
    private static partial int wapm_process(void* state, float* frame);

    [LibraryImport("webrtc_apm")]
    private static partial void wapm_destroy(void* state);
}
