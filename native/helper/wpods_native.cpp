// wpods_native.dll: passive BLE Proximity Pairing watcher + BT audio KS one-shot connect/disconnect +
// pause/resume of all playing media sessions (GSMTC) + default-output check + FTZ/DAZ for the mic stages.
//
// Apartments: the DLL never initializes COM on the caller's thread. On first use it calls
// CoIncrementMTAUsage once (never released), which keeps the process MTA alive so that WinRT/COM
// calls work on threads without COM init (they run as implicit MTA). STA threads (WinForms UI)
// stay STA; the watcher and the MMDevice objects are agile/both-threaded, so any thread works.
#include <windows.h>
#include <cfgmgr32.h>
#include <mmdeviceapi.h>
#include <ks.h>
#include <ksmedia.h>
#include <devicetopology.h>
#include <initguid.h>
#include <devpkey.h>
#include <functiondiscoverykeys_devpkey.h>
#include <xmmintrin.h>

#include <algorithm>
#include <chrono>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Devices.Bluetooth.Advertisement.h>
#include <winrt/Windows.Media.Control.h>
#include <winrt/Windows.Storage.Streams.h>

#include "wpods_native.h"

using namespace winrt::Windows::Devices::Bluetooth;
using namespace winrt::Windows::Devices::Bluetooth::Advertisement;
using namespace winrt::Windows::Media::Control;

namespace {

constexpr uint16_t kAppleCompanyId = 0x004C;
constexpr uint8_t kProximityPairing = 0x07;

SRWLOCK g_stateLock = SRWLOCK_INIT;    // serializes wpn_ble_start / wpn_ble_stop
SRWLOCK g_callbackLock = SRWLOCK_INIT; // held while the callback runs, so stop can wait for it
wpn_adv_cb g_callback;
void* g_context;
// Raw ABI pointer instead of a winrt object: no static destructor releasing COM under the loader lock.
void* g_watcher;
winrt::event_token g_receivedToken;

using PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus;

constexpr std::chrono::seconds kMediaTimeout{ 3 };

struct Subscription {
    GlobalSystemMediaTransportControlsSession session{ nullptr };
    winrt::event_token token;
};

// Event subscriptions (SessionsChanged + PlaybackInfoChanged of every session) that notice playback started by the
// user or another app while sessions are remembered. Created and destroyed only by the exported media functions.
struct MediaWatch {
    GlobalSystemMediaTransportControlsSessionManager manager{ nullptr };
    winrt::event_token sessionsChanged;
    std::vector<Subscription> sessions;
};

SRWLOCK g_mediaLock = SRWLOCK_INIT; // serializes the exported media functions; the only writers of g_watch / g_generation
SRWLOCK g_watchLock = SRWLOCK_INIT; // guards the fields below; never held across a WinRT call
std::vector<std::wstring> g_pausedApps; // SourceAppUserModelIds of the sessions wpn_media_pause_all paused
MediaWatch* g_watch;                    // raw pointer: no static destructor releasing COM under the loader lock
uint32_t g_generation;                  // handlers of an older watch see a different value and do nothing
int g_fewestPlaying;                    // fewest Playing sessions seen since the last pause; more = someone played
bool g_dirty;                           // an event arrived since the evaluator last read the sessions
bool g_resubscribe;                     // SessionsChanged arrived: subscribe the new session list
bool g_evaluating;                      // an event thread runs evaluate_watch

HRESULT ensure_mta()
{
    static HRESULT const hr = [] {
        CO_MTA_USAGE_COOKIE cookie;
        return CoIncrementMTAUsage(&cookie);
    }();
    return hr;
}

void on_received(BluetoothLEAdvertisementWatcher const&, BluetoothLEAdvertisementReceivedEventArgs const& args)
{
    for (auto const& manufacturerData : args.Advertisement().ManufacturerData()) {
        if (manufacturerData.CompanyId() != kAppleCompanyId) {
            continue;
        }
        auto const buffer = manufacturerData.Data();
        uint8_t const* const data = buffer.data();
        uint32_t const length = buffer.Length();
        if (length == 0 || data[0] != kProximityPairing) {
            continue;
        }
        uint64_t const address = args.BluetoothAddress();
        uint8_t const addressType = args.BluetoothAddressType() == BluetoothAddressType::Random ? 1 : 0;
        int16_t const rssi = args.RawSignalStrengthInDBm();
        AcquireSRWLockExclusive(&g_callbackLock);
        if (g_callback) {
            g_callback(g_context, address, addressType, rssi, data, length);
        }
        ReleaseSRWLockExclusive(&g_callbackLock);
    }
}

void set_callback(wpn_adv_cb callback, void* context)
{
    AcquireSRWLockExclusive(&g_callbackLock);
    g_callback = callback;
    g_context = context;
    ReleaseSRWLockExclusive(&g_callbackLock);
}

// BTHENUM\DEV_<address> devnode -> container ID shared by the device's audio endpoints.
bool find_container_id(uint64_t address, GUID& containerId)
{
    wchar_t prefix[32];
    int const prefixLength = swprintf_s(prefix, L"BTHENUM\\DEV_%012llX\\", address);
    // Present devnodes only: stale non-present ones from earlier pairings carry the same address.
    ULONG const flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
    std::vector<wchar_t> ids;
    CONFIGRET cr;
    do {
        ULONG size = 0;
        cr = CM_Get_Device_ID_List_SizeW(&size, L"BTHENUM", flags);
        if (cr != CR_SUCCESS) {
            break;
        }
        ids.resize(size);
        cr = CM_Get_Device_ID_ListW(L"BTHENUM", ids.data(), size, flags);
    } while (cr == CR_BUFFER_SMALL);
    if (cr != CR_SUCCESS) {
        winrt::throw_hresult(HRESULT_FROM_WIN32(CM_MapCrToWin32Err(cr, ERROR_GEN_FAILURE)));
    }
    for (wchar_t const* id = ids.data(); *id; id += wcslen(id) + 1) {
        if (_wcsnicmp(id, prefix, prefixLength) != 0) {
            continue;
        }
        DEVINST devInst;
        DEVPROPTYPE type;
        ULONG size = sizeof(containerId);
        return CM_Locate_DevNodeW(&devInst, const_cast<wchar_t*>(id), CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS &&
            CM_Get_DevNode_PropertyW(devInst, &DEVPKEY_Device_ContainerId, &type,
                reinterpret_cast<BYTE*>(&containerId), &size, 0) == CR_SUCCESS &&
            type == DEVPROP_TYPE_GUID;
    }
    return false;
}

// KS filter IDs (BTHENUM = A2DP, BTHHFENUM = Hands-Free) wired to the audio endpoints of the device.
std::vector<std::wstring> find_bt_audio_filters(IMMDeviceEnumerator* enumerator, uint64_t address)
{
    std::vector<std::wstring> filters;
    GUID containerId;
    if (!find_container_id(address, containerId)) {
        return filters;
    }
    winrt::com_ptr<IMMDeviceCollection> endpoints;
    winrt::check_hresult(enumerator->EnumAudioEndpoints(eAll, DEVICE_STATE_ACTIVE | DEVICE_STATE_UNPLUGGED, endpoints.put()));
    UINT count = 0;
    winrt::check_hresult(endpoints->GetCount(&count));
    for (UINT i = 0; i < count; ++i) {
        winrt::com_ptr<IMMDevice> endpoint;
        winrt::com_ptr<IPropertyStore> properties;
        if (FAILED(endpoints->Item(i, endpoint.put())) || FAILED(endpoint->OpenPropertyStore(STGM_READ, properties.put()))) {
            continue;
        }
        PROPVARIANT value;
        PropVariantInit(&value);
        bool const sameDevice = SUCCEEDED(properties->GetValue(PKEY_Device_ContainerId, &value)) &&
            value.vt == VT_CLSID && *value.puuid == containerId;
        PropVariantClear(&value);
        if (!sameDevice) {
            continue;
        }
        winrt::com_ptr<IDeviceTopology> topology;
        winrt::com_ptr<IConnector> connector;
        wchar_t* filterId = nullptr;
        if (FAILED(endpoint->Activate(__uuidof(IDeviceTopology), CLSCTX_ALL, nullptr, topology.put_void())) ||
            FAILED(topology->GetConnector(0, connector.put())) ||
            FAILED(connector->GetDeviceIdConnectedTo(&filterId))) {
            continue;
        }
        std::wstring filter = filterId;
        CoTaskMemFree(filterId);
        std::wstring lower = filter;
        _wcslwr_s(lower.data(), lower.size() + 1);
        bool const btFilter = lower.find(L"\\\\?\\bthenum") != std::wstring::npos ||
            lower.find(L"\\\\?\\bthhfenum") != std::wstring::npos;
        if (btFilter && std::find(filters.begin(), filters.end(), filter) == filters.end()) {
            filters.push_back(std::move(filter));
        }
    }
    return filters;
}

int32_t send_oneshot(uint64_t address, ULONG propertyId) noexcept
{
    try {
        winrt::check_hresult(ensure_mta());
        auto const enumerator = winrt::create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
        auto const filters = find_bt_audio_filters(enumerator.get(), address);
        if (filters.empty()) {
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }
        HRESULT result = S_OK;
        bool issued = false;
        for (auto const& filter : filters) {
            winrt::com_ptr<IMMDevice> device;
            winrt::com_ptr<IKsControl> control;
            HRESULT hr = enumerator->GetDevice(filter.c_str(), device.put());
            if (SUCCEEDED(hr)) {
                hr = device->Activate(__uuidof(IKsControl), CLSCTX_ALL, nullptr, control.put_void());
            }
            if (SUCCEEDED(hr)) {
                KSPROPERTY property{};
                property.Set = KSPROPSETID_BtAudio;
                property.Id = propertyId;
                property.Flags = KSPROPERTY_TYPE_GET;
                ULONG returned = 0;
                hr = control->KsProperty(&property, sizeof(property), nullptr, 0, &returned);
            }
            if (SUCCEEDED(hr)) {
                issued = true;
            } else {
                result = hr;
            }
        }
        return issued ? S_OK : result;
    } catch (...) {
        return winrt::to_hresult();
    }
}

// Waits for a WinRT async operation for at most kMediaTimeout; throws on timeout or failure.
template <typename Operation>
auto wait_result(Operation const& operation)
{
    if (operation.wait_for(kMediaTimeout) == winrt::Windows::Foundation::AsyncStatus::Started) {
        operation.Cancel();
        winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
    }
    return operation.GetResults();
}

GlobalSystemMediaTransportControlsSessionManager media_manager()
{
    winrt::check_hresult(ensure_mta());
    return wait_result(GlobalSystemMediaTransportControlsSessionManager::RequestAsync());
}

bool is_playing(GlobalSystemMediaTransportControlsSession const& session)
{
    return session.GetPlaybackInfo().PlaybackStatus() == PlaybackStatus::Playing;
}

// Sessions that cannot be read (closing) count as not playing.
int count_playing(winrt::Windows::Foundation::Collections::IVectorView<GlobalSystemMediaTransportControlsSession> const& sessions)
{
    int playing = 0;
    for (auto const& session : sessions) {
        try {
            playing += is_playing(session) ? 1 : 0;
        } catch (...) {
            // Not counted.
        }
    }
    return playing;
}

void revoke(std::vector<Subscription>& subscriptions) noexcept
{
    for (auto& subscription : subscriptions) {
        try {
            subscription.session.PlaybackInfoChanged(subscription.token);
        } catch (...) {
            // Session already gone.
        }
    }
    subscriptions.clear();
}

void release_watch(MediaWatch* watch) noexcept
{
    if (!watch) {
        return;
    }
    try {
        watch->manager.SessionsChanged(watch->sessionsChanged);
    } catch (...) {
        // Manager already gone.
    }
    revoke(watch->sessions);
    delete watch;
}

// Under g_watchLock: forgets the remembered sessions and detaches the watch (release it after unlocking).
MediaWatch* take_watch()
{
    MediaWatch* const watch = g_watch;
    g_watch = nullptr;
    g_pausedApps.clear();
    ++g_generation;
    g_dirty = false;
    g_resubscribe = false;
    return watch;
}

void on_media_event(uint32_t generation, bool sessionsChanged) noexcept;

std::vector<Subscription> subscribe_sessions(
    winrt::Windows::Foundation::Collections::IVectorView<GlobalSystemMediaTransportControlsSession> const& sessions,
    uint32_t generation)
{
    std::vector<Subscription> subscriptions;
    for (auto const& session : sessions) {
        try {
            subscriptions.push_back({ session, session.PlaybackInfoChanged([generation](auto const&, auto const&) {
                on_media_event(generation, false);
            }) });
        } catch (...) {
            // Closing session: not watched, as in count_playing.
        }
    }
    return subscriptions;
}

// One event thread at a time: re-reads the sessions after every event (a later event repeats the loop, so reads are
// in order). A rise of the Playing count above the fewest seen since the pause means the user played a remembered
// session or another app started playback: the remembered sessions are forgotten (the watch stays until the next
// exported call replaces or releases it).
void evaluate_watch() noexcept
{
    while (true) {
        AcquireSRWLockExclusive(&g_watchLock);
        if (!g_watch || !g_dirty || g_pausedApps.empty()) {
            g_evaluating = false;
            ReleaseSRWLockExclusive(&g_watchLock);
            return;
        }
        g_dirty = false;
        bool const resubscribe = std::exchange(g_resubscribe, false);
        uint32_t const generation = g_generation;
        auto const manager = g_watch->manager;
        ReleaseSRWLockExclusive(&g_watchLock);

        std::vector<Subscription> subscriptions;
        bool subscribed = false;
        int playing = -1;
        try {
            auto const sessions = manager.GetSessions();
            if (resubscribe) {
                subscriptions = subscribe_sessions(sessions, generation);
                subscribed = true;
            }
            playing = count_playing(sessions);
        } catch (...) {
            // Unreadable now; the next event reads again.
        }

        AcquireSRWLockExclusive(&g_watchLock);
        if (g_watch && g_generation == generation) {
            if (subscribed) {
                std::swap(g_watch->sessions, subscriptions); // subscriptions now holds the replaced ones
            } else if (resubscribe) {
                g_resubscribe = true; // GetSessions failed: the next event subscribes again
            }
            if (playing > g_fewestPlaying) {
                g_pausedApps.clear();
            } else if (playing >= 0) {
                g_fewestPlaying = playing;
            }
        }
        ReleaseSRWLockExclusive(&g_watchLock);
        revoke(subscriptions);
    }
}

void on_media_event(uint32_t generation, bool sessionsChanged) noexcept
{
    AcquireSRWLockExclusive(&g_watchLock);
    bool const current = g_watch && g_generation == generation && !g_pausedApps.empty();
    if (current) {
        g_dirty = true;
        g_resubscribe |= sessionsChanged;
    }
    bool const start = current && !g_evaluating;
    if (start) {
        g_evaluating = true;
    }
    ReleaseSRWLockExclusive(&g_watchLock);
    if (start) {
        evaluate_watch();
    }
}

} // namespace

int32_t __stdcall wpn_ble_start(wpn_adv_cb callback, void* ctx)
{
    AcquireSRWLockExclusive(&g_stateLock);
    HRESULT hr = S_FALSE;
    if (!g_watcher) {
        try {
            winrt::check_hresult(ensure_mta());
            BluetoothLEAdvertisementWatcher watcher;
            watcher.ScanningMode(BluetoothLEScanningMode::Passive);
            winrt::Windows::Storage::Streams::Buffer prefix(1);
            prefix.Length(1);
            prefix.data()[0] = kProximityPairing;
            BluetoothLEManufacturerData filter;
            filter.CompanyId(kAppleCompanyId);
            filter.Data(prefix);
            watcher.AdvertisementFilter().Advertisement().ManufacturerData().Append(filter);
            set_callback(callback, ctx);
            g_receivedToken = watcher.Received(&on_received);
            watcher.Start();
            g_watcher = winrt::detach_abi(watcher);
            hr = S_OK;
        } catch (...) {
            set_callback(nullptr, nullptr);
            hr = winrt::to_hresult();
        }
    }
    ReleaseSRWLockExclusive(&g_stateLock);
    return hr;
}

void __stdcall wpn_ble_stop(void)
{
    AcquireSRWLockExclusive(&g_stateLock);
    if (g_watcher) {
        BluetoothLEAdvertisementWatcher watcher{ nullptr };
        winrt::attach_abi(watcher, g_watcher);
        g_watcher = nullptr;
        try {
            watcher.Received(g_receivedToken);
            watcher.Stop();
        } catch (...) {
            // Stop on an aborted watcher (radio off) may fail; the callback is cleared below regardless.
        }
        // Waits for an in-flight callback; any later event sees no callback.
        set_callback(nullptr, nullptr);
    }
    ReleaseSRWLockExclusive(&g_stateLock);
}

int32_t __stdcall wpn_bt_connect_audio(uint64_t btAddress)
{
    return send_oneshot(btAddress, KSPROPERTY_ONESHOT_RECONNECT);
}

int32_t __stdcall wpn_bt_disconnect_audio(uint64_t btAddress)
{
    return send_oneshot(btAddress, KSPROPERTY_ONESHOT_DISCONNECT);
}

int32_t __stdcall wpn_media_pause_all(void)
{
    AcquireSRWLockExclusive(&g_mediaLock);
    int32_t result;
    MediaWatch* replaced = nullptr;
    try {
        auto const manager = media_manager();
        std::vector<std::wstring> paused;
        HRESULT failure = S_OK;
        for (auto const& session : manager.GetSessions()) {
            try {
                if (!is_playing(session)) {
                    continue;
                }
                if (wait_result(session.TryPauseAsync())) {
                    paused.emplace_back(session.SourceAppUserModelId());
                } else {
                    failure = E_FAIL;
                }
            } catch (...) {
                failure = winrt::to_hresult();
            }
        }
        if (!paused.empty()) {
            // A fresh watch with a fresh baseline; its handlers stay inert until g_generation reaches theirs.
            AcquireSRWLockShared(&g_watchLock);
            uint32_t const generation = g_generation + 1;
            ReleaseSRWLockShared(&g_watchLock);
            auto* watch = new MediaWatch{ manager, {}, {} };
            int playing = 0;
            try {
                watch->sessionsChanged = manager.SessionsChanged([generation](auto const&, auto const&) {
                    on_media_event(generation, true);
                });
                auto const sessions = manager.GetSessions();
                watch->sessions = subscribe_sessions(sessions, generation);
                playing = count_playing(sessions);
            } catch (...) {
                // No watch: the paused sessions are still remembered for resume, only never forgotten early.
                release_watch(watch);
                watch = nullptr;
            }
            AcquireSRWLockExclusive(&g_watchLock);
            replaced = g_watch;
            g_watch = watch;
            g_generation = generation;
            g_pausedApps.insert(g_pausedApps.end(), paused.begin(), paused.end());
            g_fewestPlaying = playing;
            g_dirty = false;
            g_resubscribe = false;
            ReleaseSRWLockExclusive(&g_watchLock);
            if (watch) {
                // Replays an event dropped before the commit (e.g. a paused session leaving Playing after the
                // baseline read). After unlocking: on_media_event takes g_watchLock. g_mediaLock stays held, so the
                // watch cannot change before it runs.
                on_media_event(generation, false);
            }
        }
        result = paused.empty() && FAILED(failure) ? failure : static_cast<int32_t>(paused.size());
    } catch (...) {
        result = winrt::to_hresult();
    }
    release_watch(replaced);
    ReleaseSRWLockExclusive(&g_mediaLock);
    return result;
}

int32_t __stdcall wpn_media_resume_paused(void)
{
    AcquireSRWLockExclusive(&g_mediaLock);
    AcquireSRWLockExclusive(&g_watchLock);
    std::vector<std::wstring> apps = std::move(g_pausedApps);
    MediaWatch* const watch = take_watch();
    ReleaseSRWLockExclusive(&g_watchLock);
    release_watch(watch);
    int32_t result = 0;
    try {
        if (!apps.empty()) {
            auto const sessions = media_manager().GetSessions();
            if (count_playing(sessions) == 0) {
                HRESULT failure = S_OK;
                for (auto const& session : sessions) {
                    try {
                        if (session.GetPlaybackInfo().PlaybackStatus() != PlaybackStatus::Paused) {
                            continue;
                        }
                        // Each remembered entry resumes at most one session of that app.
                        auto const app = std::find(apps.begin(), apps.end(), std::wstring_view{ session.SourceAppUserModelId() });
                        if (app == apps.end()) {
                            continue;
                        }
                        apps.erase(app);
                        if (wait_result(session.TryPlayAsync())) {
                            ++result;
                        } else {
                            failure = E_FAIL;
                        }
                    } catch (...) {
                        failure = winrt::to_hresult();
                    }
                }
                if (result == 0 && FAILED(failure)) {
                    result = failure;
                }
            }
        }
    } catch (...) {
        result = winrt::to_hresult();
    }
    ReleaseSRWLockExclusive(&g_mediaLock);
    return result;
}

void __stdcall wpn_media_forget(void)
{
    AcquireSRWLockExclusive(&g_mediaLock);
    AcquireSRWLockExclusive(&g_watchLock);
    MediaWatch* const watch = take_watch();
    ReleaseSRWLockExclusive(&g_watchLock);
    release_watch(watch);
    ReleaseSRWLockExclusive(&g_mediaLock);
}

int32_t __stdcall wpn_audio_is_default_output(uint64_t btAddress)
{
    try {
        winrt::check_hresult(ensure_mta());
        auto const enumerator = winrt::create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
        winrt::com_ptr<IMMDevice> endpoint;
        HRESULT const hr = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, endpoint.put());
        if (hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) {
            return 0;
        }
        winrt::check_hresult(hr);
        winrt::com_ptr<IPropertyStore> properties;
        winrt::check_hresult(endpoint->OpenPropertyStore(STGM_READ, properties.put()));
        PROPVARIANT value;
        PropVariantInit(&value);
        winrt::check_hresult(properties->GetValue(PKEY_Device_ContainerId, &value));
        bool const hasContainer = value.vt == VT_CLSID;
        GUID const endpointContainer = hasContainer ? *value.puuid : GUID{};
        PropVariantClear(&value);
        GUID deviceContainer;
        return hasContainer && find_container_id(btAddress, deviceContainer) && endpointContainer == deviceContainer ? 1 : 0;
    } catch (...) {
        return winrt::to_hresult();
    }
}

uint32_t __stdcall wpn_fp_set_ftz_daz(void)
{
    unsigned int const previous = _mm_getcsr();
    _mm_setcsr(previous | 0x8040);
    return previous;
}

void __stdcall wpn_fp_restore(uint32_t mxcsr)
{
    _mm_setcsr(mxcsr);
}
