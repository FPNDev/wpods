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

/// <summary>
/// %LOCALAPPDATA%\WPods\settings.json (host settings per MAC + last-used MAC) and keys.bin (proximity keys,
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
    private ulong _lastUsed;

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

    /// <summary>Stored settings, or <see cref="HostSettings.Default"/> for a device never changed.</summary>
    public HostSettings Get(ulong address)
    {
        lock (_lock)
        {
            return _devices.GetValueOrDefault(address, HostSettings.Default);
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
        var file = new SettingsFile { LastUsed = _lastUsed == 0 ? null : Mac.Format(_lastUsed) };
        foreach ((ulong address, HostSettings settings) in _devices)
        {
            file.Devices[Mac.Format(address)] = settings;
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
    public Dictionary<string, HostSettings> Devices { get; set; } = [];
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
