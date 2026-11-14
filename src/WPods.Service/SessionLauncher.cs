using System.Runtime.InteropServices;

namespace WPods.Service;

/// <summary>Starts a program as the user signed in to a session (the service runs as LocalSystem in session 0).</summary>
internal static unsafe partial class SessionLauncher
{
    public static void Start(uint session, string exe)
    {
        if (!WTSQueryUserToken(session, out nint token))
        {
            Log.Warn("update", $"no signed-in user in session {session} (Win32 error {Marshal.GetLastPInvokeError()}); app not reopened");
            return;
        }
        nint env = 0;
        try
        {
            CreateEnvironmentBlock(out env, token, false);
            fixed (char* desktop = "winsta0\\default")
            fixed (char* cmd = $"\"{exe}\"")
            fixed (char* dir = Path.GetDirectoryName(exe))
            {
                var si = new StartupInfo { Cb = sizeof(StartupInfo), Desktop = desktop };
                if (!CreateProcessAsUser(token, null, cmd, 0, 0, false, 0x00000400 /* CREATE_UNICODE_ENVIRONMENT */, env, dir, ref si, out ProcessInformation pi))
                {
                    Log.Warn("update", $"app not reopened in session {session}: Win32 error {Marshal.GetLastPInvokeError()}");
                    return;
                }
                CloseHandle(pi.Process);
                CloseHandle(pi.Thread);
                Log.Info("update", $"app reopened in session {session}");
            }
        }
        finally
        {
            if (env != 0)
            {
                DestroyEnvironmentBlock(env);
            }
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Cb;
        public char* Reserved;
        public char* Desktop;
        public char* Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved2Ptr, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQueryUserToken(uint session, out nint token);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out nint env, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(nint env);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessAsUser(nint token, string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, char* directory, ref StartupInfo startup, out ProcessInformation info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
