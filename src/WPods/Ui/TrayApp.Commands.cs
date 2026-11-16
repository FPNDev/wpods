using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using WPods.Audio;
using WPods.Core;
using WPods.Nearby;

namespace WPods.Ui;

// Commands from the main page and the nearby popup.
internal sealed partial class TrayApp
{
    private static readonly TimeSpan LinkWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HandsFreeDropTimeout = TimeSpan.FromSeconds(5);

    // User-initiated connects in progress, their cancel sources and cancel requests.
    private readonly HashSet<ulong> _connecting = [];
    private readonly Dictionary<ulong, CancellationTokenSource> _connectCancels = [];
    private readonly HashSet<ulong> _cancelled = [];
    private readonly Dictionary<ulong, int> _handsFreeApplying = [];
    // Link-up and attach waits of user connects, completed from core threads.
    private readonly Lock _linkLock = new();
    private readonly Dictionary<ulong, TaskCompletionSource> _linkWaits = [];
    private readonly Dictionary<ulong, TaskCompletionSource> _attachWaits = [];
    // Last micStage message per slot, replayed when the hidden main page shows again.
    private readonly string?[] _micStages = new string?[2];

    private const string ReleasesUrl = "https://github.com/FPNDev/wpods/releases/";

    private void OnMainMessage(WebPane pane, PageMessage m)
    {
        if (_main is not { } main || main.Pane != pane)
        {
            return;
        }
        // A volume drag sends one preview per step; only the release is logged.
        if (m.Type != "previewMixerVolume")
        {
            Log.Info("ui", "main command " + Describe(m));
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
            case "setMediaAssist" when m.Name is "calls" or "media" && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                bool on = m.Value.GetBoolean();
                bool calls = m.Name == "calls";
                RunCommand((s, ct) => new ValueTask<bool>(calls ? s.SetMediaAssistCallsAsync(on, ct) : s.SetMediaAssistMediaAsync(on, ct)), Dirty.Settings);
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
                // The AirPods reject tone volumes below 15%.
                byte volume = (byte)Math.Clamp(Math.Round(m.Value.GetDouble()), 15, 100);
                RunCommand((s, ct) => s.SetToneVolumeAsync(volume, ct), Dirty.Settings);
                break;
            }
            case "setHostSetting" when id != 0:
                SetHostSetting(id, m.Name, m.Value);
                break;
            case "setStemModes" when id != 0 && m.Modes is not null:
                SetStemModes(id, m.Modes);
                break;
            case "setMicModes" when MicConfig.TryParseModes(m.Modes, out HashSet<MicMode> modes):
                SaveMicConfig(_settings.Mic with { Modes = modes });
                break;
            case "setMicModel" when MessageWriter.TryParse(m.Mode, out MicMode micMode) && m.Name is not null && MicCatalog.FindModel(micMode, m.Name) is not null:
            {
                MicConfig config = _settings.Mic;
                SaveMicConfig(config with { Models = new Dictionary<MicMode, string>(config.Models) { [micMode] = m.Name } });
                break;
            }
            case "setEnhancement" when m.Value.ValueKind == JsonValueKind.Null
                || (m.Value.ValueKind == JsonValueKind.String && MicCatalog.FindEnhancement(m.Value.GetString()!) is not null):
                SaveMicConfig(_settings.Mic with { Enhancement = m.Value.GetString() });
                break;
            case "setMixerSource" when m.Id is not null && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                _mixer.SetPicked(m.Id, m.Value.GetBoolean());
                SendMixer();
                break;
            case "setMixerVolume" or "previewMixerVolume" when m.Id is not null && m.Value.ValueKind == JsonValueKind.Number
                && m.Value.TryGetInt32(out int percent) && percent is >= 0 and <= 100:
                _mixer.SetVolume(m.Id, percent, save: m.Type == "setMixerVolume");
                break;
            case "setMixerFilter" when m.Name is "disconnected" or "disabled" && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                // The page sends "show"; the prefs store "hide" so a missing value shows everything.
                bool hide = !m.Value.GetBoolean();
                SavePrefs(m.Name == "disconnected" ? _prefs with { MixerHideDisconnected = hide } : _prefs with { MixerHideDisabled = hide });
                SendMixer();
                break;
            }
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
            case "retryBluetooth":
                _service.Retry();
                break;
            case "setAutoUpdate" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                _service.SetAutoUpdate(m.Value.GetBoolean());
                break;
            case "checkUpdate":
                _service.CheckUpdate();
                break;
            case "updateNow":
                _service.UpdateNow();
                break;
            case "dismissUpdate" when _service.Update.Available is { Length: > 0 } available:
                // Hidden for this version only; a newer version shows the note again.
                SavePrefs(_prefs with { DismissedUpdate = available });
                SendUpdate();
                break;
            case "openReleaseNotes" when _service.Update.NotesUrl.StartsWith(ReleasesUrl, StringComparison.OrdinalIgnoreCase):
                Launch(_service.Update.NotesUrl);
                break;
            case "openAddDevice":
                // Windows 11 "Add a device" dialog.
                Launch("ms-settings-connectabledevices:devicediscovery");
                break;
            case "openLegal" when m.Name is "license" or "notices":
                OpenLegal(m.Name == "license" ? "LICENSE.txt" : "THIRD-PARTY-NOTICES.txt");
                break;
            case "getStarted":
                SavePrefs(_prefs with { FirstRunDone = true });
                break;
            case "restartWindows":
                RestartWindows();
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

    // Command type plus its set fields, for the log (none are secret).
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
            // Right after a Disconnect, Windows may still be dropping the link; joining that link would end
            // disconnected, so wait for the disconnect to finish first.
            Task pending = _connections.DisconnectFinished(address);
            if (!pending.IsCompleted)
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: waiting for the disconnect still under way");
                try
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(8));
                }
                catch (TimeoutException)
                {
                    Log.Info("ui", $"connect {Mac.Format(address)}: the disconnect did not end within 8 s; connecting anyway");
                }
            }
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
    /// Brings the link up if needed, then attaches, within <see cref="LinkWait"/>. An attach by the connection
    /// manager itself also counts; false on timeout, attach failure or Cancel.
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
            // Off the UI thread: the attach waits up to 10 s for the service's answer.
            if (_connections.Session?.Address == address || await Task.Run(() => _connections.ConnectAsync(address)))
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: attached");
                return true;
            }
            // The connection manager may still be attaching this pair on its own after the link-up event.
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

    // The core detaches at once, so "notConnected" shows immediately. A failed disconnect means the pair is attached
    // again: toast and resend the real devices and state.
    private async void Disconnect(ulong address, bool reconnect)
    {
        if (_connections.Session?.Address == address)
        {
            await _mic.StopStreamAsync();
            bool ok = await _connections.DisconnectAsync();
            Log.Info("ui", $"disconnect {Mac.Format(address)}: {ok}, reconnect={reconnect}");
            if (!ok)
            {
                SendToast("disconnectFailed", address);
                SendRealState();
            }
        }
        if (reconnect)
        {
            Connect(address);
        }
    }

    private KnownDevice? Find(ulong address) => _connections.GetDevices().Find(d => d.Address == address);

    // ---- Device commands: success shows through the core's Changed event; failure sends the current value back ----

    // The core stores the cycle and writes it on every attach. Always answers with the stored value; a failed write
    // also toasts noResponse.
    private async void SetStemModes(ulong address, string[] names)
    {
        if (_connections.Session is { } session && session.Address == address && MessageWriter.TryParseStemModes(names, out StemModes modes))
        {
            bool ok = false;
            using var timeout = new CancellationTokenSource(CommandTimeout);
            try
            {
                ok = await session.SetStemModesAsync(modes, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // No reply in time.
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Log.Warn("ui", $"setStemModes {Mac.Format(address)} [{string.Join(',', names)}] rejected: {e.Message}");
                ok = true;
            }
            if (!ok)
            {
                SendToast("noResponse", address);
            }
        }
        _devices = _connections.GetDevices();
        SendDevices(force: true);
    }

    private void Toggle(string? name, bool on)
    {
        Func<AapSession, CancellationToken, ValueTask<bool>>? command = name switch
        {
            "earDetection" => (s, ct) => s.SetEarDetectionAsync(on, ct),
            "conversationAwareness" => (s, ct) => s.SetConversationAwarenessAsync(on, ct),
            "personalizedVolume" => (s, ct) => s.SetPersonalizedVolumeAsync(on, ct),
            "oneBudAnc" => (s, ct) => s.SetOneBudAncAsync(on, ct),
            "allowOff" => (s, ct) => s.SetAllowOffAsync(on, ct),
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
                // No reply in time, or unsupported by the device (not enrolled, no heart-rate sensor).
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
        bool attached = _connections.Session?.Address == address;
        HostSettings current = _settings.Get(address);
        HostSettings? next = name switch
        {
            "disableHandsFree" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => current with { DisableHandsFree = value.GetBoolean() },
            "lowLatency" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => current with { LowLatency = value.GetBoolean() },
            // The pipeline loads the new effective mode's model (HostSettingsChanged).
            "micMode" when value.ValueKind == JsonValueKind.String && MessageWriter.TryParse(value.GetString(), out MicMode mode) => current with { MicMode = mode },
            "duckPercent" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int duck) && duck is >= 10 and <= 90 => current with { DuckPercent = duck },
            _ => null,
        };
        bool handsFree = next is not null && next.DisableHandsFree != current.DisableHandsFree;
        // Only the attached pair is editable, and Hands-Free one change at a time; otherwise the saved value goes back.
        if (next is not null && attached && !(handsFree && _handsFreeApplying.ContainsKey(address)))
        {
            if (handsFree)
            {
                SetHandsFreeApplying(address, true);
            }
            int lowLatencyFailures = Volatile.Read(ref _lowLatencyFailures);
            try
            {
                // Disabling Hands-Free while it is in use leaves the A2DP stream paused for good: drop it first.
                if (next.DisableHandsFree && !current.DisableHandsFree && _mic.HandsFreeInUse)
                {
                    await DropHandsFreeAsync(address);
                }
                await _connections.SetHostSettingsAsync(address, next);
                // The keep-alive raises Failed synchronously inside the call when Windows refuses the stream.
                if (next.LowLatency && !current.LowLatency && Volatile.Read(ref _lowLatencyFailures) != lowLatencyFailures)
                {
                    await _connections.SetHostSettingsAsync(address, current);
                }
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                Log.Warn("ui", $"host setting {name} failed: {e.Message}");
                SendToast("handsFreeFailed", address);
                await RevertHandsFreeAsync(address, current);
            }
            finally
            {
                if (handsFree)
                {
                    SetHandsFreeApplying(address, false);
                }
            }
        }
        SendHostSettings(address);
    }

    // Counts Hands-Free changes in flight; the page shows a loader while any runs.
    private void SetHandsFreeApplying(ulong address, bool applying)
    {
        int count = _handsFreeApplying.GetValueOrDefault(address) + (applying ? 1 : -1);
        if (count > 0)
        {
            _handsFreeApplying[address] = count;
        }
        else
        {
            _handsFreeApplying.Remove(address);
        }
        if (count == (applying ? 1 : 0))
        {
            SendHostSettings(address);
        }
    }

    // Disconnects only Hands-Free (A2DP stays) and waits until it is no longer in use.
    private async Task DropHandsFreeAsync(ulong address)
    {
        int hr = await Task.Run(() => Native.wpn_bt_disconnect_handsfree(address));
        Log.Info("ui", $"hands-free {Mac.Format(address)}: in use, disconnect hands-free hr=0x{hr:X8}");
        if (hr < 0)
        {
            return;
        }
        try
        {
            await _mic.HandsFreeEndedAsync().WaitAsync(HandsFreeDropTimeout);
            Log.Info("ui", "hands-free: no longer in use");
        }
        catch (TimeoutException)
        {
            Log.Warn("ui", $"hands-free: still in use after {HandsFreeDropTimeout.TotalSeconds} s");
        }
    }

    // Under MicPipeline's lock; applied on the UI thread in the same order.
    private void OnMicStage(ulong address, MicSlot slot, string? id, StageLoad load, string? code) =>
        _ui.Post(_ => ApplyMicStage(address, slot, id, load, code), null);

    // A failed load keeps the previous stage running and toasts its code, except when dropped by a detach (address 0).
    private void ApplyMicStage(ulong address, MicSlot slot, string? id, StageLoad load, string? code)
    {
        Utf8JsonWriter w = _writer.Begin("micStage");
        w.WriteString("slot", MessageWriter.Camel(slot));
        w.WriteString("id", id);
        w.WriteString("state", MessageWriter.Camel(load));
        string json = _writer.End();
        _micStages[(int)slot] = json;
        PostMain(json);
        if (load == StageLoad.Failed && address != 0)
        {
            SendToast("micModelFailed", address, code);
        }
    }

    // The pipeline follows through MicConfigChanged.
    private void SaveMicConfig(MicConfig config)
    {
        try
        {
            _settings.SetMic(config);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept for this session; the next successful save writes it.
            Log.Warn("ui", $"mic config not saved: {e.Message}");
        }
        Utf8JsonWriter w = _writer.Begin("micConfig");
        MessageWriter.WriteMicConfig(w, "config", _settings.Mic);
        PostMain(_writer.End());
    }

    private static void OpenLegal(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, file);
        if (!File.Exists(path))
        {
            Log.Warn("ui", $"{path} missing");
            return;
        }
        Launch(path);
    }

    private async Task RevertHandsFreeAsync(ulong address, HostSettings previous)
    {
        try
        {
            await _connections.SetHostSettingsAsync(address, previous);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // Windows keeps its current state; the saved setting is already reverted.
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

    private void WriteUpdate(Utf8JsonWriter w)
    {
        UpdateInfo u = _service.Update;
        Version app = typeof(TrayApp).Assembly.GetName().Version ?? new Version(0, 0, 0);
        w.WriteStartObject("update");
        w.WriteString("version", u.Installed.Length > 0 ? u.Installed : app.ToString(3));
        w.WriteString("available", u.Available.Length > 0 ? u.Available : null);
        w.WriteString("status", MessageWriter.Camel(u.Status));
        w.WriteBoolean("checked", u.Checked);
        w.WriteBoolean("auto", u.Auto);
        w.WriteString("dismissed", _prefs.DismissedUpdate);
        w.WriteEndObject();
    }

    private void SendAdapter()
    {
        if (_exiting)
        {
            return;
        }
        Utf8JsonWriter w = _writer.Begin("adapter");
        w.WriteBoolean("unsupported", _adapterUnsupported);
        PostMain(_writer.End());
    }

    private void SendRestart()
    {
        if (_exiting)
        {
            return;
        }
        Utf8JsonWriter w = _writer.Begin("restart");
        w.WriteBoolean("pending", _restartPending);
        PostMain(_writer.End());
    }

    private void SendUpdate()
    {
        if (_exiting)
        {
            return;
        }
        Utf8JsonWriter w = _writer.Begin("update");
        WriteUpdate(w);
        PostMain(_writer.End());
    }

    private void SendStartupPref()
    {
        Utf8JsonWriter w = _writer.Begin("prefs");
        w.WriteBoolean("startup", StartupRegistration.IsEnabled());
        PostMain(_writer.End());
    }

    // Tray "Start with Windows": the window's switch gets the new value.
    private void ToggleStartupFromTray()
    {
        SetStartup(!StartupRegistration.IsEnabled());
        SendStartupPref();
    }

    // "" = Same as Windows. Both pages relabel in place; the tray and notifications read labels on use.
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
            // No handler registered for the target.
            Log.Warn("ui", $"launch {target} failed: {e.Message}");
        }
    }

    // Confirmed on the restart page; a standard user may restart Windows without elevation.
    private static void RestartWindows()
    {
        Log.Info("ui", "restarting Windows: shutdown.exe /r /t 0");
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0") { CreateNoWindow = true })?.Dispose();
        }
        catch (Win32Exception e)
        {
            Log.Warn("ui", $"restart failed: {e.Message}");
        }
    }
}
