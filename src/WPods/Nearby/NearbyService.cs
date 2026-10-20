using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WPods.Core;

namespace WPods.Nearby;

public enum OfferState
{
    Offer,
    Connecting,
    Connected,
    /// <summary>Windows rejected the connect request, no link within 10 s, or attaching failed; Retry = connect again.</summary>
    Failed,
}

/// <summary>One nearby pair offered for connecting.</summary>
public sealed record NearbyOffer(ulong Address, string Name, AirPodsModel Model, PodBatteries Batteries, OfferState State);

/// <summary>
/// Mac-like nearby popup logic. Always scans passively; an advert counts only when its address resolves with the IRK
/// of a paired AirPods that has proximity keys and whose link to Windows is down. An offer appears when that pair's lid
/// opens (first advert with the lid open after closed or unseen) with at least one bud in the case; closing the lid
/// removes it unless it is connecting. <see cref="Dismiss"/> removes it, and only the next opening shows it again.
/// 45 s without adverts = gone (offer removed, lid state forgotten).
/// <see cref="OffersChanged"/> fires on thread-pool threads (BLE, CM notification, timer or connect continuation),
/// only on a real change; handlers must not block and should read <see cref="Offers"/>.
/// </summary>
public sealed class NearbyService : IDisposable
{
    private const long GoneMs = 45_000;
    private const int MaxResolvedAddresses = 256;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly ConnectionManager _connections;
    // _scanLock is never taken while holding _lock: wpn_ble_stop waits for running callbacks, which take _lock.
    private readonly Lock _lock = new();
    private readonly Lock _scanLock = new();
    private readonly Lock _refreshLock = new();
    private readonly Dictionary<ulong, Pair> _pairs = [];
    // Advertising address -> pair, null = not ours. RPAs rotate about every 15 min, so hits stay cached.
    private readonly Dictionary<ulong, Pair?> _resolved = [];
    private readonly System.Threading.Timer _expiry;
    private GCHandle _self;
    private nint _radioNotify;
    private bool _expiryArmed;
    private bool _disposed;
    private int _generation;
    private NearbyOffer[] _offers = [];

    public NearbyService(ConnectionManager connections)
    {
        _connections = connections;
        _expiry = new System.Threading.Timer(static state => ((NearbyService)state!).OnExpiry(), this, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Current offers (immutable snapshot).</summary>
    public IReadOnlyList<NearbyOffer> Offers => Volatile.Read(ref _offers);

    public event Action? OffersChanged;

    public unsafe void Start()
    {
        _self = GCHandle.Alloc(this);
        _connections.LinkChanged += OnLinkChanged;
        _connections.DevicesChanged += Refresh;
        Refresh();
        // A radio that is switched back on (or plugged in) re-arrives as an interface; the helper's watcher has stopped by then.
        _radioNotify = Native.RegisterInterfaceNotification(Native.RadioInterface, GCHandle.ToIntPtr(_self), &OnRadioNotify);
        RestartScan();
    }

    /// <summary>
    /// User clicked Connect (or Retry): asks Windows to connect audio, waits up to 10 s for the link, then switches the
    /// session to this pair. The only place that pages the AirPods.
    /// </summary>
    public async Task ConnectAsync(ulong address)
    {
        var link = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Pair? pair;
        lock (_lock)
        {
            if (!_pairs.TryGetValue(address, out pair) || pair.Offer is not { State: OfferState.Offer or OfferState.Failed } offer)
            {
                return;
            }
            pair.Link = link;
            pair.Offer = offer with { State = OfferState.Connecting };
            Publish();
        }
        OffersChanged?.Invoke();
        OfferState result = OfferState.Failed;
        try
        {
            int hr = await Task.Run(() => NearbyNative.wpn_bt_connect_audio(address)).ConfigureAwait(false);
            if (hr >= 0)
            {
                await link.Task.WaitAsync(ConnectTimeout).ConfigureAwait(false);
                if (await _connections.ConnectAsync(address).ConfigureAwait(false))
                {
                    result = OfferState.Connected;
                }
            }
        }
        catch (TimeoutException)
        {
            // No link within 10 s.
        }
        catch (TaskCanceledException)
        {
            // Cancelled or dismissed; the offer was already updated.
        }
        bool changed;
        lock (_lock)
        {
            changed = pair.Link == link && pair.Offer is not null;
            if (changed)
            {
                pair.Link = null;
                pair.Offer = pair.Offer! with { State = result };
                Publish();
            }
        }
        if (changed)
        {
            OffersChanged?.Invoke();
        }
    }

    /// <summary>Stops waiting for every connect in progress; those offers go back to <see cref="OfferState.Offer"/>.</summary>
    public void CancelConnect()
    {
        bool changed = false;
        lock (_lock)
        {
            foreach (Pair pair in _pairs.Values)
            {
                if (pair.Link is not null && pair.Offer is { } offer)
                {
                    pair.CancelConnect();
                    pair.Offer = offer with { State = OfferState.Offer };
                    changed = true;
                }
            }
            if (changed)
            {
                Publish();
            }
        }
        if (changed)
        {
            OffersChanged?.Invoke();
        }
    }

    /// <summary>"Not now" (or the UI closing a Connected offer): hides the offer until the lid closes and opens again.</summary>
    public void Dismiss(ulong address)
    {
        bool changed;
        lock (_lock)
        {
            changed = _pairs.TryGetValue(address, out Pair? pair) && pair.Offer is not null;
            if (changed)
            {
                pair!.CancelConnect();
                pair.Offer = null;
                Publish();
            }
        }
        if (changed)
        {
            OffersChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        _connections.LinkChanged -= OnLinkChanged;
        _connections.DevicesChanged -= Refresh;
        if (_radioNotify != 0)
        {
            Native.CM_Unregister_Notification(_radioNotify);
        }
        lock (_scanLock)
        {
            _disposed = true;
            NearbyNative.wpn_ble_stop();
        }
        lock (_lock)
        {
            _expiry.Dispose();
            foreach (Pair pair in _pairs.Values)
            {
                pair.CancelConnect();
                pair.Dispose();
            }
            _pairs.Clear();
            _resolved.Clear();
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    // Helper DLL: serialized calls on WinRT thread-pool threads.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnAdvert(nint context, ulong address, byte addressType, short rssi, byte* data, uint length)
    {
        if (GCHandle.FromIntPtr(context).Target is NearbyService self)
        {
            self.HandleAdvert(address, addressType, new ReadOnlySpan<byte>(data, (int)length));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe uint OnRadioNotify(nint notify, nint context, int action, byte* data, uint size)
    {
        if (action == Native.CmNotifyActionInterfaceArrival && GCHandle.FromIntPtr(context).Target is NearbyService self)
        {
            ThreadPool.QueueUserWorkItem(static s => s.RestartScan(), self, preferLocal: false);
        }
        return 0;
    }

    // The helper's watcher stops silently when it aborts (e.g. radio off); stop + start revives it.
    // A failed start (no radio) is retried on the next radio arrival or link-up.
    private unsafe void RestartScan()
    {
        lock (_scanLock)
        {
            if (_disposed)
            {
                return;
            }
            NearbyNative.wpn_ble_stop();
            NearbyNative.wpn_ble_start(&OnAdvert, GCHandle.ToIntPtr(_self));
        }
    }

    private void HandleAdvert(ulong address, byte addressType, ReadOnlySpan<byte> data)
    {
        if (!Proximity.IsValid(address, addressType, data))
        {
            return;
        }
        bool changed;
        lock (_lock)
        {
            if (!_resolved.TryGetValue(address, out Pair? pair))
            {
                pair = Resolve(address);
                if (_resolved.Count >= MaxResolvedAddresses)
                {
                    _resolved.Clear();
                }
                _resolved.Add(address, pair);
            }
            if (pair is null)
            {
                return;
            }
            changed = Update(pair, data);
            if (changed)
            {
                Publish();
            }
        }
        if (changed)
        {
            OffersChanged?.Invoke();
        }
    }

    private Pair? Resolve(ulong address)
    {
        foreach (Pair pair in _pairs.Values)
        {
            if (Proximity.Resolves(pair.IrkCipher, address))
            {
                return pair;
            }
        }
        return null;
    }

    // Under _lock.
    private bool Update(Pair pair, ReadOnlySpan<byte> data)
    {
        pair.LastSeen = Environment.TickCount64;
        if (!pair.Present)
        {
            pair.Present = true;
            // Every other present pair expires earlier, so an armed timer is already due first.
            if (!_expiryArmed)
            {
                _expiryArmed = true;
                _expiry.Change(GoneMs, Timeout.Infinite);
            }
        }
        bool wasOpen = pair.LidOpen;
        pair.LidOpen = Proximity.IsLidOpen(data);
        if (!pair.LidOpen && pair.Offer is { State: not OfferState.Connecting })
        {
            pair.Offer = null;
            return true;
        }
        if (pair.LinkUp)
        {
            return false;
        }
        if (pair.Offer is not { } offer)
        {
            if (!pair.LidOpen || wasOpen || !Proximity.IsBudInCase(data))
            {
                return false;
            }
            AirPodsModel model = Models.FromProximityId(Proximity.ModelId(data)) ?? Models.FromModelNumber(pair.ModelNumber) ?? Models.Unknown;
            PodBatteries initial = Proximity.ReadBatteries(data, pair.EncCipher, (model.Capabilities & ModelCapabilities.SingleBattery) != 0, default);
            pair.Offer = new NearbyOffer(pair.Address, pair.Name, model, initial, OfferState.Offer);
            return true;
        }
        PodBatteries batteries = Proximity.ReadBatteries(data, pair.EncCipher, (offer.Model.Capabilities & ModelCapabilities.SingleBattery) != 0, offer.Batteries);
        if (batteries == offer.Batteries)
        {
            return false;
        }
        pair.Offer = offer with { Batteries = batteries };
        return true;
    }

    private void OnExpiry()
    {
        bool changed = false;
        lock (_lock)
        {
            long now = Environment.TickCount64;
            long next = long.MaxValue;
            foreach (Pair pair in _pairs.Values)
            {
                if (!pair.Present)
                {
                    continue;
                }
                long due = pair.LastSeen + GoneMs;
                if (due > now)
                {
                    next = Math.Min(next, due);
                    continue;
                }
                pair.Present = false;
                pair.LidOpen = false;
                if (pair.Offer is { State: OfferState.Offer or OfferState.Failed })
                {
                    pair.Offer = null;
                    changed = true;
                }
            }
            _expiryArmed = next != long.MaxValue;
            if (_expiryArmed)
            {
                _expiry.Change(next - now, Timeout.Infinite);
            }
            if (changed)
            {
                Publish();
            }
        }
        if (changed)
        {
            OffersChanged?.Invoke();
        }
    }

    // CM notification thread.
    private void OnLinkChanged(ulong address, bool up)
    {
        bool changed;
        lock (_lock)
        {
            changed = _pairs.TryGetValue(address, out Pair? pair) && SetLinkUp(pair, up);
            if (changed)
            {
                Publish();
            }
        }
        if (changed)
        {
            OffersChanged?.Invoke();
        }
        // No radio-state event exists in Core; a link coming up also means the radio is on.
        if (up)
        {
            ThreadPool.QueueUserWorkItem(static s => s.RestartScan(), this, preferLocal: false);
        }
    }

    // Under _lock. A link that comes up ends a connect wait; an idle or failed offer disappears (connected to Windows).
    private static bool SetLinkUp(Pair pair, bool up)
    {
        pair.LinkUp = up;
        if (!up)
        {
            return false;
        }
        pair.Link?.TrySetResult();
        if (pair.Offer is { State: OfferState.Offer or OfferState.Failed })
        {
            pair.Offer = null;
            return true;
        }
        return false;
    }

    // Pairs = paired AirPods with proximity keys. Serialized so an older device list never overwrites a newer one.
    private void Refresh()
    {
        bool changed = false;
        lock (_refreshLock)
        {
            List<KnownDevice> devices = _connections.GetDevices();
            lock (_lock)
            {
                int generation = ++_generation;
                bool keysChanged = false;
                foreach (KnownDevice device in devices)
                {
                    if (_connections.Settings.GetKeys(device.Address) is not { } keys)
                    {
                        continue;
                    }
                    if (!_pairs.TryGetValue(device.Address, out Pair? pair))
                    {
                        pair = new Pair(device.Address);
                        _pairs.Add(device.Address, pair);
                    }
                    if (pair.SetKeys(keys))
                    {
                        keysChanged = true;
                    }
                    pair.Generation = generation;
                    pair.ModelNumber = device.ModelNumber;
                    if (pair.Name != device.Name)
                    {
                        pair.Name = device.Name;
                        if (pair.Offer is { } offer)
                        {
                            pair.Offer = offer with { Name = device.Name };
                            changed = true;
                        }
                    }
                    changed |= SetLinkUp(pair, device.LinkUp);
                }
                foreach (Pair pair in _pairs.Values)
                {
                    if (pair.Generation != generation)
                    {
                        _pairs.Remove(pair.Address);
                        pair.CancelConnect();
                        pair.Dispose();
                        changed |= pair.Offer is not null;
                        keysChanged = true;
                    }
                }
                if (keysChanged)
                {
                    _resolved.Clear();
                }
                if (changed)
                {
                    Publish();
                }
            }
        }
        if (changed)
        {
            OffersChanged?.Invoke();
        }
    }

    // Under _lock.
    private void Publish()
    {
        int count = 0;
        foreach (Pair pair in _pairs.Values)
        {
            if (pair.Offer is not null)
            {
                count++;
            }
        }
        var offers = new NearbyOffer[count];
        int i = 0;
        foreach (Pair pair in _pairs.Values)
        {
            if (pair.Offer is { } offer)
            {
                offers[i++] = offer;
            }
        }
        Volatile.Write(ref _offers, offers);
    }

    // All members are used under _lock.
    private sealed class Pair(ulong address) : IDisposable
    {
        private ProximityKeys? _keys;

        public ulong Address { get; } = address;
        public Aes IrkCipher { get; private set; } = null!;
        public Aes EncCipher { get; private set; } = null!;
        public string Name { get; set; } = "";
        public string? ModelNumber { get; set; }
        public bool LinkUp { get; set; }
        public bool Present { get; set; }
        public bool LidOpen { get; set; }
        public long LastSeen { get; set; }
        public int Generation { get; set; }
        public NearbyOffer? Offer { get; set; }
        /// <summary>Completed when the link comes up during a connect; cancelled to abandon the connect.</summary>
        public TaskCompletionSource? Link { get; set; }

        /// <summary>False when <paramref name="keys"/> is the instance already in use (SettingsStore replaces it on change).</summary>
        public bool SetKeys(ProximityKeys keys)
        {
            if (ReferenceEquals(_keys, keys))
            {
                return false;
            }
            Dispose();
            _keys = keys;
            IrkCipher = Proximity.CreateIrkCipher(keys.Irk);
            EncCipher = Proximity.CreateCipher(keys.EncKey);
            return true;
        }

        public void CancelConnect()
        {
            Link?.TrySetCanceled();
            Link = null;
        }

        public void Dispose()
        {
            IrkCipher?.Dispose();
            EncCipher?.Dispose();
        }
    }
}
