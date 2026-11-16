using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using WPods.Core;
using Timer = System.Threading.Timer;

namespace WPods.Audio;

internal enum CaptureState
{
    Idle,
    Capturing,
    /// <summary>The virtual mic pipe cannot be opened or its capture state cannot be read.</summary>
    Unavailable,
}

/// <summary>
/// Client of the WPods.Service virtual mic pipe: sends the pair and PCM, receives the capture state (under the internal
/// lock, must not call back in). Failures reopen with capped backoff.
/// </summary>
internal sealed class VirtualMic
{
    private const int MaxPcmBytes = 65536;
    private const int MaxBackoffShift = 6; // 1 s -> 64 s

    private readonly Action<CaptureState> _stateChanged;
    private readonly Lock _lock = new();
    private readonly Timer _reopen;
    private readonly byte[] _pcmMessage = new byte[1 + 8 + MaxPcmBytes];
    private NamedPipeClientStream? _pipe;
    // The 0x10 message of the pair the mic is for, sent on every (re)connect.
    private byte[]? _device;
    private CaptureState? _state;
    private int _failures;

    public VirtualMic(Action<CaptureState> stateChanged)
    {
        _stateChanged = stateChanged;
        _reopen = new Timer(static s => ((VirtualMic)s!).Connect(), this, Timeout.Infinite, Timeout.Infinite);
        _pcmMessage[0] = ServicePipes.MicPcm;
    }

    /// <summary>Opens the pipe on the thread pool; returns at once.</summary>
    public void Open() => ThreadPool.QueueUserWorkItem(static s => ((VirtualMic)s!).Connect(), this);

    /// <summary>
    /// Sets the pair shown as "Headset (A2DP, name)". <paramref name="reported"/>: the name came from the AirPods; a
    /// Windows name is only used for a pair the service has no name for yet. Resent on every reopen.
    /// </summary>
    public void SetDevice(ulong address, string name, bool reported)
    {
        byte[] message = new byte[10 + Encoding.UTF8.GetByteCount(name)];
        message[0] = ServicePipes.MicDevice;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(1), address);
        message[9] = reported ? (byte)1 : (byte)0;
        Encoding.UTF8.GetBytes(name, message.AsSpan(10));
        lock (_lock)
        {
            if (_device is not null && message.AsSpan().SequenceEqual(_device))
            {
                return;
            }
            Log.Info("vmic", $"device {Mac.Format(address)} \"{name}\"");
            _device = message;
            if (_pipe is { } pipe)
            {
                SendLocked(pipe, message, "device");
            }
        }
    }

    /// <summary>
    /// Writes mono 48 kHz s16 PCM (at most 32768 samples) as one message, its first sample at
    /// <paramref name="position"/> on the AirPods' clock (48 kHz samples). Dropped while the pipe is closed.
    /// </summary>
    public void Write(ReadOnlySpan<short> pcm, long position)
    {
        lock (_lock)
        {
            if (_pipe is not { } pipe)
            {
                return;
            }
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(pcm);
            BinaryPrimitives.WriteInt64LittleEndian(_pcmMessage.AsSpan(1), position);
            bytes.CopyTo(_pcmMessage.AsSpan(9));
            SendLocked(pipe, _pcmMessage.AsSpan(0, 9 + bytes.Length), "PCM");
        }
    }

    // Thread pool or reopen timer, one at a time: a reopen is scheduled only while the pipe is closed.
    private void Connect()
    {
        NamedPipeClientStream? pipe = ServicePipes.Connect(ServicePipes.MicPipe, out int error);
        lock (_lock)
        {
            if (pipe is null)
            {
                Log.Warn("vmic", $"open failed, Win32 error {error}");
                ScheduleReopenLocked();
                SetStateLocked(CaptureState.Unavailable);
                return;
            }
            Log.Info("vmic", "opened");
            _pipe = pipe;
            if (_device is { } device)
            {
                SendLocked(pipe, device, "device");
            }
            if (_pipe != pipe)
            {
                // The device write failed and closed it.
                return;
            }
        }
        _ = ReadLoopAsync(pipe);
    }

    // Capture-state messages until the pipe fails or the service closes it; then closes it unless that already
    // happened.
    private async Task ReadLoopAsync(NamedPipeClientStream pipe)
    {
        byte[] buffer = new byte[16];
        string reason;
        try
        {
            while (true)
            {
                int count = await pipe.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0)
                {
                    reason = "pipe closed by the service";
                    break;
                }
                if (buffer[0] != ServicePipes.MicCapture || count < 2)
                {
                    Log.Warn("vmic", $"unknown message kind 0x{buffer[0]:X2} ({count} bytes), ignored");
                    continue;
                }
                lock (_lock)
                {
                    if (_pipe != pipe)
                    {
                        return;
                    }
                    _failures = 0;
                    SetStateLocked(buffer[1] != 0 ? CaptureState.Capturing : CaptureState.Idle);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            reason = $"read failed: 0x{e.HResult:X8} {e.Message}";
        }
        lock (_lock)
        {
            if (_pipe == pipe)
            {
                Log.Warn("vmic", $"capture state {reason}; closing");
                CloseLocked();
            }
        }
    }

    private void SendLocked(NamedPipeClientStream pipe, ReadOnlySpan<byte> message, string what)
    {
        try
        {
            pipe.Write(message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Warn("vmic", $"{what} write failed: 0x{e.HResult:X8} {e.Message}; closing");
            CloseLocked();
        }
    }

    // A pending capture-state read completes with an error; its loop then sees the pipe is no longer current.
    private void CloseLocked()
    {
        _pipe!.Dispose();
        _pipe = null;
        ScheduleReopenLocked();
        SetStateLocked(CaptureState.Unavailable);
    }

    private void SetStateLocked(CaptureState state)
    {
        if (_state == state)
        {
            return;
        }
        Log.Info("vmic", $"capture state {_state?.ToString() ?? "unknown"} -> {state}");
        _state = state;
        _stateChanged(state);
    }

    private void ScheduleReopenLocked()
    {
        Log.Info("vmic", $"reopen in {1000 << _failures} ms");
        _reopen.Change(1000 << _failures, Timeout.Infinite);
        _failures = Math.Min(_failures + 1, MaxBackoffShift);
    }
}
