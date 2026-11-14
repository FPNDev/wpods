using System.Buffers.Binary;
using System.Security;
using Microsoft.Win32;

namespace WPods.Service;

/// <summary>What Windows keeps in the registry for a paired device: its cached SDP records and per-radio service state.</summary>
internal static class BluetoothRegistry
{
    private const string Devices = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";
    private const string HandsFreeService = "{0000111e-0000-1000-8000-00805f9b34fb}";
    private const ushort HandsFreeUuid = 0x111E;
    private const ushort L2capUuid = 0x0100;
    private const ushort RfcommUuid = 0x0003;
    private const ushort AapPsm = 0x1001;

    /// <summary>
    /// Whether Windows has the device's Hands-Free service enabled on <paramref name="radio"/>, however it was set (WPods
    /// or Windows' own settings); null when unknown.
    /// </summary>
    public static bool? HandsFreeEnabled(ulong radio, ulong device)
    {
        try
        {
            using RegistryKey? service = Registry.LocalMachine.OpenSubKey($@"{Devices}\{device:x12}\ServicesFor{radio:x12}\{HandsFreeService}");
            if (service is null)
            {
                return null;
            }
            bool? enabled = null;
            foreach (string name in service.GetSubKeyNames())
            {
                using RegistryKey? instance = service.OpenSubKey(name);
                if (instance?.GetValue("Enabled") is int value)
                {
                    if (value != 0)
                    {
                        return true;
                    }
                    enabled = false;
                }
            }
            return enabled;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            Log.Warn("hfp", $"{device:X12}: Hands-Free state not readable: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// From Windows' cached SDP records of the device: whether it offers AAP (L2CAP PSM 0x1001) and the RFCOMM channel of
    /// its Hands-Free service (0 = none).
    /// </summary>
    public static (bool Aap, byte HandsFreeChannel) ReadServices(ulong device)
    {
        bool aap = false;
        byte channel = 0;
        try
        {
            using RegistryKey? cached = Registry.LocalMachine.OpenSubKey($@"{Devices}\{device:x12}\CachedServices");
            if (cached is null)
            {
                return (false, 0);
            }
            foreach (string name in cached.GetValueNames())
            {
                if (cached.GetValue(name) is byte[] record)
                {
                    ReadRecord(record, ref aap, ref channel);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            Log.Warn("hfp", $"{device:X12}: cached services not readable: {e.Message}");
        }
        return (aap, channel);
    }

    // One SDP record: a sequence of attribute id (uint16) and value pairs. 0x0001 = service class ids, 0x0004 = protocol
    // descriptors (sequences of a protocol UUID and its parameter: L2CAP PSM, RFCOMM channel).
    private static void ReadRecord(ReadOnlySpan<byte> record, ref bool aap, ref byte channel)
    {
        if (!TryElement(ref record, out byte type, out ReadOnlySpan<byte> attributes) || type != 6)
        {
            return;
        }
        bool handsFree = false;
        int rfcomm = 0;
        while (attributes.Length > 0)
        {
            if (!TryElement(ref attributes, out byte idType, out ReadOnlySpan<byte> id) || !TryElement(ref attributes, out byte valueType, out ReadOnlySpan<byte> value))
            {
                return;
            }
            if (idType != 1 || id.Length != 2 || valueType != 6)
            {
                continue;
            }
            ushort attribute = BinaryPrimitives.ReadUInt16BigEndian(id);
            if (attribute == 0x0001)
            {
                while (TryElement(ref value, out byte uuidType, out ReadOnlySpan<byte> uuid))
                {
                    handsFree |= uuidType == 3 && uuid.Length == 2 && BinaryPrimitives.ReadUInt16BigEndian(uuid) == HandsFreeUuid;
                }
            }
            else if (attribute == 0x0004)
            {
                while (TryElement(ref value, out byte layerType, out ReadOnlySpan<byte> layer))
                {
                    if (layerType != 6 || !TryElement(ref layer, out byte uuidType, out ReadOnlySpan<byte> uuid) || uuidType != 3 || uuid.Length != 2)
                    {
                        continue;
                    }
                    ushort protocol = BinaryPrimitives.ReadUInt16BigEndian(uuid);
                    int parameter = TryElement(ref layer, out byte parameterType, out ReadOnlySpan<byte> p) && parameterType == 1
                        ? p.Length switch { 1 => p[0], 2 => BinaryPrimitives.ReadUInt16BigEndian(p), _ => -1 }
                        : -1;
                    aap |= protocol == L2capUuid && parameter == AapPsm;
                    if (protocol == RfcommUuid && parameter is > 0 and <= 30)
                    {
                        rfcomm = parameter;
                    }
                }
            }
        }
        if (handsFree && rfcomm != 0)
        {
            channel = (byte)rfcomm;
        }
    }

    // One SDP data element: header byte (type << 3 | size index), then the size (index 5..7: 1, 2 or 4 bytes, big-endian)
    // and the content.
    private static bool TryElement(ref ReadOnlySpan<byte> data, out byte type, out ReadOnlySpan<byte> content)
    {
        type = 0;
        content = default;
        if (data.Length < 1)
        {
            return false;
        }
        type = (byte)(data[0] >> 3);
        int index = data[0] & 7;
        int at = 1;
        long size;
        if (index < 5)
        {
            size = type == 0 ? 0 : 1 << index;
        }
        else
        {
            int width = 1 << (index - 5);
            if (data.Length < 1 + width)
            {
                return false;
            }
            size = width switch
            {
                1 => data[1],
                2 => BinaryPrimitives.ReadUInt16BigEndian(data[1..]),
                _ => BinaryPrimitives.ReadUInt32BigEndian(data[1..]),
            };
            at += width;
        }
        if (size > data.Length - at)
        {
            return false;
        }
        content = data.Slice(at, (int)size);
        data = data[(at + (int)size)..];
        return true;
    }
}
