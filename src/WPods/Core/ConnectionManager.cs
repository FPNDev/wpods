using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace WPods.Core;

/// <summary>Why a pair is not attached although WPods is not trying to attach it (<see cref="KnownDevice.Idle"/>).</summary>
public enum IdleReason
{
    /// <summary>Attached, link down, or WPods is attaching / retrying.</summary>
    None,
    /// <summary>
    /// The user pressed Disconnect. Set at once when <see cref="ConnectionManager.DisconnectAsync"/> starts; the pair is
    /// never auto-attached until its link drops (flag cleared) or the user connects it (<see cref="ConnectionManager.ConnectAsync"/>).
    /// </summary>
    UserDisconnected,
    /// <summary>
    /// The link is up but the attach and every fast retry failed. WPods keeps trying every 30 s, and at once on the
    /// next link-up, case opening (<see cref="ConnectionManager.OnCaseOpened"/>) or user Connect.
    /// </summary>
    AttachGaveUp,
}

/// <summary>
/// A paired AirPods device for the device list. <see cref="ModelName"/> comes from <see cref="Models"/>;
/// <see cref="Capabilities"/> is the live set while attached, else the set reported on the last attach plus the
/// model baseline. <see cref="StemModes"/> is the stored stem press-and-hold cycle (written on every attach).
/// </summary>
public sealed record KnownDevice(
    ulong Address, string Name, string? ModelNumber, string? ModelName, bool LinkUp, bool HasKeys, bool Attached, DeviceCapabilities Capabilities,
    IdleReason Idle, StemModes StemModes);

/// <summary>
/// Owns the single AAP session. Never pages: opens NTAPD only when Windows reports the ACL link up, attaches the
/// last-used device first, detaches quietly when the link drops, then attaches another pair whose link is already
/// up (last-used first) or waits for the next link-up event. While a pair's link is up and opening NTAPD or the
/// session fails, the attach is retried with bounded backoff (opening NTAPD never pages); after the last retry the
/// pair is marked <see cref="IdleReason.AttachGaveUp"/>.
/// All events fire on thread-pool (or CM notification) threads; handlers must not block.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    // Windows may keep the ACL up after a disconnect request (seen with the buds in a closed case); one more
    // request follows this wait.
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(3);
    // Attach retries while the link is up: the AACP L2CAP channel may refuse right after the ACL comes up while
    // Windows and the AirPods still set up A2DP/AVRCP/HFP (1-3 s), or longer while the buds sit in an open case.
    // 0.5 s catches the short race; doubling up to 8 s covers ~15.5 s in 5 retries. Each retry is one CreateFile
    // that fails fast and never pages. After that only a link event or a user Connect tries again.
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
    ];
    // After the fast retries: as long as Windows keeps the link up, WPods keeps trying (only a user Disconnect or
    // another attached pair holds a linked pair back). One fail-fast CreateFile per 30 s.
    private static readonly TimeSpan SlowRetryDelay = TimeSpan.FromSeconds(30);

    private readonly BluetoothLink _link = new();
    // Serializes every attach/detach decision; _session writes, _retries, _retrySerial and _disposed are guarded by it.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ulong, AttachRetry> _retries = [];
    // Guards _idle and _linkDownWaits (read by GetDevices and the CM notification thread without the gate).
    private readonly Lock _idleLock = new();
    private readonly Dictionary<ulong, IdleReason> _idle = [];
    private readonly Dictionary<ulong, TaskCompletionSource> _linkDownWaits = [];
    private AapSession? _session;
    private long _retrySerial;
    private bool _disposed;

    public ConnectionManager(SettingsStore settings)
    {
        Settings = settings;
    }

    public SettingsStore Settings { get; }

    /// <summary>The attached session, or null.</summary>
    public AapSession? Session => Volatile.Read(ref _session);

    public ulong LastUsed => Settings.LastUsed;

    /// <summary>A session was opened; raised before it reads its first packet, so subscribers miss nothing.</summary>
    public event Action<AapSession>? Attached;

    /// <summary>The session was closed (link down, user disconnect, switch or failure).</summary>
    public event Action<AapSession>? Detached;

    /// <summary>ACL link of a paired AirPods went up (true) or down (false).</summary>
    public event Action<ulong, bool>? LinkChanged;

    /// <summary>The result of <see cref="GetDevices"/> changed (pairing, link, attach, identity, keys, <see cref="KnownDevice.Idle"/>).</summary>
    public event Action? DevicesChanged;

    /// <summary>Opening NTAPD failed with a Win32 error other than 1167 (e.g. 32: another app holds the channel).</summary>
    public event Action<ulong, int>? AttachFailed;

    /// <summary>Applying the Hands-Free policy on attach failed.</summary>
    public event Action<ulong, Exception>? HandsFreeFailed;

    public void Start()
    {
        Log.Info("conn", $"start, lastUsed={Mac.Format(Settings.LastUsed)}");
        _link.LinkChanged += OnLinkChanged;
        _link.DevicesChanged += () => DevicesChanged?.Invoke();
        _link.Start();
        // The initial enumeration raises no events; subscribers created before Start need the first list.
        DevicesChanged?.Invoke();
        _ = Task.Run(() => OnLinkAsync(0));
    }

    public List<KnownDevice> GetDevices()
    {
        AapSession? session = Session;
        var list = new List<KnownDevice>();
        foreach (LinkDevice device in _link.Snapshot())
        {
            bool attached = session?.Address == device.Address;
            string? modelNumber = (attached ? session!.State.ModelNumber : null) ?? Settings.GetModel(device.Address)?.Number;
            AirPodsModel? model = modelNumber is null ? null : Models.FromModelNumber(modelNumber) ?? Models.Unknown;
            string name = attached ? session!.State.Name ?? device.Name : device.Name;
            DeviceCapabilities capabilities = attached
                ? session!.State.Capabilities
                : Settings.GetCapabilities(device.Address) | (model?.Capabilities ?? DeviceCapabilities.None);
            list.Add(new KnownDevice(
                device.Address, name, modelNumber, model?.Name, device.Up, Settings.HasKeys(device.Address), attached, capabilities,
                attached ? IdleReason.None : GetIdle(device.Address), Settings.GetStemModes(device.Address)));
        }
        return list;
    }

    /// <summary>
    /// Switches the session to <paramref name="address"/> when Windows already has its link up (detaching the
    /// current one first) and clears its <see cref="KnownDevice.Idle"/> reason. Returns the result of the immediate
    /// attempt; on failure a fresh retry series replaces any pending one (never cancelled), so the pair may still
    /// attach shortly after (<see cref="Attached"/>). False when the link is not up. Never pages.
    /// </summary>
    public async Task<bool> ConnectAsync(ulong address)
    {
        Log.Info("conn", $"ConnectAsync {Mac.Format(address)}: waiting for gate");
        long waitStart = Environment.TickCount64;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Log.Info("conn", $"ConnectAsync {Mac.Format(address)}: gate after {Environment.TickCount64 - waitStart} ms, session={Describe(_session)}, linkUp={_link.IsUp(address)}");
            if (_disposed)
            {
                return false;
            }
            SetIdle(address, IdleReason.None, "user connect");
            if (_session?.Address == address)
            {
                Log.Info("conn", "ConnectAsync -> true (already attached)");
                return true;
            }
            if (!_link.IsUp(address))
            {
                Log.Info("conn", "ConnectAsync -> false (link not up)");
                return false;
            }
            if (_session is not null)
            {
                await DetachLocked("switch").ConfigureAwait(false);
            }
            bool attached = AttachLocked(address, "user", newSeries: true);
            Log.Info("conn", attached ? "ConnectAsync -> true" : "ConnectAsync -> false (retries continue)");
            return attached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Mac-like full disconnect, optimistic: marks the pair <see cref="IdleReason.UserDisconnected"/> and detaches at
    /// once (<see cref="DevicesChanged"/>, <see cref="Detached"/>), then asks Windows to drop the Bluetooth audio
    /// connection; when the request fails or the link is still up 3 s later it asks once more. Completes when the
    /// link dropped or after the last wait. False when there is no session, or when both requests failed with an
    /// HRESULT error: the flag is then cleared and the pair attached again (its real state).
    /// </summary>
    public async Task<bool> DisconnectAsync()
    {
        ulong address;
        Task linkDown;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is not { } session)
            {
                Log.Info("conn", "DisconnectAsync -> false (no session)");
                return false;
            }
            address = session.Address;
            Log.Info("conn", $"DisconnectAsync {Mac.Format(address)}");
            _retries.Remove(address);
            linkDown = WaitForLinkDown(address);
            SetIdle(address, IdleReason.UserDisconnected, "user disconnect");
            await DetachLocked("user disconnect").ConfigureAwait(false);
            AttachOtherUpLocked(address);
        }
        finally
        {
            _gate.Release();
        }
        bool accepted = false;
        try
        {
            for (int attempt = 1; attempt <= 2 && _link.IsUp(address); attempt++)
            {
                int hr = await Task.Run(() => Native.wpn_bt_disconnect_audio(address)).ConfigureAwait(false);
                Log.Info("conn", $"DisconnectAsync {Mac.Format(address)}: disconnect audio attempt {attempt}/2 hr=0x{hr:X8}");
                if (hr < 0)
                {
                    continue;
                }
                accepted = true;
                try
                {
                    await linkDown.WaitAsync(DisconnectTimeout).ConfigureAwait(false);
                    break;
                }
                catch (TimeoutException)
                {
                    Log.Info("conn", $"DisconnectAsync {Mac.Format(address)}: link still up {DisconnectTimeout.TotalSeconds} s after attempt {attempt}");
                }
            }
        }
        finally
        {
            lock (_idleLock)
            {
                _linkDownWaits.Remove(address);
            }
        }
        bool linkUp = _link.IsUp(address);
        Log.Info("conn", $"DisconnectAsync {Mac.Format(address)}: final link {(linkUp ? "up" : "down")}, accepted by Windows={accepted}, idle={GetIdle(address)}");
        if (accepted || !linkUp)
        {
            return true;
        }
        // Both requests rejected: the pair is still connected; show its real state.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (GetIdle(address) == IdleReason.UserDisconnected)
            {
                SetIdle(address, IdleReason.None, "disconnect failed");
                if (_session is null && _link.IsUp(address))
                {
                    AttachLocked(address, "disconnect failed", newSeries: true);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        Log.Warn("conn", $"DisconnectAsync {Mac.Format(address)} -> false (both requests failed)");
        return false;
    }

    /// <summary>
    /// Saves host settings (allowed while disconnected). A Hands-Free change is applied at once when that device
    /// is attached; throws if Windows rejects it (the setting stays saved).
    /// </summary>
    public async Task SetHostSettingsAsync(ulong address, HostSettings settings)
    {
        bool handsFreeChanged = Settings.Get(address).DisableHandsFree != settings.DisableHandsFree;
        Log.Info("conn", $"host settings {Mac.Format(address)}: disableHandsFree={settings.DisableHandsFree} lowLatency={settings.LowLatency} micMode={settings.MicMode} duckPercent={settings.DuckPercent}");
        Settings.Set(address, settings);
        if (handsFreeChanged && Session?.Address == address)
        {
            await ApplyHandsFreeAsync(address, settings.DisableHandsFree).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Log.Info("conn", "dispose");
        _link.Dispose();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _retries.Clear();
            if (_session is not null)
            {
                await DetachLocked("exit").ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Called on the CM notification thread: relay, then decide off that thread.
    private void OnLinkChanged(ulong address, bool up)
    {
        Log.Info("conn", $"link {Mac.Format(address)} {(up ? "up" : "down")}");
        if (!up)
        {
            lock (_idleLock)
            {
                if (_linkDownWaits.Remove(address, out TaskCompletionSource? wait))
                {
                    wait.TrySetResult();
                }
            }
        }
        LinkChanged?.Invoke(address, up);
        DevicesChanged?.Invoke();
        _ = Task.Run(() => OnLinkAsync(address));
    }

    // Re-reads the current link state rather than trusting event order. address 0 = startup.
    private async Task OnLinkAsync(ulong address)
    {
        long waitStart = Environment.TickCount64;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            bool up = address != 0 && _link.IsUp(address);
            Log.Info("conn", $"link decision for {(address == 0 ? "startup" : Mac.Format(address))}: gate after {Environment.TickCount64 - waitStart} ms, linkUp now={(address == 0 ? "n/a" : up.ToString())}, session={Describe(_session)}");
            if (address != 0 && !up)
            {
                _retries.Remove(address);
                SetIdle(address, IdleReason.None, "link down");
                if (_session?.Address == address)
                {
                    await DetachLocked("link down").ConfigureAwait(false);
                }
                AttachOtherUpLocked(address);
                return;
            }
            if (address != 0)
            {
                // Every link-up event starts a new attach series for that pair.
                _retries.Remove(address);
                if (GetIdle(address) == IdleReason.AttachGaveUp)
                {
                    SetIdle(address, IdleReason.None, "link up again");
                }
            }
            if (_session is not null)
            {
                Log.Info("conn", "link decision: a session is attached, nothing to do");
                return;
            }
            ulong last = Settings.LastUsed;
            if (CanAutoAttach(last) && AttachLocked(last, address == 0 ? "startup last-used" : "link-up last-used", newSeries: false))
            {
                return;
            }
            ulong candidate = address != 0 ? address : _link.FirstUp();
            if (candidate != last && CanAutoAttach(candidate))
            {
                AttachLocked(candidate, address == 0 ? "startup first-up" : "link-up", newSeries: false);
            }
            else
            {
                Log.Info("conn", $"link decision: nothing more to attach (candidate={Mac.Format(candidate)} idle={GetIdle(candidate)}, lastUsed={Mac.Format(last)} idle={GetIdle(last)}, retry pending={_retries.ContainsKey(candidate) || _retries.ContainsKey(last)})");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Link up and nothing (user disconnect, gave up) holds the pair back.
    private bool CanAutoAttach(ulong address) => address != 0 && _link.IsUp(address) && GetIdle(address) == IdleReason.None;

    // A retry series continues while the link is up, unless the user disconnected the pair.
    private bool CanRetry(ulong address) => _link.IsUp(address) && GetIdle(address) != IdleReason.UserDisconnected;

    /// <summary>
    /// The pair's case lid opened or a bud left the case (<see cref="Nearby.NearbyService.CaseOpened"/>): as on iPhone
    /// and Mac, that means the user wants them. A pair held back (<see cref="IdleReason.UserDisconnected"/>,
    /// <see cref="IdleReason.AttachGaveUp"/>) whose Windows link is up is released and attached like a link-up, with
    /// retries. Never pages. Called on the BLE thread; returns at once.
    /// </summary>
    public void OnCaseOpened(ulong address)
    {
        if (GetIdle(address) != IdleReason.None && _link.IsUp(address))
        {
            _ = Task.Run(() => ReleaseOnCaseOpenedAsync(address));
        }
    }

    private async Task ReleaseOnCaseOpenedAsync(ulong address)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || GetIdle(address) == IdleReason.None || !_link.IsUp(address))
            {
                return;
            }
            SetIdle(address, IdleReason.None, "lid opened or bud out of case");
        }
        finally
        {
            _gate.Release();
        }
        await OnLinkAsync(address).ConfigureAwait(false);
    }

    // One attempt now; on failure the next retry is scheduled. newSeries restarts the retry count.
    private bool AttachLocked(ulong address, string reason, bool newSeries)
    {
        if (newSeries)
        {
            _retries.Remove(address);
        }
        if (TryAttachLocked(address, reason))
        {
            return true;
        }
        ScheduleRetryLocked(address);
        return false;
    }

    // Schedules the next retry for a pair whose link is up (replacing any pending one); after the fast series the
    // pair is marked AttachGaveUp and retried slowly.
    private void ScheduleRetryLocked(ulong address)
    {
        if (_disposed || !CanRetry(address))
        {
            _retries.Remove(address);
            Log.Info("conn", $"attach {Mac.Format(address)}: no retry (linkUp={_link.IsUp(address)}, idle={GetIdle(address)})");
            return;
        }
        if (!_retries.TryGetValue(address, out AttachRetry? retry))
        {
            retry = new AttachRetry();
            _retries[address] = retry;
        }
        TimeSpan delay;
        if (retry.Attempts >= RetryDelays.Length)
        {
            if (GetIdle(address) != IdleReason.AttachGaveUp)
            {
                Log.Warn("conn", $"attach {Mac.Format(address)}: all {RetryDelays.Length} fast retries failed with the link up; retrying every {SlowRetryDelay.TotalSeconds} s");
                SetIdle(address, IdleReason.AttachGaveUp, "retries exhausted");
                AttachOtherUpLocked(address);
            }
            delay = SlowRetryDelay;
        }
        else
        {
            delay = RetryDelays[retry.Attempts++];
        }
        retry.Serial = ++_retrySerial;
        Log.Info("conn", $"attach {Mac.Format(address)}: retry {retry.Attempts}/{RetryDelays.Length} in {delay.TotalMilliseconds} ms");
        _ = RetryAsync(address, retry.Serial, delay);
    }

    private async Task RetryAsync(ulong address, long serial, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_retries.TryGetValue(address, out AttachRetry? retry) || retry.Serial != serial)
            {
                Log.Info("conn", $"attach {Mac.Format(address)}: retry superseded");
                return;
            }
            string reason = $"retry {retry.Attempts}/{RetryDelays.Length}";
            if (_session is not null)
            {
                Log.Info("conn", $"attach {Mac.Format(address)}: {reason} skipped, session={Describe(_session)}");
                return;
            }
            if (_disposed || !CanRetry(address))
            {
                Log.Info("conn", $"attach {Mac.Format(address)}: {reason} stopped (linkUp={_link.IsUp(address)}, idle={GetIdle(address)})");
                _retries.Remove(address);
                return;
            }
            if (!TryAttachLocked(address, reason))
            {
                ScheduleRetryLocked(address);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryAttachLocked(ulong address, string reason)
    {
        if (_disposed)
        {
            return false;
        }
        if (_link.GetPath(address) is not { } path)
        {
            Log.Warn("conn", $"attach {Mac.Format(address)} ({reason}): no NTAPD interface");
            return false;
        }
        SafeFileHandle handle = Native.OpenNtapd(path, out int error);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            Log.Warn("conn", $"attach {Mac.Format(address)} ({reason}): open failed, Win32 error {error}, linkUp={_link.IsUp(address)}");
            if (error != Native.ErrorDeviceNotConnected)
            {
                AttachFailed?.Invoke(address, error);
            }
            else
            {
                // The driver sees no ACL: correct a stale "up" so the next real link-up raises an event again.
                _link.Recheck(address);
            }
            return false;
        }
        Log.Info("conn", $"attach {Mac.Format(address)} ({reason}): opened");
        var session = new AapSession(address, handle, Settings);
        session.Changed += change =>
        {
            if ((change & DeviceChange.Identity) != 0)
            {
                SaveModel(session);
            }
            if ((change & (DeviceChange.Identity | DeviceChange.Keys | DeviceChange.Capabilities)) != 0)
            {
                DevicesChanged?.Invoke();
            }
        };
        Volatile.Write(ref _session, session);
        Settings.LastUsed = address;
        SetIdle(address, IdleReason.None, "attached");
        Attached?.Invoke(session);
        session.Start();
        _ = WatchAsync(session);
        _ = ApplyHandsFreeOnAttachAsync(address);
        DevicesChanged?.Invoke();
        return true;
    }

    private async Task DetachLocked(string reason)
    {
        AapSession session = _session!;
        Log.Info("conn", $"detach {Mac.Format(session.Address)} ({reason}), status={session.State.Status}");
        Volatile.Write(ref _session, null);
        await session.DisposeAsync().ConfigureAwait(false);
        Detached?.Invoke(session);
        DevicesChanged?.Invoke();
    }

    // Session ended on its own (read error, 1167 or end of stream). Link still up -> retry the same pair (the count
    // restarts only when the session had reached Ready, so open-then-fail loops stay bounded); else fallback.
    private async Task WatchAsync(AapSession session)
    {
        SessionEnd end = await session.Completion.ConfigureAwait(false);
        ulong address = session.Address;
        Log.Info("conn", $"session {Mac.Format(address)} ended: {end}, status={session.State.Status}");
        if (end == SessionEnd.Closed)
        {
            return;
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session != session)
            {
                Log.Info("conn", "session end: already detached");
                return;
            }
            bool ready = session.State.Status == SessionStatus.Ready;
            await DetachLocked(end == SessionEnd.LinkLost ? "read error 1167" : "read error").ConfigureAwait(false);
            if (CanAutoAttach(address))
            {
                Log.Info("conn", $"session end with the link still up (end={end}, wasReady={ready}): retrying attach");
                if (ready)
                {
                    _retries.Remove(address);
                }
                ScheduleRetryLocked(address);
                return;
            }
            Log.Info("conn", $"session end: no retry (linkUp={_link.IsUp(address)}, idle={GetIdle(address)}), trying other pairs");
            AttachOtherUpLocked(address);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Rule 2 again after a detach: attach another pair whose link is already up (last-used first), skipping pairs
    // the user disconnected or WPods gave up on. Never pages.
    private void AttachOtherUpLocked(ulong excluded)
    {
        if (_session is not null || _disposed)
        {
            return;
        }
        ulong last = Settings.LastUsed;
        if (last != excluded && CanAutoAttach(last) && AttachLocked(last, "fallback last-used", newSeries: true))
        {
            return;
        }
        foreach (LinkDevice device in _link.Snapshot())
        {
            if (device.Address != excluded && device.Address != last && CanAutoAttach(device.Address)
                && AttachLocked(device.Address, "fallback", newSeries: true))
            {
                return;
            }
        }
        Log.Info("conn", $"fallback: no other pair to attach (excluded {Mac.Format(excluded)})");
    }

    // Persists the 0x1D model so the device list shows it while the pair is not attached.
    private void SaveModel(AapSession session)
    {
        if (session.State.ModelNumber is not { } number || session.State.ModelName is not { } name)
        {
            return;
        }
        try
        {
            Settings.SetModel(session.Address, new DeviceModel(number, name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Cosmetic data only; retried on the next attach.
            Log.Warn("conn", $"model not saved: {e.Message}");
        }
    }

    private async Task ApplyHandsFreeOnAttachAsync(ulong address)
    {
        try
        {
            await ApplyHandsFreeAsync(address, Settings.Get(address).DisableHandsFree).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Warn("conn", $"hands-free on attach failed: {e.Message}");
            HandsFreeFailed?.Invoke(address, e);
        }
    }

    // One retry after 750 ms for the transient errors Windows reports right after a connect.
    private static async Task ApplyHandsFreeAsync(ulong address, bool disable)
    {
        Log.Info("conn", $"hands-free {Mac.Format(address)}: {(disable ? "disable" : "enable")}");
        try
        {
            await Task.Run(() => HandsFree.SetEnabled(address, !disable)).ConfigureAwait(false);
        }
        catch (Win32Exception e) when (e.NativeErrorCode is Native.ErrorServiceDoesNotExist or Native.ErrorNotFound or Native.ErrorInvalidParameter)
        {
            Log.Info("conn", $"hands-free: error {e.NativeErrorCode}, retrying in 750 ms");
            await Task.Delay(750).ConfigureAwait(false);
            await Task.Run(() => HandsFree.SetEnabled(address, !disable)).ConfigureAwait(false);
        }
        Log.Info("conn", "hands-free applied");
    }

    private IdleReason GetIdle(ulong address)
    {
        lock (_idleLock)
        {
            return _idle.GetValueOrDefault(address);
        }
    }

    // Logs and raises DevicesChanged when the reason changed.
    private void SetIdle(ulong address, IdleReason reason, string why)
    {
        IdleReason previous;
        lock (_idleLock)
        {
            previous = _idle.GetValueOrDefault(address);
            if (previous == reason)
            {
                return;
            }
            if (reason == IdleReason.None)
            {
                _idle.Remove(address);
            }
            else
            {
                _idle[address] = reason;
            }
        }
        Log.Info("conn", $"{Mac.Format(address)}: idle {previous} -> {reason} ({why})");
        DevicesChanged?.Invoke();
    }

    // Completed by OnLinkChanged when the pair's link goes down; registered before the disconnect request.
    private Task WaitForLinkDown(ulong address)
    {
        lock (_idleLock)
        {
            if (!_linkDownWaits.TryGetValue(address, out TaskCompletionSource? wait))
            {
                wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _linkDownWaits[address] = wait;
            }
            return wait.Task;
        }
    }

    private static string Describe(AapSession? session) => session is null ? "none" : Mac.Format(session.Address);

    // Attach retry series of one pair; guarded by _gate.
    private sealed class AttachRetry
    {
        public int Attempts { get; set; }
        // Identifies the pending delay; a newer schedule or a reset makes older delays no-ops.
        public long Serial { get; set; }
    }
}
