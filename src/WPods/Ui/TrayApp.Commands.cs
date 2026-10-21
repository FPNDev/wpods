using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using WPods.Core;
using WPods.Nearby;

namespace WPods.Ui;

// Page -> host commands (ui-protocol.md 3 and 4).
internal sealed partial class TrayApp
{
    private static readonly TimeSpan LinkWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);

    // User-initiated connects in progress (conn "connecting"), their cancel sources (own link waits) and cancel requests.
    private readonly HashSet<ulong> _connecting = [];
    private readonly Dictionary<ulong, CancellationTokenSource> _connectCancels = [];
    private readonly HashSet<ulong> _cancelled = [];
    // Link-up and attach waits of user connects, completed from core threads.
    private readonly Lock _linkLock = new();
    private readonly Dictionary<ulong, TaskCompletionSource> _linkWaits = [];
    private readonly Dictionary<ulong, TaskCompletionSource> _attachWaits = [];

    private void OnMainMessage(WebPane pane, PageMessage m)
    {
        if (_main is not { } main || main.Pane != pane)
        {
            return;
        }
        Log.Info("ui", "main command " + Describe(m));
        ulong id = Mac.TryParse(m.Id, out ulong address) ? address : 0;
        switch (m.Type)
        {
            case "ready":
                if (_mainShown)
                {
                    SendMainInit();
                }
                break;
            case "connect" when id != 0:
                Connect(id);
                break;
            case "cancel" when id != 0:
                Cancel(id);
                break;
            case "disconnect" when id != 0:
                Disconnect(id, reconnect: false);
                break;
            case "reconnect" when id != 0:
                Disconnect(id, reconnect: true);
                break;
            case "setListeningMode" when MessageWriter.TryParse(m.Mode, out ListeningMode mode):
                RunCommand((s, ct) => s.SetListeningModeAsync(mode, ct), Dirty.ListeningMode);
                break;
            case "setToggle" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                Toggle(m.Name, m.Value.GetBoolean());
                break;
            case "setHeartRate" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                bool on = m.Value.GetBoolean();
                RunCommand((s, _) => new ValueTask<bool>(s.SetHeartRateAsync(on)), Dirty.HeartRate);
                break;
            }
            case "setHearingAid" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                bool on = m.Value.GetBoolean();
                RunCommand((s, ct) => new ValueTask<bool>(s.SetHearingAidAsync(on, ct)), Dirty.Settings);
                break;
            }
            case "setMicSide" when MessageWriter.TryParse(m.Side, out MicSide side):
                RunCommand((s, ct) => s.SetMicSideAsync(side, ct), Dirty.Settings);
                break;
            case "setPressSpeed" when m.Value.ValueKind == JsonValueKind.String && MessageWriter.TryParse(m.Value.GetString(), out PressSpeed speed):
                RunCommand((s, ct) => s.SetPressSpeedAsync(speed, ct), Dirty.Settings);
                break;
            case "setHoldDuration" when m.Value.ValueKind == JsonValueKind.String && MessageWriter.TryParse(m.Value.GetString(), out HoldDuration hold):
                RunCommand((s, ct) => s.SetHoldDurationAsync(hold, ct), Dirty.Settings);
                break;
            case "setToneVolume" when m.Value.ValueKind == JsonValueKind.Number:
            {
                byte volume = (byte)Math.Clamp(Math.Round(m.Value.GetDouble()), 0, 100);
                RunCommand((s, ct) => s.SetToneVolumeAsync(volume, ct), Dirty.Settings);
                break;
            }
            case "setHostSetting" when id != 0:
                SetHostSetting(id, m.Name, m.Value);
                break;
            case "setTheme" when m.Theme is "light" or "dark" or "auto":
                SavePrefs(_prefs with { Theme = m.Theme });
                ApplyTheme();
                SendPrefsToPopup();
                break;
            case "setAccent" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                SavePrefs(_prefs with { UseAccent = m.Value.GetBoolean() });
                SendPrefsToPopup();
                break;
            case "setStartup" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                if (!SetStartup(m.Value.GetBoolean()))
                {
                    SendStartupPref();
                }
                break;
            case "setLanguage" when m.Code is not null:
                SetLanguage(m.Code);
                break;
            case "openBluetoothSettings":
                Launch("ms-settings:bluetooth");
                break;
            case "openAddDevice":
                // Opens Windows 11's "Add a device" dialog directly (the Bluetooth / Wireless display / Everything else picker).
                Launch("ms-settings-connectabledevices:devicediscovery");
                break;
            case "getStarted":
                SavePrefs(_prefs with { FirstRunDone = true });
                break;
            case "minimize":
                main.WindowState = FormWindowState.Minimized;
                break;
            case "close":
                HideMain();
                break;
            case "beginDrag":
                main.BeginDrag();
                break;
        }
    }

    private void OnPopupMessage(WebPane pane, PageMessage m)
    {
        if (_popup?.Pane != pane)
        {
            return;
        }
        Log.Info("ui", "popup command " + Describe(m));
        switch (m.Type)
        {
            case "ready":
                SendPopupInit();
                break;
            case "popupConnect" when Mac.TryParse(m.Id, out ulong address):
                _ = _nearby.ConnectAsync(address);
                break;
            case "popupCancel":
                _nearby.CancelConnect();
                break;
            case "popupDismiss" when m.Ids is not null:
                foreach (string id in m.Ids)
                {
                    if (Mac.TryParse(id, out ulong address))
                    {
                        _nearby.Dismiss(address);
                    }
                }
                break;
            case "popupSize":
                OnPopupSize(m.Height);
                break;
        }
    }

    // Command type plus its fields that are set (none of them secret).
    private static string Describe(PageMessage m)
    {
        var text = new StringBuilder(m.Type);
        AppendField(text, "id", m.Id);
        AppendField(text, "mode", m.Mode);
        AppendField(text, "name", m.Name);
        AppendField(text, "side", m.Side);
        AppendField(text, "theme", m.Theme);
        AppendField(text, "code", m.Code);
        if (m.Value.ValueKind != JsonValueKind.Undefined)
        {
            AppendField(text, "value", m.Value.GetRawText());
        }
        if (m.Ids is not null)
        {
            AppendField(text, "ids", string.Join(',', m.Ids));
        }
        if (m.Height != 0)
        {
            AppendField(text, "height", m.Height.ToString(CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    private static void AppendField(StringBuilder text, string name, string? value)
    {
        if (value is not null)
        {
            text.Append(' ').Append(name).Append('=').Append(value);
        }
    }

    // ---- Connect / disconnect (only user clicks may page the AirPods) ----

    private async void Connect(ulong address)
    {
        if (!_connecting.Add(address))
        {
            Log.Info("ui", $"connect {Mac.Format(address)}: already connecting");
            return;
        }
        _cancelled.Remove(address);
        SendDevices();
        LogConn();
        string? failure = null;
        try
        {
            if (_offerByAddress.TryGetValue(address, out NearbyOffer? offer) && offer.State is OfferState.Offer or OfferState.Failed)
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: through the nearby offer ({offer.State})");
                await _nearby.ConnectAsync(address);
                failure = _connections.Session?.Address == address ? null : "connectFailed";
            }
            else if (Find(address) is not { LinkUp: true } && Native.GetInterfaces(Native.RadioInterface).Count == 0)
            {
                failure = "bluetoothOff";
            }
            else if (!await ConnectAudioAsync(address))
            {
                failure = "connectFailed";
            }
        }
        finally
        {
            _connecting.Remove(address);
            SendDevices();
        }
        bool cancelled = _cancelled.Remove(address);
        Log.Info("ui", $"connect {Mac.Format(address)}: done, failure={failure ?? "none"}, cancelled={cancelled}, session={(_connections.Session is { } s ? Mac.Format(s.Address) : "none")}");
        LogConn();
        if (cancelled || failure is null)
        {
            return;
        }
        SendToast(failure, address);
    }

    /// <summary>
    /// Link up (asking Windows to connect audio only when it is down), then attached, within 10 s. The waits are
    /// registered before anything can complete them, and an already-up link or an attach by the connection manager
    /// itself counts as success. False on timeout, attach failure or Cancel.
    /// </summary>
    private async Task<bool> ConnectAudioAsync(ulong address)
    {
        using var cancel = new CancellationTokenSource(LinkWait);
        var link = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectCancels[address] = cancel;
        lock (_linkLock)
        {
            _linkWaits[address] = link;
            _attachWaits[address] = attached;
        }
        try
        {
            if (Find(address) is not { LinkUp: true })
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: link down, asking Windows to connect audio");
                int hr = await Task.Run(() => NearbyNative.wpn_bt_connect_audio(address), cancel.Token);
                Log.Info("ui", $"connect {Mac.Format(address)}: connect audio hr=0x{hr:X8}, waiting for link");
                await link.Task.WaitAsync(cancel.Token);
                Log.Info("ui", $"connect {Mac.Format(address)}: link up");
            }
            else
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: link already up");
            }
            if (_connections.Session?.Address == address || await _connections.ConnectAsync(address))
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: attached");
                return true;
            }
            // The connection manager may still be attaching this pair on its own (link-up event): wait for it.
            Log.Info("ui", $"connect {Mac.Format(address)}: ConnectAsync false, waiting for an attach until the 10 s deadline");
            await attached.Task.WaitAsync(cancel.Token);
            Log.Info("ui", $"connect {Mac.Format(address)}: attached by the connection manager");
            return true;
        }
        catch (OperationCanceledException)
        {
            bool attachedNow = _connections.Session?.Address == address;
            Log.Info("ui", $"connect {Mac.Format(address)}: timed out or cancelled, attached={attachedNow}, linkUp={Find(address)?.LinkUp}");
            return attachedNow;
        }
        finally
        {
            _connectCancels.Remove(address);
            lock (_linkLock)
            {
                _linkWaits.Remove(address);
                _attachWaits.Remove(address);
            }
        }
    }

    private void Cancel(ulong address)
    {
        if (!_connecting.Contains(address))
        {
            return;
        }
        Log.Info("ui", $"connect {Mac.Format(address)}: cancel requested");
        _cancelled.Add(address);
        if (_connectCancels.TryGetValue(address, out CancellationTokenSource? cancel))
        {
            cancel.Cancel();
        }
        else
        {
            _nearby.CancelConnect();
        }
    }

    private async void Disconnect(ulong address, bool reconnect)
    {
        if (_connections.Session?.Address == address)
        {
            bool ok = await _connections.DisconnectAsync();
            Log.Info("ui", $"disconnect {Mac.Format(address)}: {ok}, reconnect={reconnect}");
        }
        if (reconnect)
        {
            Connect(address);
        }
    }

    private KnownDevice? Find(ulong address)
    {
        foreach (KnownDevice device in _connections.GetDevices())
        {
            if (device.Address == address)
            {
                return device;
            }
        }
        return null;
    }

    // ---- Device commands: success shows through the core's Changed event; failure sends the current value back ----

    private void Toggle(string? name, bool on)
    {
        Func<AapSession, CancellationToken, ValueTask<bool>>? command = name switch
        {
            "earDetection" => (s, ct) => s.SetEarDetectionAsync(on, ct),
            "conversationAwareness" => (s, ct) => s.SetConversationAwarenessAsync(on, ct),
            "personalizedVolume" => (s, ct) => s.SetPersonalizedVolumeAsync(on, ct),
            "allowOff" => (s, ct) => s.SetAllowOffAsync(on, ct),
            "oneBudAnc" => (s, ct) => s.SetOneBudAncAsync(on, ct),
            _ => null,
        };
        if (command is not null)
        {
            RunCommand(command, Dirty.Settings);
        }
    }

    private async void RunCommand(Func<AapSession, CancellationToken, ValueTask<bool>> command, Dirty field)
    {
        AapSession? session = _connections.Session;
        bool ok = false;
        if (session is not null)
        {
            using var timeout = new CancellationTokenSource(CommandTimeout);
            try
            {
                ok = await command(session, timeout.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
            {
                // No reply in 3 s, or the device doesn't support it (not enrolled, no heart-rate sensor).
                Log.Warn("ui", $"device command failed: {e.GetType().Name} {e.Message}");
            }
        }
        if (ok)
        {
            return;
        }
        Log.Warn("ui", $"device command not applied (session={(session is null ? "none" : Mac.Format(session.Address))})");
        if (_connections.Session is { } current && MainLive)
        {
            SendStateDeltas(field, current.State, force: true);
        }
        SendToast("noResponse", session?.Address ?? 0);
    }

    // ---- Host settings and app prefs ----

    private async void SetHostSetting(ulong address, string? name, JsonElement value)
    {
        HostSettings current = _settings.Get(address);
        HostSettings? next = name switch
        {
            "disableHandsFree" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => current with { DisableHandsFree = value.GetBoolean() },
            "lowLatency" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => current with { LowLatency = value.GetBoolean() },
            "micMode" when value.ValueKind == JsonValueKind.String && MessageWriter.TryParse(value.GetString(), out MicMode mode) => current with { MicMode = mode },
            _ => null,
        };
        // Host settings are editable only for the attached pair; anything else just gets the saved value back.
        if (next is not null && _connections.Session?.Address == address)
        {
            int lowLatencyFailures = Volatile.Read(ref _lowLatencyFailures);
            try
            {
                await _connections.SetHostSettingsAsync(address, next);
                // The keep-alive raises Failed synchronously inside the call when Windows refuses the sound: switch back.
                if (next.LowLatency && !current.LowLatency && Volatile.Read(ref _lowLatencyFailures) != lowLatencyFailures)
                {
                    await _connections.SetHostSettingsAsync(address, current);
                }
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                // Hands-Free could not be changed (Round 14): toast and switch back.
                Log.Warn("ui", $"host setting {name} failed: {e.Message}");
                SendToast("handsFreeFailed", address);
                await RevertHandsFreeAsync(address, current);
            }
        }
        SendHostSettings(address);
    }

    private async Task RevertHandsFreeAsync(ulong address, HostSettings previous)
    {
        try
        {
            await _connections.SetHostSettingsAsync(address, previous);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // Windows keeps whatever state it has; the saved setting is back to the previous value.
            Log.Warn("ui", $"hands-free revert failed: {e.Message}");
        }
    }

    /// <summary>False when Windows refused the change.</summary>
    private static bool SetStartup(bool enabled)
    {
        try
        {
            StartupRegistration.SetEnabled(enabled);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or SecurityException)
        {
            Log.Warn("ui", $"startup registration not changed: {e.Message}");
            return false;
        }
    }

    private void SendStartupPref()
    {
        Utf8JsonWriter w = _writer.Begin("prefs");
        w.WriteBoolean("startup", StartupRegistration.IsEnabled());
        PostMain(_writer.End());
    }

    // Tray "Start with Windows": the window's switch didn't change it, so it gets the new value.
    private void ToggleStartupFromTray()
    {
        SetStartup(!StartupRegistration.IsEnabled());
        SendStartupPref();
    }

    // "" = Same as Windows. The main window restarts (after this message handler returns) with the new labels.
    // "" = Same as Windows. Applied live: both pages relabel in place; the tray and notifications read the labels on use.
    private void SetLanguage(string code)
    {
        string choice = code.Length == 0 ? "system" : code;
        if (choice != "system" && !_locales.Files.ContainsKey(choice))
        {
            return;
        }
        SavePrefs(_prefs with { Language = choice });
        _locale = _locales.Resolve(choice);
        Log.Info("ui", $"language {choice} -> {_locale.Code}");
        UpdateTray();
        Utf8JsonWriter w = _writer.Begin("labels");
        MessageWriter.WriteLocale(w, _locale);
        string json = _writer.End();
        PostMain(json);
        _popup?.Pane.Post(json);
    }

    private static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception e)
        {
            // Nothing registered for it; nothing else to do.
            Log.Warn("ui", $"launch {target} failed: {e.Message}");
        }
    }
}
