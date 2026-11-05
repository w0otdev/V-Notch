#include "../vnotch_native.h"

namespace {

[[nodiscard]] constexpr double compute_normalized_fraction(double value, double min_val, double max_val) noexcept {
    if (!(max_val > min_val)) {
        return 0.0;
    }
    const double raw = (value - min_val) / (max_val - min_val);
    if (raw != raw) {
        return 0.0;
    }
    return std::clamp(raw, 0.0, 1.0);
}

[[nodiscard]] constexpr double compute_effective_step(double min_val, double max_val, double tick_freq) noexcept {
    if (tick_freq > 0.0) {
        return tick_freq;
    }
    return (max_val > min_val) ? (max_val - min_val) / 20.0 : 1.0;
}

[[nodiscard]] inline double snap_and_clamp_value(double value, double min_val, double max_val, double effective_step) noexcept {
    double snapped = value;
    if (effective_step > 0.0) {
        snapped = std::round((value - min_val) / effective_step) * effective_step + min_val;
    }
    if (min_val <= max_val) {
        return std::clamp(snapped, min_val, max_val);
    }
    return std::max(min_val, std::min(max_val, snapped));
}

} // namespace

VN_EXPORT int32_t __stdcall vn_elastic_slider_compute_metrics(
    double value,
    double min_val,
    double max_val,
    double tick_freq,
    double* out_fraction,
    double* out_effective_step,
    int32_t* out_tick_count,
    double* out_snapped_value) noexcept
{
    if (!out_fraction || !out_effective_step || !out_tick_count || !out_snapped_value) {
        return VN_ERR_INVALID_ARG;
    }

    const double fraction = compute_normalized_fraction(value, min_val, max_val);
    const double step = compute_effective_step(min_val, max_val, tick_freq);

    int32_t tick_count = 1;
    if (max_val > min_val) {
        if (step <= 0.0) {
            tick_count = 20;
        } else {
            const double rounded = std::round((max_val - min_val) / step);
            if (rounded >= static_cast<double>(INT32_MAX)) {
                tick_count = INT32_MAX;
            } else {
                const int32_t count = static_cast<int32_t>(rounded);
                tick_count = count > 1 ? count : 1;
            }
        }
    }

    const double snapped = snap_and_clamp_value(value, min_val, max_val, step);

    *out_fraction = fraction;
    *out_effective_step = step;
    *out_tick_count = tick_count;
    *out_snapped_value = snapped;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_elastic_slider_compute_indicator_layout(
    double value,
    double min_val,
    double max_val,
    double width,
    double track_edge_pad,
    double indicator_width,
    double* out_target_left,
    double* out_fill_width) noexcept
{
    if (!out_target_left || !out_fill_width) {
        return VN_ERR_INVALID_ARG;
    }
    if (width <= 0.0 || !(max_val > min_val)) {
        *out_target_left = 0.0;
        *out_fill_width = 0.0;
        return VN_ERR_INVALID_ARG;
    }

    const double fraction = compute_normalized_fraction(value, min_val, max_val);
    const double fill_edge_x = width * fraction;
    *out_fill_width = fill_edge_x;

    const double usable_width = width - track_edge_pad * 2.0;
    if (usable_width <= 0.0) {
        *out_target_left = 0.0;
        return VN_ERR_INVALID_ARG;
    }

    const double clamped_center_x = std::clamp(fill_edge_x, track_edge_pad, width - track_edge_pad);
    *out_target_left = clamped_center_x - (indicator_width * 0.5);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_elastic_slider_eval_mouse(
    double mouse_x,
    double width,
    double track_edge_pad,
    double min_val,
    double max_val,
    double tick_freq,
    double* out_drag_overflow,
    double* out_clamped_fraction,
    double* out_snapped_value,
    int32_t* out_overscroll_dir) noexcept
{
    if (!out_drag_overflow || !out_clamped_fraction || !out_snapped_value || !out_overscroll_dir) {
        return VN_ERR_INVALID_ARG;
    }

    const double usable_width = width - track_edge_pad * 2.0;
    if (width <= 0.0 || usable_width <= 0.0) {
        *out_drag_overflow = 0.0;
        *out_clamped_fraction = 0.0;
        *out_snapped_value = min_val;
        *out_overscroll_dir = 0;
        return VN_ERR_INVALID_ARG;
    }

    double fraction = (mouse_x - track_edge_pad) / usable_width;
    double drag_overflow = 0.0;
    int32_t overscroll_dir = 0;

    if (fraction < 0.0) {
        drag_overflow = fraction * usable_width;
        fraction = 0.0;
        overscroll_dir = -1;
    } else if (fraction > 1.0) {
        drag_overflow = (fraction - 1.0) * usable_width;
        fraction = 1.0;
        overscroll_dir = 1;
    }

    const double raw_value = min_val + fraction * (max_val - min_val);
    const double step = compute_effective_step(min_val, max_val, tick_freq);
    const double snapped_value = snap_and_clamp_value(raw_value, min_val, max_val, step);

    *out_drag_overflow = drag_overflow;
    *out_clamped_fraction = fraction;
    *out_snapped_value = snapped_value;
    *out_overscroll_dir = overscroll_dir;
    return VN_OK;
}
