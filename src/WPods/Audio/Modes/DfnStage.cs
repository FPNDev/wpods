namespace WPods.Audio.Modes;

/// <summary>
/// DeepFilterNet 3 stage, no speech gate: one <see cref="DeepFilter"/> state with the standard model, no attenuation
/// limit, optional post filter and the libDF stage thresholds. One caller at a time, no allocation per frame.
/// </summary>
internal sealed unsafe class DfnStage : IMicStage
{
    /// <summary>Measured input-to-output delay of the standard model (10 ms STFT + 2 frames lookahead).</summary>
    public const int StandardLatencySamples = 1440;

    private readonly DeepFilter _deepFilter;

    /// <param name="postFilterBeta">libDF post filter strength (0.02 = libDF default when on, 0.05 = strongest
    /// suggested); 0 = off.</param>
    public DfnStage(float postFilterBeta = 0f)
    {
        _deepFilter = new DeepFilter();
        try
        {
            if (postFilterBeta > 0f)
            {
                _deepFilter.SetPostFilterBeta(postFilterBeta);
            }
        }
        catch
        {
            _deepFilter.Dispose();
            throw;
        }
    }

    public void Process(Span<float> frame)
    {
        float* output = stackalloc float[Denoiser.FrameSize];
        fixed (float* input = frame)
        {
            _ = _deepFilter.Process(output, input);
        }
        new ReadOnlySpan<float>(output, Denoiser.FrameSize).CopyTo(frame);
    }

    public void Dispose() => _deepFilter.Dispose();
}
