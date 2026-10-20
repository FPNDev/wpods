// wpods_native.dll: passive BLE Proximity Pairing watcher + BT audio KS one-shot connect/disconnect.
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

#include <algorithm>
#include <string>
#include <vector>

#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Devices.Bluetooth.Advertisement.h>
#include <winrt/Windows.Storage.Streams.h>

#include "wpods_native.h"

using namespace winrt::Windows::Devices::Bluetooth;
using namespace winrt::Windows::Devices::Bluetooth::Advertisement;

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
