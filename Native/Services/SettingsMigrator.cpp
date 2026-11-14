#include "../vnotch_common.h"
#include <algorithm>
#include <cmath>

namespace vnotch::services {

class SettingsMigrator final {
public:
    static int32_t RoundToEven(double value) noexcept {
        const double fl = std::floor(value);
        const double diff = value - fl;
        if (diff > 0.5) return static_cast<int32_t>(fl + 1.0);
        if (diff < 0.5) return static_cast<int32_t>(fl);
        const int64_t fl_int = static_cast<int64_t>(fl);
        return (fl_int % 2 == 0) ? static_cast<int32_t>(fl_int) : static_cast<int32_t>(fl_int + 1);
    }

    static int32_t ComputeDynamicIslandWidthFromV3(int32_t width) noexcept {
        return RoundToEven(static_cast<double>(width) * 1.12 / 10.0) * 10;
    }

    static constexpr int32_t ClampAnimationFps(int32_t parsed_fps, int32_t min_fps, int32_t max_fps) noexcept {
        return std::clamp(parsed_fps, min_fps, max_fps);
    }

    static constexpr bool ShouldRevokeSpotifyCanvasConsent(
        bool has_accepted,
        bool enable_canvas,
        bool allow_online,
        int32_t consent_version) noexcept {
        return !has_accepted && (enable_canvas || allow_online || consent_version != 0);
    }

    static constexpr wchar_t AsciiToLower(wchar_t ch) noexcept {
        return (ch >= L'A' && ch <= L'Z') ? static_cast<wchar_t>(ch + (L'a' - L'A')) : ch;
    }

    static constexpr std::wstring_view TrimAsciiWhitespace(std::wstring_view sv) noexcept {
        while (!sv.empty() && vnotch::is_unicode_whitespace(static_cast<char16_t>(sv.front()))) {
            sv.remove_prefix(1);
        }
        while (!sv.empty() && vnotch::is_unicode_whitespace(static_cast<char16_t>(sv.back()))) {
            sv.remove_suffix(1);
        }
        return sv;
    }

    static constexpr bool AsciiEqualsIgnoreCase(std::wstring_view a, std::wstring_view b) noexcept {
        if (a.size() != b.size()) return false;
        for (size_t i = 0; i < a.size(); ++i) {
            if (AsciiToLower(a[i]) != AsciiToLower(b[i])) return false;
        }
        return true;
    }

    static int32_t AppendCsvTokenIfMissing(
        std::wstring_view csv,
        std::wstring_view raw_token,
        wchar_t* out_buf,
        int32_t out_cap,
        int32_t* out_len) noexcept {
        const std::wstring_view token = TrimAsciiWhitespace(raw_token);
        if (token.empty()) return VN_ERR_INVALID_ARG;

        size_t pos = 0;
        while (pos <= csv.size()) {
            const size_t comma = csv.find(L',', pos);
            const size_t end = (comma == std::wstring_view::npos) ? csv.size() : comma;
            const std::wstring_view item = TrimAsciiWhitespace(csv.substr(pos, end - pos));
            if (AsciiEqualsIgnoreCase(item, token)) {
                const int32_t len = static_cast<int32_t>(csv.size());
                if (len > out_cap) return VN_ERR_BUFFER_TOO_SMALL;
                if (len > 0) {
                    std::char_traits<wchar_t>::copy(out_buf, csv.data(), csv.size());
                }
                *out_len = len;
                return VN_OK;
            }
            if (comma == std::wstring_view::npos) break;
            pos = comma + 1;
        }

        std::wstring_view trimmed_csv = TrimAsciiWhitespace(csv);
        while (!trimmed_csv.empty() && (trimmed_csv.back() == L',' || vnotch::is_unicode_whitespace(static_cast<char16_t>(trimmed_csv.back())))) {
            trimmed_csv.remove_suffix(1);
        }

        if (trimmed_csv.empty()) {
            const int32_t req = static_cast<int32_t>(token.size());
            if (req > out_cap) return VN_ERR_BUFFER_TOO_SMALL;
            std::char_traits<wchar_t>::copy(out_buf, token.data(), token.size());
            *out_len = req;
            return VN_OK;
        }

        const int32_t req = static_cast<int32_t>(trimmed_csv.size() + 1 + token.size());
        if (req > out_cap) return VN_ERR_BUFFER_TOO_SMALL;
        std::char_traits<wchar_t>::copy(out_buf, trimmed_csv.data(), trimmed_csv.size());
        out_buf[trimmed_csv.size()] = L',';
        std::char_traits<wchar_t>::copy(out_buf + trimmed_csv.size() + 1, token.data(), token.size());
        *out_len = req;
        return VN_OK;
    }

};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_settings_migrator_compute_dynamic_island_width_v3(
    int32_t width,
    int32_t* out_width) noexcept {
    if (!out_width) return VN_E_INVALID_ARG;
    *out_width = vnotch::services::SettingsMigrator::ComputeDynamicIslandWidthFromV3(width);
    return VN_OK;
}

VN_EXPORT int32_t vn_settings_migrator_clamp_animation_fps(
    int32_t parsed_fps,
    int32_t min_fps,
    int32_t max_fps,
    int32_t* out_fps) noexcept {
    if (!out_fps || min_fps > max_fps) return VN_E_INVALID_ARG;
    *out_fps = vnotch::services::SettingsMigrator::ClampAnimationFps(parsed_fps, min_fps, max_fps);
    return VN_OK;
}

VN_EXPORT int32_t vn_settings_migrator_should_revoke_canvas_consent(
    uint8_t has_accepted,
    uint8_t enable_canvas,
    uint8_t allow_online,
    int32_t consent_version,
    uint8_t* out_revoke) noexcept {
    if (!out_revoke) return VN_ERR_INVALID_ARG;
    *out_revoke = vnotch::services::SettingsMigrator::ShouldRevokeSpotifyCanvasConsent(
        has_accepted != 0, enable_canvas != 0, allow_online != 0, consent_version) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"

VN_EXPORT int32_t __stdcall vn_settings_migrator_append_csv_token_if_missing(
    const wchar_t* csv_ptr,
    int32_t csv_len,
    const wchar_t* token_ptr,
    int32_t token_len,
    wchar_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept {
    if (!token_ptr || token_len <= 0 || !out_buf || out_cap <= 0 || !out_len) return VN_ERR_INVALID_ARG;
    if (csv_len < 0 || (csv_len > 0 && !csv_ptr)) return VN_ERR_INVALID_ARG;

    const std::wstring_view csv = (csv_len > 0) ? std::wstring_view(csv_ptr, static_cast<size_t>(csv_len)) : std::wstring_view{};
    const std::wstring_view token(token_ptr, static_cast<size_t>(token_len));
    return vnotch::services::SettingsMigrator::AppendCsvTokenIfMissing(csv, token, out_buf, out_cap, out_len);
}

