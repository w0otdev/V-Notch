#include "../vnotch_native.h"
#include <algorithm>
#include <cmath>

namespace {
void Step(double& position, double& velocity, double target, double frequency, double seconds) noexcept
{
    const double offset = position - target;
    const double impulse = velocity + frequency * offset;
    const double decay = std::exp(-frequency * seconds);
    position = target + (offset + impulse * seconds) * decay;
    velocity = (velocity - frequency * impulse * seconds) * decay;
}

bool Rest(double position, double velocity, double target, double position_epsilon, double velocity_epsilon) noexcept
{
    return std::abs(position - target) < position_epsilon && std::abs(velocity) < velocity_epsilon;
}
}

VN_EXPORT int32_t __stdcall vn_animation_step_menu_highlight(
    VnNavPillSpringState* state, double target_y, double item_height, double elapsed_seconds,
    uint8_t reduced_motion, VnNavPillVisual* visual, uint8_t* settled) noexcept
{
    if (!state || !visual || !settled) return VN_ERR_INVALID_ARG;
    const double target = std::isfinite(target_y) ? std::clamp(target_y, 0.0, 1e6) : 0.0;
    const double height = std::isfinite(item_height) ? std::clamp(item_height, 16.0, 1e6) : 32.0;
    const double seconds = std::isfinite(elapsed_seconds) ? std::clamp(elapsed_seconds, 0.0, 0.1) : 0.0;
    const bool valid = std::isfinite(state->top) && std::isfinite(state->bottom) &&
        std::isfinite(state->top_velocity) && std::isfinite(state->bottom_velocity) &&
        std::isfinite(state->width_scale) && std::isfinite(state->width_velocity) &&
        std::abs(state->top) < 1e8 && state->bottom > state->top && state->bottom < 1e8 &&
        std::abs(state->top_velocity) < 1e9 && std::abs(state->bottom_velocity) < 1e9 &&
        state->width_scale > .1 && state->width_scale < 4;
    *settled = 0;
    if (reduced_motion || !valid)
    {
        *state = {target, target + height, 0, 0, 1, 0};
        *settled = 1;
    }
    else if (seconds > 0)
    {
        double center = (state->top + state->bottom) * .5;
        double velocity = (state->top_velocity + state->bottom_velocity) * .5;
        double extent = state->bottom - state->top;
        double extent_velocity = state->bottom_velocity - state->top_velocity;
        Step(center, velocity, target + height * .5, 30.0, seconds);
        // Stretch follows speed smoothly in either direction. Reversing never
        // swaps leading/trailing spring constants or produces a tall ribbon.
        const double speed = std::hypot(velocity, 40.0) - 40.0;
        const double travel = speed / (speed + 600.0);
        Step(extent, extent_velocity, height * (1.0 + .24 * travel), 32.0, seconds);
        Step(state->width_scale, state->width_velocity, 1.0 - .035 * travel, 30.0, seconds);
        state->top = center - extent * .5;
        state->bottom = center + extent * .5;
        state->top_velocity = velocity - extent_velocity * .5;
        state->bottom_velocity = velocity + extent_velocity * .5;
        if (Rest(center, velocity, target + height * .5, .02, .2) &&
            Rest(extent, extent_velocity, height, .02, .2) &&
            Rest(state->width_scale, state->width_velocity, 1.0, .0004, .008))
        {
            *state = {target, target + height, 0, 0, 1, 0};
            *settled = 1;
        }
    }
    visual->translate_y = (state->top + state->bottom - height) * .5;
    visual->scale_y = (state->bottom - state->top) / height;
    visual->scale_x = state->width_scale;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_animation_step_menu_surface(
    VnMenuSurfaceMotionState* state, double elapsed_seconds, uint8_t reduced_motion, uint8_t* settled) noexcept
{
    if (!state || !settled) return VN_ERR_INVALID_ARG;
    const double seconds = std::isfinite(elapsed_seconds) ? std::clamp(elapsed_seconds, 0.0, .1) : 0.0;
    const bool valid = std::isfinite(state->scale_x) && std::isfinite(state->scale_y) &&
        std::isfinite(state->x) && std::isfinite(state->y) && std::isfinite(state->opacity) &&
        std::isfinite(state->scale_x_velocity) && std::isfinite(state->scale_y_velocity) &&
        std::isfinite(state->x_velocity) && std::isfinite(state->y_velocity) && std::isfinite(state->opacity_velocity) &&
        state->scale_x > .1 && state->scale_x < 4 && state->scale_y > .1 && state->scale_y < 4 &&
        std::abs(state->x) < 1e6 && std::abs(state->y) < 1e6;
    *settled = 0;
    if (reduced_motion || !valid)
    {
        *state = {1, 1, 0, 0, 1, 0, 0, 0, 0, 0};
        *settled = 1;
        return VN_OK;
    }
    if (seconds > 0)
    {
        Step(state->scale_x, state->scale_x_velocity, 1, 30, seconds);
        Step(state->scale_y, state->scale_y_velocity, 1, 34, seconds);
        Step(state->x, state->x_velocity, 0, 30, seconds);
        Step(state->y, state->y_velocity, 0, 32, seconds);
        Step(state->opacity, state->opacity_velocity, 1, 42, seconds);
    }
    if (Rest(state->scale_x, state->scale_x_velocity, 1, .0001, .002) &&
        Rest(state->scale_y, state->scale_y_velocity, 1, .0001, .002) &&
        Rest(state->x, state->x_velocity, 0, .015, .15) && Rest(state->y, state->y_velocity, 0, .015, .15) &&
        Rest(state->opacity, state->opacity_velocity, 1, .001, .01))
    {
        *state = {1, 1, 0, 0, 1, 0, 0, 0, 0, 0};
        *settled = 1;
    }
    return VN_OK;
}
