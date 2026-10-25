namespace WPods.Audio;

/// <summary>Runs stages in order on each frame; disposes them all.</summary>
internal sealed class MicChain : IMicStage
{
    private readonly IMicStage[] _stages;

    public MicChain(params IMicStage[] stages)
    {
        _stages = stages;
    }

    public void Process(Span<float> frame)
    {
        foreach (IMicStage stage in _stages)
        {
            stage.Process(frame);
        }
    }

    public void Dispose()
    {
        foreach (IMicStage stage in _stages)
        {
            stage.Dispose();
        }
    }
}
