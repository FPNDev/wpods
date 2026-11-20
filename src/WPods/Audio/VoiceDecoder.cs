using WPods.Core;

namespace WPods.Audio;

/// <summary>
/// HD voice uplink of one voice link to mono 48 kHz float: splits the service's 0x05 data into the codec's units,
/// decodes them, conceals lost ones and makes the downlink's silent unit. Mic thread only.
/// </summary>
internal sealed unsafe class VoiceDecoder : IDisposable
{
    internal const int OutputRate = FfmpegDecoder.OutputRate;
    // HFP codec IDs. 127: one LC3 unit per 60-byte packet every 7.5 ms; 256: the same units spread over 160-byte
    // packets every 20 ms, not aligned to them. 128 and 129 are believed to be AAC-ELD.
    internal const ushort CodecCvsd = 1;
    internal const ushort CodecMsbc = 2;
    internal const ushort CodecLc3 = 127;
    private const ushort CodecAacEld = 128;
    private const ushort CodecAacEldAlt = 129;
    internal const ushort CodecLc3Spread = 256;

    // Units of [u8 sequence][u8 length][frame]; LC3 frames are 7.5 ms, 58 bytes, 24 kHz codec rate (undocumented).
    internal const int UnitHeader = 2;
    internal const int Lc3FrameUs = 7500;
    internal const int Lc3Rate = 24_000;
    internal const int Lc3FrameBytes = 58;
    internal const int Lc3FrameSamples = Lc3FrameUs * OutputRate / 1_000_000;
    // mSBC packet: H2 header (0x01 + sequence byte), 57-byte frame (sync 0xAD), 1 pad byte; 120 samples at 16 kHz.
    internal const int MsbcRate = 16_000;
    internal const int MsbcFrameBytes = 57;
    internal const int MsbcPacketBytes = UnitHeader + MsbcFrameBytes + 1;
    private const int MsbcSamples = 120 * OutputRate / MsbcRate;
    // CVSD comes as linear s16le 8 kHz PCM (the service has the controller convert it), resampled in chunks.
    internal const int CvsdRate = 8_000;
    private const int CvsdChunkBytes = 320;
    // Longer gaps are a stall or dropped backlog, not radio loss: concealing them would only add delay.
    private const int MaxConcealUnits = 8;
    // AAC-ELD: core rate; units each candidate config must decode before it is picked; packets logged per link.
    private const int AacEldRate = 24_000;
    private const int ProbeUnits = 4;
    private const int LoggedPackets = 8;
    private const int LoggedBytes = 64;
    private const int MaxUnitBytes = 512;
    // One 0x05 message (the AAP pipe's read buffer) plus a partial unit left from the previous one.
    private const int BufferBytes = 65536 + MaxUnitBytes;

    // mSBC frame of 16 kHz silence.
    private static ReadOnlySpan<byte> MsbcSilence =>
    [
        0xAD, 0x00, 0x00, 0xC5, 0x00, 0x00, 0x00, 0x00, 0x77, 0x6D, 0xB6, 0xDD, 0xDB, 0x6D, 0xB7, 0x76, 0xDB, 0x6D, 0xDD,
        0xB6, 0xDB, 0x77, 0x6D, 0xB6, 0xDD, 0xDB, 0x6D, 0xB7, 0x76, 0xDB, 0x6D, 0xDD, 0xB6, 0xDB, 0x77, 0x6D, 0xB6, 0xDD,
        0xDB, 0x6D, 0xB7, 0x76, 0xDB, 0x6D, 0xDD, 0xB6, 0xDB, 0x77, 0x6D, 0xB6, 0xDD, 0xDB, 0x6D, 0xB7, 0x76, 0xDB, 0x6C,
    ];

    private readonly VoiceLink _link;
    private readonly Kind _kind;
    private readonly byte[] _buffer = new byte[BufferBytes];
    private int _start;
    private int _end;
    // The unit taken from the buffer (with its header, if any), decoded once the units lost before it are concealed.
    private readonly byte[] _unit;
    private int _unitLength;
    private int _header;
    private bool _held;
    private int _conceal;
    private int _nextSequence = -1;
    private nint _lc3;
    private FfmpegDecoder? _ffmpeg;
    // Samples of one decoded unit at 48 kHz: the length of a concealed one.
    private int _frameSamples;
    // AAC-ELD: units carry the [sequence][length] header (decided from the first packet); units for the config probe,
    // null once it ran.
    private bool? _framed;
    private List<byte[]>? _probe = [];
    private int _loggedPackets;
    private byte[]? _silence;
    private bool _silenceChanged;
    private double _silenceRms = double.MaxValue;
    private long _samples;
    private long _units;
    private long _lost;
    private long _concealed;
    private long _errors;
    private long _skippedBytes;

    private enum Kind
    {
        Lc3,
        AacEld,
        Msbc,
        Cvsd,
    }

    private VoiceDecoder(VoiceLink link, Kind kind)
    {
        _link = link;
        _kind = kind;
        _unit = new byte[Math.Max(MaxUnitBytes, (int)link.PacketIn)];
        try
        {
            switch (kind)
            {
                case Kind.Lc3:
                    _lc3 = Native.wpn_lc3_decoder_create(Lc3FrameUs, Lc3Rate, OutputRate);
                    _frameSamples = Lc3FrameSamples;
                    if (_lc3 == 0)
                    {
                        throw new InvalidOperationException("The LC3 decoder could not be created.");
                    }
                    _silence = Lc3Silence(_frameSamples);
                    break;
                case Kind.Msbc:
                    _ffmpeg = new FfmpegDecoder("sbc", [], MsbcRate, MsbcRate);
                    _frameSamples = MsbcSamples;
                    _silence = [0x01, 0x08, .. MsbcSilence, 0x00];
                    break;
                case Kind.Cvsd:
                    _ffmpeg = new FfmpegDecoder(CvsdRate);
                    _silence = link.PacketOut > 0 ? new byte[link.PacketOut] : null;
                    break;
                case Kind.AacEld:
                    // The config is picked from the first units; the silence is the quietest uplink unit.
                    break;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        _silenceChanged = _silence is not null;
        Log.Info("voice", $"codec {link.Codec} ({kind}{(kind == Kind.AacEld ? ", experimental" : "")}): interval {link.Interval} slots, packets {link.PacketIn} in / {link.PacketOut} out");
    }

    /// <summary>Null for a codec WPods does not decode (logged). Throws when the native decoder cannot load.</summary>
    public static VoiceDecoder? Create(VoiceLink link)
    {
        Kind? kind = link.Codec switch
        {
            CodecLc3 or CodecLc3Spread => Kind.Lc3,
            CodecAacEld or CodecAacEldAlt => Kind.AacEld,
            CodecMsbc => Kind.Msbc,
            CodecCvsd => Kind.Cvsd,
            _ => null,
        };
        if (kind is not { } k)
        {
            Log.Warn("voice", $"codec {link.Codec} not handled, its uplink is ignored");
            return null;
        }
        return new VoiceDecoder(link, k);
    }

    public VoiceLink Link => _link;

    /// <summary>The downlink's silent unit when it changed since the last call (at start; AAC-ELD: a quieter uplink unit).</summary>
    public byte[]? TakeSilence()
    {
        if (!_silenceChanged)
        {
            return null;
        }
        _silenceChanged = false;
        return _silence;
    }

    /// <summary>Appends one 0x05 message; <see cref="Next"/> then decodes it unit by unit.</summary>
    public void Push(ReadOnlySpan<byte> data)
    {
        if (_kind == Kind.AacEld)
        {
            LogPackets(data);
            if (_framed is null)
            {
                _framed = UnitsCover(data[..(_link.PacketIn > 0 ? Math.Min(data.Length, _link.PacketIn) : data.Length)]);
                Log.Info("voice", _framed.Value ? "AAC-ELD: packets carry [sequence][length] units, one access unit each" : "AAC-ELD: no unit headers, each packet is one access unit");
            }
        }
        _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
        _end -= _start;
        _start = 0;
        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    /// <summary>Decodes or conceals the next unit into <paramref name="output"/> (room for 1536 samples); 0 when no whole unit is left.</summary>
    public int Next(Span<float> output)
    {
        while (true)
        {
            int samples;
            if (_conceal > 0)
            {
                _conceal--;
                _concealed++;
                samples = Conceal(output);
            }
            else if (_held)
            {
                _held = false;
                samples = DecodeUnit(output);
            }
            else if (TakeUnit())
            {
                continue;
            }
            else
            {
                return 0;
            }
            if (samples > 0)
            {
                _samples += samples;
                return samples;
            }
        }
    }

    public void Dispose()
    {
        if (_lc3 != 0)
        {
            Native.wpn_lc3_decoder_free(_lc3);
            _lc3 = 0;
        }
        _ffmpeg?.Dispose();
        _ffmpeg = null;
        Log.Info("voice", $"codec {_link.Codec} ended: {_units} units, {_lost} lost ({_concealed} concealed), {_errors} decode errors, {_skippedBytes} bytes skipped, {_samples * 1000 / OutputRate} ms decoded");
    }

    // Moves the next whole unit from the buffer to _unit (_held) and counts the units lost before it (_conceal).
    private bool TakeUnit() => _kind switch
    {
        Kind.Lc3 => TakeFramed(Lc3FrameBytes),
        Kind.AacEld when _framed == true => TakeFramed(0),
        Kind.AacEld => TakeBytes(_link.PacketIn > 0 ? _link.PacketIn : _end - _start),
        Kind.Msbc => TakeMsbc(),
        _ => TakeBytes(Math.Min(_end - _start, CvsdChunkBytes) & ~1),
    };

    // [sequence][length][payload] units; length 0 = any. A byte that cannot start a unit is skipped.
    private bool TakeFramed(int length)
    {
        while (_end - _start >= UnitHeader)
        {
            int unitLength = _buffer[_start + 1];
            if (unitLength == 0 || (length != 0 && unitLength != length))
            {
                _start++;
                _skippedBytes++;
                continue;
            }
            if (_end - _start < UnitHeader + unitLength)
            {
                return false;
            }
            CountLost(_buffer[_start], 256, 127);
            Hold(_start, UnitHeader + unitLength, UnitHeader + unitLength, UnitHeader);
            return true;
        }
        return false;
    }

    private bool TakeBytes(int length)
    {
        if (length == 0 || _end - _start < length)
        {
            return false;
        }
        Hold(_start, length, length, 0);
        return true;
    }

    // Finds the next H2 header followed by an mSBC sync byte; the 2-bit H2 sequence shows up to 3 lost packets.
    private bool TakeMsbc()
    {
        while (_end - _start >= UnitHeader + MsbcFrameBytes)
        {
            int sequence = _buffer[_start + 1] switch
            {
                0x08 => 0,
                0x38 => 1,
                0xC8 => 2,
                0xF8 => 3,
                _ => -1,
            };
            if (_buffer[_start] != 0x01 || sequence < 0 || _buffer[_start + UnitHeader] != 0xAD)
            {
                _start++;
                _skippedBytes++;
                continue;
            }
            CountLost(sequence, 4, 3);
            Hold(_start + UnitHeader, MsbcFrameBytes, Math.Min(MsbcPacketBytes, _end - _start), 0);
            return true;
        }
        return false;
    }

    private void Hold(int offset, int length, int consumed, int header)
    {
        _buffer.AsSpan(offset, length).CopyTo(_unit);
        _unitLength = length;
        _header = header;
        _held = true;
        _start += consumed;
        _units++;
    }

    // Larger jumps than maxLost are a restart of the sequence, not loss.
    private void CountLost(int sequence, int modulo, int maxLost)
    {
        if (_nextSequence >= 0)
        {
            int lost = (sequence - _nextSequence + modulo) % modulo;
            if (lost > 0 && lost <= maxLost)
            {
                _lost += lost;
                _conceal = Math.Min(lost, MaxConcealUnits);
            }
        }
        _nextSequence = (sequence + 1) % modulo;
    }

    private int DecodeUnit(Span<float> output)
    {
        ReadOnlySpan<byte> payload = _unit.AsSpan(_header, _unitLength - _header);
        int samples = _kind switch
        {
            Kind.Lc3 => DecodeLc3(payload, output),
            Kind.AacEld => DecodeAac(payload, output),
            _ => _ffmpeg!.Decode(payload, output),
        };
        if (samples >= 0)
        {
            return samples;
        }
        _errors++;
        return Conceal(output);
    }

    // LC3 conceals in the decoder; mSBC and AAC-ELD repeat silence; CVSD (PCM) has nothing to conceal.
    private int Conceal(Span<float> output)
    {
        if (_kind == Kind.Lc3 && DecodeLc3([], output) is var samples and >= 0)
        {
            return samples;
        }
        output[.._frameSamples].Clear();
        return _frameSamples;
    }

    // An empty frame conceals a lost one.
    private int DecodeLc3(ReadOnlySpan<byte> frame, Span<float> output)
    {
        fixed (byte* data = frame)
        fixed (float* pcm = output)
        {
            return Native.wpn_lc3_decode(_lc3, data, frame.Length, pcm);
        }
    }

    // The first units pick the config; then the quietest unit of the first second becomes the downlink silence.
    private int DecodeAac(ReadOnlySpan<byte> unit, Span<float> output)
    {
        if (_ffmpeg is null)
        {
            if (_probe is null)
            {
                return 0;
            }
            _probe.Add(unit.ToArray());
            if (_probe.Count == ProbeUnits)
            {
                PickAacConfig(_probe, output);
                _probe = null;
            }
            return 0;
        }
        int samples = _ffmpeg.Decode(unit, output);
        if (samples > 0)
        {
            _frameSamples = samples;
            if (_samples < OutputRate)
            {
                TrackQuietest(output[..samples]);
            }
        }
        return samples;
    }

    private void TrackQuietest(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float sample in samples)
        {
            sum += sample * sample;
        }
        double rms = Math.Sqrt(sum / samples.Length);
        if (rms < _silenceRms)
        {
            _silenceRms = rms;
            _silence = _unit.AsSpan(0, _unitLength).ToArray();
            _silenceChanged = true;
        }
    }

    // The first candidate that decodes every probe unit without an error wins; the probe's audio is dropped.
    private void PickAacConfig(List<byte[]> units, Span<float> scratch)
    {
        foreach ((string name, byte[] config, int rate) in AacEldCandidates())
        {
            FfmpegDecoder decoder;
            try
            {
                decoder = new FfmpegDecoder("aac", config, AacEldRate, rate);
            }
            catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                Log.Info("voice", $"AAC-ELD {name} ({Convert.ToHexString(config)}) not opened: {e.Message}");
                continue;
            }
            int total = 0;
            int failed = -1;
            for (int i = 0; i < units.Count && failed < 0; i++)
            {
                int samples = decoder.Decode(units[i], scratch);
                if (samples < 0)
                {
                    failed = i;
                }
                total += Math.Max(samples, 0);
            }
            if (failed < 0 && total > 0)
            {
                Log.Info("voice", $"AAC-ELD {name} ({Convert.ToHexString(config)}) decodes the first {units.Count} units ({total} samples)");
                _ffmpeg = decoder;
                return;
            }
            Log.Info("voice", $"AAC-ELD {name} ({Convert.ToHexString(config)}) rejected: {(failed >= 0 ? $"unit {failed + 1} failed" : "no samples")}");
            decoder.Dispose();
        }
        Log.Warn("voice", $"AAC-ELD: no config decodes the first {units.Count} units, uplink not decoded");
    }

    // Mono 24 kHz AAC-ELD with 480- and 512-sample frames, without and then with LD-SBR (dual rate, 48 kHz out).
    private static IEnumerable<(string Name, byte[] Config, int Rate)> AacEldCandidates()
    {
        foreach (bool ldSbr in (bool[])[false, true])
        {
            foreach (bool frame480 in (bool[])[true, false])
            {
                yield return ($"{(frame480 ? 480 : 512)}-sample frames{(ldSbr ? " + LD-SBR" : "")}", EldConfig(frame480, ldSbr), ldSbr ? 2 * AacEldRate : AacEldRate);
            }
        }
    }

    // AudioSpecificConfig: AOT 39 (escaped), 24 kHz index, mono, ELDSpecificConfig, epConfig 0. The LD-SBR header
    // holds default values: the AirPods' real one is unknown.
    private static byte[] EldConfig(bool frame480, bool ldSbr)
    {
        ulong bits = 0;
        int count = 0;
        void Put(int value, int width)
        {
            bits = (bits << width) | (uint)value;
            count += width;
        }
        Put(31, 5);
        Put(39 - 32, 6);
        Put(6, 4);
        Put(1, 4);
        Put(frame480 ? 1 : 0, 1);
        // Section, scalefactor and spectral data resilience off.
        Put(0, 3);
        Put(ldSbr ? 1 : 0, 1);
        if (ldSbr)
        {
            // ldSbrSamplingRate dual rate, no CRC; sbr_header: amp res 1, start/stop freq 0, xover 0, no extra headers.
            Put(1, 1);
            Put(0, 1);
            Put(1, 1);
            Put(0, 4);
            Put(0, 4);
            Put(0, 3);
            Put(0, 2);
            Put(0, 2);
        }
        // ELDEXT_TERM, then epConfig.
        Put(0, 4);
        Put(0, 2);
        int length = (count + 7) / 8;
        bits <<= length * 8 - count;
        byte[] config = new byte[length];
        for (int i = 0; i < length; i++)
        {
            config[i] = (byte)(bits >> (8 * (length - 1 - i)));
        }
        return config;
    }

    private static bool UnitsCover(ReadOnlySpan<byte> packet)
    {
        int offset = 0;
        while (packet.Length - offset >= UnitHeader && packet[offset + 1] != 0)
        {
            offset += UnitHeader + packet[offset + 1];
        }
        return offset == packet.Length && offset > 0;
    }

    // The first packets of each AAC-ELD link, to show its framing.
    private void LogPackets(ReadOnlySpan<byte> data)
    {
        int size = _link.PacketIn > 0 ? _link.PacketIn : data.Length;
        for (int offset = 0; offset < data.Length && _loggedPackets < LoggedPackets; offset += size)
        {
            ReadOnlySpan<byte> packet = data.Slice(offset, Math.Min(size, data.Length - offset));
            _loggedPackets++;
            Log.Info("voice", $"AAC-ELD packet {_loggedPackets}: {packet.Length} bytes (message {data.Length}): {Convert.ToHexString(packet[..Math.Min(packet.Length, LoggedBytes)])}");
        }
    }

    // [0][58] + 7.5 ms of encoded silence; null when the encoder fails.
    private static byte[]? Lc3Silence(int frameSamples)
    {
        nint encoder = Native.wpn_lc3_encoder_create(Lc3FrameUs, Lc3Rate, OutputRate);
        if (encoder == 0)
        {
            Log.Warn("voice", "LC3 encoder not created: no downlink silence");
            return null;
        }
        try
        {
            float[] zeros = new float[frameSamples];
            byte[] unit = new byte[UnitHeader + Lc3FrameBytes];
            unit[1] = Lc3FrameBytes;
            fixed (float* pcm = zeros)
            fixed (byte* frame = &unit[UnitHeader])
            {
                if (Native.wpn_lc3_encode(encoder, pcm, Lc3FrameBytes, frame) != 0)
                {
                    Log.Warn("voice", "LC3 silence not encoded: no downlink silence");
                    return null;
                }
            }
            return unit;
        }
        finally
        {
            Native.wpn_lc3_encoder_free(encoder);
        }
    }
}
