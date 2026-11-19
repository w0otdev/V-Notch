#pragma once
#include <string_view>

namespace vnotch::services
{
    bool VerifyJwtSegments(std::u16string_view header, std::u16string_view payload, std::u16string_view signature) noexcept;
}
