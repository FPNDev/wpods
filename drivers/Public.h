// User-mode visible definitions shared by the WPods drivers and the WPods app.
#pragma once

// NTAPD device interface, one instance per bound AirPods (BTHENUM AAP service PDO).
// {904e6502-b92f-464d-8817-108623e33703}
DEFINE_GUID(GUID_NTAPD_DEVICE_INTERFACE,
    0x904e6502, 0xb92f, 0x464d, 0x88, 0x17, 0x10, 0x86, 0x23, 0xe3, 0x37, 0x03);

// NTA2DPVM control device: \\.\NTA2DPVM
// WriteFile: mono 48 kHz signed 16-bit little-endian PCM, even length, at most 65536 bytes.
//
// IOCTL_NTA2DPVM_WAIT_CAPTURE_STATE
//   in:  ULONG last known state
//   out: ULONG state, 0 = no capture stream running, 1 = at least one capture stream running
//   Completes at once when the state differs from the input, otherwise pends until it changes.
//   Pending requests complete with STATUS_CANCELLED on CancelIoEx or handle close.
#define IOCTL_NTA2DPVM_WAIT_CAPTURE_STATE \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x901, METHOD_BUFFERED, FILE_READ_ACCESS)
