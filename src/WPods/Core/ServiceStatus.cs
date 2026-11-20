using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Timer = System.Threading.Timer;

namespace WPods.Core;

/// <summary>WPods.Service state; values 0 to 4 match the service's.</summary>
internal enum ServiceState
{
    Starting = 0,
    Running = 1,
    /// <summary>The Guard gave the adapter back to the Windows driver after repeated failures.</summary>
    Native = 2,
    /// <summary>No adapter to take over (<see cref="ServiceStatus.Detail"/> says why).</summary>
    Unsupported = 3,
    /// <summary>An update is installing: the app closes and the new service reopens it.</summary>
    Updating = 4,
    /// <summary>App side only: the pipe cannot be opened and the Guard did not trip.</summary>
    NotRunning = 5,
}

/// <summary>Service updater state; values match the service's.</summary>
internal enum UpdateStatus
{
    Idle = 0,
    Checking = 1,
    Downloading = 2,
    /// <summary>Downloaded and verified; installs at the next app or Windows start, or on "Update now".</summary>
    Ready = 3,
    Installing = 4,
}

/// <summary>Update state from the service; empty strings when not set.</summary>
internal sealed record UpdateInfo(UpdateStatus Status, bool Auto, bool Checked, string Installed, string Available, string NotesUrl, string Error)
{
    public static readonly UpdateInfo Unknown = new(UpdateStatus.Idle, true, false, "", "", "", "");
}

/// <summary>
/// Tracks WPods.Service state over its pipe and reconnects with capped backoff.
/// Events fire on a pool or timer thread under the internal lock: handlers must not block or call back in.
/// </summary>
internal sealed class ServiceStatus : IDisposable
{
    private const int MaxBackoffShift = 6; // 1 s up to 64 s
    private const string GuardKey = @"SOFTWARE\WPods\Service";
    private const string GuardValue = "GuardTripped";

    private readonly Lock _lock = new();
    // HD voice per pair as last set; sent again after every connect, as the pipe may have been closed meanwhile.
    private readonly Dictionary<ulong, bool> _hdVoice = [];
    private readonly Timer _reconnect;
    private NamedPipeClientStream? _pipe;
    private ServiceState? _state;
    private int _failures;
    private bool _disposed;
    // Connect thread only.
    private bool _announced;

    public ServiceStatus()
    {
        _reconnect = new Timer(static s => ((ServiceStatus)s!).Connect(), this, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<ServiceState>? Changed;

    public event Action<UpdateInfo>? UpdateChanged;

    public UpdateInfo Update { get; private set; } = UpdateInfo.Unknown;

    /// <summary>Service reason code for the state, e.g. "not-usb", "none", "usbip-missing" when Unsupported.</summary>
    public string Detail { get; private set; } = "";

    public void SetAutoUpdate(bool on) => Send([ServicePipes.SetAutoUpdateMessage, on ? (byte)1 : (byte)0], "auto-update");

    public void CheckUpdate() => Send([ServicePipes.CheckUpdateMessage], "check for updates");

    public void UpdateNow() => Send([ServicePipes.UpdateNowMessage], "update now");

    /// <summary>HD voice is offered for models without the stereo mic stream.</summary>
    public static bool HdVoiceAllowed(string? modelNumber) => !Models.HasStereoMic(modelNumber);

    /// <summary>Tells the service to run Hands-Free itself for the pair (on) or not; it keeps the choice.</summary>
    public void SetHdVoice(ulong address, bool on)
    {
        // Under the lock, so a reconnect's replay never sends an older choice after this one.
        lock (_lock)
        {
            _hdVoice[address] = on;
            SendHdVoice(address, on);
        }
    }

    private void SendHdVoice(ulong address, bool on)
    {
        byte[] message = new byte[10];
        message[0] = ServicePipes.SetHdVoiceMessage;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(1), address);
        message[9] = on ? (byte)1 : (byte)0;
        Send(message, $"HD voice {(on ? "on" : "off")} for {Mac.Format(address)}");
    }

    private void Send(byte[] message, string what)
    {
        lock (_lock)
        {
            if (_pipe is not { } pipe)
            {
                Log.Warn("service", $"{what}: service pipe not open");
                return;
            }
            Log.Info("service", what);
            try
            {
                pipe.Write(message);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The read loop detects the broken pipe and reconnects.
                Log.Warn("service", $"{what}: write failed: 0x{e.HResult:X8} {e.Message}");
            }
        }
    }

    public void Start() => ThreadPool.QueueUserWorkItem(static s => ((ServiceStatus)s!).Connect(), this);

    /// <summary>Asks the service to take over the adapter again; dropped while the pipe is closed.</summary>
    public void Retry() => Send([ServicePipes.ServiceRetryMessage], "retry adapter takeover");

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _reconnect.Dispose();
            _pipe?.Dispose();
            _pipe = null;
        }
    }

    // Runs on the pool or the reconnect timer, never concurrently: a reconnect is scheduled only while the pipe is closed.
    private void Connect()
    {
        NamedPipeClientStream? pipe = ServicePipes.Connect(ServicePipes.ServicePipe, out int error);
        lock (_lock)
        {
            if (_disposed)
            {
                pipe?.Dispose();
                return;
            }
            if (pipe is null)
            {
                Log.Warn("service", $"pipe open failed, Win32 error {error}");
                ScheduleLocked();
                SetStateLocked(GuardTripped() ? ServiceState.Native : ServiceState.NotRunning, $"pipe not open, Win32 error {error}");
                return;
            }
            Log.Info("service", "pipe opened");
            _pipe = pipe;
        }
        // Once per app run: lets the service install an already downloaded update.
        if (!_announced)
        {
            _announced = true;
            Send([ServicePipes.AppStartedMessage], "app started");
        }
        lock (_lock)
        {
            foreach ((ulong address, bool on) in _hdVoice)
            {
                SendHdVoice(address, on);
            }
        }
        _ = ReadLoopAsync(pipe);
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe)
    {
        byte[] buffer = new byte[4096];
        string reason;
        try
        {
            while (true)
            {
                int count = await pipe.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0)
                {
                    reason = "pipe closed by the service";
                    break;
                }
                if (buffer[0] == ServicePipes.UpdateInfoMessage && count >= 3)
                {
                    string[] f = Encoding.UTF8.GetString(buffer, 3, count - 3).Split('\0');
                    var info = new UpdateInfo((UpdateStatus)buffer[1], (buffer[2] & 1) != 0, (buffer[2] & 2) != 0,
                        f.Length > 0 ? f[0] : "", f.Length > 1 ? f[1] : "", f.Length > 2 ? f[2] : "", f.Length > 3 ? f[3] : "");
                    lock (_lock)
                    {
                        if (_pipe != pipe)
                        {
                            return;
                        }
                        if (info != Update)
                        {
                            Log.Info("service", $"update: {info}");
                            Update = info;
                            UpdateChanged?.Invoke(info);
                        }
                    }
                    continue;
                }
                if (buffer[0] != ServicePipes.ServiceStateMessage || count < 2 || buffer[1] > (byte)ServiceState.Updating)
                {
                    Log.Warn("service", $"unknown message 0x{buffer[0]:X2} ({count} bytes), ignored");
                    continue;
                }
                string detail = Encoding.UTF8.GetString(buffer, 2, count - 2);
                lock (_lock)
                {
                    if (_pipe != pipe)
                    {
                        return;
                    }
                    _failures = 0;
                    SetStateLocked((ServiceState)buffer[1], $"service: \"{detail}\"", detail);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            reason = $"read failed: 0x{e.HResult:X8} {e.Message}";
        }
        lock (_lock)
        {
            if (_pipe == pipe)
            {
                // The last state is kept until the reconnect result replaces it.
                Log.Warn("service", $"{reason}; reconnecting");
                _pipe.Dispose();
                _pipe = null;
                ScheduleLocked();
            }
        }
    }

    private void SetStateLocked(ServiceState state, string reason, string detail = "")
    {
        if (_state == state && Detail == detail)
        {
            return;
        }
        Log.Info("service", $"state {_state?.ToString() ?? "unknown"} -> {state} ({reason})");
        _state = state;
        Detail = detail;
        Changed?.Invoke(state);
    }

    private void ScheduleLocked()
    {
        Log.Info("service", $"reconnect in {1000 << _failures} ms");
        _reconnect.Change(1000 << _failures, Timeout.Infinite);
        _failures = Math.Min(_failures + 1, MaxBackoffShift);
    }

    private static bool GuardTripped() => Flag(GuardValue);

    /// <summary>Setup installed usbip-win2, so the service starts only after the next Windows restart.</summary>
    public static bool WaitsForRestart() => Flag("WaitsForRestart");

    private static bool Flag(string name)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(GuardKey);
            return key?.GetValue(name) is int value && value == 1;
        }
        catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("service", $"{name} not readable: {e.Message}");
            return false;
        }
    }
}
