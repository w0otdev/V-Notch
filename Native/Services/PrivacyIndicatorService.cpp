#include "../vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <string_view>

namespace vnotch::services {

class PrivacyIndicatorService final {
public:
    static constexpr int64_t kFileTimeOffsetTicks = 504911232000000000LL;
    static constexpr int64_t kMaxFileTime = 2650467743999999999LL;

    static constexpr bool HasMinimumActiveDuration(
        int64_t start_file_time,
        int64_t utc_now_ticks,
        int64_t minimum_ticks) noexcept {
        if (start_file_time <= 0 || start_file_time > kMaxFileTime || minimum_ticks < 0) {
            return false;
        }
        const int64_t start_ticks = start_file_time + kFileTimeOffsetTicks;
        return start_ticks <= utc_now_ticks && (utc_now_ticks - start_ticks) >= minimum_ticks;
    }

    static constexpr bool IsIgnoredMicrophoneProcessName(std::u16string_view process_name) noexcept {
        if (vnotch::vn_equals_icase_ascii(process_name, u"audiodg") ||
            vnotch::vn_equals_icase_ascii(process_name, u"svchost") ||
            vnotch::vn_equals_icase_ascii(process_name, u"system") ||
            vnotch::vn_equals_icase_ascii(process_name, u"registry") ||
            vnotch::vn_equals_icase_ascii(process_name, u"elgato.wavelink")) {
            return true;
        }
        if (vnotch::vn_ends_with_icase_ascii(process_name, u"service") ||
            vnotch::vn_ends_with_icase_ascii(process_name, u"services") ||
            vnotch::vn_ends_with_icase_ascii(process_name, u"svc") ||
            vnotch::vn_ends_with_icase_ascii(process_name, u"daemon")) {
            return true;
        }
        return false;
    }

    static constexpr void NormalizeAppNameSlice(
        std::u16string_view raw,
        int32_t& out_start,
        int32_t& out_len) noexcept {
        out_start = 0;
        out_len = static_cast<int32_t>(raw.size());
        if (vnotch::vn_trim(raw).empty()) return;

        const size_t last_hash = raw.rfind(u'#');
        if (last_hash != std::u16string_view::npos) {
            std::u16string_view last_part = raw.substr(last_hash + 1);
            if (!vnotch::vn_trim(last_part).empty()) {
                out_start = static_cast<int32_t>(last_hash + 1);
                out_len = static_cast<int32_t>(last_part.size());
                return;
            }
        }

        const size_t underscore = raw.find(u'_');
        if (underscore != std::u16string_view::npos && underscore > 0) {
            out_start = 0;
            out_len = static_cast<int32_t>(underscore);
        }
    }

    static bool EvaluateMicrophoneActivityGate(
        bool has_candidate,
        bool has_active_session,
        float peak_level,
        float signal_threshold,
        int64_t hold_duration_ticks,
        int64_t utc_now_ticks,
        int64_t& inout_last_signal_utc_ticks) noexcept {
        if (!has_candidate || !has_active_session) {
            inout_last_signal_utc_ticks = 0;
            return false;
        }

        if (std::isfinite(peak_level) && peak_level >= signal_threshold) {
            inout_last_signal_utc_ticks = utc_now_ticks;
        }

        if (inout_last_signal_utc_ticks == 0 || utc_now_ticks < inout_last_signal_utc_ticks) {
            return false;
        }

        return (utc_now_ticks - inout_last_signal_utc_ticks) <= hold_duration_ticks;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_privacy_indicator_has_min_active_duration(
    int64_t start_file_time,
    int64_t utc_now_ticks,
    int64_t minimum_ticks,
    uint8_t* out_active) noexcept {
    if (!out_active) return VN_E_INVALID_ARG;
    *out_active = vnotch::services::PrivacyIndicatorService::HasMinimumActiveDuration(
        start_file_time, utc_now_ticks, minimum_ticks) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_privacy_indicator_is_ignored_mic_process(
    const char16_t* process_name,
    int32_t process_name_len,
    uint8_t* out_ignored) noexcept {
    if (!out_ignored || process_name_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(process_name ? process_name : u"", static_cast<size_t>(process_name_len));
    *out_ignored = vnotch::services::PrivacyIndicatorService::IsIgnoredMicrophoneProcessName(sv) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_privacy_indicator_normalize_app_name_slice(
    const char16_t* raw,
    int32_t raw_len,
    int32_t* out_start,
    int32_t* out_len) noexcept {
    if (!out_start || !out_len || raw_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(raw ? raw : u"", static_cast<size_t>(raw_len));
    vnotch::services::PrivacyIndicatorService::NormalizeAppNameSlice(sv, *out_start, *out_len);
    return VN_OK;
}

VN_EXPORT int32_t vn_privacy_indicator_evaluate_mic_gate(
    uint8_t has_candidate,
    uint8_t has_active_session,
    float peak_level,
    float signal_threshold,
    int64_t hold_duration_ticks,
    int64_t utc_now_ticks,
    int64_t* inout_last_signal_utc_ticks,
    uint8_t* out_active) noexcept {
    if (!inout_last_signal_utc_ticks || !out_active) return VN_E_INVALID_ARG;
    *out_active = vnotch::services::PrivacyIndicatorService::EvaluateMicrophoneActivityGate(
        has_candidate != 0,
        has_active_session != 0,
        peak_level,
        signal_threshold,
        hold_duration_ticks,
        utc_now_ticks,
        *inout_last_signal_utc_ticks) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
