#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class ProcessPriorityPolicy final
    {
    public:
        static int32_t vn_process_priority_is_high_impl(const char16_t* priority, int32_t priority_len, uint8_t* out_is_high) noexcept
        {
    if (out_is_high == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view sv = make_u16_view(priority, priority_len);
    *out_is_high = equals_ignore_case(sv, u"High") ? 1u : 0u;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_process_priority_is_high(const char16_t* priority, int32_t priority_len, uint8_t* out_is_high) noexcept
{
    return vnotch::services::ProcessPriorityPolicy::vn_process_priority_is_high_impl(priority, priority_len, out_is_high);
}

