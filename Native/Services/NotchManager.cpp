#include "../vnotch_common.h"

namespace vnotch::services {

#pragma pack(push, 1)
struct VnNotchGeometryOut {
    double notch_left;
    double notch_top;
    double notch_width;
    double notch_height;
    double safe_left;
    double safe_top;
    double safe_width;
    double safe_height;
};
#pragma pack(pop)

class NotchManager final {
public:
    static void ComputeNotchAndSafeAreaBounds(
        int32_t screen_left,
        int32_t screen_top,
        int32_t screen_width,
        double settings_width,
        double settings_height,
        double scale,
        double margin,
        VnNotchGeometryOut* out_geom) noexcept {
        if (!out_geom) return;
        const double notch_width = settings_width * scale;
        const double notch_height = settings_height * scale;
        const double notch_left = static_cast<double>(screen_left) + (static_cast<double>(screen_width) - notch_width) / 2.0;
        const double notch_top = static_cast<double>(screen_top);

        out_geom->notch_left = notch_left;
        out_geom->notch_top = notch_top;
        out_geom->notch_width = notch_width;
        out_geom->notch_height = notch_height;
        out_geom->safe_left = notch_left - margin;
        out_geom->safe_top = notch_top;
        out_geom->safe_width = notch_width + margin * 2.0;
        out_geom->safe_height = notch_height + margin;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_notch_manager_compute_geometry(
    int32_t screen_left,
    int32_t screen_top,
    int32_t screen_width,
    double settings_width,
    double settings_height,
    double scale,
    double margin,
    vnotch::services::VnNotchGeometryOut* out_geom) noexcept {
    if (!out_geom) return VN_E_INVALID_ARG;
    vnotch::services::NotchManager::ComputeNotchAndSafeAreaBounds(
        screen_left, screen_top, screen_width, settings_width, settings_height, scale, margin, out_geom);
    return VN_OK;
}

} // extern "C"
