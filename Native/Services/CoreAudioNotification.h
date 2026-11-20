#pragma once
#include <mmdeviceapi.h>
#include <atomic>
#include <mutex>

namespace vnotch::services {

class EndpointNotification final : public IMMNotificationClient {
    using Callback = void(__stdcall*)(int64_t);
    std::atomic<ULONG> references_{1};
    std::mutex gate_;
    Callback callback_;
    const int64_t context_;
    const bool all_endpoints_;

    HRESULT Notify(bool relevant) noexcept {
        if (relevant) {
            std::lock_guard lock(gate_);
            if (callback_) callback_(context_);
        }
        return S_OK;
    }

public:
    EndpointNotification(Callback callback, int64_t context, bool all_endpoints) noexcept
        : callback_(callback), context_(context), all_endpoints_(all_endpoints) {}

    void Disconnect() noexcept {
        std::lock_guard lock(gate_);
        callback_ = nullptr;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (iid != __uuidof(IUnknown) && iid != __uuidof(IMMNotificationClient)) return E_NOINTERFACE;
        *object = static_cast<IMMNotificationClient*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override {
        const ULONG remaining = --references_;
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(LPCWSTR, DWORD) override { return Notify(all_endpoints_); }
    HRESULT STDMETHODCALLTYPE OnDeviceAdded(LPCWSTR) override { return Notify(all_endpoints_); }
    HRESULT STDMETHODCALLTYPE OnDeviceRemoved(LPCWSTR) override { return Notify(all_endpoints_); }
    HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(LPCWSTR, const PROPERTYKEY) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(EDataFlow flow, ERole role, LPCWSTR) override {
        return Notify(all_endpoints_ || (flow == eRender && role == eMultimedia));
    }
};
}
