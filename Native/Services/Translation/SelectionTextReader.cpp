#include "vnotch_common.h"
#include <cmath>
#include <cstdint>
#include <string_view>

namespace vnotch::services::translation {

class SelectionTextReader final {
public:
    SelectionTextReader() = delete;

    [[nodiscard]] static constexpr bool NeedsSanitization(std::u16string_view part) noexcept {
        for (const char16_t ch : part) {
            if (ch == u'\0' || ch == 0xFFFCu) return true;
        }
        return false;
    }

    static constexpr int32_t SanitizeToBuffer(
        std::u16string_view part,
        char16_t* out_buf,
        int32_t out_cap) noexcept {
        int32_t written = 0;
        for (const char16_t ch : part) {
            if (ch == u'\0' || ch == 0xFFFCu) continue;
            if (out_buf && written < out_cap) {
                out_buf[written] = ch;
            }
            ++written;
        }
        return written;
    }

    [[nodiscard]] static bool ValidBounds(
        bool is_empty,
        double x,
        double y,
        double width,
        double height) noexcept {
        return !is_empty &&
               std::isfinite(x) &&
               std::isfinite(y) &&
               std::isfinite(width) &&
               std::isfinite(height) &&
               width > 0.0 &&
               height > 0.0;
    }
};

} // namespace vnotch::services::translation

extern "C" {

VN_EXPORT int32_t vn_selection_text_reader_needs_sanitization(
    const char16_t* part,
    int32_t part_len,
    uint8_t* out_needs) noexcept {
    if (!out_needs || (!part && part_len > 0) || part_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(part ? part : u"", static_cast<size_t>(part_len));
    *out_needs = vnotch::services::translation::SelectionTextReader::NeedsSanitization(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_selection_text_reader_sanitize(
    const char16_t* part,
    int32_t part_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept {
    if (!out_len || (!part && part_len > 0) || part_len < 0 || (!out_buf && out_cap > 0) || out_cap < 0) {
        return VN_E_INVALID_ARG;
    }
    const std::u16string_view sv(part ? part : u"", static_cast<size_t>(part_len));
    *out_len = vnotch::services::translation::SelectionTextReader::SanitizeToBuffer(sv, out_buf, out_cap);
    if (*out_len > 0 && (!out_buf || out_cap < *out_len)) {
        return VN_ERR_BUFFER_TOO_SMALL;
    }
    return VN_OK;
}

VN_EXPORT int32_t vn_selection_text_reader_valid_bounds(
    uint8_t is_empty,
    double x,
    double y,
    double width,
    double height,
    uint8_t* out_valid) noexcept {
    if (!out_valid) return VN_E_INVALID_ARG;
    *out_valid = vnotch::services::translation::SelectionTextReader::ValidBounds(
        is_empty != 0, x, y, width, height) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
