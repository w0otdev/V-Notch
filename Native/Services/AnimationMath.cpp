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

VN_EXPORT int32_t __stdcall vn_animation_compute_volume_hud_state(
    double volume,
    uint8_t muted,
    int32_t tick_count,
    double track_width,
    double label_width,
    double min_left_x,
    VnVolumeHudLayoutOut* out_layout,
    uint8_t* out_tick_states,
    int32_t tick_cap) noexcept
{
    if (!out_layout || !out_tick_states || tick_count <= 0) return VN_ERR_INVALID_ARG;
    if (tick_cap < tick_count) return VN_ERR_BUFFER_TOO_SMALL;

    double clamped_vol = std::isfinite(volume) ? std::clamp(volume, 0.0, 1.0) : 0.0;
    double effective_vol = (muted != 0) ? 0.0 : clamped_vol;

    int32_t pct = static_cast<int32_t>(std::clamp(std::lround(effective_vol * 100.0), 0L, 100L));
    int32_t active = static_cast<int32_t>(std::clamp(std::lround(effective_vol * static_cast<double>(tick_count)), 0L, static_cast<long>(tick_count)));
    if (effective_vol > 0.005 && active == 0) active = 1;

    constexpr int32_t kHighlightSpan = 5;
    int32_t highlight_start = std::max(0, active - kHighlightSpan);

    int32_t codepoint = 0xE74F;
    if (muted == 0 && effective_vol > 0.01) {
        if (effective_vol < 0.33) codepoint = 0xE993;
        else if (effective_vol < 0.66) codepoint = 0xE994;
        else codepoint = 0xE995;
    }

    double safe_track = (std::isfinite(track_width) && track_width > 0.0) ? track_width : 160.0;
    double safe_label = (std::isfinite(label_width) && label_width > 0.0) ? label_width : 28.0;
    double safe_min_x = (std::isfinite(min_left_x) && min_left_x >= 0.0) ? min_left_x : 30.0;

    double edge_ratio = static_cast<double>(active) / static_cast<double>(tick_count);
    double raw_x = (edge_ratio * safe_track) - safe_label;
    double max_x = std::max(safe_min_x, safe_track - safe_label);
    double clamped_x = std::clamp(raw_x, safe_min_x, max_x);

    out_layout->percentage = pct;
    out_layout->active_ticks = active;
    out_layout->highlight_start_tick = highlight_start;
    out_layout->icon_codepoint = codepoint;
    out_layout->label_offset_x = clamped_x;

    for (int32_t i = 0; i < tick_count; ++i) {
        if (i >= active) {
            out_tick_states[i] = 0;
        } else if (i >= highlight_start) {
            out_tick_states[i] = 2;
        } else {
            out_tick_states[i] = 1;
        }
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_animation_compute_volume_hud_palette(
    int32_t tick_count,
    int32_t active_ticks,
    int32_t highlight_start_tick,
    uint8_t media_r,
    uint8_t media_g,
    uint8_t media_b,
    uint8_t has_media_tint,
    uint32_t* out_argb,
    double* out_scale_y,
    int32_t tick_cap) noexcept
{
    if (!out_argb || !out_scale_y || tick_count <= 0) return VN_ERR_INVALID_ARG;
    if (tick_cap < tick_count) return VN_ERR_BUFFER_TOO_SMALL;

    int32_t clamped_active = std::clamp(active_ticks, 0, tick_count);
    (void)highlight_start_tick;

    // Exact match with MediaPlayback.ProgressBarGradient:
    // Start (left) = vibrantTargetColor (or White when no media tint)
    // End (right)  = progressDarkColor  (vibrantTargetColor * 0.65, or SecondaryColor 0xCC when no media tint)
    double start_r = has_media_tint ? static_cast<double>(media_r) : 255.0;
    double start_g = has_media_tint ? static_cast<double>(media_g) : 255.0;
    double start_b = has_media_tint ? static_cast<double>(media_b) : 255.0;

    double end_r = has_media_tint ? (start_r * 0.65) : 204.0;
    double end_g = has_media_tint ? (start_g * 0.65) : 204.0;
    double end_b = has_media_tint ? (start_b * 0.65) : 204.0;

    int32_t denom = std::max(1, clamped_active - 1);

    for (int32_t i = 0; i < tick_count; ++i) {
        if (i >= clamped_active) {
            out_argb[i] = 0x38FFFFFFu;
            out_scale_y[i] = 0.86;
            continue;
        }

        double t = (clamped_active <= 1) ? 0.0 : (static_cast<double>(i) / static_cast<double>(denom));
        double r = start_r + (end_r - start_r) * t;
        double g = start_g + (end_g - start_g) * t;
        double b = start_b + (end_b - start_b) * t;

        uint32_t ur = static_cast<uint32_t>(std::clamp(std::lround(r), 0L, 255L));
        uint32_t ug = static_cast<uint32_t>(std::clamp(std::lround(g), 0L, 255L));
        uint32_t ub = static_cast<uint32_t>(std::clamp(std::lround(b), 0L, 255L));
        out_argb[i] = (0xFFu << 24) | (ur << 16) | (ug << 8) | ub;

        int32_t dist_from_edge = (clamped_active - 1) - i;
        if (dist_from_edge == 0) out_scale_y[i] = 1.18;
        else if (dist_from_edge == 1) out_scale_y[i] = 1.12;
        else if (dist_from_edge == 2) out_scale_y[i] = 1.06;
        else out_scale_y[i] = 1.0;
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_animation_compute_volume_overscroll_spring(
    double current_volume,
    double step_delta,
    VnVolumeHudOverscrollOut* out_overscroll) noexcept
{
    if (!out_overscroll) return VN_ERR_INVALID_ARG;
    double vol = std::isfinite(current_volume) ? std::clamp(current_volume, 0.0, 1.0) : 0.0;
    double delta = std::isfinite(step_delta) ? step_delta : 0.0;

    double raw = vol + delta;
    if (raw > 1.0001) {
        double intensity = std::clamp((raw - 1.0) / 0.15, 0.35, 1.0);
        out_overscroll->scale_x = 1.0 + 0.055 * intensity;
        out_overscroll->scale_y = 1.0 - 0.035 * intensity;
        out_overscroll->translate_x = 4.5 * intensity;
        out_overscroll->origin_x = 0.0;
    } else if (raw < -0.0001) {
        double intensity = std::clamp((-raw) / 0.15, 0.35, 1.0);
        out_overscroll->scale_x = 1.0 - 0.045 * intensity;
        out_overscroll->scale_y = 1.0 + 0.030 * intensity;
        out_overscroll->translate_x = -4.5 * intensity;
        out_overscroll->origin_x = 1.0;
    } else {
        out_overscroll->scale_x = 1.0;
        out_overscroll->scale_y = 1.0;
        out_overscroll->translate_x = 0.0;
        out_overscroll->origin_x = 0.5;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_animation_compute_volume_hud_transition(
    uint8_t entering,
    uint8_t island_mode,
    uint8_t enable_blur,
    double collapsed_width,
    double collapsed_height,
    double collapsed_radius,
    VnVolumeHudTransitionParamsOut* out_params) noexcept
{
    if (!out_params) return VN_ERR_INVALID_ARG;

    double base_w = (std::isfinite(collapsed_width) && collapsed_width > 0.0) ? collapsed_width : 196.0;
    double base_h = (std::isfinite(collapsed_height) && collapsed_height > 0.0) ? collapsed_height : 32.0;
    double base_r = (std::isfinite(collapsed_radius) && collapsed_radius >= 0.0) ? collapsed_radius : 14.0;

    constexpr double kExtraWidth = 44.0;
    constexpr double kExtraHeight = 20.0;

    if (entering != 0) {
        double target_w = base_w + kExtraWidth;
        double target_h = base_h + kExtraHeight;
        double target_r = (island_mode != 0) ? (target_h * 0.5) : std::max(base_r, 18.0);

        out_params->hud_from_scale_x = 0.86;
        out_params->hud_from_scale_y = 0.78;
        out_params->hud_to_scale_x = 1.0;
        out_params->hud_to_scale_y = 1.0;
        out_params->hud_from_translate_y = -6.0;
        out_params->hud_to_translate_y = 0.0;
        out_params->hud_from_blur = (enable_blur != 0) ? 10.0 : 0.0;
        out_params->hud_to_blur = 0.0;
        out_params->media_to_scale = 0.76;
        out_params->media_to_blur = (enable_blur != 0) ? 7.0 : 0.0;
        out_params->target_width = target_w;
        out_params->target_height = target_h;
        out_params->target_corner_radius = target_r;
        out_params->morph_duration_ms = 350;
        out_params->fade_duration_ms = 240;
    } else {
        out_params->hud_from_scale_x = 1.0;
        out_params->hud_from_scale_y = 1.0;
        out_params->hud_to_scale_x = 0.88;
        out_params->hud_to_scale_y = 0.76;
        out_params->hud_from_translate_y = 0.0;
        out_params->hud_to_translate_y = -5.5;
        out_params->hud_from_blur = 0.0;
        out_params->hud_to_blur = (enable_blur != 0) ? 9.0 : 0.0;
        out_params->media_to_scale = 1.0;
        out_params->media_to_blur = 0.0;
        out_params->target_width = base_w;
        out_params->target_height = base_h;
        out_params->target_corner_radius = base_r;
        out_params->morph_duration_ms = 340;
        out_params->fade_duration_ms = 220;
    }

    return VN_OK;
}

#ifndef VN_CATEGORY_PILL_MOTION_DEFINED
#define VN_CATEGORY_PILL_MOTION_DEFINED
#pragma pack(push, 1)
struct VnCategoryPillMotionOut
{
    double start_scale_x;
    double peak_scale_x;
    double settle_overshoot_scale_x;
    double dip_scale_y;
    double rebound_scale_y;
    double content_pop_scale;
    double glow_peak_opacity;
    int32_t peak_time_ms;
    int32_t settle_overshoot_time_ms;
    int32_t duration_ms;
};
#pragma pack(pop)
#endif

VN_EXPORT int32_t __stdcall vn_animation_compute_category_pill_motion(
    double current_x,
    double current_width,
    double target_x,
    double target_width,
    uint8_t reduced_motion,
    VnCategoryPillMotionOut* out_motion) noexcept
{
    if (!out_motion || !std::isfinite(target_width) || target_width <= 0.0)
        return VN_ERR_INVALID_ARG;

    const double safe_current_w = (std::isfinite(current_width) && current_width > 1.0)
        ? current_width
        : target_width;
    const double start_scale_x = std::clamp(safe_current_w / target_width, 0.25, 4.0);

    if (reduced_motion != 0)
    {
        out_motion->start_scale_x = 1.0;
        out_motion->peak_scale_x = 1.0;
        out_motion->settle_overshoot_scale_x = 1.0;
        out_motion->dip_scale_y = 1.0;
        out_motion->rebound_scale_y = 1.0;
        out_motion->content_pop_scale = 1.0;
        out_motion->glow_peak_opacity = 0.0;
        out_motion->peak_time_ms = 0;
        out_motion->settle_overshoot_time_ms = 0;
        out_motion->duration_ms = 0;
        return VN_OK;
    }

    const double dx = (std::isfinite(current_x) && std::isfinite(target_x))
        ? std::abs(target_x - current_x)
        : 0.0;
    const double travel = std::clamp(dx / std::max(target_width, 48.0), 0.0, 6.0);

    const double stretch_delta = std::clamp(0.065 + travel * 0.042, 0.065, 0.24);
    const double base_peak_x = std::lerp(start_scale_x, 1.0, 0.45);
    out_motion->start_scale_x = start_scale_x;
    out_motion->peak_scale_x = std::clamp(base_peak_x + stretch_delta, 0.40, 4.25);
    out_motion->settle_overshoot_scale_x = 1.0 - std::clamp(0.016 + travel * 0.007, 0.016, 0.042);

    out_motion->dip_scale_y = 1.0 - std::clamp(0.055 + travel * 0.020, 0.055, 0.135);
    out_motion->rebound_scale_y = 1.0 + std::clamp(0.022 + travel * 0.006, 0.022, 0.045);
    out_motion->content_pop_scale = std::clamp(1.045 + travel * 0.008, 1.045, 1.075);
    out_motion->glow_peak_opacity = std::clamp(0.55 + travel * 0.075, 0.55, 0.92);

    out_motion->peak_time_ms = 85;
    out_motion->settle_overshoot_time_ms = 190;
    out_motion->duration_ms = 280;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_animation_compute_settings_switch_motion(
    double track_width,
    double thumb_width,
    double margin_x,
    uint8_t is_checked,
    uint8_t is_pressed,
    uint8_t reduced_motion,
    VnSettingsSwitchMotionOut* out_motion) noexcept
{
    if (!out_motion) return VN_ERR_INVALID_ARG;

    const double safe_track = std::max(0.0, track_width);
    const double safe_thumb = std::max(1.0, thumb_width);
    const double travel = std::max(0.0, safe_track - safe_thumb - margin_x * 2.0);
    const bool checked = is_checked != 0;
    const bool pressed = is_pressed != 0;

    out_motion->target_x = checked ? travel : 0.0;

    if (reduced_motion != 0)
    {
        out_motion->peak_scale_x = 1.0;
        out_motion->dip_scale_y = 1.0;
        out_motion->rebound_scale_x = 1.0;
        out_motion->rebound_scale_y = 1.0;
        out_motion->press_scale_x = 1.0;
        out_motion->press_scale_y = 1.0;
        out_motion->press_shift_x = 0.0;
        out_motion->peak_time_ms = 0;
        out_motion->rebound_time_ms = 0;
        out_motion->duration_ms = 0;
        return VN_OK;
    }

    const double travel_ratio = std::clamp(travel / 28.0, 0.75, 1.35);
    out_motion->peak_scale_x = 1.0 + 0.22 * travel_ratio;
    out_motion->dip_scale_y = 1.0 - 0.10 * travel_ratio;
    out_motion->rebound_scale_x = 0.96;
    out_motion->rebound_scale_y = 1.035;
    out_motion->press_scale_x = pressed ? 1.15 : 1.0;
    out_motion->press_scale_y = pressed ? 0.94 : 1.0;
    out_motion->press_shift_x = pressed ? (checked ? -1.8 : 1.8) : 0.0;
    out_motion->peak_time_ms = 75;
    out_motion->rebound_time_ms = 175;
    out_motion->duration_ms = 250;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_animation_compute_nav_tab_motion(
    double current_y,
    double target_y,
    double item_height,
    uint8_t reduced_motion,
    VnNavTabMotionOut* out_motion) noexcept
{
    if (!out_motion) return VN_ERR_INVALID_ARG;

    const double safe_h = std::max(24.0, item_height);
    out_motion->target_y = std::max(0.0, target_y);
    out_motion->target_height = safe_h;

    if (reduced_motion != 0)
    {
        out_motion->peak_scale_y = 1.0;
        out_motion->dip_scale_x = 1.0;
        out_motion->rebound_scale_y = 1.0;
        out_motion->rebound_scale_x = 1.0;
        out_motion->content_shift_x = 0.0;
        out_motion->peak_time_ms = 0;
        out_motion->rebound_time_ms = 0;
        out_motion->duration_ms = 0;
        return VN_OK;
    }

    const double dist = std::abs(target_y - current_y);
    const double slots = std::clamp(dist / safe_h, 0.0, 8.0);

    out_motion->peak_scale_y = 1.0 + std::clamp(0.08 + slots * 0.042, 0.08, 0.34);
    out_motion->dip_scale_x = 1.0 - std::clamp(0.018 + slots * 0.006, 0.018, 0.055);
    out_motion->rebound_scale_y = 1.0 - std::clamp(0.025 + slots * 0.005, 0.025, 0.055);
    out_motion->rebound_scale_x = 1.0 + std::clamp(0.010 + slots * 0.002, 0.010, 0.022);
    out_motion->content_shift_x = 2.5;
    out_motion->peak_time_ms = 75;
    out_motion->rebound_time_ms = 175;
    out_motion->duration_ms = 260;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_animation_compute_elastic_slider_overscroll(
    double overflow_px,
    double usable_width,
    uint8_t reduced_motion,
    double* out_scale_x,
    double* out_scale_y) noexcept
{
    if (!out_scale_x || !out_scale_y) return VN_ERR_INVALID_ARG;
    if (reduced_motion != 0 || std::abs(overflow_px) < 0.5)
    {
        *out_scale_x = 1.0;
        *out_scale_y = 1.0;
        return VN_OK;
    }

    const double abs_over = std::abs(overflow_px);
    const double max_over = std::max(40.0, usable_width * 0.22);
    const double entry = abs_over / max_over;
    const double sigmoid = 2.0 * (1.0 / (1.0 + std::exp(-entry * 1.6)) - 0.5);

    *out_scale_x = 1.0 + std::clamp(sigmoid * 0.048, 0.0, 0.055);
    *out_scale_y = 1.0 - std::clamp(sigmoid * 0.075, 0.0, 0.085);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_animation_compute_spotlight_quick_actions_motion(
    uint8_t entering,
    double viewport_width,
    uint8_t reduced_motion,
    VnQuickActionsSlideMotionOut* out_motion) noexcept
{
    if (!out_motion) return VN_ERR_INVALID_ARG;

    if (reduced_motion != 0)
    {
        out_motion->results_from_x = 0.0;
        out_motion->results_to_x = 0.0;
        out_motion->actions_from_x = 0.0;
        out_motion->actions_to_x = 0.0;
        out_motion->item_slide_x = 0.0;
        out_motion->exit_duration_ms = 0;
        out_motion->enter_duration_ms = 0;
        out_motion->stagger_step_ms = 0;
        return VN_OK;
    }

    const double safe_w = (std::isfinite(viewport_width) && viewport_width > 120.0) ? viewport_width : 720.0;
    const double travel = std::clamp(safe_w * 0.08, 42.0, 64.0);

    if (entering != 0)
    {
        out_motion->results_from_x = 0.0;
        out_motion->results_to_x = -travel;
        out_motion->actions_from_x = travel;
        out_motion->actions_to_x = 0.0;
        out_motion->item_slide_x = 14.0;
        out_motion->exit_duration_ms = 200;
        out_motion->enter_duration_ms = 280;
        out_motion->stagger_step_ms = 18;
    }
    else
    {
        out_motion->results_from_x = -travel;
        out_motion->results_to_x = 0.0;
        out_motion->actions_from_x = 0.0;
        out_motion->actions_to_x = travel;
        out_motion->item_slide_x = 0.0;
        out_motion->exit_duration_ms = 190;
        out_motion->enter_duration_ms = 270;
        out_motion->stagger_step_ms = 0;
    }
    return VN_OK;
}

} // extern "C"
