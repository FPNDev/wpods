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
/// Single-battery models (AirPods Max) have no lid or case signal: their offer appears when the pair starts advertising
/// after being gone, and a dismissal lasts until it is gone again. 45 s without adverts = gone (offer removed, lid state forgotten).
/// <see cref="OffersChanged"/> fires on thread-pool threads (BLE, CM notification, timer or connect continuation),
/// only on a real change; handlers must not block and should read <see cref="Offers"/>.
/// </summary>
public sealed class NearbyService : IDisposable
{
    private const long GoneMs = 45_000;
    private const short LostRssi = -127;
    private const long MissLogIntervalMs = 10_000;
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
    // Under _lock: valid proximity adverts that resolve to no paired AirPods, logged as one count per 10 s.
    private int _misses;
    private long _missWindowStart;
    // Under _lock: adverts of a paired AirPods dropped because the encrypted block does not confirm them, same logging.
    private int _drops;
    private long _dropWindowStart;

    public NearbyService(ConnectionManager connections)
    {
        _connections = connections;
        _expiry = new System.Threading.Timer(static state => ((NearbyService)state!).OnExpiry(), this, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Current offers (immutable snapshot).</summary>
    public IReadOnlyList<NearbyOffer> Offers => Volatile.Read(ref _offers);

    public event Action? OffersChanged;

    /// <summary>
    /// <see cref="IsNearby"/> changed for some pair. Same threading as <see cref="OffersChanged"/>; handlers must not
    /// block and should call <see cref="IsNearby"/>.
    /// </summary>
    public event Action? NearbyChanged;

    /// <summary>
    /// A paired AirPods' case lid opened, or a bud left the case (proximity adverts), whatever the link state. Raised on
    /// the BLE thread on the edge only; handlers must not block. Not raised for single-battery models (no case signal).
    /// </summary>
    public event Action<ulong>? CaseOpened;

    /// <summary>
    /// "Connected to another device": the pair's proximity adverts are seen (resolved with its keys, last one less
    /// than 45 s ago) while its link to Windows is down, unless the last one said both buds are in the closed case.
    /// </summary>
    public bool IsNearby(ulong address)
    {
        lock (_lock)
        {
            return _pairs.TryGetValue(address, out Pair? pair) && pair.Nearby;
        }
    }

    public unsafe void Start()
    {
        Log.Info("nearby", "start");
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
                Log.Info("nearby", $"connect {Mac.Format(address)}: ignored, offer state={pair?.Offer?.State.ToString() ?? "none"}");
                return;
            }
            pair.Link = link;
            // SetLinkUp completes the wait only when the link comes up; a Retry after a failed attach finds it up already.
            if (pair.LinkUp)
            {
                link.TrySetResult();
            }
            pair.Offer = offer with { State = OfferState.Connecting };
            Log.Info("nearby", $"connect {Mac.Format(address)}: offer {offer.State} -> Connecting, linkUp={pair.LinkUp}");
            Publish();
        }
        OffersChanged?.Invoke();
        OfferState result = OfferState.Failed;
        // One 10 s budget for link up and attach. A refused request (hr < 0) still waits: Windows or the AirPods may
        // connect anyway, and only "no link" or "no attach" within the budget is a failure.
        long deadline = Environment.TickCount64 + (long)ConnectTimeout.TotalMilliseconds;
        try
        {
            int hr = await Task.Run(() => NearbyNative.wpn_bt_connect_audio(address)).ConfigureAwait(false);
            Log.Info("nearby", $"connect {Mac.Format(address)}: connect audio hr=0x{hr:X8}");
            await link.Task.WaitAsync(Remaining(deadline)).ConfigureAwait(false);
            Log.Info("nearby", $"connect {Mac.Format(address)}: link up, attaching");
            bool attached = await _connections.ConnectAsync(address).ConfigureAwait(false)
                || await WaitForAttachAsync(address, deadline).ConfigureAwait(false);
            Log.Info("nearby", $"connect {Mac.Format(address)}: attached={attached}");
            if (attached)
            {
                result = OfferState.Connected;
            }
        }
        catch (TimeoutException)
        {
            // No link within 10 s.
            Log.Info("nearby", $"connect {Mac.Format(address)}: no link within 10 s");
        }
        catch (TaskCanceledException)
        {
            // Cancelled or dismissed; the offer was already updated.
            Log.Info("nearby", $"connect {Mac.Format(address)}: link wait cancelled");
        }
        catch (Exception e)
        {
            // Anything else (e.g. detaching the old session) fails the connect; the offer must not stay Connecting.
            Log.Error("nearby", $"connect {Mac.Format(address)} failed", e);
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
            Log.Info("nearby", $"connect {Mac.Format(address)}: result {result}{(changed ? "" : " (offer already changed, not applied)")}");
        }
        if (changed)
        {
            OffersChanged?.Invoke();
        }
    }

    private static TimeSpan Remaining(long deadline) => TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64));

    // ConnectAsync failed but its retry series (or the link-up attach) may still attach the pair within the budget.
    private async Task<bool> WaitForAttachAsync(ulong address, long deadline)
    {
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnAttached(AapSession session)
        {
            if (session.Address == address)
            {
                attached.TrySetResult();
            }
        }
        _connections.Attached += OnAttached;
        try
        {
            if (_connections.Session?.Address != address)
            {
                await attached.Task.WaitAsync(Remaining(deadline)).ConfigureAwait(false);
            }
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            _connections.Attached -= OnAttached;
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
                    Log.Info("nearby", $"connect {Mac.Format(pair.Address)} cancelled: offer {offer.State} -> Offer");
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
                Log.Info("nearby", $"offer {Mac.Format(address)} dismissed (was {pair!.Offer!.State})");
                pair.CancelConnect();
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
        Log.Info("nearby", "dispose");
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
        // Windows reports a device it stopped hearing (~60 s after its last advert) with RSSI -127 and that advert's old
        // payload: not an advert.
        try
        {
            if (rssi != LostRssi && GCHandle.FromIntPtr(context).Target is NearbyService self)
            {
                self.HandleAdvert(address, addressType, new ReadOnlySpan<byte>(data, (int)length));
            }
        }
        catch (Exception e)
        {
            // An exception escaping an unmanaged callback ends the process unlogged.
            Log.Error("nearby", "advert handler failed", e);
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
            int hr = NearbyNative.wpn_ble_start(&OnAdvert, GCHandle.ToIntPtr(_self));
            Log.Info("nearby", $"BLE watcher restarted, hr=0x{hr:X8}");
        }
    }

    private void HandleAdvert(ulong address, byte addressType, ReadOnlySpan<byte> data)
    {
        if (!Proximity.IsValid(address, addressType, data))
        {
            return;
        }
        bool changed;
        bool nearbyChanged;
        ulong caseOpened = 0;
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
                CountAndLog(ref _misses, ref _missWindowStart, "matched no paired AirPods");
                return;
            }
            // The resolved address never expires and bytes 5-8 are clear: an advert counts only when its encrypted
            // levels confirm the clear ones, so a captured advert with flipped bits does nothing.
            AirPodsModel model = pair.Offer?.Model ?? Models.FromProximityId(Proximity.ModelId(data)) ?? Models.FromModelNumber(pair.ModelNumber) ?? Models.Unknown;
            if (Proximity.ReadBatteries(data, pair.EncCipher, model.SingleBattery, pair.Offer?.Batteries ?? default) is not { } batteries)
            {
                CountAndLog(ref _drops, ref _dropWindowStart, "dropped (encrypted levels do not confirm the clear ones)");
                return;
            }
            bool wasNearby = pair.Nearby;
            bool wasOpen = pair.LidOpen;
            bool wasBudInCase = pair.BudInCase;
            changed = Update(pair, data, model, batteries);
            nearbyChanged = pair.Nearby != wasNearby;
            if (((!wasOpen && pair.LidOpen) || (wasBudInCase && !pair.BudInCase)) && Models.FromModelNumber(pair.ModelNumber)?.SingleBattery != true)
            {
                caseOpened = pair.Address;
            }
            if (changed)
            {
                Publish();
            }
        }
        Raise(changed, nearbyChanged);
        if (caseOpened != 0)
        {
            CaseOpened?.Invoke(caseOpened);
        }
    }

    // Under _lock.
    private static void CountAndLog(ref int count, ref long windowStart, string what)
    {
        long now = Environment.TickCount64;
        if (count++ == 0)
        {
            windowStart = now;
        }
        if (now - windowStart >= MissLogIntervalMs)
        {
            Log.Info("nearby", $"{count} proximity adverts {what} in the last {now - windowStart} ms");
            count = 0;
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

    // Under _lock. model and batteries come from HandleAdvert's check of the advert.
    private bool Update(Pair pair, ReadOnlySpan<byte> data, AirPodsModel model, PodBatteries batteries)
    {
        pair.LastSeen = Environment.TickCount64;
        bool arrived = !pair.Present;
        if (arrived)
        {
            Log.Info("nearby", $"{Mac.Format(pair.Address)} arrived (adverts seen), linkUp={pair.LinkUp}");
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
        bool budInCase = Proximity.IsBudInCase(data);
        if (arrived || wasOpen != pair.LidOpen || budInCase != pair.BudInCase)
        {
            Log.Info("nearby", $"{Mac.Format(pair.Address)} lid {(pair.LidOpen ? "open" : "closed")}, bud in case={budInCase}, linkUp={pair.LinkUp}");
        }
        pair.BudInCase = budInCase;
        pair.InClosedCase = !pair.LidOpen && Proximity.AreBothInCase(data);
        if (pair.Offer is { } offer)
        {
            if (!offer.Model.SingleBattery && !pair.LidOpen && offer.State != OfferState.Connecting)
            {
                Log.Info("nearby", $"offer {Mac.Format(pair.Address)} removed: lid closed (was {offer.State})");
                pair.Offer = null;
                return true;
            }
            if (pair.LinkUp || batteries == offer.Batteries)
            {
                return false;
            }
            pair.Offer = offer with { Batteries = batteries };
            return true;
        }
        if (pair.LinkUp)
        {
            return false;
        }
        // AirPods Max have no lid or case signal: offered on arrival (after 45 s unseen). Others: lid opening with a bud in the case.
        if (model.SingleBattery ? !arrived : (!pair.LidOpen || wasOpen || !budInCase))
        {
            return false;
        }
        pair.Offer = new NearbyOffer(pair.Address, pair.Name, model, batteries, OfferState.Offer);
        Log.Info("nearby", $"offer {Mac.Format(pair.Address)} created ({model.Name})");
        return true;
    }

    private void OnExpiry()
    {
        bool changed = false;
        bool nearbyChanged = false;
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
                nearbyChanged |= pair.Nearby;
                pair.Present = false;
                pair.LidOpen = false;
                Log.Info("nearby", $"{Mac.Format(pair.Address)} gone (no adverts for 45 s), offer={pair.Offer?.State.ToString() ?? "none"}");
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
        Raise(changed, nearbyChanged);
    }

    // CM notification thread.
    private void OnLinkChanged(ulong address, bool up)
    {
        bool changed = false;
        bool nearbyChanged = false;
        lock (_lock)
        {
            if (_pairs.TryGetValue(address, out Pair? pair))
            {
                bool wasNearby = pair.Nearby;
                changed = SetLinkUp(pair, up);
                nearbyChanged = pair.Nearby != wasNearby;
            }
            if (changed)
            {
                Publish();
            }
        }
        Raise(changed, nearbyChanged);
        // No radio-state event exists in Core; a link coming up also means the radio is on.
        if (up)
        {
            ThreadPool.QueueUserWorkItem(static s => s.RestartScan(), this, preferLocal: false);
        }
    }

    // Under _lock. A link that comes up ends a connect wait; an idle or failed offer disappears (connected to Windows).
    private static bool SetLinkUp(Pair pair, bool up)
    {
        if (pair.LinkUp != up)
        {
            Log.Info("nearby", $"{Mac.Format(pair.Address)} link {(up ? "up" : "down")}, connect waiting={pair.Link is not null}, offer={pair.Offer?.State.ToString() ?? "none"}");
        }
        pair.LinkUp = up;
        if (!up)
        {
            return false;
        }
        pair.Link?.TrySetResult();
        if (pair.Offer is { State: OfferState.Offer or OfferState.Failed } offer)
        {
            Log.Info("nearby", $"offer {Mac.Format(pair.Address)} removed: link up (was {offer.State})");
            pair.Offer = null;
            return true;
        }
        return false;
    }

    // Pairs = paired AirPods with proximity keys. Serialized so an older device list never overwrites a newer one.
    private void Refresh()
    {
        bool changed = false;
        bool nearbyChanged = false;
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
                        Log.Info("nearby", $"pair {Mac.Format(device.Address)} added (has keys), linkUp={device.LinkUp}");
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
                    bool wasNearby = pair.Nearby;
                    changed |= SetLinkUp(pair, device.LinkUp);
                    nearbyChanged |= pair.Nearby != wasNearby;
                }
                foreach (Pair pair in _pairs.Values)
                {
                    if (pair.Generation != generation)
                    {
                        Log.Info("nearby", $"pair {Mac.Format(pair.Address)} removed (unpaired or keys gone)");
                        _pairs.Remove(pair.Address);
                        pair.CancelConnect();
                        pair.Dispose();
                        changed |= pair.Offer is not null;
                        nearbyChanged |= pair.Nearby;
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
        Raise(changed, nearbyChanged);
    }

    // Outside _lock.
    private void Raise(bool offersChanged, bool nearbyChanged)
    {
        if (offersChanged)
        {
            OffersChanged?.Invoke();
        }
        if (nearbyChanged)
        {
            NearbyChanged?.Invoke();
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
        /// <summary>Last advert's bud-in-case flag; only used to log changes.</summary>
        public bool BudInCase { get; set; }
        /// <summary>Last advert: lid closed with both buds in the case (the case sends a short burst, then goes silent).</summary>
        public bool InClosedCase { get; set; }
        public long LastSeen { get; set; }
        public int Generation { get; set; }
        public NearbyOffer? Offer { get; set; }
        public bool Nearby => Present && !LinkUp && !InClosedCase;
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
