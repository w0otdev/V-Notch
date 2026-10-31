#pragma once

#include "vnotch_native.h"
#include <objbase.h>

// Small COM helpers shared by the Core Audio translation units.

namespace vnotch::com {

struct Guard {
    SRWLOCK* lock;
    explicit Guard(SRWLOCK* l) noexcept : lock(l) { AcquireSRWLockExclusive(lock); }
    Guard(const Guard&) = delete;
    Guard& operator=(const Guard&) = delete;
    ~Guard() { ReleaseSRWLockExclusive(lock); }
};

inline INIT_ONCE g_mta_once = INIT_ONCE_STATIC_INIT;

inline BOOL CALLBACK KeepMtaAlive(PINIT_ONCE, PVOID, PVOID*) noexcept {
    // Threads without their own COM initialization then count as implicit MTA members.
    CO_MTA_USAGE_COOKIE cookie = nullptr;
    (void)CoIncrementMTAUsage(&cookie);
    return TRUE;
}

inline void EnsureMta() noexcept {
    InitOnceExecuteOnce(&g_mta_once, KeepMtaAlive, nullptr, nullptr);
}

template <typename T>
struct ComPtr {
    T* ptr = nullptr;
    ComPtr() = default;
    ComPtr(const ComPtr&) = delete;
    ComPtr& operator=(const ComPtr&) = delete;
    ~ComPtr() { Reset(); }
    void Reset() noexcept {
        if (ptr) {
            ptr->Release();
            ptr = nullptr;
        }
    }
    T** Put() noexcept {
        Reset();
        return &ptr;
    }
    T** put() noexcept { return Put(); }
    T* get() const noexcept { return ptr; }
    T* operator->() const noexcept { return ptr; }
    explicit operator bool() const noexcept { return ptr != nullptr; }
};

struct ComScope {
    bool initialized;
    ComScope() noexcept : initialized(SUCCEEDED(::CoInitializeEx(nullptr, COINIT_MULTITHREADED))) {}
    ComScope(const ComScope&) = delete;
    ComScope& operator=(const ComScope&) = delete;
    ~ComScope() noexcept {
        if (initialized) {
            ::CoUninitialize();
        }
    }
};

struct UniqueHandle {
    HANDLE h = nullptr;
    UniqueHandle() = default;
    explicit UniqueHandle(HANDLE handle) noexcept : h(handle) {}
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;
    ~UniqueHandle() noexcept {
        if (valid()) {
            ::CloseHandle(h);
        }
    }
    bool valid() const noexcept {
        return h != nullptr && h != INVALID_HANDLE_VALUE;
    }
    HANDLE get() const noexcept { return h; }
};

} // namespace vnotch::com

namespace vnotch {
using com::ComPtr;
using com::ComScope;
using com::UniqueHandle;
} // namespace vnotch