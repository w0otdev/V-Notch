#include "vnotch_common.h"
#include <cstdint>

namespace vnotch::services::clipboard {

class ClipboardSearchTextCache final {
public:
    ClipboardSearchTextCache() = delete;

    [[nodiscard]] static constexpr bool CanStore(
        int32_t text_length,
        int32_t max_characters,
        int32_t max_entries) noexcept {
        return text_length >= 0 && text_length <= max_characters && max_entries > 0;
    }

    [[nodiscard]] static constexpr bool ShouldEvictOldest(
        int32_t current_characters,
        int32_t current_entries,
        int32_t text_length,
        int32_t max_characters,
        int32_t max_entries) noexcept {
        return current_characters > max_characters - text_length || current_entries >= max_entries;
    }
};

} // namespace vnotch::services::clipboard

extern "C" {

VN_EXPORT int32_t vn_clipboard_search_cache_can_store(
    int32_t text_length,
    int32_t max_characters,
    int32_t max_entries,
    uint8_t* out_can_store) noexcept {
    if (!out_can_store) return VN_E_INVALID_ARG;
    *out_can_store = vnotch::services::clipboard::ClipboardSearchTextCache::CanStore(
        text_length, max_characters, max_entries) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_clipboard_search_cache_should_evict_oldest(
    int32_t current_characters,
    int32_t current_entries,
    int32_t text_length,
    int32_t max_characters,
    int32_t max_entries,
    uint8_t* out_should_evict) noexcept {
    if (!out_should_evict) return VN_E_INVALID_ARG;
    *out_should_evict = vnotch::services::clipboard::ClipboardSearchTextCache::ShouldEvictOldest(
        current_characters, current_entries, text_length, max_characters, max_entries) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
