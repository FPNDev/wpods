using WPods.Core;

namespace WPods.Audio;

/// <summary>
/// HD voice downlink of one voice link: what Windows plays to the attached pair (loopback, mono 48 kHz) encoded in the
/// link's codec and framing and sent as 0xF2 as it is captured, on its own audio thread. The uplink's decoded samples
/// (<see cref="AdvanceLink"/>) are the link's clock: when the capture drifts a frame away from it, a frame is dropped or
/// repeated. Created and disposed on the mic thread.
/// </summary>
internal sealed class VoiceEncoder : IDisposable
{
    private const int Rate = VoiceDecoder.OutputRate;
    // Every codec takes 7.5 ms frames: one LC3 frame, one mSBC frame (120 samples at 16 kHz), 60 CVSD samples.
    private const int FrameSamples = VoiceDecoder.Lc3FrameSamples;
    private const int MsbcFrameSamples = 120;
    // Link interval slots are 0.625 ms.
    private const int SamplesPerSlot = Rate * 625 / 1_000_000;
    private const int PcmCapacity = 4096;
    private const int UnitCapacity = 512;
    // No audio for this long is a gap (Windows plays nothing there): the service's silence covers it and the next
    // audio starts anew.
    private const uint ReadTimeoutMs = 50;
    private const int OpenRetryMs = 1000;
    private const int ReopenMs = 100;
    // Frames per 0xF2 message; writes in flight beyond which frames are dropped (the pipe is stuck).
    private const int MaxBatchFrames = 3;
    private const int MaxPendingWrites = 4;
    // Drift: the average of (sent - link samples) over a window against the first window's. A frame off drops or
    // repeats one; more than ResyncSamples is a jump (uplink lost or stalled), taken as the new reference.
    private const int DriftWindowFrames = 2 * Rate / FrameSamples;
    private const int ResyncSamples = Rate * 80 / 1000;

    private static readonly byte[] H2Sequence = [0x08, 0x38, 0xC8, 0xF8];
    private static readonly KeyValuePair<string, string>[] MsbcOptions = [new("msbc", "1")];

    private readonly VoiceLink _link;
    private readonly AapSession _session;
    private readonly Kind _kind;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stopping;
    // Uplink samples decoded since the link came up (mic thread adds, downlink thread reads).
    private long _linkSamples;
    private int _pendingWrites;
    private long _failedWrites;

    // Downlink thread.
    private nint _lc3;
    private FfmpegEncoder? _ffmpeg;
    private nint _capture;
    private int _period;
    private int _openError;
    private readonly float[] _pcm = new float[PcmCapacity];
    private int _fill;
    private readonly float[] _silence = new float[FrameSamples];
    private readonly byte[] _unit = new byte[UnitCapacity];
    private readonly byte[] _batch = new byte[MaxBatchFrames * UnitCapacity];
    private int _batchBytes;
    private int _batchFrames;
    private byte _sequence;
    // Since the last start (first audio, or audio after a gap): link samples then, samples sent since, drift window.
    private bool _anchored;
    private long _anchorLink;
    private long _sent;
    private double _windowSum;
    private int _windowFrames;
    private double? _reference;
    private bool _drop;
    private bool _repeat;
    private long _frames;
    private long _leadIn;
    private long _dropped;
    private long _repeated;
    private long _resyncs;
    private long _gaps;
    private long _stuckDropped;
    private long _encodeErrors;

    private enum Kind
    {
        Lc3,
        Msbc,
        Cvsd,
    }

    private VoiceEncoder(VoiceLink link, AapSession session, Kind kind)
    {
        _link = link;
        _session = session;
        _kind = kind;
        try
        {
            switch (kind)
            {
                case Kind.Lc3:
                    _lc3 = Native.wpn_lc3_encoder_create(VoiceDecoder.Lc3FrameUs, VoiceDecoder.Lc3Rate, Rate);
                    if (_lc3 == 0)
                    {
                        throw new InvalidOperationException("The LC3 encoder could not be created.");
                    }
                    break;
                case Kind.Msbc:
                    _ffmpeg = new FfmpegEncoder("sbc", MsbcOptions, VoiceDecoder.MsbcRate, MsbcFrameSamples);
                    break;
                case Kind.Cvsd:
                    _ffmpeg = new FfmpegEncoder(VoiceDecoder.CvsdRate);
                    break;
            }
        }
        catch
        {
            FreeEncoder();
            throw;
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "voice downlink" };
    }

    /// <summary>Null for a codec WPods cannot encode (its downlink stays the silent unit). Throws when the native encoder cannot load.</summary>
    public static VoiceEncoder? Start(VoiceLink link, AapSession session)
    {
        Kind? kind = link.Codec switch
        {
            VoiceDecoder.CodecLc3 or VoiceDecoder.CodecLc3Spread => Kind.Lc3,
            VoiceDecoder.CodecMsbc => Kind.Msbc,
            VoiceDecoder.CodecCvsd => Kind.Cvsd,
            _ => null,
        };
        if (kind is not { } k)
        {
            Log.Info("voice", $"codec {link.Codec}: no encoder, the downlink stays silent");
            return null;
        }
        var encoder = new VoiceEncoder(link, session, k);
        encoder._thread.Start();
        return encoder;
    }

    /// <summary>Mic thread: <paramref name="samples"/> uplink samples (48 kHz) were decoded or concealed.</summary>
    public void AdvanceLink(int samples) => Interlocked.Add(ref _linkSamples, samples);

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread.Join();
        _wake.Dispose();
        FreeEncoder();
        Log.Info("voice", $"codec {_link.Codec} downlink ended: {_frames - _stuckDropped} frames sent ({_leadIn} silent lead-in), {_dropped} dropped and {_repeated} repeated for drift, {_resyncs} resyncs, {_gaps} capture gaps, {_stuckDropped} not written (pipe stuck or session closed),{Interlocked.Read(ref _failedWrites)} writes failed, {_encodeErrors} encode errors");
    }

    private void FreeEncoder()
    {
        if (_lc3 != 0)
        {
            Native.wpn_lc3_encoder_free(_lc3);
            _lc3 = 0;
        }
        _ffmpeg?.Dispose();
        _ffmpeg = null;
    }

    private unsafe void Run()
    {
        try
        {
            Native.MakeAudioThread();
            Native.wpn_fp_set_ftz_daz();
            while (!_stopping)
            {
                if (_capture == 0 && !Open())
                {
                    _wake.WaitOne(OpenRetryMs);
                    continue;
                }
                int read;
                fixed (float* pcm = _pcm)
                {
                    read = Native.wpn_loopback_read(_capture, pcm + _fill, (uint)(_pcm.Length - _fill), ReadTimeoutMs);
                }
                if (read < 0)
                {
                    Log.Warn("voice", $"downlink capture failed: 0x{read:X8}, reopening");
                    Close();
                    Restart();
                    _wake.WaitOne(ReopenMs);
                    continue;
                }
                if (read == 0)
                {
                    if (_anchored)
                    {
                        _gaps++;
                        Restart();
                    }
                    continue;
                }
                _fill += read;
                int offset = 0;
                for (; _fill - offset >= FrameSamples; offset += FrameSamples)
                {
                    Frame(_pcm.AsSpan(offset, FrameSamples));
                }
                _pcm.AsSpan(offset, _fill - offset).CopyTo(_pcm);
                _fill -= offset;
                Flush();
            }
        }
        catch (Exception e)
        {
            Log.Error("voice", "downlink stopped", e);
        }
        finally
        {
            Close();
        }
    }

    private bool Open()
    {
        int hr = Native.wpn_loopback_open(_session.Address, out nint capture, out uint period);
        if (hr < 0)
        {
            if (hr != _openError)
            {
                _openError = hr;
                Log.Warn("voice", $"downlink capture not opened: 0x{hr:X8}, retrying every {OpenRetryMs} ms");
            }
            return false;
        }
        _openError = 0;
        _capture = capture;
        _period = (int)period;
        Log.Info("voice", $"downlink capture opened: {period} samples per packet");
        return true;
    }

    private void Close()
    {
        if (_capture != 0)
        {
            Native.wpn_loopback_close(_capture);
            _capture = 0;
        }
    }

    // A gap or a reopen: the partial frame is dropped and the next audio starts anew.
    private void Restart()
    {
        _anchored = false;
        _fill = 0;
    }

    private void Frame(Span<float> frame)
    {
        if (!_anchored)
        {
            Anchor();
        }
        if (_drop)
        {
            _drop = false;
            _dropped++;
        }
        else
        {
            foreach (ref float sample in frame)
            {
                sample = Math.Clamp(sample, -1f, 1f);
            }
            Encode(frame);
            if (_repeat)
            {
                _repeat = false;
                _repeated++;
                Encode(frame);
            }
        }
        TrackDrift();
    }

    // The service's queue ran dry meanwhile: silence goes first, enough to cover a link interval plus a capture packet,
    // so the packets' arrival pattern does not run it dry again.
    private void Anchor()
    {
        _anchored = true;
        _anchorLink = Interlocked.Read(ref _linkSamples);
        _sent = 0;
        _windowSum = 0;
        _windowFrames = 0;
        _reference = null;
        _drop = false;
        _repeat = false;
        int leadIn = (_link.Interval * SamplesPerSlot + _period + FrameSamples - 1) / FrameSamples;
        for (int i = 0; i < leadIn; i++)
        {
            Encode(_silence);
            _leadIn++;
        }
    }

    // Window averages smooth out the uplink's and capture's arrival in bursts.
    private void TrackDrift()
    {
        _windowSum += _sent - (Interlocked.Read(ref _linkSamples) - _anchorLink);
        if (++_windowFrames < DriftWindowFrames)
        {
            return;
        }
        double average = _windowSum / _windowFrames;
        _windowSum = 0;
        _windowFrames = 0;
        if (_reference is not { } reference)
        {
            _reference = average;
            return;
        }
        double drift = average - reference;
        if (Math.Abs(drift) > ResyncSamples)
        {
            _reference = average;
            _resyncs++;
        }
        else if (drift > FrameSamples)
        {
            _drop = true;
        }
        else if (drift < -FrameSamples)
        {
            _repeat = true;
        }
    }

    // One frame into the batch in the link's framing; the service rewrites the sequence bytes.
    private void Encode(ReadOnlySpan<float> frame)
    {
        int length = _kind switch
        {
            Kind.Lc3 => EncodeLc3(frame),
            Kind.Msbc => EncodeMsbc(frame),
            _ => _ffmpeg!.Push(frame) ? _ffmpeg.Next(_unit) : -1,
        };
        if (length < 0)
        {
            _encodeErrors++;
            return;
        }
        if (_batchBytes + length > _batch.Length)
        {
            Flush();
        }
        _unit.AsSpan(0, length).CopyTo(_batch.AsSpan(_batchBytes));
        _batchBytes += length;
        _batchFrames++;
        _sent += FrameSamples;
        _frames++;
        if (_batchFrames >= MaxBatchFrames)
        {
            Flush();
        }
    }

    private unsafe int EncodeLc3(ReadOnlySpan<float> frame)
    {
        _unit[0] = _sequence++;
        _unit[1] = VoiceDecoder.Lc3FrameBytes;
        fixed (float* pcm = frame)
        fixed (byte* data = &_unit[VoiceDecoder.UnitHeader])
        {
            return Native.wpn_lc3_encode(_lc3, pcm, VoiceDecoder.Lc3FrameBytes, data) == 0 ? VoiceDecoder.UnitHeader + VoiceDecoder.Lc3FrameBytes : -1;
        }
    }

    // 0, 1 or (resampler timing) 2 packets of H2 header + 57-byte frame + pad byte.
    private int EncodeMsbc(ReadOnlySpan<float> frame)
    {
        if (!_ffmpeg!.Push(frame))
        {
            return -1;
        }
        int length = 0;
        while (length + VoiceDecoder.MsbcPacketBytes <= _unit.Length)
        {
            int encoded = _ffmpeg.Next(_unit.AsSpan(length + VoiceDecoder.UnitHeader, VoiceDecoder.MsbcFrameBytes));
            if (encoded == 0)
            {
                break;
            }
            if (encoded != VoiceDecoder.MsbcFrameBytes)
            {
                return -1;
            }
            _unit[length] = 0x01;
            _unit[length + 1] = H2Sequence[_sequence++ & 3];
            _unit[length + VoiceDecoder.MsbcPacketBytes - 1] = 0;
            length += VoiceDecoder.MsbcPacketBytes;
        }
        return length;
    }

    // Sends the batch as one 0xF2 message. Frames that cannot be written (session closed, pipe stuck) are not counted as
    // sent, so the drift reference stays right.
    private void Flush()
    {
        if (_batchFrames == 0)
        {
            return;
        }
        int frames = _batchFrames;
        ReadOnlySpan<byte> data = _batch.AsSpan(0, _batchBytes);
        _batchBytes = 0;
        _batchFrames = 0;
        if (data.IsEmpty)
        {
            return;
        }
        if (_session.Completion.IsCompleted || Volatile.Read(ref _pendingWrites) >= MaxPendingWrites)
        {
            _stuckDropped += frames;
            _sent -= (long)frames * FrameSamples;
            return;
        }
        Interlocked.Increment(ref _pendingWrites);
        // The message is copied before the call returns.
        _ = WriteAsync(_session.WriteVoiceAsync(ServicePipes.VoiceDownlink, data));
    }

    private async Task WriteAsync(ValueTask<bool> write)
    {
        try
        {
            if (!await write.ConfigureAwait(false))
            {
                Interlocked.Increment(ref _failedWrites);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingWrites);
        }
    }
}
