#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class ClipboardRetry final
    {
    public:
        static int32_t vn_clipboard_retry_delay_ms_impl(int32_t retry, int32_t* out_delay_ms) noexcept
        {
    if (out_delay_ms == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    int32_t shift = std::clamp(retry, 0, 3);
    *out_delay_ms = std::min(30 << shift, 240);
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_clipboard_retry_delay_ms(int32_t retry, int32_t* out_delay_ms) noexcept
{
    return vnotch::services::ClipboardRetry::vn_clipboard_retry_delay_ms_impl(retry, out_delay_ms);
}

