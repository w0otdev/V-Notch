#include "../vnotch_common.h"
#include <string_view>

namespace vnotch::services {

class SpotifyJson final {
public:
    static constexpr bool IsDepthWithinLimit(int32_t depth) noexcept {
        return depth <= 16;
    }

    static constexpr bool MatchesPropertyNameIgnoreCase(
        std::u16string_view prop_name,
        std::u16string_view target_name) noexcept {
        return vnotch::vn_equals_icase_ascii(prop_name, target_name);
    }

    static constexpr bool IsNonWhitespaceString(std::u16string_view value) noexcept {
        return !vnotch::vn_trim(value).empty();
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_spotify_json_is_depth_within_limit(
    int32_t depth,
    uint8_t* out_within_limit) noexcept {
    if (!out_within_limit) return VN_E_INVALID_ARG;
    *out_within_limit = vnotch::services::SpotifyJson::IsDepthWithinLimit(depth) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_spotify_json_matches_property_icase(
    const char16_t* prop_name,
    int32_t prop_len,
    const char16_t* target_name,
    int32_t target_len,
    uint8_t* out_matches) noexcept {
    if (!out_matches || prop_len < 0 || target_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view p_sv(prop_name ? prop_name : u"", static_cast<size_t>(prop_len));
    std::u16string_view t_sv(target_name ? target_name : u"", static_cast<size_t>(target_len));
    *out_matches = vnotch::services::SpotifyJson::MatchesPropertyNameIgnoreCase(p_sv, t_sv) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_spotify_json_is_non_whitespace(
    const char16_t* value,
    int32_t value_len,
    uint8_t* out_non_ws) noexcept {
    if (!out_non_ws || value_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(value ? value : u"", static_cast<size_t>(value_len));
    *out_non_ws = vnotch::services::SpotifyJson::IsNonWhitespaceString(sv) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
