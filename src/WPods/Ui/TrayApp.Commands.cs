using System.ComponentModel;
using System.Diagnostics;
using System.Security;
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
    // Link-up waits, completed from the CM notification thread.
    private readonly Lock _linkLock = new();
    private readonly Dictionary<ulong, TaskCompletionSource> _linkWaits = [];

    private void OnMainMessage(WebPane pane, PageMessage m)
    {
        if (_main is not { } main || main.Pane != pane)
        {
            return;
        }
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
                SetStartup(m.Value.GetBoolean());
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
                OnCloseRequested();
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
        switch (m.Type)
        {
            case "ready":
                SendPopupInit();
                break;
            case "popupConnect" when Mac.TryParse(m.Id, out ulong address):
                PopupConnect(address);
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

    // ---- Connect / disconnect (only user clicks may page the AirPods) ----

    private async void Connect(ulong address)
    {
        if (!_connecting.Add(address))
        {
            return;
        }
        _cancelled.Remove(address);
        SendDevices();
        string result = "connectFailed";
        try
        {
            KnownDevice? device = Find(address);
            if (device is { LinkUp: true })
            {
                result = await _connections.ConnectAsync(address) ? "connected" : result;
            }
            else if (_offerByAddress.TryGetValue(address, out NearbyOffer? offer) && offer.State is OfferState.Offer or OfferState.Failed)
            {
                await _nearby.ConnectAsync(address);
                result = _connections.Session?.Address == address ? "connected" : result;
            }
            else if (Native.GetInterfaces(Native.RadioInterface).Count == 0)
            {
                result = "bluetoothOff";
            }
            else
            {
                result = await ConnectAudioAsync(address) ? "connected" : result;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user.
        }
        finally
        {
            _connecting.Remove(address);
            SendDevices();
        }
        if (_cancelled.Remove(address))
        {
            return;
        }
        SendToast(result, address, result == "connected" ? DeviceName(address) : null);
    }

    // Windows audio connect (KS one-shot reconnect), wait up to 10 s for the link, then attach.
    private async Task<bool> ConnectAudioAsync(ulong address)
    {
        using var cancel = new CancellationTokenSource();
        var link = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectCancels[address] = cancel;
        lock (_linkLock)
        {
            _linkWaits[address] = link;
        }
        try
        {
            int hr = await Task.Run(() => NearbyNative.wpn_bt_connect_audio(address), cancel.Token);
            if (hr < 0)
            {
                return false;
            }
            if (Find(address) is not { LinkUp: true })
            {
                await link.Task.WaitAsync(LinkWait, cancel.Token);
            }
            return await _connections.ConnectAsync(address);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            _connectCancels.Remove(address);
            lock (_linkLock)
            {
                _linkWaits.Remove(address);
            }
        }
    }

    private void Cancel(ulong address)
    {
        if (!_connecting.Contains(address))
        {
            return;
        }
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
            Volatile.Write(ref _userDisconnect, address);
            await _connections.DisconnectAsync();
        }
        if (reconnect)
        {
            Connect(address);
        }
    }

    private async void PopupConnect(ulong address)
    {
        await _nearby.ConnectAsync(address);
        if (_connections.Session?.Address == address)
        {
            SendToast("connected", address, DeviceName(address));
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
            }
        }
        if (ok)
        {
            return;
        }
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
        if (next is not null)
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
        }
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            StartupRegistration.SetEnabled(enabled);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or SecurityException)
        {
            Utf8JsonWriter w = _writer.Begin("prefs");
            w.WriteBoolean("startup", StartupRegistration.IsEnabled());
            PostMain(_writer.End());
        }
    }

    // "" = Same as Windows. The main window restarts (after this message handler returns) with the new labels.
    private void SetLanguage(string code)
    {
        string choice = code.Length == 0 ? "system" : code;
        if (choice != "system" && !_locales.Files.ContainsKey(choice))
        {
            return;
        }
        SavePrefs(_prefs with { Language = choice });
        _locale = _locales.Resolve(choice);
        _openItem.Text = Text("app.name");
        _exitItem.Text = ExitText();
        UpdateTray();
        _ui.Post(static s =>
        {
            var app = (TrayApp)s!;
            bool shown = app._mainShown;
            app.DestroyMain();
            if (shown)
            {
                app.CreateMain();
            }
            if (app._popup is not null)
            {
                app.DestroyPopup();
                app.UpdatePopup();
            }
        }, this);
    }

    private static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            // Nothing registered for it; nothing else to do.
        }
    }
}
