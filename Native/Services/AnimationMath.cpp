#include "../vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <numbers>

// Animation math that used to run in C#: the damped-spring easing curve and the progress-bar spring
// integrator. The managed shell only supplies frame times and applies the results to the visual tree.

namespace vnotch::services::animation {

#pragma pack(push, 1)
struct VnProgressSpringState {
    double display_ratio;
    double target_ratio;
    double spring_target_ratio;
    double velocity;
    int32_t settle_frames;
};
#pragma pack(pop)

constexpr double kNaturalFrequency = 12.0;

constexpr double kSpringStiffness = 105.0;
constexpr double kSpringDamping = 28.0;
constexpr double kSettleThreshold = 0.0012;
constexpr double kVelocitySettle = 0.004;
constexpr double kTargetFollowSpeed = 30.0;
constexpr double kMaxVelocity = 2.4;
constexpr double kMaxStepPerFrame = 0.030;
constexpr double kReferenceFrameSeconds = 0.0166667;
constexpr int32_t kSettleFramesRequired = 3;
constexpr double kTimeoutMs = 1400.0;
constexpr double kMinFrameSeconds = 0.001;
constexpr double kMaxFrameSeconds = 0.033;
constexpr double kMinRate = 0.5;
constexpr double kMaxRate = 3.0;

// Step response of a second-order system with the given damping ratio at normalized time t.
double StepResponse(double damping_ratio, double damped_frequency, double t) noexcept {
    if (damping_ratio == 1.0)
        return 1.0 - std::exp(-kNaturalFrequency * t) * (1.0 + kNaturalFrequency * t);
    const double decay = std::exp(-damping_ratio * kNaturalFrequency * t);
    const double phase = damped_frequency * t;
    return 1.0 - decay * (std::cos(phase) + damping_ratio * kNaturalFrequency / damped_frequency * std::sin(phase));
}

// Core easing used for EaseIn; WPF mirrors it for EaseOut (1 - core(1 - t)).
double DampedSpringCore(double damping_ratio, double normalized_time) noexcept {
    const double time = std::clamp(normalized_time, 0.0, 1.0);
    if (time == 0.0 || time == 1.0) return time;
    const double damped_frequency = kNaturalFrequency * std::sqrt(1.0 - damping_ratio * damping_ratio);
    const double end_response = StepResponse(damping_ratio, damped_frequency, 1.0);
    // Normalizing by the end response lands the last frame exactly on the target.
    return 1.0 - StepResponse(damping_ratio, damped_frequency, 1.0 - time) / end_response;
}

int32_t SignOf(double value) noexcept {
    return (value > 0.0) - (value < 0.0);
}

} // namespace vnotch::services::animation

namespace anim = vnotch::services::animation;

extern "C" {

// VN_ERR_INVALID_ARG when damping_ratio is not finite or outside (0, 1].
VN_EXPORT int32_t __stdcall vn_damped_spring_ease(double damping_ratio, double normalized_time, double* out_value) noexcept {
    if (!out_value || !std::isfinite(damping_ratio) || damping_ratio <= 0.0 || damping_ratio > 1.0) return VN_ERR_INVALID_ARG;
    *out_value = anim::DampedSpringCore(damping_ratio, normalized_time);
    return VN_OK;
}

// One integration step of the progress spring. frame_seconds is the raw time since the previous frame,
// elapsed_ms the time since the animation started. out_finished = 1 when the spring settled or timed out.
VN_EXPORT int32_t __stdcall vn_progress_spring_step(
    anim::VnProgressSpringState* state, double frame_seconds, double playback_rate, double elapsed_ms,
    uint8_t* out_finished) noexcept {
    if (!state || !out_finished) return VN_ERR_INVALID_ARG;
    *out_finished = 0;

    const double dt = std::clamp(std::isfinite(frame_seconds) ? frame_seconds : anim::kMinFrameSeconds,
                                 anim::kMinFrameSeconds, anim::kMaxFrameSeconds);
    double rate = (std::isfinite(playback_rate) && playback_rate > 0.0) ? playback_rate : 1.0;
    rate = std::clamp(rate, anim::kMinRate, anim::kMaxRate);
    const double scaled_dt = dt * rate;

    const double target_follow = 1.0 - std::exp(-anim::kTargetFollowSpeed * scaled_dt);
    state->spring_target_ratio += (state->target_ratio - state->spring_target_ratio) * target_follow;

    const double error = state->spring_target_ratio - state->display_ratio;
    const double spring_force = anim::kSpringStiffness * error - anim::kSpringDamping * state->velocity;
    state->velocity = std::clamp(state->velocity + spring_force * scaled_dt, -anim::kMaxVelocity, anim::kMaxVelocity);

    const double previous = state->display_ratio;
    state->display_ratio += state->velocity * scaled_dt;

    const double step = state->display_ratio - previous;
    const double max_step = anim::kMaxStepPerFrame * (scaled_dt / anim::kReferenceFrameSeconds);
    if (std::abs(step) > max_step) state->display_ratio = previous + anim::SignOf(step) * max_step;

    // Crossing the spring target snaps to it so the bar never oscillates past the playhead.
    if ((previous - state->spring_target_ratio) * (state->display_ratio - state->spring_target_ratio) < 0.0) {
        state->display_ratio = state->spring_target_ratio;
        state->velocity = 0.0;
    }

    if (std::abs(state->target_ratio - state->display_ratio) < anim::kSettleThreshold &&
        std::abs(state->velocity) < anim::kVelocitySettle)
        ++state->settle_frames;
    else
        state->settle_frames = 0;

    if (state->settle_frames >= anim::kSettleFramesRequired || elapsed_ms > anim::kTimeoutMs) {
        state->display_ratio = state->target_ratio;
        state->velocity = 0.0;
        state->settle_frames = 0;
        *out_finished = 1;
    }

    state->display_ratio = std::clamp(state->display_ratio, 0.0, 1.0);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_animation_compute_volume_fill_width(
    double width, double level, double* out_fill_width) noexcept
{
    if (!out_fill_width) return VN_ERR_INVALID_ARG;
    if (!std::isfinite(width) || !std::isfinite(level)) {
        *out_fill_width = 0.0;
        return VN_OK;
    }
    *out_fill_width = std::max(0.0, width) * std::clamp(level, 0.0, 1.0);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_animation_compute_update_pulse_rgb(
    double elapsed_ms, uint8_t* out_r, uint8_t* out_g, uint8_t* out_b) noexcept
{
    if (!out_r || !out_g || !out_b) return VN_ERR_INVALID_ARG;
    constexpr double kPeriodMs = 3000.0;
    double e = std::isfinite(elapsed_ms) && elapsed_ms >= 0.0 ? elapsed_ms : 0.0;
    double phase = std::fmod(e, kPeriodMs) / kPeriodMs;
    double mix = 0.5 - (0.5 * std::cos(phase * std::numbers::pi * 2.0));
    *out_r = static_cast<uint8_t>(std::clamp(std::lround(48.0 + ((255.0 - 48.0) * mix)), 0L, 255L));
    *out_g = static_cast<uint8_t>(std::clamp(std::lround(209.0 + ((255.0 - 209.0) * mix)), 0L, 255L));
    *out_b = static_cast<uint8_t>(std::clamp(std::lround(88.0 + ((255.0 - 88.0) * mix)), 0L, 255L));
    return VN_OK;
}

} // extern "C"
