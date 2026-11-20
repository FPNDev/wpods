using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace WPods.Service;

/// <summary>
/// The Bluetooth adapter as usbip-win2 sees it: runs Windows' USB/IP requests on the WinUSB-bound radio, one worker per
/// pipe (in-order completion, as on a real host controller). HCI events and ACL data go through <see cref="HciProxy"/>.
/// </summary>
internal sealed class RadioLink : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly WinUsbDevice _device;
    private readonly HciProxy _hci;
    private readonly Lock _write = new();
    private readonly ConcurrentDictionary<byte, BlockingCollection<Urb>> _queues = new();
    private readonly ConcurrentDictionary<uint, Urb> _live = new();
    private readonly Lock _isoLock = new();
    // Under _isoLock: call audio ended since the last SET_INTERFACE, and iso pipes whose open failure was already logged
    // for the current alternate setting.
    private bool _callAudioEnded;
    private readonly HashSet<byte> _isoOpenFailed = [];
    private readonly Dictionary<byte, IsoPipe> _isoPipes = [];
    // Under _isoLock, per alternate setting: whether Windows sent any call audio out, our silence packets so far, and
    // the SCO packet header being parsed from the incoming stream.
    private bool _windowsSendsOut;
    private long _silencePackets;
    private readonly byte[] _scoInHeader = new byte[3];
    private int _scoInHave;
    private int _scoInSkip;
    private int _silenceSequence;
    // mSBC frame of silence (57 bytes; ffmpeg sbc -msbc of 16 kHz zeros) and the H2 header's sequence bytes.
    private static readonly byte[] MsbcSilence =
    [
        0xAD, 0x00, 0x00, 0xC5, 0x00, 0x00, 0x00, 0x00, 0x77, 0x6D, 0xB6, 0xDD, 0xDB, 0x6D, 0xB7, 0x76, 0xDB, 0x6D, 0xDD,
        0xB6, 0xDB, 0x77, 0x6D, 0xB6, 0xDD, 0xDB, 0x6D, 0xB7, 0x76, 0xDB, 0x6D, 0xDD, 0xB6, 0xDB, 0x77, 0x6D, 0xB6, 0xDD,
        0xDB, 0x6D, 0xB7, 0x76, 0xDB, 0x6D, 0xDD, 0xB6, 0xDB, 0x77, 0x6D, 0xB6, 0xDD, 0xDB, 0x6D, 0xB7, 0x76, 0xDB, 0x6C,
    ];
    private static readonly byte[] H2Sequence = [0x08, 0x38, 0xC8, 0xF8];
    private readonly byte _eventPipe;
    private readonly byte _aclIn;
    private readonly byte _aclOut;
    private readonly List<Thread> _workers = [];
    private int _lost;

    public RadioLink(TcpClient client, WinUsbDevice device, HciProxy hci)
    {
        _client = client;
        _stream = client.GetStream();
        _device = device;
        _hci = hci;
        (_eventPipe, _aclIn, _aclOut) = FindHciPipes(device.ConfigDescriptor);
        Log.Info("radio", $"HCI pipes: events 0x{_eventPipe:X2}, ACL in 0x{_aclIn:X2}, ACL out 0x{_aclOut:X2}");
    }

    // When Windows first configured the virtual adapter (0 = not configured), and how long its driver's setup may take.
    private long _configuredAt;
    private const long SetupMs = 30_000;

    // MediaTek register requests were answered in place (logged once); control pipe worker only.
    private bool _uhwLogged;

    /// <summary>
    /// Windows' driver reset the virtual adapter, so the real one must be reset too. Raised on the control pipe worker
    /// before the request is answered: handlers must return at once.
    /// </summary>
    public event Action? ResetRequested;

    /// <summary>Set once a WinUSB transfer failed because the radio is gone.</summary>
    public bool DeviceLost => Volatile.Read(ref _lost) != 0;

    /// <summary>Serves Windows' requests on the caller's thread until the device is detached, lost or disposed.</summary>
    public void Run()
    {
        _hci.SetSender(SendInternal);
        _hci.SetCommandSender(SendOwnCommand);
        _hci.CallAudioEnded += OnCallAudioEnded;
        _hci.CallAudioStarted += OnCallAudioStarted;
        _hci.VoiceLinkChanged += OnVoiceLinkChanged;
        try
        {
            Loop();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or EndOfStreamException)
        {
            Log.Info("radio", $"USB/IP connection ended: {e.Message}");
        }
        finally
        {
            _hci.VoiceLinkChanged -= OnVoiceLinkChanged;
            _hci.CallAudioStarted -= OnCallAudioStarted;
            _hci.CallAudioEnded -= OnCallAudioEnded;
            _hci.SetCommandSender(null);
            _hci.SetSender(null);
            Stop();
        }
    }

    public void Dispose() => _client.Dispose();

    // The HCI interface is the first whose default setting has an interrupt IN (HCI events, the first one) and bulk IN
    // and OUT pipes (ACL data): interface 0, or 2 on Apple adapters with keyboard and mouse interfaces first.
    private static (byte Events, byte AclIn, byte AclOut) FindHciPipes(byte[] c)
    {
        byte events = 0;
        byte aclIn = 0;
        byte aclOut = 0;
        bool inDefault = false;
        for (int at = 0; at + 2 <= c.Length && c[at] > 0; at += c[at])
        {
            if (c[at + 1] == 4 && at + 4 <= c.Length)
            {
                if (events != 0 && aclIn != 0 && aclOut != 0)
                {
                    break;
                }
                inDefault = c[at + 3] == 0;
                events = aclIn = aclOut = 0;
            }
            else if (c[at + 1] == 5 && inDefault && at + 4 <= c.Length)
            {
                byte address = c[at + 2];
                int type = c[at + 3] & 3;
                if (type == 3 && (address & 0x80) != 0)
                {
                    events = events == 0 ? address : events;
                }
                else if (type == 2 && (address & 0x80) != 0)
                {
                    aclIn = address;
                }
                else if (type == 2)
                {
                    aclOut = address;
                }
            }
        }
        return events != 0 && aclIn != 0 && aclOut != 0 ? (events, aclIn, aclOut) : ((byte)0x81, (byte)0x82, (byte)0x02);
    }

    private void Loop()
    {
        var header = new byte[48];
        while (true)
        {
            _stream.ReadExactly(header);
            uint cmd = UsbIp.U32(header, 0);
            uint seq = UsbIp.U32(header, 4);
            uint dir = UsbIp.U32(header, 12);
            uint ep = UsbIp.U32(header, 16);
            if (cmd == UsbIp.CmdUnlink)
            {
                Unlink(seq, UsbIp.U32(header, 20));
                continue;
            }
            if (cmd != UsbIp.CmdSubmit)
            {
                throw new IOException($"unknown USB/IP command {cmd}");
            }
            uint length = UsbIp.U32(header, 24);
            uint packets = UsbIp.U32(header, 32);
            byte[] data = dir == 0 && length > 0 ? ReadN((int)length) : [];
            byte pipe = (byte)(ep | (dir == 1 ? 0x80u : 0));
            if (packets != UsbIp.NoIso && packets > 0)
            {
                var iu = new Urb { Seq = seq, Pipe = pipe, In = dir == 1, Length = length, Data = data, IsoDescriptors = ReadN((int)(16 * packets)), Packets = (int)packets, StartFrame = UsbIp.U32(header, 28) };
                SubmitIso(iu);
                continue;
            }
            Urb urb;
            if (ep == 0)
            {
                // Control: Data = 8-byte setup + OUT data.
                var withSetup = new byte[8 + data.Length];
                header.AsSpan(40, 8).CopyTo(withSetup);
                data.CopyTo(withSetup, 8);
                urb = new Urb { Seq = seq, Pipe = 0, In = dir == 1, Length = length, Data = withSetup };
                if (header[40] == 0x20)
                {
                    _hci.RewriteCommand(withSetup.AsSpan(8));
                    urb.Held = _hci.OnCommand(data);
                    Interlocked.Increment(ref _commandsArrived);
                }
            }
            else
            {
                urb = new Urb { Seq = seq, Pipe = pipe, In = dir == 1, Length = length, Data = data };
            }
            _live[seq] = urb;
            if (ep == 0 && header[40] == 0x01 && header[41] == 0x0B)
            {
                // SET_INTERFACE runs here, before the iso URBs Windows sends right after it: queued behind other control
                // transfers it would come too late, and the first call audio URBs would fail without bandwidth.
                Execute(0, urb);
                continue;
            }
            Queue(urb.Pipe).Add(urb);
        }
    }

    private byte[] ReadN(int n)
    {
        var b = new byte[n];
        _stream.ReadExactly(b);
        return b;
    }

    private void Send(byte[] frame)
    {
        lock (_write)
        {
            _stream.Write(frame);
        }
    }

    private void Unlink(uint seq, uint victim)
    {
        if (_live.TryGetValue(victim, out Urb? u))
        {
            if (u.IsoDescriptors is not null)
            {
                u.Cancelled = true;
                lock (_isoLock)
                {
                    if (_isoPipes.TryGetValue(u.Pipe, out IsoPipe? ip))
                    {
                        ip.Cancel(u);
                    }
                }
                Send(UsbIp.RetUnlinkFrame(seq, UsbIp.ECONNRESET));
                return;
            }
            lock (u)
            {
                if (u.Completed)
                {
                    // Its RET_SUBMIT already went out: nothing to unlink.
                    Send(UsbIp.RetUnlinkFrame(seq, 0));
                    return;
                }
                u.Cancelled = true;
                // Only while this transfer runs (Completed is set before the pipe's next one starts).
                if (u.Running && u.Pipe != 0 && _device.TryGetPipeInterface(u.Pipe, out nint iface))
                {
                    WinUsbDevice.Abort(iface, u.Pipe);
                }
                Send(UsbIp.RetUnlinkFrame(seq, UsbIp.ECONNRESET));
            }
        }
        else
        {
            Send(UsbIp.RetUnlinkFrame(seq, 0));
        }
    }

    // The service's own ACL packets join Windows' on the ACL OUT pipe, in order.
    private void SendInternal(Urb urb) => Queue(_aclOut).Add(urb);

    // The service's own HCI command (opcode, length, parameters) goes through the control pipe worker, between Windows'.
    private void SendOwnCommand(byte[] command)
    {
        var data = new byte[8 + command.Length];
        data[0] = 0x20;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), (ushort)command.Length);
        command.CopyTo(data, 8);
        Queue(0).Add(new Urb { Pipe = 0, Internal = true, Data = data, Length = (uint)command.Length });
    }

    // Windows' HCI commands that arrived, and those the control worker got to (sent or cancelled).
    private long _commandsArrived;
    private long _commandsRun;

    // Waits (up to 1 s) for Windows' command at the controller to be answered, then sends ours; its events never reach
    // Windows. One still queued behind ours is not waited for: Windows only sends it once its previous one was answered.
    private void ExecuteOwnCommand(Urb u)
    {
        for (int i = 0; ((_hci.UnansweredMs > 0 && Interlocked.Read(ref _commandsArrived) == Interlocked.Read(ref _commandsRun)) || _hci.OwnCommandPending) && i < 50; i++)
        {
            Thread.Sleep(20);
        }
        ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(u.Data.AsSpan(8));
        _hci.OwnCommandSent(opcode);
        int error = _device.Control(BinaryPrimitives.ReadUInt64LittleEndian(u.Data), u.Data[8..], out _);
        if (error != 0)
        {
            _hci.OwnCommandFailed(opcode);
            NoteError(error);
            Log.Warn("radio", $"own HCI command 0x{opcode:X4} not sent, Win32 error {error}");
        }
    }

    private BlockingCollection<Urb> Queue(byte pipe) => _queues.GetOrAdd(pipe, StartWorker);

    private BlockingCollection<Urb> StartWorker(byte pipe)
    {
        var q = new BlockingCollection<Urb>();
        var t = new Thread(() => Work(pipe, q)) { IsBackground = true, Name = $"pipe {pipe:X2}" };
        if (pipe == _eventPipe || pipe == _aclIn || pipe == _aclOut)
        {
            t.Priority = ThreadPriority.AboveNormal;
        }
        lock (_workers)
        {
            _workers.Add(t);
        }
        t.Start();
        return q;
    }

    private void Work(byte pipe, BlockingCollection<Urb> q)
    {
        var deferred = new Queue<Urb>();
        // Own packets deferred since the last log line: at most one line a minute.
        int deferrals = 0;
        long deferralsSince = Environment.TickCount64;
        try
        {
            while (!q.IsCompleted)
            {
                if (!q.TryTake(out Urb? u, deferred.Count > 0 ? 50 : Timeout.Infinite))
                {
                    FlushDeferred(deferred);
                    continue;
                }
                if (u.Internal && pipe == 0)
                {
                    ExecuteOwnCommand(u);
                    continue;
                }
                if (u.Internal)
                {
                    // Ours wait without blocking Windows' packets: while Windows is mid-PDU on the link, while the
                    // controller has no buffers, and behind earlier ones of ours.
                    if (deferred.Count > 0 || _hci.MustDefer((int)u.Seq) || !WriteInternal(u))
                    {
                        deferred.Enqueue(u);
                        deferrals++;
                        long elapsed = Environment.TickCount64 - deferralsSince;
                        if (elapsed >= 60_000)
                        {
                            Log.Info("acl", $"{deferrals} own ACL packets deferred in the last {elapsed / 1000} s (Windows mid-PDU or no controller buffers)");
                            deferrals = 0;
                            deferralsSince = Environment.TickCount64;
                        }
                    }
                    continue;
                }
                // Call audio setup waiting for the AirPods' mic to stop.
                while (u.Held && _hci.CallAudioHeld())
                {
                    Thread.Sleep(20);
                }
                bool command = pipe == 0 && u.Data.Length > 8 && u.Data[0] == 0x20;
                // One HCI command at a time: Windows' next one waits for our command's status.
                for (int i = 0; command && _hci.OwnCommandPending && i < 100; i++)
                {
                    Thread.Sleep(20);
                }
                if (command)
                {
                    Interlocked.Increment(ref _commandsRun);
                }
                if (u.Cancelled)
                {
                    _live.TryRemove(u.Seq, out _);
                    continue;
                }
                Execute(pipe, u);
                if (deferred.Count > 0)
                {
                    FlushDeferred(deferred);
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // The queue was completed or the connection closed: the link is stopping.
        }
    }

    private void FlushDeferred(Queue<Urb> deferred)
    {
        while (deferred.TryPeek(out Urb? u))
        {
            if (!_hci.LinkExists((int)u.Seq))
            {
                deferred.Dequeue();
                continue;
            }
            if (_hci.MustDefer((int)u.Seq) || !WriteInternal(u))
            {
                return;
            }
            deferred.Dequeue();
        }
    }

    // Writes one of our PDUs, all its fragments back to back. False when the controller has no buffers for it now
    // (nothing written; tried again later); true when written, or dropped on a write error.
    private bool WriteInternal(Urb u)
    {
        if (!_device.TryGetPipeInterface(_aclOut, out nint iface))
        {
            return true;
        }
        byte[][] fragments = u.Fragments!;
        if (!_hci.TryReserveOwn(fragments))
        {
            return false;
        }
        for (int i = 0; i < fragments.Length; i++)
        {
            byte[] fragment = fragments[i];
            int error = _device.Write(iface, _aclOut, fragment, out uint got);
            if (error != 0)
            {
                // This one and the rest were reserved but not sent.
                for (int k = fragments.Length - 1; k >= i; k--)
                {
                    _hci.UnreserveOut(fragments[k]);
                }
                NoteError(error);
                Log.Warn("radio", $"own ACL packet not sent, Win32 error {error}");
                return true;
            }
            if (fragment.Length >= 8 && BinaryPrimitives.ReadUInt16LittleEndian(fragment.AsSpan(6)) == 0x0001)
            {
                Log.Info("radio", $"own signaling packet written: {Convert.ToHexString(fragment)} ({got} bytes)");
            }
        }
        return true;
    }

    private void Execute(byte pipe, Urb u)
    {
        u.Running = true;
        int status = 0;
        uint got = 0;
        byte[] result = [];
        // IN data taken from the proxy's queues, and whether it ended a packet (to put it back if Windows cancels).
        bool taken = false;
        bool ended = false;
        if (pipe == 0)
        {
            (status, got, result) = Control(u);
            // Successful HCI commands are logged by the proxy.
            if (u.Data[0] != 0x20 || status != 0)
            {
                string data = u.In && result.Length > 0 ? " " + Convert.ToHexString(result.AsSpan(0, Math.Min(result.Length, 64))) : "";
                Log.Info("usb", $"control {Convert.ToHexString(u.Data.AsSpan(0, 8))}: status {status}, {(u.In ? result.Length : got)} bytes{data}");
            }
        }
        else if (!_device.TryGetPipeInterface(pipe, out nint iface))
        {
            status = UsbIp.EPIPE;
        }
        else if (u.In)
        {
            (status, got, result, taken, ended) = ReadIn(pipe, iface, u);
        }
        else
        {
            bool acl = pipe == _aclOut;
            if (acl && _hci.FilterWindowsAcl(u.Data))
            {
                // Held A2DP media or an AVDTP command answered by the proxy: completed as if sent.
                got = (uint)u.Data.Length;
            }
            else if (acl && !_hci.ReserveOut(u.Data, () => u.Cancelled))
            {
                u.Running = false;
                _live.TryRemove(u.Seq, out _);
                return;
            }
            else
            {
                int error = _device.Write(iface, pipe, u.Data, out got);
                if (error != 0)
                {
                    if (acl)
                    {
                        _hci.UnreserveOut(u.Data);
                    }
                    NoteError(error);
                    status = UsbIp.Errno(error);
                }
            }
        }
        u.Running = false;
        // Completion and Unlink exclude each other: a transfer cancelled first sends nothing and its bytes go to the next
        // one; a completed one is answered before any RET_UNLINK for it.
        lock (u)
        {
            if (u.Cancelled)
            {
                if (taken)
                {
                    _hci.PutBack(pipe == _eventPipe, result, ended);
                }
                _live.TryRemove(u.Seq, out _);
                return; // RET_UNLINK already sent, no RET_SUBMIT
            }
            u.Completed = true;
            // actual_length of an OUT transfer stays as the device reported it; only IN data may have been filtered.
            Send(UsbIp.Ret(u.Seq, status, u.In ? (uint)result.Length : got, u.In ? result : []));
            _live.TryRemove(u.Seq, out _);
        }
    }

    // Events and ACL data go through the proxy: Windows gets what it queued, and while nothing is queued (only our own
    // traffic arrived) the device is read again, so Windows' request stays pending.
    private (int Status, uint Got, byte[] Data, bool Taken, bool Ended) ReadIn(byte pipe, nint iface, Urb u)
    {
        var buffer = new byte[u.Length];
        bool events = pipe == _eventPipe;
        bool hci = events || pipe == _aclIn;
        var read = hci ? new byte[u.Length] : buffer;
        while (true)
        {
            if (hci)
            {
                // A cancelled transfer takes nothing: what is queued stays for Windows' next one.
                if (u.Cancelled)
                {
                    return (UsbIp.ECONNRESET, 0, [], false, false);
                }
                int queued = _hci.Take(events, buffer, out bool ended);
                if (queued > 0)
                {
                    return (0, (uint)queued, buffer[..queued], true, ended);
                }
            }
            int error = _device.Read(iface, pipe, read, out uint got);
            if (error != 0)
            {
                NoteError(error);
                return (UsbIp.Errno(error), 0, [], false, false);
            }
            if (!hci)
            {
                return (0, got, buffer[..(int)got], false, false);
            }
            if (events)
            {
                // A transfer shorter than the read ends with a short packet: the controller's event ends there.
                _hci.FeedEvents(read.AsSpan(0, (int)got), complete: got < read.Length);
            }
            else
            {
                _hci.FeedAcl(read.AsSpan(0, (int)got));
            }
        }
    }

    private void NoteError(int error)
    {
        if (WinUsbDevice.IsDeviceLost(error) && Interlocked.Exchange(ref _lost, 1) == 0)
        {
            Log.Warn("radio", $"radio lost (Win32 error {error})");
            _client.Dispose();
        }
    }

    // Standard requests that WinUSB owns are emulated; the rest go to the device.
    private (int Status, uint Got, byte[] Data) Control(Urb u)
    {
        ReadOnlySpan<byte> sp = u.Data.AsSpan(0, 8);
        byte rt = sp[0];
        byte req = sp[1];
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(sp[2..]);
        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(sp[4..]);
        if (rt == 0x00 && req == 0x09)
        {
            // SET_CONFIGURATION: WinUSB already configured the device. Drivers may repeat it during setup (Intel's ~0.1 s
            // in); a later one means the driver reset the adapter, which usbip-win2 never passes on, so the real chip is
            // reset too. 0 (driver unloading) starts over. Runs on the control pipe worker only.
            long now = Environment.TickCount64;
            bool again = false;
            if (value == 0)
            {
                _configuredAt = 0;
            }
            else if (_configuredAt == 0)
            {
                _configuredAt = now;
            }
            else
            {
                again = now - _configuredAt > SetupMs;
            }
            Log.Info("radio", $"SET_CONFIGURATION {value}{(again ? ": again, the driver reset the adapter" : "")}");
            if (again)
            {
                ResetRequested?.Invoke();
            }
            return (0, 0, []);
        }
        if (rt == 0x01 && req == 0x0B)
        {
            int ifn = index & 0xFF;
            if (ifn < _device.Interfaces.Count)
            {
                CloseIso(_device.Interfaces[ifn]);
            }
            lock (_isoLock)
            {
                // A new alternate setting: call audio may flow again, and open failures are worth one warning again.
                _callAudioEnded = false;
                _isoOpenFailed.Clear();
                _windowsSendsOut = false;
                _silencePackets = 0;
                _scoInHave = 0;
                _scoInSkip = 0;
            }
            int error = _device.SetAlternate(ifn, (byte)value);
            Log.Info("radio", $"SET_INTERFACE {ifn} alt {value}: {(error == 0 ? "ok" : $"Win32 error {error}")}");
            return (error == 0 ? 0 : UsbIp.EPIPE, 0, []);
        }
        if (rt == 0x02 && req == 0x01 && value == 0)
        {
            // CLEAR_FEATURE(ENDPOINT_HALT)
            if (_device.TryGetPipeInterface((byte)index, out nint iface))
            {
                WinUsbDevice.Reset(iface, (byte)index);
            }
            return (0, 0, []);
        }
        if (rt == 0x80 && req == 0x06 && ((value >> 8) == 1 || ((value >> 8) == 2 && _device.IsFunction)
            || value == (0x0300 | WinUsbDevice.SerialIndex)))
        {
            // Device descriptor (with our serial index), a combined device's function configuration, our serial string.
            byte[] d = (value >> 8) switch
            {
                1 => _device.DeviceDescriptor,
                2 => _device.ConfigDescriptor,
                _ => UsbIp.StringDescriptor(_device.Serial),
            };
            byte[] part = d[..Math.Min(d.Length, (int)u.Length)];
            return (0, (uint)part.Length, part);
        }
        ulong setup = BinaryPrimitives.ReadUInt64LittleEndian(sp);
        byte[] buffer = u.In ? new byte[u.Length] : u.Data[8..];
        if (u.Length == 4 && rt == (u.In ? 0xC2 : 0x42) && req == (u.In ? 0x01 : 0x02)
            && !_device.TryGetPipeInterface((byte)index, out _))
        {
            // MediaTek register read/write over the USB hardware path (address in wValue:wIndex), sent to an endpoint
            // that does not exist, which WinUSB refuses for most addresses: writes are dropped, reads use the plain
            // register read of the same address.
            if (!_uhwLogged)
            {
                _uhwLogged = true;
                Log.Info("radio", "MediaTek register requests: writes are dropped, reads use 0x63");
            }
            if (!u.In)
            {
                return (0, u.Length, []);
            }
            setup = (setup & ~0xFFFFUL) | 0x63C0;
        }
        int e = _device.Control(setup, buffer, out uint got);
        if (e != 0 && rt == 0x80 && req == 0x06 && value == 0x0300)
        {
            // An adapter without any strings has no language list either; ours (English) makes the serial readable.
            byte[] part = UsbIp.EnglishLanguages[..Math.Min(4, (int)u.Length)];
            return (0, (uint)part.Length, part);
        }
        if (e != 0)
        {
            NoteError(e);
            Log.Warn("radio", $"control rt=0x{rt:X2} req=0x{req:X2} v=0x{value:X4} i=0x{index:X4} failed, Win32 error {e}");
            return (UsbIp.Errno(e), 0, []);
        }
        // GET_DESCRIPTOR(CONFIGURATION): clear bmAttributes bit 5 (remote wakeup). USB/IP cannot carry a wake signal,
        // so a device advertised as wake-capable gets selectively suspended and never resumes.
        if (u.In && rt == 0x80 && req == 0x06 && (value >> 8) == 2 && got >= 8)
        {
            buffer[7] &= unchecked((byte)~0x20);
        }
        return (0, got, u.In ? buffer[..(int)got] : []);
    }

    // Under _isoLock throughout, so OnCallAudioEnded and SET_INTERFACE never close a pipe while a URB goes into it.
    private void SubmitIso(Urb u)
    {
        _live[u.Seq] = u;
        lock (_isoLock)
        {
            if (!u.In && _callAudioEnded)
            {
                // No call audio link any more: what Windows still sends until it switches the interface goes nowhere
                // (the controller can fail on call audio data for a link it closed).
                RetIso(u, 0, IsoPipe.OutResult(u));
                return;
            }
            if (OpenIsoPipeLocked(u.Pipe) is not { } ip)
            {
                RetIso(u, UsbIp.EPIPE, null);
                return;
            }
            if (!u.In && !_windowsSendsOut)
            {
                _windowsSendsOut = true;
                Log.Info("radio", $"call audio: Windows sends outgoing audio{(_silencePackets > 0 ? $" (after {_silencePackets} silence packets of ours)" : "")}");
            }
            ip.Submit(u);
        }
    }

    // The open pipe, else opened now; null when the interface's current setting has none or no bandwidth for it.
    private IsoPipe? OpenIsoPipeLocked(byte pipe)
    {
        if (_isoPipes.TryGetValue(pipe, out IsoPipe? ip) || !_device.TryGetPipeInterface(pipe, out nint iface))
        {
            return ip;
        }
        try
        {
            ip = new IsoPipe(_device, iface, pipe, RetIso);
            _isoPipes[pipe] = ip;
            return ip;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Once per pipe and alternate setting: Windows keeps submitting until it selects one with bandwidth.
            if (_isoOpenFailed.Add(pipe))
            {
                Log.Warn("radio", $"iso pipe 0x{pipe:X2} open failed: {e.Message}");
            }
            return null;
        }
    }

    // While a call audio link is up and Windows sends nothing out (only recording, nothing playing), the AirPods hang up
    // after ~3 s. Each SCO packet that comes in is answered with a silent one of the same size until Windows sends.
    private void FeedSilence(Urb inUrb, IsoResult result)
    {
        if (_windowsSendsOut || !_isoLock.TryEnter())
        {
            return;
        }
        try
        {
            if (_windowsSendsOut || _callAudioEnded)
            {
                return;
            }
            var silence = new List<byte>();
            int count = 0;
            for (int i = 0; i < inUrb.Packets; i++)
            {
                if (result.Status[i] != 0)
                {
                    continue;
                }
                for (int b = result.Offsets[i]; b < result.Offsets[i] + result.Actual[i]; b++)
                {
                    if (_scoInSkip > 0)
                    {
                        _scoInSkip--;
                        continue;
                    }
                    _scoInHeader[_scoInHave++] = result.Data[b];
                    if (_scoInHave < 3)
                    {
                        continue;
                    }
                    _scoInHave = 0;
                    int length = _scoInHeader[2];
                    _scoInSkip = length;
                    // Same handle (flags cleared) and length as the packet that came in.
                    silence.Add(_scoInHeader[0]);
                    silence.Add((byte)(_scoInHeader[1] & 0x0F));
                    silence.Add((byte)length);
                    silence.AddRange(SilencePayload(length, result.Data.AsSpan(b + 1)));
                    count++;
                }
            }
            if (count == 0)
            {
                return;
            }
            byte outPipe = (byte)(inUrb.Pipe & 0x7F);
            if (OpenIsoPipeLocked(outPipe) is not { } ip)
            {
                return;
            }
            if (_silencePackets == 0)
            {
                Log.Info("radio", "call audio: Windows sends nothing out, sending silence until it does");
            }
            _silencePackets += count;
            ip.Submit(new Urb { Pipe = outPipe, In = false, Internal = true, Data = [.. silence], Packets = count });
        }
        finally
        {
            _isoLock.Exit();
        }
    }

    // Silence for one SCO payload: an mSBC frame of silence when the call carries mSBC (the AirPods' packets start with
    // the H2 header), else zero PCM.
    private byte[] SilencePayload(int length, ReadOnlySpan<byte> incoming)
    {
        var payload = new byte[length];
        bool msbc = length >= 2 + MsbcSilence.Length && incoming.Length >= 3 && incoming[0] == 0x01 && (incoming[1] & 0x0F) == 0x08 && incoming[2] == 0xAD;
        if (msbc)
        {
            payload[0] = 0x01;
            payload[1] = H2Sequence[_silenceSequence];
            _silenceSequence = (_silenceSequence + 1) % H2Sequence.Length;
            MsbcSilence.CopyTo(payload, 2);
        }
        return payload;
    }

    private void RetIso(Urb u, int status, IsoResult? result)
    {
        if (u.Internal)
        {
            if (u.In)
            {
                OnVoiceIso(u, status, result);
            }
            return;
        }
        _live.TryRemove(u.Seq, out _);
        if (u.Cancelled)
        {
            return;
        }
        try
        {
            Send(UsbIp.RetIso(u.Seq, status, u.StartFrame, u.IsoDescriptors!, u.Packets, u.In, result));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The connection is ending; the URB loop reports it.
        }
        if (u.In && status == 0 && result is { } r)
        {
            FeedSilence(u, r);
        }
    }

    // Stops outgoing call audio at once and drops what was queued, as Linux btusb does when no SCO link is left.
    // The work runs off the proxy's lock.
    private void OnCallAudioEnded()
    {
        lock (_isoLock)
        {
            _callAudioEnded = true;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_isoLock)
            {
                foreach (byte pipe in _isoPipes.Keys.ToList())
                {
                    if ((pipe & 0x80) == 0)
                    {
                        _isoPipes[pipe].Close();
                        _isoPipes.Remove(pipe);
                    }
                }
            }
            Log.Info("radio", "call audio ended: outgoing call audio stopped until Windows switches the interface");
        });
    }

    // A call audio link came up: its outgoing audio flows again even if Windows keeps the interface's setting.
    private void OnCallAudioStarted()
    {
        lock (_isoLock)
        {
            _callAudioEnded = false;
        }
    }

    // HD voice: our own voice link streams on the call audio interface while Windows has none. Each SCO packet in is
    // answered with one out, keeping the controller's rate and timing; 2-frame IN URBs space sparse transparent packets
    // evenly, while CVSD's PCM fills every frame in one continuous OUT stream.
    private const int TransparentUrbs = 8;
    private const int TransparentFrames = 2;
    private const int CvsdUrbs = 8;
    private const int CvsdFrames = 8;
    // CVSD's SCO packets carry 48 bytes of PCM (3 frames with the header); the OUT stream starts that many ahead.
    private const byte CvsdPacketBytes = 48;
    private const int CvsdLeadPackets = 4;
    // Failed IN URBs in a row after which the voice stream gives up.
    private const int MaxVoiceFailures = 20;
    private VoiceLinkInfo? _voice;
    // The proxy's latest link (any thread); the session ended (under _isoLock); the interface our link streams on.
    private VoiceLinkInfo? _voiceWanted;
    private bool _voiceClosed;
    private int _voiceInterface = 1;
    private IsoPipe? _voiceIn;
    private IsoPipe? _voiceOut;
    // Iso completion thread of _voiceIn only: the SCO packet being parsed, failed IN URBs in a row.
    private readonly byte[] _voiceHeader = new byte[3];
    private int _voiceHeaderHave;
    private readonly byte[] _voicePacket = new byte[255];
    private int _voicePacketHave;
    private int _voiceRemaining;
    private int _voiceFailures;
    // Per voice link: SCO packets by packet status flag (0 good, 1 possibly invalid, 2 no data, 3 partly lost) and bytes.
    private readonly long[] _voiceStatus = new long[4];
    private long _voiceBytes;

    // Pool items may run out of order: each applies the latest wanted link, so a late start never revives one that ended.
    private void OnVoiceLinkChanged(VoiceLinkInfo? info)
    {
        Volatile.Write(ref _voiceWanted, info);
        ThreadPool.QueueUserWorkItem(static s => ((RadioLink)s!).ApplyVoiceLink(), this);
    }

    private void ApplyVoiceLink()
    {
        VoiceLinkInfo? info;
        IsoPipe? input;
        IsoPipe? output;
        lock (_isoLock)
        {
            info = Volatile.Read(ref _voiceWanted);
            if (_voiceClosed || ReferenceEquals(info, _voice))
            {
                return;
            }
            (input, output) = (_voiceIn, _voiceOut);
            _voice = null;
            _voiceIn = null;
            _voiceOut = null;
        }
        // Closed outside the lock: their completer may wait for the proxy's lock, whose holder may wait for this one.
        input?.Close();
        output?.Close();
        lock (_isoLock)
        {
            if (_voiceClosed || _voice is not null)
            {
                return;
            }
            if (info is null || !ReferenceEquals(Volatile.Read(ref _voiceWanted), info))
            {
                _device.SetAlternate(_voiceInterface, 0);
                Log.Info("radio", $"voice link stopped: interface {_voiceInterface} alt 0; {_voiceBytes} bytes in, SCO packets by status {string.Join("/", _voiceStatus)}");
                return;
            }
            StartVoiceLocked(info);
        }
    }

    private void StartVoiceLocked(VoiceLinkInfo info)
    {
        int index = CallAudioInterface();
        if (index >= _device.Interfaces.Count)
        {
            Log.Warn("radio", "voice link: the adapter has no call audio interface");
            return;
        }
        _voiceInterface = index;
        nint iface = _device.Interfaces[index];
        CloseIso(iface);
        // Transparent data as Windows streams mSBC (alt 6, else 1); 16-bit PCM for one CVSD link: alt 2.
        byte alt = info.Transparent ? (byte)6 : (byte)2;
        int error = _device.SetAlternate(index, alt);
        // Right after a previous voice link WinUSB may still report the interface busy for a moment.
        for (int i = 0; error == 170 && i < 10; i++)
        {
            Thread.Sleep(200);
            error = _device.SetAlternate(index, alt);
        }
        if (error != 0 && info.Transparent)
        {
            alt = 1;
            error = _device.SetAlternate(index, alt);
        }
        (byte inPipe, byte outPipe) = error == 0 ? _device.IsoPipes(index) : ((byte)0, (byte)0);
        if (inPipe == 0 || outPipe == 0)
        {
            Log.Warn("radio", $"voice link: interface {index} alt {alt} not usable (Win32 error {error})");
            if (error == 0)
            {
                _device.SetAlternate(index, 0);
            }
            return;
        }
        try
        {
            _voiceIn = new IsoPipe(_device, iface, inPipe, RetIso);
            _voiceOut = new IsoPipe(_device, iface, outPipe, RetIso, continuousOut: !info.Transparent);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("radio", $"voice link: iso pipes not opened: {e.Message}");
            StopVoiceLocked();
            _device.SetAlternate(index, 0);
            return;
        }
        _voice = info;
        DropVoicePacket();
        _voiceFailures = 0;
        Array.Clear(_voiceStatus);
        _voiceBytes = 0;
        Log.Info("radio", $"voice link 0x{info.Handle:X3}: interface {index} alt {alt}, pipes 0x{inPipe:X2}/0x{outPipe:X2}");
        for (int i = 0; i < (info.Transparent ? TransparentUrbs : CvsdUrbs); i++)
        {
            _voiceIn.Submit(new Urb { Pipe = inPipe, In = true, Internal = true, Packets = info.Transparent ? TransparentFrames : CvsdFrames });
        }
        if (!info.Transparent)
        {
            // CVSD's PCM fills every frame: the OUT stream starts CvsdLeadPackets of silence ahead, so each completion's
            // packets join it before it runs dry.
            byte[] lead = new byte[CvsdLeadPackets * (3 + CvsdPacketBytes)];
            for (int at = 0; at < lead.Length; at += 3 + CvsdPacketBytes)
            {
                lead[at] = (byte)info.Handle;
                lead[at + 1] = (byte)(info.Handle >> 8);
                lead[at + 2] = CvsdPacketBytes;
            }
            _voiceOut.Submit(new Urb { Pipe = outPipe, In = false, Internal = true, Data = lead, Packets = CvsdLeadPackets });
        }
    }

    // Call audio is the interface after the HCI one: 1, or 3 on Apple adapters with keyboard and mouse first.
    private int CallAudioInterface()
    {
        if (_device.TryGetPipeInterface(_aclIn, out nint hci))
        {
            for (int i = 0; i < _device.Interfaces.Count; i++)
            {
                if (_device.Interfaces[i] == hci)
                {
                    return i + 1;
                }
            }
        }
        return 1;
    }

    private void StopVoiceLocked()
    {
        _voice = null;
        _voiceIn?.Close();
        _voiceOut?.Close();
        _voiceIn = null;
        _voiceOut = null;
    }

    // Lost data: the SCO packet being parsed is dropped and the next one starts anew.
    private void DropVoicePacket()
    {
        _voiceHeaderHave = 0;
        _voicePacketHave = 0;
        _voiceRemaining = 0;
    }

    // Our IN URB completed: its SCO payloads go to the proxy, one OUT packet per SCO packet in goes out, and the URB is
    // resubmitted (also after a failure, unless they keep failing).
    private void OnVoiceIso(Urb u, int status, IsoResult? result)
    {
        VoiceLinkInfo? voice = Volatile.Read(ref _voice);
        if (voice is null || status == UsbIp.ECONNRESET)
        {
            return;
        }
        var uplink = new List<byte>();
        var downlink = new List<byte>();
        int count = 0;
        if (status == 0 && result is { } r)
        {
            _voiceFailures = 0;
            for (int i = 0; i < u.Packets; i++)
            {
                if (r.Status[i] != 0)
                {
                    DropVoicePacket();
                    continue;
                }
                for (int b = r.Offsets[i]; b < r.Offsets[i] + r.Actual[i]; b++)
                {
                    if (_voiceRemaining > 0)
                    {
                        _voicePacket[_voicePacketHave++] = r.Data[b];
                        if (--_voiceRemaining == 0)
                        {
                            uplink.AddRange(_voicePacket.AsSpan(0, _voicePacketHave));
                            _voicePacketHave = 0;
                        }
                        continue;
                    }
                    _voiceHeader[_voiceHeaderHave++] = r.Data[b];
                    if (_voiceHeaderHave < 3)
                    {
                        continue;
                    }
                    _voiceHeaderHave = 0;
                    if ((_voiceHeader[0] | ((_voiceHeader[1] & 0x0F) << 8)) != voice.Handle)
                    {
                        // Out of step after lost data: SCO packets start on a frame, so the rest of this one is skipped.
                        break;
                    }
                    _voiceRemaining = _voiceHeader[2];
                    _voiceStatus[(_voiceHeader[1] >> 4) & 3]++;
                    _voiceBytes += _voiceRemaining;
                    // Transparent data goes out in the link's packets; 16-bit PCM (CVSD) in the controller's own size.
                    byte[] payload = _hci.TakeVoiceOut(voice.Transparent ? voice.TxLength : _voiceRemaining);
                    downlink.Add((byte)voice.Handle);
                    downlink.Add((byte)(voice.Handle >> 8));
                    downlink.Add((byte)payload.Length);
                    downlink.AddRange(payload);
                    count++;
                }
            }
        }
        else
        {
            DropVoicePacket();
            if (++_voiceFailures >= MaxVoiceFailures)
            {
                if (_voiceFailures == MaxVoiceFailures)
                {
                    Log.Warn("radio", $"voice link: {MaxVoiceFailures} IN transfers failed in a row, the voice stream stops");
                }
                return;
            }
        }
        if (uplink.Count > 0)
        {
            _hci.OnVoiceIn(voice.Handle, [.. uplink]);
        }
        // StopVoice holds the lock while it closes the pipes and waits for this thread: give up once the link is gone.
        while (!_isoLock.TryEnter(5))
        {
            if (Volatile.Read(ref _voice) != voice)
            {
                return;
            }
        }
        try
        {
            if (_voice != voice || _voiceIn is not { } inPipe)
            {
                return;
            }
            if (count > 0 && _voiceOut is { } outPipe)
            {
                outPipe.Submit(new Urb { Pipe = (byte)(u.Pipe & 0x7F), In = false, Internal = true, Data = [.. downlink], Packets = count });
            }
            inPipe.Submit(new Urb { Pipe = u.Pipe, In = true, Internal = true, Packets = u.Packets });
        }
        finally
        {
            _isoLock.Exit();
        }
    }

    // Iso pipes of one interface close before its alternate setting changes (their buffers belong to the old setting).
    private void CloseIso(nint iface)
    {
        lock (_isoLock)
        {
            foreach (byte pipe in _isoPipes.Keys.ToList())
            {
                if (_isoPipes[pipe].Interface == iface)
                {
                    _isoPipes[pipe].Close();
                    _isoPipes.Remove(pipe);
                }
            }
        }
    }

    private void Stop()
    {
        _client.Dispose();
        lock (_isoLock)
        {
            _voiceClosed = true;
            StopVoiceLocked();
            foreach (IsoPipe ip in _isoPipes.Values)
            {
                ip.Close();
            }
            _isoPipes.Clear();
        }
        // Give back the isochronous (SCO) bandwidth a call reserved: alternate setting 0, as Linux btusb does.
        for (int i = 1; i < _device.Interfaces.Count; i++)
        {
            _device.SetAlternate(i, 0);
        }
        foreach (BlockingCollection<Urb> q in _queues.Values)
        {
            q.CompleteAdding();
        }
        // Blocked reads end once their pipes are aborted.
        foreach (byte pipe in _queues.Keys)
        {
            if (pipe != 0 && _device.TryGetPipeInterface(pipe, out nint iface))
            {
                WinUsbDevice.Abort(iface, pipe);
            }
        }
        lock (_workers)
        {
            foreach (Thread t in _workers)
            {
                t.Join(2000);
            }
        }
    }
}
