using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WPods.Service;

/// <summary>USB/IP wire format (big-endian, 48-byte URB headers) shared by the radio and the mic.</summary>
internal static class UsbIp
{
    public const uint CmdSubmit = 1;
    public const uint CmdUnlink = 2;
    public const uint RetSubmit = 3;
    public const uint RetUnlink = 4;
    public const uint NoIso = 0xFFFFFFFF;
    public const int ECONNRESET = -104;
    public const int EPIPE = -32;
    public const int EPROTO = -71;
    public const int EOVERFLOW = -75;
    public const int EXDEV = -18;
    public const int ETIMEDOUT = -110;

    public static uint U32(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt32BigEndian(b[offset..]);

    public static void P32(Span<byte> b, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(b[offset..], value);

    /// <summary>USB string descriptor 0: the supported languages, English (US) only.</summary>
    public static readonly byte[] EnglishLanguages = [4, 0x03, 0x09, 0x04];

    /// <summary>A USB string descriptor (UTF-16LE).</summary>
    public static byte[] StringDescriptor(string s)
    {
        byte[] u = Encoding.Unicode.GetBytes(s);
        var d = new byte[2 + u.Length];
        d[0] = (byte)d.Length;
        d[1] = 0x03;
        u.CopyTo(d, 2);
        return d;
    }

    /// <summary>Win32 error of a WinUSB transfer -> Linux errno for RET_SUBMIT.</summary>
    public static int Errno(int win32) => win32 switch
    {
        995 => ECONNRESET,
        31 => EPIPE,
        121 => ETIMEDOUT,
        _ => EPROTO,
    };

    /// <summary>usbip_usb_device (312 bytes) for an import reply.</summary>
    public static byte[] DeviceInfo(string busId, byte[] device, byte[] config, uint speed)
    {
        var b = new byte[312];
        Encoding.ASCII.GetBytes("/sys/devices/platform/wpods/" + busId).CopyTo(b, 0);
        Encoding.ASCII.GetBytes(busId).CopyTo(b, 256);
        P32(b, 288, 1);
        P32(b, 292, 1);
        P32(b, 296, speed);
        b[300] = device[9];
        b[301] = device[8];
        b[302] = device[11];
        b[303] = device[10];
        b[304] = device[13];
        b[305] = device[12];
        b[306] = device[4];
        b[307] = device[5];
        b[308] = device[6];
        b[309] = config[5];
        b[310] = device[17];
        b[311] = config[4];
        return b;
    }

    /// <summary>RET_SUBMIT for a non-isochronous URB.</summary>
    public static byte[] Ret(uint seq, int status, uint actual, ReadOnlySpan<byte> data)
    {
        var b = new byte[48 + data.Length];
        P32(b, 0, RetSubmit);
        P32(b, 4, seq);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), status);
        P32(b, 24, actual);
        P32(b, 32, NoIso);
        data.CopyTo(b.AsSpan(48));
        return b;
    }

    public static byte[] RetUnlinkFrame(uint seq, int status)
    {
        var b = new byte[48];
        P32(b, 0, RetUnlink);
        P32(b, 4, seq);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), status);
        return b;
    }

    /// <summary>
    /// RET_SUBMIT for an isochronous URB as usbip-win2 expects it: IN data packed back to back, descriptors keep the
    /// CMD_SUBMIT offsets and lengths, error_count = packets with nonzero status (all of them fails the whole URB).
    /// </summary>
    public static byte[] RetIso(uint seq, int status, uint startFrame, byte[] cmdDescriptors, int packets, bool isIn, IsoResult? result)
    {
        using var payload = new MemoryStream();
        var descriptors = new byte[16 * packets];
        uint total = 0;
        uint errors = 0;
        for (int i = 0; i < packets; i++)
        {
            uint offset = U32(cmdDescriptors, 16 * i);
            uint length = U32(cmdDescriptors, (16 * i) + 4);
            uint actual = 0;
            int packetStatus = EXDEV;
            if (result is { } r)
            {
                actual = (uint)Math.Clamp(r.Actual[i], 0, (int)length);
                packetStatus = r.Status[i] == 0 ? 0 : EXDEV;
                if (isIn && actual > 0)
                {
                    payload.Write(r.Data, r.Offsets[i], (int)actual);
                }
            }
            if (packetStatus != 0)
            {
                errors++;
            }
            total += actual;
            P32(descriptors, 16 * i, offset);
            P32(descriptors, (16 * i) + 4, length);
            P32(descriptors, (16 * i) + 8, actual);
            BinaryPrimitives.WriteInt32BigEndian(descriptors.AsSpan((16 * i) + 12), packetStatus);
        }
        int dataLength = (int)payload.Length;
        var b = new byte[48 + dataLength + descriptors.Length];
        P32(b, 0, RetSubmit);
        P32(b, 4, seq);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), status);
        P32(b, 24, total);
        P32(b, 28, startFrame);
        P32(b, 32, (uint)packets);
        P32(b, 36, errors);
        payload.GetBuffer().AsSpan(0, dataLength).CopyTo(b.AsSpan(48));
        descriptors.CopyTo(b, 48 + dataLength);
        return b;
    }
}

/// <summary>
/// USB/IP server on 127.0.0.1 (random port) for usbip-win2's virtual host controller. An import is accepted only while
/// the service itself attaches that bus id; every other connection is refused.
/// </summary>
internal sealed class UsbIpServer : IDisposable
{
    private static readonly string UsbipExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip", "usbip.exe");
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Armed> _armed = [];
    private const int SystemProcess = 4;

    // An attach in progress; UsbipProcess is 0 until usbip.exe started. Fields under _lock.
    private sealed class Armed(byte[] info, TaskCompletionSource<TcpClient> done)
    {
        public byte[] Info { get; } = info;
        public TaskCompletionSource<TcpClient> Done { get; } = done;
        public int UsbipProcess;
    }

    // The process that owns the other end of a loopback connection (-1 when not found).
    private unsafe int PeerProcess(TcpClient client)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint remote)
        {
            return -1;
        }
        const int AfInet = 2, TcpTableOwnerPidConnections = 4;
        uint size = 0;
        _ = GetExtendedTcpTable(null, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
        byte[] table = new byte[size + 1024];
        size = (uint)table.Length;
        fixed (byte* t = table)
        {
            if (GetExtendedTcpTable(t, ref size, false, AfInet, TcpTableOwnerPidConnections, 0) != 0)
            {
                return -1;
            }
            // MIB_TCPTABLE_OWNER_PID: count, then rows of state, local addr, local port, remote addr, remote port, pid
            // (ports in network byte order in the low 16 bits).
            uint count = *(uint*)t;
            uint* row = (uint*)(t + 4);
            for (uint i = 0; i < count; i++, row += 6)
            {
                if (NetPort(row[2]) == remote.Port && NetPort(row[4]) == Port)
                {
                    return (int)row[5];
                }
            }
        }
        return -1;

        static int NetPort(uint value) => (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));
    }

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    private static extern unsafe uint GetExtendedTcpTable(byte* table, ref uint size, bool sort, int family, int tableClass, uint reserved);
    private readonly CancellationTokenSource _stop = new();

    public UsbIpServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Log.Info("usbip", $"listening on 127.0.0.1:{Port}");
        _ = AcceptLoopAsync();
    }

    public int Port { get; }

    public static bool ClientInstalled => File.Exists(UsbipExe);

    /// <summary>
    /// Unplugs the virtual device on usbip-win2's <paramref name="port"/>; closing the connection alone makes usbip-win2
    /// keep reconnecting it.
    /// </summary>
    public static void Detach(int port) => Run($"detach port {port}", "detach", "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Cancels usbip-win2's reconnect attempts for <paramref name="busId"/>: after an attach it retries lost connections
    /// on its own (from 30 s, up to 20 times; `--once` covers only the first attach), even for a device already gone.
    /// </summary>
    public void StopReconnect(string busId) =>
        Run($"stop reconnecting {busId}", "-t", Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "attach", "-r", "127.0.0.1", "-b", busId, "--stop");

    private static void Run(string what, params string[] args)
    {
        var psi = new ProcessStartInfo(UsbipExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using Process? p = Process.Start(psi);
        if (p is null)
        {
            return;
        }
        Task<string> output = p.StandardOutput.ReadToEndAsync();
        Task<string> errors = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15000))
        {
            p.Kill();
        }
        Log.Info("usbip", $"{what}: exit {(p.HasExited ? p.ExitCode : -1)} {(output.Result + errors.Result).Trim()}");
    }

    /// <summary>
    /// Runs usbip-win2's attach for <paramref name="busId"/>; returns the connection and usbip-win2 port once the import
    /// was answered with <paramref name="deviceInfo"/>. Throws on failure or after 30 s.
    /// </summary>
    public async Task<(TcpClient Client, int Port)> AttachAsync(string busId, byte[] deviceInfo, CancellationToken cancel)
    {
        var done = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = new Armed(deviceInfo, done);
        lock (_lock)
        {
            _armed[busId] = armed;
        }
        try
        {
            var psi = new ProcessStartInfo(UsbipExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in (string[])["-t", Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "attach", "-r", "127.0.0.1", "-b", busId, "--once"])
            {
                psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("usbip.exe did not start");
            lock (_lock)
            {
                armed.UsbipProcess = p.Id;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            Task<string> output = p.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errors = p.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                p.Kill();
                throw;
            }
            string text = ((await output.ConfigureAwait(false)) + (await errors.ConfigureAwait(false))).Trim();
            Log.Info("usbip", $"attach {busId}: exit {p.ExitCode} {text}");
            if (p.ExitCode != 0)
            {
                if (done.Task.IsCompletedSuccessfully)
                {
                    done.Task.Result.Dispose();
                }
                throw new InvalidOperationException($"usbip attach {busId} failed ({p.ExitCode}): {text}");
            }
            TcpClient client = await done.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            // "successfully attached to port N"
            int at = text.LastIndexOf("port ", StringComparison.Ordinal);
            int port = at >= 0 && int.TryParse(text.AsSpan(at + 5).TrimEnd(), out int n) ? n : 0;
            return (client, port);
        }
        finally
        {
            lock (_lock)
            {
                if (_armed.TryGetValue(busId, out Armed? entry) && entry == armed)
                {
                    _armed.Remove(busId);
                }
            }
            done.TrySetCanceled(CancellationToken.None);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = HandshakeAsync(client);
        }
    }

    // OP_REQ_IMPORT for an armed bus id -> OP_REP_IMPORT + device info, and the connection goes to the attacher.
    private async Task HandshakeAsync(TcpClient client)
    {
        client.NoDelay = true;
        NetworkStream s = client.GetStream();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var op = new byte[8];
            await s.ReadExactlyAsync(op, timeout.Token).ConfigureAwait(false);
            ushort code = BinaryPrimitives.ReadUInt16BigEndian(op.AsSpan(2));
            if (code != 0x8003)
            {
                Log.Warn("usbip", $"refused op 0x{code:X4}");
                client.Dispose();
                return;
            }
            var bus = new byte[32];
            await s.ReadExactlyAsync(bus, timeout.Token).ConfigureAwait(false);
            string busId = Encoding.ASCII.GetString(bus).TrimEnd('\0');
            // Only usbip-win2 may import: the usbip.exe this service started, or its driver (System, which no program
            // can be). Other local programs are refused and the attach stays armed.
            int peer = PeerProcess(client);
            Armed? entry;
            bool armed;
            lock (_lock)
            {
                armed = _armed.TryGetValue(busId, out entry) && (peer == SystemProcess || peer == entry!.UsbipProcess) && _armed.Remove(busId);
            }
            if (!armed && entry is not null)
            {
                Log.Warn("usbip", $"refused import of {busId} by process {peer}: not usbip-win2");
            }
            var reply = new byte[8];
            BinaryPrimitives.WriteUInt16BigEndian(reply, 0x0111);
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), 0x0003);
            if (!armed)
            {
                if (entry is null)
                {
                    Log.Warn("usbip", $"refused import of {busId}: no attach in progress");
                }
                BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), 1);
                await s.WriteAsync(reply, timeout.Token).ConfigureAwait(false);
                client.Dispose();
                return;
            }
            await s.WriteAsync(reply, timeout.Token).ConfigureAwait(false);
            await s.WriteAsync(entry!.Info, timeout.Token).ConfigureAwait(false);
            Log.Info("usbip", $"import of {busId} accepted (process {peer})");
            if (!entry.Done.TrySetResult(client))
            {
                client.Dispose();
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            Log.Warn("usbip", $"handshake failed: {e.Message}");
            client.Dispose();
        }
    }
}
