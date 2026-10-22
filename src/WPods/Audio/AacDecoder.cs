using System.Runtime.InteropServices;

namespace WPods.Audio;

/// <summary>
/// AirPods mic AAC-ELD decoder (FFmpeg "aac", float planar output) plus the 64 -> 48 kHz resampler to mono float
/// (-1..1 full scale, not clipped). Not thread-safe: one caller (the session read loop). Constructor throws when FFmpeg is missing or rejects the stream config.
/// </summary>
internal sealed unsafe partial class AacDecoder : IDisposable
{
    private const string AvCodec = "avcodec-61";
    private const string AvUtil = "avutil-59";
    private const string SwResample = "swresample-5";

    private const int MaxAuLength = byte.MaxValue;
    private const int PaddingSize = 64; // AV_INPUT_BUFFER_PADDING_SIZE
    private const int ErrorAgain = -11; // AVERROR(EAGAIN)
    private const int LogLevelError = 16;
    private const int SampleFormatFloat = 3; // AV_SAMPLE_FMT_FLT (packed; mono, so one plane)
    private const int OutputRate = 48_000;
    // The ASC declares 48 kHz, but the AirPods send 480-sample frames at 64 kHz (measured 133.3 AU/s), so the
    // decoded frames are resampled as 64 kHz audio.
    private const int StreamRate = 64_000;

    // FFmpeg 7.1 x64 field offsets (avcodec-61 / avutil-59 ABI, checked with offsetof against the build headers).
    private const int ContextExtradata = 72;
    private const int ContextExtradataSize = 80;
    private const int ContextSampleRate = 344;
    private const int PacketData = 24;
    private const int PacketSize = 32;
    private const int FrameExtendedData = 96;
    private const int FrameSamples = 112;
    private const int FrameFormat = 116;
    private const int FrameChannelLayout = 408;
    private const int ChannelLayoutSize = 24;

    // ER AAC-ELD (AOT 39), 48 kHz index, mono, 480-sample frames.
    private static ReadOnlySpan<byte> AudioSpecificConfig => [0xF8, 0xE6, 0x30, 0x00];

    private readonly byte* _au;
    private byte* _context;
    private byte* _packet;
    private byte* _frame;
    private void* _resampler;

    public AacDecoder()
    {
        _au = (byte*)NativeMemory.AllocZeroed(MaxAuLength + PaddingSize);
        try
        {
            av_log_set_level(LogLevelError);
            void* codec;
            fixed (byte* name = "aac\0"u8)
            {
                codec = avcodec_find_decoder_by_name(name);
            }
            if (codec is null || (_context = avcodec_alloc_context3(codec)) is null)
            {
                throw new InvalidOperationException("The FFmpeg aac decoder is not available.");
            }
            // Freed by avcodec_free_context.
            byte* extradata = (byte*)av_mallocz((nuint)(AudioSpecificConfig.Length + PaddingSize));
            if (extradata is null)
            {
                throw new InvalidOperationException("FFmpeg could not allocate the decoder config.");
            }
            AudioSpecificConfig.CopyTo(new Span<byte>(extradata, AudioSpecificConfig.Length));
            *(byte**)(_context + ContextExtradata) = extradata;
            *(int*)(_context + ContextExtradataSize) = AudioSpecificConfig.Length;
            // FFmpeg 7.1 needs the rate set before open for ELD, despite the ASC.
            *(int*)(_context + ContextSampleRate) = OutputRate;
            if (avcodec_open2(_context, codec, null) < 0
                || (_packet = av_packet_alloc()) is null
                || (_frame = av_frame_alloc()) is null)
            {
                throw new InvalidOperationException("FFmpeg could not open the AAC-ELD decoder.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decodes one access unit (1..255 bytes) and appends the resampled samples to <paramref name="output"/>.
    /// Returns the sample count, or -1 when FFmpeg rejected the unit (the decoder stays usable).
    /// </summary>
    public int Decode(ReadOnlySpan<byte> au, Span<float> output)
    {
        au.CopyTo(new Span<byte>(_au, MaxAuLength));
        new Span<byte>(_au + au.Length, PaddingSize).Clear();
        *(byte**)(_packet + PacketData) = _au;
        *(int*)(_packet + PacketSize) = au.Length;
        if (avcodec_send_packet(_context, _packet) < 0)
        {
            return -1;
        }
        int total = 0;
        fixed (float* start = output)
        {
            while (true)
            {
                // receive_frame unrefs the previous frame itself.
                int result = avcodec_receive_frame(_context, _frame);
                if (result == ErrorAgain)
                {
                    return total;
                }
                if (result < 0 || (_resampler is null && !CreateResampler()))
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
    public void Flush() => avcodec_flush_buffers(_context);

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
        NativeMemory.Free(_au);
    }

    // Configured once from the first decoded frame's layout and format.
    private bool CreateResampler()
    {
        byte* mono = stackalloc byte[ChannelLayoutSize];
        // Default native-order layout: nothing to uninit.
        av_channel_layout_default(mono, 1);
        void* resampler = null;
        if (swr_alloc_set_opts2(&resampler, mono, SampleFormatFloat, OutputRate, _frame + FrameChannelLayout, *(int*)(_frame + FrameFormat), StreamRate, 0, null) < 0)
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
    private static partial byte* avcodec_alloc_context3(void* codec);

    [LibraryImport(AvCodec)]
    private static partial int avcodec_open2(byte* context, void* codec, void** options);

    [LibraryImport(AvCodec)]
    private static partial void avcodec_free_context(byte** context);

    [LibraryImport(AvCodec)]
    private static partial void avcodec_flush_buffers(byte* context);

    [LibraryImport(AvCodec)]
    private static partial int avcodec_send_packet(byte* context, byte* packet);

    [LibraryImport(AvCodec)]
    private static partial int avcodec_receive_frame(byte* context, byte* frame);

    [LibraryImport(AvCodec)]
    private static partial byte* av_packet_alloc();

    [LibraryImport(AvCodec)]
    private static partial void av_packet_free(byte** packet);

    [LibraryImport(AvUtil)]
    private static partial void* av_mallocz(nuint size);

    [LibraryImport(AvUtil)]
    private static partial void av_log_set_level(int level);

    [LibraryImport(AvUtil)]
    private static partial byte* av_frame_alloc();

    [LibraryImport(AvUtil)]
    private static partial void av_frame_free(byte** frame);

    [LibraryImport(AvUtil)]
    private static partial void av_channel_layout_default(byte* layout, int channels);

    [LibraryImport(SwResample)]
    private static partial int swr_alloc_set_opts2(void** resampler, byte* outLayout, int outFormat, int outRate, byte* inLayout, int inFormat, int inRate, int logOffset, void* logContext);

    [LibraryImport(SwResample)]
    private static partial int swr_init(void* resampler);

    [LibraryImport(SwResample)]
    private static partial int swr_convert(void* resampler, byte** output, int outputCount, byte** input, int inputCount);

    [LibraryImport(SwResample)]
    private static partial void swr_free(void** resampler);
}
