using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WPods.Core;
using Timer = System.Threading.Timer;

namespace WPods.Audio;

internal enum CaptureState
{
    Idle,
    Capturing,
    /// <summary>The device cannot be opened or its capture state cannot be read.</summary>
    Unavailable,
}

/// <summary>
/// \\.\NTA2DPVM: PCM writes plus one pending capture-state IOCTL. Any failure closes the device and reopens it
/// with a capped backoff, independent of the AAP session. The state callback runs only on change, under the
/// internal lock (callers must not call back into this class from it).
/// </summary>
internal sealed unsafe partial class VirtualMic
{
    private const string DevicePath = @"\\.\NTA2DPVM";
    private const uint IoctlWaitCaptureState = 0x00226404;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int ErrorIoPending = 997;
    private const int MaxBackoffShift = 6; // 1 s -> 64 s

    private readonly Action<CaptureState> _stateChanged;
    private readonly Lock _lock = new();
    private readonly Timer _reopen;
    private readonly ManualResetEvent _writeDone = new(false);
    private readonly NativeOverlapped* _writeOverlapped;
    private Device? _device;
    private CaptureState? _state;
    private int _failures;

    public VirtualMic(Action<CaptureState> stateChanged)
    {
        _stateChanged = stateChanged;
        _reopen = new Timer(static s => ((VirtualMic)s!).Open(), this, Timeout.Infinite, Timeout.Infinite);
        _writeOverlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        _writeOverlapped->EventHandle = _writeDone.SafeWaitHandle.DangerousGetHandle();
    }

    public bool IsOpen => Volatile.Read(ref _device) is not null;

    public void Open()
    {
        SafeFileHandle handle = CreateFile(DevicePath, GenericRead | GenericWrite, FileShareReadWrite, 0, OpenExisting, FileFlagOverlapped, 0);
        if (handle.IsInvalid)
        {
            Log.Warn("vmic", $"open failed, Win32 error {Marshal.GetLastPInvokeError()}");
            handle.Dispose();
            lock (_lock)
            {
                ScheduleReopenLocked();
                SetStateLocked(CaptureState.Unavailable);
            }
            return;
        }
        Log.Info("vmic", "opened");
        var device = new Device(handle);
        device.Wait = ThreadPool.RegisterWaitForSingleObject(device.Completed, (s, _) => OnWaitCompleted((Device)s!), device, Timeout.Infinite, executeOnlyOnce: false);
        lock (_lock)
        {
            _device = device;
        }
        WaitForCapture(device);
    }

    /// <summary>Writes mono 48 kHz s16 PCM (at most 32768 samples). Never blocks; dropped while the device is closed.</summary>
    public void Write(ReadOnlySpan<short> pcm)
    {
        lock (_lock)
        {
            if (_device is not { } device)
            {
                return;
            }
            fixed (short* data = pcm)
            {
                if (WriteFile(device.Handle, data, (uint)(pcm.Length * sizeof(short)), null, _writeOverlapped)
                    || (Marshal.GetLastPInvokeError() == ErrorIoPending && GetOverlappedResult(device.Handle, _writeOverlapped, out _, true)))
                {
                    return;
                }
            }
            // The pending capture IOCTL completes with 995; its callback closes the device.
            Log.Warn("vmic", $"write failed, Win32 error {Marshal.GetLastPInvokeError()}; closing");
            _device = null;
            CancelIoEx(device.Handle, device.Overlapped);
            ScheduleReopenLocked();
            SetStateLocked(CaptureState.Unavailable);
        }
    }

    // Issued under the lock so a concurrent write failure either cancels it or is seen here.
    private void WaitForCapture(Device device)
    {
        lock (_lock)
        {
            if (_device == device)
            {
                bool capturing = _state == CaptureState.Capturing;
                *device.Input = capturing ? 1u : 0u;
                if (DeviceIoControl(device.Handle, IoctlWaitCaptureState, device.Input, sizeof(uint), device.Output, sizeof(uint), null, device.Overlapped))
                {
                    return;
                }
                int error = Marshal.GetLastPInvokeError();
                if (error == ErrorIoPending)
                {
                    // Pending means the state equals the input (this makes an unknown state known after an open).
                    SetStateLocked(capturing ? CaptureState.Capturing : CaptureState.Idle);
                    return;
                }
                Log.Warn("vmic", $"capture-state IOCTL failed, Win32 error {error}; closing");
                _device = null;
                ScheduleReopenLocked();
                SetStateLocked(CaptureState.Unavailable);
            }
            // Nothing pending: no completion will signal the event.
            Close(device);
        }
    }

    // Wait-thread callback; runs once per completed IOCTL (synchronous completions signal the event too).
    private void OnWaitCompleted(Device device)
    {
        bool ok = GetOverlappedResult(device.Handle, device.Overlapped, out _, false);
        lock (_lock)
        {
            if (!ok || _device != device)
            {
                if (_device == device)
                {
                    Log.Warn("vmic", "capture-state IOCTL completed with an error; closing");
                    _device = null;
                    ScheduleReopenLocked();
                    SetStateLocked(CaptureState.Unavailable);
                }
                Close(device);
                return;
            }
            _failures = 0;
            SetStateLocked(*device.Output != 0 ? CaptureState.Capturing : CaptureState.Idle);
        }
        WaitForCapture(device);
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

    // Called with no I/O pending on the device.
    private static void Close(Device device)
    {
        device.Wait!.Unregister(null);
        device.Completed.Dispose();
        device.Handle.Dispose();
        NativeMemory.Free(device.Overlapped);
    }

    // One open handle with its IOCTL OVERLAPPED (+ in/out ULONGs in the same block) and completion event.
    private sealed class Device
    {
        public Device(SafeFileHandle handle)
        {
            Handle = handle;
            Overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)(sizeof(NativeOverlapped) + 2 * sizeof(uint)));
            Overlapped->EventHandle = Completed.SafeWaitHandle.DangerousGetHandle();
        }

        public SafeFileHandle Handle { get; }
        public AutoResetEvent Completed { get; } = new(false);
        public NativeOverlapped* Overlapped { get; }
        public uint* Input => (uint*)(Overlapped + 1);
        public uint* Output => Input + 1;
        public RegisteredWaitHandle? Wait { get; set; }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(SafeFileHandle file, void* buffer, uint length, uint* written, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputLength, void* output, uint outputLength, uint* returned, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOverlappedResult(SafeFileHandle file, NativeOverlapped* overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);
}
