using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using WPods.Audio;

namespace WPods.Core;

/// <summary>Apple's mic mode names, in Apple's order; each runs the model <see cref="MicConfig.Models"/> picks.</summary>
public enum MicMode
{
    Standard,
    VoiceIsolation,
    WideSpectrum,
}

/// <summary>Host-side settings of one device; editable while it is disconnected.</summary>
public sealed record HostSettings
{
    public static readonly HostSettings Default = new();

    public bool DisableHandsFree { get; init; } = true;
    public bool LowLatency { get; init; }
    /// <summary>Saved mic mode; <see cref="MicConfig.EffectiveMode"/> gives the one that runs.</summary>
    [JsonConverter(typeof(MicModeJsonConverter))]
    public MicMode MicMode { get; init; }
    /// <summary>Media volume during a conversation, percent of the previous volume; 10..90 (clamped on load).</summary>
    public int DuckPercent { get; init; } = DefaultDuckPercent;

    internal const int DefaultDuckPercent = 40;
    internal const int MinDuckPercent = 10;
    internal const int MaxDuckPercent = 90;
}

/// <summary>App UI preferences (settings.json "ui"; every field optional so older files load).</summary>
public sealed record UiPrefs
{
    public static readonly UiPrefs Default = new();

    /// <summary>"light", "dark" or "auto".</summary>
    public string Theme { get; init; } = "auto";
    public bool UseAccent { get; init; }
    /// <summary>"system" (Windows UI language) or a locale file's ISO code.</summary>
    public string Language { get; init; } = "system";
    /// <summary>Main window height in DIPs.</summary>
    public double WindowHeight { get; init; } = 640;
    public bool FirstRunDone { get; init; }
}

/// <summary>
/// App-wide mic settings (settings.json "mic"), editable any time: the enabled modes (at least one), the model id of
/// each mode (Audio.MicCatalog) and the voice enhancement id (null = Off).
/// </summary>
public sealed record MicConfig(IReadOnlySet<MicMode> Modes, IReadOnlyDictionary<MicMode, string> Models, string? Enhancement)
{
    /// <summary>Every mode, in Apple's order (= enum order).</summary>
    public static readonly MicMode[] AllModes = Enum.GetValues<MicMode>();

    /// <summary>Every mode enabled, each with its default model, no enhancement.</summary>
    public static readonly MicConfig Default = new(new HashSet<MicMode>(AllModes), AllModes.ToDictionary(m => m, MicCatalog.DefaultModel), null);

    /// <summary>
    /// The mode that runs for a pair whose saved mode is <paramref name="saved"/>: itself when enabled, else the next
    /// enabled one in the cycle Voice Isolation -> Standard -> Wide Spectrum -> Voice Isolation.
    /// </summary>
    public MicMode EffectiveMode(MicMode saved)
    {
        MicMode mode = saved;
        for (int i = 0; i < AllModes.Length; i++)
        {
            if (Modes.Contains(mode))
            {
                return mode;
            }
            mode = mode switch
            {
                MicMode.VoiceIsolation => MicMode.Standard,
                MicMode.Standard => MicMode.WideSpectrum,
                _ => MicMode.VoiceIsolation,
            };
        }
        // Unreachable: at least one mode is enabled.
        return saved;
    }

    /// <summary>Parses lower-camel mode names; false for none, or for an unknown or numeric name.</summary>
    public static bool TryParseModes(IReadOnlyList<string>? names, out HashSet<MicMode> modes)
    {
        modes = [];
        if (names is null || names.Count == 0)
        {
            return false;
        }
        foreach (string name in names)
        {
            if (!TryParseMode(name, out MicMode mode))
            {
                return false;
            }
            modes.Add(mode);
        }
        return true;
    }

    /// <summary>Parses one mode name, any case; false for an unknown or numeric name.</summary>
    internal static bool TryParseMode(string? name, out MicMode mode) =>
        Enum.TryParse(name, ignoreCase: true, out mode) && Enum.IsDefined(mode) && !char.IsAsciiDigit(name[0]);
}

/// <summary>Model reported over AAP 0x1D, remembered for pairs that are not attached.</summary>
public sealed record DeviceModel(string Number, string Name);

/// <summary>
/// %LOCALAPPDATA%\WPods\settings.json (host settings, model, reported capabilities, stem modes, ear detection, mic side, last seen pod-saved settings and Windows pairing date per MAC + last-used MAC) and keys.bin (proximity keys,
/// DPAPI CurrentUser). Loaded once; every change is written atomically. Thread-safe.
/// </summary>
public sealed class SettingsStore
{
    /// <summary>Apple default: press and hold switches between Noise Cancellation and Transparency.</summary>
    internal const StemModes DefaultStemModes = StemModes.NoiseCancellation | StemModes.Transparency;

    private const int KeyRecordSize = 8 + 16 + 16;

    private readonly Lock _lock = new();
    private readonly string _directory;
    private readonly string _settingsPath;
    private readonly string _keysPath;
    private readonly Dictionary<ulong, HostSettings> _devices = [];
    private readonly Dictionary<ulong, ProximityKeys> _keys = [];
    private readonly Dictionary<ulong, DeviceModel> _models = [];
    private readonly Dictionary<ulong, DeviceCapabilities> _capabilities = [];
    private readonly Dictionary<ulong, StemModes> _stemModes = [];
    private readonly Dictionary<ulong, bool> _earDetection = [];
    private readonly Dictionary<ulong, bool> _allowOff = [];
    private readonly Dictionary<ulong, MicSide> _micSide = [];
    private readonly Dictionary<ulong, PodSettings> _podSettings = [];
    private readonly Dictionary<ulong, DateTime> _pairingDates = [];
    private ulong _lastUsed;
    private UiPrefs _ui = UiPrefs.Default;
    private MicConfig _mic = MicConfig.Default;

    public SettingsStore()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WPods"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wpods", "device_settings.jsonl"))
    {
    }

    internal SettingsStore(string directory, string legacyFile)
    {
        _directory = directory;
        _settingsPath = Path.Combine(directory, "settings.json");
        _keysPath = Path.Combine(directory, "keys.bin");
        if (File.Exists(_settingsPath))
        {
            LoadSettings();
        }
        else if (File.Exists(legacyFile))
        {
            Migrate(legacyFile);
            SaveSettings();
        }
        if (File.Exists(_keysPath))
        {
            LoadKeys();
        }
        Log.Info("settings", $"loaded: lastUsed={(_lastUsed == 0 ? "none" : Mac.Format(_lastUsed))} devices={_devices.Count} models={_models.Count} capabilities={_capabilities.Count} keys={_keys.Count} language={_ui.Language} theme={_ui.Theme}");
    }

    /// <summary>Raised on the caller's thread of <see cref="ConnectionManager.SetHostSettingsAsync"/>.</summary>
    public event Action<ulong, HostSettings>? HostSettingsChanged;

    /// <summary>Raised on the caller's thread of <see cref="SetMic"/>, outside the store's lock.</summary>
    public event Action<MicConfig>? MicConfigChanged;

    /// <summary>settings.json existed but could not be loaded; every setting is the default. A missing file is not a failure.</summary>
    public bool LoadFailed { get; private set; }

    /// <summary>Last successfully attached device; 0 = none.</summary>
    public ulong LastUsed
    {
        get
        {
            lock (_lock)
            {
                return _lastUsed;
            }
        }
        internal set
        {
            lock (_lock)
            {
                if (_lastUsed == value)
                {
                    return;
                }
                _lastUsed = value;
                SaveSettings();
                Log.Info("settings", $"lastUsed = {Mac.Format(value)}");
            }
        }
    }

    public UiPrefs Ui
    {
        get
        {
            lock (_lock)
            {
                return _ui;
            }
        }
    }

    public void SetUi(UiPrefs prefs)
    {
        lock (_lock)
        {
            if (_ui == prefs)
            {
                return;
            }
            _ui = prefs;
            SaveSettings();
        }
    }

    public MicConfig Mic
    {
        get
        {
            lock (_lock)
            {
                return _mic;
            }
        }
    }

    /// <summary>
    /// Stores a valid <paramref name="config"/> (checked by the caller) and raises <see cref="MicConfigChanged"/>, also
    /// when settings.json could not be written (the write's exception is rethrown after).
    /// </summary>
    public void SetMic(MicConfig config)
    {
        try
        {
            lock (_lock)
            {
                _mic = config;
                SaveSettings();
            }
        }
        finally
        {
            MicConfigChanged?.Invoke(config);
        }
    }

    /// <summary>Stored settings, or <see cref="HostSettings.Default"/> for a device never changed.</summary>
    public HostSettings Get(ulong address) => Read(_devices, address, HostSettings.Default);

    /// <summary>Last model seen for this pair; null if it was never attached.</summary>
    public DeviceModel? GetModel(ulong address)
    {
        lock (_lock)
        {
            return _models.GetValueOrDefault(address);
        }
    }

    /// <summary>Stem press-and-hold cycle of the pair; <see cref="DefaultStemModes"/> when never set.</summary>
    internal StemModes GetStemModes(ulong address) => Read(_stemModes, address, DefaultStemModes);

    /// <summary>Ear detection (0x0A) of the pair as last set by the user or reported by the device; true (Apple default) when never set.</summary>
    internal bool GetEarDetection(ulong address) => Read(_earDetection, address, true);

    /// <summary>Allow Off (0x34) of the pair as last set by the user; false when never set.</summary>
    internal bool GetAllowOff(ulong address) => Read(_allowOff, address, false);

    /// <summary>Microphone side (0x01) of the pair as last set by the user or reported by the device; Automatic when never set.</summary>
    internal MicSide GetMicSide(ulong address) => Read(_micSide, address, MicSide.Automatic);

    /// <summary>Every capability the pair reported on any attach; None if it was never attached.</summary>
    internal DeviceCapabilities GetCapabilities(ulong address) => Read(_capabilities, address, DeviceCapabilities.None);

    /// <summary>Pod-saved settings of the pair as last seen; <see cref="PodSettings.None"/> if never seen.</summary>
    internal PodSettings GetPodSettings(ulong address) => Read(_podSettings, address, PodSettings.None);

    public bool HasKeys(ulong address)
    {
        lock (_lock)
        {
            return _keys.ContainsKey(address);
        }
    }

    public ProximityKeys? GetKeys(ulong address)
    {
        lock (_lock)
        {
            return _keys.GetValueOrDefault(address);
        }
    }

    /// <summary>
    /// Stores <paramref name="settings"/> and raises <see cref="HostSettingsChanged"/> when they differ from the stored
    /// ones, also when settings.json could not be written (the write's exception is rethrown after).
    /// </summary>
    internal void Set(ulong address, HostSettings settings)
    {
        bool changed = false;
        try
        {
            lock (_lock)
            {
                if (_devices.TryGetValue(address, out HostSettings? current) && current == settings)
                {
                    return;
                }
                _devices[address] = settings;
                changed = true;
                SaveSettings();
            }
        }
        finally
        {
            if (changed)
            {
                HostSettingsChanged?.Invoke(address, settings);
            }
        }
    }

    internal void SetModel(ulong address, DeviceModel model) => Store(_models, address, model);

    internal void SetCapabilities(ulong address, DeviceCapabilities capabilities) => Store(_capabilities, address, capabilities);

    /// <summary>
    /// Records the install date of the pair's Windows device entry (<see cref="Native.GetDeviceInstallDate"/>). A date
    /// other than the stored one means the pair was removed and paired again: its remembered capabilities and pod-saved
    /// settings are cleared and true is returned. The first date is only stored.
    /// </summary>
    internal bool SetPairingDate(ulong address, DateTime date)
    {
        lock (_lock)
        {
            bool known = _pairingDates.TryGetValue(address, out DateTime stored);
            if (known && stored == date)
            {
                return false;
            }
            _pairingDates[address] = date;
            if (known)
            {
                _capabilities.Remove(address);
                _podSettings.Remove(address);
            }
            SaveSettings();
            Log.Info("settings", known
                ? $"{Mac.Format(address)}: paired again (Windows entry {stored:o} -> {date:o}); remembered capabilities and pod settings cleared"
                : $"{Mac.Format(address)}: pairing date {date:o} stored");
            return known;
        }
    }

    internal void SetPodSettings(ulong address, PodSettings settings) => Store(_podSettings, address, settings);

    /// <summary>Stores a valid (<see cref="Aap.IsValidStemModes"/>) stem cycle mask.</summary>
    internal void SetStemModes(ulong address, StemModes modes) => Store(_stemModes, address, modes);

    internal void SetEarDetection(ulong address, bool on)
    {
        if (Store(_earDetection, address, on))
        {
            Log.Info("settings", $"{Mac.Format(address)}: earDetection = {on}");
        }
    }

    internal void SetAllowOff(ulong address, bool on)
    {
        if (Store(_allowOff, address, on))
        {
            Log.Info("settings", $"{Mac.Format(address)}: allowOff = {on}");
        }
    }

    internal void SetMicSide(ulong address, MicSide side)
    {
        if (Store(_micSide, address, side))
        {
            Log.Info("settings", $"{Mac.Format(address)}: micSide = {side}");
        }
    }

    /// <summary>Stores keys; false when identical to the stored ones (nothing written).</summary>
    internal bool SetKeys(ulong address, ProximityKeys keys)
    {
        lock (_lock)
        {
            if (_keys.TryGetValue(address, out ProximityKeys? current)
                && current.Irk.AsSpan().SequenceEqual(keys.Irk)
                && current.EncKey.AsSpan().SequenceEqual(keys.EncKey))
            {
                return false;
            }
            _keys[address] = keys;
            SaveKeys();
            Log.Info("settings", $"proximity keys stored for {Mac.Format(address)}");
            return true;
        }
    }

    // False when value equals the stored one (nothing written).
    private bool Store<T>(Dictionary<ulong, T> map, ulong address, T value)
    {
        lock (_lock)
        {
            if (map.TryGetValue(address, out T? current) && EqualityComparer<T>.Default.Equals(current, value))
            {
                return false;
            }
            map[address] = value;
            SaveSettings();
            return true;
        }
    }

    private T Read<T>(Dictionary<ulong, T> map, ulong address, T fallback)
    {
        lock (_lock)
        {
            return map.GetValueOrDefault(address, fallback);
        }
    }

    private void LoadSettings()
    {
        try
        {
            using FileStream stream = File.OpenRead(_settingsPath);
            SettingsFile? file = JsonSerializer.Deserialize(stream, SettingsJson.Default.SettingsFile);
            if (file is null)
            {
                Log.Error("settings", "settings.json is null, using defaults");
                LoadFailed = true;
                return;
            }
            // A JSON null bypasses the nullable annotations: every collection, entry and UI string is checked here.
            UiPrefs ui = file.Ui ?? UiPrefs.Default;
            _ui = ui with { Theme = ui.Theme ?? UiPrefs.Default.Theme, Language = ui.Language ?? UiPrefs.Default.Language };
            _mic = file.Mic is { } mic ? LoadMic(mic) : MicConfig.Default;
            if (file.LastUsed is { } last && Mac.TryParse(last, out ulong lastUsed))
            {
                _lastUsed = lastUsed;
            }
            foreach ((string mac, HostSettings? settings) in file.Devices ?? [])
            {
                if (settings is not null && Mac.TryParse(mac, out ulong address))
                {
                    _devices[address] = Normalize(address, settings);
                }
            }
            foreach ((string mac, DeviceModel? model) in file.Models ?? [])
            {
                if (model is { Number: not null, Name: not null } && Mac.TryParse(mac, out ulong address))
                {
                    _models[address] = model;
                }
            }
            LoadMap(file.Capabilities, _capabilities);
            foreach ((string mac, StemModes modes) in file.StemModes ?? [])
            {
                if (!Mac.TryParse(mac, out ulong address))
                {
                    continue;
                }
                if (Aap.IsValidStemModes(modes))
                {
                    _stemModes[address] = modes;
                }
                else
                {
                    Log.Warn("settings", $"{mac}: stemModes {modes} invalid (needs 2+ of Off, NC, Transparency, Adaptive), using the default");
                }
            }
            LoadMap(file.EarDetection, _earDetection);
            LoadMap(file.AllowOff, _allowOff);
            foreach ((string mac, MicSide side) in file.MicSide ?? [])
            {
                if (!Mac.TryParse(mac, out ulong address))
                {
                    continue;
                }
                if (Enum.IsDefined(side))
                {
                    _micSide[address] = side;
                }
                else
                {
                    Log.Warn("settings", $"{mac}: micSide {side} invalid, using Automatic");
                }
            }
            LoadMap(file.PodSettings, _podSettings);
            foreach ((string mac, DateTime date) in file.PairingDates ?? [])
            {
                if (Mac.TryParse(mac, out ulong address))
                {
                    _pairingDates[address] = date.ToUniversalTime();
                }
            }
        }
        catch (Exception e)
        {
            // Unreadable or malformed file: start from defaults, never from a partial load; the next change rewrites it.
            Log.Error("settings", "settings.json unreadable, using defaults", e);
            LoadFailed = true;
            _lastUsed = 0;
            _ui = UiPrefs.Default;
            _mic = MicConfig.Default;
            _devices.Clear();
            _models.Clear();
            _capabilities.Clear();
            _stemModes.Clear();
            _earDetection.Clear();
            _allowOff.Clear();
            _micSide.Clear();
            _podSettings.Clear();
            _pairingDates.Clear();
        }
    }

    // Fills values missing from an older file and clamps out-of-range ones from a hand-edited file. The source-generated
    // reader sets a missing init property to 0 instead of keeping its default, and 0 is never a valid duck percent.
    private static HostSettings Normalize(ulong address, HostSettings settings)
    {
        if (settings.DuckPercent == 0)
        {
            settings = settings with { DuckPercent = HostSettings.DefaultDuckPercent };
        }
        int duck = Math.Clamp(settings.DuckPercent, HostSettings.MinDuckPercent, HostSettings.MaxDuckPercent);
        if (duck != settings.DuckPercent)
        {
            Log.Warn("settings", $"{Mac.Format(address)}: duckPercent {settings.DuckPercent} out of range, clamped to {duck}");
            settings = settings with { DuckPercent = duck };
        }
        return settings;
    }

    // Each field falls back on its own: missing, empty or invalid modes -> all; an unknown or missing model -> the
    // mode's default; an unknown enhancement -> Off.
    private static MicConfig LoadMic(MicFile file)
    {
        if (!MicConfig.TryParseModes(file.Modes, out HashSet<MicMode> modes))
        {
            Log.Warn("settings", $"mic modes [{string.Join(',', file.Modes ?? [])}] invalid, enabling all");
            modes = [.. MicConfig.AllModes];
        }
        var models = new Dictionary<MicMode, string>(MicConfig.AllModes.Length);
        foreach (MicMode mode in MicConfig.AllModes)
        {
            string? id = file.Models?.GetValueOrDefault(ModeName(mode));
            if (id is null || MicCatalog.FindModel(mode, id) is null)
            {
                Log.Warn("settings", $"mic model {id ?? "none"} for {mode} unknown, using the default");
                id = MicCatalog.DefaultModel(mode);
            }
            models[mode] = id;
        }
        string? enhancement = file.Enhancement;
        if (enhancement is not null && MicCatalog.FindEnhancement(enhancement) is null)
        {
            Log.Warn("settings", $"mic enhancement {enhancement} unknown, using Off");
            enhancement = null;
        }
        return new MicConfig(modes, models, enhancement);
    }

    private static MicFile SaveMic(MicConfig config)
    {
        var modes = new List<string>(MicConfig.AllModes.Length);
        var models = new Dictionary<string, string>(MicConfig.AllModes.Length);
        foreach (MicMode mode in MicConfig.AllModes)
        {
            string name = ModeName(mode);
            if (config.Modes.Contains(mode))
            {
                modes.Add(name);
            }
            models[name] = config.Models[mode];
        }
        return new MicFile { Modes = modes, Models = models, Enhancement = config.Enhancement };
    }

    // Lower-camel, e.g. VoiceIsolation -> "voiceIsolation".
    private static string ModeName(MicMode mode) => JsonNamingPolicy.CamelCase.ConvertName(mode.ToString());

    // One-time import of %UserProfile%\.wpods\device_settings.jsonl (left in place).
    private void Migrate(string legacyFile)
    {
        try
        {
            foreach (string line in File.ReadLines(legacyFile))
            {
                LegacyRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize(line, SettingsJson.Default.LegacyRecord);
                }
                catch (JsonException e)
                {
                    Log.Warn("settings", $"legacy settings line skipped: {e.Message}");
                    continue;
                }
                if (record?.Mac is not { } mac || !Mac.TryParse(mac, out ulong address))
                {
                    continue;
                }
                _devices[address] = new HostSettings
                {
                    DisableHandsFree = record.DisableHandsFreeMode ?? true,
                    LowLatency = record.LowLatencyMode ?? false,
                    MicMode = record.MicMode == "noiseCancelling" ? MicMode.VoiceIsolation : MicMode.Standard,
                };
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing to migrate.
            Log.Warn("settings", $"legacy settings not migrated: {e.Message}");
        }
    }

    private void SaveSettings()
    {
        var file = new SettingsFile { LastUsed = _lastUsed == 0 ? null : Mac.Format(_lastUsed), Ui = _ui, Mic = SaveMic(_mic) };
        SaveMap(_devices, file.Devices);
        SaveMap(_models, file.Models);
        SaveMap(_capabilities, file.Capabilities);
        SaveMap(_stemModes, file.StemModes);
        SaveMap(_earDetection, file.EarDetection);
        SaveMap(_allowOff, file.AllowOff);
        SaveMap(_micSide, file.MicSide);
        SaveMap(_podSettings, file.PodSettings);
        SaveMap(_pairingDates, file.PairingDates);
        WriteAtomic(_settingsPath, JsonSerializer.SerializeToUtf8Bytes(file, SettingsJson.Default.SettingsFile));
    }

    private static void SaveMap<T>(Dictionary<ulong, T> source, Dictionary<string, T> target)
    {
        foreach ((ulong address, T value) in source)
        {
            target[Mac.Format(address)] = value;
        }
    }

    private static void LoadMap<T>(Dictionary<string, T>? source, Dictionary<ulong, T> target)
    {
        foreach ((string mac, T value) in source ?? [])
        {
            if (value is not null && Mac.TryParse(mac, out ulong address))
            {
                target[address] = value;
            }
        }
    }

    // keys.bin = DPAPI(record*), record = address u64 LE | IRK 16 | ENC_KEY 16.
    private void LoadKeys()
    {
        byte[]? plain;
        try
        {
            plain = Native.Dpapi(File.ReadAllBytes(_keysPath), protect: false);
        }
        catch (Exception e)
        {
            Log.Error("settings", "keys.bin unreadable", e);
            return;
        }
        if (plain is null)
        {
            Log.Warn("settings", "keys.bin could not be decrypted");
            return;
        }
        for (int offset = 0; offset + KeyRecordSize <= plain.Length; offset += KeyRecordSize)
        {
            ReadOnlySpan<byte> record = plain.AsSpan(offset, KeyRecordSize);
            _keys[BinaryPrimitives.ReadUInt64LittleEndian(record)] = new ProximityKeys(record.Slice(8, 16).ToArray(), record.Slice(24, 16).ToArray());
        }
        Array.Clear(plain);
    }

    private void SaveKeys()
    {
        byte[] plain = new byte[_keys.Count * KeyRecordSize];
        int offset = 0;
        foreach ((ulong address, ProximityKeys keys) in _keys)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(plain.AsSpan(offset), address);
            keys.Irk.CopyTo(plain, offset + 8);
            keys.EncKey.CopyTo(plain, offset + 24);
            offset += KeyRecordSize;
        }
        byte[]? protectedData = Native.Dpapi(plain, protect: true);
        Array.Clear(plain);
        if (protectedData is not null)
        {
            WriteAtomic(_keysPath, protectedData);
        }
    }

    private void WriteAtomic(string path, ReadOnlySpan<byte> data)
    {
        Directory.CreateDirectory(_directory);
        string temp = path + ".tmp";
        // Data on disk before the rename: after a power loss NTFS can keep the rename but lose unflushed data.
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}

internal sealed class SettingsFile
{
    public string? LastUsed { get; set; }
    public UiPrefs? Ui { get; set; }
    /// <summary>App-wide mic settings (optional; older files have none, missing = <see cref="MicConfig.Default"/>).</summary>
    public MicFile? Mic { get; set; }
    public Dictionary<string, HostSettings> Devices { get; set; } = [];
    public Dictionary<string, DeviceModel> Models { get; set; } = [];
    public Dictionary<string, DeviceCapabilities> Capabilities { get; set; } = [];
    /// <summary>Stem press-and-hold cycle per pair (optional; older files have none).</summary>
    public Dictionary<string, StemModes> StemModes { get; set; } = [];
    /// <summary>Ear detection (0x0A) per pair (optional; older files have none, missing = on).</summary>
    public Dictionary<string, bool> EarDetection { get; set; } = [];
    /// <summary>Allow Off (0x34) per pair (optional; older files have none, missing = off).</summary>
    public Dictionary<string, bool> AllowOff { get; set; } = [];
    /// <summary>Microphone side (0x01) per pair (optional; older files have none, missing = Automatic).</summary>
    public Dictionary<string, MicSide> MicSide { get; set; } = [];
    /// <summary>Last seen pod-saved settings per pair, for display only (optional; older files have none).</summary>
    public Dictionary<string, PodSettings> PodSettings { get; set; } = [];
    /// <summary>Install date (UTC) of each pair's Windows device entry, for re-pair detection (optional; older files have none).</summary>
    public Dictionary<string, DateTime> PairingDates { get; set; } = [];
}

/// <summary>settings.json "mic" as plain strings, so a bad value falls back on its own instead of failing the file.</summary>
internal sealed class MicFile
{
    /// <summary>Enabled modes, lower-camel, in Apple's order.</summary>
    public List<string>? Modes { get; set; }
    /// <summary>Model id per lower-camel mode name.</summary>
    public Dictionary<string, string>? Models { get; set; }
    public string? Enhancement { get; set; }
}

internal sealed class LegacyRecord
{
    public string? Mac { get; set; }
    public bool? DisableHandsFreeMode { get; set; }
    public bool? LowLatencyMode { get; set; }
    public string? MicMode { get; set; }
}

internal sealed class MicModeJsonConverter : JsonConverter<MicMode>
{
    public override MicMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        MicMode? legacy = text?.ToLowerInvariant() switch
        {
            "voiceiso1" or "voiceiso2" => MicMode.Standard,
            "voiceiso3" or "voiceiso4" => MicMode.VoiceIsolation,
            _ => null,
        };
        if (legacy is { } migrated)
        {
            Log.Info("settings", $"stored micMode {text} migrated to {migrated}");
            return migrated;
        }
        if (!MicConfig.TryParseMode(text, out MicMode mode))
        {
            throw new JsonException($"Unknown micMode {text ?? reader.TokenType.ToString()}.");
        }
        return mode;
    }

    public override void Write(Utf8JsonWriter writer, MicMode value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(SettingsFile))]
[JsonSerializable(typeof(LegacyRecord))]
internal sealed partial class SettingsJson : JsonSerializerContext;
