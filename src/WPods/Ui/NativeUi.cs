using System.Runtime.InteropServices;

namespace WPods.Ui;

/// <summary>Win32 calls of the UI host.</summary>
internal static unsafe partial class NativeUi
{
    public const int WmSettingChange = 0x001A;
    public const int WmDisplayChange = 0x007E;
    public const int WmGetMinMaxInfo = 0x0024;
    public const int WmNcCalcSize = 0x0083;
    public const int WmNcHitTest = 0x0084;
    public const int WmNcLButtonDown = 0x00A1;
    public const int WmHotKey = 0x0312;
    public const int WmExitSizeMove = 0x0232;
    public const int WmDpiChanged = 0x02E0;
    public const int WmDwmColorizationColorChanged = 0x0320;
    public const int WmMouseActivate = 0x0021;

    public const int HtClient = 1;
    public const int HtCaption = 2;
    public const int HtTop = 12;
    public const int HtBottom = 15;
    public const int MaNoActivate = 3;

    public const int WsPopup = unchecked((int)0x80000000);
    public const int WsThickFrame = 0x00040000;
    public const int WsExTopmost = 0x00000008;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;

    public const int SmCxSmIcon = 49;

    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    public const int DwmwaUseImmersiveDarkMode = 20;
    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwcpRound = 2;

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MuiLanguageName = 0x8;
    private const uint LocaleSNativeDisplayName = 0x73;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hwnd, int id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    /// <summary>Lets any process (the running instance) take the foreground; called by a second instance before it signals.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(long point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserPreferredUILanguages(uint flags, out uint count, char* buffer, ref uint length);

    [LibraryImport("kernel32.dll")]
    private static partial int GetUserDefaultLocaleName(char* name, int length);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetLocaleInfoEx(string locale, uint type, char* data, int length);

    public static void SetBounds(nint hwnd, Rectangle bounds) =>
        SetWindowPos(hwnd, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoZOrder | SwpNoActivate);

    public static void SetWindowInt(nint hwnd, int attribute, int value) => DwmSetWindowAttribute(hwnd, attribute, value, sizeof(int));

    /// <summary>Work area and effective DPI of the monitor nearest to <paramref name="point"/> (POINT passed by value as one 64-bit value).</summary>
    public static (Rectangle WorkArea, uint Dpi) MonitorAt(Point point) => Monitor(MonitorFromPoint((uint)point.X | ((long)point.Y << 32), MonitorDefaultToNearest));

    public static (Rectangle WorkArea, uint Dpi) PrimaryMonitor() => Monitor(MonitorFromPoint(0, MonitorDefaultToPrimary));

    private static (Rectangle WorkArea, uint Dpi) Monitor(nint monitor)
    {
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        GetMonitorInfo(monitor, ref info);
        if (GetDpiForMonitor(monitor, 0, out uint dpi, out _) < 0)
        {
            dpi = 96;
        }
        return (Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom), dpi);
    }

    /// <summary>First Windows display language (e.g. "uk-UA"); "" if none.</summary>
    public static string UserUiLanguage()
    {
        char* buffer = stackalloc char[256];
        uint length = 256;
        if (GetUserPreferredUILanguages(MuiLanguageName, out _, buffer, ref length) && length > 1)
        {
            return new string(buffer);
        }
        int count = GetUserDefaultLocaleName(buffer, 85);
        return count > 1 ? new string(buffer) : "";
    }

    /// <summary>The language's name in itself, e.g. "English (United States)".</summary>
    public static string NativeDisplayName(string locale)
    {
        char* buffer = stackalloc char[128];
        int count = GetLocaleInfoEx(locale, LocaleSNativeDisplayName, buffer, 128);
        return count > 1 ? new string(buffer) : locale;
    }
}
