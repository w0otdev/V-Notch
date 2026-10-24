#include "vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <cstdint>

namespace vnotch::services {

class SpotlightLayoutMetrics final {
public:
    static double ResolveMeasureWidth(double actual_width, double configured_width) noexcept {
        if (IsPositiveFinite(actual_width)) return actual_width;
        if (IsPositiveFinite(configured_width)) return configured_width;
        return 768.0;
    }

    static void CalculateEntranceSize(
        double measure_width,
        double desired_height,
        double actual_height,
        double margin_left,
        double margin_top,
        double margin_right,
        double margin_bottom,
        double* out_width,
        double* out_height) noexcept
    {
        double width = std::max(1.0, measure_width - margin_left - margin_right);
        double height = desired_height - margin_top - margin_bottom;
        if (!IsPositiveFinite(height)) {
            height = IsPositiveFinite(actual_height) ? std::max(1.0, actual_height) : 1.0;
        }
        *out_width = width;
        *out_height = height;
    }

private:
    static bool IsPositiveFinite(double value) noexcept {
        return std::isfinite(value) && value > 0.0;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_spotlight_layout_resolve_measure_width(
    double actual_width, double configured_width, double* out_width) noexcept
{
    if (!out_width) return VN_ERR_INVALID_ARG;
    *out_width = vnotch::services::SpotlightLayoutMetrics::ResolveMeasureWidth(actual_width, configured_width);
    return VN_OK;
}

VN_EXPORT int32_t vn_spotlight_layout_calculate_entrance_size(
    double measure_width,
    double desired_height,
    double actual_height,
    double margin_left,
    double margin_top,
    double margin_right,
    double margin_bottom,
    double* out_width,
    double* out_height) noexcept
{
    if (!out_width || !out_height) return VN_ERR_INVALID_ARG;
    vnotch::services::SpotlightLayoutMetrics::CalculateEntranceSize(
        measure_width, desired_height, actual_height,
        margin_left, margin_top, margin_right, margin_bottom,
        out_width, out_height);
    return VN_OK;
}

} // extern "C"
