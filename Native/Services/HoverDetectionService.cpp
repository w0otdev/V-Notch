#include "vnotch_common.h"
#include <cmath>
#include <cstdint>

namespace vnotch::services {

class HoverDetectionService final {
public:
    static constexpr int32_t kTopEdgeRevealThickness = 3;

    static constexpr bool IsPointInTopEdgeRevealZone(
        double notch_left, double notch_top, double notch_width, double notch_height,
        int32_t hover_zone_margin, double px, double py) noexcept
    {
        if (notch_width <= 0.0 || notch_height < 0.0) return false;
        double left = notch_left - hover_zone_margin;
        double right = notch_left + notch_width + hover_zone_margin;
        double bottom = notch_top + kTopEdgeRevealThickness;
        return px >= left && px <= right && py >= notch_top && py <= bottom;
    }

    static double GetDistanceToNotch(
        double notch_left, double notch_top, double notch_width, double notch_height,
        double px, double py) noexcept
    {
        double cx = notch_left + notch_width / 2.0;
        double cy = notch_top + notch_height / 2.0;
        double dx = px - cx;
        double dy = py - cy;
        return std::sqrt(dx * dx + dy * dy);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_hover_is_in_top_edge_reveal_zone(
    double notch_left, double notch_top, double notch_width, double notch_height,
    int32_t hover_zone_margin, double px, double py,
    uint8_t* out_in_zone) noexcept
{
    if (!out_in_zone) return VN_ERR_INVALID_ARG;
    *out_in_zone = vnotch::services::HoverDetectionService::IsPointInTopEdgeRevealZone(
        notch_left, notch_top, notch_width, notch_height, hover_zone_margin, px, py) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_hover_get_distance_to_notch(
    double notch_left, double notch_top, double notch_width, double notch_height,
    double px, double py,
    double* out_dist) noexcept
{
    if (!out_dist) return VN_ERR_INVALID_ARG;
    *out_dist = vnotch::services::HoverDetectionService::GetDistanceToNotch(
        notch_left, notch_top, notch_width, notch_height, px, py);
    return VN_OK;
}

} // extern "C"
