using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Timer = System.Threading.Timer;

namespace WPods.Audio;

/// <summary>
/// \\.\NTA2DPVM: PCM writes plus one pending capture-state IOCTL. Any failure closes the device and reopens it
/// with a capped backoff, independent of the AAP session. The capture callback runs on a thread-pool thread,
/// one call at a time.
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

    private readonly Action<bool> _captureChanged;
    private readonly Lock _lock = new();
    private readonly Timer _reopen;
    private readonly ManualResetEvent _writeDone = new(false);
    private readonly NativeOverlapped* _writeOverlapped;
    private Device? _device;
    private uint _state;
    private int _failures;

    public VirtualMic(Action<bool> captureChanged)
    {
        _captureChanged = captureChanged;
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
            handle.Dispose();
            lock (_lock)
            {
                ScheduleReopenLocked();
            }
            return;
        }
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
            _device = null;
            CancelIoEx(device.Handle, device.Overlapped);
            ScheduleReopenLocked();
        }
    }

    // Issued under the lock so a concurrent write failure either cancels it or is seen here.
    private void WaitForCapture(Device device)
    {
        lock (_lock)
        {
            if (_device == device)
            {
                *device.Input = _state;
                if (DeviceIoControl(device.Handle, IoctlWaitCaptureState, device.Input, sizeof(uint), device.Output, sizeof(uint), null, device.Overlapped)
                    || Marshal.GetLastPInvokeError() == ErrorIoPending)
                {
                    return;
                }
                _device = null;
                ScheduleReopenLocked();
            }
            // Nothing pending: no completion will signal the event.
            Close(device);
        }
    }

    // Wait-thread callback; runs once per completed IOCTL (synchronous completions signal the event too).
    private void OnWaitCompleted(Device device)
    {
        bool ok = GetOverlappedResult(device.Handle, device.Overlapped, out _, false);
        uint state;
        bool changed;
        lock (_lock)
        {
            if (!ok || _device != device)
            {
                if (_device == device)
                {
                    _device = null;
                    ScheduleReopenLocked();
                }
                Close(device);
                return;
            }
            _failures = 0;
            state = *device.Output;
            changed = state != _state;
            _state = state;
        }
        if (changed)
        {
            _captureChanged(state != 0);
        }
        WaitForCapture(device);
    }

    private void ScheduleReopenLocked()
    {
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
