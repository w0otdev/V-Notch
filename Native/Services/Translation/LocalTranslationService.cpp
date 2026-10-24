#include "vnotch_common.h"
#include <cstdint>

namespace vnotch::services::translation {

class LocalTranslationService final {
public:
    LocalTranslationService() = delete;

    [[nodiscard]] static constexpr bool ShouldCacheResult(
        bool literals_preserved,
        bool weekdays_preserved,
        int32_t cache_entries) noexcept {
        return literals_preserved && weekdays_preserved && cache_entries > 0;
    }

    [[nodiscard]] static constexpr bool IsCacheEntryExpired(
        int64_t expires_utc_ticks,
        int64_t now_utc_ticks) noexcept {
        return expires_utc_ticks <= now_utc_ticks;
    }
};

} // namespace vnotch::services::translation

extern "C" {

VN_EXPORT int32_t vn_local_translation_should_cache_result(
    uint8_t literals_preserved,
    uint8_t weekdays_preserved,
    int32_t cache_entries,
    uint8_t* out_should_cache) noexcept {
    if (!out_should_cache) return VN_E_INVALID_ARG;
    *out_should_cache = vnotch::services::translation::LocalTranslationService::ShouldCacheResult(
        literals_preserved != 0, weekdays_preserved != 0, cache_entries) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_local_translation_is_cache_entry_expired(
    int64_t expires_utc_ticks,
    int64_t now_utc_ticks,
    uint8_t* out_expired) noexcept {
    if (!out_expired) return VN_E_INVALID_ARG;
    *out_expired = vnotch::services::translation::LocalTranslationService::IsCacheEntryExpired(
        expires_utc_ticks, now_utc_ticks) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
