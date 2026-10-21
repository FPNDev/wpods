using System.Buffers.Binary;
using System.Runtime.InteropServices;
using WPods.Core;

namespace WPods.Audio;

/// <summary>
/// Loops an inaudible 18.5 kHz, 1-LSB tone on the default output so Windows keeps the Bluetooth audio stream
/// open (no wake-up delay). Active only while a session is attached and that device's LowLatency host setting
/// is on. Create before <see cref="ConnectionManager.Start"/>.
/// </summary>
public sealed unsafe partial class LowLatencyKeepAlive
{
    private const uint SndAsync = 0x1;
    private const uint SndMemory = 0x4;
    private const uint SndLoop = 0x8;
    private const uint SndPurge = 0x40;
    private const int SampleRate = 44_100;
    private const int ToneHz = 18_500;
    private const int Channels = 2;
    private const int DataLength = SampleRate * Channels * sizeof(short); // 1 s; 18 500 whole cycles, loops seamlessly
    private const int HeaderLength = 44;

    private readonly SettingsStore _settings;
    private readonly Lock _lock = new();
    private byte[]? _wave;
    private ulong _address;
    private bool _playing;

    public LowLatencyKeepAlive(ConnectionManager connections)
    {
        _settings = connections.Settings;
        connections.Attached += OnAttached;
        connections.Detached += OnDetached;
        _settings.HostSettingsChanged += OnHostSettingsChanged;
    }

    /// <summary>
    /// Windows refused to play the keep-alive sound. Raised on the thread that applied the change (attach:
    /// thread-pool; settings change: the caller of SetHostSettingsAsync); must not block.
    /// </summary>
    public event Action? Failed;

    private void OnAttached(AapSession session)
    {
        lock (_lock)
        {
            _address = session.Address;
            ApplyLocked();
        }
    }

    private void OnDetached(AapSession session)
    {
        lock (_lock)
        {
            if (_address == session.Address)
            {
                _address = 0;
                ApplyLocked();
            }
        }
    }

    private void OnHostSettingsChanged(ulong address, HostSettings settings)
    {
        lock (_lock)
        {
            if (address == _address)
            {
                ApplyLocked();
            }
        }
    }

    private void ApplyLocked()
    {
        bool wanted = _address != 0 && _settings.Get(_address).LowLatency;
        if (wanted == _playing)
        {
            return;
        }
        if (!wanted)
        {
            PlaySound(null, 0, SndPurge);
            _playing = false;
            Log.Info("keepalive", "stopped");
            return;
        }
        _wave ??= CreateWave();
        fixed (byte* wave = _wave)
        {
            _playing = PlaySound(wave, 0, SndMemory | SndLoop | SndAsync);
        }
        if (!_playing)
        {
            Log.Warn("keepalive", $"PlaySound refused for {Mac.Format(_address)}");
            Failed?.Invoke();
            return;
        }
        Log.Info("keepalive", $"started for {Mac.Format(_address)}");
    }

    // Built once in the pinned heap: PlaySound reads it asynchronously for as long as it loops.
    private static byte[] CreateWave()
    {
        byte[] wave = GC.AllocateArray<byte>(HeaderLength + DataLength, pinned: true);
        Span<byte> span = wave;
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], HeaderLength - 8 + DataLength);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * Channels * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], Channels * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], DataLength);
        Span<short> samples = MemoryMarshal.Cast<byte, short>(span[HeaderLength..]);
        for (int n = 0; n < SampleRate; n++)
        {
            short value = (short)Math.Round(Math.Sin(2 * Math.PI * ToneHz * n / SampleRate));
            samples[2 * n] = value;
            samples[2 * n + 1] = value;
        }
        return wave;
    }

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySound(void* sound, nint module, uint flags);
}
