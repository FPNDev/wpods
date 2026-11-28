using System.Runtime.InteropServices;

namespace WPods.Audio;

/// <summary>
/// FFmpeg audio decoder ("aac" for AAC-ELD, "sbc" for mSBC), or raw s16 PCM, plus the resampler to mono 48 kHz float
/// (-1..1 full scale, not clipped). Not thread-safe: one caller (the mic thread). Constructors throw when FFmpeg is
/// missing or rejects the config.
/// </summary>
internal sealed unsafe partial class FfmpegDecoder : IDisposable
{
    internal const string AvCodec = "avcodec-61";
    internal const string AvUtil = "avutil-59";
    internal const string SwResample = "swresample-5";

    private const int MaxUnitLength = 1024;
    private const int PaddingSize = 64; // AV_INPUT_BUFFER_PADDING_SIZE
    internal const int ErrorAgain = -11; // AVERROR(EAGAIN)
    internal const int LogLevelError = 16;
    internal const int SampleFormatS16 = 1; // AV_SAMPLE_FMT_S16
    internal const int SampleFormatFloat = 3; // AV_SAMPLE_FMT_FLT (packed; mono, so one plane)
    internal const int OutputRate = 48_000;

    // FFmpeg 7.1 field offsets, the same on x64 and arm64 (avcodec-61 / avutil-59 ABI, checked with offsetof against
    // the build headers for both).
    private const int ContextExtradata = 72;
    private const int ContextExtradataSize = 80;
    internal const int ContextTimeBase = 84;
    internal const int ContextSampleRate = 344;
    internal const int ContextSampleFormat = 348;
    internal const int ContextChannelLayout = 352;
    internal const int ContextFrameSize = 376;
    internal const int PacketData = 24;
    internal const int PacketSize = 32;
    private const int FrameExtendedData = 96;
    internal const int FrameSamples = 112;
    internal const int FrameFormat = 116;
    internal const int FrameChannelLayout = 408;
    internal const int ChannelLayoutSize = 24;

    // ER AAC-ELD (AOT 39), 48 kHz index, mono, 480-sample frames.
    private static ReadOnlySpan<byte> StereoMicConfig => [0xF8, 0xE6, 0x30, 0x00];

    private readonly byte* _unit;
    // Rate the decoded frames are resampled from.
    private readonly int _streamRate;
    private byte* _context;
    private byte* _packet;
    private byte* _frame;
    private void* _resampler;

    /// <param name="config">Codec extradata (the AAC AudioSpecificConfig), empty for none.</param>
    /// <param name="contextRate">Sample rate set on the codec context before open, 0 for none.</param>
    /// <param name="streamRate">Rate of the decoded frames, which may differ from what the config declares.</param>
    public FfmpegDecoder(string codec, ReadOnlySpan<byte> config, int contextRate, int streamRate)
    {
        _streamRate = streamRate;
        _unit = (byte*)NativeMemory.AllocZeroed(MaxUnitLength + PaddingSize);
        try
        {
            av_log_set_level(LogLevelError);
            byte* name = stackalloc byte[codec.Length + 1];
            System.Text.Encoding.ASCII.GetBytes(codec, new Span<byte>(name, codec.Length));
            name[codec.Length] = 0;
            void* decoder = avcodec_find_decoder_by_name(name);
            if (decoder is null || (_context = avcodec_alloc_context3(decoder)) is null)
            {
                throw new InvalidOperationException($"The FFmpeg {codec} decoder is not available.");
            }
            if (config.Length > 0)
            {
                // Freed by avcodec_free_context.
                byte* extradata = (byte*)av_mallocz((nuint)(config.Length + PaddingSize));
                if (extradata is null)
                {
                    throw new InvalidOperationException("FFmpeg could not allocate the decoder config.");
                }
                config.CopyTo(new Span<byte>(extradata, config.Length));
                *(byte**)(_context + ContextExtradata) = extradata;
                *(int*)(_context + ContextExtradataSize) = config.Length;
            }
            // FFmpeg 7.1 needs the rate set before open for ELD, despite the ASC.
            if (contextRate > 0)
            {
                *(int*)(_context + ContextSampleRate) = contextRate;
            }
            if (avcodec_open2(_context, decoder, null) < 0
                || (_packet = av_packet_alloc()) is null
                || (_frame = av_frame_alloc()) is null)
            {
                throw new InvalidOperationException($"FFmpeg could not open the {codec} decoder.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Raw mono s16le PCM at <paramref name="pcmRate"/>: only resampled.</summary>
    public FfmpegDecoder(int pcmRate)
    {
        _streamRate = pcmRate;
        _unit = (byte*)NativeMemory.AllocZeroed(MaxUnitLength + PaddingSize);
        byte* mono = stackalloc byte[ChannelLayoutSize];
        av_channel_layout_default(mono, 1);
        if (!CreateResampler(mono, SampleFormatS16))
        {
            Dispose();
            throw new InvalidOperationException("FFmpeg could not create the PCM resampler.");
        }
    }

    /// <summary>The AirPods' 0x58 stereo mic stream: the ASC declares 48 kHz, but 480-sample frames come at 64 kHz.</summary>
    public static FfmpegDecoder StereoMic() => new("aac", StereoMicConfig, OutputRate, 64_000);

    /// <summary>
    /// Decodes one unit (1..1024 bytes; raw PCM: even length) and appends the resampled samples to
    /// <paramref name="output"/>. Returns the sample count, or -1 when FFmpeg rejected the unit (the decoder stays usable).
    /// </summary>
    public int Decode(ReadOnlySpan<byte> unit, Span<float> output)
    {
        if (unit.Length > MaxUnitLength)
        {
            return -1;
        }
        unit.CopyTo(new Span<byte>(_unit, MaxUnitLength));
        new Span<byte>(_unit + unit.Length, PaddingSize).Clear();
        fixed (float* start = output)
        {
            if (_context is null)
            {
                byte* destination = (byte*)start;
                byte* source = _unit;
                return swr_convert(_resampler, &destination, output.Length, &source, unit.Length / sizeof(short));
            }
            *(byte**)(_packet + PacketData) = _unit;
            *(int*)(_packet + PacketSize) = unit.Length;
            if (avcodec_send_packet(_context, _packet) < 0)
            {
                return -1;
            }
            int total = 0;
            while (true)
            {
                // receive_frame unrefs the previous frame itself.
                int result = avcodec_receive_frame(_context, _frame);
                if (result == ErrorAgain)
                {
                    return total;
                }
                if (result < 0 || (_resampler is null && !CreateResampler(_frame + FrameChannelLayout, *(int*)(_frame + FrameFormat))))
                {
                    return -1;
                }
                byte* destination = (byte*)(start + total);
                int converted = swr_convert(_resampler, &destination, output.Length - total, *(byte***)(_frame + FrameExtendedData), *(int*)(_frame + FrameSamples));
                if (converted < 0)
                {
                    return -1;
                }
                total += converted;
            }
        }
    }

    /// <summary>Drops decoder state (call after a gap in the stream).</summary>
    public void Flush()
    {
        if (_context is not null)
        {
            avcodec_flush_buffers(_context);
        }
    }

    public void Dispose()
    {
        void* resampler = _resampler;
        swr_free(&resampler);
        _resampler = null;
        fixed (byte** frame = &_frame)
        {
            av_frame_free(frame);
        }
        fixed (byte** packet = &_packet)
        {
            av_packet_free(packet);
        }
        fixed (byte** context = &_context)
        {
            avcodec_free_context(context);
        }
        NativeMemory.Free(_unit);
    }

    // Configured once, from the first decoded frame's layout and format (or the raw PCM input).
    private bool CreateResampler(byte* inLayout, int inFormat)
    {
        byte* mono = stackalloc byte[ChannelLayoutSize];
        // Default native-order layout: nothing to uninit.
        av_channel_layout_default(mono, 1);
        void* resampler = null;
        if (swr_alloc_set_opts2(&resampler, mono, SampleFormatFloat, OutputRate, inLayout, inFormat, _streamRate, 0, null) < 0)
        {
            return false;
        }
        if (swr_init(resampler) < 0)
        {
            swr_free(&resampler);
            return false;
        }
        _resampler = resampler;
        return true;
    }

    [LibraryImport(AvCodec)]
    private static partial void* avcodec_find_decoder_by_name(byte* name);

    [LibraryImport(AvCodec)]
    internal static partial byte* avcodec_alloc_context3(void* codec);

    [LibraryImport(AvCodec)]
    internal static partial int avcodec_open2(byte* context, void* codec, void** options);

    [LibraryImport(AvCodec)]
    internal static partial void avcodec_free_context(byte** context);

    [LibraryImport(AvCodec)]
    private static partial void avcodec_flush_buffers(byte* context);

    [LibraryImport(AvCodec)]
    private static partial int avcodec_send_packet(byte* context, byte* packet);

    [LibraryImport(AvCodec)]
    private static partial int avcodec_receive_frame(byte* context, byte* frame);

    [LibraryImport(AvCodec)]
    internal static partial byte* av_packet_alloc();

    [LibraryImport(AvCodec)]
    internal static partial void av_packet_free(byte** packet);

    [LibraryImport(AvUtil)]
    private static partial void* av_mallocz(nuint size);

    [LibraryImport(AvUtil)]
    internal static partial void av_log_set_level(int level);

    [LibraryImport(AvUtil)]
    internal static partial byte* av_frame_alloc();

    [LibraryImport(AvUtil)]
    internal static partial void av_frame_free(byte** frame);

    [LibraryImport(AvUtil)]
    internal static partial void av_channel_layout_default(byte* layout, int channels);

    [LibraryImport(SwResample)]
    internal static partial int swr_alloc_set_opts2(void** resampler, byte* outLayout, int outFormat, int outRate, byte* inLayout, int inFormat, int inRate, int logOffset, void* logContext);

    [LibraryImport(SwResample)]
    internal static partial int swr_init(void* resampler);

    [LibraryImport(SwResample)]
    internal static partial int swr_convert(void* resampler, byte** output, int outputCount, byte** input, int inputCount);

    [LibraryImport(SwResample)]
    internal static partial void swr_free(void** resampler);
}

/// <summary>
/// Mono 48 kHz float (-1..1) resampled to an FFmpeg audio encoder's rate and encoded ("sbc" with msbc=1 for mSBC), or
/// only resampled to raw s16le PCM. Not thread-safe: one caller. Constructors throw when FFmpeg is missing or rejects
/// the config.
/// </summary>
internal sealed unsafe partial class FfmpegEncoder : IDisposable
{
    // Resampled samples not yet encoded or taken: a few frames.
    private const int QueueSamples = 4096;

    // Samples per encoder frame; 0 for raw PCM.
    private readonly int _frameSamples;
    private short* _queue;
    private int _queued;
    private byte* _context;
    private byte* _packet;
    private byte* _frame;
    private void* _resampler;

    /// <param name="options">Encoder options for avcodec_open2.</param>
    /// <param name="frameSamples">Samples per frame the opened encoder must take (checked).</param>
    public FfmpegEncoder(string codec, IEnumerable<KeyValuePair<string, string>> options, int rate, int frameSamples)
    {
        _frameSamples = frameSamples;
        _queue = (short*)NativeMemory.AllocZeroed(QueueSamples * sizeof(short));
        void* dictionary = null;
        try
        {
            FfmpegDecoder.av_log_set_level(FfmpegDecoder.LogLevelError);
            void* encoder;
            fixed (byte* name = Ascii(codec))
            {
                encoder = avcodec_find_encoder_by_name(name);
            }
            if (encoder is null || (_context = FfmpegDecoder.avcodec_alloc_context3(encoder)) is null)
            {
                throw new InvalidOperationException($"The FFmpeg {codec} encoder is not available.");
            }
            *(int*)(_context + FfmpegDecoder.ContextTimeBase) = 1;
            *(int*)(_context + FfmpegDecoder.ContextTimeBase + sizeof(int)) = rate;
            *(int*)(_context + FfmpegDecoder.ContextSampleRate) = rate;
            *(int*)(_context + FfmpegDecoder.ContextSampleFormat) = FfmpegDecoder.SampleFormatS16;
            // The context's layout starts unspecified: nothing to uninit.
            FfmpegDecoder.av_channel_layout_default(_context + FfmpegDecoder.ContextChannelLayout, 1);
            foreach ((string key, string value) in options)
            {
                fixed (byte* k = Ascii(key))
                fixed (byte* v = Ascii(value))
                {
                    if (av_dict_set(&dictionary, k, v, 0) < 0)
                    {
                        throw new InvalidOperationException("FFmpeg could not allocate the encoder options.");
                    }
                }
            }
            if (FfmpegDecoder.avcodec_open2(_context, encoder, &dictionary) < 0
                || *(int*)(_context + FfmpegDecoder.ContextFrameSize) != frameSamples
                || (_packet = FfmpegDecoder.av_packet_alloc()) is null
                || (_frame = FfmpegDecoder.av_frame_alloc()) is null)
            {
                throw new InvalidOperationException($"FFmpeg could not open the {codec} encoder.");
            }
            *(int*)(_frame + FfmpegDecoder.FrameSamples) = frameSamples;
            *(int*)(_frame + FfmpegDecoder.FrameFormat) = FfmpegDecoder.SampleFormatS16;
            FfmpegDecoder.av_channel_layout_default(_frame + FfmpegDecoder.FrameChannelLayout, 1);
            if (av_frame_get_buffer(_frame, 0) < 0 || !CreateResampler(rate))
            {
                throw new InvalidOperationException($"FFmpeg could not set up the {codec} encoder's buffers.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            av_dict_free(&dictionary);
        }
    }

    /// <summary>Raw mono s16le PCM at <paramref name="pcmRate"/>: only resampled.</summary>
    public FfmpegEncoder(int pcmRate)
    {
        _queue = (short*)NativeMemory.AllocZeroed(QueueSamples * sizeof(short));
        if (!CreateResampler(pcmRate))
        {
            Dispose();
            throw new InvalidOperationException("FFmpeg could not create the PCM resampler.");
        }
    }

    /// <summary>Resamples and queues mono 48 kHz samples for <see cref="Next"/>; false when the resampler failed.</summary>
    public bool Push(ReadOnlySpan<float> pcm)
    {
        fixed (float* input = pcm)
        {
            byte* destination = (byte*)(_queue + _queued);
            byte* source = (byte*)input;
            int converted = FfmpegDecoder.swr_convert(_resampler, &destination, QueueSamples - _queued, &source, pcm.Length);
            if (converted < 0)
            {
                return false;
            }
            _queued += converted;
            return true;
        }
    }

    /// <summary>
    /// One encoded packet into <paramref name="output"/> (raw PCM: the queued samples as s16le, as many as fit). Returns its
    /// length, 0 when no whole frame is queued, or -1 on an encoder error.
    /// </summary>
    public int Next(Span<byte> output)
    {
        if (_context is null)
        {
            int samples = Math.Min(_queued, output.Length / sizeof(short));
            new ReadOnlySpan<byte>(_queue, samples * sizeof(short)).CopyTo(output);
            Dequeue(samples);
            return samples * sizeof(short);
        }
        while (true)
        {
            int result = avcodec_receive_packet(_context, _packet);
            if (result == 0)
            {
                int size = *(int*)(_packet + FfmpegDecoder.PacketSize);
                bool fits = size <= output.Length;
                if (fits)
                {
                    new ReadOnlySpan<byte>(*(byte**)(_packet + FfmpegDecoder.PacketData), size).CopyTo(output);
                }
                av_packet_unref(_packet);
                return fits ? size : -1;
            }
            if (result != FfmpegDecoder.ErrorAgain)
            {
                return -1;
            }
            if (_queued < _frameSamples)
            {
                return 0;
            }
            // data[0] is the frame's first field.
            if (av_frame_make_writable(_frame) < 0)
            {
                return -1;
            }
            new ReadOnlySpan<short>(_queue, _frameSamples).CopyTo(new Span<short>(*(short**)_frame, _frameSamples));
            Dequeue(_frameSamples);
            if (avcodec_send_frame(_context, _frame) < 0)
            {
                return -1;
            }
        }
    }

    public void Dispose()
    {
        void* resampler = _resampler;
        FfmpegDecoder.swr_free(&resampler);
        _resampler = null;
        fixed (byte** frame = &_frame)
        {
            FfmpegDecoder.av_frame_free(frame);
        }
        fixed (byte** packet = &_packet)
        {
            FfmpegDecoder.av_packet_free(packet);
        }
        fixed (byte** context = &_context)
        {
            FfmpegDecoder.avcodec_free_context(context);
        }
        NativeMemory.Free(_queue);
        _queue = null;
    }

    private void Dequeue(int samples)
    {
        _queued -= samples;
        new ReadOnlySpan<short>(_queue + samples, _queued).CopyTo(new Span<short>(_queue, _queued));
    }

    private bool CreateResampler(int rate)
    {
        byte* mono = stackalloc byte[FfmpegDecoder.ChannelLayoutSize];
        // Default native-order layout: nothing to uninit.
        FfmpegDecoder.av_channel_layout_default(mono, 1);
        void* resampler = null;
        if (FfmpegDecoder.swr_alloc_set_opts2(&resampler, mono, FfmpegDecoder.SampleFormatS16, rate, mono, FfmpegDecoder.SampleFormatFloat, FfmpegDecoder.OutputRate, 0, null) < 0)
        {
            return false;
        }
        if (FfmpegDecoder.swr_init(resampler) < 0)
        {
            FfmpegDecoder.swr_free(&resampler);
            return false;
        }
        _resampler = resampler;
        return true;
    }

    private static byte[] Ascii(string text) => System.Text.Encoding.ASCII.GetBytes(text + "\0");

    [LibraryImport(FfmpegDecoder.AvCodec)]
    private static partial void* avcodec_find_encoder_by_name(byte* name);

    [LibraryImport(FfmpegDecoder.AvCodec)]
    private static partial int avcodec_send_frame(byte* context, byte* frame);

    [LibraryImport(FfmpegDecoder.AvCodec)]
    private static partial int avcodec_receive_packet(byte* context, byte* packet);

    [LibraryImport(FfmpegDecoder.AvCodec)]
    private static partial void av_packet_unref(byte* packet);

    [LibraryImport(FfmpegDecoder.AvUtil)]
    private static partial int av_frame_get_buffer(byte* frame, int align);

    [LibraryImport(FfmpegDecoder.AvUtil)]
    private static partial int av_frame_make_writable(byte* frame);

    [LibraryImport(FfmpegDecoder.AvUtil)]
    private static partial int av_dict_set(void** dictionary, byte* key, byte* value, int flags);

    [LibraryImport(FfmpegDecoder.AvUtil)]
    private static partial void av_dict_free(void** dictionary);
}
