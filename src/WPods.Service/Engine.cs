using System.ComponentModel;
using System.Net.Sockets;
using Microsoft.Win32;

namespace WPods.Service;

/// <summary>
/// Binds the Bluetooth adapter to WinUSB, serves it and the WPods mic to usbip-win2, and restarts when the radio is lost.
/// Guard: 3 failed takeovers within 10 minutes give the adapter back to its native driver until the app asks to retry.
/// </summary>
internal sealed class Engine
{
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HealthySession = TimeSpan.FromSeconds(60);
    // After a wake: how long the adapter is watched for lost state, and how long a command may wait for its answer.
    private static readonly TimeSpan ResumeWatch = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UnansweredLimit = TimeSpan.FromSeconds(3);
    // Bluetooth company ID of Realtek Semiconductor.
    private const ushort RealtekManufacturer = 0x005D;

    private readonly HciProxy _hci = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _retry = new();
    private readonly ManualResetEventSlim _running = new();
    private readonly Lock _lock = new();
    // Serializes the driver switches of a takeover and of RestoreUnused.
    private readonly Lock _bindLock = new();
    private Timer? _unusedTimer;
    private readonly StatusPipeServer _status;
    private readonly MicPipeServer _micPipe;
    private readonly AapPipeServer _aapPipe;
    private readonly AutoResetEvent _micChanged = new(false);
    private UsbIpServer? _server;
    private Thread? _supervisor;
    private Thread? _micThread;
    private ServiceState _state = ServiceState.Starting;
    private string _detail = "";
    private MicDevice? _mic;
    private TcpClient? _micClient;
    private int _micPort;
    private int _busIds;
    private bool _relaunched;
    private bool _suspended;
    private volatile bool _resumeRestart;
    private RadioLink? _link;
    private IDisposable? _removedWatch;
    // Real adapter resets requested by the vendor driver, oldest first.
    private readonly Queue<DateTime> _adapterResets = new();
    private const int MaxAdapterResets = 3;
    private static readonly TimeSpan AdapterResetWindow = TimeSpan.FromHours(24);
    private (ulong Address, string Name)? _micWanted;

    public Engine()
    {
        _status = new StatusPipeServer(this);
        _micPipe = new MicPipeServer(this);
        _aapPipe = new AapPipeServer(_hci);
        if (Registry.LocalMachine.OpenSubKey(RadioDriver.StateKey) is { } key)
        {
            using (key)
            {
                if (key.GetValue("MicAddress") is long address && key.GetValue("MicName") is string name)
                {
                    _micWanted = ((ulong)address, name);
                }
            }
        }
    }

    public (ServiceState State, string Detail) State
    {
        get
        {
            lock (_lock)
            {
                return (_state, _detail);
            }
        }
    }

    public bool MicCapturing => Volatile.Read(ref _mic)?.Capturing ?? false;

    public static bool GuardTripped
    {
        get => Registry.LocalMachine.OpenSubKey(RadioDriver.StateKey)?.GetValue("GuardTripped") is int v && v == 1;
        set
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(RadioDriver.StateKey);
            key.SetValue("GuardTripped", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    /// <summary>Set by uninstall before it stops the service: the stop then also forgets the virtual devices.</summary>
    public static bool Uninstalling
    {
        get => Registry.LocalMachine.OpenSubKey(RadioDriver.StateKey)?.GetValue("Uninstalling") is int v && v == 1;
        set
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(RadioDriver.StateKey);
            key.SetValue("Uninstalling", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    /// <summary>Set by Setup after installing usbip-win2, which needs a restart; cleared when the service starts.</summary>
    public static bool WaitsForRestart
    {
        get => Registry.LocalMachine.OpenSubKey(RadioDriver.StateKey)?.GetValue("WaitsForRestart") is int v && v == 1;
        set
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(RadioDriver.StateKey);
            key.SetValue("WaitsForRestart", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    /// <summary>
    /// Gives every adapter WPods bound back to its native driver. True when all are restored, or on uninstall
    /// unplugged and marked to get it when plugged in again.
    /// </summary>
    public static bool RestoreNative()
    {
        bool restored = RestoreOthers(keep: RadioDriver.StoredInstance);
        if (RadioDriver.StoredInstance is not { } id)
        {
            return restored;
        }
        if (!RadioDriver.IsPresent(id))
        {
            return GiveBackUnplugged(id) && restored;
        }
        if (Uninstalling)
        {
            int forgotten = 0;
            foreach (string other in RadioDriver.OtherInstances(id))
            {
                // The detach is not instant: wait until Windows has unplugged the virtual adapter.
                for (int i = 0; i < 50 && RadioDriver.IsPresent(other); i++)
                {
                    Thread.Sleep(100);
                }
                // Audio nodes only, so native audio devices get their plain names back; removing the adapter, input or
                // LE nodes can remove pairings the native adapter shares.
                forgotten += RadioDriver.ForgetHiddenSubtree(other, includeRoot: false, audioOnly: true);
            }
            MicDevice.ForgetAllDevnodes();
            Log.Info("radio", $"forgot {forgotten} hidden audio device nodes of the virtual adapter");
        }
        if (!RadioDriver.IsOnWinUsb(id))
        {
            return restored;
        }
        try
        {
            int carried = RadioDriver.CarryLeDeviceIds();
            if (carried > 0)
            {
                Log.Info("radio", $"stored the Device ID of {carried} LE devices paired on the virtual adapter");
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Warn("radio", $"Device IDs of LE devices not stored: {e.Message}");
        }
        try
        {
            RadioDriver.RestoreNative(id);
            if (Uninstalling)
            {
                // Re-plug so Windows sets the adapter's devices up fresh from the pairing data.
                RadioDriver.CyclePort(id);
            }
            return restored;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Error("radio", "restoring the native driver failed", e);
            return false;
        }
    }

    /// <summary>Restores every bound adapter except <paramref name="keep"/> still on WinUSB; false if one is not.</summary>
    private static bool RestoreOthers(string? keep)
    {
        bool restored = true;
        foreach (string bound in RadioDriver.BoundInstances)
        {
            if (bound.Equals(keep, StringComparison.OrdinalIgnoreCase) || !RadioDriver.BoundToWinUsb(bound))
            {
                continue;
            }
            if (!RadioDriver.IsPresent(bound))
            {
                restored &= GiveBackUnplugged(bound);
                continue;
            }
            try
            {
                RadioDriver.RestoreNative(bound);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                Log.Error("radio", $"restoring the native driver on {bound} failed", e);
                restored = false;
            }
        }
        return restored;
    }

    // An unplugged adapter's driver cannot be changed. On uninstall Windows is told to reinstall the native driver on
    // re-plug; otherwise RestoreUnused gives it back once it is plugged in.
    private static bool GiveBackUnplugged(string id)
    {
        if (!RadioDriver.BoundToWinUsb(id))
        {
            return true;
        }
        if (!Uninstalling)
        {
            return false;
        }
        if (RadioDriver.MarkForReinstall(id))
        {
            Log.Info("radio", $"{id} is unplugged: Windows installs its native driver again when it is plugged in");
            return true;
        }
        Log.Warn("radio", $"{id} is unplugged and still on WinUSB, and could not be marked for its native driver");
        return false;
    }

    // Gives unused adapters on WinUSB (all of them while the Guard tripped or usbip-win2 is missing) their native driver
    // back. Skipped while the stored adapter is unplugged, as Find then picks the next adapter.
    private void RestoreUnused()
    {
        lock (_bindLock)
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }
            string? keep = GuardTripped || !UsbIpServer.ClientInstalled ? null : RadioDriver.StoredInstance;
            if (keep is not null && !RadioDriver.IsPresent(keep))
            {
                return;
            }
            // RunSession records the stored adapter as bound too.
            RestoreOthers(keep);
        }
    }

    /// <summary>Null while an update is being installed at start.</summary>
    public Updater? Updater { get; private set; }

    /// <summary>Set by "--stop" before Setup replaces the files, so an update keeps the adapter on WinUSB.</summary>
    public static bool KeepAdapterOnStop
    {
        get => Registry.LocalMachine.OpenSubKey(RadioDriver.StateKey)?.GetValue("KeepAdapterOnStop") is int v && v == 1;
        set
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(RadioDriver.StateKey);
            key.SetValue("KeepAdapterOnStop", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    public void Start()
    {
        KeepAdapterOnStop = false;
        WaitsForRestart = false;
        WPods.Service.Updater.ProtectDataFolder();
        _ = _status.RunAsync(_stop.Token);
        // A downloaded update installs before any takeover; if Setup fails, the service starts as usual.
        if (WPods.Service.Updater.InstallStagedAtStart(() =>
        {
            if (!_stop.IsCancellationRequested)
            {
                StartRunning();
            }
        }))
        {
            SetState(ServiceState.Updating, "installing an update");
            return;
        }
        StartRunning();
    }

    private void StartRunning()
    {
        Updater = new Updater(_status.BroadcastUpdate);
        // A device removed in Settings must also lose its hidden twin on the other adapter.
        _removedWatch = RadioDriver.WatchRemovedBluetoothDevices(id =>
        {
            int forgotten = RadioDriver.ForgetTwinsOfRemoved(id);
            if (forgotten > 0)
            {
                Log.Info("radio", $"{id} was removed: forgot {forgotten} hidden nodes of the same device");
            }
        });
        _ = _micPipe.RunAsync(_stop.Token);
        _ = _aapPipe.RunAsync(_stop.Token);
        _supervisor = new Thread(Supervise) { IsBackground = true, Name = "supervisor" };
        _supervisor.Start();
        _micThread = new Thread(ServeMic) { IsBackground = true, Name = "mic" };
        _micThread.Start();
        _unusedTimer = new Timer(_ => RestoreUnused(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void Stop(bool restoreNative)
    {
        _unusedTimer?.Dispose();
        _removedWatch?.Dispose();
        _stop.Cancel();
        DetachMic();
        _micChanged.Set();
        _supervisor?.Join(10000);
        _micThread?.Join(5000);
        _server?.Dispose();
        Updater?.Dispose();
        if (restoreNative)
        {
            RestoreNative();
        }
    }

    public void OnSuspend()
    {
        Log.Info("engine", "going to sleep");
        Volatile.Write(ref _suspended, true);
    }

    /// <summary>
    /// After a wake, re-plugs the virtual adapter: always for a Realtek one, else only if the adapter lost its firmware:
    /// commands go unanswered, or it reports a hardware error or another firmware version.
    /// </summary>
    public void OnResume()
    {
        if (!Interlocked.Exchange(ref _suspended, false))
        {
            return;
        }
        if (Volatile.Read(ref _link) is not { } watched)
        {
            Log.Info("engine", "resumed from sleep: no session running");
            return;
        }
        if (_hci.Manufacturer == RealtekManufacturer)
        {
            // Realtek controllers drop their firmware in sleep unless armed for remote wake, which WinUSB does not do,
            // and still answer from ROM: Realtek's driver loads it again on a fresh virtual adapter.
            Log.Info("engine", "resumed from sleep: Realtek adapter, plugging the virtual adapter in again");
            _resumeRestart = true;
            watched.Dispose();
            return;
        }
        Log.Info("engine", $"resumed from sleep: watching the adapter for {ResumeWatch.TotalSeconds:F0} s");
        long until = Environment.TickCount64 + (long)ResumeWatch.TotalMilliseconds;
        string? trouble = null;
        void OnTrouble(string reason) => Volatile.Write(ref trouble, reason);
        _hci.ControllerTrouble += OnTrouble;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                while (Environment.TickCount64 < until && !_stop.IsCancellationRequested && Volatile.Read(ref _link) == watched)
                {
                    string? reason = Volatile.Read(ref trouble)
                        ?? (_hci.UnansweredMs > UnansweredLimit.TotalMilliseconds ? $"a command unanswered for {_hci.UnansweredMs} ms" : null);
                    if (reason is not null)
                    {
                        Log.Info("engine", $"after the wake: {reason}: plugging the virtual adapter in again");
                        _resumeRestart = true;
                        watched.Dispose();
                        return;
                    }
                    Thread.Sleep(250);
                }
                Log.Info("engine", Volatile.Read(ref _link) == watched ? "after the wake: the adapter kept its state" : "after the wake: the session ended");
            }
            finally
            {
                _hci.ControllerTrouble -= OnTrouble;
            }
        });
    }

    public void AppStarted()
    {
        if (MicCapturing || _hci.CallAudioActive)
        {
            Log.Info("update", "app started during a recording or call: a downloaded update waits for the next start");
            return;
        }
        Updater?.AppStarted();
    }

    /// <summary>The app asks to take the adapter over again (after the Guard or when no adapter was found).</summary>
    public void Retry()
    {
        Log.Info("engine", "retry requested");
        _retry.Set();
    }

    /// <summary>
    /// Sets the pair the mic is for. A Windows name (not <paramref name="reported"/> by the AirPods) is used only for a
    /// pair without a known name, so the mic's name and its Windows endpoint do not flip on every connect.
    /// </summary>
    public void SetMic(ulong address, string name, bool reported)
    {
        if (!reported && KnownMicName(address) is { } known)
        {
            name = known;
        }
        bool sameMic;
        lock (_lock)
        {
            if (_micWanted is { } w && w.Address == address && w.Name == name)
            {
                return;
            }
            sameMic = _micWanted is { } old && old.Address == address;
            Log.Info("mic", $"pair {address:X12} \"{name}\"{(reported ? "" : " (Windows name)")}");
            _micWanted = (address, name);
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(RadioDriver.StateKey);
            key.SetValue("MicAddress", (long)address, RegistryValueKind.QWord);
            key.SetValue("MicName", name);
        }
        if (sameMic && MicCapturing)
        {
            // A rename re-plugs the mic, so it waits until no app records from it.
            Log.Info("mic", "rename waits until the recording stops");
            return;
        }
        // The mic thread attaches the new mic after the detach.
        DetachMic();
        _micChanged.Set();
    }

    private static string? KnownMicName(ulong address)
    {
        using RegistryKey? names = Registry.LocalMachine.OpenSubKey(RadioDriver.StateKey + @"\MicNames");
        return names?.GetValue(MicDevice.SerialOf(address)) as string;
    }

    // Tells the app the capture state and applies a rename that waited for the recording to stop.
    private void OnCapture(MicDevice mic, bool capturing)
    {
        _micPipe.NotifyCapture(capturing);
        bool renamed;
        lock (_lock)
        {
            renamed = !capturing && _micWanted is { } w && (w.Address != mic.Address || w.Name != mic.Name);
        }
        if (renamed)
        {
            // Not on the mic's own URB thread, which the detach ends.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                DetachMic();
                _micChanged.Set();
            });
        }
    }

    private void DetachMic()
    {
        TcpClient? client;
        int port;
        lock (_lock)
        {
            client = _micClient;
            port = _micPort;
            _micClient = null;
            _micPort = 0;
        }
        if (port != 0)
        {
            UsbIpServer.Detach(port);
        }
        client?.Dispose();
    }

    public void MicPush(long position, ReadOnlySpan<byte> pcm) => Volatile.Read(ref _mic)?.Push(position, pcm);

    private void SetState(ServiceState state, string detail)
    {
        lock (_lock)
        {
            if (_state == state && _detail == detail)
            {
                return;
            }
            _state = state;
            _detail = detail;
        }
        Log.Info("engine", $"state {state} {detail}");
        if (state == ServiceState.Running)
        {
            _running.Set();
        }
        else
        {
            _running.Reset();
        }
        if (state is not (ServiceState.Starting or ServiceState.Updating) && !_relaunched)
        {
            // Reopens the app if it closed for an update, once the outcome is known, so it can show it.
            _relaunched = true;
            ThreadPool.QueueUserWorkItem(_ => WPods.Service.Updater.RelaunchApp());
        }
        _status.Broadcast(state, detail);
    }

    // True when a retry was requested, false on timeout or stop.
    private bool WaitRetry(TimeSpan timeout)
    {
        int signaled = WaitHandle.WaitAny([_retry.WaitHandle, _stop.Token.WaitHandle], timeout);
        _retry.Reset();
        return signaled == 0;
    }

    private void Supervise()
    {
        var failures = new Queue<DateTime>();
        while (!_stop.IsCancellationRequested)
        {
            if (GuardTripped)
            {
                SetState(ServiceState.Native, "");
                if (!WaitRetry(Timeout.InfiniteTimeSpan))
                {
                    continue;
                }
                lock (_bindLock)
                {
                    GuardTripped = false;
                }
                failures.Clear();
            }
            if (!UsbIpServer.ClientInstalled)
            {
                // Without usbip-win2 the adapter cannot work through WPods: give it back to Windows.
                SetState(ServiceState.Unsupported, "usbip-missing");
                RestoreUnused();
                WaitRetry(TimeSpan.FromSeconds(30));
                continue;
            }
            if (RadioDriver.Find(out string reason) is not { } id)
            {
                SetState(ServiceState.Unsupported, reason);
                WaitRetry(TimeSpan.FromSeconds(10));
                continue;
            }
            if (RadioDriver.IsDisabled(id))
            {
                // Never re-enable an adapter turned off in Device Manager.
                SetState(ServiceState.Starting, "the Bluetooth adapter is disabled in Device Manager");
                WaitRetry(TimeSpan.FromSeconds(10));
                continue;
            }
            SetState(ServiceState.Starting, "");
            DateTime started = DateTime.UtcNow;
            bool failed;
            try
            {
                // Before every attach: installing usbip-win2 again, also while the service runs, turns the task back on.
                UsbIpServer.SetShutdownTask(false);
                string ended = RunSession(id);
                Log.Info("engine", $"session ended: {ended}");
                // A replug after resume is no failure, however short the session was.
                failed = DateTime.UtcNow - started < HealthySession && !_resumeRestart;
                _resumeRestart = false;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Error("engine", "takeover failed", e);
                failed = true;
            }
            if (_stop.IsCancellationRequested)
            {
                break;
            }
            if (failed)
            {
                failures.Enqueue(DateTime.UtcNow);
                while (failures.Count > 0 && DateTime.UtcNow - failures.Peek() > FailureWindow)
                {
                    failures.Dequeue();
                }
                if (failures.Count >= 3)
                {
                    Log.Warn("engine", "3 failed takeovers within 10 minutes: giving the adapter back to Windows");
                    _running.Reset();
                    RestoreNative();
                    GuardTripped = true;
                    continue;
                }
            }
            SetState(ServiceState.Starting, "");
            WaitRetry(TimeSpan.FromSeconds(2));
        }
    }

    // Runs one takeover until the radio or its USB/IP connection goes away; returns why it ended.
    private string RunSession(string id)
    {
        lock (_bindLock)
        {
            RadioDriver.StoredInstance = id;
            RadioDriver.RememberBound(id);
        }
        // Windows cannot restart an adapter onto WinUSB while it is open (e.g. audio playing): re-plug it in software,
        // else retry every 5 s. Waiting never trips the Guard, and a stop ends it before any further driver switch.
        bool replugged = false;
        while (!RadioDriver.IsOnWinUsb(id) || RadioDriver.NeedsRestart(id))
        {
            if (_stop.IsCancellationRequested)
            {
                return "service stopping";
            }
            if (!RadioDriver.BindWinUsb(id) && !RadioDriver.NeedsRestart(id))
            {
                break;
            }
            if (!replugged && !_stop.IsCancellationRequested && RadioDriver.CyclePort(id))
            {
                replugged = true;
                for (int i = 0; i < 60 && !_stop.IsCancellationRequested
                    && !(RadioDriver.IsPresent(id) && RadioDriver.IsOnWinUsb(id) && !RadioDriver.NeedsRestart(id)); i++)
                {
                    Thread.Sleep(250);
                }
                continue;
            }
            SetState(ServiceState.Starting, "waiting for the Bluetooth adapter to be free");
            WaitRetry(TimeSpan.FromSeconds(5));
            if (_stop.IsCancellationRequested)
            {
                return "service stopping";
            }
        }
        // Forget the native adapter's now hidden audio nodes, or Windows numbers the virtual adapter's twins
        // ("Headphones (2- AirPods Pro)").
        int forgotten = RadioDriver.ForgetHiddenSubtree(id, includeRoot: false, audioOnly: true);
        if (forgotten > 0)
        {
            Log.Info("radio", $"forgot {forgotten} hidden audio device nodes of the adapter's native driver");
        }
        WinUsbDevice device = OpenRadio(id, _stop.Token);
        // An open handle would block Windows removing the adapter (driver update, Device Manager, restart): release it
        // and answer only once it is closed.
        var gate = new Lock();
        RadioLink? link = null;
        bool removing = false;
        using var released = new ManualResetEventSlim();
        IDisposable watch = RadioDriver.WatchRemoval(device.File, () =>
        {
            Log.Info("radio", "Windows is removing the adapter: letting it go");
            lock (gate)
            {
                removing = true;
                if (link is null)
                {
                    device.Dispose();
                }
                else
                {
                    link.Dispose();
                }
            }
            released.Wait(TimeSpan.FromSeconds(10));
        });
        string bus;
        int port;
        try
        {
            // Forget hidden audio nodes of virtual adapters with another serial; this one's carry the audio endpoints
            // and their settings.
            int stale = 0;
            foreach (string other in RadioDriver.OtherInstances(id))
            {
                if (!other.EndsWith(@"\" + device.Serial, StringComparison.OrdinalIgnoreCase))
                {
                    stale += RadioDriver.ForgetHiddenSubtree(other, includeRoot: false, audioOnly: true);
                }
            }
            if (stale > 0)
            {
                Log.Info("radio", $"forgot {stale} hidden audio device nodes of older virtual adapters");
            }
            _server ??= new UsbIpServer();
            bus = NextBusId();
            byte[] info = UsbIp.DeviceInfo(bus, device.DeviceDescriptor, device.ConfigDescriptor, device.Speed);
            TcpClient client;
            (client, port) = _server.AttachAsync(bus, info, _stop.Token).GetAwaiter().GetResult();
            lock (gate)
            {
                if (!removing)
                {
                    link = new RadioLink(client, device, _hci);
                }
            }
            if (link is not { } current)
            {
                // Windows took the adapter while it was being attached.
                client.Dispose();
            }
            else
            {
                Volatile.Write(ref _link, current);
                current.ResetRequested += () =>
                {
                    // Capped per day: a driver that keeps resetting the adapter has a problem a reset does not cure.
                    lock (_adapterResets)
                    {
                        while (_adapterResets.Count > 0 && DateTime.UtcNow - _adapterResets.Peek() > AdapterResetWindow)
                        {
                            _adapterResets.Dequeue();
                        }
                        if (_adapterResets.Count >= MaxAdapterResets)
                        {
                            Log.Warn("radio", $"the driver reset the adapter {MaxAdapterResets} times within 24 h: not resetting the real adapter again");
                            return;
                        }
                        _adapterResets.Enqueue(DateTime.UtcNow);
                    }
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        // After the reply went out, reset the real USB port as the driver's reset would natively; the
                        // session then restarts on the reset chip.
                        Thread.Sleep(300);
                        if (!RadioDriver.CyclePort(id))
                        {
                            Log.Warn("radio", "the adapter's USB port could not be reset; plugging the virtual adapter in again");
                            current.Dispose();
                        }
                    });
                };
                // On stop, a clean detach first, which also ends the connection.
                using (_stop.Token.Register(() =>
                {
                    UsbIpServer.Detach(port);
                    current.Dispose();
                }))
                {
                    SetState(ServiceState.Running, "");
                    current.Run();
                }
            }
        }
        finally
        {
            // Keeps a running resume watch from acting on this session.
            Volatile.Write(ref _link, null);
            // Order matters: closing the handle releases a waiting removal; only then may the watch end, as its
            // Dispose waits for the callback.
            lock (gate)
            {
                link?.Dispose();
                device.Dispose();
            }
            released.Set();
            watch.Dispose();
        }
        _running.Reset();
        DetachMic();
        if (!_stop.IsCancellationRequested)
        {
            // usbip-win2 would otherwise keep reconnecting the broken device on its own.
            if (port != 0)
            {
                UsbIpServer.Detach(port);
            }
            _server.StopReconnect(bus);
        }
        return _stop.IsCancellationRequested ? "service stopping"
            : link is null ? "adapter removed by Windows"
            : link.DeviceLost ? "radio lost" : "USB/IP connection closed";
    }

    // A fresh bus id per attach: usbip-win2 keeps retrying a broken import for a while, which must never match a new one.
    private string NextBusId() => $"1-{Interlocked.Increment(ref _busIds)}";

    // WinUSB's device interface appears a moment after the bind.
    private static WinUsbDevice OpenRadio(string id, CancellationToken stop)
    {
        string path = RadioDriver.DevicePath(id);
        // A serial number from the real adapter's instance ID: stable for this adapter in this USB port.
        string serial = "WPODS" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.ToUpperInvariant())), 0, 8);
        for (int i = 0; ; i++)
        {
            try
            {
                return WinUsbDevice.Open(path, RadioDriver.IsFunction(id), serial);
            }
            catch (Win32Exception e) when (i < 30 && !stop.IsCancellationRequested)
            {
                if (i == 0)
                {
                    Log.Info("radio", $"waiting for WinUSB ({e.NativeErrorCode})");
                }
                Thread.Sleep(500);
            }
        }
    }

    // Keeps the mic of the wanted pair attached while the radio runs.
    private void ServeMic()
    {
        while (!_stop.IsCancellationRequested)
        {
            if (!_running.IsSet)
            {
                WaitHandle.WaitAny([_running.WaitHandle, _stop.Token.WaitHandle]);
                continue;
            }
            (ulong Address, string Name)? wanted;
            lock (_lock)
            {
                wanted = _micWanted;
            }
            if (wanted is not { } w || _server is not { } server)
            {
                WaitHandle.WaitAny([_micChanged, _stop.Token.WaitHandle], 5000);
                continue;
            }
            try
            {
                AttachMic(server, w.Address, w.Name);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Error("mic", "attach failed", e);
            }
            // Gives Windows a moment to remove a detached device.
            WaitHandle.WaitAny([_micChanged, _stop.Token.WaitHandle], 2000);
        }
    }

    private void AttachMic(UsbIpServer server, ulong address, string name)
    {
        var mic = new MicDevice(address, name);
        using (RegistryKey names = Registry.LocalMachine.CreateSubKey(RadioDriver.StateKey + @"\MicNames"))
        {
            // Windows keeps the name it saw first for a device; a renamed pair needs its old device nodes removed.
            if (names.GetValue(mic.Serial) is string old && old != name)
            {
                MicDevice.ForgetDevnodes(mic.Serial);
            }
            names.SetValue(mic.Serial, name);
        }
        string bus = NextBusId();
        byte[] info = UsbIp.DeviceInfo(bus, mic.Device, mic.Config, 2);
        (TcpClient client, int port) = server.AttachAsync(bus, info, _stop.Token).GetAwaiter().GetResult();
        bool current;
        lock (_lock)
        {
            current = _micWanted is { } w && w.Address == address && w.Name == name && _running.IsSet;
            if (current)
            {
                _micClient = client;
                _micPort = port;
            }
        }
        if (!current)
        {
            // The wanted mic changed while attaching.
            UsbIpServer.Detach(port);
            client.Dispose();
            return;
        }
        mic.CaptureChanged += capturing => OnCapture(mic, capturing);
        Volatile.Write(ref _mic, mic);
        Log.Info("mic", $"attached \"Headset (A2DP, {name})\" ({mic.Serial})");
        try
        {
            mic.Serve(client);
        }
        finally
        {
            Volatile.Write(ref _mic, null);
            int stalePort = 0;
            lock (_lock)
            {
                if (_micClient == client)
                {
                    // Ended on its own (not detached by us): still unplug it so usbip-win2 does not retry.
                    _micClient = null;
                    stalePort = _micPort;
                    _micPort = 0;
                }
            }
            if (stalePort != 0)
            {
                UsbIpServer.Detach(stalePort);
                server.StopReconnect(bus);
            }
        }
    }
}
