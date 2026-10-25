using WPods.Audio.Dsp;

namespace WPods.Audio.Onnx;

/// <summary>
/// Microsoft NSNet2 (DNS Challenge baseline; weights CC-BY-4.0, code MIT), GRU gain mask on log-power spectra,
/// streamed frame by frame after native\export\export_nsnet2.py exposed its GRU states. 48 kHz model: 960-sample
/// sqrt-Hann window zero-padded to 1024, hop 512 through a <see cref="FrameFifo"/> (delay 448 + 480 = 928 samples).
/// Gains are floored at -80 dB as upstream. No allocation per frame; one caller at a time.
/// </summary>
internal sealed class NsNet2Stage : IMicStage
{
    public const string File48k = "nsnet2_48k.onnx";

    private const int FrameSize48k = 480;
    private const float MinGain = 1e-4f;
    private const float MinPower = 1e-12f;

    private readonly OnnxModel _model;
    private readonly Stft _stft;
    private readonly FrameFifo _fifo;
    private readonly float[] _block;
    private readonly float[] _spectrum;

    /// <param name="fileName"><see cref="File48k"/>.</param>
    public NsNet2Stage(string fileName)
    {
        _model = new OnnxModel(fileName);
        _stft = new Stft(1024, 512, StftWindow.SqrtHann, 960);
        if (_model.Input.Length != _stft.Bins || _model.Output.Length != _stft.Bins)
        {
            _model.Dispose();
            throw new InvalidOperationException($"{fileName}: gain tensors are not {_stft.Bins} bins.");
        }
        _block = new float[_stft.Hop];
        _spectrum = new float[2 * _stft.Bins];
        _fifo = new FrameFifo(FrameSize48k, _stft.Hop);
    }

    public void Process(Span<float> frame)
    {
        _fifo.PushInput(frame);
        float[] feature = _model.Input;
        while (_fifo.PopBlock(_block))
        {
            _stft.Analyze(_block, _spectrum);
            for (int k = 0; k < feature.Length; k++)
            {
                float re = _spectrum[2 * k];
                float im = _spectrum[2 * k + 1];
                feature[k] = MathF.Log10(MathF.Max(re * re + im * im, MinPower));
            }
            _model.Run();
            float[] gain = _model.Output;
            for (int k = 0; k < gain.Length; k++)
            {
                float g = MathF.Max(gain[k], MinGain);
                _spectrum[2 * k] *= g;
                _spectrum[2 * k + 1] *= g;
            }
            _stft.Synthesize(_spectrum, _block);
            _fifo.PushBlock(_block);
        }
        _fifo.PopOutput(frame);
    }

    public void Dispose() => _model.Dispose();
}
