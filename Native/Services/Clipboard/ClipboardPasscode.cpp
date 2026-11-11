#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class ClipboardPasscode final
    {
    public:
        static int32_t vn_clipboard_passcode_try_copy_digits_impl(intptr_t unicode_ptr,
    int32_t pin_len,
    uint8_t* out_digits,
    int32_t dest_len,
    uint8_t* out_ok) noexcept
        {
    if (out_ok == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_ok = 0u;
    if (out_digits != nullptr && dest_len > 0)
    {
        SecureZeroMemory(out_digits, static_cast<size_t>(dest_len));
    }
    if (pin_len != 6 || dest_len != 6 || unicode_ptr == 0 || out_digits == nullptr)
    {
        return VN_OK;
    }

    const auto* chars = reinterpret_cast<const char16_t*>(unicode_ptr);
    for (int32_t i = 0; i < 6; ++i)
    {
        char16_t d = chars[i];
        if (d < u'0' || d > u'9')
        {
            SecureZeroMemory(out_digits, 6);
            return VN_OK;
        }
            out_digits[i] = static_cast<uint8_t>(d);
        }
        *out_ok = 1u;
        return VN_OK;
    }
    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_clipboard_passcode_try_copy_digits(intptr_t unicode_ptr,
    int32_t pin_len,
    uint8_t* out_digits,
    int32_t dest_len,
    uint8_t* out_ok) noexcept
{
    return vnotch::services::ClipboardPasscode::vn_clipboard_passcode_try_copy_digits_impl(unicode_ptr, pin_len, out_digits, dest_len, out_ok);
}

