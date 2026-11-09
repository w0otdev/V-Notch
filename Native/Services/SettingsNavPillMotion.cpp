#include "../vnotch_native.h"
#include <algorithm>
#include <cmath>

namespace {
void AdvanceSpring(double& position, double& velocity, double target, double frequency, double seconds) noexcept
{
    // Closed-form critically damped spring: stable across frame rates and keeps
    // the current velocity when the destination changes mid-flight.
    const double offset = position - target;
    const double impulse = velocity + frequency * offset;
    const double decay = std::exp(-frequency * seconds);
    position = target + (offset + impulse * seconds) * decay;
    velocity = (velocity - frequency * impulse * seconds) * decay;
}

bool Valid(const VnNavPillSpringState& state) noexcept
{
    return std::isfinite(state.top) && std::isfinite(state.bottom) &&
        std::isfinite(state.top_velocity) && std::isfinite(state.bottom_velocity) &&
        std::isfinite(state.width_scale) && std::isfinite(state.width_velocity) &&
        std::abs(state.top) < 1e8 && std::abs(state.bottom) < 1e8 &&
        std::abs(state.top_velocity) < 1e9 && std::abs(state.bottom_velocity) < 1e9 &&
        state.width_scale > 0.1 && state.width_scale < 4;
}
}

VN_EXPORT int32_t __stdcall vn_animation_step_nav_pill(
    VnNavPillSpringState* state, double target_y, double item_height,
    double elapsed_seconds, uint8_t reduced_motion,
    VnNavPillVisual* out_visual, uint8_t* out_settled) noexcept
{
    if (!state || !out_visual || !out_settled) return VN_ERR_INVALID_ARG;
    const double height = std::isfinite(item_height) ? std::clamp(item_height, 24.0, 1e6) : 30.0;
    const double target = std::isfinite(target_y) ? std::clamp(target_y, 0.0, 1e6) : 0.0;
    const double seconds = std::isfinite(elapsed_seconds) ? std::clamp(elapsed_seconds, 0.0, 0.1) : 0.0;
    *out_settled = 0;

    if (reduced_motion || !Valid(*state))
    {
        *state = {target, target + height, 0, 0, 1, 0};
        *out_settled = 1;
    }
    else if (seconds > 0)
    {
        const double center = (state->top + state->bottom) * 0.5;
        const double distance = target + height * 0.5 - center;
        const double slots = std::abs(distance) / height;
        const double leading = 36.0;
        const double trailing = leading - 15.0 / (1.0 + slots * 0.25);
        AdvanceSpring(state->top, state->top_velocity, target, distance >= 0 ? trailing : leading, seconds);
        AdvanceSpring(state->bottom, state->bottom_velocity, target + height, distance >= 0 ? leading : trailing, seconds);

        const double stretch = std::clamp((state->bottom - state->top) / height, 0.72, 2.1);
        const double width = std::clamp(1.0 / std::sqrt(stretch), 0.90, 1.035);
        AdvanceSpring(state->width_scale, state->width_velocity, width, 32.0, seconds);

        if (std::abs(state->top - target) < 0.025 && std::abs(state->bottom - target - height) < 0.025 &&
            std::abs(state->top_velocity) < 0.25 && std::abs(state->bottom_velocity) < 0.25 &&
            std::abs(state->width_scale - 1.0) < 0.0005 && std::abs(state->width_velocity) < 0.01)
        {
            *state = {target, target + height, 0, 0, 1, 0};
            *out_settled = 1;
        }
    }

    out_visual->translate_y = (state->top + state->bottom - height) * 0.5;
    out_visual->scale_y = std::clamp((state->bottom - state->top) / height, 0.72, 2.1);
    out_visual->scale_x = std::clamp(state->width_scale, 0.90, 1.035);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_animation_step_nav_hover(
    VnNavHoverSpringState* state, double target_y, uint8_t visible, uint8_t selected,
    double elapsed_seconds, uint8_t reduced_motion, uint8_t* out_settled) noexcept
{
    if (!state || !out_settled) return VN_ERR_INVALID_ARG;
    const double target = std::isfinite(target_y) ? std::clamp(target_y, 0.0, 1e6) : 0.0;
    const double opacity = visible ? 1.0 : 0.0;
    const double selected_opacity = selected ? 1.0 : 0.0;
    const double seconds = std::isfinite(elapsed_seconds) ? std::clamp(elapsed_seconds, 0.0, 0.1) : 0.0;
    const bool valid = std::isfinite(state->y) && std::isfinite(state->y_velocity) &&
        std::isfinite(state->opacity) && std::isfinite(state->opacity_velocity) &&
        std::isfinite(state->selected_opacity) && std::isfinite(state->selected_velocity) &&
        std::abs(state->y) < 1e8 && std::abs(state->y_velocity) < 1e9;
    *out_settled = 0;
    if (reduced_motion || !valid)
    {
        *state = {target, 0, opacity, 0, selected_opacity, 0};
        *out_settled = 1;
        return VN_OK;
    }

    AdvanceSpring(state->y, state->y_velocity, target, 34.0, seconds);
    AdvanceSpring(state->opacity, state->opacity_velocity, opacity, 32.0, seconds);
    AdvanceSpring(state->selected_opacity, state->selected_velocity, selected_opacity, 32.0, seconds);
    if (std::abs(state->y - target) < 0.01 && std::abs(state->y_velocity) < 0.1 &&
        std::abs(state->opacity - opacity) < 0.001 && std::abs(state->opacity_velocity) < 0.01 &&
        std::abs(state->selected_opacity - selected_opacity) < 0.001 && std::abs(state->selected_velocity) < 0.01)
    {
        *state = {target, 0, opacity, 0, selected_opacity, 0};
        *out_settled = 1;
    }
    return VN_OK;
}
