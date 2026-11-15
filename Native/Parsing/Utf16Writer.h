#pragma once
#include "vnotch_native.h"
namespace vnotch::ui {
struct Utf16Writer
{
    char16_t* buf;
    int32_t cap;
    int32_t len = 0;
    bool overflow = false;

    void Append(std::u16string_view sv) noexcept
    {
        if (overflow || sv.empty()) return;
        if (sv.size() > static_cast<size_t>(cap - len))
        {
            overflow = true;
            return;
        }
        std::memcpy(buf + len, sv.data(), sv.size() * sizeof(char16_t));
        len += static_cast<int32_t>(sv.size());
    }

    void AppendChar(char16_t ch) noexcept
    {
        if (overflow) return;
        if (len >= cap)
        {
            overflow = true;
            return;
        }
        buf[len++] = ch;
    }
};
}
