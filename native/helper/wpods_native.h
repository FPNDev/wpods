#ifndef WPODS_NATIVE_H
#define WPODS_NATIVE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Called for each received BLE advertisement whose manufacturer data has company ID 0x004C (Apple)
// and whose payload starts with type byte 0x07 (Proximity Pairing). data/length = manufacturer
// payload AFTER the 2-byte company ID (so data[0] == 0x07). Called on a background thread;
// the callee must return quickly and must not call wpn_ble_stop from inside the callback.
// addressType: 0 = public, 1 = random.
typedef void (__stdcall *wpn_adv_cb)(void* ctx, uint64_t address, uint8_t addressType,
                                     int16_t rssi, const uint8_t* data, uint32_t length);
// Starts a PASSIVE BLE scan (never sends scan requests). Returns HRESULT (S_OK, or failure e.g.
// no BLE radio). Calling while started returns S_FALSE and keeps the first callback.
int32_t __stdcall wpn_ble_start(wpn_adv_cb callback, void* ctx);
// Stops the scan; after return no further callbacks run. Safe to call when not started.
void __stdcall wpn_ble_stop(void);
// Asks Windows to connect the paired Classic BT audio device with this address, like the
// Settings "Connect" button (KSPROPSETID_BtAudio / KSPROPERTY_ONESHOT_RECONNECT on its BT audio
// KS filter(s)). Returns HRESULT: S_OK = request issued to at least one filter (does NOT mean
// connected - confirm via ACL link event); HRESULT_FROM_WIN32(ERROR_NOT_FOUND) = no BT audio
// filter for that address. Synchronous, may take up to a few hundred ms; call off the UI thread.
int32_t __stdcall wpn_bt_connect_audio(uint64_t btAddress);
// Same lookup, KSPROPERTY_ONESHOT_DISCONNECT: asks Windows to drop the device's BT audio
// connection (Mac-like Disconnect). Same return semantics; confirm via ACL link event.
int32_t __stdcall wpn_bt_disconnect_audio(uint64_t btAddress);

#ifdef __cplusplus
}
#endif

#endif
