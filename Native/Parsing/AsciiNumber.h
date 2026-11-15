#pragma once
#include "vnotch_common.h"
#include "vnotch_cpu.h"
#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <immintrin.h>
#include <limits>
#include <string_view>

namespace vnotch::controllers {
constexpr wchar_t ToLowerAscii(wchar_t c) noexcept
{
    return (c >= L'A' && c <= L'Z') ? static_cast<wchar_t>(c + (L'a' - L'A')) : c;
}

constexpr bool IsAsciiWhitespace(wchar_t c) noexcept
{
    return c == L' ' || c == L'\t' || c == L'\r' || c == L'\n' || c == L'\v' || c == L'\f';
}

inline std::wstring_view TrimWhitespace(std::wstring_view sv) noexcept
{
    size_t start = 0;
    while (start < sv.size() && IsAsciiWhitespace(sv[start]))
    {
        ++start;
    }
    size_t end = sv.size();
    while (end > start && IsAsciiWhitespace(sv[end - 1]))
    {
        --end;
    }
    return sv.substr(start, end - start);
}

inline bool EqualsIgnoreCaseAscii(std::wstring_view a, std::wstring_view b) noexcept
{
    if (a.size() != b.size())
    {
        return false;
    }
    for (size_t i = 0; i < a.size(); ++i)
    {
        if (ToLowerAscii(a[i]) != ToLowerAscii(b[i]))
        {
            return false;
        }
    }
    return true;
}

template <typename T, size_t MaxChars>
inline bool TryParseAsciiNumberStrict(std::wstring_view sv, T* out_val) noexcept
{
    sv = TrimWhitespace(sv);
    if (sv.empty() || sv.size() > MaxChars)
    {
        return false;
    }
    char buf[MaxChars + 1]{};
    for (size_t i = 0; i < sv.size(); ++i)
    {
        if (sv[i] > 0x7F)
        {
            return false;
        }
        buf[i] = static_cast<char>(sv[i]);
    }
    T v{};
    auto [ptr, ec] = std::from_chars(buf, buf + sv.size(), v);
    if (ec != std::errc{} || ptr != buf + sv.size())
    {
        return false;
    }
    *out_val = v;
    return true;
}

inline bool TryParseInt32Strict(std::wstring_view sv, int32_t* out_val) noexcept
{
    return TryParseAsciiNumberStrict<int32_t, 16>(sv, out_val);
}

inline bool TryParseDoubleStrict(std::wstring_view sv, double* out_val) noexcept
{
    return TryParseAsciiNumberStrict<double, 63>(sv, out_val);
}

inline bool TryTicksFromSeconds(double total_seconds, int64_t* out_ticks) noexcept
{
    *out_ticks = 0;
    constexpr double kMaxMillis = static_cast<double>(std::numeric_limits<int64_t>::max() / 10000LL);
    if (!std::isfinite(total_seconds) || total_seconds <= 0.0)
    {
        return false;
    }
    double millis = total_seconds * 1000.0;
    if (!std::isfinite(millis) || millis >= kMaxMillis || millis <= 0.0)
    {
        return false;
    }
    double ticks_d = millis * 10000.0;
    if (!std::isfinite(ticks_d) || ticks_d < 1.0 || ticks_d >= static_cast<double>(std::numeric_limits<int64_t>::max()))
    {
        return false;
    }
    int64_t ticks = static_cast<int64_t>(std::llround(ticks_d));
    if (ticks <= 0)
    {
        return false;
    }
    *out_ticks = ticks;
    return true;
}
}
