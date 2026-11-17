using WPods.Core;
using Shortcut = WPods.Core.Shortcut;

namespace WPods.Ui;

/// <summary>Why a bound hotkey may not run as set.</summary>
internal enum HotkeyStatus
{
    Ok,
    /// <summary>Turn on and Turn off of one setting share it: it switches between them.</summary>
    Toggle,
    /// <summary>Shared with another action on the same setting: neither runs.</summary>
    Conflict,
    /// <summary>Windows or another app registered it first.</summary>
    Taken,
    /// <summary>WPods' own Win+Shift+A for the nearby popup.</summary>
    Reserved,
}

/// <summary>
/// The hotkey actions and the registration of their shortcuts on the host window. An action id is "setting.verb";
/// actions on different settings may share a shortcut and all run, except noise control with a mic mode.
/// </summary>
internal sealed class Hotkeys
{
    // Groups of a shortcut run in this order, and the first one's sound plays: noise, mic, then on/off settings.
    public static readonly string[] Actions =
    [
        "noise.cycle", "noise.off", "noise.transparency", "noise.adaptive", "noise.noiseCancellation",
        "mic.cycle", "mic.standard", "mic.voiceIsolation", "mic.wideSpectrum",
        "mixer.on", "mixer.off", "lowLatency.on", "lowLatency.off", "handsFree.on", "handsFree.off",
        "conversation.on", "conversation.off", "hearingAid.on", "hearingAid.off", "mediaAssist.on", "mediaAssist.off",
        "window.toggle",
    ];

    private const int FirstId = 100;

    // Win+Alt combinations are rarely used by apps, so they seldom take over an app's own keys. Low latency gets one
    // key for both Turn on and Turn off, so it toggles.
    private static readonly Dictionary<string, Shortcut> Defaults = new()
    {
        ["noise.cycle"] = new(0x4E, (int)(NativeUi.ModWin | NativeUi.ModAlt)),
        ["mic.cycle"] = new(0x56, (int)(NativeUi.ModWin | NativeUi.ModAlt)),
        ["lowLatency.on"] = new(0x4C, (int)(NativeUi.ModWin | NativeUi.ModAlt)),
        ["lowLatency.off"] = new(0x4C, (int)(NativeUi.ModWin | NativeUi.ModAlt)),
    };

    private static readonly Shortcut PopupShortcut = new(0x41, (int)(NativeUi.ModWin | NativeUi.ModShift));
    private static readonly HashSet<string> Known = [.. Actions];

    private readonly HostWindow _host;
    private readonly Dictionary<int, Shortcut> _shortcutById = [];
    private readonly Dictionary<Shortcut, List<string>> _actionsByShortcut = [];
    private readonly Dictionary<string, HotkeyStatus> _status = [];
    private IReadOnlyDictionary<string, Shortcut> _bindings = new Dictionary<string, Shortcut>();
    private bool _paused;

    public Hotkeys(HostWindow host)
    {
        _host = host;
    }

    /// <summary>The bound actions and their shortcuts, as last applied.</summary>
    public IReadOnlyDictionary<string, Shortcut> Bindings => _bindings;

    public static bool IsAction(string? name) => name is not null && Known.Contains(name);

    /// <summary>The stored bindings, or the defaults while the user never changed one; unknown actions are left out.</summary>
    public static Dictionary<string, Shortcut> Effective(HotkeyConfig config)
    {
        var bindings = new Dictionary<string, Shortcut>();
        foreach ((string action, Shortcut shortcut) in config.Bindings ?? Defaults)
        {
            if (Known.Contains(action))
            {
                bindings[action] = shortcut;
            }
        }
        return bindings;
    }

    public HotkeyStatus StatusOf(string action) => _status.GetValueOrDefault(action);

    /// <summary>The other actions bound to <paramref name="action"/>'s shortcut.</summary>
    public IEnumerable<string> SharedWith(string action)
    {
        if (!_bindings.TryGetValue(action, out Shortcut shortcut))
        {
            yield break;
        }
        foreach (string other in _actionsByShortcut[shortcut])
        {
            if (other != action)
            {
                yield return other;
            }
        }
    }

    public void Apply(IReadOnlyDictionary<string, Shortcut> bindings)
    {
        UnregisterAll();
        _bindings = bindings;
        _actionsByShortcut.Clear();
        _status.Clear();
        foreach ((string action, Shortcut shortcut) in bindings)
        {
            if (!_actionsByShortcut.TryGetValue(shortcut, out List<string>? actions))
            {
                _actionsByShortcut[shortcut] = actions = [];
            }
            actions.Add(action);
        }
        foreach ((Shortcut _, List<string> actions) in _actionsByShortcut)
        {
            List<List<string>> groups = GroupBySetting(actions);
            // Noise control and a mic mode on one shortcut clash too: the AirPods' chime would hide the mic mode's sound.
            bool noiseAndMic = groups.Exists(g => SettingOf(g[0]) == "noise") && groups.Exists(g => SettingOf(g[0]) == "mic");
            foreach (List<string> group in groups)
            {
                HotkeyStatus status = noiseAndMic && SettingOf(group[0]) is "noise" or "mic" ? HotkeyStatus.Conflict
                    : group.Count == 1 ? HotkeyStatus.Ok
                    : IsTogglePair(group) ? HotkeyStatus.Toggle
                    : HotkeyStatus.Conflict;
                foreach (string action in group)
                {
                    _status[action] = status;
                }
            }
        }
        if (!_paused)
        {
            RegisterAll();
        }
    }

    /// <summary>While the user records a shortcut, none is registered, so the keys reach the page.</summary>
    public void Pause(bool paused)
    {
        if (paused == _paused)
        {
            return;
        }
        _paused = paused;
        if (paused)
        {
            UnregisterAll();
        }
        else
        {
            Apply(_bindings);
        }
    }

    /// <summary>
    /// The actions a WM_HOTKEY id runs, one group per setting (a toggle pair is one group); <paramref name="conflicted"/>
    /// tells whether a group was left out because its actions conflict.
    /// </summary>
    public List<List<string>> Resolve(int id, out bool conflicted)
    {
        conflicted = false;
        var runnable = new List<List<string>>();
        if (!_shortcutById.TryGetValue(id, out Shortcut shortcut))
        {
            return runnable;
        }
        foreach (List<string> group in GroupBySetting(_actionsByShortcut[shortcut]))
        {
            if (_status[group[0]] == HotkeyStatus.Conflict)
            {
                conflicted = true;
            }
            else
            {
                runnable.Add(group);
            }
        }
        return runnable;
    }

    /// <summary>"noise" for "noise.cycle".</summary>
    public static string SettingOf(string action) => action[..action.IndexOf('.')];

    private static bool IsTogglePair(List<string> group) =>
        group.Count == 2 && group.Contains(SettingOf(group[0]) + ".on") && group.Contains(SettingOf(group[0]) + ".off");

    // In Actions order, so a group lists its actions the same way every time.
    private static List<List<string>> GroupBySetting(List<string> actions)
    {
        var groups = new List<List<string>>();
        var bySetting = new Dictionary<string, List<string>>();
        foreach (string action in Actions)
        {
            if (!actions.Contains(action))
            {
                continue;
            }
            string setting = SettingOf(action);
            if (!bySetting.TryGetValue(setting, out List<string>? group))
            {
                bySetting[setting] = group = [];
                groups.Add(group);
            }
            group.Add(action);
        }
        return groups;
    }

    private void RegisterAll()
    {
        int id = FirstId;
        foreach ((Shortcut shortcut, List<string> actions) in _actionsByShortcut)
        {
            HotkeyStatus failure = HotkeyStatus.Ok;
            if (shortcut == PopupShortcut)
            {
                failure = HotkeyStatus.Reserved;
            }
            else if (_host.Register(id, (uint)shortcut.Mods, (uint)shortcut.Vk))
            {
                _shortcutById[id] = shortcut;
            }
            else
            {
                failure = HotkeyStatus.Taken;
            }
            id++;
            if (failure != HotkeyStatus.Ok)
            {
                Log.Info("hotkeys", $"vk 0x{shortcut.Vk:X2} mods {shortcut.Mods} not registered: {failure}");
                foreach (string action in actions)
                {
                    _status[action] = failure;
                }
            }
        }
    }

    private void UnregisterAll()
    {
        foreach (int id in _shortcutById.Keys)
        {
            _host.Unregister(id);
        }
        _shortcutById.Clear();
    }
}
