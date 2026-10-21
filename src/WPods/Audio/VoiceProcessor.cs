using WPods.Core;

namespace WPods.Audio;

/// <summary>
/// Mic processing for every mode except Wide Spectrum, one 480-sample 48 kHz frame at a time, in place:
/// Standard = RNNoise + 25 % of what it removed; Voice Isolation and VI 1 = RNNoise; VI 2 = RNNoise + speech gate;
/// VI 3 = DeepFilterNet 3; VI 4 = DeepFilterNet 3 + speech gate driven by a parallel RNNoise VAD on the same input.
/// DeepFilterNet is created on the first VI 3/4 frame; if it cannot load, those modes fall back to RNNoise (+ gate).
/// One caller at a time (the session read loop); no allocation per frame.
/// </summary>
internal sealed unsafe class VoiceProcessor : IDisposable
{
    private const int FrameSize = Denoiser.FrameSize;
    // Standard mode keeps this share of what RNNoise removed (~ -12 dB residual noise).
    private const float StandardResidual = 0.25f;
    private const float PcmScale = 32768f;

    private readonly Denoiser _rnnoise = new();
    private readonly SpeechGate _gate = new();
    private float[] _input = GC.AllocateArray<float>(FrameSize, pinned: true);
    private float[] _previousInput = GC.AllocateArray<float>(FrameSize, pinned: true);
    private Denoiser? _vad;
    private DeepFilter? _deepFilter;
    private bool _deepFilterFailed;

    public void Process(Span<short> frame, MicMode mode)
    {
        if (mode is MicMode.VoiceIso3 or MicMode.VoiceIso4 && EnsureDeepFilter())
        {
            DeepFilter(frame, mode == MicMode.VoiceIso4);
        }
        else
        {
            RnNoise(frame, mode);
        }
    }

    public void Dispose()
    {
        _rnnoise.Dispose();
        _vad?.Dispose();
        _deepFilter?.Dispose();
    }

    private void RnNoise(Span<short> frame, MicMode mode)
    {
        float* denoised = stackalloc float[FrameSize];
        fixed (float* input = _input)
        fixed (float* previous = _previousInput)
        {
            for (int i = 0; i < FrameSize; i++)
            {
                input[i] = frame[i];
            }
            float voice = _rnnoise.Process(denoised, input);
            if (mode == MicMode.Standard)
            {
                // RNNoise output lags one frame, so the dry signal mixed back is the previous input frame.
                for (int i = 0; i < FrameSize; i++)
                {
                    denoised[i] += StandardResidual * (previous[i] - denoised[i]);
                }
            }
            else if (mode is MicMode.VoiceIso2 or MicMode.VoiceIso4)
            {
                _gate.Apply(denoised, voice);
            }
            Store(frame, denoised, 1f);
        }
        (_input, _previousInput) = (_previousInput, _input);
    }

    private void DeepFilter(Span<short> frame, bool gate)
    {
        float* input = stackalloc float[FrameSize];
        float* enhanced = stackalloc float[FrameSize];
        for (int i = 0; i < FrameSize; i++)
        {
            input[i] = frame[i] / PcmScale;
        }
        _deepFilter!.Process(enhanced, input);
        if (gate)
        {
            // The VAD sees the input about 30 ms before DeepFilterNet's delayed output: the gate opens early
            // (pre-roll) and the 250 ms hangover covers the early close.
            float* vadInput = stackalloc float[FrameSize];
            float* unused = stackalloc float[FrameSize];
            for (int i = 0; i < FrameSize; i++)
            {
                vadInput[i] = frame[i];
            }
            _gate.Apply(enhanced, (_vad ??= new Denoiser()).Process(unused, vadInput));
        }
        Store(frame, enhanced, PcmScale);
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
            Log.Error("mic", "DeepFilterNet init failed, VI 3/4 use RNNoise", e);
            _deepFilterFailed = true;
            return false;
        }
    }

    private static void Store(Span<short> frame, float* samples, float scale)
    {
        for (int i = 0; i < FrameSize; i++)
        {
            frame[i] = (short)Math.Clamp(MathF.Round(samples[i] * scale), short.MinValue, short.MaxValue);
        }
    }
}
