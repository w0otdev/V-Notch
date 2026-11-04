#include "vnotch_common.h"
#include "vnotch_handle_registry.h"
#include <array>
#include <stdexcept>

namespace {
struct Key { uint64_t a, b; bool operator==(const Key&) const = default; };
uint64_t hash_bytes(const void* data, size_t length) noexcept {
    auto bytes = static_cast<const uint8_t*>(data); uint64_t hash = 14695981039346656037ULL;
    for (size_t i = 0; i < length; ++i) { hash ^= bytes[i]; hash *= 1099511628211ULL; }
    return hash;
}
struct Row {
    Key id{}; std::u16string fingerprint;
    int64_t ticks = 0; uint64_t fingerprint_hash = 0;
    int32_t slot = -1, guid_next = -1, fingerprint_next = -1;
    bool personal = false;
};
struct Index {
    SRWLOCK mutex = SRWLOCK_INIT;
    std::vector<Row> rows;
    std::vector<int32_t> guid_buckets = std::vector<int32_t>(1024, -1), fingerprint_buckets = std::vector<int32_t>(1024, -1);
    int32_t free_head = -1;
    size_t count = 0;
    ~Index() { for (auto& row : rows) wipe(row); }
    static void wipe(Row& row) noexcept {
        if (!row.fingerprint.empty()) SecureZeroMemory(row.fingerprint.data(), row.fingerprint.size() * sizeof(char16_t));
        row.fingerprint.clear();
        row.fingerprint.shrink_to_fit();
    }
    size_t guid_bucket(Key id) const { return hash_bytes(&id, sizeof(id)) & (guid_buckets.size() - 1); }
    size_t fingerprint_bucket(uint64_t hash) const { return hash & (fingerprint_buckets.size() - 1); }
    int32_t find(Key id) const {
        for (auto at = guid_buckets[guid_bucket(id)]; at >= 0; at = rows[at].guid_next) if (rows[at].id == id) return at;
        return -1;
    }
    void unlink_fingerprint(int32_t at) {
        auto* link = &fingerprint_buckets[fingerprint_bucket(rows[at].fingerprint_hash)];
        while (*link >= 0 && *link != at) link = &rows[*link].fingerprint_next;
        if (*link >= 0) *link = rows[at].fingerprint_next;
    }
    void grow() {
        if (count < guid_buckets.size()) return;
        // Allocate both tables before publishing either, so allocation failure keeps the index usable.
        std::vector<int32_t> guid(guid_buckets.size() * 2, -1), fingerprints(guid.size(), -1);
        guid_buckets.swap(guid); fingerprint_buckets.swap(fingerprints);
        for (int32_t i = 0; static_cast<size_t>(i) < rows.size(); ++i) {
            auto& row = rows[i]; if (row.slot < 0) continue;
            auto g = guid_bucket(row.id), f = fingerprint_bucket(row.fingerprint_hash);
            row.guid_next = guid_buckets[g]; guid_buckets[g] = i;
            row.fingerprint_next = fingerprint_buckets[f]; fingerprint_buckets[f] = i;
        }
    }
};
vnotch::HandleRegistry<Index> indices;
struct Guard { SRWLOCK* mutex; Guard(SRWLOCK* value) : mutex(value) { AcquireSRWLockExclusive(mutex); } ~Guard() { ReleaseSRWLockExclusive(mutex); } };
}

VN_EXPORT int32_t vn_clipboard_index_create(int64_t* handle) noexcept {
    if (!handle) return VN_ERR_INVALID_ARG;
    *handle = 0;
    try { *handle = indices.Add(std::make_unique<Index>()); return VN_OK; } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t vn_clipboard_index_close(int64_t handle) noexcept { return indices.Remove(handle) ? VN_OK : VN_ERR_INVALID_ARG; }
VN_EXPORT int32_t vn_clipboard_index_put(int64_t handle, uint64_t a, uint64_t b, int32_t slot,
    const char16_t* fingerprint, int32_t length, int64_t ticks, uint8_t personal) noexcept {
    if (slot < 0 || length < 0 || length > 2 * 1024 * 1024 || (!fingerprint && length)) return VN_ERR_INVALID_ARG;
    try {
        auto owner = indices.Acquire(handle); if (!owner) return VN_ERR_INVALID_ARG;
        Guard guard(&owner->mutex); auto& index = *owner.get();
        std::u16string text(vnotch::make_u16_view(fingerprint, length));
        const auto hash = hash_bytes(text.data(), text.size() * sizeof(char16_t)) ^ personal;
        Key id{a, b}; auto at = index.find(id);
        if (at < 0) {
            index.grow();
            if (index.free_head < 0) { at = static_cast<int32_t>(index.rows.size()); index.rows.emplace_back(); }
            else { at = index.free_head; index.free_head = index.rows[at].guid_next; }
            auto bucket = index.guid_bucket(id);
            index.rows[at].guid_next = index.guid_buckets[bucket]; index.guid_buckets[bucket] = at; ++index.count;
        } else index.unlink_fingerprint(at);
        auto& row = index.rows[at]; Index::wipe(row);
        row.id = id; row.slot = slot; row.fingerprint = std::move(text); row.fingerprint_hash = hash; row.ticks = ticks; row.personal = personal != 0;
        const auto bucket = index.fingerprint_bucket(hash);
        row.fingerprint_next = index.fingerprint_buckets[bucket]; index.fingerprint_buckets[bucket] = at;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t vn_clipboard_index_find(int64_t handle, uint64_t a, uint64_t b, int32_t* slot) noexcept {
    if (!slot) return VN_ERR_INVALID_ARG; *slot = -1;
    auto owner = indices.Acquire(handle); if (!owner) return VN_ERR_INVALID_ARG;
    Guard guard(&owner->mutex); auto at = owner->find({a,b}); if (at >= 0) *slot = owner->rows[at].slot;
    return VN_OK;
}
VN_EXPORT int32_t vn_clipboard_index_remove(int64_t handle, uint64_t a, uint64_t b) noexcept {
    try {
        auto owner = indices.Acquire(handle); if (!owner) return VN_ERR_INVALID_ARG;
        Guard guard(&owner->mutex); auto& index = *owner.get();
        auto at = index.find({a,b}); if (at < 0) return VN_OK;
        index.unlink_fingerprint(at);
        auto* link = &index.guid_buckets[index.guid_bucket({a,b})];
        while (*link != at) link = &index.rows[*link].guid_next;
        *link = index.rows[at].guid_next;
        Index::wipe(index.rows[at]); index.rows[at].slot = -1;
        index.rows[at].guid_next = index.free_head; index.free_head = at;
        if (--index.count == 0) {
            index.rows.clear();
            index.rows.shrink_to_fit();
            index.free_head = -1;
            if (index.guid_buckets.size() > 1024) {
                index.guid_buckets.assign(1024, -1);
                index.guid_buckets.shrink_to_fit();
                index.fingerprint_buckets.assign(1024, -1);
                index.fingerprint_buckets.shrink_to_fit();
            } else {
                std::fill(index.guid_buckets.begin(), index.guid_buckets.end(), -1);
                std::fill(index.fingerprint_buckets.begin(), index.fingerprint_buckets.end(), -1);
            }
        }
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t vn_clipboard_index_duplicate(int64_t handle, const char16_t* fingerprint, int32_t length, uint8_t personal, int32_t* slot) noexcept {
    if (!slot || length < 0 || (!fingerprint && length)) return VN_ERR_INVALID_ARG; *slot = -1;
    auto owner = indices.Acquire(handle); if (!owner) return VN_ERR_INVALID_ARG;
    Guard guard(&owner->mutex);
    auto text = vnotch::make_u16_view(fingerprint, length);
    auto hash = hash_bytes(text.data(), text.size() * sizeof(char16_t)) ^ personal;
    int64_t newest = INT64_MIN;
    for (auto at = owner->fingerprint_buckets[owner->fingerprint_bucket(hash)]; at >= 0; at = owner->rows[at].fingerprint_next) {
        auto& row = owner->rows[at];
        if (row.personal == (personal != 0) && row.fingerprint == text && (*slot < 0 || row.ticks > newest)) { *slot = row.slot; newest = row.ticks; }
    }
    return VN_OK;
}
