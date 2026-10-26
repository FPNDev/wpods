using System.Globalization;
using WPods.Audio.Dsp;

namespace WPods.Audio.Onnx;

/// <summary>
/// DPDFNet (Ceva, Apache-2.0; DeepFilterNet2 + dual-path RNN blocks), streaming ONNX export: per hop the model takes
/// the spectrum of a two-hop Vorbis-windowed frame plus one flat state vector (whose ERB and spectral normalization
/// parts start from the model's metadata) and returns the enhanced spectrum. 48 kHz models only (sample_rate
/// metadata checked; 960 / 480), run on the stream directly. Delay: one hop (STFT) plus the model's own 4-hop delay
/// (confirmed by cross-correlation). No allocation per frame; one caller at a time.
/// </summary>
internal sealed class DpdfNetStage : IMicStage
{
    public const string Dpdf2File = "dpdfnet2_48khz_hr.onnx";
    public const string Dpdf8File = "dpdfnet8_48khz_hr.onnx";

    private readonly OnnxModel _model;
    private readonly Stft _stft;

    /// <param name="fileName">One of the *File constants.</param>
    /// <param name="intraOpThreads">ONNX Runtime threads per operator.</param>
    public DpdfNetStage(string fileName, int intraOpThreads = 1)
    {
        _model = new OnnxModel(fileName, intraOpThreads);
        try
        {
            int rate = int.Parse(_model.Metadata("sample_rate") ?? "48000", CultureInfo.InvariantCulture);
            if (rate != 48000)
            {
                throw new InvalidOperationException($"{fileName}: unsupported sample rate {rate}.");
            }
            _stft = new Stft(2 * Denoiser.FrameSize, Denoiser.FrameSize, StftWindow.Vorbis);
            if (_model.Input.Length != 2 * _stft.Bins)
            {
                throw new InvalidOperationException($"{fileName}: spectrum input has {_model.Input.Length} floats, expected {2 * _stft.Bins}.");
            }
            float[] state = _model.State(1);
            int offset = Fill(state, 0, _model.Metadata("erb_norm_init"));
            Fill(state, offset, _model.Metadata("spec_norm_init"));
        }
        catch
        {
            _model.Dispose();
            throw;
        }
    }

    public void Process(Span<float> frame)
    {
        _stft.Analyze(frame, _model.Input);
        _model.Run();
        _stft.Synthesize(_model.Output, frame);
    }

    public void Dispose() => _model.Dispose();

    /// <summary>Writes the comma-separated floats of a metadata value at offset; returns the offset after them.</summary>
    private static int Fill(float[] state, int offset, string? values)
    {
        if (values is null)
        {
            throw new InvalidOperationException("DPDFNet model lacks its state initialization metadata.");
        }
        foreach (Range part in values.AsSpan().Split(','))
        {
            state[offset++] = float.Parse(values.AsSpan()[part], CultureInfo.InvariantCulture);
        }
        return offset;
    }
}
