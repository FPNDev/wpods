namespace WPods.Audio;

/// <summary>
/// One processing step of a mic mode model or a voice enhancement: 480-sample 48 kHz mono float
/// frames (-1..1 full scale), processed in place, one caller at a time (the session read loop), no allocation per frame.
/// Created when selected; the constructor throws when it cannot load (missing DLL or model) and the caller falls back.
/// </summary>
internal interface IMicStage : IDisposable
{
    void Process(Span<float> frame);

    void IDisposable.Dispose()
    {
    }
}
