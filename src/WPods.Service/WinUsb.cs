using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WPods.Service;

/// <summary>One USB/IP request from Windows (or one of the service's own ACL packets when <see cref="Internal"/>).</summary>
internal sealed class Urb
{
    public uint Seq;
    /// <summary>Endpoint address: 0 = control, bit 7 = IN.</summary>
    public byte Pipe;
    public bool In;
    public uint Length;
    /// <summary>OUT data; for control transfers the 8-byte setup packet first.</summary>
    public byte[] Data = [];
    public volatile bool Cancelled;
    public volatile bool Running;
    /// <summary>Its RET_SUBMIT was sent (non-isochronous only); guarded by a lock on the URB.</summary>
    public bool Completed;
    /// <summary>Isochronous only: the 16-byte USB/IP packet descriptors and their count.</summary>
    public byte[]? IsoDescriptors;
    public int Packets;
    public uint StartFrame;
    public nint Overlapped;
    /// <summary>Isochronous OUT already completed towards Windows before the device finished it.</summary>
    public bool Early;
    /// <summary>The service's own ACL OUT packets (one L2CAP PDU, already fragmented); no RET goes to Windows.</summary>
    public bool Internal;
    public byte[][]? Fragments;
}

/// <summary>Result of one isochronous transfer: IN data as the controller placed it, per-packet offset, length, USBD status.</summary>
internal readonly record struct IsoResult(byte[] Data, int[] Offsets, int[] Actual, int[] Status);

/// <summary>A USB device bound to Windows' in-box WinUSB driver; transfers return Win32 errors instead of throwing.</summary>
internal sealed unsafe partial class WinUsbDevice : IDisposable
{
    public const int ErrorAborted = 995;
    private readonly SafeFileHandle _file;
    private readonly List<nint> _interfaces = [];
    // Pipe id (endpoint address) -> handle of the interface whose current alternate setting has it. Replaced as a whole.
    private volatile Dictionary<byte, nint> _pipes = [];

    private WinUsbDevice(SafeFileHandle file)
    {
        _file = file;
    }

    public SafeFileHandle File => _file;
    public byte[] DeviceDescriptor { get; } = new byte[18];
    public byte[] ConfigDescriptor { get; private set; } = [];
    public IReadOnlyList<nint> Interfaces => _interfaces;
    public nint FirstInterface => _interfaces[0];

    /// <summary>
    /// Opened on one function of a combined device: descriptors show only its interfaces, renumbered from 0 in WinUSB
    /// order, as a plain Bluetooth device (class E0/01/01).
    /// </summary>
    public bool IsFunction { get; private set; }

    // Function only: real interface number of each virtual one (index = virtual number = WinUSB interface order).
    private byte[] _realInterfaces = [];

    /// <summary>The device's interface number for a virtual one (the same for a whole device).</summary>
    public int RealInterface(int number) => IsFunction && number < _realInterfaces.Length ? _realInterfaces[number] : number;

    /// <summary>
    /// String index of the serial given to an adapter without one. Not 0xEE: Windows reads that index on first
    /// enumeration for a Microsoft OS descriptor, and anything else there fails the enumeration.
    /// </summary>
    public const byte SerialIndex = 0xE0;

    /// <summary>
    /// Serial shown when the real adapter has none (else null). Windows then names the adapter by it, not by its
    /// usbip-win2 port, so its devices, audio endpoints and their settings keep their IDs on any port.
    /// </summary>
    public string? Serial { get; private set; }

    public static WinUsbDevice Open(string path, bool function, string serial)
    {
        SafeFileHandle file = CreateFile(path, 0xC0000000, 3, 0, 3, 0x40000000 /* FILE_FLAG_OVERLAPPED */, 0);
        if (file.IsInvalid)
        {
            throw new Win32Exception();
        }
        var dev = new WinUsbDevice(file);
        try
        {
            if (!WinUsb_Initialize(file, out nint first))
            {
                throw new Win32Exception();
            }
            dev._interfaces.Add(first);
            for (byte i = 0; WinUsb_GetAssociatedInterface(first, i, out nint assoc); i++)
            {
                dev._interfaces.Add(assoc);
            }
            fixed (byte* d = dev.DeviceDescriptor)
            {
                if (!WinUsb_GetDescriptor(first, 1, 0, 0, d, 18, out _))
                {
                    throw new Win32Exception();
                }
            }
            byte* head = stackalloc byte[9];
            if (!WinUsb_GetDescriptor(first, 2, 0, 0, head, 9, out _))
            {
                throw new Win32Exception();
            }
            var config = new byte[BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(head + 2, 2))];
            fixed (byte* c = config)
            {
                if (!WinUsb_GetDescriptor(first, 2, 0, 0, c, (uint)config.Length, out _))
                {
                    throw new Win32Exception();
                }
            }
            dev.ConfigDescriptor = config;
            Log.Info("usb", $"opened {path}: device descriptor {Convert.ToHexString(dev.DeviceDescriptor)}, configuration {Convert.ToHexString(config)}, {dev._interfaces.Count} interfaces");
            // Never suspend the adapter on idle: Bluetooth links stay up while USB is quiet (Linux btusb refuses
            // autosuspend with connections), and some controllers lose their firmware when suspended.
            byte off = 0;
            if (!WinUsb_SetPowerPolicy(first, 0x81 /* AUTO_SUSPEND */, 1, &off))
            {
                Log.Warn("usb", $"auto-suspend not turned off: Win32 error {Marshal.GetLastPInvokeError()}");
            }
            if (function)
            {
                dev.ShowFunctionOnly();
            }
            // USB 2.0 at most: a higher bcdUSB (some Intel adapters report 0x0250) makes Windows' hub driver demand a
            // BOS descriptor and apply stricter rules to the virtual device, which usbip-win2 shows as High-Speed.
            if (BinaryPrimitives.ReadUInt16LittleEndian(dev.DeviceDescriptor.AsSpan(2)) > 0x0200)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(dev.DeviceDescriptor.AsSpan(2), 0x0200);
            }
            if (dev.DeviceDescriptor[16] == 0)
            {
                dev.DeviceDescriptor[16] = SerialIndex;
                dev.Serial = serial;
            }
            dev.MapPipes();
            return dev;
        }
        catch
        {
            dev.Dispose();
            throw;
        }
    }

    // Rebuilds the device and configuration descriptors for this function alone.
    private void ShowFunctionOnly()
    {
        var numbers = new List<byte>();
        byte* desc = stackalloc byte[9];
        foreach (nint h in _interfaces)
        {
            if (WinUsb_QueryInterfaceSettings(h, 0, desc))
            {
                numbers.Add(desc[2]);
            }
        }
        _realInterfaces = [.. numbers];
        byte[] c = ConfigDescriptor;
        var o = new List<byte>(c.AsSpan(0, Math.Min(9, c.Length)).ToArray());
        int current = -1;
        for (int at = c[0]; at + 2 <= c.Length && c[at] > 0; at += c[at])
        {
            byte type = c[at + 1];
            if (type == 0x0B)
            {
                continue; // interface association: the virtual device is a single function
            }
            if (type == 0x04)
            {
                current = Array.IndexOf(_realInterfaces, c[at + 2]);
            }
            if (current < 0)
            {
                continue;
            }
            byte[] d = c.AsSpan(at, Math.Min(c[at], c.Length - at)).ToArray();
            if (type == 0x04)
            {
                d[2] = (byte)current;
            }
            o.AddRange(d);
        }
        byte[] config = [.. o];
        BinaryPrimitives.WriteUInt16LittleEndian(config.AsSpan(2), (ushort)config.Length);
        config[4] = (byte)_realInterfaces.Length;
        config[7] &= unchecked((byte)~0x20); // no remote wakeup over USB/IP
        ConfigDescriptor = config;
        DeviceDescriptor[4] = 0xE0; // wireless controller / RF / Bluetooth programming interface
        DeviceDescriptor[5] = 0x01;
        DeviceDescriptor[6] = 0x01;
        DeviceDescriptor[17] = 1;
        IsFunction = true;
        Log.Info("usb", $"function of a combined device: interfaces {string.Join(",", _realInterfaces)} shown as 0..{_realInterfaces.Length - 1}");
    }

    /// <summary>Win32 errors that mean the device is gone (unplugged, re-enumerated, or taken by another driver).</summary>
    public static bool IsDeviceLost(int error) => error is 2 or 6 or 22 or 433 or 1167;

    public bool TryGetPipeInterface(byte pipe, out nint handle) => _pipes.TryGetValue(pipe, out handle);

    /// <summary>Maps the pipes of every interface's current alternate setting to that interface.</summary>
    public void MapPipes()
    {
        var map = new Dictionary<byte, nint>();
        byte* desc = stackalloc byte[9];
        byte* info = stackalloc byte[12];
        foreach (nint h in _interfaces)
        {
            if (!WinUsb_GetCurrentAlternateSetting(h, out byte alt) || !WinUsb_QueryInterfaceSettings(h, alt, desc))
            {
                continue;
            }
            for (byte p = 0; p < desc[4]; p++)
            {
                if (WinUsb_QueryPipe(h, alt, p, info))
                {
                    map[info[4]] = h;
                }
            }
        }
        _pipes = map;
    }

    /// <summary>Maximum bytes per interval of <paramref name="pipe"/> in the interface's current alternate setting, 0 if none.</summary>
    public static int MaxBytesPerInterval(nint iface, byte pipe)
    {
        byte* desc = stackalloc byte[9];
        byte* info = stackalloc byte[12];
        if (!WinUsb_GetCurrentAlternateSetting(iface, out byte alt) || !WinUsb_QueryInterfaceSettings(iface, alt, desc))
        {
            return 0;
        }
        for (byte p = 0; p < desc[4]; p++)
        {
            if (WinUsb_QueryPipe(iface, alt, p, info) && info[4] == pipe)
            {
                // WINUSB_PIPE_INFORMATION: PipeType u32, PipeId u8, MaximumPacketSize u16 at offset 6, Interval u8.
                return BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(info + 6, 2));
            }
        }
        return 0;
    }

    public int Control(ulong setup, byte[] buffer, out uint got)
    {
        fixed (byte* b = buffer)
        {
            return WinUsb_ControlTransfer(FirstInterface, setup, b, (uint)buffer.Length, out got, 0) ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    public int Read(nint iface, byte pipe, byte[] buffer, out uint got)
    {
        fixed (byte* b = buffer)
        {
            return WinUsb_ReadPipe(iface, pipe, b, (uint)buffer.Length, out got, 0) ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    public int Write(nint iface, byte pipe, byte[] data, out uint got)
    {
        fixed (byte* b = data)
        {
            return WinUsb_WritePipe(iface, pipe, b, (uint)data.Length, out got, 0) ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    /// <summary>USB/IP speed of the device (1 low, 2 full, 3 high), as WinUSB reports it; full speed if unknown.</summary>
    public uint Speed
    {
        get
        {
            byte speed = 0;
            uint length = 1;
            return WinUsb_QueryDeviceInformation(FirstInterface, 1 /* DEVICE_SPEED */, ref length, &speed) && speed is >= 1 and <= 3 ? speed : 2u;
        }
    }

    public static void Abort(nint iface, byte pipe) => WinUsb_AbortPipe(iface, pipe);

    public static void Reset(nint iface, byte pipe) => WinUsb_ResetPipe(iface, pipe);

    public int SetAlternate(int interfaceIndex, byte alt)
    {
        if (interfaceIndex >= _interfaces.Count)
        {
            return 87;
        }
        if (!WinUsb_SetCurrentAlternateSetting(_interfaces[interfaceIndex], alt))
        {
            return Marshal.GetLastPInvokeError();
        }
        MapPipes();
        return 0;
    }

    public void Dispose()
    {
        for (int i = _interfaces.Count - 1; i >= 0; i--)
        {
            WinUsb_Free(_interfaces[i]);
        }
        _interfaces.Clear();
        _file.Dispose();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_Initialize(SafeFileHandle file, out nint iface);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_Free(nint iface);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_GetAssociatedInterface(nint iface, byte index, out nint assoc);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_GetDescriptor(nint iface, byte type, byte index, ushort lang, byte* buffer, uint length, out uint got);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_GetCurrentAlternateSetting(nint iface, out byte alt);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_SetCurrentAlternateSetting(nint iface, byte alt);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_QueryInterfaceSettings(nint iface, byte alt, byte* descriptor9);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_QueryPipe(nint iface, byte alt, byte index, byte* pipeInfo12);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_ControlTransfer(nint iface, ulong setup, byte* buffer, uint length, out uint got, nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_ReadPipe(nint iface, byte pipe, byte* buffer, uint length, out uint got, nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_WritePipe(nint iface, byte pipe, byte* buffer, uint length, out uint got, nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_SetPowerPolicy(nint iface, uint type, uint length, byte* value);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_QueryDeviceInformation(nint iface, uint type, ref uint length, byte* value);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_AbortPipe(nint iface, byte pipe);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_ResetPipe(nint iface, byte pipe);
}

/// <summary>
/// One isochronous pipe (Bluetooth SCO for Hands-Free). URBs go to WinUSB at once, so several are in flight as on a real
/// host controller; one thread completes them in order. Must be closed before the alternate setting changes.
/// </summary>
internal sealed unsafe partial class IsoPipe
{
    private const int Slots = 64;
    private const int SlotSize = 4096;
    private const int MaxPackets = 256;
    // Windows sends the next SCO OUT packet only after a completion, and the USB/IP round trip exceeds the 1 ms frame:
    // OUT URBs complete early while at most OutDepth frames are queued in WinUSB, bounding added latency to OutDepth ms.
    private const int OutDepth = 8;

    private readonly WinUsbDevice _device;
    private readonly byte _pipe;
    private readonly int _bytesPerInterval;
    private readonly nint _buffer;
    private readonly nint _descriptors;
    private readonly nint _registration;
    private readonly nint[] _overlapped = new nint[Slots];
    private readonly BlockingCollection<(Urb Urb, int Slot)> _pending = new(Slots - 1);
    private readonly Queue<Urb> _held = new();
    private readonly Action<Urb, int, IsoResult?> _done;
    private readonly Thread _completer;
    private int _next;
    private long _urbs;
    private long _failed;
    private volatile bool _streaming;

    public IsoPipe(WinUsbDevice device, nint iface, byte pipe, Action<Urb, int, IsoResult?> done)
    {
        _device = device;
        Interface = iface;
        _pipe = pipe;
        _done = done;
        _bytesPerInterval = WinUsbDevice.MaxBytesPerInterval(iface, pipe);
        if (_bytesPerInterval == 0)
        {
            throw new InvalidOperationException($"pipe 0x{pipe:X2} has no bandwidth in the current alternate setting");
        }
        _buffer = (nint)NativeMemory.Alloc(Slots * SlotSize);
        _descriptors = (nint)NativeMemory.Alloc(Slots * MaxPackets * 12);
        for (int i = 0; i < Slots; i++)
        {
            // OVERLAPPED (32 bytes) with a manual-reset event at offset 24.
            _overlapped[i] = (nint)NativeMemory.AllocZeroed(32);
            *(nint*)(_overlapped[i] + 24) = CreateEvent(0, true, false, 0);
        }
        if (!WinUsb_RegisterIsochBuffer(iface, pipe, _buffer, Slots * SlotSize, out _registration))
        {
            int error = Marshal.GetLastPInvokeError();
            Free();
            throw new Win32Exception(error);
        }
        Log.Info("usb", $"iso pipe 0x{pipe:X2} open, {_bytesPerInterval} bytes per interval");
        _completer = new Thread(Complete) { IsBackground = true, Name = $"iso {pipe:X2}", Priority = ThreadPriority.Highest };
        _completer.Start();
    }

    public nint Interface { get; }

    /// <summary>Called only from the URB loop thread.</summary>
    public void Submit(Urb u)
    {
        int slot = _next;
        _next = (_next + 1) % Slots;
        int length = u.In ? u.Packets * _bytesPerInterval : u.Data.Length;
        if (length > SlotSize || u.Packets > MaxPackets)
        {
            Log.Warn("usb", $"iso 0x{_pipe:X2} URB too large ({length} bytes, {u.Packets} packets)");
            _done(u, UsbIp.EOVERFLOW, null);
            return;
        }
        nint ov = _overlapped[slot];
        ResetEvent(*(nint*)(ov + 24));
        *(long*)ov = 0;
        *(long*)(ov + 8) = 0;
        u.Overlapped = ov;
        uint offset = (uint)(slot * SlotSize);
        if (!u.In)
        {
            Marshal.Copy(u.Data, 0, _buffer + (int)offset, length);
        }
        int error = Start(u, slot, offset, length, _streaming);
        if (error != 0 && _streaming)
        {
            // The stream ran dry (a gap): restart it at the next possible frame.
            error = Start(u, slot, offset, length, false);
        }
        if (error != 0)
        {
            Log.Warn("usb", $"iso 0x{_pipe:X2} submit failed, Win32 error {error}");
            _streaming = false;
            _done(u, UsbIp.EPROTO, null);
            return;
        }
        _streaming = true;
        u.Running = true;
        _pending.Add((u, slot));
        if (!u.In)
        {
            lock (_held)
            {
                _held.Enqueue(u);
                DrainLocked();
            }
        }
    }

    public void Cancel(Urb u)
    {
        if (u.Running && u.Overlapped != 0)
        {
            CancelIoEx(_device.File, u.Overlapped);
        }
    }

    /// <summary>Aborts what is in flight, waits for the completions, then releases the buffer registration.</summary>
    public void Close()
    {
        WinUsbDevice.Abort(Interface, _pipe);
        _pending.CompleteAdding();
        if (_completer.Join(2000))
        {
            WinUsb_UnregisterIsochBuffer(_registration);
            Free();
        }
        // Else the memory stays allocated: WinUSB may still write into it.
        Log.Info("usb", $"iso pipe 0x{_pipe:X2} closed: {_urbs} URBs ok, {_failed} failed");
    }

    private int Start(Urb u, int slot, uint offset, int length, bool continueStream)
    {
        bool ok = u.In
            ? WinUsb_ReadIsochPipeAsap(_registration, offset, (uint)length, continueStream, (uint)u.Packets, _descriptors + (slot * MaxPackets * 12), u.Overlapped)
            : WinUsb_WriteIsochPipeAsap(_registration, offset, (uint)length, continueStream, u.Overlapped);
        int error = ok ? 0 : Marshal.GetLastPInvokeError();
        return error == 997 /* ERROR_IO_PENDING */ ? 0 : error;
    }

    // Caller holds _held.
    private void DrainLocked()
    {
        while (_held.Count > 0 && _pending.Count <= OutDepth)
        {
            Urb h = _held.Dequeue();
            h.Early = true;
            _done(h, 0, OutResult(h));
        }
    }

    /// <summary>Result of an isochronous OUT transfer with every packet counted as sent.</summary>
    public static IsoResult OutResult(Urb u)
    {
        var actual = new int[u.Packets];
        for (int i = 0; i < u.Packets; i++)
        {
            actual[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(u.IsoDescriptors.AsSpan((16 * i) + 4));
        }
        return new IsoResult([], new int[u.Packets], actual, new int[u.Packets]);
    }

    private void Complete()
    {
        foreach ((Urb u, int slot) in _pending.GetConsumingEnumerable())
        {
            bool ok = WinUsb_GetOverlappedResult(Interface, u.Overlapped, out _, true);
            int error = ok ? 0 : Marshal.GetLastPInvokeError();
            u.Running = false;
            if (!u.In)
            {
                lock (_held)
                {
                    DrainLocked();
                }
            }
            if (!ok)
            {
                if (error != WinUsbDevice.ErrorAborted)
                {
                    _failed++;
                }
                _streaming = false;
                if (!u.Early)
                {
                    _done(u, error == WinUsbDevice.ErrorAborted ? UsbIp.ECONNRESET : UsbIp.EPROTO, null);
                }
                continue;
            }
            _urbs++;
            if (u.Early)
            {
                continue;
            }
            if (!u.In)
            {
                _done(u, 0, OutResult(u));
                continue;
            }
            int n = u.Packets;
            var data = new byte[n * _bytesPerInterval];
            Marshal.Copy(_buffer + (slot * SlotSize), data, 0, data.Length);
            var offsets = new int[n];
            var actual = new int[n];
            var status = new int[n];
            // USBD_ISO_PACKET_DESCRIPTOR: Offset u32, Length u32, Status i32.
            int* d = (int*)(_descriptors + (slot * MaxPackets * 12));
            for (int i = 0; i < n; i++)
            {
                offsets[i] = d[3 * i];
                actual[i] = d[(3 * i) + 1];
                status[i] = d[(3 * i) + 2];
            }
            _done(u, 0, new IsoResult(data, offsets, actual, status));
        }
    }

    private void Free()
    {
        for (int i = 0; i < Slots; i++)
        {
            if (_overlapped[i] != 0)
            {
                CloseHandle(*(nint*)(_overlapped[i] + 24));
                NativeMemory.Free((void*)_overlapped[i]);
                _overlapped[i] = 0;
            }
        }
        NativeMemory.Free((void*)_buffer);
        NativeMemory.Free((void*)_descriptors);
    }

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_RegisterIsochBuffer(nint iface, byte pipe, nint buffer, int length, out nint registration);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_UnregisterIsochBuffer(nint registration);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_ReadIsochPipeAsap(nint registration, uint offset, uint length, [MarshalAs(UnmanagedType.Bool)] bool continueStream, uint packets, nint descriptors, nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_WriteIsochPipeAsap(nint registration, uint offset, uint length, [MarshalAs(UnmanagedType.Bool)] bool continueStream, nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_GetOverlappedResult(nint iface, nint overlapped, out uint got, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
    private static partial nint CreateEvent(nint security, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ResetEvent(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(SafeFileHandle file, nint overlapped);
}
