// Mixer: plays picked endpoints (loopback for render endpoints) live in the attached pair's A2DP endpoint, or its
// Hands-Free one while Hands-Free is active, on one real-time engine thread that exists only while there is something
// to play. g_lock is never held across COM, a wait or the event callback.
// Also a loopback capture of what Windows plays to the pair, as mono 48 kHz, pulled by the caller's own thread.
#include <windows.h>
#include <cfgmgr32.h>
#include <mmreg.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <audiopolicy.h>
#include <avrt.h>
#include <ks.h>
#include <ksmedia.h>
#include <devicetopology.h>
#include <initguid.h>
#include <devpkey.h>
#include <functiondiscoverykeys_devpkey.h>

#include <algorithm>
#include <atomic>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <string>
#include <utility>
#include <vector>

#include <winrt/base.h>

#include "internal.h"
#include "wpods_native.h"

namespace {

constexpr int32_t kDevicesChanged = 0;
constexpr int32_t kSourceGone = 1;
constexpr int32_t kLogLine = 2;

constexpr double kMarginSeconds = 0.002; // FIFO low point kept this far above empty (unseen scheduling jitter)
constexpr double kWindowSeconds = 0.25;  // low point observation window = control loop update interval
constexpr double kKp = 0.2;              // 1/s: a fill error decays with a ~5 s time constant
constexpr double kKi = kKp * kKp / 4;    // 1/s^2: critically damped drift integrator
constexpr double kMaxAdjust = 0.005;     // ratio stays within +-0.5% of nominal (< 9 cents)
constexpr double kRampSeconds = 0.01;    // a full-scale gain change takes 10 ms
constexpr float kClipKnee = 0.891f;      // -1 dBFS: the limiter is transparent below it
constexpr size_t kMaxSources = MAXIMUM_WAIT_OBJECTS - 2; // wait handles left after the wake and render events

struct SourceEntry {
    SourceEntry(wchar_t const* sourceId, float sourceGain) : id(sourceId), gain(sourceGain) {}
    std::wstring const id;
    std::atomic<float> gain;
};

class ExclusiveLock {
public:
    explicit ExclusiveLock(SRWLOCK& lock) : m_lock(lock) { AcquireSRWLockExclusive(&m_lock); }
    ~ExclusiveLock() { ReleaseSRWLockExclusive(&m_lock); }
    ExclusiveLock(ExclusiveLock const&) = delete;
    ExclusiveLock& operator=(ExclusiveLock const&) = delete;

private:
    SRWLOCK& m_lock;
};

SRWLOCK g_apiLock = SRWLOCK_INIT;      // serializes wpn_mixer_init / wpn_mixer_shutdown
SRWLOCK g_callbackLock = SRWLOCK_INIT; // shared while the event callback runs, exclusive to change it
wpn_mixer_event_cb g_callback;
void* g_context;
IMMDeviceEnumerator* g_notifier; // holds the registered notification client; raw: no static destructor releasing COM

SRWLOCK g_lock = SRWLOCK_INIT; // guards the fields below; never held across COM, a wait or the event callback
bool g_initialized;
uint64_t g_target;
std::vector<std::shared_ptr<SourceEntry>> g_sources;
bool g_reconfigure; // target, sources or endpoints changed since the engine last looked
HANDLE g_wake;      // auto-reset: wakes the engine thread; lives from init to shutdown
HANDLE g_thread;    // newest engine thread
bool g_running;     // the engine thread reads g_reconfigure again before it exits

DWORD WINAPI engine_main(void* previous);

// Under g_lock: flags a change for the engine and wakes it, or starts it when there may be something to play.
void kick_locked()
{
    g_reconfigure = true;
    if (!g_initialized) {
        return;
    }
    if (g_running) {
        SetEvent(g_wake);
        return;
    }
    if (g_target == 0 || g_sources.empty()) {
        return;
    }
    // The new thread waits for and closes the previous handle (that thread is exiting or gone).
    if (HANDLE const thread = CreateThread(nullptr, 0, engine_main, g_thread, 0, nullptr)) {
        g_thread = thread;
        g_running = true;
    }
}

auto find_source_locked(wchar_t const* id)
{
    return std::find_if(g_sources.begin(), g_sources.end(), [id](auto const& entry) { return entry->id == id; });
}

void set_callback(wpn_mixer_event_cb callback, void* context)
{
    AcquireSRWLockExclusive(&g_callbackLock);
    g_callback = callback;
    g_context = context;
    ReleaseSRWLockExclusive(&g_callbackLock);
}

// Never called with g_lock held.
void notify(int32_t kind, wchar_t const* id)
{
    AcquireSRWLockShared(&g_callbackLock);
    if (g_callback) {
        g_callback(g_context, kind, id);
    }
    ReleaseSRWLockShared(&g_callbackLock);
}

// The direct source's jitter buffer: created once by the engine thread and never freed (so wpn_mixer_push_mic needs
// no lifetime lock), fed while g_micDirect is set (a direct source is open).
std::atomic<void*> g_micJitter;
std::atomic<bool> g_micDirect;

void __stdcall jitter_log(void*, wchar_t const* line)
{
    notify(kLogLine, line);
}

void* mic_jitter()
{
    void* jitter = g_micJitter.load();
    if (!jitter) {
        jitter = wpn_jitter_create(jitter_log, nullptr);
        if (!jitter) {
            winrt::throw_hresult(E_OUTOFMEMORY);
        }
        g_micJitter.store(jitter);
    }
    return jitter;
}

// Static instance, no reference counting. Callbacks only flag work for the engine thread and never block.
class NotificationClient final : public IMMNotificationClient {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override
    {
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IMMNotificationClient)) {
            *object = static_cast<IMMNotificationClient*>(this);
            return S_OK;
        }
        *object = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }

    HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(LPCWSTR, DWORD) override { return changed(); }
    HRESULT STDMETHODCALLTYPE OnDeviceAdded(LPCWSTR) override { return changed(); }
    HRESULT STDMETHODCALLTYPE OnDeviceRemoved(LPCWSTR id) override
    {
        bool removed = false;
        {
            ExclusiveLock lock(g_lock);
            auto const entry = find_source_locked(id);
            if (entry != g_sources.end()) {
                g_sources.erase(entry);
                removed = true;
            }
            kick_locked();
        }
        if (removed) {
            notify(kSourceGone, id);
        }
        notify(kDevicesChanged, nullptr);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(EDataFlow, ERole, LPCWSTR) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(LPCWSTR, PROPERTYKEY const key) override
    {
        if (key.fmtid == PKEY_Device_FriendlyName.fmtid && key.pid == PKEY_Device_FriendlyName.pid) {
            notify(kDevicesChanged, nullptr);
        }
        return S_OK;
    }

private:
    static HRESULT changed()
    {
        {
            ExclusiveLock lock(g_lock);
            kick_locked();
        }
        notify(kDevicesChanged, nullptr);
        return S_OK;
    }
};

NotificationClient g_client;

struct CoTaskMemFreer {
    void operator()(void* memory) const noexcept { CoTaskMemFree(memory); }
};
using MixFormat = std::unique_ptr<WAVEFORMATEX, CoTaskMemFreer>;

struct Format {
    uint32_t channels = 0;
    uint32_t rate = 0;
    uint32_t mask = 0; // SPEAKER_* positions, 0 = unknown
    uint32_t bits = 0;
    bool isFloat = false;
};

// Float32, or 16/32-bit integer PCM (24 bits in a 32-bit container reads as 32); false for anything else.
bool parse_format(WAVEFORMATEX const* format, Format& out)
{
    bool isFloat = format->wFormatTag == WAVE_FORMAT_IEEE_FLOAT;
    bool isPcm = format->wFormatTag == WAVE_FORMAT_PCM;
    out.mask = 0;
    if (format->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
        size_t{ format->cbSize } >= sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)) {
        auto const* const extensible = reinterpret_cast<WAVEFORMATEXTENSIBLE const*>(format);
        isFloat = extensible->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        isPcm = extensible->SubFormat == KSDATAFORMAT_SUBTYPE_PCM;
        out.mask = extensible->dwChannelMask;
    }
    out.channels = format->nChannels;
    out.rate = format->nSamplesPerSec;
    out.bits = format->wBitsPerSample;
    out.isFloat = isFloat;
    return out.channels > 0 && out.rate > 0 && uint32_t{ format->nBlockAlign } == out.channels * out.bits / 8 &&
        (isFloat ? out.bits == 32 : isPcm && (out.bits == 16 || out.bits == 32));
}

float read_sample(BYTE const* data, size_t index, Format const& format)
{
    if (format.isFloat) {
        return reinterpret_cast<float const*>(data)[index];
    }
    if (format.bits == 16) {
        return reinterpret_cast<int16_t const*>(data)[index] * (1.0f / 32768);
    }
    return static_cast<float>(reinterpret_cast<int32_t const*>(data)[index] * (1.0 / 2147483648.0));
}

// Weights (outChannels rows x format.channels columns): mono feeds the front pair; anything else folds by speaker
// position (front 1, center and surround 0.707, LFE dropped).
std::vector<float> mix_matrix(Format const& format, uint32_t outChannels)
{
    uint32_t const in = format.channels;
    std::vector<float> matrix(size_t{ outChannels } * in, 0.0f);
    if (in == outChannels) {
        for (uint32_t c = 0; c < in; ++c) {
            matrix[size_t{ c } * in + c] = 1;
        }
        return matrix;
    }
    if (in == 1) {
        for (uint32_t o = 0; o < std::min(outChannels, 2u); ++o) {
            matrix[o] = 1;
        }
        return matrix;
    }
    constexpr uint32_t frontLeft = SPEAKER_FRONT_LEFT | SPEAKER_FRONT_LEFT_OF_CENTER;
    constexpr uint32_t frontRight = SPEAKER_FRONT_RIGHT | SPEAKER_FRONT_RIGHT_OF_CENTER;
    constexpr uint32_t otherLeft = SPEAKER_BACK_LEFT | SPEAKER_SIDE_LEFT | SPEAKER_TOP_FRONT_LEFT | SPEAKER_TOP_BACK_LEFT;
    constexpr uint32_t otherRight =
        SPEAKER_BACK_RIGHT | SPEAKER_SIDE_RIGHT | SPEAKER_TOP_FRONT_RIGHT | SPEAKER_TOP_BACK_RIGHT;
    constexpr uint32_t center = SPEAKER_FRONT_CENTER | SPEAKER_BACK_CENTER | SPEAKER_TOP_CENTER |
        SPEAKER_TOP_FRONT_CENTER | SPEAKER_TOP_BACK_CENTER;
    constexpr float side = 0.7071f;
    std::vector<float> left(in, 0.0f);
    std::vector<float> right(in, 0.0f);
    uint32_t mask = format.mask;
    for (uint32_t c = 0; c < in; ++c) {
        uint32_t const speaker = mask & (~mask + 1); // lowest remaining position; 0 = unknown
        mask &= mask - 1;
        if (speaker == 0) {
            (c % 2 ? right : left)[c] = c < 2 ? 1 : side;
        } else if (speaker & frontLeft) {
            left[c] = 1;
        } else if (speaker & frontRight) {
            right[c] = 1;
        } else if (speaker & otherLeft) {
            left[c] = side;
        } else if (speaker & otherRight) {
            right[c] = side;
        } else if (speaker & center) {
            left[c] = side;
            right[c] = side;
        }
    }
    for (uint32_t c = 0; c < in; ++c) {
        if (outChannels == 1) {
            matrix[c] = 0.5f * (left[c] + right[c]);
        } else {
            matrix[c] = left[c];
            matrix[size_t{ in } + c] = right[c];
        }
    }
    return matrix;
}

float soft_clip(float x)
{
    float const magnitude = std::fabs(x);
    if (magnitude <= kClipKnee) {
        return x;
    }
    float const y = kClipKnee + (1 - kClipKnee) * std::tanh((magnitude - kClipKnee) / (1 - kClipKnee));
    return std::copysign(y, x);
}

winrt::handle make_event()
{
    winrt::handle event{ CreateEventW(nullptr, FALSE, FALSE, nullptr) };
    if (!event) {
        winrt::throw_last_error();
    }
    return event;
}

// A fresh client per attempt: one whose Initialize failed may not take another.
winrt::com_ptr<IAudioClient> activate(IMMDevice* device)
{
    winrt::com_ptr<IAudioClient> client;
    winrt::check_hresult(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, client.put_void()));
    return client;
}

MixFormat mix_format(IAudioClient* client)
{
    WAVEFORMATEX* format = nullptr;
    winrt::check_hresult(client->GetMixFormat(&format));
    return MixFormat(format);
}

// IAudioClient3 shared stream at the engine's minimum period for this format; returns that period in frames, or 0
// when IAudioClient3 is missing or refuses (e.g. another stream holds the engine at a different period).
uint32_t init_low_latency(winrt::com_ptr<IAudioClient> const& client, DWORD flags, WAVEFORMATEX const* format)
{
    auto const client3 = client.try_as<IAudioClient3>();
    UINT32 defaultPeriod = 0;
    UINT32 fundamentalPeriod = 0;
    UINT32 minPeriod = 0;
    UINT32 maxPeriod = 0;
    if (!client3 ||
        FAILED(client3->GetSharedModeEnginePeriod(format, &defaultPeriod, &fundamentalPeriod, &minPeriod, &maxPeriod)) ||
        FAILED(client3->InitializeSharedAudioStream(flags, minPeriod, format, nullptr))) {
        return 0;
    }
    return minPeriod;
}

// Legacy shared stream with the smallest buffer the engine allows; period = the engine period in this format's frames.
HRESULT init_legacy(IAudioClient* client, DWORD flags, WAVEFORMATEX const* format, uint32_t& period)
{
    HRESULT hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, format, nullptr);
    REFERENCE_TIME devicePeriod = 0;
    if (SUCCEEDED(hr)) {
        hr = client->GetDevicePeriod(&devicePeriod, nullptr);
    }
    period = static_cast<uint32_t>((devicePeriod * format->nSamplesPerSec + 5'000'000) / 10'000'000);
    return hr;
}

bool container_of(IPropertyStore* properties, GUID& containerId)
{
    PROPVARIANT value;
    PropVariantInit(&value);
    bool const found = SUCCEEDED(properties->GetValue(PKEY_Device_ContainerId, &value)) && value.vt == VT_CLSID;
    if (found) {
        containerId = *value.puuid;
    }
    PropVariantClear(&value);
    return found;
}

// The paired device's ACTIVE endpoint of this flow whose KS filter is Hands-Free (BTHHFENUM) or A2DP (BTHENUM), or
// null.
winrt::com_ptr<IMMDevice> find_pair_endpoint(IMMDeviceEnumerator* enumerator, uint64_t address, EDataFlow flow, bool handsFree)
{
    GUID containerId;
    if (!find_container_id(address, containerId)) {
        return nullptr;
    }
    winrt::com_ptr<IMMDeviceCollection> endpoints;
    winrt::check_hresult(enumerator->EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, endpoints.put()));
    UINT count = 0;
    winrt::check_hresult(endpoints->GetCount(&count));
    for (UINT i = 0; i < count; ++i) {
        winrt::com_ptr<IMMDevice> endpoint;
        winrt::com_ptr<IPropertyStore> properties;
        GUID endpointContainer;
        if (FAILED(endpoints->Item(i, endpoint.put())) || FAILED(endpoint->OpenPropertyStore(STGM_READ, properties.put())) ||
            !container_of(properties.get(), endpointContainer) || endpointContainer != containerId) {
            continue;
        }
        winrt::com_ptr<IDeviceTopology> topology;
        winrt::com_ptr<IConnector> connector;
        wchar_t* filterId = nullptr;
        if (FAILED(endpoint->Activate(__uuidof(IDeviceTopology), CLSCTX_ALL, nullptr, topology.put_void())) ||
            FAILED(topology->GetConnector(0, connector.put())) || FAILED(connector->GetDeviceIdConnectedTo(&filterId))) {
            continue;
        }
        std::wstring filter = filterId;
        CoTaskMemFree(filterId);
        _wcslwr_s(filter.data(), filter.size() + 1);
        // "bthenum" is no substring of "bthhfenum".
        if (filter.find(handsFree ? L"\\\\?\\bthhfenum" : L"\\\\?\\bthenum") != std::wstring::npos) {
            return endpoint;
        }
    }
    return nullptr;
}

constexpr wchar_t kOwnMicHardwareId[] = L"USB\\VID_1209&PID_0001";
// DEVPKEY_Device_LastKnownParent (DEVPROP_TYPE_STRING), missing from the SDK's devpkey.h.
constexpr DEVPROPKEY kLastKnownParent{ { 0x83da6326, 0x97a6, 0x4088, { 0x94, 0x53, 0xa1, 0x92, 0x3f, 0x57, 0x3b, 0x29 } }, 10 };

bool string_property(DEVINST node, DEVPROPKEY const& key, wchar_t (&buffer)[MAX_DEVICE_ID_LEN])
{
    DEVPROPTYPE type;
    ULONG size = sizeof(buffer);
    return CM_Get_DevNode_PropertyW(node, &key, &type, reinterpret_cast<BYTE*>(buffer), &size, 0) == CR_SUCCESS &&
        type == DEVPROP_TYPE_STRING;
}

bool has_hardware_id(DEVINST node, wchar_t const* hardwareId)
{
    DEVPROPTYPE type;
    ULONG size = 0;
    if (CM_Get_DevNode_PropertyW(node, &DEVPKEY_Device_HardwareIds, &type, nullptr, &size, 0) != CR_BUFFER_SMALL) {
        return false;
    }
    std::vector<wchar_t> ids(size / sizeof(wchar_t) + 2, L'\0'); // zero tail: always double-terminated
    if (CM_Get_DevNode_PropertyW(node, &DEVPKEY_Device_HardwareIds, &type, reinterpret_cast<BYTE*>(ids.data()), &size, 0) !=
            CR_SUCCESS ||
        type != DEVPROP_TYPE_STRING_LIST) {
        return false;
    }
    for (wchar_t const* id = ids.data(); *id; id += wcslen(id) + 1) {
        if (_wcsicmp(id, hardwareId) == 0) {
            return true;
        }
    }
    return false;
}

// True when the endpoint's devnode (SWD\MMDEVAPI\<endpoint ID>) has the WPods virtual mic's devnode among its
// ancestors. A non-present devnode has no Parent, only LastKnownParent.
bool is_own_mic(wchar_t const* endpointId)
{
    std::wstring instance = std::wstring(L"SWD\\MMDEVAPI\\") + endpointId;
    DEVINST node;
    if (CM_Locate_DevNodeW(&node, instance.data(), CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) {
        return false;
    }
    while (true) {
        if (has_hardware_id(node, kOwnMicHardwareId)) {
            return true;
        }
        wchar_t parent[MAX_DEVICE_ID_LEN];
        if ((!string_property(node, DEVPKEY_Device_Parent, parent) && !string_property(node, kLastKnownParent, parent)) ||
            CM_Locate_DevNodeW(&node, parent, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) {
            return false;
        }
    }
}

std::wstring endpoint_id(IMMDevice* device)
{
    wchar_t* raw = nullptr;
    winrt::check_hresult(device->GetId(&raw));
    std::wstring id = raw;
    CoTaskMemFree(raw);
    return id;
}

// Shared by the watch and its COM sinks, which may outlive it by a callback in flight.
struct WatchState {
    winrt::handle wake; // a duplicate of g_wake, so a late callback never signals a closed handle
    std::atomic<int> active{ 0 };
    std::atomic<bool> rescan{ false }; // a session was created: the engine registers for it
};

// One watched session. Its state starts unknown; the first state read by the engine counts only if no event came
// first, and once closed (unregistered) later events are ignored, so the count cannot latch.
class SessionEvents : public winrt::implements<SessionEvents, IAudioSessionEvents> {
public:
    explicit SessionEvents(std::shared_ptr<WatchState> state) : m_state(std::move(state)) {}

    void initialize(AudioSessionState state)
    {
        int expected = kUnknown;
        if (m_session.compare_exchange_strong(expected, state)) {
            count(kUnknown, state);
        }
    }
    void close() { count(m_session.exchange(kClosed), AudioSessionStateExpired); }

    HRESULT STDMETHODCALLTYPE OnDisplayNameChanged(LPCWSTR, LPCGUID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnIconPathChanged(LPCWSTR, LPCGUID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnSimpleVolumeChanged(float, BOOL, LPCGUID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnChannelVolumeChanged(DWORD, float[], DWORD, LPCGUID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnGroupingParamChanged(LPCGUID, LPCGUID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnStateChanged(AudioSessionState state) override
    {
        set(state);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnSessionDisconnected(AudioSessionDisconnectReason) override
    {
        set(AudioSessionStateExpired);
        return S_OK;
    }

private:
    static constexpr int kUnknown = -1;
    static constexpr int kClosed = -2;

    void set(int state)
    {
        int previous = m_session.load();
        do {
            if (previous == kClosed) {
                return;
            }
        } while (!m_session.compare_exchange_weak(previous, state));
        count(previous, state);
    }

    // Wakes the engine when the number of active sessions goes from 0 to 1 or from 1 to 0.
    void count(int previous, int state)
    {
        bool const was = previous == AudioSessionStateActive;
        bool const is = state == AudioSessionStateActive;
        if (was == is) {
            return;
        }
        int const now = is ? ++m_state->active : --m_state->active;
        if (now == (is ? 1 : 0)) {
            SetEvent(m_state->wake.get());
        }
    }

    std::shared_ptr<WatchState> const m_state;
    std::atomic<int> m_session{ kUnknown };
};

class SessionCreated : public winrt::implements<SessionCreated, IAudioSessionNotification> {
public:
    explicit SessionCreated(std::shared_ptr<WatchState> state) : m_state(std::move(state)) {}

    // Registering here is not allowed: the engine thread does it.
    HRESULT STDMETHODCALLTYPE OnSessionCreated(IAudioSessionControl*) override
    {
        m_state->rescan = true;
        SetEvent(m_state->wake.get());
        return S_OK;
    }

private:
    std::shared_ptr<WatchState> const m_state;
};

// Hands-Free active = at least one AudioSessionStateActive session on the pair's Hands-Free capture endpoint (any
// process, the mixer's own capture included; render sessions never count). Engine thread only.
class SessionWatch {
public:
    SessionWatch() = default;
    SessionWatch(SessionWatch const&) = delete;
    SessionWatch& operator=(SessionWatch const&) = delete;
    ~SessionWatch() { disarm(); }

    std::wstring const& id() const { return m_id; }
    bool active() const { return m_state && m_state->active > 0; }

    // Throws on failure (the caller disarms).
    void arm(IMMDevice* device, std::wstring id)
    {
        m_state = std::make_shared<WatchState>();
        HANDLE wake = nullptr;
        winrt::check_bool(DuplicateHandle(GetCurrentProcess(), g_wake, GetCurrentProcess(), &wake, 0, FALSE, DUPLICATE_SAME_ACCESS));
        m_state->wake.attach(wake);
        winrt::check_hresult(device->Activate(__uuidof(IAudioSessionManager2), CLSCTX_ALL, nullptr, m_manager.put_void()));
        m_created = winrt::make_self<SessionCreated>(m_state);
        winrt::check_hresult(m_manager->RegisterSessionNotification(m_created.get()));
        scan(); // the first GetSessionEnumerator also starts the session notifications
        m_id = std::move(id);
    }

    // Throws on failure (the caller disarms).
    void rescan_if_needed()
    {
        if (m_state && m_state->rescan.exchange(false)) {
            scan();
        }
    }

    void disarm() noexcept
    {
        if (m_manager && m_created) {
            m_manager->UnregisterSessionNotification(m_created.get());
        }
        for (auto const& session : m_sessions) {
            session.control->UnregisterAudioSessionNotification(session.events.get());
            session.events->close();
        }
        m_sessions.clear();
        m_created = nullptr;
        m_manager = nullptr;
        m_state.reset();
        m_id.clear();
    }

private:
    struct Watched {
        std::wstring instance;
        winrt::com_ptr<IAudioSessionControl> control;
        winrt::com_ptr<SessionEvents> events;
    };

    // Registers for sessions not yet watched; unregisters those that are gone.
    void scan()
    {
        winrt::com_ptr<IAudioSessionEnumerator> sessions;
        winrt::check_hresult(m_manager->GetSessionEnumerator(sessions.put()));
        int count = 0;
        winrt::check_hresult(sessions->GetCount(&count));
        std::vector<Watched> current;
        for (int i = 0; i < count; ++i) {
            winrt::com_ptr<IAudioSessionControl> control;
            wchar_t* raw = nullptr;
            if (FAILED(sessions->GetSession(i, control.put()))) {
                continue;
            }
            auto const control2 = control.try_as<IAudioSessionControl2>();
            if (!control2 || FAILED(control2->GetSessionInstanceIdentifier(&raw))) {
                continue;
            }
            std::wstring instance = raw;
            CoTaskMemFree(raw);
            auto const known = std::find_if(m_sessions.begin(), m_sessions.end(),
                [&instance](Watched const& session) { return session.instance == instance; });
            if (known != m_sessions.end()) {
                current.push_back(std::move(*known));
                m_sessions.erase(known);
                continue;
            }
            auto events = winrt::make_self<SessionEvents>(m_state);
            if (FAILED(control->RegisterAudioSessionNotification(events.get()))) {
                continue;
            }
            AudioSessionState state;
            if (SUCCEEDED(control->GetState(&state))) {
                events->initialize(state);
            }
            current.push_back({ std::move(instance), std::move(control), std::move(events) });
        }
        for (auto const& session : m_sessions) {
            session.control->UnregisterAudioSessionNotification(session.events.get());
            session.events->close();
        }
        m_sessions = std::move(current);
    }

    std::wstring m_id; // the watched endpoint; empty = none
    std::shared_ptr<WatchState> m_state;
    winrt::com_ptr<IAudioSessionManager2> m_manager;
    winrt::com_ptr<SessionCreated> m_created;
    std::vector<Watched> m_sessions;
};

struct Output {
    winrt::com_ptr<IAudioClient> client;
    winrt::com_ptr<IAudioRenderClient> render;
    winrt::handle event;
    std::vector<BYTE> format; // the stream's WAVEFORMATEX(+extension) = the endpoint mix format
    Format layout;
    uint32_t period = 0;   // engine period, frames
    uint32_t fill = 0;     // frames kept queued: one period
    bool rendered = false; // a pass succeeded since it opened
};

// One open source stream with its FIFO (output channel layout, source rate) and interpolator. Engine thread only.
class Source {
public:
    explicit Source(std::shared_ptr<SourceEntry> sourceEntry) : entry(std::move(sourceEntry)) {}
    Source(Source const&) = delete;
    Source& operator=(Source const&) = delete;
    ~Source()
    {
        if (m_jitter) {
            g_micDirect = false;
        }
    }

    std::shared_ptr<SourceEntry> const entry;
    winrt::com_ptr<IAudioClient> client;
    winrt::com_ptr<IAudioCaptureClient> capture;
    winrt::handle event;
    bool delivered = false; // a packet arrived since it opened

    // format: the capture stream's; capturePeriod: frames per capture packet; bufferFrames: the capture buffer.
    void configure(Format const& format, uint32_t capturePeriod, uint32_t bufferFrames, Output const& output)
    {
        m_format = format;
        m_capturePeriod = capturePeriod;
        m_bufferFrames = bufferFrames;
        retarget(output);
    }

    // The direct source: mono 48 kHz float from the jitter buffer (int16 scale, normalized on the way in).
    void configure_direct(void* jitter, Output const& output)
    {
        m_jitter = jitter;
        m_format = { 1, 48000, 0, 32, true };
        retarget(output);
    }

    bool pulls() const { return m_jitter != nullptr; }

    // (Re)builds everything that depends on the output (an output switch keeps the capture stream): channel map,
    // rate ratio, prime levels; the FIFO starts empty, primes again and fades in, and drift is learned anew.
    void retarget(Output const& output)
    {
        m_channels = output.layout.channels;
        m_direct = m_format.isFloat && m_format.channels == m_channels;
        m_matrix = m_direct ? std::vector<float>{} : mix_matrix(m_format, m_channels);
        m_rate = m_format.rate;
        m_nominal = m_rate / output.layout.rate;
        m_step = m_nominal;
        m_integral = 0;
        // Capture adds a packet at a time and render takes up to a period at a time, so the fill swings by about
        // both periods; play from margin above that and let the control loop find the real low point.
        double const burst = m_capturePeriod + output.period * m_nominal;
        m_margin = kMarginSeconds * m_rate;
        m_prime = static_cast<uint64_t>(std::ceil(burst + m_margin));
        m_overflow = m_prime + static_cast<uint64_t>(std::ceil(burst));
        m_windowLength = static_cast<uint32_t>(kWindowSeconds * output.layout.rate);
        m_rampStep = static_cast<float>(1 / (kRampSeconds * output.layout.rate));
        uint64_t const capacity = std::bit_ceil(uint64_t{ m_format.rate } + m_bufferFrames);
        m_mask = capacity - 1;
        m_ring.assign(capacity * m_channels, 0.0f);
        m_head = 0;
        m_tail = 0;
        m_pos = 1;
        m_frac = 0;
        m_playing = false;
        m_gain = 0;
        if (m_jitter) {
            // Filled on demand (pull), so it plays at once and never under- or overflows; no control loop, as the
            // jitter buffer absorbs arrival jitter and drift.
            m_playing = true;
            m_pulled.assign(static_cast<size_t>(std::ceil(output.fill * m_nominal)) + 8, 0.0f);
        }
        start_window();
    }

    // Moves every waiting capture packet into the FIFO.
    HRESULT drain()
    {
        if (m_jitter) {
            return S_OK;
        }
        while (true) {
            UINT32 packet = 0;
            HRESULT hr = capture->GetNextPacketSize(&packet);
            if (FAILED(hr) || packet == 0) {
                return hr;
            }
            BYTE* data = nullptr;
            UINT32 frames = 0;
            DWORD flags = 0;
            hr = capture->GetBuffer(&data, &frames, &flags, nullptr, nullptr);
            if (FAILED(hr)) {
                return hr;
            }
            push(data, frames, (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0);
            hr = capture->ReleaseBuffer(frames);
            if (FAILED(hr)) {
                return hr;
            }
            delivered = true;
        }
    }

    // Adds `frames` output frames of this source into out (scratch: frames x channels floats).
    void add(float* out, uint32_t frames, float* scratch)
    {
        if (m_jitter) {
            pull(frames);
        }
        uint32_t produced = 0;
        bool underrun = false;
        if (m_playing) {
            for (; produced < frames; ++produced) {
                // Written this way round: at 3+ source frames per output frame the position can pass the head.
                if (m_pos + 3 > m_head) {
                    underrun = true;
                    break;
                }
                float const* const x0 = frame(m_pos - 1);
                float const* const x1 = frame(m_pos);
                float const* const x2 = frame(m_pos + 1);
                float const* const x3 = frame(m_pos + 2);
                float const t = static_cast<float>(m_frac);
                float* const y = scratch + size_t{ produced } * m_channels;
                for (uint32_t c = 0; c < m_channels; ++c) {
                    float const k1 = 0.5f * (x2[c] - x0[c]);
                    float const k2 = x0[c] - 2.5f * x1[c] + 2 * x2[c] - 0.5f * x3[c];
                    float const k3 = 0.5f * (x3[c] - x0[c]) + 1.5f * (x1[c] - x2[c]);
                    y[c] = ((k3 * t + k2) * t + k1) * t + x1[c];
                }
                m_frac += m_step;
                double const whole = std::floor(m_frac);
                m_pos += static_cast<uint64_t>(whole);
                m_frac -= whole;
            }
            m_tail = std::min(m_pos, m_head) - 1;
            if (underrun) {
                // Silence for this source only until primed again; it then fades in from 0.
                m_playing = false;
                m_windowClean = false;
            } else {
                m_low = std::min(m_low, static_cast<double>(m_head - m_pos) - m_frac);
            }
        }
        float const target = entry->gain.load(std::memory_order_relaxed);
        float gain = m_gain;
        for (uint32_t f = 0; f < produced; ++f) {
            gain = std::clamp(target, gain - m_rampStep, gain + m_rampStep);
            float const* const y = scratch + size_t{ f } * m_channels;
            float* const o = out + size_t{ f } * m_channels;
            for (uint32_t c = 0; c < m_channels; ++c) {
                o[c] += gain * y[c];
            }
        }
        m_gain = underrun ? 0.0f : gain;
        m_windowFrames += frames;
        if (m_windowFrames >= m_windowLength) {
            if (m_windowClean && !m_jitter) {
                // PI on the window's low point: above the margin = too much latency, so read slightly faster.
                double const error = (m_low - m_margin) / m_rate;
                m_integral = std::clamp(m_integral + kKi * kWindowSeconds * error, -kMaxAdjust, kMaxAdjust);
                m_step = m_nominal * (1 + std::clamp(kKp * error + m_integral, -kMaxAdjust, kMaxAdjust));
            }
            start_window();
        }
    }

private:
    float* frame(uint64_t index) { return m_ring.data() + (index & m_mask) * m_channels; }

    void start_window()
    {
        m_windowFrames = 0;
        m_low = std::numeric_limits<double>::infinity();
        m_windowClean = m_playing;
    }

    // Direct source: pulls from the jitter buffer exactly the frames the interpolator reads for `frames` output frames
    // (up to its position after them + 2 ahead, plus 1 of rounding slack), so the FIFO adds ~4 samples of delay.
    void pull(uint32_t frames)
    {
        double const end = static_cast<double>(m_pos) + m_frac + frames * m_step;
        uint64_t const needed = static_cast<uint64_t>(end) + 4;
        if (needed <= m_head) {
            return;
        }
        uint32_t const count = static_cast<uint32_t>(std::min<uint64_t>(needed - m_head, m_pulled.size()));
        wpn_jitter_pull(m_jitter, m_pulled.data(), count);
        for (uint32_t i = 0; i < count; ++i) {
            m_pulled[i] *= 1.0f / 32768;
        }
        push(reinterpret_cast<BYTE const*>(m_pulled.data()), count, false);
    }

    // Plays from m_prime frames before the newest one; anything older is dropped.
    void restart()
    {
        m_pos = m_head - m_prime;
        m_frac = 0;
        m_tail = m_pos - 1;
        m_playing = true;
        start_window();
    }

    void push(BYTE const* data, uint32_t frames, bool silent)
    {
        uint64_t const capacity = m_mask + 1;
        if (m_head - m_tail + frames > capacity) {
            m_tail = m_head + frames - capacity;
            if (m_pos <= m_tail) {
                m_pos = m_tail + 1;
                m_frac = 0;
            }
        }
        uint32_t const in = m_format.channels;
        for (uint32_t f = 0; f < frames; ++f) {
            float* const y = frame(m_head + f);
            if (silent) {
                std::fill_n(y, m_channels, 0.0f);
            } else if (m_direct) {
                std::memcpy(y, reinterpret_cast<float const*>(data) + size_t{ f } * m_channels, m_channels * sizeof(float));
            } else {
                for (uint32_t o = 0; o < m_channels; ++o) {
                    float sum = 0;
                    for (uint32_t c = 0; c < in; ++c) {
                        sum += m_matrix[size_t{ o } * in + c] * read_sample(data, size_t{ f } * in + c, m_format);
                    }
                    y[o] = sum;
                }
            }
        }
        m_head += frames;
        if (!m_playing ? m_head - m_tail > m_prime : m_head - m_pos > m_overflow) {
            restart(); // primed, or overflowed (e.g. after a stall)
        }
    }

    Format m_format; // capture stream
    uint32_t m_capturePeriod = 0;
    uint32_t m_bufferFrames = 0;
    uint32_t m_channels = 0; // output channels = FIFO frame width
    bool m_direct = false;   // float32 with the output's channel count: copied as is
    std::vector<float> m_matrix;
    std::vector<float> m_ring;
    uint64_t m_mask = 0;
    uint64_t m_head = 0; // next frame written
    uint64_t m_tail = 0; // oldest frame kept (m_pos - 1 while playing)
    uint64_t m_pos = 1;  // interpolator: frame index + fraction
    double m_frac = 0;
    bool m_playing = false;
    double m_rate = 0;
    double m_nominal = 1; // source rate / output rate
    double m_step = 1;    // source frames per output frame
    double m_integral = 0;
    double m_margin = 0;
    uint64_t m_prime = 0;
    uint64_t m_overflow = 0;
    uint32_t m_windowLength = 0;
    uint32_t m_windowFrames = 0;
    double m_low = 0;
    bool m_windowClean = false;
    float m_gain = 0;
    float m_rampStep = 0;
    void* m_jitter = nullptr;    // direct source: the mic jitter buffer
    std::vector<float> m_pulled; // direct source: one pass's pull
};

class Engine {
public:
    void run()
    {
        while (true) {
            bool external;
            {
                ExclusiveLock lock(g_lock);
                if (!g_initialized || (!g_reconfigure && !m_dirty && !active())) {
                    g_running = false;
                    break;
                }
                external = std::exchange(g_reconfigure, false);
            }
            if (external || m_dirty) {
                m_dirty = false;
                reconcile(external);
                if (!active()) {
                    continue; // everything closed: exits unless something changed meanwhile
                }
            }
            if (WaitForMultipleObjects(static_cast<DWORD>(m_handles.size()), m_handles.data(), FALSE, INFINITE) == WAIT_FAILED) {
                close_all();
                continue;
            }
            process();
            watch_changes();
        }
        close_all();
    }

private:
    bool active() const { return m_output.client && !m_sources.empty(); }

    void close_all()
    {
        m_sources.clear();
        m_output = {};
        m_handles.clear();
    }

    void reconcile(bool external)
    {
        try {
            reconcile_streams(external);
        } catch (...) {
            close_all(); // e.g. no device enumerator; the next change retries
        }
    }

    // After a wake: registers for new Hands-Free sessions, and has the output switched when Hands-Free started or
    // ended. A failing watch counts as not active until the next endpoint change arms it again.
    void watch_changes()
    {
        try {
            m_watch.rescan_if_needed();
        } catch (...) {
            m_watch.disarm();
        }
        if (m_watch.active() != m_wantHandsFree) {
            m_dirty = true;
        }
    }

    // Watches the target's Hands-Free capture endpoint (re-armed when it appears, disappears or changes).
    void arm_watch(uint64_t target)
    {
        try {
            auto const device = target != 0 ? find_pair_endpoint(m_enumerator.get(), target, eCapture, true) : nullptr;
            std::wstring id = device ? endpoint_id(device.get()) : std::wstring{};
            if (id == m_watch.id()) {
                return;
            }
            m_watch.disarm();
            if (device) {
                m_watch.arm(device.get(), std::move(id));
            }
        } catch (...) {
            m_watch.disarm();
        }
    }

    // Opens the wanted output kind, else the other one; false when neither is there or opens.
    bool open_output_for(uint64_t target, bool handsFree)
    {
        for (bool const kind : { handsFree, !handsFree }) {
            try {
                if (auto const device = find_pair_endpoint(m_enumerator.get(), target, eRender, kind)) {
                    open_output(device.get(), kind);
                    return true;
                }
            } catch (...) {
                // Try the other kind.
            }
        }
        return false;
    }

    // Brings the streams in line with the configuration. Parked = failed without ever working; retried only after
    // the next external change, so a broken endpoint cannot make the engine spin.
    void reconcile_streams(bool external)
    {
        if (external) {
            m_parked.clear();
            m_outputParked = false;
        }
        uint64_t target;
        std::vector<std::shared_ptr<SourceEntry>> entries;
        {
            ExclusiveLock lock(g_lock);
            target = g_target;
            entries = g_sources;
        }
        if (target != m_target) {
            close_all();
            m_target = target;
        }
        std::erase_if(m_sources, [&entries](std::unique_ptr<Source> const& source) {
            return std::find(entries.begin(), entries.end(), source->entry) == entries.end();
        });
        if (!m_enumerator) {
            m_enumerator = winrt::create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
        }
        if (external) {
            // A direct source has no stream to report its endpoint leaving: closed (still set) when it is no longer
            // ACTIVE, reopened below when it is again.
            std::erase_if(m_sources, [this](std::unique_ptr<Source> const& source) {
                winrt::com_ptr<IMMDevice> device;
                DWORD state = 0;
                return source->pulls() &&
                    (FAILED(m_enumerator->GetDevice(source->entry->id.c_str(), device.put())) ||
                        FAILED(device->GetState(&state)) || state != DEVICE_STATE_ACTIVE);
            });
            arm_watch(target);
        }
        bool const handsFree = m_watch.active();
        m_wantHandsFree = handsFree;
        if (m_output.client && m_outputHandsFree != handsFree) {
            // Switch only when the wanted endpoint is there; else stay on the current one. Sources stay open.
            winrt::com_ptr<IMMDevice> device;
            try {
                device = find_pair_endpoint(m_enumerator.get(), target, eRender, handsFree);
            } catch (...) {
                // Not switched.
            }
            if (device) {
                m_output = {};
                m_handles.clear();
                try {
                    open_output(device.get(), handsFree);
                } catch (...) {
                    // Reopened below.
                }
            }
        }
        std::vector<std::pair<std::shared_ptr<SourceEntry>, winrt::com_ptr<IMMDevice>>> ready;
        std::vector<std::shared_ptr<SourceEntry>> gone;
        if (target != 0 && !m_outputParked) {
            for (auto const& entry : entries) {
                bool const open = std::any_of(m_sources.begin(), m_sources.end(), [&entry](auto const& source) {
                    return source->entry == entry;
                });
                if (open || std::find(m_parked.begin(), m_parked.end(), entry) != m_parked.end()) {
                    continue;
                }
                winrt::com_ptr<IMMDevice> device;
                DWORD state = 0;
                HRESULT const hr = m_enumerator->GetDevice(entry->id.c_str(), device.put());
                if (hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) {
                    gone.push_back(entry);
                } else if (SUCCEEDED(hr) && SUCCEEDED(device->GetState(&state)) && state == DEVICE_STATE_ACTIVE) {
                    ready.emplace_back(entry, std::move(device));
                }
            }
        }
        drop_gone(gone);
        if (!m_output.client && !m_outputParked && target != 0 && (!m_sources.empty() || !ready.empty())) {
            m_outputParked = !open_output_for(target, handsFree);
        }
        if (m_output.client) {
            for (auto const& [entry, device] : ready) {
                if (m_sources.size() == kMaxSources) {
                    break;
                }
                try {
                    m_sources.push_back(open_source(entry, device));
                } catch (...) {
                    m_parked.push_back(entry); // closed but still set
                }
            }
        }
        if (!active()) {
            close_all();
            return;
        }
        m_handles = { g_wake, m_output.event.get() };
        for (auto const& source : m_sources) {
            if (!source->pulls()) {
                m_handles.push_back(source->event.get());
            }
        }
    }

    // Sources whose endpoint Windows no longer knows: dropped from the configuration (if still the same entry) and
    // reported, outside g_lock.
    static void drop_gone(std::vector<std::shared_ptr<SourceEntry>> const& gone)
    {
        std::vector<std::shared_ptr<SourceEntry>> dropped;
        {
            ExclusiveLock lock(g_lock);
            for (auto const& entry : gone) {
                auto const it = std::find(g_sources.begin(), g_sources.end(), entry);
                if (it != g_sources.end()) {
                    g_sources.erase(it);
                    dropped.push_back(entry);
                }
            }
        }
        for (auto const& entry : dropped) {
            notify(kSourceGone, entry->id.c_str());
        }
    }

    // Sources still open (an output switch) are retargeted to the new output.
    void open_output(IMMDevice* device, bool handsFree)
    {
        Output output;
        output.client = activate(device);
        MixFormat const mix = mix_format(output.client.get());
        if (!parse_format(mix.get(), output.layout) || !output.layout.isFloat) {
            winrt::throw_hresult(AUDCLNT_E_UNSUPPORTED_FORMAT);
        }
        DWORD const flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
        output.period = init_low_latency(output.client, flags, mix.get());
        if (output.period == 0) {
            output.client = activate(device);
            winrt::check_hresult(init_legacy(output.client.get(), flags, mix.get(), output.period));
        }
        UINT32 bufferFrames = 0;
        winrt::check_hresult(output.client->GetBufferSize(&bufferFrames));
        output.fill = std::clamp(output.period, 1u, bufferFrames);
        output.event = make_event();
        winrt::check_hresult(output.client->SetEventHandle(output.event.get()));
        winrt::check_hresult(output.client->GetService(__uuidof(IAudioRenderClient), output.render.put_void()));
        // One period of silence queued before the start, so the first engine pass has data.
        BYTE* data = nullptr;
        winrt::check_hresult(output.render->GetBuffer(output.fill, &data));
        winrt::check_hresult(output.render->ReleaseBuffer(output.fill, AUDCLNT_BUFFERFLAGS_SILENT));
        auto const* const bytes = reinterpret_cast<BYTE const*>(mix.get());
        output.format.assign(bytes, bytes + sizeof(WAVEFORMATEX) + mix->cbSize);
        m_scratch.assign(size_t{ output.fill } * output.layout.channels, 0.0f);
        winrt::check_hresult(output.client->Start());
        m_output = std::move(output);
        m_outputHandsFree = handsFree;
        for (auto const& source : m_sources) {
            source->retarget(m_output);
        }
    }

    std::unique_ptr<Source> open_source(std::shared_ptr<SourceEntry> const& entry, winrt::com_ptr<IMMDevice> const& device)
    {
        // The WPods mic skips Windows: its PCM comes straight from the app. One direct source at a time (the app
        // feeds one mic); another WPods mic endpoint is captured as usual.
        bool const directOpen = std::any_of(m_sources.begin(), m_sources.end(), [](auto const& source) { return source->pulls(); });
        if (!directOpen && is_own_mic(entry->id.c_str())) {
            void* const jitter = mic_jitter();
            wpn_jitter_reset(jitter);
            auto source = std::make_unique<Source>(entry);
            source->configure_direct(jitter, m_output);
            g_micDirect = true;
            return source;
        }
        EDataFlow flow;
        winrt::check_hresult(device.as<IMMEndpoint>()->GetDataFlow(&flow));
        DWORD const flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | (flow == eRender ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0);
        auto source = std::make_unique<Source>(entry);
        source->client = activate(device.get());
        MixFormat const mix = mix_format(source->client.get());
        Format deviceFormat;
        bool const supported = parse_format(mix.get(), deviceFormat);
        Format format;
        uint32_t period = 0;
        if (supported && deviceFormat.rate == m_output.layout.rate) {
            // Same rate: the device's own format at its minimum period; channels (if any) are mapped here.
            period = init_low_latency(source->client, flags, mix.get());
            format = deviceFormat;
        }
        if (period == 0) {
            // Windows converts to the output format (float32, its channels and rate) at the legacy period. Not on
            // Hands-Free: the stream outlives the switch back, so it keeps the device's own rate.
            bool converted = false;
            if (!supported || !m_outputHandsFree) {
                source->client = activate(device.get());
                format = m_output.layout;
                auto const* const outputFormat = reinterpret_cast<WAVEFORMATEX const*>(m_output.format.data());
                DWORD const convert = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                converted = SUCCEEDED(init_legacy(source->client.get(), flags | convert, outputFormat, period));
            }
            if (!converted) {
                // The device's format; channels mapped here, the rate ratio folded into the interpolator.
                if (!supported) {
                    winrt::throw_hresult(AUDCLNT_E_UNSUPPORTED_FORMAT);
                }
                source->client = activate(device.get());
                winrt::check_hresult(init_legacy(source->client.get(), flags, mix.get(), period));
                format = deviceFormat;
            }
        }
        UINT32 bufferFrames = 0;
        winrt::check_hresult(source->client->GetBufferSize(&bufferFrames));
        source->event = make_event();
        winrt::check_hresult(source->client->SetEventHandle(source->event.get()));
        winrt::check_hresult(source->client->GetService(__uuidof(IAudioCaptureClient), source->capture.put_void()));
        source->configure(format, period, bufferFrames, m_output);
        winrt::check_hresult(source->client->Start());
        return source;
    }

    // After any wake: drains every source, then tops the render buffer up to one period.
    void process()
    {
        for (auto it = m_sources.begin(); it != m_sources.end();) {
            if (SUCCEEDED((*it)->drain())) {
                ++it;
                continue;
            }
            // Closed but still set (unplugged, invalidated, ...): reopened when its endpoint is ACTIVE; at once if it
            // had worked (e.g. a format change), else after the next change.
            if (!(*it)->delivered) {
                m_parked.push_back((*it)->entry);
            }
            it = m_sources.erase(it);
            m_dirty = true;
        }
        if (m_output.client && FAILED(render())) {
            m_outputParked = !m_output.rendered;
            close_all();
            m_dirty = true;
        }
    }

    HRESULT render()
    {
        UINT32 padding = 0;
        HRESULT hr = m_output.client->GetCurrentPadding(&padding);
        if (FAILED(hr) || padding >= m_output.fill) {
            return hr;
        }
        UINT32 const frames = m_output.fill - padding;
        BYTE* data = nullptr;
        hr = m_output.render->GetBuffer(frames, &data);
        if (FAILED(hr)) {
            return hr;
        }
        float* const out = reinterpret_cast<float*>(data);
        size_t const samples = size_t{ frames } * m_output.layout.channels;
        std::fill_n(out, samples, 0.0f);
        for (auto const& source : m_sources) {
            source->add(out, frames, m_scratch.data());
        }
        for (size_t i = 0; i < samples; ++i) {
            out[i] = soft_clip(out[i]);
        }
        hr = m_output.render->ReleaseBuffer(frames, 0);
        if (SUCCEEDED(hr)) {
            m_output.rendered = true;
        }
        return hr;
    }

    winrt::com_ptr<IMMDeviceEnumerator> m_enumerator;
    uint64_t m_target = 0;
    Output m_output;
    std::vector<std::unique_ptr<Source>> m_sources;
    std::vector<std::shared_ptr<SourceEntry>> m_parked;
    bool m_outputParked = false;
    bool m_outputHandsFree = false; // the open output is the Hands-Free render endpoint
    bool m_wantHandsFree = false;   // the watch's answer when the engine last reconciled
    bool m_dirty = false; // a stream closed itself or Hands-Free changed: reconcile before waiting again
    SessionWatch m_watch;
    std::vector<HANDLE> m_handles;
    std::vector<float> m_scratch;
};

// Takes the calling thread out of power throttling and keeps it on the performance cores (highest EfficiencyClass);
// on CPUs whose cores are all one class there is nothing to restrict.
void use_performance_cores()
{
    THREAD_POWER_THROTTLING_STATE throttling{ THREAD_POWER_THROTTLING_CURRENT_VERSION, THREAD_POWER_THROTTLING_EXECUTION_SPEED, 0 };
    SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, &throttling, sizeof(throttling));
    ULONG length = 0;
    GetSystemCpuSetInformation(nullptr, 0, &length, nullptr, 0);
    std::vector<BYTE> buffer(length);
    if (!GetSystemCpuSetInformation(reinterpret_cast<PSYSTEM_CPU_SET_INFORMATION>(buffer.data()), length, &length, nullptr, 0)) {
        return;
    }
    std::vector<ULONG> ids;
    BYTE best = 0;
    bool mixed = false;
    for (ULONG offset = 0; offset < length;) {
        auto const* info = reinterpret_cast<SYSTEM_CPU_SET_INFORMATION const*>(buffer.data() + offset);
        if (info->Size == 0) {
            break;
        }
        if (info->Type == CpuSetInformation) {
            BYTE const efficiency = info->CpuSet.EfficiencyClass;
            if (!ids.empty() && efficiency != best) {
                mixed = true;
            }
            if (ids.empty() || efficiency > best) {
                best = efficiency;
                ids.clear();
            }
            if (efficiency == best) {
                ids.push_back(info->CpuSet.Id);
            }
        }
        offset += info->Size;
    }
    if (mixed) {
        SetThreadSelectedCpuSets(GetCurrentThread(), ids.data(), static_cast<ULONG>(ids.size()));
    }
}

DWORD WINAPI engine_main(void* previous)
{
    if (previous) {
        WaitForSingleObject(previous, INFINITE);
        CloseHandle(previous);
    }
    HRESULT const hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    DWORD taskIndex = 0;
    HANDLE const task = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);
    use_performance_cores();
    if (SUCCEEDED(hr)) {
        Engine engine;
        engine.run();
    } else {
        ExclusiveLock lock(g_lock);
        g_running = false;
    }
    if (task) {
        AvRevertMmThreadCharacteristics(task);
    }
    if (SUCCEEDED(hr)) {
        CoUninitialize();
    }
    return 0;
}

constexpr uint32_t kLoopbackRate = 48000;

// One loopback stream: its packets averaged to mono and, when Windows cannot convert the rate, resampled here
// (linear). Used by one thread at a time.
class Loopback {
public:
    Loopback() = default;
    Loopback(Loopback const&) = delete;
    Loopback& operator=(Loopback const&) = delete;
    ~Loopback()
    {
        if (m_client) {
            m_client->Stop();
        }
    }

    // Throws on failure.
    void open(uint64_t address)
    {
        auto const enumerator = winrt::create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
        winrt::com_ptr<IMMDevice> device = find_pair_endpoint(enumerator.get(), address, eRender, false);
        if (!device) {
            device = find_pair_endpoint(enumerator.get(), address, eRender, true);
        }
        if (!device) {
            winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_NOT_FOUND));
        }
        DWORD const flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_LOOPBACK;
        m_client = activate(device.get());
        MixFormat const mix = mix_format(m_client.get());
        Format deviceFormat;
        bool const supported = parse_format(mix.get(), deviceFormat);
        uint32_t period = 0;
        if (supported && deviceFormat.rate == kLoopbackRate) {
            m_format = deviceFormat;
            period = init_low_latency(m_client, flags, mix.get());
            if (period == 0) {
                m_client = activate(device.get());
                winrt::check_hresult(init_legacy(m_client.get(), flags, mix.get(), period));
            }
        } else {
            // Windows converts to float32 at 48 kHz in the device's channels; else the device's format is read as is.
            WAVEFORMATEXTENSIBLE wanted{};
            wanted.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
            wanted.Format.nChannels = mix->nChannels;
            wanted.Format.nSamplesPerSec = kLoopbackRate;
            wanted.Format.wBitsPerSample = 32;
            wanted.Format.nBlockAlign = static_cast<WORD>(mix->nChannels * sizeof(float));
            wanted.Format.nAvgBytesPerSec = kLoopbackRate * wanted.Format.nBlockAlign;
            wanted.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
            wanted.Samples.wValidBitsPerSample = 32;
            wanted.dwChannelMask = supported ? deviceFormat.mask : 0;
            wanted.SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
            DWORD const convert = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            m_client = activate(device.get());
            if (SUCCEEDED(init_legacy(m_client.get(), flags | convert, &wanted.Format, period))) {
                m_format = { mix->nChannels, kLoopbackRate, wanted.dwChannelMask, 32, true };
            } else {
                if (!supported) {
                    winrt::throw_hresult(AUDCLNT_E_UNSUPPORTED_FORMAT);
                }
                m_client = activate(device.get());
                winrt::check_hresult(init_legacy(m_client.get(), flags, mix.get(), period));
                m_format = deviceFormat;
            }
        }
        m_event = make_event();
        winrt::check_hresult(m_client->SetEventHandle(m_event.get()));
        winrt::check_hresult(m_client->GetService(__uuidof(IAudioCaptureClient), m_capture.put_void()));
        m_step = static_cast<double>(m_format.rate) / kLoopbackRate;
        m_period = static_cast<uint32_t>(uint64_t{ period } * kLoopbackRate / m_format.rate);
        winrt::check_hresult(m_client->Start());
    }

    uint32_t period() const { return m_period; }

    // Samples written, or a negative HRESULT.
    int32_t read(float* out, uint32_t capacity, uint32_t timeoutMs)
    {
        // The event may still be set for a packet an earlier drain took: 0 only once the timeout really ran out.
        ULONGLONG const deadline = GetTickCount64() + timeoutMs;
        while (m_next == m_pending.size()) {
            HRESULT const hr = drain();
            if (FAILED(hr)) {
                return hr;
            }
            if (m_next != m_pending.size()) {
                break;
            }
            ULONGLONG const now = GetTickCount64();
            if (now >= deadline) {
                return 0;
            }
            DWORD const wait = WaitForSingleObject(m_event.get(), static_cast<DWORD>(deadline - now));
            if (wait == WAIT_FAILED) {
                return HRESULT_FROM_WIN32(GetLastError());
            }
        }
        size_t const count = std::min<size_t>({ capacity, m_pending.size() - m_next, INT32_MAX });
        std::copy_n(m_pending.data() + m_next, count, out);
        m_next += count;
        if (m_next == m_pending.size()) {
            m_pending.clear();
            m_next = 0;
        }
        return static_cast<int32_t>(count);
    }

private:
    HRESULT drain()
    {
        while (true) {
            UINT32 packet = 0;
            HRESULT hr = m_capture->GetNextPacketSize(&packet);
            if (FAILED(hr) || packet == 0) {
                return hr;
            }
            BYTE* data = nullptr;
            UINT32 frames = 0;
            DWORD flags = 0;
            hr = m_capture->GetBuffer(&data, &frames, &flags, nullptr, nullptr);
            if (FAILED(hr)) {
                return hr;
            }
            push(data, frames, (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0);
            hr = m_capture->ReleaseBuffer(frames);
            if (FAILED(hr)) {
                return hr;
            }
        }
    }

    void push(BYTE const* data, uint32_t frames, bool silent)
    {
        uint32_t const channels = m_format.channels;
        float const scale = 1.0f / static_cast<float>(channels);
        for (uint32_t f = 0; f < frames; ++f) {
            float sample = 0;
            if (!silent) {
                for (uint32_t c = 0; c < channels; ++c) {
                    sample += read_sample(data, size_t{ f } * channels + c, m_format);
                }
                sample *= scale;
            }
            if (m_format.rate == kLoopbackRate) {
                m_pending.push_back(sample);
                continue;
            }
            // Output points between the previous input sample (0) and this one (1).
            for (; m_frac < 1; m_frac += m_step) {
                m_pending.push_back(m_previous + (sample - m_previous) * static_cast<float>(m_frac));
            }
            m_frac -= 1;
            m_previous = sample;
        }
    }

    winrt::com_ptr<IAudioClient> m_client;
    winrt::com_ptr<IAudioCaptureClient> m_capture;
    winrt::handle m_event;
    Format m_format;
    uint32_t m_period = 0;
    double m_step = 1; // input frames per output sample
    double m_frac = 0;
    float m_previous = 0;
    std::vector<float> m_pending; // mono 48 kHz not yet read, from m_next on
    size_t m_next = 0;
};

} // namespace

int32_t __stdcall wpn_mixer_init(wpn_mixer_event_cb callback, void* ctx)
{
    AcquireSRWLockExclusive(&g_apiLock);
    HRESULT hr = S_FALSE;
    if (!g_notifier) {
        try {
            winrt::check_hresult(ensure_mta());
            auto enumerator = winrt::create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
            winrt::handle wake = make_event();
            set_callback(callback, ctx);
            hr = enumerator->RegisterEndpointNotificationCallback(&g_client);
            if (FAILED(hr)) {
                set_callback(nullptr, nullptr);
                winrt::throw_hresult(hr);
            }
            g_notifier = enumerator.detach();
            ExclusiveLock lock(g_lock);
            g_wake = wake.detach();
            g_initialized = true;
            kick_locked();
            hr = S_OK;
        } catch (...) {
            hr = winrt::to_hresult();
        }
    }
    ReleaseSRWLockExclusive(&g_apiLock);
    return hr;
}

void __stdcall wpn_mixer_shutdown(void)
{
    AcquireSRWLockExclusive(&g_apiLock);
    if (g_notifier) {
        g_notifier->UnregisterEndpointNotificationCallback(&g_client);
        g_notifier->Release();
        g_notifier = nullptr;
        HANDLE thread;
        {
            ExclusiveLock lock(g_lock);
            g_initialized = false;
            g_target = 0;
            g_sources.clear();
            g_reconfigure = false;
            thread = std::exchange(g_thread, nullptr);
            SetEvent(g_wake);
        }
        if (thread) {
            WaitForSingleObject(thread, INFINITE);
            CloseHandle(thread);
        }
        {
            ExclusiveLock lock(g_lock);
            CloseHandle(g_wake);
            g_wake = nullptr;
        }
        // Waits for a callback in flight on a notification thread; later ones see no callback.
        set_callback(nullptr, nullptr);
    }
    ReleaseSRWLockExclusive(&g_apiLock);
}

int32_t __stdcall wpn_mixer_enum_devices(const uint64_t* pairs, uint32_t pairCount, wpn_mixer_device_cb cb, void* ctx)
{
    try {
        winrt::check_hresult(ensure_mta());
        std::vector<std::pair<GUID, uint64_t>> containers;
        for (uint32_t i = 0; i < pairCount; ++i) {
            GUID containerId;
            if (find_container_id(pairs[i], containerId)) {
                containers.emplace_back(containerId, pairs[i]);
            }
        }
        auto const enumerator = winrt::create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
        winrt::com_ptr<IMMDeviceCollection> endpoints;
        winrt::check_hresult(enumerator->EnumAudioEndpoints(eAll, DEVICE_STATEMASK_ALL, endpoints.put()));
        UINT count = 0;
        winrt::check_hresult(endpoints->GetCount(&count));
        for (UINT i = 0; i < count; ++i) {
            winrt::com_ptr<IMMDevice> endpoint;
            winrt::com_ptr<IPropertyStore> properties;
            wchar_t* rawId = nullptr;
            DWORD state = 0;
            EDataFlow flow;
            if (FAILED(endpoints->Item(i, endpoint.put())) || FAILED(endpoint->GetId(&rawId))) {
                continue;
            }
            std::unique_ptr<wchar_t, CoTaskMemFreer> const id(rawId);
            auto const direction = endpoint.try_as<IMMEndpoint>();
            if (!direction || FAILED(direction->GetDataFlow(&flow)) || FAILED(endpoint->GetState(&state)) ||
                FAILED(endpoint->OpenPropertyStore(STGM_READ, properties.put()))) {
                continue;
            }
            uint64_t pair = 0;
            GUID containerId;
            if (container_of(properties.get(), containerId)) {
                auto const match = std::find_if(containers.begin(), containers.end(),
                    [&](auto const& entry) { return entry.first == containerId; });
                if (match != containers.end()) {
                    pair = match->second;
                }
            }
            // A pair's own output would play back into itself.
            if (pair != 0 && flow == eRender) {
                continue;
            }
            PROPVARIANT name;
            PropVariantInit(&name);
            bool const named = SUCCEEDED(properties->GetValue(PKEY_Device_FriendlyName, &name)) && name.vt == VT_LPWSTR;
            uint32_t const flags = is_own_mic(id.get()) ? 1u : 0u;
            cb(ctx, id.get(), named ? name.pwszVal : L"", flow == eRender ? 0 : 1, state, pair, flags);
            PropVariantClear(&name);
        }
        return S_OK;
    } catch (...) {
        return winrt::to_hresult();
    }
}

int32_t __stdcall wpn_mixer_set_target(uint64_t btAddress)
{
    ExclusiveLock lock(g_lock);
    if (g_target != btAddress) {
        g_target = btAddress;
        kick_locked();
    }
    return S_OK;
}

int32_t __stdcall wpn_mixer_set_source(const wchar_t* id, float gain)
{
    if (!id) {
        return E_POINTER;
    }
    gain = std::clamp(gain, 0.0f, 1.0f);
    try {
        auto entry = std::make_shared<SourceEntry>(id, gain);
        ExclusiveLock lock(g_lock);
        auto const existing = find_source_locked(id);
        if (existing != g_sources.end()) {
            (*existing)->gain.store(gain, std::memory_order_relaxed);
        } else {
            g_sources.push_back(std::move(entry));
            kick_locked();
        }
        return S_OK;
    } catch (...) {
        return winrt::to_hresult();
    }
}

int32_t __stdcall wpn_mixer_remove_source(const wchar_t* id)
{
    if (!id) {
        return E_POINTER;
    }
    ExclusiveLock lock(g_lock);
    auto const existing = find_source_locked(id);
    if (existing == g_sources.end()) {
        return S_FALSE;
    }
    g_sources.erase(existing);
    kick_locked();
    return S_OK;
}

void __stdcall wpn_mixer_push_mic(const int16_t* pcm, uint32_t count, int64_t pos)
{
    if (!g_micDirect.load()) {
        return;
    }
    // Set (and never cleared) before g_micDirect was first set.
    wpn_jitter_push(g_micJitter.load(), pcm, count, pos);
}

int32_t __stdcall wpn_loopback_open(uint64_t btAddress, void** capture, uint32_t* period)
{
    if (!capture || !period) {
        return E_POINTER;
    }
    *capture = nullptr;
    *period = 0;
    try {
        winrt::check_hresult(ensure_mta());
        auto loopback = std::make_unique<Loopback>();
        loopback->open(btAddress);
        *period = loopback->period();
        *capture = loopback.release();
        return S_OK;
    } catch (...) {
        return winrt::to_hresult();
    }
}

int32_t __stdcall wpn_loopback_read(void* capture, float* out, uint32_t capacity, uint32_t timeoutMs)
{
    if (!capture || (!out && capacity > 0)) {
        return E_POINTER;
    }
    try {
        return static_cast<Loopback*>(capture)->read(out, capacity, timeoutMs);
    } catch (...) {
        return winrt::to_hresult();
    }
}

void __stdcall wpn_loopback_close(void* capture)
{
    delete static_cast<Loopback*>(capture);
}
