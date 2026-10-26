namespace WPods.Audio;

/// <summary>Runs stages in order on each frame; disposes them all.</summary>
internal sealed class MicChain(params IMicStage[] stages) : IMicStage
{
    public void Process(Span<float> frame)
    {
        foreach (IMicStage stage in stages)
        {
            stage.Process(frame);
        }
    }

    public void Dispose()
    {
        foreach (IMicStage stage in stages)
        {
            stage.Dispose();
        }
    }
}
