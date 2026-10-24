#include "../vnotch_common.h"
#include <string_view>

namespace vnotch::services {

class VideoIdCache final {
public:
    static constexpr bool ShouldEvictOldest(int32_t current_count, int32_t capacity, int32_t order_count) noexcept {
        return current_count >= capacity && order_count > 0;
    }

    static constexpr bool ShouldEvictEntry(std::u16string_view cached_video_id, std::u16string_view stale_video_id) noexcept {
        return cached_video_id == stale_video_id;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_video_id_cache_should_evict_oldest(
    int32_t current_count,
    int32_t capacity,
    int32_t order_count,
    uint8_t* out_evict) noexcept {
    if (!out_evict) return VN_E_INVALID_ARG;
    *out_evict = vnotch::services::VideoIdCache::ShouldEvictOldest(current_count, capacity, order_count) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_video_id_cache_should_evict_entry(
    const char16_t* cached_video_id,
    int32_t cached_len,
    const char16_t* stale_video_id,
    int32_t stale_len,
    uint8_t* out_evict) noexcept {
    if (!out_evict || cached_len < 0 || stale_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view cached_sv(cached_video_id ? cached_video_id : u"", static_cast<size_t>(cached_len));
    std::u16string_view stale_sv(stale_video_id ? stale_video_id : u"", static_cast<size_t>(stale_len));
    *out_evict = vnotch::services::VideoIdCache::ShouldEvictEntry(cached_sv, stale_sv) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
