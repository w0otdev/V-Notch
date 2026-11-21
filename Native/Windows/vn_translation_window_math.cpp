#include "../vnotch_native.h"

VN_EXPORT int32_t __stdcall vn_translation_compute_popup_limits(
    double work_height,
    double dpi_scale_y,
    double* out_max_height,
    double* out_panel_max_height) noexcept
{
    if (!out_max_height || !out_panel_max_height || dpi_scale_y <= 0.0 ||
        !std::isfinite(work_height) || !std::isfinite(dpi_scale_y))
    {
        return VN_ERR_INVALID_ARG;
    }

    const double max_height = std::max(60.0, work_height / dpi_scale_y - 16.0);
    const double panel_max_height = std::max(1.0, max_height - 90.0);
    *out_max_height = max_height;
    *out_panel_max_height = panel_max_height;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_translation_compute_viewport_height(
    double panel_max_height,
    double chrome_height,
    double current_max_height,
    double* out_height,
    uint8_t* out_changed) noexcept
{
    if (!out_height || !out_changed || std::isnan(panel_max_height) || std::isnan(chrome_height))
        return VN_ERR_INVALID_ARG;

    const double height = std::clamp(panel_max_height - chrome_height, 1.0, 260.0);
    *out_height = height;
    *out_changed = std::abs(current_max_height - height) >= 0.5 ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_translation_compute_drag_position(
    double origin_x,
    double origin_y,
    double start_x,
    double start_y,
    double screen_x,
    double screen_y,
    int32_t* out_x,
    int32_t* out_y) noexcept
{
    if (!out_x || !out_y ||
        !std::isfinite(origin_x) || !std::isfinite(origin_y) ||
        !std::isfinite(start_x) || !std::isfinite(start_y) ||
        !std::isfinite(screen_x) || !std::isfinite(screen_y))
    {
        return VN_ERR_INVALID_ARG;
    }

    const double pos_x = origin_x + (screen_x - start_x);
    const double pos_y = origin_y + (screen_y - start_y);
    *out_x = static_cast<int32_t>(std::nearbyint(pos_x));
    *out_y = static_cast<int32_t>(std::nearbyint(pos_y));
    return VN_OK;
}
