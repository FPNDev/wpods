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
            if (!Enum.TryParse(name, ignoreCase: true, out MicMode mode) || !Enum.IsDefined(mode) || char.IsAsciiDigit(name[0]))
            {
                return false;
            }
            modes.Add(mode);
        }
        return true;
    }
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
    public HostSettings Get(ulong address)
    {
        lock (_lock)
        {
            return _devices.GetValueOrDefault(address, HostSettings.Default);
        }
    }

    /// <summary>Last model seen for this pair; null if it was never attached.</summary>
    public DeviceModel? GetModel(ulong address)
    {
        lock (_lock)
        {
            return _models.GetValueOrDefault(address);
        }
    }

    /// <summary>Stem press-and-hold cycle of the pair; <see cref="DefaultStemModes"/> when never set.</summary>
    internal StemModes GetStemModes(ulong address)
    {
        lock (_lock)
        {
            return _stemModes.GetValueOrDefault(address, DefaultStemModes);
        }
    }

    /// <summary>Ear detection (0x0A) of the pair as last set by the user or reported by the device; true (Apple default) when never set.</summary>
    internal bool GetEarDetection(ulong address)
    {
        lock (_lock)
        {
            return _earDetection.GetValueOrDefault(address, true);
        }
    }

    /// <summary>Microphone side (0x01) of the pair as last set by the user or reported by the device; Automatic when never set.</summary>
    internal MicSide GetMicSide(ulong address)
    {
        lock (_lock)
        {
            return _micSide.GetValueOrDefault(address, MicSide.Automatic);
        }
    }

    /// <summary>Every capability the pair reported on any attach; None if it was never attached.</summary>
    internal DeviceCapabilities GetCapabilities(ulong address)
    {
        lock (_lock)
        {
            return _capabilities.GetValueOrDefault(address);
        }
    }

    /// <summary>Pod-saved settings of the pair as last seen; <see cref="PodSettings.None"/> if never seen.</summary>
    internal PodSettings GetPodSettings(ulong address)
    {
        lock (_lock)
        {
            return _podSettings.GetValueOrDefault(address, PodSettings.None);
        }
    }

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

    internal void Set(ulong address, HostSettings settings)
    {
        lock (_lock)
        {
            if (_devices.TryGetValue(address, out HostSettings? current) && current == settings)
            {
                return;
            }
            _devices[address] = settings;
            SaveSettings();
        }
        HostSettingsChanged?.Invoke(address, settings);
    }

    internal void SetModel(ulong address, DeviceModel model)
    {
        lock (_lock)
        {
            if (_models.TryGetValue(address, out DeviceModel? current) && current == model)
            {
                return;
            }
            _models[address] = model;
            SaveSettings();
        }
    }

    internal void SetCapabilities(ulong address, DeviceCapabilities capabilities)
    {
        lock (_lock)
        {
            if (_capabilities.TryGetValue(address, out DeviceCapabilities current) && current == capabilities)
            {
                return;
            }
            _capabilities[address] = capabilities;
            SaveSettings();
        }
    }

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

    internal void SetPodSettings(ulong address, PodSettings settings)
    {
        lock (_lock)
        {
            if (_podSettings.TryGetValue(address, out PodSettings? current) && current == settings)
            {
                return;
            }
            _podSettings[address] = settings;
            SaveSettings();
        }
    }

    /// <summary>Stores a valid (<see cref="Aap.IsValidStemModes"/>) stem cycle mask.</summary>
    internal void SetStemModes(ulong address, StemModes modes)
    {
        lock (_lock)
        {
            if (_stemModes.TryGetValue(address, out StemModes current) && current == modes)
            {
                return;
            }
            _stemModes[address] = modes;
            SaveSettings();
        }
    }

    internal void SetEarDetection(ulong address, bool on)
    {
        lock (_lock)
        {
            if (_earDetection.TryGetValue(address, out bool current) && current == on)
            {
                return;
            }
            _earDetection[address] = on;
            SaveSettings();
            Log.Info("settings", $"{Mac.Format(address)}: earDetection = {on}");
        }
    }

    internal void SetMicSide(ulong address, MicSide side)
    {
        lock (_lock)
        {
            if (_micSide.TryGetValue(address, out MicSide current) && current == side)
            {
                return;
            }
            _micSide[address] = side;
            SaveSettings();
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

    private void LoadSettings()
    {
        try
        {
            using FileStream stream = File.OpenRead(_settingsPath);
            SettingsFile? file = JsonSerializer.Deserialize(stream, SettingsJson.Default.SettingsFile);
            if (file is null)
            {
                return;
            }
            _ui = file.Ui ?? UiPrefs.Default;
            _mic = file.Mic is { } mic ? LoadMic(mic) : MicConfig.Default;
            if (file.LastUsed is { } last && Mac.TryParse(last, out ulong lastUsed))
            {
                _lastUsed = lastUsed;
            }
            foreach ((string mac, HostSettings settings) in file.Devices)
            {
                if (Mac.TryParse(mac, out ulong address))
                {
                    _devices[address] = Normalize(address, settings);
                }
            }
            foreach ((string mac, DeviceModel model) in file.Models)
            {
                if (Mac.TryParse(mac, out ulong address))
                {
                    _models[address] = model;
                }
            }
            foreach ((string mac, DeviceCapabilities capabilities) in file.Capabilities)
            {
                if (Mac.TryParse(mac, out ulong address))
                {
                    _capabilities[address] = capabilities;
                }
            }
            foreach ((string mac, StemModes modes) in file.StemModes)
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
            foreach ((string mac, bool on) in file.EarDetection)
            {
                if (Mac.TryParse(mac, out ulong address))
                {
                    _earDetection[address] = on;
                }
            }
            foreach ((string mac, MicSide side) in file.MicSide)
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
            foreach ((string mac, PodSettings settings) in file.PodSettings)
            {
                if (Mac.TryParse(mac, out ulong address))
                {
                    _podSettings[address] = settings;
                }
            }
            foreach ((string mac, DateTime date) in file.PairingDates)
            {
                if (Mac.TryParse(mac, out ulong address))
                {
                    _pairingDates[address] = date.ToUniversalTime();
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable file: start from defaults; the next change rewrites it.
            Log.Error("settings", "settings.json unreadable, using defaults", e);
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
        foreach ((ulong address, HostSettings settings) in _devices)
        {
            file.Devices[Mac.Format(address)] = settings;
        }
        foreach ((ulong address, DeviceModel model) in _models)
        {
            file.Models[Mac.Format(address)] = model;
        }
        foreach ((ulong address, DeviceCapabilities capabilities) in _capabilities)
        {
            file.Capabilities[Mac.Format(address)] = capabilities;
        }
        foreach ((ulong address, StemModes modes) in _stemModes)
        {
            file.StemModes[Mac.Format(address)] = modes;
        }
        foreach ((ulong address, bool on) in _earDetection)
        {
            file.EarDetection[Mac.Format(address)] = on;
        }
        foreach ((ulong address, MicSide side) in _micSide)
        {
            file.MicSide[Mac.Format(address)] = side;
        }
        foreach ((ulong address, PodSettings settings) in _podSettings)
        {
            file.PodSettings[Mac.Format(address)] = settings;
        }
        foreach ((ulong address, DateTime date) in _pairingDates)
        {
            file.PairingDates[Mac.Format(address)] = date;
        }
        WriteAtomic(_settingsPath, JsonSerializer.SerializeToUtf8Bytes(file, SettingsJson.Default.SettingsFile));
    }

    // keys.bin = DPAPI(record*), record = address u64 LE | IRK 16 | ENC_KEY 16.
    private void LoadKeys()
    {
        byte[]? plain;
        try
        {
            plain = Native.Dpapi(File.ReadAllBytes(_keysPath), protect: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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
        File.WriteAllBytes(temp, data);
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

/// <summary>
/// MicMode as its member name; on read also migrates the removed test modes (Round 19): VoiceIso1/VoiceIso2 ->
/// Standard, VoiceIso3/VoiceIso4 -> VoiceIsolation. The migrated value is written back on the next save.
/// </summary>
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
        if (text is null || !Enum.TryParse(text, ignoreCase: true, out MicMode mode) || !Enum.IsDefined(mode) || char.IsAsciiDigit(text[0]))
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
