using System.ComponentModel;

namespace WPods.Core;

/// <summary>Enables or disables the Hands-Free (HFP, 0x111E) service of one paired device. Blocking; call off the UI thread.</summary>
internal static unsafe class HandsFree
{
    private static readonly Guid Service = new("0000111E-0000-1000-8000-00805F9B34FB");

    /// <summary><paramref name="changing"/> runs just before Windows is asked to change the service (not when it is already in that state).</summary>
    /// <exception cref="Win32Exception">Windows rejected the change.</exception>
    /// <exception cref="InvalidOperationException">The device is not paired on any radio.</exception>
    public static void SetEnabled(ulong address, bool enabled, Action? changing = null)
    {
        if (!Native.TryFindPairedDevice(address, out nint radio, out Native.BluetoothDeviceInfo device))
        {
            throw new InvalidOperationException("Windows could not find this paired AirPods device on any Bluetooth radio.");
        }
        try
        {
            if (TryIsEnabled(radio, device, out bool current, out _) && current == enabled)
            {
                return;
            }
            changing?.Invoke();
            uint result = Native.BluetoothSetServiceState(radio, device, Service, enabled ? 1u : 0u);
            // Windows may report the already-applied state as E_INVALIDARG, or race the state query and return
            // ERROR_INVALID_PARAMETER; confirm the requested state before treating the latter as done.
            if (result is Native.ErrorSuccess or Native.EInvalidArg)
            {
                return;
            }
            if (result == Native.ErrorInvalidParameter && TryIsEnabled(radio, device, out current, out _) && current == enabled)
            {
                return;
            }
            uint queryResult = 0;
            if (!enabled && TryIsEnabled(radio, device, out current, out queryResult) && !current)
            {
                return;
            }
            string details = enabled ? string.Empty : $" Profile-state query returned 0x{queryResult:X8}.";
            throw new Win32Exception(unchecked((int)result),
                $"Windows could not {(enabled ? "enable" : "disable")} Hands-Free Telephony for this device.{details}");
        }
        finally
        {
            Native.CloseHandle(radio);
        }
    }

    private static bool TryIsEnabled(nint radio, in Native.BluetoothDeviceInfo device, out bool enabled, out uint result)
    {
        enabled = false;
        uint count = 0;
        result = Native.BluetoothEnumerateInstalledServices(radio, device, ref count, null);
        if (result == Native.ErrorNotFound)
        {
            return true;
        }
        if (result is not (Native.ErrorSuccess or Native.ErrorMoreData))
        {
            return false;
        }
        if (count == 0)
        {
            return result == Native.ErrorSuccess;
        }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Guid[] services = new Guid[count];
            uint capacity = count;
            fixed (Guid* p = services)
            {
                result = Native.BluetoothEnumerateInstalledServices(radio, device, ref capacity, p);
            }
            if (result == Native.ErrorNotFound)
            {
                return true;
            }
            if (result == Native.ErrorMoreData)
            {
                count = capacity > count ? capacity : count + 1;
                continue;
            }
            if (result != Native.ErrorSuccess)
            {
                return false;
            }
            enabled = services.AsSpan(0, (int)Math.Min(capacity, count)).Contains(Service);
            return true;
        }
        return false;
    }
}
