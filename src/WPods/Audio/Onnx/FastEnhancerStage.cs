using WPods.Audio.Dsp;

namespace WPods.Audio.Onnx;

/// <summary>
/// FastEnhancer (SNU, MIT) waveform-to-waveform streaming ONNX export: per call the model takes one hop of new
/// samples (its STFT, network and overlap-add run inside the graph, with the input and output overlap as caches) and
/// returns one hop, delayed by n_fft - hop (the length of its first cache). Older exports (onnx-vd release) take the
/// whole n_fft-sample frame instead of one hop; the stage then keeps that frame as a sliding window. 48 kHz models run
/// on the stream directly; hops other than one 10 ms frame go through a <see cref="FrameFifo"/>. No allocation per
/// frame; one caller at a time.
/// </summary>
internal sealed class FastEnhancerStage : IMicStage
{
    public const string T48kFile = "fastenhancer48k_t.onnx";
    public const string B48kFile = "fastenhancer48k_b.onnx";
    public const string S48kFile = "fastenhancer48k_s.onnx";
    public const string M48kFile = "fastenhancer48k_m.onnx";
    public const string L48kFile = "fastenhancer48k_l.onnx";

    private readonly OnnxModel _model;
    private readonly FrameFifo _fifo;
    private readonly float[] _block;

    /// <param name="fileName">Model file (one of the *File constants).</param>
    public FastEnhancerStage(string fileName)
    {
        _model = new OnnxModel(fileName);
        _block = new float[_model.Output.Length];
        _fifo = new FrameFifo(Denoiser.FrameSize, _block.Length);
    }

    public void Process(Span<float> frame)
    {
        _fifo.PushInput(frame);
        float[] input = _model.Input;
        int hop = _block.Length;
        while (_fifo.PopBlock(_block))
        {
            input.AsSpan(hop).CopyTo(input);
            _block.CopyTo(input.AsSpan(input.Length - hop));
            _model.Run();
            _fifo.PushBlock(_model.Output);
        }
        _fifo.PopOutput(frame);
    }

    public void Dispose() => _model.Dispose();
}
