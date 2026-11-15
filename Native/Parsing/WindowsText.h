#pragma once
#include "vnotch_ext_core.h"
namespace vnotch::windows_text {
    constexpr bool is_dotnet_whitespace(wchar_t c) noexcept
    {
        switch (c)
        {
        case 0x0009:
        case 0x000A:
        case 0x000B:
        case 0x000C:
        case 0x000D:
        case 0x0020:
        case 0x0085:
        case 0x00A0:
        case 0x1680:
        case 0x2000:
        case 0x2001:
        case 0x2002:
        case 0x2003:
        case 0x2004:
        case 0x2005:
        case 0x2006:
        case 0x2007:
        case 0x2008:
        case 0x2009:
        case 0x200A:
        case 0x2028:
        case 0x2029:
        case 0x202F:
        case 0x205F:
        case 0x3000:
            return true;
        default:
            return false;
        }
    }

    constexpr bool is_ascii_letter(wchar_t c) noexcept
    {
        return (c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z');
    }

    constexpr bool is_ascii_digit(wchar_t c) noexcept
    {
        return c >= L'0' && c <= L'9';
    }

    constexpr bool is_ascii_hex(wchar_t c) noexcept
    {
        return (c >= L'0' && c <= L'9') ||
               (c >= L'a' && c <= L'f') ||
               (c >= L'A' && c <= L'F');
    }

    constexpr wchar_t to_ascii_lower_w(wchar_t c) noexcept
    {
        return (c >= L'A' && c <= L'Z') ? static_cast<wchar_t>(c + (L'a' - L'A')) : c;
    }

    constexpr uint8_t to_ascii_lower_b(uint8_t c) noexcept
    {
        return (c >= 'A' && c <= 'Z') ? static_cast<uint8_t>(c + ('a' - 'A')) : c;
    }

    constexpr bool equals_ascii_icase_w(std::wstring_view a, std::wstring_view b) noexcept
    {
        if (a.size() != b.size()) return false;
        for (size_t i = 0; i < a.size(); ++i)
        {
            if (to_ascii_lower_w(a[i]) != to_ascii_lower_w(b[i]))
                return false;
        }
        return true;
    }

    constexpr bool ends_with_ascii_icase_w(std::wstring_view text, std::wstring_view suffix) noexcept
    {
        if (text.size() < suffix.size()) return false;
        return equals_ascii_icase_w(text.substr(text.size() - suffix.size()), suffix);
    }

    inline float clamp_f32_preserve_nan(float v, float lo, float hi) noexcept
    {
        if (std::isnan(v)) return v;
        if (v < lo) return lo;
        if (v > hi) return hi;
        return v;
    }

    constexpr int32_t parse_digits(std::wstring_view sv) noexcept
    {
        int32_t acc = 0;
        for (wchar_t ch : sv)
        {
            if (!is_ascii_digit(ch)) return -1;
            acc = acc * 10 + static_cast<int32_t>(ch - L'0');
        }
        return acc;
    }

    constexpr int32_t days_in_month(int32_t year, int32_t month) noexcept
    {
        switch (month)
        {
        case 1:
        case 3:
        case 5:
        case 7:
        case 8:
        case 10:
        case 12:
            return 31;
        case 4:
        case 6:
        case 9:
        case 11:
            return 30;
        case 2:
        {
            const bool leap = ((year % 4 == 0) && (year % 100 != 0)) || (year % 400 == 0);
            return leap ? 29 : 28;
        }
        default:
            return 0;
        }
    }

    constexpr bool is_html_space(wchar_t c) noexcept
    {
        return c == L' ' || c == L'\t' || c == L'\r' || c == L'\n' || c == L'\f';
    }
}
