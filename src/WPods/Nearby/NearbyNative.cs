using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WPods.Nearby;

/// <summary>wpods_native.dll: passive BLE scan for Apple Proximity Pairing adverts and the KS one-shot connect.</summary>
internal static unsafe partial class NearbyNative
{
    /// <summary>Callback: (ctx, address, addressType 0 public / 1 random, rssi, payload after the company ID, length).</summary>
    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int wpn_ble_start(delegate* unmanaged[Stdcall]<nint, ulong, byte, short, byte*, uint, void> callback, nint context);

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial void wpn_ble_stop();

    [LibraryImport("wpods_native.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial int wpn_bt_connect_audio(ulong btAddress);
}
