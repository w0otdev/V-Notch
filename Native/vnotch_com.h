#pragma once

#include "vnotch_native.h"
#include <objbase.h>
#include <utility>

// Small COM helpers shared by the Core Audio translation units.

namespace vnotch::com {

struct SrwExclusiveGuard {
    SRWLOCK* lock;
    explicit SrwExclusiveGuard(SRWLOCK* l) noexcept : lock(l) { AcquireSRWLockExclusive(lock); }
    SrwExclusiveGuard(const SrwExclusiveGuard&) = delete;
    SrwExclusiveGuard& operator=(const SrwExclusiveGuard&) = delete;
    SrwExclusiveGuard(SrwExclusiveGuard&&) = delete;
    SrwExclusiveGuard& operator=(SrwExclusiveGuard&&) = delete;
    ~SrwExclusiveGuard() { ReleaseSRWLockExclusive(lock); }
};
using Guard = SrwExclusiveGuard;

struct SrwSharedGuard {
    SRWLOCK* lock;
    explicit SrwSharedGuard(SRWLOCK* l) noexcept : lock(l) { AcquireSRWLockShared(lock); }
    SrwSharedGuard(const SrwSharedGuard&) = delete;
    SrwSharedGuard& operator=(const SrwSharedGuard&) = delete;
    SrwSharedGuard(SrwSharedGuard&&) = delete;
    SrwSharedGuard& operator=(SrwSharedGuard&&) = delete;
    ~SrwSharedGuard() { ReleaseSRWLockShared(lock); }
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
    ComPtr(ComPtr&& other) noexcept : ptr(std::exchange(other.ptr, nullptr)) {}
    ComPtr& operator=(ComPtr&& other) noexcept {
        if (this != &other) {
            Reset();
            ptr = std::exchange(other.ptr, nullptr);
        }
        return *this;
    }
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
    // Takes ownership of an already-AddRef'd pointer; releases any previous reference.
    void Attach(T* owned) noexcept {
        Reset();
        ptr = owned;
    }
    T* Detach() noexcept {
        return std::exchange(ptr, nullptr);
    }
    // Takes a new reference on a borrowed pointer; the previous reference (if any) is released.
    void Assign(T* borrowed) noexcept {
        if (borrowed) borrowed->AddRef();
        Reset();
        ptr = borrowed;
    }
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
    UniqueHandle(UniqueHandle&& other) noexcept : h(std::exchange(other.h, nullptr)) {}
    UniqueHandle& operator=(UniqueHandle&& other) noexcept {
        if (this != &other) {
            if (valid()) ::CloseHandle(h);
            h = std::exchange(other.h, nullptr);
        }
        return *this;
    }
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
