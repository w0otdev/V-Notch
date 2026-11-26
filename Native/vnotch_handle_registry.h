#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>

#include <cstddef>
#include <cstdint>
#include <memory>
#include <new>
#include <stdexcept>
#include <type_traits>
#include <utility>
#include <vector>

namespace vnotch
{
    // Handle table for objects that native exports hand to managed code as an opaque int64.
    //
    // Lifetime contract:
    //  - A handle packs (generation << 32) | (slot index + 1). Closing a handle bumps the slot generation, so a
    //    stale handle never resolves to a different object that later reuses the slot.
    //  - Acquire() returns a Lease that keeps the object alive for as long as the lease lives. Holding the lock
    //    only while looking the object up is not enough: the pointer would outlive the lock.
    //  - Remove() unpublishes the handle immediately. The object is destroyed when the last lease is released,
    //    or right away when none is outstanding. Destruction never runs under the registry lock.
    template <typename T>
    class HandleRegistry
    {
        static constexpr uint32_t kNoFreeSlot = 0xFFFFFFFFu;
        static constexpr size_t kMaxSlots = 0xFFFFFFFEu;

        struct Slot
        {
            std::unique_ptr<T> object;
            uint32_t generation = 1;
            int32_t leases = 0;
            uint32_t next_free = kNoFreeSlot;
            bool retired = false;
        };

        struct LockGuard
        {
            SRWLOCK* lock;
            explicit LockGuard(SRWLOCK* l) noexcept : lock(l) { AcquireSRWLockExclusive(lock); }
            LockGuard(const LockGuard&) = delete;
            LockGuard& operator=(const LockGuard&) = delete;
            ~LockGuard() { ReleaseSRWLockExclusive(lock); }
        };

        static constexpr uint32_t kGenerationMask = 0x7FFFFFFFu;

        static constexpr uint32_t NextGeneration(uint32_t generation) noexcept
        {
            const uint32_t next = (generation + 1u) & kGenerationMask;
            return next == 0u ? 1u : next;
        }

        static constexpr int64_t Pack(uint32_t generation, size_t index) noexcept
        {
            return (static_cast<int64_t>(generation) << 32) | static_cast<int64_t>(index + 1u);
        }

        // Resolves a handle to a live slot index. Caller holds the lock.
        bool ResolveLocked(int64_t handle, size_t& index) const noexcept
        {
            if (handle <= 0)
            {
                return false;
            }
            const uint32_t low = static_cast<uint32_t>(static_cast<uint64_t>(handle) & 0xFFFFFFFFull);
            const uint32_t generation = static_cast<uint32_t>(static_cast<uint64_t>(handle) >> 32);
            if (low == 0u || static_cast<size_t>(low) > slots_.size())
            {
                return false;
            }
            index = static_cast<size_t>(low) - 1u;
            const Slot& slot = slots_[index];
            return slot.object != nullptr && !slot.retired && slot.generation == generation;
        }

        void Release(size_t index) noexcept
        {
            std::unique_ptr<T> doomed;
            {
                LockGuard guard(&lock_);
                Slot& slot = slots_[index];
                if (--slot.leases == 0 && slot.retired)
                {
                    doomed = std::move(slot.object);
                    slot.retired = false;
                    slot.generation = NextGeneration(slot.generation);
                    slot.next_free = free_head_;
                    free_head_ = static_cast<uint32_t>(index);
                }
            }
        }

    public:
        class Lease
        {
        public:
            Lease() noexcept = default;
            Lease(const Lease&) = delete;
            Lease& operator=(const Lease&) = delete;
            Lease(Lease&& other) noexcept
                : registry_(std::exchange(other.registry_, nullptr)), index_(other.index_), object_(std::exchange(other.object_, nullptr))
            {
            }
            Lease& operator=(Lease&& other) noexcept
            {
                if (this != &other)
                {
                    Reset();
                    registry_ = std::exchange(other.registry_, nullptr);
                    index_ = other.index_;
                    object_ = std::exchange(other.object_, nullptr);
                }
                return *this;
            }
            ~Lease() { Reset(); }

            T* get() const noexcept { return object_; }
            T* operator->() const noexcept { return object_; }
            explicit operator bool() const noexcept { return object_ != nullptr; }

            void Reset() noexcept
            {
                if (registry_ != nullptr)
                {
                    registry_->Release(index_);
                    registry_ = nullptr;
                    object_ = nullptr;
                }
            }

        private:
            friend class HandleRegistry;
            Lease(HandleRegistry* registry, size_t index, T* object) noexcept : registry_(registry), index_(index), object_(object) {}

            HandleRegistry* registry_ = nullptr;
            size_t index_ = 0;
            T* object_ = nullptr;
        };

        HandleRegistry() noexcept = default;
        HandleRegistry(const HandleRegistry&) = delete;
        HandleRegistry& operator=(const HandleRegistry&) = delete;

        // Stores the object and returns its handle. May throw std::bad_alloc or std::length_error.
        int64_t Add(std::unique_ptr<T> item)
        {
            LockGuard guard(&lock_);
            if (free_head_ != kNoFreeSlot)
            {
                const size_t index = static_cast<size_t>(free_head_);
                Slot& slot = slots_[index];
                free_head_ = slot.next_free;
                slot.next_free = kNoFreeSlot;
                slot.object = std::move(item);
                return Pack(slot.generation, index);
            }
            if (slots_.size() >= kMaxSlots)
            {
                throw std::length_error("HandleRegistry capacity exceeded");
            }
            slots_.emplace_back();
            Slot& slot = slots_.back();
            slot.object = std::move(item);
            return Pack(slot.generation, slots_.size() - 1u);
        }

        // Empty lease when the handle is invalid, stale or already removed.
        Lease Acquire(int64_t handle) noexcept
        {
            LockGuard guard(&lock_);
            size_t index = 0;
            if (!ResolveLocked(handle, index))
            {
                return Lease();
            }
            Slot& slot = slots_[index];
            ++slot.leases;
            return Lease(this, index, slot.object.get());
        }

        // Unpublishes the handle. Returns false when it did not resolve. When extracted is given and no lease is
        // outstanding, ownership moves to the caller (who destroys it outside the lock); otherwise the object is
        // destroyed here, or by the last lease holder.
        bool Remove(int64_t handle, std::unique_ptr<T>* extracted = nullptr) noexcept
        {
            std::unique_ptr<T> doomed;
            {
                LockGuard guard(&lock_);
                size_t index = 0;
                if (!ResolveLocked(handle, index))
                {
                    return false;
                }
                Slot& slot = slots_[index];
                slot.retired = true;
                if (slot.leases == 0)
                {
                    doomed = std::move(slot.object);
                    slot.retired = false;
                    slot.generation = NextGeneration(slot.generation);
                    slot.next_free = free_head_;
                    free_head_ = static_cast<uint32_t>(index);
                }
            }
            if (extracted != nullptr)
            {
                *extracted = std::move(doomed);
            }
            return true;
        }

    private:
        SRWLOCK lock_ = SRWLOCK_INIT;
        std::vector<Slot> slots_;
        uint32_t free_head_ = kNoFreeSlot;
    };

    // Process-lifetime handle registry whose storage is intentionally never destroyed on DLL_PROCESS_DETACH.
    // Destroying HandleRegistry<T> at process exit runs ~T() (thread joins, COM Release, FreeLibrary) under OS Loader Lock.
    template <typename T>
    class ImmortalHandleRegistry
    {
    public:
        using Lease = typename HandleRegistry<T>::Lease;

        ImmortalHandleRegistry() noexcept
        {
            ::new (static_cast<void*>(storage_)) HandleRegistry<T>();
        }
        ~ImmortalHandleRegistry() = default;
        ImmortalHandleRegistry(const ImmortalHandleRegistry&) = delete;
        ImmortalHandleRegistry& operator=(const ImmortalHandleRegistry&) = delete;

        int64_t Add(std::unique_ptr<T> item) { return Registry().Add(std::move(item)); }
        Lease Acquire(int64_t handle) noexcept { return Registry().Acquire(handle); }
        bool Remove(int64_t handle, std::unique_ptr<T>* extracted = nullptr) noexcept
        {
            return Registry().Remove(handle, extracted);
        }

    private:
        HandleRegistry<T>& Registry() noexcept
        {
            return *std::launder(reinterpret_cast<HandleRegistry<T>*>(storage_));
        }

        alignas(HandleRegistry<T>) std::byte storage_[sizeof(HandleRegistry<T>)]{};
    };

    static_assert(std::is_trivially_destructible_v<ImmortalHandleRegistry<int>>);
}
