using System.Security.Cryptography;
using WPods.Core;

namespace WPods.Nearby;

/// <summary>Batteries of a nearby pair; absent = default. <see cref="Headphone"/> is used instead of the others for single-battery models.</summary>
public readonly record struct PodBatteries(Battery Left, Battery Right, Battery Case, Battery Headphone);

/// <summary>
/// Apple Continuity Proximity Pairing advert: manufacturer payload after company ID 0x004C, 27 bytes, type 0x07.
/// Layout: [1] length 0x19, [2] 0x01 = paired, [3..4] model, [5] status (bit 5 = left first), [6] pod nibbles,
/// [7] charging flags | case nibble, [8] lid (bit 3 = closed), [11..26] AES-128-ECB with the pair's ENC_KEY.
/// </summary>
internal static class Proximity
{
    private const int Length = 27;
    private const int MaxNibble = 10;
    private const int MaxLevel = 100;
    private const byte Absent = 0xFF;

    /// <summary>A paired-state advert from a resolvable private (random, top bits 01) address.</summary>
    public static bool IsValid(ulong address, byte addressType, ReadOnlySpan<byte> data) =>
        addressType == 1 && address >> 46 == 0b01
        && data.Length == Length && data[0] == 0x07 && data[1] == Length - 2 && data[2] == 0x01;

    /// <summary>Bytes 3-4 little-endian (Apple Bluetooth product ID).</summary>
    public static ushort ModelId(ReadOnlySpan<byte> data) => (ushort)(data[3] | data[4] << 8);

    public static bool IsLidOpen(ReadOnlySpan<byte> data) => (data[8] & 0x08) == 0;

    /// <summary>Status bit 2 (both in case), bit 4 (one in case) or bit 6 (this bud in case).</summary>
    public static bool IsBudInCase(ReadOnlySpan<byte> data) => (data[5] & 0x54) != 0;

    /// <summary>Status bit 2: both buds in the case.</summary>
    public static bool AreBothInCase(ReadOnlySpan<byte> data) => (data[5] & 0x04) != 0;

    /// <summary>Cipher for <see cref="Resolves"/>. AAP delivers the IRK least-significant byte first; AES takes it reversed.</summary>
    public static Aes CreateIrkCipher(ReadOnlySpan<byte> irk)
    {
        Span<byte> key = stackalloc byte[16];
        irk.CopyTo(key);
        key.Reverse();
        return CreateCipher(key);
    }

    /// <summary>Cipher for the encrypted payload: ENC_KEY exactly as received.</summary>
    public static Aes CreateCipher(ReadOnlySpan<byte> key)
    {
        var aes = Aes.Create();
        aes.SetKey(key);
        return aes;
    }

    /// <summary>Bluetooth Core Vol 3 Part H 2.2.2: the low 24 bits (hash) equal ah(IRK, prand = the next 24 bits).</summary>
    public static bool Resolves(Aes irk, ulong address)
    {
        Span<byte> block = stackalloc byte[16];
        Span<byte> output = stackalloc byte[16];
        block[13] = (byte)(address >> 40);
        block[14] = (byte)(address >> 32);
        block[15] = (byte)(address >> 24);
        irk.EncryptEcb(block, output, PaddingMode.None);
        return (output[13] << 16 | output[14] << 8 | output[15]) == (int)(address & 0xFFFFFF);
    }

    /// <summary>
    /// Exact levels from the encrypted payload when they agree with the clear nibbles (x10, within 10 %), else the
    /// nibbles. A decrypted level that is neither valid nor 0xFF (absent) keeps the matching value of <paramref name="previous"/>.
    /// </summary>
    public static PodBatteries ReadBatteries(ReadOnlySpan<byte> data, Aes encKey, bool single, in PodBatteries previous)
    {
        bool leftFirst = (data[5] & 0x20) != 0;
        int low = data[6] & 0x0F;
        int high = data[6] >> 4;
        int flags = data[7] >> 4;
        Span<byte> plain = stackalloc byte[16];
        encKey.DecryptEcb(data[11..], plain, PaddingMode.None);

        if (single)
        {
            Battery clearHeadphone = FromNibble(low, (flags & 1) != 0);
            Battery headphone = previous.Headphone;
            for (int i = 1; i <= 3; i++)
            {
                if ((plain[i] & 0x7F) <= MaxLevel)
                {
                    headphone = FromLevel(plain[i], previous.Headphone);
                    break;
                }
            }
            return new PodBatteries(default, default, default, Agree(clearHeadphone, headphone) == true ? headphone : clearHeadphone);
        }

        Battery clearLeft = FromNibble(leftFirst ? low : high, (flags & (leftFirst ? 1 : 2)) != 0);
        Battery clearRight = FromNibble(leftFirst ? high : low, (flags & (leftFirst ? 2 : 1)) != 0);
        Battery clearCase = FromNibble(data[7] & 0x0F, (flags & 4) != 0);
        Battery left = FromLevel(plain[leftFirst ? 1 : 2], previous.Left);
        Battery right = FromLevel(plain[leftFirst ? 2 : 1], previous.Right);
        Battery @case = FromLevel(plain[3], previous.Case);
        bool? l = Agree(clearLeft, left);
        bool? r = Agree(clearRight, right);
        bool? c = Agree(clearCase, @case);
        bool decrypted = l != false && r != false && c != false && (l == true || r == true || c == true);
        return decrypted ? new PodBatteries(left, right, @case, default) : new PodBatteries(clearLeft, clearRight, clearCase, default);
    }

    private static Battery FromNibble(int nibble, bool charging) =>
        nibble <= MaxNibble ? new Battery((byte)(nibble * 10), charging ? BatteryStatus.Charging : BatteryStatus.Discharging) : default;

    // Bit 7 = charging, bits 0-6 = level.
    private static Battery FromLevel(byte value, Battery previous)
    {
        if (value == Absent)
        {
            return default;
        }
        int level = value & 0x7F;
        return level <= MaxLevel ? new Battery((byte)level, value >= 0x80 ? BatteryStatus.Charging : BatteryStatus.Discharging) : previous;
    }

    // null = nothing to compare (either side absent).
    private static bool? Agree(Battery clear, Battery decrypted) =>
        clear.Percent is { } nibble && decrypted.Percent is { } level ? Math.Abs(level - nibble) <= 10 : null;
}
