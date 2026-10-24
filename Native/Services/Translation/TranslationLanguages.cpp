#include "vnotch_common.h"
#include <array>
#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>

namespace vnotch::services {

class TranslationLanguages final {
public:
    static bool HasAtLeastLetters(std::u16string_view text, int32_t minimum) noexcept {
        if (minimum <= 0) return true;
        int32_t count = 0;
        for (char16_t c : text) {
            if (IsLetter(c) && ++count >= minimum) {
                return true;
            }
        }
        return false;
    }

    static std::u16string_view DetectUnambiguousScript(std::u16string_view text) noexcept {
        int32_t total_letters = 0;
        int32_t kana = 0, han = 0, ko = 0, th = 0, lo = 0, el = 0, hy = 0, ka = 0, vi = 0;
        for (char16_t c : text) {
            if (!IsLetter(c)) continue;
            total_letters++;
            if (c >= 0x3040u && c <= 0x30FFu) kana++;
            else if ((c >= 0x4E00u && c <= 0x9FFFu) || (c >= 0x3400u && c <= 0x4DBFu)) han++;
            else if (c >= 0xAC00u && c <= 0xD7AFu) ko++;
            else if (c >= 0x0E00u && c <= 0x0E7Fu) th++;
            else if (c >= 0x0E80u && c <= 0x0EFFu) lo++;
            else if (c >= 0x0370u && c <= 0x03FFu) el++;
            else if (c >= 0x0530u && c <= 0x058Fu) hy++;
            else if (c >= 0x10A0u && c <= 0x10FFu) ka++;
            else if (IsUnambiguousVietnameseChar(c)) vi++;
        }

        if (total_letters == 0) return {};

        int32_t ja = kana > 0 ? kana + han : 0;
        if (IsDominantScript(ja, total_letters)) return u"ja";
        if (IsDominantScript(ko, total_letters)) return u"ko";
        if (IsDominantScript(th, total_letters)) return u"th";
        if (IsDominantScript(lo, total_letters)) return u"lo";
        if (IsDominantScript(el, total_letters)) return u"el";
        if (IsDominantScript(hy, total_letters)) return u"hy";
        if (IsDominantScript(ka, total_letters)) return u"ka";
        if (vi > 0 && total_letters < 24 && IsPureLatinScript(text)) return u"vi";
        return {};
    }

private:
    static constexpr bool IsDecimalDigit(char16_t ch) noexcept {
        constexpr std::array<uint16_t, 37> kDigitBases = {
            0x0030u, 0x0660u, 0x06F0u, 0x07C0u, 0x0966u, 0x09E6u, 0x0A66u, 0x0AE6u,
            0x0B66u, 0x0BE6u, 0x0C66u, 0x0CE6u, 0x0D66u, 0x0DE6u, 0x0E50u, 0x0ED0u,
            0x0F20u, 0x1040u, 0x1090u, 0x17E0u, 0x1810u, 0x1946u, 0x19D0u, 0x1A80u,
            0x1A90u, 0x1B50u, 0x1BB0u, 0x1C40u, 0x1C50u, 0xA620u, 0xA8D0u, 0xA900u,
            0xA9D0u, 0xA9F0u, 0xAA50u, 0xABF0u, 0xFF10u
        };
        uint16_t code = static_cast<uint16_t>(ch);
        for (uint16_t base : kDigitBases) {
            if (code >= base && code <= base + 9u) return true;
        }
        return false;
    }

    static constexpr bool IsLetter(char16_t ch) noexcept {
        return vn_is_letter_or_digit(ch) && !IsDecimalDigit(ch);
    }

    static bool IsUnambiguousVietnameseChar(char16_t c) noexcept {
        char16_t low = vnotch::to_lower_invariant(c);
        static constexpr std::u16string_view kViChars = u"ơưấầẩẫậắằẳẵặếềểễệốồổỗộớờởỡợứừửữự";
        return kViChars.find(low) != std::u16string_view::npos;
    }

    static constexpr bool IsPureLatinScript(std::u16string_view text) noexcept {
        for (char16_t c : text) {
            if (IsLetter(c) && c > 0x024Fu && !(c >= 0x1E00u && c <= 0x1EFFu)) {
                return false;
            }
        }
        return true;
    }

    static constexpr bool IsDominantScript(int32_t script_count, int32_t total_letters) noexcept {
        return script_count > 0 && ((total_letters < 12 && script_count == total_letters) || script_count * 2 >= total_letters);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_translation_lang_has_at_least_letters(
    const char16_t* text_ptr, int32_t text_len, int32_t minimum, uint8_t* out_has) noexcept
{
    if (!out_has) return VN_ERR_INVALID_ARG;
    if (!text_ptr || text_len <= 0) {
        *out_has = minimum <= 0 ? 1 : 0;
        return VN_OK;
    }
    *out_has = vnotch::services::TranslationLanguages::HasAtLeastLetters(
        std::u16string_view(text_ptr, static_cast<size_t>(text_len)), minimum) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_translation_lang_detect_unambiguous_script(
    const char16_t* text_ptr, int32_t text_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (!out_len) return VN_ERR_INVALID_ARG;
    *out_len = 0;
    if (!text_ptr || text_len <= 0) return VN_OK;
    auto script = vnotch::services::TranslationLanguages::DetectUnambiguousScript(
        std::u16string_view(text_ptr, static_cast<size_t>(text_len)));
    return vnotch::copy_out_u16(script, out_buf, out_cap, out_len);
}

} // extern "C"
