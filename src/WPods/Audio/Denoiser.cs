using System.Runtime.InteropServices;

namespace WPods.Audio;

/// <summary>RNNoise state: 480-sample 48 kHz frames, floats in 16-bit scale. One caller at a time.</summary>
internal sealed unsafe partial class Denoiser : IDisposable
{
    public const int FrameSize = 480;

    private void* _state;

    public Denoiser()
    {
        _state = rnnoise_create(null);
        if (_state is null)
        {
            throw new InvalidOperationException("RNNoise could not be initialized.");
        }
    }

    /// <summary>
    /// Denoises one frame and returns RNNoise's voice probability (0..1) for this input frame. The output lags the
    /// input by one frame (RNNoise's analysis window).
    /// </summary>
    public float Process(float* output, float* input) => rnnoise_process_frame(_state, output, input);

    public void Dispose()
    {
        rnnoise_destroy(_state);
        _state = null;
    }

    [LibraryImport("rnnoise")]
    private static partial void* rnnoise_create(void* model);

    [LibraryImport("rnnoise")]
    private static partial float rnnoise_process_frame(void* state, float* output, float* input);

    [LibraryImport("rnnoise")]
    private static partial void rnnoise_destroy(void* state);
}
