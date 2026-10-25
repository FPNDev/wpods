namespace WPods.Audio.Dsp;

/// <summary>
/// Re-blocks a stream of fixed-size frames (e.g. 480 samples per 10 ms) into blocks of another size (a model hop)
/// and back. Per frame: <see cref="PushInput"/> once, then <see cref="PopBlock"/> / <see cref="PushBlock"/> while a
/// block is available, then <see cref="PopOutput"/> once. The output starts with <see cref="LatencySamples"/> zeros
/// (hop - gcd(frameSize, hop), the least that keeps a full frame always ready), so the round trip adds exactly that
/// delay. No allocation per call; one caller at a time.
/// </summary>
internal sealed class FrameFifo
{
    private readonly float[] _input;
    private readonly float[] _output;
    private int _inputCount;
    private int _outputCount;

    /// <param name="frameSize">Samples per <see cref="PushInput"/> / <see cref="PopOutput"/> call.</param>
    /// <param name="hop">Samples per <see cref="PopBlock"/> / <see cref="PushBlock"/> call.</param>
    public FrameFifo(int frameSize, int hop)
    {
        if (frameSize < 1 || hop < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(hop), hop, "Frame size and hop must be positive.");
        }
        FrameSize = frameSize;
        Hop = hop;
        LatencySamples = hop - Gcd(frameSize, hop);
        _input = new float[frameSize + hop];
        _output = new float[LatencySamples + frameSize + hop];
        _outputCount = LatencySamples;
    }

    public int FrameSize { get; }

    public int Hop { get; }

    /// <summary>Delay added by the re-blocking, in samples.</summary>
    public int LatencySamples { get; }

    /// <summary>Appends one input frame (<see cref="FrameSize"/> samples).</summary>
    public void PushInput(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSize)
        {
            throw new ArgumentException("PushInput takes FrameSize samples.", nameof(frame));
        }
        frame.CopyTo(_input.AsSpan(_inputCount));
        _inputCount += FrameSize;
    }

    /// <summary>Moves the next <see cref="Hop"/> input samples into block; false when fewer are buffered.</summary>
    public bool PopBlock(Span<float> block)
    {
        if (block.Length != Hop)
        {
            throw new ArgumentException("PopBlock writes Hop samples.", nameof(block));
        }
        if (_inputCount < Hop)
        {
            return false;
        }
        _input.AsSpan(0, Hop).CopyTo(block);
        _inputCount -= Hop;
        _input.AsSpan(Hop, _inputCount).CopyTo(_input);
        return true;
    }

    /// <summary>Appends one processed block (<see cref="Hop"/> samples).</summary>
    public void PushBlock(ReadOnlySpan<float> block)
    {
        if (block.Length != Hop)
        {
            throw new ArgumentException("PushBlock takes Hop samples.", nameof(block));
        }
        block.CopyTo(_output.AsSpan(_outputCount));
        _outputCount += Hop;
    }

    /// <summary>Moves the next <see cref="FrameSize"/> processed samples into frame.</summary>
    public void PopOutput(Span<float> frame)
    {
        if (frame.Length != FrameSize)
        {
            throw new ArgumentException("PopOutput writes FrameSize samples.", nameof(frame));
        }
        if (_outputCount < FrameSize)
        {
            throw new InvalidOperationException("PopOutput needs every available block processed first.");
        }
        _output.AsSpan(0, FrameSize).CopyTo(frame);
        _outputCount -= FrameSize;
        _output.AsSpan(FrameSize, _outputCount).CopyTo(_output);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return a;
    }
}
