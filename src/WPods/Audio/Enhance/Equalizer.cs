using WPods.Audio.Dsp;

namespace WPods.Audio.Enhance;

/// <summary>Static EQ: a cascade of biquads (high-pass, shelves, peaks) applied in order. IIR, no latency.</summary>
internal sealed class Equalizer : IMicStage
{
    private readonly Biquad[] _filters;

    public Equalizer(params Biquad[] filters)
    {
        _filters = filters;
    }

    public void Process(Span<float> frame)
    {
        foreach (Biquad filter in _filters)
        {
            filter.Process(frame);
        }
    }

    public void Dispose()
    {
    }
}
