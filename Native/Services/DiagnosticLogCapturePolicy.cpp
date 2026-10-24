#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class DiagnosticLogCapturePolicy final
    {
    public:
        static int32_t vn_diagnostic_log_try_capture_impl(int64_t* state_window_and_count, int64_t tick_count_ms, uint8_t* out_captured) noexcept
        {
    if (state_window_and_count == nullptr || out_captured == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    constexpr uint32_t MAX_ENTRIES_PER_SECOND = 32u;
    int64_t window = (tick_count_ms / 1000LL) << 32;
    std::atomic_ref<int64_t> state(*state_window_and_count);

    for (int32_t attempt = 0; attempt < 4; ++attempt)
    {
        int64_t previous = state.load(std::memory_order_acquire);
        uint32_t count = ((previous & ~0xFFFFFFFFLL) == window) ? static_cast<uint32_t>(previous) : 0u;
        if (count >= MAX_ENTRIES_PER_SECOND)
        {
            *out_captured = 0u;
            return VN_OK;
        }
        int64_t next = window | static_cast<int64_t>(count + 1u);
        if (state.compare_exchange_strong(previous, next, std::memory_order_acq_rel))
        {
            *out_captured = 1u;
            return VN_OK;
        }
    }
    *out_captured = 0u;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_diagnostic_log_try_capture(int64_t* state_window_and_count, int64_t tick_count_ms, uint8_t* out_captured) noexcept
{
    return vnotch::services::DiagnosticLogCapturePolicy::vn_diagnostic_log_try_capture_impl(state_window_and_count, tick_count_ms, out_captured);
}

