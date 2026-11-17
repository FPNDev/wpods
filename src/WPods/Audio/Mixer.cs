using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WPods.Core;

namespace WPods.Audio;

/// <summary>wpods_native flow values.</summary>
public enum MixerFlow
{
    Output = 0,
    Input = 1,
}

/// <summary>Windows DEVICE_STATE_* values.</summary>
public enum MixerState
{
    Active = 1,
    Disabled = 2,
    NotPresent = 4,
    Unplugged = 8,
}

/// <summary>
/// <paramref name="Pair"/>: the AirPods pair whose Hands-Free mic this is, else 0. <paramref name="OwnMic"/>: WPods'
/// virtual mic, which the mixer plays directly from the mic pipeline instead of through Windows.
/// </summary>
public sealed record MixerDevice(string Id, string Name, MixerFlow Flow, MixerState State, ulong Pair, bool OwnMic);

/// <summary>
/// Plays the endpoints picked on the Mixer page in the attached pair, each at its own volume, on top of Windows audio,
/// and forgets the settings of endpoints Windows no longer knows. Create before <see cref="ConnectionManager.Start"/>.
/// </summary>
public sealed unsafe partial class Mixer : IDisposable
{
    // wpn_mixer_event_cb kind: a log line from the direct source's jitter buffer.
    private const int EventLogLine = 2;

    private const uint FlagOwnMic = 1;

    private readonly ConnectionManager _connections;
    private readonly SettingsStore _settings;
    private readonly MicPipeline _mic;
    private readonly Lock _lock = new();
    private readonly bool _available;
    private GCHandle _self;
    // Under _lock.
    private IReadOnlyList<MixerDevice> _devices = [];
    private HashSet<string> _ids = [];
    private HashSet<string> _ownMicIds = [];
    private HashSet<ulong> _pairs = [];
    private ulong _target;
    private bool _refreshPending;
    private bool _refreshing;
    private bool _disposed;

    public Mixer(ConnectionManager connections, MicPipeline mic)
    {
        _connections = connections;
        _settings = connections.Settings;
        _mic = mic;
        _self = GCHandle.Alloc(this);
        int hr = wpn_mixer_init(&OnEvent, GCHandle.ToIntPtr(_self));
        _available = hr >= 0;
        if (!_available)
        {
            Log.Warn("mixer", $"engine not available: init error 0x{hr:X8}");
        }
        MixerConfig config = _settings.Mixer;
        if (config.Enabled)
        {
            foreach (string id in config.Sources)
            {
                SetSource(id, config.Volume(id));
            }
        }
        connections.Attached += OnAttached;
        connections.Detached += OnDetached;
        connections.DevicesChanged += OnPairsChanged;
        if (_available)
        {
            mic.Written += PushMic;
        }
        Refresh();
    }

    /// <summary>The device list or the settings changed; raised on a thread-pool thread.</summary>
    public event Action? Changed;

    /// <summary>Inputs first, then outputs; in each, active endpoints first, then by name.</summary>
    public IReadOnlyList<MixerDevice> Devices
    {
        get
        {
            lock (_lock)
            {
                return _devices;
            }
        }
    }

    /// <summary>Picks or unpicks a listed endpoint; its volume is kept either way.</summary>
    public void SetPicked(string id, bool on)
    {
        lock (_lock)
        {
            MixerConfig config = _settings.Mixer;
            if (_disposed || !_ids.Contains(id) || config.Sources.Contains(id) == on)
            {
                return;
            }
            var sources = new HashSet<string>(config.Sources);
            if (on)
            {
                sources.Add(id);
                if (config.Enabled)
                {
                    SetSource(id, config.Volume(id));
                }
            }
            else
            {
                sources.Remove(id);
                RemoveSource(id);
            }
            Save(config with { Sources = sources });
            Log.Info("mixer", $"{(on ? "picked" : "unpicked")} {id}");
            UpdateDirectLocked();
        }
    }

    /// <summary>Sets a listed endpoint's volume (0..100) at once; saved only when <paramref name="save"/> (slider released).</summary>
    public void SetVolume(string id, int percent, bool save)
    {
        lock (_lock)
        {
            MixerConfig config = _settings.Mixer;
            if (_disposed || !_ids.Contains(id))
            {
                return;
            }
            if (config.Enabled && config.Sources.Contains(id))
            {
                SetSource(id, percent);
            }
            if (save)
            {
                Save(config with { Volumes = new Dictionary<string, int>(config.Volumes) { [id] = percent } });
            }
        }
    }

    /// <summary>Plays the picked endpoints or stops them all; the picks and volumes stay either way.</summary>
    public void SetEnabled(bool on)
    {
        lock (_lock)
        {
            MixerConfig config = _settings.Mixer;
            if (_disposed || config.Enabled == on)
            {
                return;
            }
            foreach (string id in config.Sources)
            {
                if (on)
                {
                    SetSource(id, config.Volume(id));
                }
                else
                {
                    RemoveSource(id);
                }
            }
            Save(config with { Enabled = on });
            Log.Info("mixer", on ? "turned on" : "turned off");
            UpdateDirectLocked();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _connections.Attached -= OnAttached;
        _connections.Detached -= OnDetached;
        _connections.DevicesChanged -= OnPairsChanged;
        _mic.Written -= PushMic;
        _mic.SetDirectListener(false);
        // No callback runs after this returns, so the handle can go.
        wpn_mixer_shutdown();
        _self.Free();
    }

    private void OnAttached(AapSession session)
    {
        lock (_lock)
        {
            SetTargetLocked(session.Address);
        }
    }

    private void OnDetached(AapSession session)
    {
        lock (_lock)
        {
            if (_target == session.Address)
            {
                SetTargetLocked(0);
            }
        }
    }

    private void SetTargetLocked(ulong address)
    {
        if (_disposed || _target == address || !_available)
        {
            return;
        }
        _target = address;
        int hr = wpn_mixer_set_target(address);
        Log.Info("mixer", $"output {(address == 0 ? "none" : Mac.Format(address))}, hr=0x{hr:X8}");
        UpdateDirectLocked();
    }

    // The mic pipeline streams the AirPods mic while the mixer plays WPods' mic (on, picked, with a pair attached).
    private void UpdateDirectLocked()
    {
        MixerConfig config = _settings.Mixer;
        _mic.SetDirectListener(!_disposed && _target != 0 && config.Enabled && config.Sources.Overlaps(_ownMicIds));
    }

    // Mic thread: each processed block, for the direct source (ignored by the engine while it has none).
    private void PushMic(ReadOnlySpan<short> pcm, long position)
    {
        fixed (short* samples = pcm)
        {
            wpn_mixer_push_mic(samples, (uint)pcm.Length, position);
        }
    }

    // A newly paired pair's outputs must leave the list and its Hands-Free mic be marked.
    private void OnPairsChanged()
    {
        List<KnownDevice> devices = _connections.GetDevices();
        bool known;
        lock (_lock)
        {
            known = devices.TrueForAll(d => _pairs.Contains(d.Address));
        }
        if (!known)
        {
            Refresh();
        }
    }

    // Coalesced: one worker at a time, which runs again when asked meanwhile.
    private void Refresh()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _refreshPending = true;
            if (_refreshing)
            {
                return;
            }
            _refreshing = true;
        }
        ThreadPool.QueueUserWorkItem(static self => self.RunRefresh(), this, preferLocal: false);
    }

    private void RunRefresh()
    {
        while (true)
        {
            lock (_lock)
            {
                if (!_refreshPending || _disposed)
                {
                    _refreshing = false;
                    return;
                }
                _refreshPending = false;
            }
            if (Enumerate())
            {
                Changed?.Invoke();
            }
        }
    }

    // False when Windows could not list the endpoints: the previous list and the settings stay.
    private bool Enumerate()
    {
        var pairs = new HashSet<ulong>();
        foreach (KnownDevice device in _connections.GetDevices())
        {
            pairs.Add(device.Address);
        }
        ulong[] known = [.. pairs];
        var found = new List<MixerDevice>();
        GCHandle list = GCHandle.Alloc(found);
        int hr;
        try
        {
            fixed (ulong* addresses = known)
            {
                hr = wpn_mixer_enum_devices(addresses, (uint)known.Length, &OnDevice, GCHandle.ToIntPtr(list));
            }
        }
        finally
        {
            list.Free();
        }
        if (hr < 0)
        {
            Log.Warn("mixer", $"endpoints not listed: error 0x{hr:X8}");
            return false;
        }
        found.Sort(static (a, b) =>
            a.Flow != b.Flow ? b.Flow.CompareTo(a.Flow)
            : (a.State == MixerState.Active) != (b.State == MixerState.Active) ? (a.State == MixerState.Active ? -1 : 1)
            : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        var ids = new HashSet<string>(found.Count);
        var ownMicIds = new HashSet<string>();
        foreach (MixerDevice device in found)
        {
            ids.Add(device.Id);
            if (device.OwnMic && device.State != MixerState.NotPresent)
            {
                ownMicIds.Add(device.Id);
            }
        }
        // Not-present endpoints (unplugged or removed hardware, old jacks) stay known, so a pick survives until the
        // device is back, but are listed only when picked: like the Sound control panel, which never shows them.
        IReadOnlySet<string> picked = _settings.Mixer.Sources;
        found.RemoveAll(d => d.State == MixerState.NotPresent && !picked.Contains(d.Id));
        lock (_lock)
        {
            if (_disposed)
            {
                return false;
            }
            _devices = found;
            _ids = ids;
            _ownMicIds = ownMicIds;
            _pairs = pairs;
            ForgetGoneLocked();
            UpdateDirectLocked();
        }
        return true;
    }

    // Endpoints Windows no longer knows in any state are gone for good: drop their pick and volume.
    private void ForgetGoneLocked()
    {
        MixerConfig config = _settings.Mixer;
        var sources = new HashSet<string>(config.Sources);
        var volumes = new Dictionary<string, int>(config.Volumes);
        foreach (string id in config.Sources)
        {
            if (!_ids.Contains(id))
            {
                sources.Remove(id);
                RemoveSource(id);
                Log.Info("mixer", $"forgot {id}: no longer in Windows");
            }
        }
        foreach (string id in config.Volumes.Keys)
        {
            if (!_ids.Contains(id))
            {
                volumes.Remove(id);
            }
        }
        if (sources.Count != config.Sources.Count || volumes.Count != config.Volumes.Count)
        {
            Save(config with { Sources = sources, Volumes = volumes });
        }
    }

    private void SetSource(string id, int percent)
    {
        if (!_available)
        {
            return;
        }
        // Squared: a roughly even loudness step across the slider (50% = -12 dB).
        float level = percent / 100f;
        int hr = wpn_mixer_set_source(id, level * level);
        if (hr < 0)
        {
            Log.Warn("mixer", $"source {id} not set: error 0x{hr:X8}");
        }
    }

    private void RemoveSource(string id)
    {
        if (_available)
        {
            wpn_mixer_remove_source(id);
        }
    }

    private void Save(MixerConfig config)
    {
        try
        {
            _settings.SetMixer(config);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept for this session; the next successful save writes it.
            Log.Warn("mixer", $"settings not saved: {e.Message}");
        }
    }

    // Endpoint notification thread: never blocks, so _lock is taken on the thread pool. A source gone for good shows
    // as missing in the next list.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(nint context, int kind, char* id)
    {
        try
        {
            if (kind == EventLogLine)
            {
                Log.Info("mixer", new string(id));
            }
            else if (GCHandle.FromIntPtr(context).Target is Mixer self)
            {
                ThreadPool.QueueUserWorkItem(static s => s.Refresh(), self, preferLocal: false);
            }
        }
        catch (Exception e)
        {
            Log.Error("mixer", "event callback failed", e);
        }
    }

    // Synchronous, inside wpn_mixer_enum_devices.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnDevice(nint context, char* id, char* name, int flow, uint state, ulong pair, uint flags)
    {
        try
        {
            var list = (List<MixerDevice>)GCHandle.FromIntPtr(context).Target!;
            list.Add(new MixerDevice(new string(id), new string(name), (MixerFlow)flow, (MixerState)state, pair, (flags & FlagOwnMic) != 0));
        }
        catch (Exception e)
        {
            Log.Error("mixer", "device callback failed", e);
        }
    }

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int wpn_mixer_init(delegate* unmanaged[Stdcall]<nint, int, char*, void> callback, nint context);

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial void wpn_mixer_shutdown();

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int wpn_mixer_enum_devices(ulong* pairs, uint pairCount,
        delegate* unmanaged[Stdcall]<nint, char*, char*, int, uint, ulong, uint, void> callback, nint context);

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial void wpn_mixer_push_mic(short* pcm, uint count, long position);

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int wpn_mixer_set_target(ulong btAddress);

    [LibraryImport("wpods_native.dll", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int wpn_mixer_set_source(string id, float gain);

    [LibraryImport("wpods_native.dll", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int wpn_mixer_remove_source(string id);
}
