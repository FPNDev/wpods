namespace WPods.Service;

/// <summary>
/// Pipe contract between the WPods service (LocalSystem) and the per-user app. Local only, message mode, ACL'd to
/// SYSTEM, Administrators and interactive users; integers little-endian.
///
/// \\.\pipe\WPods.Aap - one connection = one AAP channel (L2CAP PSM 0x1001) to one AirPods.
///   app -> service, first: "AP" + u64 Bluetooth address.
///   service -> app: 0x02 + u32 Win32 error (0 open, 1167 no ACL link, 170 already open, 1225 refused, 121 timeout);
///     the pipe closes after a nonzero reply.
///   Open: app -> service = one AAP packet per message (max 1691 bytes).
///         service -> app = 0x00 + inbound AAP packet; 0x01 + u32 error when the channel ended (1167 link lost,
///         0 closed by the AirPods), then the pipe closes; 0x03 + u8 call audio (Hands-Free) starting or up (1) or
///         ended (0), sent before the call audio link is up, on change and on open.
///   Closing the pipe closes the channel.
///
/// \\.\pipe\WPods.Mic - the virtual microphone "Headset (A2DP, &lt;AirPods name&gt;)". One client at a time.
///   app -> service: 0x10 + u64 address + u8 name source + UTF-8 name, on connect and on change. Source 1 = AirPods'
///     name, 0 = Windows' name (used only while the service has none). Only that pair's mic is shown; renaming the mic
///     being recorded from waits until recording stops.
///   app -> service: 0x11 + PCM, mono 48 kHz s16le, even length, max 65536 bytes.
///   service -> app: 0x12 + u8 capture state (1 = an app records). After connect and on change.
///
/// \\.\pipe\WPods.Service - service state and updates.
///   service -> app: 0x20 + u8 state + UTF-8 detail. After connect and on change.
///   service -> app: 0x22 + u8 UpdateStatus + u8 flags (1 auto-update on, 2 check finished)
///     + UTF-8 "installed\0available\0release notes URL\0error code" (fields empty when none). After connect and on change.
///   app -> service: 0x21 = retry the adapter takeover after the Guard handed it back.
///   app -> service: 0x23 + u8 (1 on, 0 off) = auto-update; 0x24 = check for updates; 0x25 = update now;
///     0x26 = app started (a downloaded update installs then).
/// </summary>
internal static class Protocol
{
    public const string AapPipe = "WPods.Aap";
    public const string MicPipe = "WPods.Mic";
    public const string StatusPipe = "WPods.Service";

    public const byte AapData = 0x00;
    public const byte AapEnded = 0x01;
    public const byte AapOpenResult = 0x02;
    public const byte AapCallAudio = 0x03;
    public const byte MicDevice = 0x10;
    public const byte MicPcm = 0x11;
    public const byte MicCapture = 0x12;
    public const byte Status = 0x20;
    public const byte Retry = 0x21;
    public const byte UpdateInfo = 0x22;
    public const byte SetAutoUpdate = 0x23;
    public const byte CheckUpdate = 0x24;
    public const byte UpdateNow = 0x25;
    public const byte AppStarted = 0x26;

    public const int MaxAapPacket = 1691;
    public const int MaxPcm = 65536;

    public const int ErrorTimeout = 121;
    public const int ErrorBusy = 170;
    public const int ErrorNotConnected = 1167;
    public const int ErrorRefused = 1225;
}

/// <summary>Values of the 0x20 status message.</summary>
internal enum ServiceState : byte
{
    /// <summary>Taking over the adapter.</summary>
    Starting = 0,
    /// <summary>The adapter runs through WPods; all features available.</summary>
    Running = 1,
    /// <summary>The Guard gave the adapter back to Windows' own driver after repeated failures (error B2).</summary>
    Native = 2,
    /// <summary>
    /// No adapter to take over. Detail: "not-usb" = only non-USB adapters (UART, SDIO; error B3), "none" = no adapter,
    /// "usbip-missing" = usbip-win2 not installed.
    /// </summary>
    Unsupported = 3,
    /// <summary>An update is installing: the app closes and the new service reopens it.</summary>
    Updating = 4,
}
