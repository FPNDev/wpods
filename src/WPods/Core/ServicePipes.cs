using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WPods.Core;

/// <summary>Client side of the WPods.Service pipes: local, message mode, integers little-endian.</summary>
internal static partial class ServicePipes
{
    public const string AapPipe = "WPods.Aap";
    public const string MicPipe = "WPods.Mic";
    public const string ServicePipe = "WPods.Service";

    // Message kinds (first byte).
    public const byte AapPacket = 0x00;
    public const byte AapEnded = 0x01;
    private const byte AapOpenReply = 0x02;
    public const byte AapCallAudio = 0x03;
    public const byte MicDevice = 0x10;
    public const byte MicPcm = 0x11;
    public const byte MicCapture = 0x12;
    public const byte ServiceStateMessage = 0x20;
    public const byte ServiceRetryMessage = 0x21;
    public const byte UpdateInfoMessage = 0x22;
    public const byte SetAutoUpdateMessage = 0x23;
    public const byte CheckUpdateMessage = 0x24;
    public const byte UpdateNowMessage = 0x25;
    public const byte AppStartedMessage = 0x26;

    private const int ErrorInvalidData = 13;
    private const int ErrorBrokenPipe = 109;
    private const int ErrorSemTimeout = 121;
    private const int ErrorPipeBusy = 231;
    // Read/write data and attributes plus SYNCHRONIZE: exactly what the pipe ACL grants interactive users.
    // GENERIC_WRITE would also request FILE_APPEND_DATA (create pipe instances) and be denied.
    private const uint PipeAccess = 0x00100183;
    private const uint OpenExisting = 3;
    // The pipe server may only identify this user, never impersonate them, in case another process owns the name.
    private const uint FileFlagOverlapped = 0x40000000 | 0x00100000 /* SECURITY_SQOS_PRESENT */ | 0x00010000 /* SECURITY_IDENTIFICATION */;
    private const int ErrorAccessDenied = 5;
    private const uint BusyWaitMs = 2000;
    // The service replies within 10 s; this only guards against a stuck service.
    private static readonly TimeSpan AapReplyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Opens the pipe in message mode; null with the Win32 error (2 = no such pipe, 231 = busy after 2 s).</summary>
    public static NamedPipeClientStream? Connect(string name, out int error)
    {
        string path = @"\\.\pipe\" + name;
        SafePipeHandle handle = CreateFile(path, PipeAccess, 0, 0, OpenExisting, FileFlagOverlapped, 0);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : Native.ErrorSuccess;
        if (error == ErrorPipeBusy && WaitNamedPipe(path, BusyWaitMs))
        {
            handle.Dispose();
            handle = CreateFile(path, PipeAccess, 0, 0, OpenExisting, FileFlagOverlapped, 0);
            error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : Native.ErrorSuccess;
        }
        if (error == Native.ErrorSuccess && !FromService(handle))
        {
            Log.Warn("pipe", $"{name}: the pipe is not the WPods service's");
            error = ErrorAccessDenied;
        }
        if (error != Native.ErrorSuccess)
        {
            handle.Dispose();
            return null;
        }
        var pipe = new NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, handle);
        try
        {
            pipe.ReadMode = PipeTransmissionMode.Message;
            return pipe;
        }
        catch (IOException e)
        {
            pipe.Dispose();
            error = e.HResult & 0xFFFF;
            return null;
        }
    }

    /// <summary>
    /// Opens an AAP channel to <paramref name="address"/> without paging. Null with the error: 1167 no ACL link,
    /// 170 open by another client, 1225 refused by the AirPods, 121 no answer, else a pipe error.
    /// </summary>
    public static NamedPipeClientStream? OpenAap(ulong address, out int error)
    {
        if (Connect(AapPipe, out error) is not { } pipe)
        {
            return null;
        }
        try
        {
            Span<byte> hello = stackalloc byte[10];
            hello[0] = (byte)'A';
            hello[1] = (byte)'P';
            BinaryPrimitives.WriteUInt64LittleEndian(hello[2..], address);
            pipe.Write(hello);
            byte[] reply = new byte[16];
            using var timeout = new CancellationTokenSource(AapReplyTimeout);
            int count = pipe.ReadAsync(reply, timeout.Token).AsTask().GetAwaiter().GetResult();
            error = count == 0 ? ErrorBrokenPipe
                : count == 5 && reply[0] == AapOpenReply ? BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(1))
                : ErrorInvalidData;
        }
        catch (OperationCanceledException)
        {
            error = ErrorSemTimeout;
        }
        catch (IOException e)
        {
            error = e.HResult & 0xFFFF;
        }
        if (error == Native.ErrorSuccess)
        {
            return pipe;
        }
        pipe.Dispose();
        return null;
    }

    // Rejects pipes squatted by another process under the service's name.
    private static unsafe bool FromService(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint server))
        {
            return false;
        }
        nint scm = OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
        if (scm == 0)
        {
            return false;
        }
        try
        {
            nint service = OpenService(scm, "WPodsService", 0x0004 /* SERVICE_QUERY_STATUS */);
            if (service == 0)
            {
                return false;
            }
            try
            {
                // SERVICE_STATUS_PROCESS: 7 DWORDs of status, then dwProcessId, dwServiceFlags.
                uint* status = stackalloc uint[9];
                return QueryServiceStatusEx(service, 0 /* SC_STATUS_PROCESS_INFO */, (byte*)status, 9 * sizeof(uint), out _)
                    && status[7] == server;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenService(nint scm, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryServiceStatusEx(nint service, int level, byte* buffer, uint size, out uint needed);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafePipeHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipe(string name, uint timeoutMs);
}
