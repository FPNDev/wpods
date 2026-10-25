using System.Runtime.InteropServices;

namespace WPods.Audio;

/// <summary>
/// DeepFilterNet 3 (deepfilter.dll = unmodified libDF v0.5.6 C API, model
/// DeepFilterNet3_onnx.tar.gz next to the exe): 480-sample 48 kHz frames (hop size checked at
/// creation), floats in -1..1. No attenuation limit, post filter off until set, libDF stage thresholds (-10 / 30 /
/// 20 dB). The output lags the input by the STFT window plus the model lookahead. One caller at a time.
/// </summary>
internal sealed unsafe partial class DeepFilter : IDisposable
{
    public const string ModelFile = "DeepFilterNet3_onnx.tar.gz";

    // >= 100 dB = no limit in libDF (full suppression).
    private const float AttenuationLimitDb = 100f;

    // Length of models/DeepFilterNet3_onnx.tar.gz in the libDF v0.5.6 source tarball (native/build-deepfilter.ps1).
    // No SHA-256 is pinned for the model alone (only for the whole tarball), so the length is checked instead.
    private const long ModelLength = 7983136;

    private void* _state;

    public DeepFilter()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ModelFile);
        // df_create aborts the process (Rust panic) when the model cannot be loaded, so a missing (FileNotFoundException),
        // unreadable (IOException, UnauthorizedAccessException) or truncated file is caught here.
        using (FileStream model = File.OpenRead(path))
        {
            if (model.Length != ModelLength)
            {
                throw new InvalidDataException($"DeepFilterNet model is {model.Length} bytes, expected {ModelLength}: {path}");
            }
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

    /// <summary>Post filter strength (libDF suggests 0..0.05); 0 turns it off.</summary>
    public void SetPostFilterBeta(float beta) => df_set_post_filter_beta(_state, beta);

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
    private static partial void df_set_post_filter_beta(void* state, float beta);

    [LibraryImport("deepfilter")]
    private static partial float df_process_frame(void* state, float* input, float* output);

    [LibraryImport("deepfilter")]
    private static partial void df_free(void* state);
}
