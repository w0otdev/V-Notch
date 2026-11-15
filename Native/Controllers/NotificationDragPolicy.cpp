#include "vnotch_ext_ui.h"

#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string_view>

#include "Controls/UiValueMath.h"
using namespace vnotch::ui;

VN_EXPORT int32_t __stdcall vn_appnotify_step_drag_interaction(
    VnAppNotifyDragState* io_state,
    double point_x,
    double point_y,
    double origin_x,
    double origin_y,
    int64_t timestamp,
    double* out_translate_x,
    double* out_translate_y,
    double* out_opacity,
    uint8_t* out_moved,
    uint8_t* out_started_dragging) noexcept
{
    if (!io_state || !out_translate_x || !out_translate_y || !out_opacity || !out_moved || !out_started_dragging)
        return VN_ERR_INVALID_ARG;

    *out_translate_x = 0.0;
    *out_translate_y = 0.0;
    *out_opacity = 1.0;
    *out_moved = 0;
    *out_started_dragging = 0;

    const double dx = point_x - origin_x;
    const double dy = point_y - origin_y;
    if (io_state->is_dragging == 0 && std::abs(dx) < 5.0 && std::abs(dy) < 5.0)
        return VN_OK;

    if (io_state->is_dragging == 0)
    {
        io_state->is_dragging = 1;
        *out_started_dragging = 1;
    }

    const int64_t elapsed = std::max<int64_t>(1, timestamp - io_state->last_timestamp);
    io_state->velocity = (point_x - io_state->last_x) / static_cast<double>(elapsed);
    io_state->last_x = point_x;
    io_state->last_y = point_y;
    io_state->last_timestamp = timestamp;

    *out_translate_x = dx / (1.0 + std::abs(dx) / 280.0);
    *out_translate_y = ClampF64(dy * 0.12, -12.0, 12.0);
    *out_opacity = ClampF64(1.0 - std::abs(dx) / 360.0, 0.45, 1.0);
    *out_moved = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_appnotify_eval_drag_release(
    double point_x,
    double origin_x,
    int64_t last_timestamp,
    int64_t timestamp,
    double velocity,
    uint8_t is_dragging,
    uint8_t* out_dismiss,
    int32_t* out_dismiss_direction,
    uint8_t* out_is_click) noexcept
{
    if (!out_dismiss || !out_dismiss_direction || !out_is_click)
        return VN_ERR_INVALID_ARG;

    const double delta = point_x - origin_x;
    const double eff_velocity = (timestamp - last_timestamp <= 100) ? velocity : 0.0;
    const int32_t delta_sign = delta > 0.0 ? 1 : (delta < 0.0 ? -1 : 0);
    const int32_t vel_sign = eff_velocity > 0.0 ? 1 : (eff_velocity < 0.0 ? -1 : 0);

    const bool dismiss = (is_dragging != 0) &&
                         (std::abs(delta) >= 72.0 ||
                          (std::abs(delta) >= 16.0 && std::abs(eff_velocity) >= 0.65 && delta_sign == vel_sign));

    *out_dismiss = dismiss ? 1 : 0;
    *out_dismiss_direction = delta_sign;
    *out_is_click = (is_dragging == 0) ? 1 : 0;
    return VN_OK;
}

