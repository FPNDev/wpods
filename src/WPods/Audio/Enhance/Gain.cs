namespace WPods.Audio.Enhance;

/// <summary>Fixed make-up gain: brings a variant's typical speech back to the loudness of enhancement Off.</summary>
internal sealed class Gain : IMicStage
{
    private readonly float _gain;

    public Gain(float db)
    {
        _gain = Level.DbToGain(db);
    }

    public void Process(Span<float> frame)
    {
        for (int i = 0; i < frame.Length; i++)
        {
            frame[i] *= _gain;
        }
    }
}
