using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WPods.Core;

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
    public MicMode MicMode { get; init; }
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
    public bool FirstHideToastShown { get; init; }
}

/// <summary>Model reported over AAP 0x1D, remembered for pairs that are not attached.</summary>
public sealed record DeviceModel(string Number, string Name);

/// <summary>
/// %LOCALAPPDATA%\WPods\settings.json (host settings, model and reported capabilities per MAC + last-used MAC) and keys.bin (proximity keys,
/// DPAPI CurrentUser). Loaded once; every change is written atomically. Thread-safe.
/// </summary>
public sealed class SettingsStore
{
    private const int KeyRecordSize = 8 + 16 + 16;

    private readonly Lock _lock = new();
    private readonly string _directory;
    private readonly string _settingsPath;
    private readonly string _keysPath;
    private readonly Dictionary<ulong, HostSettings> _devices = [];
    private readonly Dictionary<ulong, ProximityKeys> _keys = [];
    private readonly Dictionary<ulong, DeviceModel> _models = [];
    private readonly Dictionary<ulong, DeviceCapabilities> _capabilities = [];
    private ulong _lastUsed;
    private UiPrefs _ui = UiPrefs.Default;

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
    }

    /// <summary>Raised on the caller's thread of <see cref="ConnectionManager.SetHostSettingsAsync"/>.</summary>
    public event Action<ulong, HostSettings>? HostSettingsChanged;

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

    /// <summary>Capabilities the pair reported on its last attach; None if it was never attached.</summary>
    internal DeviceCapabilities GetCapabilities(ulong address)
    {
        lock (_lock)
        {
            return _capabilities.GetValueOrDefault(address);
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
            if (file.LastUsed is { } last && Mac.TryParse(last, out ulong lastUsed))
            {
                _lastUsed = lastUsed;
            }
            foreach ((string mac, HostSettings settings) in file.Devices)
            {
                if (Mac.TryParse(mac, out ulong address))
                {
                    _devices[address] = settings;
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
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable file: start from defaults; the next change rewrites it.
        }
    }

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
                catch (JsonException)
                {
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
        }
    }

    private void SaveSettings()
    {
        var file = new SettingsFile { LastUsed = _lastUsed == 0 ? null : Mac.Format(_lastUsed), Ui = _ui };
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
            return;
        }
        if (plain is null)
        {
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
    public Dictionary<string, HostSettings> Devices { get; set; } = [];
    public Dictionary<string, DeviceModel> Models { get; set; } = [];
    public Dictionary<string, DeviceCapabilities> Capabilities { get; set; } = [];
}

internal sealed class LegacyRecord
{
    public string? Mac { get; set; }
    public bool? DisableHandsFreeMode { get; set; }
    public bool? LowLatencyMode { get; set; }
    public string? MicMode { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(SettingsFile))]
[JsonSerializable(typeof(LegacyRecord))]
internal sealed partial class SettingsJson : JsonSerializerContext;
