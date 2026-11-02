#include "../vnotch_native.h"
#include "../vnotch_handle_registry.h"
#include <memory>

namespace {
struct RegistryWatch {
    HKEY key = nullptr;
    HANDLE event = nullptr, signal = nullptr;
    PTP_WAIT wait = nullptr;
    SRWLOCK arm_gate = SRWLOCK_INIT;
    std::atomic<bool> changed{true}, failed{false}, stopping{false};
    ~RegistryWatch() {
        stopping.store(true);
        if (wait) {
            AcquireSRWLockExclusive(&arm_gate);
            SetThreadpoolWait(wait, nullptr, nullptr);
            ReleaseSRWLockExclusive(&arm_gate);
            WaitForThreadpoolWaitCallbacks(wait, TRUE);
            CloseThreadpoolWait(wait);
        }
        if (key) RegCloseKey(key);
        if (event) CloseHandle(event);
    }
    bool Arm() noexcept {
        AcquireSRWLockExclusive(&arm_gate);
        if (stopping.load()) { ReleaseSRWLockExclusive(&arm_gate); return false; }
        const LSTATUS status = RegNotifyChangeKeyValue(key, TRUE,
            REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_CHANGE_SECURITY | REG_NOTIFY_THREAD_AGNOSTIC,
            event, TRUE);
        if (status != ERROR_SUCCESS) { failed.store(true); ReleaseSRWLockExclusive(&arm_gate); return false; }
        SetThreadpoolWait(wait, event, nullptr);
        ReleaseSRWLockExclusive(&arm_gate);
        return true;
    }
    static void CALLBACK Changed(PTP_CALLBACK_INSTANCE, void* context, PTP_WAIT, TP_WAIT_RESULT) noexcept {
        auto* self = static_cast<RegistryWatch*>(context);
        if (self->stopping.load()) return;
        self->Arm();
        self->changed.store(true, std::memory_order_release);
        SetEvent(self->signal);
    }
};
vnotch::HandleRegistry<RegistryWatch> watches;
}

VN_EXPORT int32_t __stdcall vn_registry_watch_create(HKEY hive, const wchar_t* path, int32_t length,
    HANDLE signal, int64_t* out_handle) noexcept {
    if (!path || length <= 0 || !signal || !out_handle) return VN_ERR_INVALID_ARG;
    *out_handle = 0;
    try {
        auto watch = std::make_unique<RegistryWatch>();
        const std::wstring terminated(path, length);
        if (RegOpenKeyExW(hive, terminated.c_str(), 0, KEY_NOTIFY, &watch->key) != ERROR_SUCCESS) return VN_ERR_FAIL;
        watch->signal = signal;
        watch->event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        watch->wait = CreateThreadpoolWait(RegistryWatch::Changed, watch.get(), nullptr);
        if (!watch->event || !watch->wait || !watch->Arm()) return VN_ERR_FAIL;
        *out_handle = watches.Add(std::move(watch));
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t __stdcall vn_registry_watch_consume(int64_t handle, uint8_t* changed) noexcept {
    if (!changed) return VN_ERR_INVALID_ARG;
    auto watch = watches.Acquire(handle);
    if (!watch) return VN_ERR_INVALID_ARG;
    *changed = watch->changed.exchange(false, std::memory_order_acq_rel) ? 1 : 0;
    return watch->failed.load() ? VN_ERR_FAIL : VN_OK;
}
VN_EXPORT int32_t __stdcall vn_registry_watch_close(int64_t handle) noexcept {
    watches.Remove(handle);
    return VN_OK;
}
