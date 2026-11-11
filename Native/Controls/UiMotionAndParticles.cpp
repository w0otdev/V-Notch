#include "../vnotch_native.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>
#include <numbers>

namespace
{
    constexpr int32_t kMinParticles = 24;
    constexpr int32_t kMaxParticles = 180;
    constexpr int32_t kTintVariants = 3;
    constexpr double kR2AlphaX = 0.7548776662466927;
    constexpr double kR2AlphaY = 0.5698402909980532;
    constexpr float kTau = 2.0f * std::numbers::pi_v<float>;

    [[nodiscard]] constexpr float dust_next_float(uint32_t& rng_state) noexcept
    {
        uint32_t x = rng_state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        rng_state = x;
        return static_cast<float>(x & 0x00FFFFFFu) * (1.0f / 16777216.0f);
    }

    [[nodiscard]] inline float dust_compute_lifecycle_alpha(
        float life,
        float peak_alpha,
        float twinkle_phase) noexcept
    {
        float envelope = 1.0f;
        if (life < 0.22f)
        {
            const float t = life * (1.0f / 0.22f);
            envelope = t * t * (3.0f - 2.0f * t);
        }
        else if (life > 0.74f)
        {
            const float t = (1.0f - life) * (1.0f / 0.26f);
            envelope = std::clamp(t * t * (3.0f - 2.0f * t), 0.0f, 1.0f);
        }

        const float twinkle = 0.82f + 0.18f * std::sin(life * kTau * 1.5f + twinkle_phase);
        return peak_alpha * envelope * twinkle;
    }
}

#ifndef VN_DUST_PARTICLE_DEFINED
#define VN_DUST_PARTICLE_DEFINED
#pragma pack(push, 1)
struct VnDustParticle
{
    float x;
    float y;
    float vx;
    float vy;
    float base_radius;
    float radius;
    float peak_alpha;
    float alpha;
    float life;
    float life_speed;
    float twinkle_phase;
    uint8_t tint_index;
};
#pragma pack(pop)
#endif

namespace
{
    inline void dust_spawn_particle(
        VnDustParticle& p,
        double width,
        double height,
        bool random_life_phase,
        uint32_t& rng_state,
        int32_t& r2_sequence_index) noexcept
    {
        const int32_t seq = ++r2_sequence_index;
        double u = std::fmod(0.5 + static_cast<double>(seq) * kR2AlphaX, 1.0);
        double v = std::fmod(0.5 + static_cast<double>(seq) * kR2AlphaY, 1.0);
        if (u < 0.0) u += 1.0;
        if (v < 0.0) v += 1.0;

        const double jitter_x = (static_cast<double>(dust_next_float(rng_state)) - 0.5) * 4.5;
        const double jitter_y = (static_cast<double>(dust_next_float(rng_state)) - 0.5) * 3.0;

        const double pad_x = std::min(3.0, width * 0.04);
        const double pad_y = std::min(2.0, height * 0.08);
        const double usable_w = std::max(4.0, width - pad_x * 2.0);
        const double usable_h = std::max(4.0, height - pad_y * 2.0);

        const float x = static_cast<float>(std::clamp(pad_x + u * usable_w + jitter_x, pad_x, width - pad_x));
        const float y = static_cast<float>(std::clamp(pad_y + v * usable_h + jitter_y, pad_y, height - pad_y));

        const float size_roll = dust_next_float(rng_state);
        float radius;
        if (size_roll < 0.62f)
            radius = 0.62f + dust_next_float(rng_state) * 0.24f;
        else if (size_roll < 0.90f)
            radius = 0.88f + dust_next_float(rng_state) * 0.24f;
        else
            radius = 1.14f + dust_next_float(rng_state) * 0.22f;

        const float angle = dust_next_float(rng_state) * kTau;
        const float speed = 7.5f + dust_next_float(rng_state) * 14.5f;

        const float lifetime = 0.60f + dust_next_float(rng_state) * 0.85f;
        const float life = random_life_phase ? dust_next_float(rng_state) : 0.0f;
        const float peak_alpha = size_roll < 0.62f
            ? (0.42f + dust_next_float(rng_state) * 0.46f)
            : (0.55f + dust_next_float(rng_state) * 0.40f);

        const uint8_t tint = static_cast<uint8_t>((seq % kTintVariants + kTintVariants) % kTintVariants);

        p.x = x;
        p.y = y;
        p.vx = std::cos(angle) * speed;
        p.vy = std::sin(angle) * speed * 0.78f;
        p.base_radius = radius;
        p.radius = radius;
        p.peak_alpha = peak_alpha;
        p.life = life;
        p.life_speed = 1.0f / lifetime;
        p.twinkle_phase = dust_next_float(rng_state) * kTau;
        p.tint_index = tint;
        p.alpha = dust_compute_lifecycle_alpha(life, peak_alpha, p.twinkle_phase);
    }
}

extern "C" VN_EXPORT int32_t __stdcall vn_dust_particles_init(
    VnDustParticle* particles,
    int32_t capacity,
    double width,
    double height,
    uint32_t* io_rng_state,
    int32_t* io_r2_sequence,
    int32_t* out_active_count) noexcept
{
    if (!particles || capacity <= 0 || !io_rng_state || !io_r2_sequence || !out_active_count)
        return VN_ERR_INVALID_ARG;

    const double w = std::max(12.0, width);
    const double h = std::max(14.0, height);

    const int32_t target_count = static_cast<int32_t>(std::rint((w * h) / 22.0));
    const int32_t max_allowed = std::min(kMaxParticles, capacity);
    const int32_t min_allowed = std::min(kMinParticles, max_allowed);
    const int32_t active_count = std::clamp(target_count, min_allowed, max_allowed);

    uint32_t rng = *io_rng_state;
    if (rng == 0u) rng = 1u;
    int32_t seq = *io_r2_sequence;

    for (int32_t i = 0; i < active_count; ++i)
    {
        dust_spawn_particle(particles[i], w, h, true, rng, seq);
    }

    *io_rng_state = rng;
    *io_r2_sequence = seq;
    *out_active_count = active_count;
    return VN_OK;
}

extern "C" VN_EXPORT int32_t __stdcall vn_dust_particles_step(
    VnDustParticle* particles,
    int32_t active_count,
    double width,
    double height,
    float dt,
    uint8_t is_dissolving,
    double dissolve_progress,
    double reform_progress,
    uint32_t* io_rng_state,
    int32_t* io_r2_sequence) noexcept
{
    if (!particles || active_count < 0 || !io_rng_state || !io_r2_sequence)
        return VN_ERR_INVALID_ARG;

    if (active_count == 0)
        return VN_OK;

    if (is_dissolving != 0)
    {
        const float drag = std::exp(-4.8f * dt);
        const float remain = static_cast<float>(1.0 - dissolve_progress);
        const float alpha_fade = remain * remain;
        const float radius_scale = 1.0f - static_cast<float>(dissolve_progress) * 0.35f;

        for (int32_t i = 0; i < active_count; ++i)
        {
            VnDustParticle& p = particles[i];
            p.x += p.vx * dt;
            p.y += p.vy * dt;
            p.vx *= drag;
            p.vy *= drag;
            p.radius = std::max(0.35f, p.base_radius * radius_scale);
            p.alpha = p.peak_alpha * alpha_fade;
        }
        return VN_OK;
    }

    const double w = std::max(12.0, width);
    const double h = std::max(14.0, height);
    const float max_w = static_cast<float>(w) - 1.5f;
    const float max_h = static_cast<float>(h) - 1.5f;
    const float reform_factor = static_cast<float>(reform_progress);

    uint32_t rng = *io_rng_state;
    if (rng == 0u) rng = 1u;
    int32_t seq = *io_r2_sequence;

    for (int32_t i = 0; i < active_count; ++i)
    {
        VnDustParticle& p = particles[i];
        p.life += p.life_speed * dt;

        const float curl = std::sin(p.twinkle_phase + p.life * 4.2f) * 6.0f;
        p.x += (p.vx + curl * 0.25f) * dt;
        p.y += (p.vy - curl * 0.18f) * dt;

        if (p.life >= 1.0f || p.x < 1.5f || p.x > max_w || p.y < 1.5f || p.y > max_h)
        {
            dust_spawn_particle(p, w, h, false, rng, seq);
        }

        p.alpha = dust_compute_lifecycle_alpha(p.life, p.peak_alpha, p.twinkle_phase) * reform_factor;
    }

    *io_rng_state = rng;
    *io_r2_sequence = seq;
    return VN_OK;
}

extern "C" VN_EXPORT int32_t __stdcall vn_dust_particles_dissolve_impulse(
    VnDustParticle* particles,
    int32_t active_count,
    double width,
    double height,
    float origin_x,
    float origin_y,
    uint32_t* io_rng_state) noexcept
{
    if (!particles || active_count < 0 || !io_rng_state)
        return VN_ERR_INVALID_ARG;

    const double w = std::max(12.0, width);
    const double h = std::max(14.0, height);
    const float max_dist = std::max(24.0f, std::sqrt(static_cast<float>(w * w + h * h)));

    uint32_t rng = *io_rng_state;
    if (rng == 0u) rng = 1u;

    for (int32_t i = 0; i < active_count; ++i)
    {
        VnDustParticle& p = particles[i];
        const float dx = p.x - origin_x;
        const float dy = p.y - origin_y;
        const float dist = std::sqrt(dx * dx + dy * dy);

        const float base_angle = dist > 0.5f ? std::atan2(dy, dx) : (dust_next_float(rng) * kTau);
        const float scatter_angle = base_angle + (dust_next_float(rng) - 0.5f) * 0.65f;

        const float proximity = 1.0f - std::clamp(dist / max_dist, 0.0f, 0.75f);
        const float speed = (85.0f + dust_next_float(rng) * 165.0f) * (0.75f + 0.55f * proximity);

        p.vx = std::cos(scatter_angle) * speed;
        p.vy = std::sin(scatter_angle) * speed;
        p.peak_alpha = std::max(p.alpha, 0.45f + dust_next_float(rng) * 0.45f);
    }

    *io_rng_state = rng;
    return VN_OK;
}

extern "C" VN_EXPORT int32_t __stdcall vn_morph_settings_icon_sample_contour(
    const double* raw_xy,
    int32_t raw_point_count,
    const double* matrix6,
    uint8_t is_closed,
    int32_t sample_count,
    double* out_sampled_xy,
    int32_t out_cap_doubles,
    double* out_center_x,
    double* out_center_y,
    double* out_min_x,
    double* out_bbox_area) noexcept
{
    if (!raw_xy || raw_point_count < 2 || !matrix6 || sample_count <= 0 ||
        !out_sampled_xy || !out_center_x || !out_center_y || !out_min_x || !out_bbox_area)
        return VN_ERR_INVALID_ARG;

    if (out_cap_doubles < sample_count * 2)
        return VN_ERR_BUFFER_TOO_SMALL;

    // shortcut: stack contour buffer caps at 2048 flattened vertices per figure, upgrade if complex SVG paths exceed 2048 segments
    constexpr int32_t kMaxVertices = 2048;
    double vertices_xy[(kMaxVertices + 1) * 2];
    double lengths[kMaxVertices + 1];

    const double m11 = matrix6[0];
    const double m12 = matrix6[1];
    const double m21 = matrix6[2];
    const double m22 = matrix6[3];
    const double ox = matrix6[4];
    const double oy = matrix6[5];

    int32_t v_count = 0;
    const int32_t limit = std::min(raw_point_count, kMaxVertices);
    for (int32_t i = 0; i < limit; ++i)
    {
        const double px = raw_xy[i * 2];
        const double py = raw_xy[i * 2 + 1];
        const double tx = px * m11 + py * m21 + ox;
        const double ty = px * m12 + py * m22 + oy;
        if (!std::isfinite(tx) || !std::isfinite(ty))
            continue;

        if (v_count > 0)
        {
            const double dx = tx - vertices_xy[(v_count - 1) * 2];
            const double dy = ty - vertices_xy[(v_count - 1) * 2 + 1];
            if (std::sqrt(dx * dx + dy * dy) <= 0.000001)
                continue;
        }

        vertices_xy[v_count * 2] = tx;
        vertices_xy[v_count * 2 + 1] = ty;
        ++v_count;
    }

    if (v_count < 2)
        return VN_ERR_FAIL;

    if (is_closed != 0)
    {
        const double dx = vertices_xy[(v_count - 1) * 2] - vertices_xy[0];
        const double dy = vertices_xy[(v_count - 1) * 2 + 1] - vertices_xy[1];
        if (std::sqrt(dx * dx + dy * dy) > 0.000001)
        {
            vertices_xy[v_count * 2] = vertices_xy[0];
            vertices_xy[v_count * 2 + 1] = vertices_xy[1];
            ++v_count;
        }
    }

    lengths[0] = 0.0;
    for (int32_t i = 1; i < v_count; ++i)
    {
        const double dx = vertices_xy[i * 2] - vertices_xy[(i - 1) * 2];
        const double dy = vertices_xy[i * 2 + 1] - vertices_xy[(i - 1) * 2 + 1];
        lengths[i] = lengths[i - 1] + std::sqrt(dx * dx + dy * dy);
    }

    const double total_length = lengths[v_count - 1];
    if (!std::isfinite(total_length) || total_length <= 0.000001)
        return VN_ERR_FAIL;

    int32_t edge = 1;
    double min_x = std::numeric_limits<double>::infinity();
    double max_x = -std::numeric_limits<double>::infinity();
    double min_y = std::numeric_limits<double>::infinity();
    double max_y = -std::numeric_limits<double>::infinity();
    double sum_x = 0.0;
    double sum_y = 0.0;

    for (int32_t i = 0; i < sample_count; ++i)
    {
        const double distance = total_length * static_cast<double>(i) / static_cast<double>(sample_count);
        while (edge < v_count - 1 && lengths[edge] < distance)
            ++edge;

        const double seg_len = lengths[edge] - lengths[edge - 1];
        const double fraction = seg_len > 0.0 ? (distance - lengths[edge - 1]) / seg_len : 0.0;
        const double x0 = vertices_xy[(edge - 1) * 2];
        const double y0 = vertices_xy[(edge - 1) * 2 + 1];
        const double x1 = vertices_xy[edge * 2];
        const double y1 = vertices_xy[edge * 2 + 1];
        const double sx = x0 + (x1 - x0) * fraction;
        const double sy = y0 + (y1 - y0) * fraction;

        out_sampled_xy[i * 2] = sx;
        out_sampled_xy[i * 2 + 1] = sy;

        if (sx < min_x) min_x = sx;
        if (sx > max_x) max_x = sx;
        if (sy < min_y) min_y = sy;
        if (sy > max_y) max_y = sy;
        sum_x += sx;
        sum_y += sy;
    }

    *out_center_x = sum_x / static_cast<double>(sample_count);
    *out_center_y = sum_y / static_cast<double>(sample_count);
    *out_min_x = min_x;
    *out_bbox_area = (max_x - min_x) * (max_y - min_y);
    return VN_OK;
}

extern "C" VN_EXPORT int32_t __stdcall vn_morph_settings_icon_compute_points(
    const double* from_xy,
    int32_t from_point_count,
    const double* to_xy,
    int32_t to_point_count,
    double center_x,
    double center_y,
    double progress,
    int32_t sample_count,
    double* out_xy,
    int32_t out_cap_doubles,
    int32_t* out_point_count) noexcept
{
    if (sample_count <= 0 || !out_xy || !out_point_count)
        return VN_ERR_INVALID_ARG;

    if (out_cap_doubles < sample_count * 2)
        return VN_ERR_BUFFER_TOO_SMALL;

    if (from_xy != nullptr || to_xy != nullptr)
    {
        for (int32_t j = 0; j < sample_count; ++j)
        {
            const double ax = (from_xy != nullptr && j < from_point_count) ? from_xy[j * 2] : center_x;
            const double ay = (from_xy != nullptr && j < from_point_count) ? from_xy[j * 2 + 1] : center_y;
            const double bx = (to_xy != nullptr && j < to_point_count) ? to_xy[j * 2] : center_x;
            const double by = (to_xy != nullptr && j < to_point_count) ? to_xy[j * 2 + 1] : center_y;

            out_xy[j * 2] = ax + (bx - ax) * progress;
            out_xy[j * 2 + 1] = ay + (by - ay) * progress;
        }
        *out_point_count = sample_count;
        return VN_OK;
    }

    // Procedural gear/circle morph fallback when no explicit contours are provided.
    constexpr double kTwoPi = 2.0 * std::numbers::pi_v<double>;
    constexpr int32_t kTeeth = 8;
    const double t = std::clamp(progress, 0.0, 1.0);
    const double rotation = t * (std::numbers::pi_v<double> * 0.25);
    const double base_r = 18.0;
    const double tooth_amp = 4.0 * (1.0 - t);

    for (int32_t j = 0; j < sample_count; ++j)
    {
        const double angle = rotation + kTwoPi * static_cast<double>(j) / static_cast<double>(sample_count);
        const double tooth_wave = std::clamp(std::cos(angle * kTeeth) * 2.5, -1.0, 1.0);
        const double r = base_r + tooth_amp * tooth_wave;
        out_xy[j * 2] = center_x + std::cos(angle) * r;
        out_xy[j * 2 + 1] = center_y + std::sin(angle) * r;
    }

    *out_point_count = sample_count;
    return VN_OK;
}

extern "C" VN_EXPORT int32_t __stdcall vn_clipboard_motion_compute_stagger(
    const int32_t* candidate_indices,
    int32_t count,
    int32_t base_delay_ms,
    int32_t* out_sorted_positions,
    int32_t* out_delays_ms) noexcept
{
    if (count < 0 || (count > 0 && (!candidate_indices || !out_sorted_positions || !out_delays_ms)))
        return VN_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; ++i)
    {
        out_sorted_positions[i] = i;
    }

    // Stable insertion sort matching Enumerable.OrderBy
    for (int32_t i = 1; i < count; ++i)
    {
        const int32_t pos = out_sorted_positions[i];
        const int32_t key = candidate_indices[pos];
        int32_t j = i - 1;
        while (j >= 0 && candidate_indices[out_sorted_positions[j]] > key)
        {
            out_sorted_positions[j + 1] = out_sorted_positions[j];
            --j;
        }
        out_sorted_positions[j + 1] = pos;
    }

    for (int32_t k = 0; k < count; ++k)
    {
        out_delays_ms[k] = std::min(k, 7) * 40 + base_delay_ms;
    }

    return VN_OK;
}

extern "C" VN_EXPORT int32_t __stdcall vn_clipboard_motion_card_delay(
    int32_t item_index,
    int32_t animated_count,
    int32_t base_delay_ms,
    int32_t* out_delay_ms) noexcept
{
    if (!out_delay_ms)
        return VN_ERR_INVALID_ARG;

    const int32_t visual_index = item_index >= 0 ? item_index : std::max(0, animated_count - 1);
    *out_delay_ms = std::min(visual_index, 7) * 40 + base_delay_ms;
    return VN_OK;
}

extern "C" VN_EXPORT int32_t __stdcall vn_clipboard_motion_compute_reflow_offset(
    double origin_x,
    double rendered_offset_x,
    double scroll_offset,
    double previous_x,
    uint8_t has_previous_layout,
    double previous_layout_x,
    uint8_t same_transform,
    double* out_layout_x,
    double* out_offset_x,
    uint8_t* out_should_animate) noexcept
{
    if (!out_layout_x || !out_offset_x || !out_should_animate)
        return VN_ERR_INVALID_ARG;

    const double layout_x = origin_x - rendered_offset_x + scroll_offset;
    const double offset_x = previous_x - layout_x;
    const bool skip = (has_previous_layout != 0) &&
                      (std::abs(layout_x - previous_layout_x) < 0.5) &&
                      (same_transform != 0);

    *out_layout_x = layout_x;
    *out_offset_x = offset_x;
    *out_should_animate = skip ? 0u : 1u;
    return VN_OK;
}

extern "C" VN_EXPORT int32_t __stdcall vn_visualizer_step_bars(
    const double* current_heights,
    const double* smoothed_heights,
    const double* draw_heights,
    int32_t bar_count,
    double width,
    double height,
    double dpi_scale_x,
    double dpi_scale_y,
    double current_opacity,
    double icon_mix,
    double* out_bar_rects_xywh,
    int32_t out_rects_cap_doubles,
    double* out_corner_radius,
    double* out_frame_opacity,
    double* out_morph_opacity,
    double* out_bars_opacity,
    uint8_t* out_draw_settled) noexcept
{
    if (bar_count <= 0)
        return VN_ERR_INVALID_ARG;

    if (out_bar_rects_xywh && out_rects_cap_doubles < bar_count * 4)
        return VN_ERR_BUFFER_TOO_SMALL;

    uint8_t settled = 1u;
    if (current_heights && smoothed_heights)
    {
        for (int32_t i = 0; i < bar_count; ++i)
        {
            if (std::abs(smoothed_heights[i] - current_heights[i]) > 0.0012)
            {
                settled = 0u;
            }
        }
    }
    if (out_draw_settled)
        *out_draw_settled = settled;

    const double frame_opacity = current_opacity + (1.0 - current_opacity) * icon_mix;
    const double morph_t = std::clamp(icon_mix / 0.18, 0.0, 1.0);
    const double morph_opacity = morph_t * morph_t * (3.0 - 2.0 * morph_t);
    const double bars_opacity = 1.0 - morph_opacity;

    if (out_frame_opacity) *out_frame_opacity = frame_opacity;
    if (out_morph_opacity) *out_morph_opacity = morph_opacity;
    if (out_bars_opacity) *out_bars_opacity = bars_opacity;

    const double safe_dpi_x = dpi_scale_x > 0.0 ? dpi_scale_x : 1.0;
    const double safe_dpi_y = dpi_scale_y > 0.0 ? dpi_scale_y : 1.0;
    const double bar_width = width * 0.10;
    const double spacing = (width * 0.05) + 0.2;
    const double total_content_width = (bar_width * bar_count) + (spacing * (bar_count - 1));
    const double start_x = (width - total_content_width) / 2.0;
    const double snapped_w = std::max(1.0, std::rint(bar_width * safe_dpi_x) / safe_dpi_x);

    if (out_corner_radius)
        *out_corner_radius = snapped_w * 0.5;

    if (out_bar_rects_xywh && draw_heights)
    {
        for (int32_t i = 0; i < bar_count; ++i)
        {
            const double x = start_x + i * (bar_width + spacing);
            const double half_height = draw_heights[i] * height / 2.0;
            const double top = std::rint((height / 2.0 - half_height) * safe_dpi_y) / safe_dpi_y;
            const double bottom = std::rint((height / 2.0 + half_height) * safe_dpi_y) / safe_dpi_y;
            const double rx = std::rint(x * safe_dpi_x) / safe_dpi_x;
            const double rh = std::max(0.0, bottom - top);

            out_bar_rects_xywh[i * 4 + 0] = rx;
            out_bar_rects_xywh[i * 4 + 1] = top;
            out_bar_rects_xywh[i * 4 + 2] = snapped_w;
            out_bar_rects_xywh[i * 4 + 3] = rh;
        }
    }

    return VN_OK;
}

extern "C" VN_EXPORT int32_t __stdcall vn_visualizer_compute_morph_quads(
    const double* bar_rects_xywh,
    int32_t bar_count,
    double width,
    double height,
    double play_mix,
    double check_mix,
    double icon_mix,
    double* out_quad_points_xy,
    int32_t out_cap_doubles) noexcept
{
    if (!bar_rects_xywh || bar_count <= 0 || !out_quad_points_xy)
        return VN_ERR_INVALID_ARG;

    if (out_cap_doubles < bar_count * 8)
        return VN_ERR_BUFFER_TOO_SMALL;

    const double size = std::min(width, height);
    const double offset_x = (width - size) / 2.0;
    const double offset_y = (height - size) / 2.0;
    const double scale = size / 24.0;

    for (int32_t i = 0; i < bar_count; ++i)
    {
        const double bar_left = bar_rects_xywh[i * 4 + 0];
        const double bar_top = bar_rects_xywh[i * 4 + 1];
        const double bar_right = bar_left + bar_rects_xywh[i * 4 + 2];
        const double bar_bottom = bar_top + bar_rects_xywh[i * 4 + 3];

        for (int32_t corner = 0; corner < 4; ++corner)
        {
            const bool right = (corner == 1 || corner == 2);
            const bool bottom = (corner >= 2);
            const int32_t right_offset = right ? 1 : 0;

            const double play_x = 7.0 + 13.0 * static_cast<double>(i + right_offset) / static_cast<double>(bar_count);
            const double play_half_height = 8.0 * (20.0 - play_x) / 13.0;
            const double play_y = 12.0 + (bottom ? play_half_height : -play_half_height);

            const int32_t slice = i < 3 ? i : (i - 3);
            const int32_t slices = i < 3 ? 3 : 2;
            const double pause_x = (i < 3 ? 6.0 : 14.0) + 4.0 * static_cast<double>(slice + right_offset) / static_cast<double>(slices);
            const double pause_y = bottom ? 20.0 : 4.0;

            double icon_x = pause_x + (play_x - pause_x) * play_mix;
            double icon_y = pause_y + (play_y - pause_y) * play_mix;

            const bool left_arm = i < 2;
            const int32_t check_slice = left_arm ? i : (i - 2);
            const double check_x = left_arm
                ? (3.0 + 6.0 * static_cast<double>(check_slice + right_offset) / 2.0)
                : (9.0 + 12.0 * static_cast<double>(check_slice + right_offset) / 3.0);
            const double check_y_base = left_arm ? (check_x + 9.0) : (27.0 - check_x);
            const double check_y = check_y_base + (bottom ? 1.7 : -1.7);

            icon_x += (check_x - icon_x) * check_mix;
            icon_y += (check_y - icon_y) * check_mix;

            icon_x = offset_x + icon_x * scale;
            icon_y = offset_y + icon_y * scale;

            const double bar_cx = right ? bar_right : bar_left;
            const double bar_cy = bottom ? bar_bottom : bar_top;

            const int32_t idx = (i * 4 + corner) * 2;
            out_quad_points_xy[idx] = bar_cx + (icon_x - bar_cx) * icon_mix;
            out_quad_points_xy[idx + 1] = bar_cy + (icon_y - bar_cy) * icon_mix;
        }
    }

    return VN_OK;
}
