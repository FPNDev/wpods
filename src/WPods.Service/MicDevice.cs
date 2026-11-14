using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Win32;

namespace WPods.Service;

/// <summary>
/// The WPods microphone: a virtual USB Audio Class 1 capture device (mono 48 kHz 16-bit) for Windows' usbaudio driver,
/// named "&lt;terminal&gt; (&lt;product&gt;)" = "Headset (A2DP, &lt;AirPods name&gt;)". The serial comes from the
/// pair's address, so each pair has its own endpoint. SET_INTERFACE 1 alt 1/0 = an app started/stopped recording.
/// </summary>
internal sealed class MicDevice
{
    public const ushort Vid = 0x1209;
    // pid.codes shared test PID.
    public const ushort Pid = 0x0001;
    private const int Rate = 48000;
    // At most ~85 ms of PCM waits for the host; older samples are dropped so latency cannot grow.
    private const int Backlog = 4096;

    private readonly Dictionary<byte, byte[]> _strings = [];
    private readonly Lock _pcmLock = new();
    private readonly short[] _ring = new short[Backlog];
    private int _ringStart;
    private int _ringCount;
    private volatile bool _capturing;
    // Per-capture log stats: samples received, sent, and sent as silence because the app's PCM ran out; peak levels.
    private long _received;
    private long _sent;
    private long _missing;
    private int _peakIn;
    private int _peakOut;
    // Latency trim, under _pcmLock: least PCM left after a transfer in the current window, the window's length, and
    // samples dropped this capture.
    private const int TrimAbove = 480; // 10 ms
    private const int TrimTo = 240; // 5 ms
    private int _leastLeft = int.MaxValue;
    private int _levelMs;
    private long _trimmed;
    // Feature unit 2 as Windows set it: mute, and volume in 1/256 dB (-60..0 dB) with its linear gain.
    private const short MinVolume = -60 * 256;
    private volatile bool _muted;
    private short _volume;
    private volatile float _gain = 1f;
    private byte _configuration;
    private byte _alt;

    public MicDevice(ulong address, string name)
    {
        Address = address;
        Name = name;
        Serial = SerialOf(address);
        Device = [18, 0x01, 0x10, 0x01, 0x00, 0x00, 0x00, 64, unchecked((byte)Vid), Vid >> 8, unchecked((byte)Pid), Pid >> 8, 0x00, 0x01, 1, 2, 3, 1];
        _strings[0] = UsbIp.EnglishLanguages;
        _strings[1] = UsbIp.StringDescriptor("WPods");
        _strings[2] = UsbIp.StringDescriptor("A2DP, " + (name.Length > 100 ? name[..100] : name));
        _strings[3] = UsbIp.StringDescriptor(Serial);
        _strings[4] = UsbIp.StringDescriptor("Headset");
        var b = new List<byte>();
        void A(params byte[] x) => b.AddRange(x);
        A(9, 0x02, 0, 0, 2, 1, 0, 0x80, 50);                        // configuration: 2 interfaces, bus powered
        A(9, 0x04, 0, 0, 0, 0x01, 0x01, 0x00, 0);                    // AC interface 0
        A(9, 0x24, 0x01, 0x00, 0x01, 39, 0, 1, 1);                   // AC header, wTotalLength 9+12+9+9 = 39, AS interface 1
        A(12, 0x24, 0x02, 1, 0x01, 0x02, 0, 1, 0x00, 0x00, 0, 4);    // input terminal 1: microphone, mono, iTerminal 4
        A(9, 0x24, 0x03, 3, 0x01, 0x01, 0, 2, 0);                    // output terminal 3: USB streaming, source FU 2
        A(9, 0x24, 0x06, 2, 1, 1, 0x03, 0x00, 0);                    // feature unit 2: mute + volume, source IT 1
        A(9, 0x04, 1, 0, 0, 0x01, 0x02, 0x00, 0);                    // AS interface 1 alt 0 (zero bandwidth)
        A(9, 0x04, 1, 1, 1, 0x01, 0x02, 0x00, 0);                    // AS interface 1 alt 1
        A(7, 0x24, 0x01, 3, 1, 0x01, 0x00);                          // AS general: terminal link 3, PCM
        A(11, 0x24, 0x02, 1, 1, 2, 16, 1, 0x80, 0xBB, 0x00);         // format type I: mono, 2 bytes, 16 bits, 48000 Hz
        A(9, 0x05, 0x81, 0x0D, 96, 0, 1, 0, 0);                      // iso IN endpoint 1, synchronous, 96 bytes every 1 ms
        A(7, 0x25, 0x01, 0x01, 0, 0, 0);                             // CS endpoint: sampling frequency control
        Config = [.. b];
        BinaryPrimitives.WriteUInt16LittleEndian(Config.AsSpan(2), (ushort)Config.Length);
    }

    public ulong Address { get; }
    public string Name { get; }
    public string Serial { get; }
    public byte[] Device { get; }
    public byte[] Config { get; }

    /// <summary>Raised on the URB loop thread when an app starts (true) or stops (false) recording.</summary>
    public event Action<bool>? CaptureChanged;

    public bool Capturing => _capturing;

    public static string SerialOf(ulong address) => $"WPODS{address:X12}";

    /// <summary>Queues PCM from the app (mono 48 kHz s16le). Dropped while no app records.</summary>
    public void Push(ReadOnlySpan<byte> pcm)
    {
        if (!_capturing)
        {
            return;
        }
        lock (_pcmLock)
        {
            _received += pcm.Length / 2;
            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                short s = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]);
                _peakIn = Math.Max(_peakIn, Math.Abs((int)s));
                if (_ringCount == Backlog)
                {
                    _ringStart = (_ringStart + 1) % Backlog;
                    _ringCount--;
                }
                _ring[(_ringStart + _ringCount) % Backlog] = s;
                _ringCount++;
            }
        }
    }

    /// <summary>Serves usbaudio's requests on the caller's thread until the connection ends.</summary>
    public void Serve(TcpClient client)
    {
        NetworkStream s = client.GetStream();
        var write = new Lock();
        var jobs = new BlockingCollection<(uint Seq, uint StartFrame, byte[] Descriptors, int Packets, long Due)>();
        var pending = new ConcurrentDictionary<uint, bool>();
        void Send(byte[] frame)
        {
            lock (write)
            {
                s.Write(frame);
            }
        }
        var pacer = new Thread(() =>
        {
            try
            {
                foreach (var job in jobs.GetConsumingEnumerable())
                {
                    WaitUntil(job.Due);
                    if (!pending.TryRemove(job.Seq, out _))
                    {
                        continue; // unlinked
                    }
                    Send(UsbIp.RetIso(job.Seq, 0, job.StartFrame, job.Descriptors, job.Packets, isIn: true, Fill(job.Descriptors, job.Packets)));
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // Connection closed.
            }
        }) { IsBackground = true, Name = "mic iso", Priority = ThreadPriority.Highest };
        pacer.Start();
        long nextDue = 0;
        var header = new byte[48];
        try
        {
            while (true)
            {
                s.ReadExactly(header);
                uint cmd = UsbIp.U32(header, 0);
                uint seq = UsbIp.U32(header, 4);
                uint dir = UsbIp.U32(header, 12);
                uint ep = UsbIp.U32(header, 16);
                if (cmd == UsbIp.CmdUnlink)
                {
                    Send(UsbIp.RetUnlinkFrame(seq, pending.TryRemove(UsbIp.U32(header, 20), out _) ? UsbIp.ECONNRESET : 0));
                    continue;
                }
                if (cmd != UsbIp.CmdSubmit)
                {
                    throw new IOException($"unknown USB/IP command {cmd}");
                }
                uint length = UsbIp.U32(header, 24);
                uint packets = UsbIp.U32(header, 32);
                byte[] outData = dir == 0 && length > 0 ? new byte[length] : [];
                s.ReadExactly(outData);
                byte[] descriptors = [];
                if (packets != UsbIp.NoIso && packets > 0)
                {
                    descriptors = new byte[16 * packets];
                    s.ReadExactly(descriptors);
                }
                if (ep == 0)
                {
                    (byte[]? data, int status) = Control(header.AsSpan(40, 8), outData);
                    if (data is not null && data.Length > length)
                    {
                        data = data[..(int)length];
                    }
                    uint actual = dir == 0 ? (status == 0 ? (uint)outData.Length : 0) : (uint)(data?.Length ?? 0);
                    Send(UsbIp.Ret(seq, status, actual, dir == 1 ? data ?? [] : []));
                    continue;
                }
                if (ep == 1 && dir == 1 && descriptors.Length > 0)
                {
                    long now = Stopwatch.GetTimestamp();
                    nextDue = Math.Max(nextDue, now) + (packets * Stopwatch.Frequency / 1000);
                    pending[seq] = true;
                    jobs.Add((seq, UsbIp.U32(header, 28), descriptors, (int)packets, nextDue));
                    continue;
                }
                Send(UsbIp.Ret(seq, UsbIp.EPIPE, 0, []));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or EndOfStreamException)
        {
            Log.Info("mic", $"USB/IP connection ended: {e.Message}");
        }
        finally
        {
            jobs.CompleteAdding();
            client.Dispose();
            pacer.Join(1000);
            _configuration = 0;
            SetAlt(0);
        }
    }

    private static void WaitUntil(long due)
    {
        while (true)
        {
            long left = due - Stopwatch.GetTimestamp();
            if (left <= 0)
            {
                return;
            }
            // With 1 ms timer resolution, sleeping the last millisecond overshoots by under 1 ms, which the host's
            // request queue absorbs; spinning would cost CPU.
            Thread.Sleep((int)Math.Max(1, left * 1000 / Stopwatch.Frequency));
        }
    }

    // One iso IN transfer: every packet full (its CMD length), PCM from the backlog, silence when it runs out.
    private IsoResult Fill(byte[] descriptors, int packets)
    {
        var offsets = new int[packets];
        var actual = new int[packets];
        int total = 0;
        for (int i = 0; i < packets; i++)
        {
            offsets[i] = total;
            actual[i] = (int)UsbIp.U32(descriptors, (16 * i) + 4) & ~1;
            total += actual[i];
        }
        var data = new byte[total];
        if (_capturing)
        {
            lock (_pcmLock)
            {
                int n = Math.Min(_ringCount, total / 2);
                for (int i = 0; i < n; i++)
                {
                    short s = Scale(_ring[(_ringStart + i) % Backlog]);
                    _peakOut = Math.Max(_peakOut, Math.Abs((int)s));
                    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2 * i), s);
                }
                _ringStart = (_ringStart + n) % Backlog;
                _ringCount -= n;
                _sent += total / 2;
                _missing += (total / 2) - n;
                TrimLocked(packets);
            }
        }
        return new IsoResult(data, offsets, actual, new int[packets]);
    }

    // Windows' mute and input volume for this mic (feature unit 2), applied to every sample sent.
    private short Scale(short s) => _muted ? (short)0 : _gain == 1f ? s : (short)(s * _gain);

    // PCM left over after a transfer is latency; the least left over across a second was never needed (e.g. late PCM
    // queued behind gap-filling silence). Above TrimAbove it is trimmed to TrimTo, oldest first.
    // Caller holds _pcmLock; packets = this transfer's 1 ms frames.
    private void TrimLocked(int packets)
    {
        _leastLeft = Math.Min(_leastLeft, _ringCount);
        _levelMs += packets;
        if (_levelMs < 1000)
        {
            return;
        }
        if (_leastLeft > TrimAbove)
        {
            int drop = _leastLeft - TrimTo;
            _ringStart = (_ringStart + drop) % Backlog;
            _ringCount -= drop;
            _trimmed += drop;
        }
        _leastLeft = int.MaxValue;
        _levelMs = 0;
    }

    // Returns (data, status): status 0 = OK, -32 = stall.
    private (byte[]? Data, int Status) Control(ReadOnlySpan<byte> setup, byte[] outData)
    {
        byte rt = setup[0];
        byte req = setup[1];
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(setup[2..]);
        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(setup[4..]);
        if ((rt & 0x60) == 0)
        {
            switch (req)
            {
                case 0x06:
                    {
                        int type = value >> 8;
                        if (type == 1)
                        {
                            return (Device, 0);
                        }
                        if (type == 2)
                        {
                            return (Config, 0);
                        }
                        return type == 3 && _strings.TryGetValue((byte)value, out byte[]? str) ? (str, 0) : (null, UsbIp.EPIPE);
                    }
                case 0x05:
                    return (null, 0);
                case 0x09:
                    _configuration = (byte)value;
                    SetAlt(0);
                    return (null, 0);
                case 0x08:
                    return ([_configuration], 0);
                case 0x0B:
                    if (index == 1)
                    {
                        SetAlt((byte)value);
                        return (null, 0);
                    }
                    return index == 0 && value == 0 ? (null, 0) : (null, UsbIp.EPIPE);
                case 0x0A:
                    return ([index == 1 ? _alt : (byte)0], 0);
                case 0x00:
                    return ([0, 0], 0);
                case 0x01:
                case 0x03:
                    return (null, 0);
                default:
                    return (null, UsbIp.EPIPE);
            }
        }
        int recipient = rt & 0x1F;
        if (recipient == 0x02 && (value >> 8) == 0x01 && (byte)index == 0x81)
        {
            // Endpoint sampling frequency: only 48 kHz.
            if (req == 0x01)
            {
                int rate = outData.Length >= 3 ? outData[0] | (outData[1] << 8) | (outData[2] << 16) : 0;
                return rate is Rate or 0 ? (null, 0) : (null, UsbIp.EPIPE);
            }
            if (req is 0x81 or 0x82 or 0x83)
            {
                return ([0x80, 0xBB, 0x00], 0);
            }
            if (req == 0x84)
            {
                return ([1, 0, 0], 0);
            }
        }
        if (recipient == 0x01 && (index >> 8) == 2)
        {
            // Feature unit 2: mute, and volume -60..0 dB in 1 dB steps.
            int selector = value >> 8;
            if (selector == 1)
            {
                if (req == 0x01 && outData.Length >= 1)
                {
                    _muted = outData[0] != 0;
                    Log.Info("mic", _muted ? "muted" : "unmuted");
                }
                return req == 0x01 ? (null, 0) : ([_muted ? (byte)1 : (byte)0], 0);
            }
            if (selector == 2)
            {
                if (req == 0x01 && outData.Length >= 2)
                {
                    _volume = Math.Clamp(BinaryPrimitives.ReadInt16LittleEndian(outData), MinVolume, (short)0);
                    _gain = MathF.Pow(10, _volume / 256f / 20);
                    Log.Info("mic", $"volume {_volume / 256f:0.#} dB");
                }
                if (req == 0x01)
                {
                    return (null, 0);
                }
                short v = req switch { 0x81 => _volume, 0x82 => MinVolume, 0x84 => 256, _ => 0 };
                return ([(byte)v, (byte)(v >> 8)], 0);
            }
        }
        Log.Warn("mic", $"stall: control rt=0x{rt:X2} req=0x{req:X2} v=0x{value:X4} i=0x{index:X4}");
        return (null, UsbIp.EPIPE);
    }

    private void SetAlt(byte alt)
    {
        bool was = _capturing;
        _alt = alt;
        bool now = _configuration == 1 && _alt == 1;
        if (now == was)
        {
            return;
        }
        string stats = "";
        lock (_pcmLock)
        {
            if (now)
            {
                _ringCount = 0;
                _received = _sent = _missing = _trimmed = 0;
                _peakIn = _peakOut = 0;
                _leastLeft = int.MaxValue;
                _levelMs = 0;
            }
            else
            {
                stats = $": {_received} samples from the app, {_sent} sent, {_missing} of them silence (no PCM in time), {_trimmed} dropped as latency; peak in {_peakIn}, out {_peakOut}";
            }
        }
        _capturing = now;
        Log.Info("mic", now ? "capture started" : "capture stopped" + stats);
        CaptureChanged?.Invoke(now);
    }

    /// <summary>
    /// Forgets Windows' device nodes of the mic with <paramref name="serial"/>, so the next attach gets a fresh endpoint
    /// with the new name instead of Windows' cached one.
    /// </summary>
    public static void ForgetDevnodes(string serial)
    {
        string parent = $@"{UsbDevice}\{serial}";
        // The detach is not instant: wait until Windows has unplugged the device (only hidden nodes are forgotten).
        for (int i = 0; i < 30 && RadioDriver.IsPresent(parent); i++)
        {
            Thread.Sleep(100);
        }
        int removed = RadioDriver.ForgetHiddenSubtree(parent, includeRoot: true, audioOnly: false);
        Log.Info("mic", $"forgot {removed} device nodes of {parent}");
    }

    /// <summary>Forgets every pair's mic devnodes; WPods serials only, as other devices may share the test ID.</summary>
    public static void ForgetAllDevnodes()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + UsbDevice);
        foreach (string serial in key?.GetSubKeyNames() ?? [])
        {
            if (serial.StartsWith("WPODS", StringComparison.OrdinalIgnoreCase))
            {
                RadioDriver.ForgetHiddenSubtree($@"{UsbDevice}\{serial}", includeRoot: true, audioOnly: false);
            }
        }
    }

    private static string UsbDevice => $@"USB\VID_{Vid:X4}&PID_{Pid:X4}";
}
