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

// Media functions below: synchronous (each WinRT call waits at most 3 s), call off the UI thread;
// they never initialize COM on the caller's thread (implicit MTA, see wpods_native.cpp). They are serialized.
// Pauses EVERY Windows media session (GlobalSystemMediaTransportControlsSessionManager::GetSessions) whose
// PlaybackStatus is Playing and adds their SourceAppUserModelIds to the remembered set (earlier entries stay).
// While the set is not empty the DLL watches SessionsChanged and every session's PlaybackInfoChanged: when the
// number of Playing sessions rises above the fewest seen since this call (the user played a remembered session,
// or any app started playback), the set is forgotten. Returns the number of sessions paused (0 = nothing was
// playing), negative HRESULT when nothing was paused because of an error (E_FAIL = an app refused the pause,
// HRESULT_FROM_WIN32(ERROR_TIMEOUT) = no answer within 3 s).
int32_t __stdcall wpn_media_pause_all(void);
// If no session is Playing, resumes the remembered sessions that still exist and are still Paused (each
// remembered entry resumes at most one session of that app); always forgets the set and stops watching.
// Returns the number of sessions resumed (0 = nothing remembered, set forgotten by the watch, a session is
// playing, or none still paused), negative HRESULT when nothing was resumed because of an error (same codes).
int32_t __stdcall wpn_media_resume_paused(void);
// Forgets the remembered sessions without resuming them and stops watching.
void __stdcall wpn_media_forget(void);
// Returns 1 if the default eConsole render endpoint (the endpoint Windows plays to) belongs to the
// paired Classic BT device with this address (its PKEY_Device_ContainerId equals the container ID of
// the present BTHENUM\DEV_<address> devnode; A2DP and Hands-Free endpoints share it), 0 if not or
// if there is no default render endpoint, negative HRESULT on error.
int32_t __stdcall wpn_audio_is_default_output(uint64_t btAddress);
// Sets flush-to-zero (0x8000) and denormals-are-zero (0x0040) in the calling thread's MXCSR and returns the
// previous MXCSR. Pair with wpn_fp_restore on the same thread, so a thread-pool thread is handed back unchanged.
uint32_t __stdcall wpn_fp_set_ftz_daz(void);
// Writes mxcsr (a value returned by wpn_fp_set_ftz_daz) back to the calling thread's MXCSR.
void __stdcall wpn_fp_restore(uint32_t mxcsr);

#ifdef __cplusplus
}
#endif

#endif
