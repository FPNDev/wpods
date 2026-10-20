using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace WPods.Core;

/// <summary>A paired AirPods device for the device list.</summary>
public sealed record KnownDevice(ulong Address, string Name, string? ModelNumber, string? ModelName, bool LinkUp, bool HasKeys, bool Attached);

/// <summary>
/// Owns the single AAP session. Never pages: opens NTAPD only when Windows reports the ACL link up, attaches the
/// last-used device first, detaches quietly when the link drops, then attaches another pair whose link is already
/// up (last-used first) or waits for the next link-up event.
/// All events fire on thread-pool (or CM notification) threads; handlers must not block.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(3);
    private const int MaxReopenAttempts = 3;

    private readonly BluetoothLink _link = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AapSession? _session;
    private AapSession? _disconnecting;
    private int _generation;
    private int _reopenAttempts;

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

    /// <summary>The result of <see cref="GetDevices"/> changed (pairing, link, attach, identity, keys).</summary>
    public event Action? DevicesChanged;

    /// <summary>Opening NTAPD failed with a Win32 error other than 1167 (e.g. 32: another app holds the channel).</summary>
    public event Action<ulong, int>? AttachFailed;

    /// <summary>Applying the Hands-Free policy on attach failed.</summary>
    public event Action<ulong, Exception>? HandsFreeFailed;

    public void Start()
    {
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
            DeviceModel? stored = Settings.GetModel(device.Address);
            string? modelNumber = stored?.Number;
            string? modelName = stored?.Name;
            if (attached && session!.State.ModelNumber is { } liveNumber)
            {
                modelNumber = liveNumber;
                modelName = session.State.ModelName;
            }
            string name = attached ? session!.State.Name ?? device.Name : device.Name;
            list.Add(new KnownDevice(device.Address, name, modelNumber, modelName, device.Up, Settings.HasKeys(device.Address), attached));
        }
        return list;
    }

    /// <summary>
    /// Switches the session to <paramref name="address"/> when Windows already has its link up (detaching the
    /// current one first). False when the link is not up or opening failed. Never pages.
    /// </summary>
    public async Task<bool> ConnectAsync(ulong address)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _generation++;
            _reopenAttempts = 0;
            if (_session?.Address == address)
            {
                return true;
            }
            if (!_link.IsUp(address))
            {
                return false;
            }
            if (_session is not null)
            {
                await DetachLocked().ConfigureAwait(false);
            }
            return TryAttachLocked(address);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Mac-like full disconnect: asks Windows to drop the device's Bluetooth audio connection, then detaches when
    /// the link drops (or after 3 s). Returns false when there is no session or Windows rejected the request.
    /// </summary>
    public async Task<bool> DisconnectAsync()
    {
        AapSession? session;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            session = _session;
            if (session is null)
            {
                return false;
            }
            _generation++;
            _disconnecting = session;
        }
        finally
        {
            _gate.Release();
        }
        int hr = await Task.Run(() => Native.wpn_bt_disconnect_audio(session.Address)).ConfigureAwait(false);
        try
        {
            await session.Completion.WaitAsync(DisconnectTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Link still up: detach anyway (closing the AAP channel lets the link drop).
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session == session)
            {
                await DetachLocked().ConfigureAwait(false);
                AttachOtherUpLocked(session.Address);
            }
            _disconnecting = null;
        }
        finally
        {
            _gate.Release();
        }
        return hr >= 0;
    }

    /// <summary>
    /// Saves host settings (allowed while disconnected). A Hands-Free change is applied at once when that device
    /// is attached; throws if Windows rejects it (the setting stays saved).
    /// </summary>
    public async Task SetHostSettingsAsync(ulong address, HostSettings settings)
    {
        bool handsFreeChanged = Settings.Get(address).DisableHandsFree != settings.DisableHandsFree;
        Settings.Set(address, settings);
        if (handsFreeChanged && Session?.Address == address)
        {
            await ApplyHandsFreeAsync(address, settings.DisableHandsFree).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _link.Dispose();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _generation++;
            if (_session is not null)
            {
                await DetachLocked().ConfigureAwait(false);
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
        LinkChanged?.Invoke(address, up);
        DevicesChanged?.Invoke();
        _ = Task.Run(() => OnLinkAsync(address));
    }

    // Re-reads the current link state rather than trusting event order. address 0 = startup.
    private async Task OnLinkAsync(ulong address)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (address != 0 && !_link.IsUp(address))
            {
                if (_session?.Address == address)
                {
                    await DetachLocked().ConfigureAwait(false);
                }
                if (_session is null)
                {
                    AttachOtherUpLocked(address);
                }
                return;
            }
            if (_session is not null)
            {
                return;
            }
            ulong last = Settings.LastUsed;
            if (last != 0 && _link.IsUp(last) && TryAttachLocked(last))
            {
                return;
            }
            ulong candidate = address != 0 ? address : _link.FirstUp();
            if (candidate != 0 && candidate != last)
            {
                TryAttachLocked(candidate);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryAttachLocked(ulong address)
    {
        if (_link.GetPath(address) is not { } path)
        {
            return false;
        }
        SafeFileHandle handle = Native.OpenNtapd(path, out int error);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            if (error != Native.ErrorDeviceNotConnected)
            {
                AttachFailed?.Invoke(address, error);
            }
            return false;
        }
        var session = new AapSession(address, handle, Settings);
        session.Changed += change =>
        {
            if ((change & DeviceChange.Identity) != 0)
            {
                SaveModel(session);
            }
            if ((change & (DeviceChange.Identity | DeviceChange.Keys)) != 0)
            {
                DevicesChanged?.Invoke();
            }
        };
        Volatile.Write(ref _session, session);
        Settings.LastUsed = address;
        Attached?.Invoke(session);
        session.Start();
        _ = WatchAsync(session);
        _ = ApplyHandsFreeOnAttachAsync(address);
        DevicesChanged?.Invoke();
        return true;
    }

    private async Task DetachLocked()
    {
        AapSession session = _session!;
        Volatile.Write(ref _session, null);
        await session.DisposeAsync().ConfigureAwait(false);
        Detached?.Invoke(session);
        DevicesChanged?.Invoke();
    }

    // Session ended on its own: 1167 -> quiet; other failure while the link is still up -> bounded reopen
    // of the same device (Windows briefly taking over the audio profile), never while the link is down.
    private async Task WatchAsync(AapSession session)
    {
        SessionEnd end = await session.Completion.ConfigureAwait(false);
        if (end == SessionEnd.Closed)
        {
            return;
        }
        ulong address = session.Address;
        int generation;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session != session)
            {
                return;
            }
            await DetachLocked().ConfigureAwait(false);
            if (session == _disconnecting)
            {
                return;
            }
            if (end == SessionEnd.LinkLost || !_link.IsUp(address))
            {
                AttachOtherUpLocked(address);
                return;
            }
            if (session.State.Status == SessionStatus.Ready)
            {
                _reopenAttempts = 0;
            }
            generation = _generation;
        }
        finally
        {
            _gate.Release();
        }
        while (true)
        {
            int delay;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_reopenAttempts >= MaxReopenAttempts || generation != _generation || _session is not null || !_link.IsUp(address))
                {
                    return;
                }
                delay = 1000 << _reopenAttempts++;
            }
            finally
            {
                _gate.Release();
            }
            await Task.Delay(delay).ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (generation != _generation || _session is not null || !_link.IsUp(address) || TryAttachLocked(address))
                {
                    return;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    // Rule 2 again after a detach: attach another pair whose link is already up (last-used first). Never pages.
    private void AttachOtherUpLocked(ulong excluded)
    {
        ulong last = Settings.LastUsed;
        if (last != excluded && last != 0 && _link.IsUp(last) && TryAttachLocked(last))
        {
            return;
        }
        foreach (LinkDevice device in _link.Snapshot())
        {
            if (device.Up && device.Address != excluded && device.Address != last && TryAttachLocked(device.Address))
            {
                return;
            }
        }
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
            HandsFreeFailed?.Invoke(address, e);
        }
    }

    // One retry after 750 ms for the transient errors Windows reports right after a connect.
    private static async Task ApplyHandsFreeAsync(ulong address, bool disable)
    {
        try
        {
            await Task.Run(() => HandsFree.SetEnabled(address, !disable)).ConfigureAwait(false);
        }
        catch (Win32Exception e) when (e.NativeErrorCode is Native.ErrorServiceDoesNotExist or Native.ErrorNotFound or Native.ErrorInvalidParameter)
        {
            await Task.Delay(750).ConfigureAwait(false);
            await Task.Run(() => HandsFree.SetEnabled(address, !disable)).ConfigureAwait(false);
        }
    }
}
