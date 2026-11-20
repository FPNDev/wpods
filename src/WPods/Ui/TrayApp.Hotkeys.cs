using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using WPods.Audio;
using WPods.Core;
using WPods.Nearby;
using Shortcut = WPods.Core.Shortcut;

namespace WPods.Ui;

// Global hotkeys: running their actions, the notification window that confirms them, and the Hotkeys page.
internal sealed partial class TrayApp
{
    private const int OsdMarginDip = 24;
    private const int VkF13 = 0x7C;
    private const int VkF24 = 0x87;

    // Stem order of the home page and the AirPods' own settings.
    private static readonly ListeningMode[] NoiseOrder =
        [ListeningMode.Off, ListeningMode.Transparency, ListeningMode.Adaptive, ListeningMode.NoiseCancellation];

    private sealed record OsdItem(string Icon, string Title, string Detail, string Tone);

    private sealed record Outcome(OsdItem Item, Sounds.Cue? Cue);

    private readonly System.Windows.Forms.Timer _osdTimer = new();
    private Hotkeys _hotkeys = null!;
    private PopupWindow? _osd;
    private bool _osdShown;
    private bool _osdSuspendPending;
    private Size _osdSize;
    // Monitor the notification shows on, kept while it stays up so new content doesn't move it.
    private Point? _osdAnchor;
    // Last notification sent, posted again if the page loads after it; its number tells a stale "osdHidden" apart.
    private string? _osdMessage;
    private int _osdSeq;
    // Mode the attached pair had before its current one, for a second press of a mode's hotkey.
    private AapSession? _modesSession;
    private ListeningMode? _noiseLast;
    private ListeningMode? _noisePrevious;
    private MicMode? _micLast;
    private MicMode? _micPrevious;

    private void StartHotkeys()
    {
        _hotkeys = new Hotkeys(_host);
        _hotkeys.Apply(Hotkeys.Effective(_settings.Hotkeys));
        _osdTimer.Tick += OnOsdTimer;
    }

    private void OnHotKey(int id)
    {
        if (id == HostWindow.PopupHotKeyId)
        {
            FocusPopup();
        }
        else if (!_exiting)
        {
            RunHotkey(id);
        }
    }

    // Groups run one after another; the notification shows each as pending, then its result.
    private async void RunHotkey(int id)
    {
        List<List<string>> groups = _hotkeys.Resolve(id, out bool conflicted);
        Log.Info("hotkeys", $"pressed: [{string.Join("; ", groups.Select(g => string.Join(',', g)))}]{(conflicted ? " (conflicting actions skipped)" : "")}");
        var items = new List<OsdItem?>();
        Sounds.Cue? cue = null;
        if (groups.Count == 0 && conflicted)
        {
            Outcome fail = Fail(Text("app.name"), Text("osd.conflict"));
            items.Add(fail.Item);
            cue = fail.Cue;
            ShowOsd(items);
        }
        foreach (List<string> group in groups)
        {
            string setting = Hotkeys.SettingOf(group[0]);
            if (setting == "window")
            {
                ToggleMainFromHotkey();
                continue;
            }
            int slot = items.Count;
            items.Add(null);
            void Pending(OsdItem item)
            {
                items[slot] = item;
                ShowOsd(items);
            }
            Outcome outcome = setting switch
            {
                "noise" => await NoiseAsync(group[0], Pending),
                "mic" => await MicAsync(group[0], Pending),
                "connection" => await ConnectionAsync(group, Pending),
                _ => await ToggleAsync(setting, group, Pending),
            };
            items[slot] = outcome.Item;
            // One sound per press, from the first group in Actions order: noise control (the AirPods' own chime when
            // it worked), then mic modes, then on/off settings.
            if (slot == 0)
            {
                cue = outcome.Cue;
            }
            ShowOsd(items);
        }
        if (cue is { } sound && _settings.Hotkeys.Sounds)
        {
            Sounds.Play(sound);
        }
    }

    private static Outcome Fail(string title, string reason) => new(new OsdItem("warning", title, reason, "fail"), Sounds.Cue.Error);

    // ---- Noise control ----

    private async Task<Outcome> NoiseAsync(string action, Action<OsdItem> pending)
    {
        string group = Text("nc.title");
        if (_connections.Session is not { } session)
        {
            return Fail(group, Text("osd.notConnected"));
        }
        DeviceState s = session.State;
        DeviceCapabilities caps = s.Capabilities;
        if ((caps & DeviceCapabilities.ListeningMode) == 0)
        {
            return Fail(group, Text("osd.notSupported"));
        }
        ListeningMode? current = s.ListeningMode;
        ListeningMode target;
        if (action == "noise.cycle")
        {
            target = NextNoise(session, current ?? ListeningMode.NoiseCancellation);
        }
        else
        {
            target = action switch
            {
                "noise.off" => ListeningMode.Off,
                "noise.transparency" => ListeningMode.Transparency,
                "noise.adaptive" => ListeningMode.Adaptive,
                _ => ListeningMode.NoiseCancellation,
            };
            if (target == ListeningMode.Off && !OffAllowed(s))
            {
                return Fail(NoiseName(target), Text("osd.offNotAllowed"));
            }
            if (target == ListeningMode.Adaptive && (caps & DeviceCapabilities.Adaptive) == 0)
            {
                return Fail(NoiseName(target), Text("osd.notSupported"));
            }
            if (current == target)
            {
                target = _noisePrevious is { } previous && previous != target && NoiseAllowed(s, previous) ? previous : NextNoise(session, target);
            }
        }
        if (NoiseBlocked(s, target) is { } reason)
        {
            return Fail(NoiseName(target), Text(reason));
        }
        var done = new OsdItem(NoiseIcon(target), NoiseName(target), group, "mode");
        pending(done with { Tone = "pending" });
        bool ok = await RunCommandAsync((x, ct) => x.SetListeningModeAsync(target, ct), Dirty.ListeningMode, toast: false);
        // The AirPods play their own chime for a new listening mode.
        return ok ? new Outcome(done, null) : Fail(NoiseName(target), Text("toast.noResponse"));
    }

    private static bool OffAllowed(DeviceState s) => (s.Capabilities & DeviceCapabilities.AllowOff) == 0 || s.AllowOff != false;

    private static bool NoiseAllowed(DeviceState s, ListeningMode mode) => mode switch
    {
        ListeningMode.Off => OffAllowed(s),
        ListeningMode.Adaptive => (s.Capabilities & DeviceCapabilities.Adaptive) != 0,
        _ => true,
    };

    // The next mode after `from` in the stored press-and-hold cycle, as a stem press would pick it.
    private ListeningMode NextNoise(AapSession session, ListeningMode from)
    {
        StemModes cycle = _settings.GetStemModes(session.Address);
        int start = Array.IndexOf(NoiseOrder, from);
        for (int i = 1; i <= NoiseOrder.Length; i++)
        {
            ListeningMode mode = NoiseOrder[(start + i) % NoiseOrder.Length];
            if ((cycle & StemOf(mode)) != 0 && NoiseAllowed(session.State, mode))
            {
                return mode;
            }
        }
        return from;
    }

    private static StemModes StemOf(ListeningMode mode) => mode switch
    {
        ListeningMode.Off => StemModes.Off,
        ListeningMode.Transparency => StemModes.Transparency,
        ListeningMode.Adaptive => StemModes.Adaptive,
        _ => StemModes.NoiseCancellation,
    };

    // The home page's rules: no change with both AirPods out of the ears, and Noise Cancellation or Adaptive need both
    // in unless "Noise cancellation with one AirPod" is on. Returns the label key of the reason.
    private static string? NoiseBlocked(DeviceState s, ListeningMode target)
    {
        if (Models.FromModelNumber(s.ModelNumber)?.SingleBattery == true)
        {
            return null;
        }
        DeviceState ears = s.EarSource;
        DeviceState batteries = s.BatterySource;
        EarStatus? left = ears.LeftEar;
        EarStatus? right = ears.RightEar;
        bool outOfEar = left != EarStatus.InEar && right != EarStatus.InEar
            && (left is EarStatus.OutOfEar or EarStatus.InCase || right is EarStatus.OutOfEar or EarStatus.InCase);
        if (outOfEar)
        {
            return "nc.outOfEarNote";
        }
        if (target is not (ListeningMode.NoiseCancellation or ListeningMode.Adaptive) || s.OneBudAnc == true)
        {
            return null;
        }
        int present = (batteries.LeftBattery.Percent is not null && left != EarStatus.Disconnected ? 1 : 0)
            + (batteries.RightBattery.Percent is not null && right != EarStatus.Disconnected ? 1 : 0);
        int inEar = (left == EarStatus.InEar ? 1 : 0) + (right == EarStatus.InEar ? 1 : 0);
        return present < 2 || inEar == 1 ? "nc.oneBudNote" : null;
    }

    private string NoiseName(ListeningMode mode) => Text("nc." + MessageWriter.Camel(mode));

    private static string NoiseIcon(ListeningMode mode) => mode switch
    {
        ListeningMode.Off => "nc-off",
        ListeningMode.Transparency => "nc-transparency",
        ListeningMode.Adaptive => "nc-adaptive",
        _ => "nc-anc",
    };

    // ---- Mic modes ----

    private async Task<Outcome> MicAsync(string action, Action<OsdItem> pending)
    {
        string group = Text("mic.mode");
        if (_connections.Session is not { } session)
        {
            return Fail(group, Text("osd.notConnected"));
        }
        MicConfig config = _settings.Mic;
        HostSettings host = _settings.Get(session.Address);
        MicMode current = config.EffectiveMode(host.MicMode);
        MicMode target;
        if (action == "mic.cycle")
        {
            if (config.Modes.Count < 2)
            {
                return Fail(group, Text("osd.oneMicMode"));
            }
            target = NextMic(config, current);
        }
        else
        {
            target = action switch
            {
                "mic.standard" => MicMode.Standard,
                "mic.voiceIsolation" => MicMode.VoiceIsolation,
                _ => MicMode.WideSpectrum,
            };
            if (!config.Modes.Contains(target))
            {
                return Fail(MicName(target), Text("osd.micModeOff").Replace("{mode}", MicName(target), StringComparison.Ordinal));
            }
            if (current == target)
            {
                target = _micPrevious is { } previous && previous != target && config.Modes.Contains(previous) ? previous : NextMic(config, target);
            }
        }
        var done = new OsdItem(MicIcon(target), MicName(target), group, "mode");
        pending(done with { Tone = "pending" });
        bool ok = await ApplyHostSettingsAsync(session.Address, h => h with { MicMode = target }, toast: false);
        SendHostSettings(session.Address);
        if (!ok)
        {
            return Fail(MicName(target), Text("osd.notConnected"));
        }
        return new Outcome(done, target switch
        {
            MicMode.Standard => Sounds.Cue.Standard,
            MicMode.VoiceIsolation => Sounds.Cue.VoiceIsolation,
            _ => Sounds.Cue.WideSpectrum,
        });
    }

    // Home page order: Standard, Voice Isolation, Wide Spectrum.
    private static MicMode NextMic(MicConfig config, MicMode from)
    {
        MicMode[] all = MicConfig.AllModes;
        int start = Array.IndexOf(all, from);
        for (int i = 1; i <= all.Length; i++)
        {
            MicMode mode = all[(start + i) % all.Length];
            if (config.Modes.Contains(mode))
            {
                return mode;
            }
        }
        return from;
    }

    private string MicName(MicMode mode) => Text("mic." + MessageWriter.Camel(mode));

    private static string MicIcon(MicMode mode) => mode switch
    {
        MicMode.Standard => "mic-standard",
        MicMode.VoiceIsolation => "mic-voice",
        _ => "mic-wide",
    };

    // ---- On/off settings ----

    // A group is "x.on", "x.off" or both (the same hotkey toggles).
    private async Task<Outcome> ToggleAsync(string setting, List<string> group, Action<OsdItem> pending)
    {
        (string title, string icon) = setting switch
        {
            "mixer" => (Text("mixer.title"), "mixer"),
            "lowLatency" => (Text("windows.latency"), "latency"),
            "lowLatencyMic" => (Text("windows.latencyMic"), "mic"),
            "handsFree" => (Text("hotkeys.handsFree"), "hands-free"),
            "conversation" => (Text("features.conversation"), "conversation"),
            "hearingAid" => (Text("features.hearingAid"), "ear"),
            _ => (Text("hearing.mediaAssist"), "media-assist"),
        };
        AapSession? session = _connections.Session;
        if (setting != "mixer" && session is null)
        {
            return Fail(title, Text("osd.notConnected"));
        }
        DeviceState? s = session?.State;
        DeviceCapabilities caps = s?.Capabilities ?? DeviceCapabilities.None;
        DeviceCapabilities hearing = setting switch
        {
            "hearingAid" => DeviceCapabilities.HearingAid,
            "mediaAssist" => DeviceCapabilities.MediaAssist,
            _ => DeviceCapabilities.None,
        };
        if (hearing != DeviceCapabilities.None && (caps & hearing) != 0 && s!.HearingAidEnrolled != true)
        {
            return Fail(title, Text("hearing.setup"));
        }
        HostSettings host = session is null ? HostSettings.Default : _settings.Get(session.Address);
        bool? current = setting switch
        {
            "mixer" => _settings.Mixer.Enabled,
            "lowLatency" => host.LowLatency,
            // Only for a pair whose mic comes through WPods, as on the settings page.
            "lowLatencyMic" when Models.HasStereoMic(s!.ModelNumber) || _connections.HdVoiceOn(session!.Address, host) => host.LowLatencyMic,
            "handsFree" => !host.DisableHandsFree,
            "conversation" when (caps & DeviceCapabilities.ConversationAwareness) != 0 => s!.ConversationAwareness == true,
            "hearingAid" when (caps & DeviceCapabilities.HearingAid) != 0 => s!.HearingAidEnabled,
            "mediaAssist" when (caps & DeviceCapabilities.MediaAssist) != 0 && s!.MediaAssistMedia is { } media => media,
            _ => null,
        };
        if (current is not { } was)
        {
            return Fail(title, Text("osd.notSupported"));
        }
        // HD voice keeps Windows' Hands-Free off, as on the settings page.
        if (setting == "handsFree" && _connections.HdVoiceOn(session!.Address, host))
        {
            return Fail(title, Text("osd.hdVoiceOn"));
        }
        bool on = group.Count == 2 ? !was : group[0].EndsWith(".on", StringComparison.Ordinal);
        var done = new OsdItem(icon, title, Text(on ? "osd.on" : "osd.off"), on ? "on" : "off");
        // A Hands-Free change still applying: this press only shows it; the running change reports its result.
        if (setting == "handsFree" && _handsFreeApplying.ContainsKey(session!.Address))
        {
            return new Outcome(done with { Tone = "pending" }, null);
        }
        pending(done with { Tone = "pending" });
        bool ok;
        string failure = "toast.noResponse";
        switch (setting)
        {
            case "mixer":
                _mixer.SetEnabled(on);
                SendMixer();
                ok = true;
                break;
            case "lowLatency":
                ok = await ApplyHostSettingsAsync(session!.Address, h => h with { LowLatency = on }, toast: false);
                SendHostSettings(session.Address);
                failure = on ? "toast.lowLatencyFailed" : failure;
                break;
            case "lowLatencyMic":
                ok = await ApplyHostSettingsAsync(session!.Address, h => h with { LowLatencyMic = on }, toast: false);
                SendHostSettings(session.Address);
                break;
            case "handsFree":
                ok = await ApplyHostSettingsAsync(session!.Address, h => h with { DisableHandsFree = !on }, toast: false);
                SendHostSettings(session.Address);
                failure = "toast.handsFreeFailed";
                break;
            case "conversation":
                ok = await RunCommandAsync((x, ct) => x.SetConversationAwarenessAsync(on, ct), Dirty.Settings, toast: false);
                break;
            case "hearingAid":
                ok = await RunCommandAsync((x, ct) => new ValueTask<bool>(x.SetHearingAidAsync(on, ct)), Dirty.Settings, toast: false);
                break;
            default:
                ok = await RunCommandAsync((x, ct) => new ValueTask<bool>(x.SetMediaAssistMediaAsync(on, ct)), Dirty.Settings, toast: false);
                break;
        }
        return ok ? new Outcome(done, on ? Sounds.Cue.On : Sounds.Cue.Off) : Fail(title, Text(failure));
    }

    // Connect: the last used pair; Disconnect: the attached one. One hotkey for both switches between them.
    private async Task<Outcome> ConnectionAsync(List<string> group, Action<OsdItem> pending)
    {
        AapSession? session = _connections.Session;
        bool connect = group.Count == 2 ? session is null : group[0] == "connection.on";
        ulong address = session?.Address ?? _settings.LastUsed;
        string title = Find(address)?.Name is { Length: > 0 } name ? name : Text("app.name");
        var done = new OsdItem("bluetooth", title, Text(connect ? "status.connected" : "status.notConnected"), connect ? "on" : "off");
        // Disconnect during a connect cancels it, like the home page's Cancel.
        if (!connect && session is null && _connecting.Contains(address))
        {
            Cancel(address);
            return new Outcome(done, Sounds.Cue.Off);
        }
        if (connect == (session is not null))
        {
            return new Outcome(done, null);
        }
        if (address == 0)
        {
            return Fail(title, Text("toast.connectFailed"));
        }
        var connecting = new OsdItem("bluetooth", title, Text("status.connecting"), "pending");
        // A connect still under way: this press only shows it; the running one reports its result.
        if (connect && (_connecting.Contains(address)
            || _offerByAddress.TryGetValue(address, out NearbyOffer? offer) && offer.State == OfferState.Connecting))
        {
            return new Outcome(connecting, null);
        }
        if (!connect)
        {
            Disconnect(address, reconnect: false);
            return new Outcome(done, Sounds.Cue.Off);
        }
        pending(connecting);
        if (await ConnectCoreAsync(address) is { } failure)
        {
            return Fail(title, Text("toast." + failure));
        }
        // Cancelled from the home page or by a Disconnect hotkey.
        return _connections.Session?.Address == address ? new Outcome(done, Sounds.Cue.On) : new Outcome(done with { Detail = Text("status.notConnected"), Tone = "off" }, null);
    }

    private void ToggleMainFromHotkey()
    {
        if (_mainShown && _main!.WindowState != FormWindowState.Minimized && Form.ActiveForm == _main)
        {
            HideMain();
        }
        else
        {
            ShowMain();
        }
    }

    // Called from Flush: remembers the attached pair's previous listening and mic mode.
    private void TrackModes(AapSession? session)
    {
        if (session != _modesSession)
        {
            _modesSession = session;
            _noisePrevious = null;
            _micPrevious = null;
            _noiseLast = session?.State.ListeningMode;
            _micLast = session is null ? null : _settings.Mic.EffectiveMode(_settings.Get(session.Address).MicMode);
            return;
        }
        if (session is null)
        {
            return;
        }
        ListeningMode? noise = session.State.ListeningMode;
        if (noise is not null && noise != _noiseLast)
        {
            _noisePrevious = _noiseLast ?? _noisePrevious;
            _noiseLast = noise;
        }
        MicMode mic = _settings.Mic.EffectiveMode(_settings.Get(session.Address).MicMode);
        if (mic != _micLast)
        {
            _micPrevious = _micLast;
            _micLast = mic;
        }
    }

    // ---- Hotkeys page ----

    // A shortcut needs Ctrl, Alt or Win, so it never takes over typing; F13 to F24 may go alone.
    private static bool TryParseShortcut(JsonElement value, out Shortcut? shortcut)
    {
        shortcut = null;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("vk", out JsonElement vkValue) || !vkValue.TryGetInt32(out int vk)
            || !value.TryGetProperty("mods", out JsonElement modsValue) || !modsValue.TryGetInt32(out int mods)
            || vk is <= 0 or >= 0xFF || mods is < 0 or > 0xF)
        {
            return false;
        }
        if ((mods & (int)(NativeUi.ModAlt | NativeUi.ModCtrl | NativeUi.ModWin)) == 0 && vk is < VkF13 or > VkF24)
        {
            return false;
        }
        shortcut = new Shortcut(vk, mods);
        return true;
    }

    private void SetHotkey(string action, Shortcut? shortcut)
    {
        var bindings = new Dictionary<string, Shortcut>(_hotkeys.Bindings);
        if (shortcut is { } s)
        {
            bindings[action] = s;
        }
        else
        {
            bindings.Remove(action);
        }
        SaveHotkeys(_settings.Hotkeys with { Bindings = bindings });
        _hotkeys.Apply(bindings);
        SendHotkeys();
    }

    private void SaveHotkeys(HotkeyConfig config)
    {
        try
        {
            _settings.SetHotkeys(config);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept for this session; the next successful save writes it.
            Log.Warn("hotkeys", $"settings not saved: {e.Message}");
        }
    }

    private void SendHotkeys()
    {
        Utf8JsonWriter w = _writer.Begin("hotkeys");
        MessageWriter.WriteHotkeys(w, _hotkeys, _settings.Hotkeys.Sounds);
        PostMain(_writer.End());
    }

    // ---- Notification window ----

    private void ShowOsd(List<OsdItem?> items)
    {
        if (_exiting)
        {
            return;
        }
        Utf8JsonWriter w = _writer.Begin("osd");
        w.WriteNumber("seq", ++_osdSeq);
        w.WriteStartArray("items");
        foreach (OsdItem? item in items)
        {
            if (item is null)
            {
                continue;
            }
            w.WriteStartObject();
            w.WriteString("icon", item.Icon);
            w.WriteString("title", item.Title);
            w.WriteString("detail", item.Detail);
            w.WriteString("tone", item.Tone);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        _osdMessage = _writer.End();
        if (_osd is not { } window)
        {
            CreateOsd();
            return;
        }
        _osdTimer.Stop();
        _osdSuspendPending = false;
        ArmOsd(window);
        window.Pane.Post(_osdMessage);
    }

    // A WebView2 paints only in a shown window, so the window is shown cloaked until the page reports its first frame
    // of the new content (osdReady); a cold page would otherwise flash an empty window.
    private static void ArmOsd(PopupWindow window)
    {
        if (!window.Visible)
        {
            NativeUi.SetWindowInt(window.Handle, NativeUi.DwmwaCloak, 1);
            window.Show();
        }
        window.Pane.SetVisible(true);
    }

    private async void CreateOsd()
    {
        Log.Info("ui", "osd: create");
        var pane = new WebPane(OnOsdMessage, OnPaneFailed);
        var window = new PopupWindow(PopupBackground, Dark, pane);
        window.ScaleChanged += PlaceOsd;
        window.CloseRequested += HideOsd;
        _osd = window;
        _osdShown = false;
        _osdSize = Size.Empty;
        // A typical size until the page reports its own.
        (Rectangle area, uint dpi) = NativeUi.PrimaryMonitor();
        float scale = dpi / 96f;
        int width = (int)MathF.Round(360 * scale);
        int height = (int)MathF.Round(72 * scale);
        window.Bounds = new Rectangle(area.Left, area.Bottom - height, width, height);
        pane.SetBounds(new Rectangle(0, 0, width, height));
        nint handle = window.Handle;
        ArmOsd(window);
        try
        {
            CoreWebView2Environment environment = await EnvironmentAsync();
            if (!window.IsDisposed)
            {
                await pane.InitializeAsync(environment, handle, "osd.html", PopupBackground);
            }
        }
        catch (Exception e)
        {
            Log.Error("ui", "osd: WebView2 init failed", e);
            _environment = null;
            if (_osd == window)
            {
                DestroyOsd();
            }
        }
    }

    private void OnOsdMessage(WebPane pane, PageMessage m)
    {
        if (_osd?.Pane != pane)
        {
            return;
        }
        switch (m.Type)
        {
            case "ready":
                Utf8JsonWriter w = _writer.Begin("init");
                MessageWriter.WriteLocale(w, _locale);
                WritePrefs(w);
                w.WriteBoolean("visible", _osdShown);
                pane.Post(_writer.End());
                if (_osdMessage is not null)
                {
                    pane.Post(_osdMessage);
                }
                break;
            case "osdSize":
                OnOsdSize(new Size((int)Math.Ceiling(m.Width), (int)Math.Ceiling(m.Height)));
                break;
            case "osdReady" when !_osdShown && _osdMessage is not null:
                Log.Info("ui", $"osd: show ({_osdSize.Width}x{_osdSize.Height} DIPs)");
                _osdShown = true;
                NativeUi.SetWindowInt(_osd!.Handle, NativeUi.DwmwaCloak, 0);
                PostVisibility(pane, true);
                break;
            case "osdHidden" when m.Value.ValueKind == JsonValueKind.Number && m.Value.TryGetInt32(out int seq) && seq == _osdSeq:
                HideOsd();
                break;
        }
    }

    private void OnOsdSize(Size dip)
    {
        _osdSize = dip;
        if (_osd is not { } window || dip.Width <= 0 || dip.Height <= 0)
        {
            return;
        }
        if (!_osdShown)
        {
            _osdAnchor = Cursor.Position;
        }
        PlaceOsd();
        // The page answers osdReady once it painted at this size.
        _writer.Begin("osdPlaced");
        window.Pane.Post(_writer.End());
    }

    // Bottom centre of the work area of the monitor with the mouse pointer, above the taskbar.
    private void PlaceOsd()
    {
        if (_osd is not { } window || _osdSize.IsEmpty)
        {
            return;
        }
        (Rectangle area, uint dpi) = NativeUi.MonitorAt(_osdAnchor ?? Cursor.Position);
        float scale = dpi / 96f;
        int width = (int)Math.Ceiling(_osdSize.Width * scale);
        int height = (int)Math.Ceiling(_osdSize.Height * scale);
        int margin = (int)MathF.Round(OsdMarginDip * scale);
        NativeUi.SetBounds(window.Handle, new Rectangle(area.Left + (area.Width - width) / 2, area.Bottom - margin - height, width, height));
    }

    // The page faded out: cloak it but keep it painted, hide and suspend it after SuspendAfter, destroy it after
    // DestroyAfter.
    private void HideOsd()
    {
        if (_osd is not { } window || !_osdShown)
        {
            return;
        }
        _osdShown = false;
        _osdAnchor = null;
        _osdMessage = null;
        PostVisibility(window.Pane, false);
        NativeUi.SetWindowInt(window.Handle, NativeUi.DwmwaCloak, 1);
        _osdSuspendPending = true;
        _osdTimer.Interval = (int)SuspendAfter.TotalMilliseconds;
        _osdTimer.Start();
    }

    private async void OnOsdTimer(object? sender, EventArgs e)
    {
        _osdTimer.Stop();
        if (_osd is not { } window || _osdShown)
        {
            return;
        }
        if (_osdSuspendPending)
        {
            _osdSuspendPending = false;
            _osdTimer.Interval = (int)(DestroyAfter - SuspendAfter).TotalMilliseconds;
            _osdTimer.Start();
            window.Hide();
            window.Pane.SetVisible(false);
            await window.Pane.SuspendAsync();
            return;
        }
        DestroyOsd();
    }

    private void DestroyOsd()
    {
        if (_osd is not { } window)
        {
            return;
        }
        Log.Info("ui", "osd: destroy");
        window.Hide();
        _osd = null;
        _osdShown = false;
        _osdAnchor = null;
        _osdTimer.Stop();
        window.Destroy();
    }
}
