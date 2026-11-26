#pragma once
#include <mmdeviceapi.h>
#include <atomic>

#pragma comment(lib, "synchronization.lib")

namespace vnotch::services {

class EndpointNotification final : public IMMNotificationClient {
    using Callback = void(__stdcall*)(int64_t);
    std::atomic<ULONG> references_{1};
    SRWLOCK gate_ = SRWLOCK_INIT;
    Callback callback_;
    const int64_t context_;
    const bool all_endpoints_;
    std::atomic<int32_t> in_flight_{0};
    std::atomic<DWORD> callback_thread_{0};

    HRESULT Notify(bool relevant) noexcept {
        if (!relevant) return S_OK;
        Callback callback = nullptr;
        DWORD previous_thread = 0;
        {
            AcquireSRWLockExclusive(&gate_);
            callback = callback_;
            if (callback) {
                in_flight_.fetch_add(1, std::memory_order_acq_rel);
                previous_thread = callback_thread_.exchange(GetCurrentThreadId(), std::memory_order_acq_rel);
            }
            ReleaseSRWLockExclusive(&gate_);
        }
        if (!callback) return S_OK;
        // Invoke outside gate_ so re-entrant Disconnect()/Dispose() cannot deadlock.
        callback(context_);
        callback_thread_.store(previous_thread, std::memory_order_release);
        if (in_flight_.fetch_sub(1, std::memory_order_acq_rel) == 1) {
            WakeByAddressAll(&in_flight_);
        }
        return S_OK;
    }

public:
    EndpointNotification(Callback callback, int64_t context, bool all_endpoints) noexcept
        : callback_(callback), context_(context), all_endpoints_(all_endpoints) {}

    void Disconnect() noexcept {
        {
            AcquireSRWLockExclusive(&gate_);
            callback_ = nullptr;
            ReleaseSRWLockExclusive(&gate_);
        }
        const DWORD self_tid = GetCurrentThreadId();
        for (;;) {
            int32_t active = in_flight_.load(std::memory_order_acquire);
            if (active == 0 || callback_thread_.load(std::memory_order_acquire) == self_tid) break;
            WaitOnAddress(&in_flight_, &active, sizeof(active), INFINITE);
        }
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
