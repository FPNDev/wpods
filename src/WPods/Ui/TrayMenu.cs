using System.Runtime.InteropServices;

namespace WPods.Ui;

/// <summary>
/// The tray icon's right-click menu as a native Win32 popup menu, so Windows draws its own check mark, spacing,
/// DPI scaling and menu theme.
/// </summary>
internal static unsafe partial class TrayMenu
{
    public const int Open = 1;
    public const int Startup = 2;
    public const int Quit = 3;

    private const uint MfString = 0x0000;
    private const uint MfChecked = 0x0008;
    private const uint MfSeparator = 0x0800;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmRightAlign = 0x0008;
    private const uint TpmBottomAlign = 0x0020;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const int SmMenuDropAlignment = 40;
    private const int WmNull = 0x0000;
    // uxtheme.dll exports by ordinal only (undocumented, used by Explorer and Notepad): SetPreferredAppMode, FlushMenuThemes.
    private const nint SetPreferredAppModeOrdinal = 135;
    private const nint FlushMenuThemesOrdinal = 136;
    private const int AllowDark = 1;

    private static delegate* unmanaged<void> s_flushMenuThemes;

    /// <summary>
    /// Lets Win32 menus follow Windows' light/dark app mode. Call once before the first menu; without the ordinals
    /// (other Windows builds) menus stay light.
    /// </summary>
    public static void AllowDarkMode()
    {
        nint uxtheme = LoadLibrary("uxtheme.dll");
        if (uxtheme == 0)
        {
            return;
        }
        nint setPreferredAppMode = GetProcAddress(uxtheme, SetPreferredAppModeOrdinal);
        if (setPreferredAppMode != 0)
        {
            ((delegate* unmanaged<int, int>)setPreferredAppMode)(AllowDark);
        }
        s_flushMenuThemes = (delegate* unmanaged<void>)GetProcAddress(uxtheme, FlushMenuThemesOrdinal);
    }

    /// <summary>Drops cached menu themes after Windows' app mode changed.</summary>
    public static void FlushThemes()
    {
        if (s_flushMenuThemes != null)
        {
            s_flushMenuThemes();
        }
    }

    /// <summary>Shows the menu at the cursor and returns the chosen command (0 = dismissed).</summary>
    public static int Show(nint owner, string open, string startup, bool startupChecked, string quit)
    {
        nint menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MfString, Open, open);
            AppendMenu(menu, MfString | (startupChecked ? MfChecked : 0), Startup, startup);
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, Quit, quit);
            SetMenuDefaultItem(menu, Open, 0);
            // POINT { int x; int y; } read as one 64-bit value.
            GetCursorPos(out long cursor);
            int x = (int)cursor;
            int y = (int)(cursor >> 32);
            uint align = GetSystemMetrics(SmMenuDropAlignment) != 0 ? TpmRightAlign : TpmLeftAlign;
            // The documented tray pattern: foreground first so the menu closes on an outside click, WM_NULL after.
            NativeUi.SetForegroundWindow(owner);
            int command = TrackPopupMenuEx(menu, align | TpmBottomAlign | TpmRightButton | TpmReturnCmd, x, y, owner, 0);
            PostMessage(owner, WmNull, 0, 0);
            return command;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibrary(string name);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetProcAddress(nint module, nint ordinal);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(nint menu, uint flags, nint id, string? text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hwnd, int msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out long point);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
