using WPods.Audio.Dsp;

namespace WPods.Audio.Onnx;

/// <summary>
/// 16 kHz spectrum-to-spectrum streaming ONNX models: GTCRN by Xiaobin Rong (MIT) and the GTCRN-vd spec2spec export
/// of the FastEnhancer onnx-vd release (SNU, MIT), both with a sqrt-Hann window. Per hop the model takes the 257-bin
/// spectrum of a 512-sample frame plus its caches and returns the enhanced spectrum. Runs on the low band through
/// <see cref="BandSplit16k"/>; the hop of 256 goes through a <see cref="FrameFifo"/>. Delay at 16 kHz:
/// (512 - 256) + FIFO (256 - gcd(160, 256)) = 256 + 224 = 480 samples (30 ms), plus the band split.
/// No allocation per frame; one caller at a time.
/// </summary>
internal sealed class Spectrum16kStage : IMicStage
{
    public const string GtcrnFile = "gtcrn_simple.onnx";

    /// <summary>Model file of a FastEnhancer onnx-vd spec2spec export, e.g. "gtcrn".</summary>
    public static string VdSpecFile(string name) => $"fevd_{name}_spec.onnx";

    private const int FftSize = 512;
    private const int Hop = 256;

    private readonly OnnxModel _model;
    private readonly Stft _stft;
    private readonly FrameFifo _fifo;
    private readonly BandSplit16k _split;
    private readonly float[] _low = new float[BandSplit16k.Block];
    private readonly float[] _block;

    /// <param name="fileName"><see cref="GtcrnFile"/> or a <see cref="VdSpecFile"/> name.</param>
    /// <param name="window">The STFT window the model was trained with.</param>
    /// <param name="highBand">8-24 kHz handling.</param>
    public Spectrum16kStage(string fileName, StftWindow window, HighBand highBand = HighBand.GainTransfer)
    {
        _stft = new Stft(FftSize, Hop, window);
        _fifo = new FrameFifo(BandSplit16k.Block, Hop);
        _block = new float[Hop];
        _model = new OnnxModel(fileName);
        if (_model.Input.Length != 2 * _stft.Bins || _model.Output.Length != 2 * _stft.Bins)
        {
            _model.Dispose();
            throw new InvalidOperationException($"{fileName}: spectrum tensors are not {_stft.Bins} bins.");
        }
        _split = new BandSplit16k(_stft.LatencySamples + _fifo.LatencySamples, highBand);
    }

    public void Process(Span<float> frame)
    {
        _split.Split(frame, _low);
        _fifo.PushInput(_low);
        while (_fifo.PopBlock(_block))
        {
            _stft.Analyze(_block, _model.Input);
            _model.Run();
            _stft.Synthesize(_model.Output, _block);
            _fifo.PushBlock(_block);
        }
        _fifo.PopOutput(_low);
        _split.Merge(_low, frame);
    }

    public void Dispose() => _model.Dispose();
}
