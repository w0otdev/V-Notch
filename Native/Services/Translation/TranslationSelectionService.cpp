#include "vnotch_common.h"
#include <cstdint>
#include <string_view>

namespace vnotch::services::translation {

class TranslationSelectionService final {
public:
    TranslationSelectionService() = delete;

    [[nodiscard]] static constexpr bool CanSendUnicodeInput(std::u16string_view text) noexcept {
        if (text.size() > 4000) return false;
        for (const char16_t ch : text) {
            if (ch == u'\n' || ch == u'\r') return false;
        }
        return true;
    }

    [[nodiscard]] static constexpr bool CanPreserveRecentSnapshot(
        bool manual,
        bool protected_input,
        bool same_window,
        int64_t now_tick,
        int64_t last_valid_tick) noexcept {
        return manual && !protected_input && same_window && (now_tick - last_valid_tick <= 5000LL);
    }
};

} // namespace vnotch::services::translation

extern "C" {

VN_EXPORT int32_t vn_translation_selection_can_send_unicode_input(
    const char16_t* text,
    int32_t text_len,
    uint8_t* out_can_send) noexcept {
    if (!out_can_send || (!text && text_len > 0) || text_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(text ? text : u"", static_cast<size_t>(text_len));
    *out_can_send = vnotch::services::translation::TranslationSelectionService::CanSendUnicodeInput(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_translation_selection_can_preserve_recent_snapshot(
    uint8_t manual,
    uint8_t protected_input,
    uint8_t same_window,
    int64_t now_tick,
    int64_t last_valid_tick,
    uint8_t* out_can_preserve) noexcept {
    if (!out_can_preserve) return VN_E_INVALID_ARG;
    *out_can_preserve = vnotch::services::translation::TranslationSelectionService::CanPreserveRecentSnapshot(
        manual != 0, protected_input != 0, same_window != 0, now_tick, last_valid_tick) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
