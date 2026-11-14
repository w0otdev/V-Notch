#pragma once

#include "vnotch_native.h"

#ifndef VN_ERR_OVERFLOW_DEFINED
#define VN_ERR_OVERFLOW_DEFINED
constexpr int32_t VN_ERR_OVERFLOW = -4;
#endif

#pragma pack(push, 1)

struct VnLiquidGlassFrameDimsInput
{
    int32_t region_x;
    int32_t region_y;
    int32_t display_w;
    int32_t display_h;
    int32_t max_region_w;
    int32_t surface_w;
    int32_t surface_h;
    int32_t bevel_mode;
    double bitmap_dpi;
    double z_radius;
    double refraction;
    double chromatic_aberration;
    double distortion;
    double edge_bend;
    uint8_t gpu_mode;
    uint8_t full_surface;
    uint8_t has_prev_bounds;
    uint8_t reserved;
    int32_t prev_bounds_x;
    int32_t prev_bounds_y;
    int32_t prev_bounds_w;
    int32_t prev_bounds_h;
};
static_assert(sizeof(VnLiquidGlassFrameDimsInput) == 100);

struct VnLiquidGlassFrameDimsOut
{
    int32_t out_w;
    int32_t out_h;
    double out_scale;
    int32_t buf_w;
    int32_t buf_h;
    int32_t notch_off_x;
    int32_t notch_off_y;
    double min_half;
    int32_t margin;
    int32_t src_w;
    int32_t src_h;
    int32_t phys_src_w;
    int32_t phys_src_h;
    int32_t src_x;
    int32_t src_y;
    int32_t capture_shift_x;
    int32_t capture_shift_y;
    int32_t map_capture_shift_y;
    uint8_t has_next_bounds;
    uint8_t pad0;
    uint8_t pad1;
    uint8_t pad2;
    int32_t next_bounds_x;
    int32_t next_bounds_y;
    int32_t next_bounds_w;
    int32_t next_bounds_h;
};
static_assert(sizeof(VnLiquidGlassFrameDimsOut) == 100);

struct VnLiquidGlassGpuGeometryInput
{
    int32_t region_x;
    int32_t region_y;
    int32_t src_x;
    int32_t src_y;
    int32_t src_w;
    int32_t src_h;
    int32_t out_w;
    int32_t out_h;
    int32_t bevel_mode;
    double sub_x;
    double sub_y;
    double out_scale;
    double min_half;
    double top_corner_radius;
    double bottom_corner_radius;
    double power_factor;
    double refraction_a;
    double refraction_b;
    double refraction_c;
    double refraction_d;
    double f_power;
    double noise;
    double glow_weight;
    double glow_bias;
    double glow_edge1;
    double refraction;
    double edge_bend;
    double chromatic_aberration;
    double distortion;
    double z_radius;
    double saturation;
    double brightness;
    double edge_highlight;
    double specular;
    double fresnel;
};
static_assert(sizeof(VnLiquidGlassGpuGeometryInput) == 244);

struct VnLiquidGlassGpuGeometryOut
{
    double src_w;
    double src_h;
    double notch_w;
    double notch_h;
    double off_x;
    double off_y;
    double top_corner_r;
    double bottom_corner_r;
    double power_factor;
    double a;
    double b;
    double c;
    double d;
    double f_power;
    double noise;
    double glow_weight;
    double glow_bias;
    double glow_edge0;
    double glow_edge1;
    double chroma;
    double edge_bend;
    double sat_factor;
    double bright_add;
    double bevel_mode;
    int32_t capture_origin_x;
    int32_t capture_origin_y;
};
static_assert(sizeof(VnLiquidGlassGpuGeometryOut) == 200);

struct VnAiUsageWindowEntry
{
    uint8_t is_codex_bucket;
    uint8_t matches_first_bucket;
    uint8_t has_duration;
    uint8_t reserved;
    int32_t duration_minutes;
};
static_assert(sizeof(VnAiUsageWindowEntry) == 8);

struct VnVolumeWheelState
{
    double accumulator;
    int64_t last_wheel_utc_ticks;
    int64_t suppress_until_utc_ticks;
    uint8_t is_wheel_active;
    uint8_t reserved0;
    uint8_t reserved1;
    uint8_t reserved2;
};
static_assert(sizeof(VnVolumeWheelState) == 28);

struct VnAppNotifyDragState
{
    double last_x;
    double last_y;
    int64_t last_timestamp;
    double velocity;
    uint8_t is_dragging;
    uint8_t reserved0;
    uint8_t reserved1;
    uint8_t reserved2;
};
static_assert(sizeof(VnAppNotifyDragState) == 36);

struct VnRoundedWindowRegion
{
    int32_t left;
    int32_t top;
    int32_t right;
    int32_t bottom;
    int32_t corner_diameter;
};
static_assert(sizeof(VnRoundedWindowRegion) == 20);

#pragma pack(pop)

// ---------------------------------------------------------------------------
// LiquidGlass Optical, Geometry, Pacing & Capacity Exports
// ---------------------------------------------------------------------------

VN_EXPORT int32_t __stdcall vn_liquid_glass_build_optical_lut(
    float refraction_amount,
    float rim_width,
    float* out_lut1024) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_compute_frame_dims(
    const VnLiquidGlassFrameDimsInput* input,
    VnLiquidGlassFrameDimsOut* out_dims) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_create_gpu_geometry(
    const VnLiquidGlassGpuGeometryInput* input,
    VnLiquidGlassGpuGeometryOut* out_geom) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_grow_present_capacity(
    int32_t current_cap,
    int32_t required,
    int32_t slack,
    int32_t* out_cap) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_grow_pixel_buffer_capacity(
    int32_t current_cap,
    int32_t required,
    int32_t* out_cap) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_clamp_capture_origin(
    int32_t requested,
    int32_t capture_len,
    int32_t desktop_origin,
    int32_t desktop_len,
    int32_t* out_origin) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_compute_rim_width(
    double normalized_radius,
    double output_scale,
    double min_half,
    double* out_rim_width) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_lens_profile(
    double normalized_distance,
    uint8_t broad,
    double* out_profile) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_refraction_amplitude(
    double rim_width,
    double refraction,
    int32_t bevel_mode,
    double edge_bend,
    double* out_amplitude) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_edge_bend_gain(
    double edge_bend,
    double* out_gain) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_directional_lens_width(
    double rim_width,
    double inward_normal_x,
    double edge_bend,
    double* out_width) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_is_gpu_geometry_valid(
    double src_w,
    double src_h,
    double notch_w,
    double notch_h,
    uint8_t* out_valid) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_compute_sampling_margin(
    double rim_width,
    double refraction,
    double chroma,
    double distortion,
    int32_t bevel_mode,
    double edge_bend,
    int32_t* out_margin) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_exponential_refract(
    double x,
    double a,
    double b,
    double c,
    double d,
    double* out_value) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_choose_frame_interval_ms(
    double active_interval_ms,
    uint8_t idle,
    uint8_t animating,
    uint8_t has_frame,
    int64_t unchanged_ms,
    uint8_t force_refresh,
    double* out_interval_ms) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_choose_processing_interval_ms(
    double interval_ms,
    double work_ms,
    uint8_t gpu_mode,
    double* out_interval_ms) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_blur_amount_to_sigma(
    double amount,
    double dpi_scale,
    double* out_sigma) noexcept;

VN_EXPORT int32_t __stdcall vn_liquid_glass_compute_next_deadline_ms(
    double prev_deadline_ms,
    double interval_ms,
    double now_ms,
    double* out_deadline_ms) noexcept;

// ---------------------------------------------------------------------------
// Controls: WordClock, AiMarkdown Stream Closer, AiUsage Window Resolver
// ---------------------------------------------------------------------------

VN_EXPORT int32_t __stdcall vn_word_clock_format_time(
    const wchar_t* lang,
    int32_t lang_len,
    int32_t hour24,
    int32_t minute,
    const wchar_t* prefix,
    int32_t prefix_len,
    const wchar_t* oclock,
    int32_t oclock_len,
    wchar_t* out_buf,
    int32_t cap,
    int32_t* out_len) noexcept;

VN_EXPORT int32_t __stdcall vn_ai_markdown_close_stream_spans(
    const wchar_t* md,
    int32_t len,
    const VnMarkdownBoundarySummary* summary,
    int32_t* out_insert_pos,
    wchar_t* out_closer,
    int32_t cap,
    int32_t* out_len) noexcept;

VN_EXPORT int32_t __stdcall vn_ai_usage_resolve_account_windows(
    const VnAiUsageWindowEntry* entries,
    int32_t count,
    int32_t* out_short_idx,
    int32_t* out_long_idx) noexcept;

// ---------------------------------------------------------------------------
// Windows & Setup Helpers
// ---------------------------------------------------------------------------

VN_EXPORT int32_t __stdcall vn_mainwindow_step_volume_wheel(
    VnVolumeWheelState* io_state,
    int32_t raw_delta,
    int64_t now_utc_ticks,
    uint8_t is_indicator_active,
    int32_t* out_volume_delta,
    uint8_t* out_accepted,
    uint8_t* out_suppressed) noexcept;

VN_EXPORT int32_t __stdcall vn_desktop_edge_probe_notch_obscured(
    intptr_t self_hwnd,
    uint8_t* out_obscured) noexcept;

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
    uint8_t* out_started_dragging) noexcept;

VN_EXPORT int32_t __stdcall vn_appnotify_eval_drag_release(
    double point_x,
    double origin_x,
    int64_t last_timestamp,
    int64_t timestamp,
    double velocity,
    uint8_t is_dragging,
    uint8_t* out_dismiss,
    int32_t* out_dismiss_direction,
    uint8_t* out_is_click) noexcept;

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
    double* out_y) noexcept;

VN_EXPORT int32_t __stdcall vn_window_calc_rounded_region(
    double width_dip,
    double height_dip,
    double margin_dip,
    double corner_radius_dip,
    double dpi_scale,
    VnRoundedWindowRegion* out_region) noexcept;

VN_EXPORT int32_t __stdcall vn_install_cleanup_validate_manifest_entry(
    const wchar_t* rel_path,
    int32_t path_len,
    const wchar_t* sha256,
    int32_t sha_len,
    uint8_t* out_valid) noexcept;
