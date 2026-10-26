using WPods.Audio.Dsp;

namespace WPods.Audio.Enhance;

/// <summary>Static EQ: a cascade of biquads (high-pass, shelves, peaks) applied in order. IIR, no latency.</summary>
internal sealed class Equalizer(params Biquad[] filters) : IMicStage
{
    public void Process(Span<float> frame)
    {
        foreach (Biquad filter in filters)
        {
            filter.Process(frame);
        }
    }
}
