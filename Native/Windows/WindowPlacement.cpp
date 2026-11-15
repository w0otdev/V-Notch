#include "vnotch_ext_ui.h"

#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string_view>

#include "Controls/UiValueMath.h"
using namespace vnotch::ui;

VN_EXPORT int32_t __stdcall vn_translation_clamp_placement(
    double sel_left,
    double sel_top,
    double sel_bottom,
    double work_left,
    double work_top,
    double work_right,
    double work_bottom,
    double width,
    double height,
    double* out_x,
    double* out_y) noexcept
{
    if (!out_x || !out_y)
        return VN_ERR_INVALID_ARG;

    const double max_x = MathF64Max(work_left, work_right - width);
    *out_x = ClampF64(sel_left, work_left, max_x);

    double y = sel_bottom + 8.0;
    if (y + height > work_bottom)
        y = sel_top - height - 8.0;

    const double max_y = MathF64Max(work_top, work_bottom - height);
    *out_y = ClampF64(y, work_top, max_y);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_window_calc_rounded_region(
    double width_dip,
    double height_dip,
    double margin_dip,
    double corner_radius_dip,
    double dpi_scale,
    VnRoundedWindowRegion* out_region) noexcept
{
    if (!out_region)
        return VN_ERR_INVALID_ARG;

    const double scale = (dpi_scale > 0.0 && std::isfinite(dpi_scale)) ? dpi_scale : 1.0;
    const double min_dim_dip = margin_dip * 2.0 + 1.0;

    const int32_t left = UncheckedRoundToInt32(margin_dip * scale);
    const int32_t top = left;
    const int32_t right = std::max(
        left + 1,
        UncheckedRoundToInt32((MathF64Max(width_dip, min_dim_dip) - margin_dip) * scale));
    const int32_t bottom = std::max(
        top + 1,
        UncheckedRoundToInt32((MathF64Max(height_dip, min_dim_dip) - margin_dip) * scale));
    const int32_t corner_diameter = std::max(
        1,
        UncheckedRoundToInt32(corner_radius_dip * 2.0 * scale));

    out_region->left = left;
    out_region->top = top;
    out_region->right = right;
    out_region->bottom = bottom;
    out_region->corner_diameter = corner_diameter;
    return VN_OK;
}

