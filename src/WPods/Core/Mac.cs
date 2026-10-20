using System.Globalization;

namespace WPods.Core;

/// <summary>Bluetooth address helpers; addresses are BLUETOOTH_ADDRESS values (48 bits in a ulong).</summary>
public static class Mac
{
    /// <summary>12 uppercase hex digits, e.g. 40DA5C668CB3 (settings key form).</summary>
    public static string Format(ulong address) => address.ToString("X12", CultureInfo.InvariantCulture);

    /// <summary>Accepts 12 hex digits, optionally separated by ':' or '-'.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out ulong address)
    {
        Span<char> digits = stackalloc char[12];
        int count = 0;
        foreach (char c in text)
        {
            if (c is ':' or '-')
            {
                continue;
            }
            if (count == 12)
            {
                address = 0;
                return false;
            }
            digits[count++] = c;
        }
        address = 0;
        return count == 12 && ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
    }

    /// <summary>Address in an NTAPD interface path: "&amp;" + 12 hex digits + "_c".</summary>
    internal static bool TryParseInterfacePath(ReadOnlySpan<char> path, out ulong address)
    {
        for (int i = 0; i + 15 <= path.Length; i++)
        {
            if (path[i] == '&' && path[i + 13] == '_' && (path[i + 14] | 0x20) == 'c'
                && ulong.TryParse(path.Slice(i + 1, 12), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address))
            {
                return true;
            }
        }
        address = 0;
        return false;
    }
}
