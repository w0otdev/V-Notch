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

} // extern "C"
