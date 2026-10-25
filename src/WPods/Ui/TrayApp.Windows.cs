using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using WPods.Core;
using WPods.Nearby;

namespace WPods.Ui;

// Main window and popup lifecycle, init messages, theme.
internal sealed partial class TrayApp
{
    private static readonly TimeSpan SuspendAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DestroyAfter = TimeSpan.FromMinutes(10);

    // tokens.css bg-window / surface-flyout, so the window never flashes another colour before the page paints.
    private static readonly Color MainLight = Color.FromArgb(0xF2, 0xF2, 0xF4);
    private static readonly Color MainDark = Color.FromArgb(0x1C, 0x1C, 0x1F);
    private static readonly Color PopupLight = Color.FromArgb(0xFB, 0xFB, 0xFC);
    private static readonly Color PopupDark = Color.FromArgb(0x2C, 0x2C, 0x30);

    private readonly Icon _appIcon;
    private readonly System.Windows.Forms.Timer _hideTimer = new();
    private readonly System.Windows.Forms.Timer _popupTimer = new();
    private Task<CoreWebView2Environment>? _environment;
    private MainWindow? _main;
    private bool _mainShown;
    private bool _suspendPending;
    // The main page got its init: a re-show sends only what can change while hidden.
    private bool _mainInitSent;
    private Point? _mainPosition;
    private PopupWindow? _popup;
    private bool _popupShown;
    private double _popupHeight;
    // No offers: the popup is hidden and kept for the next case open (see ParkPopup).
    private bool _popupParked;
    private bool _popupSuspendPending;
    // Popups open on their own (case open): their WebView2 failure balloon shows once per run.
    private bool _popupFailureShown;

    private bool Dark => _prefs.Theme == "dark" || (_prefs.Theme == "auto" && !_appsLight);

    private Color MainBackground => Dark ? MainDark : MainLight;

    private Color PopupBackground => Dark ? PopupDark : PopupLight;

    private Task<CoreWebView2Environment> EnvironmentAsync() => _environment ??= WebPane.CreateEnvironmentAsync();

    // ---- Main window ----

    private void ShowMain()
    {
        if (_exiting)
        {
            return;
        }
        _hideTimer.Stop();
        if (_main is not { } window)
        {
            Log.Info("ui", "main window: create");
            CreateMain();
            return;
        }
        if (!_mainShown)
        {
            Log.Info("ui", $"main window: show (page ready={window.Pane.Ready})");
            if (window.WindowState == FormWindowState.Normal)
            {
                window.Bounds = ClampToWorkArea(window.Bounds);
            }
            window.Pane.SetVisible(true);
            _mainShown = true;
            window.Show();
            if (window.Pane.Ready)
            {
                if (_mainInitSent)
                {
                    SendMainRefresh(window.Pane);
                }
                else
                {
                    SendMainInit();
                }
            }
        }
        if (window.WindowState == FormWindowState.Minimized)
        {
            window.WindowState = FormWindowState.Normal;
        }
        window.Activate();
    }

    private async void CreateMain()
    {
        var pane = new WebPane(OnMainMessage, OnPaneFailed);
        var window = new MainWindow(MainBounds(), MainBackground, Dark, Text("app.name"), _appIcon, pane);
        window.CloseRequested += HideMain;
        window.HeightChanged += OnMainHeightChanged;
        _main = window;
        _mainShown = true;
        window.Show();
        window.Activate();
        try
        {
            CoreWebView2Environment environment = await EnvironmentAsync();
            if (!window.IsDisposed)
            {
                await pane.InitializeAsync(environment, window.Handle, "index.html", MainBackground);
            }
        }
        catch (Exception e)
        {
            // No usable WebView2: drop the empty window so the next show tries again.
            Log.Error("ui", "main window: WebView2 init failed", e);
            _environment = null;
            if (_main == window)
            {
                DestroyMain();
                ShowWebViewFailed();
            }
        }
    }

    private void HideMain()
    {
        if (_main is not { } window || !_mainShown)
        {
            return;
        }
        if (window.WindowState == FormWindowState.Normal)
        {
            _mainPosition = window.Location;
        }
        Log.Info("ui", "main window: hide");
        PostVisibility(window.Pane, false);
        _mainShown = false;
        window.Hide();
        window.Pane.SetVisible(false);
        _suspendPending = true;
        _hideTimer.Interval = (int)SuspendAfter.TotalMilliseconds;
        _hideTimer.Start();
    }

    // 30 s hidden: suspend the page; 10 min hidden: destroy the window and its WebView2.
    private async void OnHideTimer(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        if (_main is not { } window || _mainShown)
        {
            return;
        }
        if (_suspendPending)
        {
            _suspendPending = false;
            _hideTimer.Interval = (int)(DestroyAfter - SuspendAfter).TotalMilliseconds;
            _hideTimer.Start();
            Log.Info("ui", "main window: suspend page");
            await window.Pane.SuspendAsync();
            return;
        }
        DestroyMain();
    }

    private void DestroyMain()
    {
        if (_main is not { } window)
        {
            return;
        }
        if (window.WindowState == FormWindowState.Normal)
        {
            _mainPosition = window.Location;
        }
        Log.Info("ui", "main window: destroy");
        _main = null;
        _mainShown = false;
        _mainInitSent = false;
        _hideTimer.Stop();
        window.Destroy();
    }

    private void OnMainHeightChanged(double heightDip)
    {
        double height = Math.Round(Math.Clamp(heightDip, MainWindow.MinHeightDip, MainWindow.MaxHeightDip));
        if (height != _prefs.WindowHeight)
        {
            SavePrefs(_prefs with { WindowHeight = height });
        }
    }

    // A WebView2 process died (UI thread; the pane is no longer Ready): rebuild that window's page.
    private void OnPaneFailed(WebPane pane, CoreWebView2ProcessFailedKind kind)
    {
        if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // The cached environment's browser process is gone: the rebuild creates a new one.
            _environment = null;
        }
        _ui.Post(static s =>
        {
            var (app, failed) = ((TrayApp, WebPane))s!;
            Log.Warn("ui", $"WebView2 process failed ({(app._main?.Pane == failed ? "main" : app._popup?.Pane == failed ? "popup" : "stale pane")})");
            if (app._main?.Pane == failed)
            {
                bool shown = app._mainShown;
                app.DestroyMain();
                if (shown)
                {
                    app.CreateMain();
                }
            }
            else if (app._popup?.Pane == failed)
            {
                app.DestroyPopup();
                app.UpdatePopup();
            }
        }, (this, pane));
    }

    /// <summary>Last position (or centred on the primary work area), 400 x remembered height DIPs at that monitor's DPI.</summary>
    private Rectangle MainBounds()
    {
        (Rectangle area, uint dpi) = _mainPosition is { } position ? NativeUi.MonitorAt(position) : NativeUi.PrimaryMonitor();
        float scale = dpi / 96f;
        int width = (int)MathF.Round(MainWindow.WidthDip * scale);
        int height = (int)Math.Round(Math.Clamp(_prefs.WindowHeight, MainWindow.MinHeightDip, MainWindow.MaxHeightDip) * scale);
        Point location = _mainPosition ?? new Point(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2);
        return ClampToWorkArea(new Rectangle(location, new Size(width, height)));
    }

    private static Rectangle ClampToWorkArea(Rectangle bounds)
    {
        Rectangle area = NativeUi.MonitorAt(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)).WorkArea;
        int x = Math.Max(area.Left, Math.Min(bounds.X, area.Right - bounds.Width));
        int y = Math.Max(area.Top, Math.Min(bounds.Y, area.Bottom - bounds.Height));
        return new Rectangle(x, y, bounds.Width, bounds.Height);
    }

    private void SendMainInit()
    {
        Array.Clear(_lastSent);
        Utf8JsonWriter w = _writer.Begin("init");
        MessageWriter.WriteLocale(w, _locale);
        w.WriteStartArray("languages");
        foreach ((string code, LocaleFile file) in _locales.Files)
        {
            w.WriteStartObject();
            w.WriteString("code", code);
            w.WriteString("originalName", file.OriginalName);
            w.WriteString("engName", file.EngName);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteString("language", _prefs.Language == "system" ? "" : _prefs.Language);
        w.WriteString("windowsLanguageName", NativeUi.NativeDisplayName(NativeUi.UserUiLanguage()));
        WritePrefs(w);
        w.WriteBoolean("startup", StartupRegistration.IsEnabled());
        w.WriteBoolean("firstRun", !_prefs.FirstRunDone);
        WriteDevices(w);
        if (Selected() is { } selected)
        {
            w.WriteString("selected", Mac.Format(selected));
        }
        else
        {
            w.WriteNull("selected");
        }
        MessageWriter.WriteState(w, _session?.State);
        w.WriteString("mic", MessageWriter.Camel(_micStatus));
        MessageWriter.WriteMicCatalog(w);
        MessageWriter.WriteMicConfig(w, "micConfig", _settings.Mic);
        w.WriteBoolean("visible", _mainShown);
        PostMain(_writer.End());
        _mainInitSent = true;
        if (_settingsResetPending)
        {
            _settingsResetPending = false;
            QueueToast("settingsReset", 0, "S1");
        }
    }

    // Re-show of a page that has its init: what can change while it is hidden (sends to a hidden page are dropped).
    private void SendMainRefresh(WebPane pane)
    {
        Array.Clear(_lastSent);
        PostVisibility(pane, true);
        SendDevices();
        Utf8JsonWriter w = _writer.Begin("state");
        MessageWriter.WriteState(w, _session?.State);
        PostMain(_writer.End());
        w = _writer.Begin("mic");
        w.WriteString("status", MessageWriter.Camel(_micStatus));
        SendMain(Sent.Mic, _writer.End(), force: false);
        SendStartupPref();
        w = _writer.Begin("accent");
        MessageWriter.WriteAccent(w, _accent);
        PostMain(_writer.End());
        w = _writer.Begin("micConfig");
        MessageWriter.WriteMicConfig(w, "config", _settings.Mic);
        PostMain(_writer.End());
        foreach (string? stage in _micStages)
        {
            if (stage is not null)
            {
                PostMain(stage);
            }
        }
    }

    // attached ?? last used ?? first device.
    private ulong? Selected()
    {
        if (_session is { } session)
        {
            return session.Address;
        }
        ulong last = _connections.LastUsed;
        foreach (KnownDevice device in _devices)
        {
            if (device.Address == last)
            {
                return last;
            }
        }
        return _devices.Count > 0 ? _devices[0].Address : null;
    }

    /// <summary>theme, useAccent, accent.</summary>
    private void WritePrefs(Utf8JsonWriter w)
    {
        w.WriteString("theme", _prefs.Theme);
        w.WriteBoolean("useAccent", _prefs.UseAccent);
        MessageWriter.WriteAccent(w, _accent);
    }

    private void PostVisibility(WebPane pane, bool visible)
    {
        Utf8JsonWriter w = _writer.Begin("visibility");
        w.WriteBoolean("visible", visible);
        pane.Post(_writer.End());
    }

    private void SavePrefs(UiPrefs prefs)
    {
        _prefs = prefs;
        try
        {
            _settings.SetUi(prefs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept for this session; the next successful save writes it.
            Log.Warn("ui", $"prefs not saved: {e.Message}");
        }
    }

    // ---- Nearby popup ----

    private void UpdatePopup()
    {
        if (_offers.Count == 0)
        {
            ParkPopup();
            return;
        }
        if (_popup is not { } window)
        {
            CreatePopup();
            return;
        }
        if (!_popupParked)
        {
            window.Pane.Post(OffersMessage("offers"));
            return;
        }
        Log.Info("ui", $"popup: reuse ({_offers.Count} offers)");
        _popupParked = false;
        _popupTimer.Stop();
        window.Pane.SetVisible(true);
        // Full init: a suspended page may have missed labels, prefs or accent sent while parked.
        if (window.Pane.Ready)
        {
            SendPopupInit();
        }
        // Card height unchanged since its last report: the page reports no new size, so show at that height.
        if (_popupHeight > 0)
        {
            OnPopupSize(_popupHeight);
        }
    }

    // No offers: hide the popup and keep its page, so the next case open skips the WebView2 cold start.
    // The empty offers make the page hide its card and report height 0. 30 s parked: suspend; 10 min: destroy.
    private void ParkPopup()
    {
        if (_popup is not { } window || _popupParked)
        {
            return;
        }
        Log.Info("ui", "popup: park");
        HidePopup();
        window.Pane.Post(OffersMessage("offers"));
        _popupParked = true;
        _popupSuspendPending = true;
        _popupTimer.Interval = (int)SuspendAfter.TotalMilliseconds;
        _popupTimer.Start();
    }

    private async void OnPopupTimer(object? sender, EventArgs e)
    {
        _popupTimer.Stop();
        if (_popup is not { } window || !_popupParked)
        {
            return;
        }
        if (_popupSuspendPending)
        {
            _popupSuspendPending = false;
            _popupTimer.Interval = (int)(DestroyAfter - SuspendAfter).TotalMilliseconds;
            _popupTimer.Start();
            Log.Info("ui", "popup: suspend page");
            window.Pane.SetVisible(false);
            await window.Pane.SuspendAsync();
            return;
        }
        DestroyPopup();
    }

    private async void CreatePopup()
    {
        Log.Info("ui", $"popup: create ({_offers.Count} offers)");
        var pane = new WebPane(OnPopupMessage, OnPaneFailed);
        var window = new PopupWindow(PopupBackground, Dark, pane);
        window.ScaleChanged += PlacePopup;
        window.CloseRequested += HidePopup;
        _popup = window;
        _popupShown = false;
        _popupHeight = 0;
        // Laid out hidden at a typical height; shown once the page reports its card height.
        (Rectangle area, uint dpi) = NativeUi.PrimaryMonitor();
        float scale = dpi / 96f;
        int width = (int)MathF.Round(PopupWindow.WidthDip * scale);
        int height = (int)MathF.Round(360 * scale);
        window.Bounds = new Rectangle(area.Right - width, area.Bottom - height, width, height);
        pane.SetBounds(new Rectangle(0, 0, width, height));
        nint handle = window.Handle;
        try
        {
            CoreWebView2Environment environment = await EnvironmentAsync();
            if (!window.IsDisposed)
            {
                await pane.InitializeAsync(environment, handle, "popup.html", PopupBackground);
            }
        }
        catch (Exception e)
        {
            Log.Error("ui", "popup: WebView2 init failed", e);
            _environment = null;
            if (_popup == window)
            {
                // Dropped so the next offers retry with a new popup.
                DestroyPopup();
                if (!_popupFailureShown)
                {
                    _popupFailureShown = true;
                    ShowWebViewFailed();
                }
            }
        }
    }

    private void OnPopupSize(double heightDip)
    {
        _popupHeight = heightDip;
        if (_popup is not { } window)
        {
            return;
        }
        if (heightDip <= 0 || _offers.Count == 0)
        {
            HidePopup();
            return;
        }
        PlacePopup();
        if (!_popupShown)
        {
            Log.Info("ui", "popup: show");
            _popupShown = true;
            window.Show();
            PostVisibility(window.Pane, true);
            _host.SetHotKey(true);
        }
    }

    // 320 DIPs wide, 12 DIPs from the bottom-right corner of the primary work area (which excludes the taskbar).
    private void PlacePopup()
    {
        if (_popup is not { } window || _popupHeight <= 0)
        {
            return;
        }
        (Rectangle area, uint dpi) = NativeUi.PrimaryMonitor();
        float scale = dpi / 96f;
        int width = (int)MathF.Round(PopupWindow.WidthDip * scale);
        int height = (int)Math.Ceiling(_popupHeight * scale);
        int margin = (int)MathF.Round(PopupWindow.MarginDip * scale);
        NativeUi.SetBounds(window.Handle, new Rectangle(area.Right - margin - width, area.Bottom - margin - height, width, height));
    }

    private void HidePopup()
    {
        if (_popup is not { } window || !_popupShown)
        {
            return;
        }
        Log.Info("ui", "popup: hide");
        _popupShown = false;
        _host.SetHotKey(false);
        PostVisibility(window.Pane, false);
        window.Hide();
    }

    private void DestroyPopup()
    {
        if (_popup is not { } window)
        {
            return;
        }
        HidePopup();
        Log.Info("ui", "popup: destroy");
        _popup = null;
        _popupParked = false;
        _popupTimer.Stop();
        window.Destroy();
    }

    private void SendPopupInit()
    {
        Utf8JsonWriter w = _writer.Begin("init");
        MessageWriter.WriteLocale(w, _locale);
        WritePrefs(w);
        w.WriteBoolean("visible", _popupShown);
        WriteOffers(w);
        _popup!.Pane.Post(_writer.End());
    }

    private string OffersMessage(string type)
    {
        Utf8JsonWriter w = _writer.Begin(type);
        WriteOffers(w);
        return _writer.End();
    }

    private void WriteOffers(Utf8JsonWriter w)
    {
        w.WriteStartArray("offers");
        foreach (NearbyOffer offer in _offers)
        {
            MessageWriter.WriteOffer(w, offer);
        }
        w.WriteEndArray();
    }

    // Win+Shift+A while the popup shows: activate it (allowed: the hotkey gave this process the foreground) and focus its main action.
    private void OnHotKey()
    {
        if (_popup is not { } window || !_popupShown)
        {
            return;
        }
        NativeUi.SetForegroundWindow(window.Handle);
        window.Pane.Focus();
        _writer.Begin("focus");
        window.Pane.Post(_writer.End());
    }

    // ---- Theme and display ----

    private void ApplyTheme()
    {
        Log.Info("ui", $"theme {_prefs.Theme}, dark={Dark}");
        _main?.SetTheme(MainBackground, Dark);
        _popup?.SetTheme(PopupBackground, Dark);
    }

    /// <summary>prefs to the popup page (the main page changed them itself).</summary>
    private void SendPrefsToPopup()
    {
        if (_popup is null)
        {
            return;
        }
        Utf8JsonWriter w = _writer.Begin("prefs");
        w.WriteString("theme", _prefs.Theme);
        w.WriteBoolean("useAccent", _prefs.UseAccent);
        _popup.Pane.Post(_writer.End());
    }

    // Windows app/taskbar mode or accent colour changed.
    private void OnColorsChanged()
    {
        _lightTaskbar = WindowsTheme.TaskbarUsesLight();
        bool appsLight = WindowsTheme.AppsUseLight();
        if (appsLight != _appsLight)
        {
            _appsLight = appsLight;
            ApplyTheme();
            TrayMenu.FlushThemes();
        }
        AccentRamp? accent = WindowsTheme.ReadAccent();
        if (accent != _accent)
        {
            _accent = accent;
            Utf8JsonWriter w = _writer.Begin("accent");
            MessageWriter.WriteAccent(w, accent);
            string json = _writer.End();
            PostMain(json);
            _popup?.Pane.Post(json);
        }
        UpdateTray();
    }

    private void OnDisplayChanged()
    {
        _trayDpi = NativeUi.PrimaryMonitor().Dpi;
        UpdateTray();
        PlacePopup();
    }
}
