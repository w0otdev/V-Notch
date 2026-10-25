#include "../vnotch_common.h"
#include <string_view>

namespace vnotch::services {

class MediaSourceCache final {
public:
    static constexpr int32_t ResolveCapacity(int32_t capacity, int32_t default_capacity) noexcept {
        return capacity > 0 ? capacity : default_capacity;
    }

    static constexpr bool MatchesExpectedSource(std::u16string_view cached_source, std::u16string_view expected_source) noexcept {
        return vnotch::vn_equals_icase_ascii(cached_source, expected_source);
    }

    static constexpr bool ShouldEvict(int32_t current_count, int32_t capacity, bool has_last) noexcept {
        return current_count > capacity && has_last;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_media_source_cache_resolve_capacity(
    int32_t capacity,
    int32_t default_capacity,
    int32_t* out_capacity) noexcept {
    if (!out_capacity) return VN_E_INVALID_ARG;
    *out_capacity = vnotch::services::MediaSourceCache::ResolveCapacity(capacity, default_capacity);
    return VN_OK;
}

VN_EXPORT int32_t vn_media_source_cache_matches_source(
    const char16_t* cached_source,
    int32_t cached_len,
    const char16_t* expected_source,
    int32_t expected_len,
    uint8_t* out_matches) noexcept {
    std::u16string_view cached_sv;
    std::u16string_view expected_sv;
    if (!out_matches || !vnotch::try_make_u16_view(cached_source, cached_len, cached_sv) ||
        !vnotch::try_make_u16_view(expected_source, expected_len, expected_sv)) return VN_E_INVALID_ARG;
    *out_matches = vnotch::services::MediaSourceCache::MatchesExpectedSource(cached_sv, expected_sv) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_source_cache_should_evict(
    int32_t current_count,
    int32_t capacity,
    uint8_t has_last,
    uint8_t* out_evict) noexcept {
    if (!out_evict) return VN_E_INVALID_ARG;
    *out_evict = vnotch::services::MediaSourceCache::ShouldEvict(current_count, capacity, has_last != 0) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
