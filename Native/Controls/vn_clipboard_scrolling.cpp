#include "../vnotch_native.h"

VN_EXPORT int32_t __stdcall vn_clipboard_scroll_category_drop_speed(
    double x,
    double width,
    double* out_speed) noexcept
{
    if (!out_speed) return VN_ERR_INVALID_ARG;

    if (!(width > 0.0) || !(x >= 0.0) || !(x <= width))
    {
        *out_speed = 0.0;
        return VN_OK;
    }

    const double edge = std::min(40.0, width / 2.0);
    if (x < edge)
    {
        *out_speed = -12.0 * (1.0 - x / edge);
    }
    else if (x > width - edge)
    {
        *out_speed = 12.0 * (1.0 - (width - x) / edge);
    }
    else
    {
        *out_speed = 0.0;
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_scroll_category_overscroll(
    double excess,
    uint8_t reduce_motion,
    double* out_elastic_x) noexcept
{
    if (!out_elastic_x) return VN_ERR_INVALID_ARG;

    // A bounded resistance curve keeps repeated wheel input within 20 DIPs.
    *out_elastic_x = reduce_motion != 0 ? 0.0 : -20.0 * std::tanh(excess / 120.0);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_scroll_step_category_frame(
    double scroll_target,
    double scrollable_width,
    double current_offset,
    double current_elastic_x,
    double seconds,
    uint8_t reduce_motion,
    double* out_clamped_target,
    double* out_next_offset,
    double* out_next_elastic_x,
    uint8_t* out_stop) noexcept
{
    if (!out_clamped_target || !out_next_offset || !out_next_elastic_x || !out_stop)
        return VN_ERR_INVALID_ARG;

    const double max_width = scrollable_width > 0.0 ? scrollable_width : 0.0;
    const double clamped_target = std::clamp(scroll_target, 0.0, max_width);
    const double remaining = clamped_target - current_offset;

    // Smooth critically-damped relaxation of any drag overscroll back to 0
    // (pure exponential decay: never oscillates or rebounds past zero)
    const double next_elastic_x = std::abs(current_elastic_x) > 0.05
        ? current_elastic_x * std::exp(-24.0 * seconds)
        : 0.0;

    const bool stop = (std::abs(remaining) < 0.5 && std::abs(next_elastic_x) < 0.1) || (reduce_motion != 0);

    *out_clamped_target = clamped_target;
    *out_next_elastic_x = next_elastic_x;
    *out_next_offset = stop
        ? clamped_target
        : (current_offset + remaining * (1.0 - std::exp(-22.0 * seconds)));
    *out_stop = stop ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_scroll_step_cards_frame(
    double scroll_target,
    double scrollable_width,
    double current_pos,
    double seconds,
    uint8_t reduce_motion,
    double* out_clamped_target,
    double* out_next_pos,
    uint8_t* out_stop) noexcept
{
    if (!out_clamped_target || !out_next_pos || !out_stop)
        return VN_ERR_INVALID_ARG;

    const double max_width = scrollable_width > 0.0 ? scrollable_width : 0.0;
    const double clamped_target = std::clamp(scroll_target, 0.0, max_width);
    // Layout may round the applied offset. Feeding it back into the easing
    // loses subpixel movement and can stall the tail at high refresh rates.
    const double current = std::clamp(current_pos, 0.0, max_width);
    const double remaining = clamped_target - current;

    const bool stop = (std::abs(remaining) < 0.5) || (reduce_motion != 0);

    *out_clamped_target = clamped_target;
    *out_next_pos = stop
        ? clamped_target
        : (current + remaining * (1.0 - std::exp(-22.0 * seconds)));
    *out_stop = stop ? 1u : 0u;
    return VN_OK;
}
