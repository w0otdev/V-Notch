#include "vnotch_common.h"
#include <cstdint>
#include <string_view>

namespace vnotch::services {

class TranslationStructuredText final {
public:
    static constexpr bool ContainsEmoji(std::u16string_view element) noexcept {
        size_t i = 0;
        size_t n = element.size();
        while (i < n) {
            uint32_t rune = element[i];
            if (rune >= 0xD800u && rune <= 0xDBFFu && i + 1 < n) {
                uint32_t low = element[i + 1];
                if (low >= 0xDC00u && low <= 0xDFFFu) {
                    rune = 0x10000u + ((rune - 0xD800u) << 10) + (low - 0xDC00u);
                    i += 2;
                } else {
                    i++;
                }
            } else {
                i++;
            }

            if (IsEmojiRune(rune)) {
                return true;
            }
        }
        return false;
    }

private:
    static constexpr bool IsEmojiRune(uint32_t r) noexcept {
        return (r >= 0x1F000u && r <= 0x1FAFFu) ||
               (r >= 0x2600u && r <= 0x27BFu) ||
               (r >= 0x2190u && r <= 0x21FFu) ||
               (r >= 0x2300u && r <= 0x23FFu) ||
               (r >= 0x2B00u && r <= 0x2BFFu) ||
               r == 0x25AAu || r == 0x25ABu || r == 0x25B6u || r == 0x25C0u ||
               r == 0xFE0Fu || r == 0x20E3u || r == 0x00A9u || r == 0x00AEu ||
               r == 0x203Cu || r == 0x2049u || r == 0x2122u || r == 0x2139u;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_translation_structured_contains_emoji(
    const char16_t* elem_ptr, int32_t elem_len, uint8_t* out_contains) noexcept
{
    if (!out_contains) return VN_ERR_INVALID_ARG;
    if (!elem_ptr || elem_len <= 0) {
        *out_contains = 0;
        return VN_OK;
    }
    *out_contains = vnotch::services::TranslationStructuredText::ContainsEmoji(
        std::u16string_view(elem_ptr, static_cast<size_t>(elem_len))) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
