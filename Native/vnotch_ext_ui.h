#pragma once

#include "vnotch_native.h"

#ifndef VN_ERR_OVERFLOW_DEFINED
#define VN_ERR_OVERFLOW_DEFINED
constexpr int32_t VN_ERR_OVERFLOW = -4;
#endif

#pragma pack(push, 1)









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
