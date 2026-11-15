#pragma once
#include "vnotch_ext_sub.h"
#include "vnotch_common.h"
namespace vnotch::parsing {
constexpr std::u16string_view make_u16_view(const wchar_t* ptr, int32_t len) noexcept
{
    return (ptr != nullptr && len > 0)
        ? std::u16string_view(reinterpret_cast<const char16_t*>(ptr), static_cast<size_t>(len))
        : std::u16string_view{};
}

inline int32_t write_wstr_out(std::u16string_view src, wchar_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (out_len == nullptr) return VN_ERR_INVALID_ARG;
    const int32_t needed = static_cast<int32_t>(src.size());
    *out_len = needed;
    if (needed == 0) return VN_OK;
    if (out_buf == nullptr || out_cap < needed) return VN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(out_buf, src.data(), static_cast<size_t>(needed) * sizeof(wchar_t));
    return VN_OK;
}

constexpr char16_t ascii_to_lower(char16_t ch) noexcept
{
    return (ch >= u'A' && ch <= u'Z') ? static_cast<char16_t>(ch + 32) : ch;
}

constexpr bool is_ascii_alpha(char16_t ch) noexcept
{
    return (ch >= u'A' && ch <= u'Z') || (ch >= u'a' && ch <= u'z');
}

constexpr bool is_ascii_alnum(char16_t ch) noexcept
{
    return is_ascii_alpha(ch) || (ch >= u'0' && ch <= u'9');
}

constexpr bool is_ascii_hex(char16_t ch) noexcept
{
    return (ch >= u'0' && ch <= u'9') ||
           (ch >= u'a' && ch <= u'f') ||
           (ch >= u'A' && ch <= u'F');
}

constexpr uint16_t hex_value(char16_t ch) noexcept
{
    if (ch >= u'0' && ch <= u'9') return static_cast<uint16_t>(ch - u'0');
    if (ch >= u'a' && ch <= u'f') return static_cast<uint16_t>(10 + (ch - u'a'));
    return static_cast<uint16_t>(10 + (ch - u'A'));
}

constexpr bool equals_ascii_icase(std::u16string_view a, std::u16string_view b) noexcept
{
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i)
    {
        if (ascii_to_lower(a[i]) != ascii_to_lower(b[i])) return false;
    }
    return true;
}

// Sorted BMP non-ASCII Nd (Decimal_Digit_Number) 10-digit block bases.
constexpr uint16_t kNonAsciiNdBases[36] = {
    0x0660u, 0x06F0u, 0x07C0u, 0x0966u, 0x09E6u, 0x0A66u, 0x0AE6u, 0x0B66u, 0x0BE6u,
    0x0C66u, 0x0CE6u, 0x0D66u, 0x0DE6u, 0x0E50u, 0x0ED0u, 0x0F20u, 0x1040u, 0x1090u,
    0x17E0u, 0x1810u, 0x1946u, 0x19D0u, 0x1A80u, 0x1A90u, 0x1B50u, 0x1BB0u, 0x1C40u,
    0x1C50u, 0xA620u, 0xA8D0u, 0xA900u, 0xA9D0u, 0xA9F0u, 0xAA50u, 0xABF0u, 0xFF10u
};

constexpr bool is_nd_digit(char16_t ch) noexcept
{
    if (ch < 0x80u) return ch >= u'0' && ch <= u'9';
    const uint16_t code = static_cast<uint16_t>(ch);
    int32_t lo = 0;
    int32_t hi = 35;
    while (lo <= hi)
    {
        const int32_t mid = lo + ((hi - lo) >> 1);
        const uint16_t base = kNonAsciiNdBases[mid];
        if (code < base)
            hi = mid - 1;
        else if (code > static_cast<uint16_t>(base + 9u))
            lo = mid + 1;
        else
            return true;
    }
    return false;
}

constexpr bool is_unicode_letter(char16_t ch) noexcept
{
    if (ch < 0x80u) return is_ascii_alpha(ch);
    return vn_is_letter_or_digit(ch) && !is_nd_digit(ch);
}

// Sorted BMP Nl | No ranges for \p{N}.
constexpr VnCharRange kUnicodeNlNoRanges[33] = {
    {0x00B2u, 0x00B3u}, {0x00B9u, 0x00B9u}, {0x00BCu, 0x00BEu}, {0x09F4u, 0x09F9u},
    {0x0B72u, 0x0B77u}, {0x0BF0u, 0x0BF2u}, {0x0C78u, 0x0C7Eu}, {0x0D58u, 0x0D5Eu},
    {0x0D70u, 0x0D78u}, {0x0F2Au, 0x0F33u}, {0x1369u, 0x137Cu}, {0x16EEu, 0x16F0u},
    {0x17F0u, 0x17F9u}, {0x19DAu, 0x19DAu}, {0x2070u, 0x2070u}, {0x2074u, 0x2079u},
    {0x2080u, 0x2089u}, {0x2150u, 0x2182u}, {0x2185u, 0x2189u}, {0x2460u, 0x249Bu},
    {0x24EAu, 0x24FFu}, {0x2776u, 0x2793u}, {0x2CFDu, 0x2CFDu}, {0x3007u, 0x3007u},
    {0x3021u, 0x3029u}, {0x3038u, 0x303Au}, {0x3192u, 0x3195u}, {0x3220u, 0x3229u},
    {0x3248u, 0x324Fu}, {0x3251u, 0x325Fu}, {0x3280u, 0x3289u}, {0x32B1u, 0x32BFu},
    {0xA6E6u, 0xA6EFu}
};

constexpr bool is_unicode_number(char16_t ch) noexcept
{
    if (is_nd_digit(ch)) return true;
    if (ch < 0x80u) return false;
    if (ch >= 0xA830u && ch <= 0xA835u) return true;
    return vn_in_ranges(ch, kUnicodeNlNoRanges, 33);
}

// Sorted BMP Sc (Currency_Symbol) ranges for \p{Sc}.
constexpr VnCharRange kCurrencySymbolRanges[17] = {
    {0x00A2u, 0x00A5u}, {0x058Fu, 0x058Fu}, {0x060Bu, 0x060Bu}, {0x07FEu, 0x07FFu},
    {0x09F2u, 0x09F3u}, {0x09FBu, 0x09FBu}, {0x0AF1u, 0x0AF1u}, {0x0BF9u, 0x0BF9u},
    {0x0E3Fu, 0x0E3Fu}, {0x17DBu, 0x17DBu}, {0x20A0u, 0x20C0u}, {0xA838u, 0xA838u},
    {0xFDFCu, 0xFDFCu}, {0xFE69u, 0xFE69u}, {0xFF04u, 0xFF04u}, {0xFFE0u, 0xFFE1u},
    {0xFFE5u, 0xFFE6u}
};

constexpr bool is_currency_symbol(char16_t ch) noexcept
{
    if (ch == u'$') return true;
    if (ch < 0x80u) return false;
    return vn_in_ranges(ch, kCurrencySymbolRanges, 17);
}

constexpr bool is_connector_punct(char16_t ch) noexcept
{
    return ch == u'_' || ch == 0x203Fu || ch == 0x2040u || ch == 0x2054u ||
           ch == 0xFE33u || ch == 0xFE34u ||
           (ch >= 0xFE4Du && ch <= 0xFE4Fu) || ch == 0xFF3Fu;
}

constexpr bool is_regex_word_char(char16_t ch) noexcept
{
    if (ch < 0x80u) return is_ascii_alnum(ch) || ch == u'_';
    return vn_is_letter_or_digit(ch) || vn_is_non_spacing_mark(ch) || is_connector_punct(ch);
}

constexpr bool is_word_boundary(std::u16string_view text, size_t index) noexcept
{
    const bool left_word = index > 0 && is_regex_word_char(text[index - 1]);
    const bool right_word = index < text.size() && is_regex_word_char(text[index]);
    return left_word != right_word;
}

}
