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
// Same, on the Hands-Free filter(s) only: drops the Hands-Free connection (and its call audio), A2DP stays.
int32_t __stdcall wpn_bt_disconnect_handsfree(uint64_t btAddress);

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
// Returns 1 if an app records from the paired Classic BT device's mic (an active audio session on a
// capture endpoint with its container ID: only Hands-Free has one), 0 if not or if there is no such
// endpoint, negative HRESULT on error. Synchronous, call off the UI thread.
int32_t __stdcall wpn_bt_handsfree_mic_in_use(uint64_t btAddress);
// Sets flush-to-zero (0x8000) and denormals-are-zero (0x0040) in the calling thread's MXCSR, for good: only for a
// thread of the caller's own (never a thread-pool thread).
void __stdcall wpn_fp_set_ftz_daz(void);

// ---- Jitter buffer (jitter.cpp) ----
// The WPods mic's jitter buffer, used by the service's virtual USB mic and the mixer's direct source. Thread-safe
// between one producer (push) and one consumer (pull); real-time safe.
// Gets a summary line once a minute while dropouts happen, outside the internal lock; must return quickly.
typedef void (__stdcall *wpn_jitter_log_cb)(void* ctx, const wchar_t* line);
typedef struct wpn_jitter_stats {
    int64_t received;  // samples pushed
    int64_t sent;      // samples pulled
    int64_t concealed; // pulled samples that were concealment or silence
    int64_t dropouts;  // dropouts concealed
    int64_t trimmed;   // samples that arrived after their time had passed
    int32_t delayMs;   // current playout delay
} wpn_jitter_stats;
// Mono 48 kHz; NULL on allocation failure. Lives as long as the process (there is no destroy). log may be NULL.
void* __stdcall wpn_jitter_create(wpn_jitter_log_cb log, void* ctx);
// Empties it (capture start); the last stream's arrival spread (at most 40 ms) is used at least for the first seconds;
// stats zeroed.
void __stdcall wpn_jitter_reset(void* jitter);
// Queues PCM whose first sample is at pos on the AirPods' clock (48 kHz samples).
void __stdcall wpn_jitter_push(void* jitter, const int16_t* pcm, uint32_t count, int64_t pos);
// The next count samples, int16 scale (-32768..32767); never fails (concealment or silence when the PCM ran out).
void __stdcall wpn_jitter_pull(void* jitter, float* out, uint32_t count);
void __stdcall wpn_jitter_get_stats(void* jitter, wpn_jitter_stats* stats);

// Mixer (mixer.cpp): plays picked endpoints live in the pair's A2DP endpoint (Hands-Free while that is active), on top
// of Windows audio. The WPods virtual mic is played directly from wpn_mixer_push_mic instead of through Windows.
// Event kinds: 0 = devices changed (id NULL: re-run wpn_mixer_enum_devices); 1 = a source no longer exists in Windows
// and was dropped (id = its endpoint ID); 2 = a log line from the direct source's jitter buffer.
typedef void (__stdcall *wpn_mixer_event_cb)(void* ctx, int32_t kind, const wchar_t* id);
// Callbacks run on any thread, must return quickly and never call wpn_mixer_shutdown. Returns HRESULT; S_FALSE when
// already initialized (the first callback stays).
int32_t __stdcall wpn_mixer_init(wpn_mixer_event_cb callback, void* ctx);
// Stops the engine (all streams, thread), forgets all sources and the target, unregisters. After return no further
// callbacks run. Safe when not initialized.
void __stdcall wpn_mixer_shutdown(void);
// flow: 0 render, 1 capture. state: DEVICE_STATE_*. pair: the address from pairs[] whose Hands-Free mic this is,
// else 0. flags: 1 = the WPods virtual mic. Strings are valid only during the callback.
typedef void (__stdcall *wpn_mixer_device_cb)(void* ctx, const wchar_t* id, const wchar_t* name, int32_t flow,
                                              uint32_t state, uint64_t pair, uint32_t flags);
// Every audio endpoint in any state, except the render endpoints of the pairs in pairs[]. Synchronous; callable
// without wpn_mixer_init. Returns HRESULT.
int32_t __stdcall wpn_mixer_enum_devices(const uint64_t* pairs, uint32_t pairCount, wpn_mixer_device_cb cb,
                                         void* ctx);
// Plays to the paired device with this address (0 = none). S_OK does not mean found: the engine looks it up.
int32_t __stdcall wpn_mixer_set_target(uint64_t btAddress);
// Adds a source by endpoint ID (render endpoints are captured by loopback) or updates its gain (linear, 0..1).
int32_t __stdcall wpn_mixer_set_source(const wchar_t* id, float gain);
// Removes a source; S_FALSE if it was not added. Returns HRESULT.
int32_t __stdcall wpn_mixer_remove_source(const wchar_t* id);
// Feeds the WPods mic's PCM (mono 48 kHz, what the virtual mic gets) to the direct source; ignored while there is none.
// One producer thread. pos: the first sample's position on the AirPods' clock (see wpn_jitter_push).
void __stdcall wpn_mixer_push_mic(const int16_t* pcm, uint32_t count, int64_t pos);
// All wpn_mixer_* functions are thread-safe and never hold an internal lock while a callback runs, so they may be
// called under a lock the event callback also takes.

// Loopback capture (mixer.cpp): what Windows plays to the paired device with this address (its A2DP output, else its
// Hands-Free one) as mono float 48 kHz; period: samples per capture packet. Returns HRESULT (ERROR_NOT_FOUND when the
// device has no active output). Independent of wpn_mixer_init; one thread at a time per capture.
int32_t __stdcall wpn_loopback_open(uint64_t btAddress, void** capture, uint32_t* period);
// Waits up to timeoutMs for audio when none is buffered; returns the samples written to out (at most capacity; 0 = none
// came, as while Windows plays nothing there), or a negative HRESULT when the stream failed (close it and reopen).
int32_t __stdcall wpn_loopback_read(void* capture, float* out, uint32_t capacity, uint32_t timeoutMs);
// Stops and frees it; NULL is ignored.
void __stdcall wpn_loopback_close(void* capture);

// ---- LC3 codec (lc3.cpp, Google's liblc3) ----
// frame_us: 7500 or 10000. sample_rate: LC3 codec rate (8000, 16000, 24000, 32000, 48000). pcm_rate: rate of the
// float PCM given/returned (>= sample_rate, e.g. 48000; liblc3 resamples internally via its sr_pcm parameter).
// Returns NULL on bad parameters or allocation failure.
void* __cdecl wpn_lc3_decoder_create(int frame_us, int sample_rate, int pcm_rate);
// Decodes one frame of nbytes (frame != NULL) or conceals a lost frame (frame == NULL, nbytes ignored). Writes one
// frame of mono floats in [-1, 1] at pcm_rate to pcm (frame_us * pcm_rate / 1e6, e.g. 360 for 7.5 ms at 48 kHz).
// Returns the sample count, or < 0 on error.
int __cdecl wpn_lc3_decode(void* decoder, const unsigned char* frame, int nbytes, float* pcm);
void __cdecl wpn_lc3_decoder_free(void* decoder);
void* __cdecl wpn_lc3_encoder_create(int frame_us, int sample_rate, int pcm_rate);
// Encodes one frame of mono floats in [-1, 1] at pcm_rate into exactly nbytes (20..400). Returns 0, or < 0 on error.
int __cdecl wpn_lc3_encode(void* encoder, const float* pcm, int nbytes, unsigned char* frame);
void __cdecl wpn_lc3_encoder_free(void* encoder);

#ifdef __cplusplus
}
#endif

#endif
