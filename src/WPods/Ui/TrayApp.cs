using System.Globalization;
using System.Text;
using System.Text.Json;
using WPods.Audio;
using WPods.Core;
using WPods.Nearby;

namespace WPods.Ui;

/// <summary>
/// Owns the UI: tray icon, main window, nearby popup, notifications and the page protocol (ui-protocol.md).
/// Core events arrive on thread-pool threads and only set dirty flags; one posted <see cref="Flush"/> per burst then
/// reads the current state on the UI thread, updates the tray and sends each changed message once.
/// </summary>
internal sealed partial class TrayApp : ApplicationContext
{
    [Flags]
    private enum Dirty
    {
        None = 0,
        Session = 1 << 0,
        Devices = 1 << 1,
        Battery = 1 << 2,
        Ear = 1 << 3,
        ListeningMode = 1 << 4,
        Settings = 1 << 5,
        HeartRate = 1 << 6,
        Status = 1 << 7,
        Mic = 1 << 8,
        Offers = 1 << 9,
        HostSettings = 1 << 10,
        Toast = 1 << 11,
    }

    // Main-page messages whose last sent JSON is kept to skip sends that change nothing.
    private enum Sent
    {
        Devices,
        Battery,
        Ear,
        ListeningMode,
        Settings,
        HeartRate,
        Mic,
        Count,
    }

    private sealed record ToastRequest(string Id, ulong Device);

    private const int LowBatteryPercent = 10;
    private const char MiddleDot = (char)0xB7;

    private readonly SettingsStore _settings;
    private readonly ConnectionManager _connections;
    private readonly NearbyService _nearby;
    private readonly MicPipeline _mic;
    private readonly ConversationDucker _ducker;
    private readonly Localization _locales;
    private readonly SynchronizationContext _ui;
    private readonly HostWindow _host;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly MessageWriter _writer = new();
    private readonly string?[] _lastSent = new string?[(int)Sent.Count];
    private readonly Lock _hostSettingsLock = new();
    private readonly HashSet<ulong> _hostSettingsDirty = [];
    private readonly bool[] _lowNotified = new bool[4];
    private readonly Dictionary<ulong, NearbyOffer> _offerByAddress = [];

    private UiPrefs _prefs;
    private ResolvedLocale _locale;
    private bool _lightTaskbar;
    private bool _appsLight;
    private AccentRamp? _accent;
    private uint _trayDpi;

    private int _dirty;
    private ToastRequest? _pendingToast;
    private MicStatus _micSeen;
    private int _lowLatencyFailures;
    private ulong _lastAttached;
    private ulong _userDisconnect;

    // UI-thread state, refreshed by Flush.
    private AapSession? _session;
    private SessionStatus _sessionStatus;
    private List<KnownDevice> _devices = [];
    private IReadOnlyList<NearbyOffer> _offers = [];
    private MicStatus _micStatus;
    private TrayGlyph _glyph;
    private Icon? _trayIcon;
    private nint _trayIconHandle;
    private bool _exiting;

    public TrayApp(SettingsStore settings, ConnectionManager connections, NearbyService nearby, MicPipeline mic,
        LowLatencyKeepAlive keepAlive, ConversationDucker ducker, Localization locales, bool startHidden)
    {
        _settings = settings;
        _connections = connections;
        _nearby = nearby;
        _mic = mic;
        _ducker = ducker;
        _locales = locales;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;
        _prefs = settings.Ui;
        _locale = locales.Resolve(_prefs.Language);
        _lightTaskbar = WindowsTheme.TaskbarUsesLight();
        _appsLight = WindowsTheme.AppsUseLight();
        _accent = WindowsTheme.ReadAccent();
        _trayDpi = NativeUi.PrimaryMonitor().Dpi;
        _micStatus = mic.Status;
        _micSeen = _micStatus;
        _appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!;
        _host = new HostWindow(OnColorsChanged, OnDisplayChanged, OnHotKey);
        _hideTimer.Tick += OnHideTimer;

        _openItem = new ToolStripMenuItem(Text("app.name"), null, (_, _) => ShowMain());
        _openItem.Font = new Font(_openItem.Font, FontStyle.Bold);
        _exitItem = new ToolStripMenuItem(ExitText(), null, (_, _) => Exit());
        var menu = new ContextMenuStrip();
        menu.Items.Add(_openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);
        _tray = new NotifyIcon { ContextMenuStrip = menu };
        _tray.MouseClick += OnTrayClick;

        connections.Attached += OnAttached;
        connections.Detached += OnDetached;
        connections.DevicesChanged += () => MarkDirty(Dirty.Devices);
        connections.LinkChanged += OnLinkChanged;
        connections.HandsFreeFailed += (address, _) => QueueToast("handsFreeFailed", address);
        settings.HostSettingsChanged += OnHostSettingsChanged;
        mic.StatusChanged += OnMicStatus;
        keepAlive.Failed += OnLowLatencyFailed;
        nearby.OffersChanged += () => MarkDirty(Dirty.Offers);
        Program.ActivationRequested += () => _ui.Post(static s => ((TrayApp)s!).ShowMain(), this);

        _devices = connections.GetDevices();
        UpdateTray();
        _tray.Visible = true;
        if (!startHidden)
        {
            ShowMain();
        }
    }

    private bool MainLive => _main is { Pane.Ready: true } && _mainShown;

    // ---- Thread-pool side: record what changed, post one flush per burst ----

    private void MarkDirty(Dirty dirty)
    {
        if (Interlocked.Or(ref _dirty, (int)dirty) == 0)
        {
            _ui.Post(static s => ((TrayApp)s!).Flush(), this);
        }
    }

    private void QueueToast(string id, ulong device)
    {
        Volatile.Write(ref _pendingToast, new ToastRequest(id, device));
        MarkDirty(Dirty.Toast);
    }

    private void OnAttached(AapSession session)
    {
        session.Changed += OnSessionChanged;
        Volatile.Write(ref _lastAttached, session.Address);
        Interlocked.CompareExchange(ref _userDisconnect, 0, session.Address);
        MarkDirty(Dirty.Session);
    }

    private void OnDetached(AapSession session)
    {
        session.Changed -= OnSessionChanged;
        MarkDirty(Dirty.Session);
    }

    private void OnSessionChanged(DeviceChange change)
    {
        Dirty dirty = Dirty.None;
        if ((change & DeviceChange.Connection) != 0)
        {
            dirty |= Dirty.Status;
        }
        if ((change & DeviceChange.Identity) != 0)
        {
            dirty |= Dirty.Devices;
        }
        if ((change & DeviceChange.Battery) != 0)
        {
            dirty |= Dirty.Battery;
        }
        if ((change & DeviceChange.Ear) != 0)
        {
            dirty |= Dirty.Ear;
        }
        if ((change & DeviceChange.ListeningMode) != 0)
        {
            dirty |= Dirty.ListeningMode;
        }
        if ((change & DeviceChange.Settings) != 0)
        {
            dirty |= Dirty.Settings;
        }
        if ((change & DeviceChange.HeartRate) != 0)
        {
            dirty |= Dirty.HeartRate;
        }
        if (dirty != Dirty.None)
        {
            MarkDirty(dirty);
        }
    }

    // CM notification thread. A link that comes up ends a connect wait; the attached pair's link dropping without
    // the user asking is the "disconnected" toast.
    private void OnLinkChanged(ulong address, bool up)
    {
        if (up)
        {
            lock (_linkLock)
            {
                if (_linkWaits.Remove(address, out TaskCompletionSource? wait))
                {
                    wait.TrySetResult();
                }
            }
        }
        else if (Volatile.Read(ref _lastAttached) == address && Volatile.Read(ref _userDisconnect) != address)
        {
            QueueToast("disconnected", address);
        }
        MarkDirty(Dirty.Devices);
    }

    private void OnHostSettingsChanged(ulong address, HostSettings settings)
    {
        lock (_hostSettingsLock)
        {
            _hostSettingsDirty.Add(address);
        }
        MarkDirty(Dirty.HostSettings);
    }

    // Under MicPipeline's lock (serialized).
    private void OnMicStatus(MicStatus status)
    {
        if (status == MicStatus.Problem && _micSeen == MicStatus.Starting)
        {
            QueueToast("micFailed", 0);
        }
        _micSeen = status;
        MarkDirty(Dirty.Mic);
    }

    // Raised on the thread that applied the setting: on attach, or inside SetHostSettingsAsync (see setHostSetting).
    private void OnLowLatencyFailed()
    {
        Interlocked.Increment(ref _lowLatencyFailures);
        QueueToast("lowLatencyFailed", Volatile.Read(ref _lastAttached));
    }

    // ---- UI thread ----

    private void Flush()
    {
        var dirty = (Dirty)Interlocked.Exchange(ref _dirty, 0);
        if (_exiting)
        {
            return;
        }
        AapSession? session = _connections.Session;
        if (session != _session)
        {
            _session = session;
            _sessionStatus = SessionStatus.Initializing;
            Array.Clear(_lowNotified);
            dirty |= Dirty.Session | Dirty.Devices | Dirty.Status;
        }
        if ((dirty & Dirty.Offers) != 0)
        {
            _offers = _nearby.Offers;
            _offerByAddress.Clear();
            foreach (NearbyOffer offer in _offers)
            {
                _offerByAddress[offer.Address] = offer;
            }
            dirty |= Dirty.Devices;
        }
        if ((dirty & Dirty.Devices) != 0)
        {
            _devices = _connections.GetDevices();
        }
        if ((dirty & Dirty.Mic) != 0)
        {
            _micStatus = _mic.Status;
        }
        DeviceState? state = session?.State;
        if (state is not null && (dirty & Dirty.Status) != 0)
        {
            if (state.Status == SessionStatus.NoResponse && _sessionStatus != SessionStatus.NoResponse)
            {
                SendToast("noResponse", session!.Address);
            }
            _sessionStatus = state.Status;
        }
        if (state is not null && (dirty & (Dirty.Battery | Dirty.Session)) != 0)
        {
            CheckLowBattery(state);
        }
        UpdateTray();
        if (MainLive)
        {
            SendMainDeltas(dirty, state);
        }
        if ((dirty & Dirty.Offers) != 0)
        {
            UpdatePopup();
        }
        if ((dirty & Dirty.Toast) != 0 && Interlocked.Exchange(ref _pendingToast, null) is { } toast)
        {
            SendToast(toast.Id, toast.Device);
        }
    }

    private void SendMainDeltas(Dirty dirty, DeviceState? state)
    {
        if ((dirty & Dirty.Devices) != 0)
        {
            SendDevices();
        }
        if ((dirty & Dirty.Session) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("state");
            MessageWriter.WriteState(w, state);
            PostMain(_writer.End());
            Array.Clear(_lastSent, (int)Sent.Battery, (int)Sent.Mic - (int)Sent.Battery);
        }
        else if (state is not null)
        {
            SendStateDeltas(dirty, state, force: false);
        }
        if ((dirty & Dirty.Mic) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("mic");
            w.WriteString("status", MessageWriter.Camel(_micStatus));
            SendMain(Sent.Mic, _writer.End(), force: false);
        }
        if ((dirty & Dirty.HostSettings) != 0)
        {
            ulong[] addresses;
            lock (_hostSettingsLock)
            {
                addresses = [.. _hostSettingsDirty];
                _hostSettingsDirty.Clear();
            }
            foreach (ulong address in addresses)
            {
                SendHostSettings(address);
            }
        }
    }

    private void SendDevices()
    {
        Utf8JsonWriter w = _writer.Begin("devices");
        WriteDevices(w);
        SendMain(Sent.Devices, _writer.End(), force: false);
    }

    /// <summary>Battery, ear, listening mode, settings and heart-rate messages for the flags set in <paramref name="dirty"/>.</summary>
    private void SendStateDeltas(Dirty dirty, DeviceState state, bool force)
    {
        if ((dirty & Dirty.Battery) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("battery");
            MessageWriter.WriteBatteries(w, state);
            SendMain(Sent.Battery, _writer.End(), force);
        }
        if ((dirty & Dirty.Ear) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("ear");
            MessageWriter.WriteEar(w, state);
            SendMain(Sent.Ear, _writer.End(), force);
        }
        if ((dirty & Dirty.ListeningMode) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("listeningMode");
            MessageWriter.WriteListeningMode(w, state);
            SendMain(Sent.ListeningMode, _writer.End(), force);
        }
        if ((dirty & Dirty.Settings) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("settings");
            w.WriteStartObject("settings");
            MessageWriter.WriteSettingFields(w, state);
            w.WriteEndObject();
            SendMain(Sent.Settings, _writer.End(), force);
        }
        if ((dirty & Dirty.HeartRate) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("heartRate");
            MessageWriter.WriteHeartRate(w, state);
            SendMain(Sent.HeartRate, _writer.End(), force);
        }
    }

    private void SendMain(Sent kind, string json, bool force)
    {
        if (!force && _lastSent[(int)kind] == json)
        {
            return;
        }
        _lastSent[(int)kind] = json;
        PostMain(json);
    }

    private void PostMain(string json)
    {
        if (MainLive)
        {
            _main!.Pane.Post(json);
        }
    }

    private void SendHostSettings(ulong address)
    {
        Utf8JsonWriter w = _writer.Begin("hostSettings");
        w.WriteString("id", Mac.Format(address));
        MessageWriter.WriteHost(w, _settings.Get(address));
        PostMain(_writer.End());
    }

    /// <summary>In-page toast; only while the main window shows (protocol 6).</summary>
    private void SendToast(string id, ulong device, string? name = null)
    {
        if (!MainLive)
        {
            return;
        }
        Utf8JsonWriter w = _writer.Begin("toast");
        w.WriteString("toast", id);
        if (device != 0)
        {
            w.WriteString("device", Mac.Format(device));
        }
        else
        {
            w.WriteNull("device");
        }
        if (name is not null)
        {
            w.WriteString("name", name);
        }
        else
        {
            w.WriteNull("name");
        }
        PostMain(_writer.End());
    }

    /// <summary>"devices": Device[] and "attached".</summary>
    private void WriteDevices(Utf8JsonWriter w)
    {
        w.WriteStartArray("devices");
        foreach (KnownDevice device in _devices)
        {
            string conn = Conn(device, out NearbyOffer? offer);
            MessageWriter.WriteDevice(w, device, conn, conn == "elsewhere" ? offer!.Batteries : null, _settings.Get(device.Address));
        }
        w.WriteEndArray();
        if (_session is { } session)
        {
            w.WriteString("attached", Mac.Format(session.Address));
        }
        else
        {
            w.WriteNull("attached");
        }
    }

    private string Conn(KnownDevice device, out NearbyOffer? offer)
    {
        _offerByAddress.TryGetValue(device.Address, out offer);
        if (device.Attached)
        {
            return "connected";
        }
        if (_connecting.Contains(device.Address) || offer is { State: OfferState.Connecting })
        {
            return "connecting";
        }
        if (device.LinkUp)
        {
            return "reconnecting";
        }
        return offer is { State: OfferState.Offer or OfferState.Failed } ? "elsewhere" : "disconnected";
    }

    private string Text(string key) => _locale.Text(key);

    // Not in the locale files yet (reported); English until a "tray.exit" label exists.
    private string ExitText() => _locale.Labels.GetValueOrDefault("tray.exit", "Exit");

    // ---- Tray icon, tooltip, low-battery notification ----

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        if (_mainShown && _main!.WindowState != FormWindowState.Minimized)
        {
            HideMain();
        }
        else
        {
            ShowMain();
        }
    }

    private void UpdateTray()
    {
        int size = NativeUi.GetSystemMetricsForDpi(NativeUi.SmCxSmIcon, _trayDpi);
        TrayGlyph glyph;
        string tip;
        if (_session?.State is not { } state)
        {
            glyph = new TrayGlyph(size, _lightTaskbar, false, IsMax(_connections.LastUsed) ? TrayDevice.Max : TrayDevice.Buds, -1, false, false);
            tip = Text("status.disconnected");
        }
        else
        {
            Battery shown;
            TrayDevice device;
            if (state.HeadphoneBattery.Percent is not null || IsMax(_session.Address))
            {
                shown = state.HeadphoneBattery;
                device = TrayDevice.Max;
            }
            else
            {
                Battery left = state.LeftBattery;
                Battery right = state.RightBattery;
                bool budsReported = left.Percent is not null || right.Percent is not null;
                bool budsInCase = state.PrimaryEar == EarStatus.InCase && state.SecondaryEar == EarStatus.InCase;
                if ((budsInCase || !budsReported) && state.CaseBattery.Percent is not null)
                {
                    shown = state.CaseBattery;
                    device = TrayDevice.Case;
                }
                else
                {
                    shown = right.Percent is null || (left.Percent is { } l && l <= right.Percent) ? left : right;
                    device = TrayDevice.Buds;
                }
            }
            glyph = new TrayGlyph(size, _lightTaskbar, true, device, shown.Percent ?? -1, shown.Status == BatteryStatus.Charging,
                _micStatus == MicStatus.Live);
            tip = Tooltip(state);
        }
        if (glyph != _glyph || _trayIcon is null)
        {
            nint handle = TrayIconRenderer.Render(glyph);
            Icon? oldIcon = _trayIcon;
            nint oldHandle = _trayIconHandle;
            _trayIcon = Icon.FromHandle(handle);
            _trayIconHandle = handle;
            _tray.Icon = _trayIcon;
            _glyph = glyph;
            oldIcon?.Dispose();
            if (oldHandle != 0)
            {
                NativeUi.DestroyIcon(oldHandle);
            }
        }
        if (tip != _tray.Text)
        {
            _tray.Text = tip;
        }
    }

    // Two lines: name, then "Left 84% / Right 82% / Case 61%" joined by a middle dot (absent parts left out, "charging" after a charging part).
    private string Tooltip(DeviceState state)
    {
        string name = state.Name ?? DeviceName(_session!.Address);
        var parts = new StringBuilder();
        AppendPart(parts, "battery.left", state.LeftBattery);
        AppendPart(parts, "battery.right", state.RightBattery);
        AppendPart(parts, "battery.case", state.CaseBattery);
        AppendPart(parts, "battery.max", state.HeadphoneBattery);
        string tip = parts.Length == 0 ? name : name + "\n" + parts;
        return tip.Length <= 127 ? tip : tip[..127];
    }

    private void AppendPart(StringBuilder parts, string label, Battery battery)
    {
        if (battery.Percent is not { } pct)
        {
            return;
        }
        if (parts.Length > 0)
        {
            parts.Append(' ').Append(MiddleDot).Append(' ');
        }
        parts.Append(Text(label)).Append(' ').Append(Text("battery.pct").Replace("{pct}", pct.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        if (battery.Status == BatteryStatus.Charging)
        {
            parts.Append(' ').Append(Text("battery.charging").ToLowerInvariant());
        }
    }

    private bool IsMax(ulong address)
    {
        foreach (KnownDevice device in _devices)
        {
            if (device.Address == address)
            {
                return ((Models.FromModelNumber(device.ModelNumber) ?? Models.Unknown).Capabilities & ModelCapabilities.SingleBattery) != 0;
            }
        }
        return false;
    }

    private string DeviceName(ulong address)
    {
        foreach (KnownDevice device in _devices)
        {
            if (device.Address == address)
            {
                return device.Name;
            }
        }
        return Text("app.name");
    }

    // Once per discharge per part: below 10% and not charging; re-armed at 10% or more, or while charging.
    private void CheckLowBattery(DeviceState state)
    {
        int lowest = -1;
        int lowestPct = 0;
        for (int i = 0; i < _lowNotified.Length; i++)
        {
            Battery battery = i switch
            {
                0 => state.LeftBattery,
                1 => state.RightBattery,
                2 => state.CaseBattery,
                _ => state.HeadphoneBattery,
            };
            if (battery.Percent is not { } pct)
            {
                continue;
            }
            if (pct >= LowBatteryPercent || battery.Status == BatteryStatus.Charging)
            {
                _lowNotified[i] = false;
            }
            else if (!_lowNotified[i])
            {
                _lowNotified[i] = true;
                if (lowest < 0 || pct < lowestPct)
                {
                    lowest = i;
                    lowestPct = pct;
                }
            }
        }
        if (lowest < 0)
        {
            return;
        }
        string part = Text(lowest switch
        {
            0 => "battery.left",
            1 => "battery.right",
            2 => "battery.case",
            _ => "battery.max",
        });
        string text = Text("battery.low")
            .Replace("{name}", state.Name ?? DeviceName(_session!.Address), StringComparison.Ordinal)
            .Replace("{part}", part, StringComparison.Ordinal)
            .Replace("{pct}", lowestPct.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        _tray.ShowBalloonTip(10_000, "", text, ToolTipIcon.None);
    }

    // ---- Exit (tray menu) ----

    private async void Exit()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        _hideTimer.Stop();
        DestroyPopup();
        if (_main is { } main)
        {
            _main = null;
            main.Destroy();
        }
        _host.SetHotKey(false);
        await _connections.DisposeAsync();
        _ducker.Dispose();
        _nearby.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon?.Dispose();
        NativeUi.DestroyIcon(_trayIconHandle);
        _host.Dispose();
        Application.Exit();
    }
}
