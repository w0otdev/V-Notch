#include "../vnotch_native.h"

VN_EXPORT int32_t __stdcall vn_translation_compute_character_fades(
    double elapsed,
    double interval,
    double fade_ms,
    int32_t brush_count,
    int32_t completed_index,
    double* out_opacities,
    int32_t opacity_cap,
    int32_t* out_start_index,
    int32_t* out_end_index,
    int32_t* out_new_completed) noexcept
{
    if (!out_start_index || !out_end_index || !out_new_completed ||
        brush_count < 0 || completed_index < 0 || opacity_cap < 0 ||
        interval <= 0.0 || fade_ms <= 0.0 ||
        !std::isfinite(elapsed) || !std::isfinite(interval) || !std::isfinite(fade_ms))
    {
        return VN_ERR_INVALID_ARG;
    }

    const int32_t active_end = std::min(brush_count, static_cast<int32_t>(elapsed / interval) + 1);
    const int32_t count = active_end > completed_index ? (active_end - completed_index) : 0;
    if (count > 0 && !out_opacities)
        return VN_ERR_INVALID_ARG;
    if (count > opacity_cap)
        return VN_ERR_BUFFER_TOO_SMALL;

    int32_t new_completed = completed_index;
    for (int32_t i = completed_index; i < active_end; ++i)
    {
        const double progress = std::clamp((elapsed - static_cast<double>(i) * interval) / fade_ms, 0.0, 1.0);
        const double inv = 1.0 - progress;
        out_opacities[i - completed_index] = 1.0 - inv * inv;
        if (std::abs(progress - 1.0) < 1e-6)
            new_completed = i + 1;
    }

    *out_start_index = completed_index;
    *out_end_index = active_end > completed_index ? active_end : completed_index;
    *out_new_completed = new_completed;
    return VN_OK;
}

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
