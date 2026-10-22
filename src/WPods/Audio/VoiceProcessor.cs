using WPods.Core;

namespace WPods.Audio;

/// <summary>
/// Mic processing for every mode except Wide Spectrum (raw), one 480-sample 48 kHz float frame (-1..1 full scale) at a
/// time, in place: Standard = RNNoise + speech gate; Voice Isolation = DeepFilterNet 3 + speech gate driven by a
/// parallel RNNoise VAD on the same input. DeepFilterNet is created on the first Voice Isolation frame; if it cannot
/// load, Voice Isolation falls back to RNNoise + gate. One caller at a time (the session read loop); no allocation per
/// frame.
/// </summary>
internal sealed unsafe class VoiceProcessor : IDisposable
{
    private const int FrameSize = Denoiser.FrameSize;
    // RNNoise takes floats in 16-bit scale; the pipeline runs in -1..1.
    private const float RnNoiseScale = 32768f;

    private readonly Denoiser _rnnoise = new();
    private readonly SpeechGate _gate = new();
    private Denoiser? _vad;
    private DeepFilter? _deepFilter;
    private bool _deepFilterFailed;

    public void Process(Span<float> frame, MicMode mode)
    {
        fixed (float* samples = frame)
        {
            if (mode == MicMode.VoiceIsolation && EnsureDeepFilter())
            {
                DeepFilter(samples);
            }
            else
            {
                RnNoise(samples);
            }
        }
    }

    public void Dispose()
    {
        _rnnoise.Dispose();
        _vad?.Dispose();
        _deepFilter?.Dispose();
    }

    private void RnNoise(float* frame)
    {
        float* input = stackalloc float[FrameSize];
        float* denoised = stackalloc float[FrameSize];
        for (int i = 0; i < FrameSize; i++)
        {
            input[i] = frame[i] * RnNoiseScale;
        }
        _gate.Apply(denoised, _rnnoise.Process(denoised, input));
        for (int i = 0; i < FrameSize; i++)
        {
            frame[i] = denoised[i] / RnNoiseScale;
        }
    }

    private void DeepFilter(float* frame)
    {
        float* enhanced = stackalloc float[FrameSize];
        float* vadInput = stackalloc float[FrameSize];
        float* unused = stackalloc float[FrameSize];
        _deepFilter!.Process(enhanced, frame);
        // The VAD sees the input about 30 ms before DeepFilterNet's delayed output: the gate opens early (pre-roll)
        // and the 250 ms hangover covers the early close.
        for (int i = 0; i < FrameSize; i++)
        {
            vadInput[i] = frame[i] * RnNoiseScale;
        }
        _gate.Apply(enhanced, (_vad ??= new Denoiser()).Process(unused, vadInput));
        new ReadOnlySpan<float>(enhanced, FrameSize).CopyTo(new Span<float>(frame, FrameSize));
    }

    private bool EnsureDeepFilter()
    {
        if (_deepFilter is not null)
        {
            return true;
        }
        if (_deepFilterFailed)
        {
            return false;
        }
        try
        {
            long start = Environment.TickCount64;
            _deepFilter = new DeepFilter();
            Log.Info("mic", $"DeepFilterNet 3 created in {Environment.TickCount64 - start} ms");
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            Log.Error("mic", "DeepFilterNet init failed, Voice Isolation uses RNNoise", e);
            _deepFilterFailed = true;
            return false;
        }
    }
}
