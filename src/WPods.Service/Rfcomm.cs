namespace WPods.Service;

/// <summary>
/// RFCOMM (TS 07.10 basic mode) frames as the session initiator, with credit-based flow control offered in PN.
/// The initiator sends commands and UIH frames with C/R = 1 and responses with C/R = 0.
/// </summary>
internal static class Rfcomm
{
    // Control field without the P/F bit.
    public const byte Sabm = 0x2F;
    public const byte Ua = 0x63;
    public const byte Dm = 0x0F;
    public const byte Disc = 0x43;
    public const byte Uih = 0xEF;
    /// <summary>Multiplexer message type of Parameter Negotiation (first byte of a DLC 0 UIH frame, >> 2).</summary>
    public const byte PnType = 0x20;
    private const byte PollFinal = 0x10;
    // Credits PN gives the AirPods for the DLC: enough for their AT commands until the decline closes it.
    private const byte InitialCredits = 7;
    private const int MaxFrame = 127;

    /// <summary>SABM or DISC with P set.</summary>
    public static byte[] Command(byte dlci, byte control) => Frame((byte)((dlci << 2) | 0x03), (byte)(control | PollFinal), [], fcsLength: 3);

    /// <summary>UA or DM with F set.</summary>
    public static byte[] Response(byte dlci, byte control) => Frame((byte)((dlci << 2) | 0x01), (byte)(control | PollFinal), [], fcsLength: 3);

    /// <summary>UIH frame; its FCS covers address and control only.</summary>
    public static byte[] Data(byte dlci, ReadOnlySpan<byte> info) => Frame((byte)((dlci << 2) | 0x03), Uih, info, fcsLength: 2);

    /// <summary>Modem Status command on DLC 0 for <paramref name="dlci"/>: RTC, RTR and DV set.</summary>
    public static byte[] ModemStatus(byte dlci) => Data(0, [0xE3, 0x05, (byte)((dlci << 2) | 0x03), 0x8D]);

    /// <summary>
    /// Parameter Negotiation command on DLC 0 for <paramref name="dlci"/>: UIH frames, credit-based flow control
    /// (CL 0xF), priority 7, N1 127, <see cref="InitialCredits"/> credits for the AirPods.
    /// </summary>
    public static byte[] ParameterNegotiation(byte dlci) =>
        Data(0, [(PnType << 2) | 0x03, 0x11, dlci, 0xF0, 0x07, 0x00, MaxFrame, 0x00, 0x00, InitialCredits]);

    /// <summary>
    /// Splits a frame; <paramref name="control"/> is without the P/F bit. A UIH frame with P/F set starts with a credit
    /// byte (credit-based flow control), which is skipped. The FCS is not checked (L2CAP has its own).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> frame, out byte dlci, out byte control, out ReadOnlySpan<byte> info)
    {
        dlci = 0;
        control = 0;
        info = default;
        if (frame.Length < 4)
        {
            return false;
        }
        dlci = (byte)(frame[0] >> 2);
        control = (byte)(frame[1] & ~PollFinal);
        int length = frame[2] >> 1;
        int header = 3;
        if ((frame[2] & 1) == 0)
        {
            length |= frame[3] << 7;
            header = 4;
        }
        if (control == Uih && (frame[1] & PollFinal) != 0)
        {
            header++;
        }
        if (header + length + 1 > frame.Length)
        {
            return false;
        }
        info = frame.Slice(header, length);
        return true;
    }

    private static byte[] Frame(byte address, byte control, ReadOnlySpan<byte> info, int fcsLength)
    {
        int header = info.Length < 128 ? 3 : 4;
        var frame = new byte[header + info.Length + 1];
        frame[0] = address;
        frame[1] = control;
        if (header == 3)
        {
            frame[2] = (byte)((info.Length << 1) | 1);
        }
        else
        {
            frame[2] = (byte)(info.Length << 1);
            frame[3] = (byte)(info.Length >> 7);
        }
        info.CopyTo(frame.AsSpan(header));
        frame[^1] = Fcs(frame.AsSpan(0, fcsLength));
        return frame;
    }

    // TS 07.10 CRC-8 (reversed polynomial 0xE0), sent as its complement.
    private static byte Fcs(ReadOnlySpan<byte> bytes)
    {
        int crc = 0xFF;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xE0 : crc >> 1;
            }
        }
        return (byte)(0xFF - crc);
    }
}
