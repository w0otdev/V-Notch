#pragma once
#include "vnotch_common.h"
#include "Platform/DirectoryChangeStamp.h"
#include <deque>
#include <memory>
#include <mutex>
#include <unordered_map>

namespace vnotch::services::localfiles {
struct DirectoryEntry {
    std::wstring name;
    std::u16string normalized;
    DWORD attributes;
};
using DirectoryEntries = std::vector<DirectoryEntry>;

// A short-lived directory cache, not a disk index. Entries contain no query or
// visibility decisions, so exclusions and ranking are evaluated on every query.
class DirectoryCache final {
public:
    struct Slot {
        std::wstring path;
        platform::DirectoryChangeStamp changes;
        // shortcut: uses std::shared_ptr for lock-free cross-thread snapshot reads; replace with generation-indexed arena if cache contention grows.
        std::shared_ptr<const DirectoryEntries> entries;
        uint64_t generation = 0;
        ULONGLONG used = 0;
        size_t bytes = 0;
    };
    struct Ticket {
        std::shared_ptr<Slot> slot;
        std::shared_ptr<const DirectoryEntries> entries;
        uint64_t generation = 0;
    };
    Ticket Read(const std::wstring& path) {
        std::lock_guard guard(lock_);
        auto& slot = slots_[path];
        if (!slot) { slot = std::make_shared<Slot>(); slot->path = path; }
        slot->used = GetTickCount64();
        if (Changed(*slot)) Invalidate(*slot);
        Ticket ticket{slot, slot->entries, slot->generation};
        Trim();
        return ticket;
    }
    void Publish(const Ticket& ticket, std::shared_ptr<DirectoryEntries> entries) {
        if (!ticket.slot || !entries) return;
        std::lock_guard guard(lock_);
        auto& slot = *ticket.slot;
        if (Changed(slot)) Invalidate(slot);
        if (slot.generation != ticket.generation) return;
        // An evicted in-flight walk cannot put itself back into the cache.
        auto found = slots_.find(slot.path);
        if (found == slots_.end() || found->second != ticket.slot) return;
        size_t bytes = sizeof(DirectoryEntries) + entries->capacity() * sizeof(DirectoryEntry);
        for (const auto& entry : *entries) bytes += 2 * (entry.name.capacity() + entry.normalized.capacity());
        bytes_ -= slot.bytes;
        slot.bytes = bytes;
        bytes_ += bytes;
        slot.entries = std::move(entries);
        Trim();
    }
private:
    std::mutex lock_;
    std::unordered_map<std::wstring, std::shared_ptr<Slot>> slots_;
    size_t bytes_ = 0;
    static bool Changed(Slot& slot) noexcept {
        return slot.changes.Changed(slot.path, FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_DIR_NAME |
            FILE_NOTIFY_CHANGE_ATTRIBUTES, false, 3000);
    }
    void Invalidate(Slot& slot) noexcept {
        bytes_ -= slot.bytes;
        slot.bytes = 0;
        slot.entries.reset();
        ++slot.generation;
    }
    void Trim() {
        while (slots_.size() > 512 || bytes_ > 8 * 1024 * 1024) {
            auto oldest = std::min_element(slots_.begin(), slots_.end(), [](const auto& a, const auto& b) {
                return a.second->used < b.second->used;
            });
            bytes_ -= oldest->second->bytes;
            slots_.erase(oldest);
        }
    }
};
inline DirectoryCache directoryCache;

struct Walk {
    std::deque<std::wstring> pending;
    std::wstring directory;
    HANDLE cursor = INVALID_HANDLE_VALUE;
    DirectoryCache::Ticket ticket;
    std::shared_ptr<DirectoryEntries> collected;
    size_t collectedStringBytes = 0;
    size_t cachedIndex = 0;
    DirectoryEntry current;
    WIN32_FIND_DATAW data{};
    bool haveFirst = false;
    explicit Walk(const std::wstring& root) { pending.push_back(root); }
    ~Walk() { if (cursor != INVALID_HANDLE_VALUE) FindClose(cursor); }
    bool Done() const noexcept { return cursor == INVALID_HANDLE_VALUE && !ticket.entries && pending.empty(); }

    // The returned entry stays alive until the next Next() call. Cached names
    // and normalized text are borrowed instead of copied for every query.
    const DirectoryEntry* Next() {
        if (ticket.entries) {
            if (cachedIndex < ticket.entries->size()) return &(*ticket.entries)[cachedIndex++];
            ticket = {};
            return nullptr;
        }
        if (cursor == INVALID_HANDLE_VALUE) {
            if (pending.empty()) return nullptr;
            directory = std::move(pending.front()); pending.pop_front();
            ticket = directoryCache.Read(directory);
            cachedIndex = 0;
            if (ticket.entries) return Next();
            std::wstring pattern = L"\\\\?\\" + directory;
            if (pattern.back() != L'\\') pattern.push_back(L'\\');
            pattern.push_back(L'*');
            cursor = FindFirstFileExW(pattern.c_str(), FindExInfoBasic, &data, FindExSearchNameMatch,
                nullptr, FIND_FIRST_EX_LARGE_FETCH);
            if (cursor == INVALID_HANDLE_VALUE) { ticket = {}; return nullptr; }
            collected = std::make_shared<DirectoryEntries>();
            collectedStringBytes = 0;
            haveFirst = true;
        }
        if (!haveFirst && !FindNextFileW(cursor, &data)) {
            const bool complete = GetLastError() == ERROR_NO_MORE_FILES;
            FindClose(cursor); cursor = INVALID_HANDLE_VALUE;
            if (complete) directoryCache.Publish(ticket, std::move(collected));
            collected.reset(); ticket = {};
            return nullptr;
        }
        haveFirst = false;
        current.name = data.cFileName;
        current.normalized = normalize_search({reinterpret_cast<const char16_t*>(current.name.data()), current.name.size()});
        current.attributes = data.dwFileAttributes;
        if (collected) {
            collected->push_back(current);
            const auto& saved = collected->back();
            collectedStringBytes += sizeof(wchar_t) * saved.name.capacity() + sizeof(char16_t) * saved.normalized.capacity();
            // Bound bytes rather than entry count so ordinary large folders can
            // be reused while typing. Oversized directories remain streaming.
            if (sizeof(DirectoryEntries) + collected->capacity() * sizeof(DirectoryEntry) + collectedStringBytes > 4 * 1024 * 1024)
                collected.reset();
        }
        return &current;
    }
};
}
