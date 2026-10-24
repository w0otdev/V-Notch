#include "vnotch_common.h"
#include <cstdint>
#include <cstring>
#include <cwctype>
#include <string_view>

namespace vnotch::services {

class SpotifyScriptMetadataReader final {
public:
    static bool FindOperationQueryHash(
        std::u16string_view window,
        std::u16string_view operation,
        char16_t* out_hash64) noexcept
    {
        if (operation.empty() || window.size() < operation.size() + 74) {
            return false;
        }

        size_t n = window.size();
        for (size_t i = 0; i + operation.size() + 74 <= n; i++) {
            if (!IsQuote(window[i])) continue;

            size_t pos = i + 1;
            if (!MatchAsciiIgnoreCase(window, pos, operation)) continue;
            pos += operation.size();

            if (pos >= n || !IsQuote(window[pos])) continue;
            pos++;

            if (!SkipBoundedWhitespace(window, &pos, 128)) continue;
            if (pos >= n || window[pos] != u',') continue;
            pos++;

            if (!SkipBoundedWhitespace(window, &pos, 128)) continue;
            if (pos >= n || !IsQuote(window[pos])) continue;
            pos++;

            if (!MatchAsciiIgnoreCase(window, pos, u"query")) continue;
            pos += 5;

            if (pos >= n || !IsQuote(window[pos])) continue;
            pos++;

            if (!SkipBoundedWhitespace(window, &pos, 128)) continue;
            if (pos >= n || window[pos] != u',') continue;
            pos++;

            if (!SkipBoundedWhitespace(window, &pos, 128)) continue;
            if (pos >= n || !IsQuote(window[pos])) continue;
            pos++;

            if (pos + 65 > n) continue;
            bool all_hex = true;
            for (size_t h = 0; h < 64; h++) {
                if (!IsHex(window[pos + h])) {
                    all_hex = false;
                    break;
                }
            }
            if (!all_hex) continue;
            if (!IsQuote(window[pos + 64])) continue;

            std::memcpy(out_hash64, window.data() + pos, 64 * sizeof(char16_t));
            return true;
        }
        return false;
    }

private:
    static constexpr bool IsQuote(char16_t c) noexcept {
        return c == u'"' || c == u'\'';
    }

    static constexpr char16_t ToLowerAscii(char16_t c) noexcept {
        return (c >= u'A' && c <= u'Z') ? static_cast<char16_t>(c + 32) : c;
    }

    static constexpr bool IsHex(char16_t c) noexcept {
        return (c >= u'0' && c <= u'9') ||
               (c >= u'a' && c <= u'f') ||
               (c >= u'A' && c <= u'F');
    }

    static bool IsWhiteSpace(char16_t c) noexcept {
        return std::iswspace(static_cast<wint_t>(c)) != 0;
    }

    static bool MatchAsciiIgnoreCase(std::u16string_view s, size_t pos, std::u16string_view target) noexcept {
        if (pos + target.size() > s.size()) return false;
        for (size_t k = 0; k < target.size(); k++) {
            if (ToLowerAscii(s[pos + k]) != ToLowerAscii(target[k])) return false;
        }
        return true;
    }

    static bool SkipBoundedWhitespace(std::u16string_view s, size_t* io_pos, size_t max_ws) noexcept {
        size_t count = 0;
        while (*io_pos < s.size() && IsWhiteSpace(s[*io_pos])) {
            (*io_pos)++;
            count++;
            if (count > max_ws) return false;
        }
        return true;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_spotify_script_find_hash(
    const char16_t* window_ptr, int32_t window_len,
    const char16_t* op_ptr, int32_t op_len,
    char16_t* out_hash64, uint8_t* out_found) noexcept
{
    if (!out_hash64 || !out_found) return VN_ERR_INVALID_ARG;
    *out_found = 0;
    if (!window_ptr || window_len <= 0 || !op_ptr || op_len <= 0) {
        return VN_OK;
    }
    *out_found = vnotch::services::SpotifyScriptMetadataReader::FindOperationQueryHash(
        std::u16string_view(window_ptr, static_cast<size_t>(window_len)),
        std::u16string_view(op_ptr, static_cast<size_t>(op_len)),
        out_hash64) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
