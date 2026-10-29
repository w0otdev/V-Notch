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
};

} // namespace vnotch::com