using System.Runtime.InteropServices;

namespace WPods.Audio;

/// <summary>
/// DeepFilterNet 3 (deepfilter.dll = libDF v0.5.6 C API, model DeepFilterNet3_onnx.tar.gz next to the exe):
/// 480-sample 48 kHz frames (hop size checked at creation), floats in -1..1, no attenuation limit, post filter off.
/// The output lags the input by the STFT window plus the model lookahead. One caller at a time.
/// </summary>
internal sealed unsafe partial class DeepFilter : IDisposable
{
    public const string ModelFile = "DeepFilterNet3_onnx.tar.gz";

    // >= 100 dB = no limit in libDF (full suppression).
    private const float AttenuationLimitDb = 100f;

    private void* _state;

    public DeepFilter()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ModelFile);
        // df_create aborts the process (Rust panic) when the model cannot be loaded, so a missing file is caught here.
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"DeepFilterNet model not found: {path}");
        }
        _state = df_create(path, AttenuationLimitDb);
        if (_state is null)
        {
            throw new InvalidOperationException("DeepFilterNet could not be initialized.");
        }
        nuint hop = df_get_frame_length(_state);
        if (hop != Denoiser.FrameSize)
        {
            Dispose();
            throw new InvalidOperationException($"DeepFilterNet hop size {hop}, expected {Denoiser.FrameSize}.");
        }
    }

    /// <summary>Enhances one frame; returns the local SNR estimate in dB.</summary>
    public float Process(float* output, float* input) => df_process_frame(_state, input, output);

    public void Dispose()
    {
        df_free(_state);
        _state = null;
    }

    [LibraryImport("deepfilter", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void* df_create(string path, float attenuationLimitDb);

    [LibraryImport("deepfilter")]
    private static partial nuint df_get_frame_length(void* state);

    [LibraryImport("deepfilter")]
    private static partial float df_process_frame(void* state, float* input, float* output);

    [LibraryImport("deepfilter")]
    private static partial void df_free(void* state);
}
