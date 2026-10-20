using Microsoft.Win32;

namespace WPods.Ui;

/// <summary>Windows accent ramp as "#rrggbb" (UISettings Accent, AccentDark1..3, AccentLight1..3).</summary>
internal sealed record AccentRamp(string Accent, string Dark1, string Dark2, string Dark3, string Light1, string Light2, string Light3);

/// <summary>Windows colour settings read from HKCU (no WinRT): app/taskbar light mode and the accent palette.</summary>
internal static class WindowsTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    /// <summary>"Choose your default app mode" is Light.</summary>
    public static bool AppsUseLight() => ReadFlag("AppsUseLightTheme");

    /// <summary>"Choose your default Windows mode" (taskbar) is Light.</summary>
    public static bool TaskbarUsesLight() => ReadFlag("SystemUsesLightTheme");

    /// <summary>
    /// AccentPalette = 8 RGBA entries: Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3, (unused). Null when missing.
    /// </summary>
    public static AccentRamp? ReadAccent()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(AccentKey);
        if (key?.GetValue("AccentPalette") is not byte[] { Length: >= 28 } p)
        {
            return null;
        }
        return new AccentRamp(Hex(p, 3), Hex(p, 4), Hex(p, 5), Hex(p, 6), Hex(p, 2), Hex(p, 1), Hex(p, 0));
    }

    private static string Hex(byte[] palette, int entry) =>
        string.Create(7, (palette, entry * 4), static (span, s) =>
        {
            span[0] = '#';
            for (int i = 0; i < 3; i++)
            {
                byte b = s.palette[s.Item2 + i];
                span[1 + i * 2] = "0123456789abcdef"[b >> 4];
                span[2 + i * 2] = "0123456789abcdef"[b & 0xF];
            }
        });

    private static bool ReadFlag(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(name) is not int value || value != 0;
    }
}
