#include "../vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <cstdint>

#pragma pack(push, 1)
struct VnGlassInteractionState
{
    double target_x;
    double target_y;
    double x;
    double y;
    double velocity_x;
    double velocity_y;
    double active;
    double active_target;
    double active_velocity;
    double press;
    double press_target;
    double press_velocity;
    double light_x;
    double light_y;
    double light_velocity_x;
    double light_velocity_y;
};
#pragma pack(pop)

namespace vnotch::controllers
{

class LiquidGlassInteractionState final
{
    static constexpr double kRestLightX = 0.5;
    static constexpr double kRestLightY = -0.08;
    static constexpr double kPosTol = 0.0001;
    static constexpr double kVelTol = 0.001;

    static __forceinline void Spring(
        double& value, double& velocity, double target,
        double stiffness, double damping, double dt) noexcept
    {
        velocity += (target - value) * stiffness * dt;
        velocity *= std::exp(-damping * dt);
        value += velocity * dt;
    }

    static __forceinline bool Settled(double value, double velocity, double target) noexcept
    {
        return std::abs(value - target) <= kPosTol && std::abs(velocity) <= kVelTol;
    }

public:
    static int32_t Advance(
        VnGlassInteractionState* state,
        double delta_time,
        uint8_t* out_is_settled) noexcept
    {
        if (state == nullptr || out_is_settled == nullptr)
        {
            return VN_ERR_INVALID_ARG;
        }

        double dt = std::clamp(delta_time, 0.0, 1.0 / 20.0);
        Spring(state->x, state->velocity_x, state->target_x, 150.0, 18.0, dt);
        Spring(state->y, state->velocity_y, state->target_y, 150.0, 18.0, dt);
        Spring(state->active, state->active_velocity, state->active_target, 95.0, 15.0, dt);
        Spring(state->press, state->press_velocity, state->press_target, 210.0, 18.0, dt);

        double light_target_x = kRestLightX + (state->x - kRestLightX) * state->press;
        double light_target_y = kRestLightY + (state->y - kRestLightY) * state->press;
        Spring(state->light_x, state->light_velocity_x, light_target_x, 75.0, 14.0, dt);
        Spring(state->light_y, state->light_velocity_y, light_target_y, 75.0, 14.0, dt);

        bool is_settled =
            Settled(state->x, state->velocity_x, state->target_x) &&
            Settled(state->y, state->velocity_y, state->target_y) &&
            Settled(state->active, state->active_velocity, state->active_target) &&
            Settled(state->press, state->press_velocity, state->press_target) &&
            Settled(state->light_x, state->light_velocity_x, light_target_x) &&
            Settled(state->light_y, state->light_velocity_y, light_target_y);

        if (is_settled)
        {
            state->x = state->target_x;
            state->y = state->target_y;
            state->active = state->active_target;
            state->press = state->press_target;
            state->light_x = kRestLightX + (state->x - kRestLightX) * state->press;
            state->light_y = kRestLightY + (state->y - kRestLightY) * state->press;
            state->velocity_x = state->velocity_y = state->active_velocity =
                state->press_velocity = state->light_velocity_x = state->light_velocity_y = 0.0;
        }

        *out_is_settled = is_settled ? 1u : 0u;
        return VN_OK;
    }
};

} // namespace vnotch::controllers

extern "C"
{

VN_EXPORT int32_t vn_liquid_glass_interaction_advance(
    VnGlassInteractionState* state,
    double delta_time,
    uint8_t* out_is_settled) noexcept
{
    return vnotch::controllers::LiquidGlassInteractionState::Advance(state, delta_time, out_is_settled);
}

} // extern "C"
