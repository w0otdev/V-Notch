#include "../vnotch_common.h"
#include <cmath>

namespace vnotch::services {

class WindowScreenGeometry final {
public:
    static void ComputeScreenPosition(
        double origin_x,
        double origin_y,
        double local_x,
        double local_y,
        double dpi_scale_x,
        double dpi_scale_y,
        double* out_x,
        double* out_y,
        bool* out_finite) noexcept {
        const double px = origin_x + std::round(local_x * dpi_scale_x);
        const double py = origin_y + std::round(local_y * dpi_scale_y);
        if (out_x) *out_x = px;
        if (out_y) *out_y = py;
        if (out_finite) *out_finite = std::isfinite(px) && std::isfinite(py);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_window_screen_geometry_compute_position(
    double origin_x,
    double origin_y,
    double local_x,
    double local_y,
    double dpi_scale_x,
    double dpi_scale_y,
    double* out_x,
    double* out_y,
    uint8_t* out_finite) noexcept {
    if (!out_x || !out_y || !out_finite) return VN_E_INVALID_ARG;
    bool finite = false;
    vnotch::services::WindowScreenGeometry::ComputeScreenPosition(
        origin_x, origin_y, local_x, local_y, dpi_scale_x, dpi_scale_y, out_x, out_y, &finite);
    *out_finite = finite ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
