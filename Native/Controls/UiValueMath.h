#pragma once
#include "vnotch_ext_ui.h"

#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string_view>

namespace vnotch::ui {
constexpr double ClampF64(double value, double min_val, double max_val) noexcept
{
    if (value < min_val) return min_val;
    if (value > max_val) return max_val;
    return value;
}

inline double MathF64Max(double a, double b) noexcept
{
    if (std::isnan(a) || std::isnan(b))
        return std::numeric_limits<double>::quiet_NaN();
    return a > b ? a : b;
}

inline int32_t UncheckedRoundToInt32(double value) noexcept
{
    if (!std::isfinite(value))
        return std::numeric_limits<int32_t>::min();
    const double rounded = std::rint(value);
    if (rounded < -2147483648.0 || rounded > 2147483647.0)
        return std::numeric_limits<int32_t>::min();
    return static_cast<int32_t>(rounded);
}

constexpr bool IsUtf16WhiteSpace(char16_t ch) noexcept
{
    return (ch >= 0x0009 && ch <= 0x000D) ||
           ch == 0x0020 ||
           ch == 0x0085 ||
           ch == 0x00A0 ||
           ch == 0x1680 ||
           (ch >= 0x2000 && ch <= 0x200A) ||
           ch == 0x2028 ||
           ch == 0x2029 ||
           ch == 0x202F ||
           ch == 0x205F ||
           ch == 0x3000;
}

constexpr bool IsAsciiHexDigit(char16_t ch) noexcept
{
    return (ch >= u'0' && ch <= u'9') ||
           (ch >= u'a' && ch <= u'f') ||
           (ch >= u'A' && ch <= u'F');
}

constexpr bool IsAsciiAlpha(char16_t ch) noexcept
{
    return (ch >= u'a' && ch <= u'z') || (ch >= u'A' && ch <= u'Z');
}

constexpr char16_t ToAsciiLower(char16_t ch) noexcept
{
    return (ch >= u'A' && ch <= u'Z') ? static_cast<char16_t>(ch + 32) : ch;
}

constexpr bool EqualsAsciiIgnoreCase(std::u16string_view a, std::u16string_view b) noexcept
{
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i)
    {
        if (ToAsciiLower(a[i]) != ToAsciiLower(b[i]))
            return false;
    }
    return true;
}
}
