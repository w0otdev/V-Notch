#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>
#include <wincrypt.h>
#include <powerbase.h>
#include <dwmapi.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#pragma comment(lib, "kernel32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "powrprof.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "normaliz.lib")

#define VN_EXPORT extern "C" __declspec(dllexport)
VN_EXPORT int32_t __stdcall vn_update_parse_atom(const uint8_t*, int32_t, char16_t*, int32_t, int32_t*) noexcept;

constexpr int32_t VN_OK = 0;
constexpr int32_t VN_ERR_INVALID_ARG = -1;
constexpr int32_t VN_ERR_BUFFER_TOO_SMALL = -2;
constexpr int32_t VN_ERR_FAIL = -3;

#pragma pack(push, 1)

struct VnSessionSwitchInputs
{
    uint8_t current_is_premium;
    uint8_t best_is_os_current;
    uint8_t best_is_recent_latest_playback;
    uint8_t current_still_playing;
    uint8_t best_has_fresh_timeline;
    double pending_elapsed_seconds;
};

struct VnSessionScoreInputs
{
    uint8_t has_title;
    uint8_t has_artist;
    uint8_t has_thumbnail;
    uint8_t artist_is_non_generic;
    uint8_t is_spotify;
    uint8_t is_music;
    uint8_t is_youtube;
    uint8_t is_browser;
    uint8_t is_os_current;
    uint8_t dedicated_music_app_playing;
    uint8_t is_active;
    uint8_t has_play_start_age;
    double play_start_age_seconds;
    uint8_t has_latest_playing_age;
    double latest_playing_age_seconds;
    uint8_t has_last_playing_idle;
    double last_playing_idle_seconds;
    uint8_t has_timeline_age;
    double timeline_age_seconds;
    uint8_t timeline_boost;
    uint8_t timeline_penalty;
};

struct VnThumbnailSourceInputs
{
    uint8_t platform_is_youtube;
    uint8_t platform_is_browser;
    uint8_t platform_is_soundcloud;
    uint8_t has_source_app;
    uint8_t is_likely_youtube;
    uint8_t has_soundcloud_session_override;
    uint8_t browser_has_youtube_tab_open;
    uint8_t has_track;
    uint8_t thumbnail_is_null_or_placeholder;
};

struct VnThumbnailSourcePlan
{
    uint8_t is_potential_youtube;
    uint8_t is_potential_soundcloud;
};

struct VnSoundCloudFetchInputs
{
    uint8_t is_new_soundcloud_track;
    uint8_t same_track_fetch_running;
    uint8_t thumbnail_is_null_or_placeholder;
    uint8_t has_mismatched_thumb_source;
    uint8_t retry_interval_elapsed;
};

struct VnYouTubeFetchInputs
{
    uint8_t is_new_track;
    uint8_t same_track_fetch_running;
    uint8_t needs_better_thumbnail;
    uint8_t retry_interval_elapsed;
};

struct VnTimelineSolveInputs
{
    int64_t start_time_ticks;
    int64_t end_time_ticks;
    int64_t max_seek_time_ticks;
    int64_t position_ticks;
    int64_t last_updated_utc_ticks;
    int64_t last_updated_offset_ticks;
    uint8_t is_initial_or_big_change;
    uint8_t is_new_track;
    uint8_t is_browser_timeline_track;
    uint8_t is_live_timeline;
    uint8_t is_playing;
    double playback_rate;
    int64_t now_utc_ticks;
    int64_t now_offset_ticks;
};

struct VnTimelineSolveResult
{
    int64_t position_ticks;
    int64_t duration_ticks;
    int64_t last_updated_utc_ticks;
    int64_t last_updated_offset_ticks;
    uint8_t is_indeterminate;
};

struct VnTimelineClassifierState
{
    int64_t last_end_ticks;
    int64_t last_end_observed_utc_ticks;
    uint8_t has_last_end_observed;
    int32_t growth_samples;
    uint8_t is_live;
    uint8_t was_playing;
};


struct VnSystemPowerStatus
{
    uint8_t ac_line_status;
    uint8_t battery_flag;
    uint8_t battery_life_percent;
    uint8_t system_status_flag;
    int32_t battery_life_time;
    int32_t battery_full_life_time;
};


struct VnSystemBatteryState
{
    uint8_t ac_on_line;
    uint8_t battery_present;
    uint8_t charging;
    uint8_t discharging;
    uint8_t spare1_0;
    uint8_t spare1_1;
    uint8_t spare1_2;
    uint8_t spare1_3;
    uint32_t max_capacity;
    uint32_t remaining_capacity;
    int32_t rate;
    uint32_t estimated_time;
    uint32_t default_alert1;
    uint32_t default_alert2;
};

struct VnBatteryStateOut
{
    int32_t percentage;
    uint8_t is_charging;
    uint8_t is_plugged_in;
    uint8_t is_battery_saver;
    uint8_t has_battery;
    int32_t remaining_minutes;
    double power_watts;
    uint8_t has_power_rate;
};

struct VnBrowserAdTransitionDecision
{
    uint8_t should_hold;
    int64_t transition_started_utc_ticks;
    uint8_t was_transition_active;
};

struct VnTransientSessionGapDecision
{
    uint8_t should_hold;
    int64_t gap_started_utc_ticks;
};

struct VnJsonBoundaryState
{
    uint8_t quoted;
    uint8_t escaped;
    uint8_t started;
    int32_t depth;
};

struct VnGlassMapParams
{
    int32_t out_w;
    int32_t out_h;
    int32_t src_w;
    int32_t src_h;
    int32_t margin;
    int32_t notch_w;
    int32_t notch_h;
    int32_t notch_off_x;
    int32_t notch_off_y;
    int32_t capture_shift_x;
    int32_t capture_shift_y;
    double out_scale;
    double top_corner_radius;
    double bottom_corner_radius;
    double edge_bend;
    double chromatic_aberration;
    double distortion;
    double f_power;
    double refraction_a;
    double refraction_b;
    double refraction_c;
    double refraction_d;
};

struct VnGlassBackdropOpticsOut
{
    uint8_t red;
    uint8_t green;
    uint8_t blue;
    uint8_t reserved;
    double light_x;
    double light_y;
    double contrast;
    int32_t outside_rgb;
};

struct VnProgressiveBlurStep
{
    int32_t delay_ms;
    int32_t fade_duration_ms;
    int32_t blur_duration_ms;
    int32_t slide_duration_ms;
    double start_blur_radius;
    double start_opacity;
    double start_translate_y;
};

#pragma pack(pop)

VN_EXPORT int32_t __stdcall vn_visualizer_mix_frames_to_mono(
    const uint8_t* buffer,
    int32_t length,
    int32_t bytes_recorded,
    int32_t channel_count,
    int32_t bits_per_sample,
    uint8_t is_float,
    float* out_mono,
    int32_t mono_cap,
    int32_t* out_frames_written) noexcept;

VN_EXPORT int32_t __stdcall vn_soundcloud_find_next_anchor_href(
    const char16_t* html_ptr,
    int32_t html_len,
    int32_t start_pos,
    int32_t* out_href_start,
    int32_t* out_href_len,
    int32_t* out_next_pos,
    uint8_t* out_found) noexcept;

VN_EXPORT int32_t __stdcall vn_glass_build_refraction_lut(
    double f_power,
    double refraction_a,
    double refraction_b,
    double refraction_c,
    double refraction_d,
    double* out_lut,
    int32_t lut_len) noexcept;

VN_EXPORT int32_t __stdcall vn_glass_build_refraction_rows(
    const VnGlassMapParams* params,
    const double* lut,
    int32_t lut_len,
    int32_t y_start,
    int32_t y_end,
    int32_t* out_map) noexcept;

VN_EXPORT int32_t __stdcall vn_glass_refract_gather(
    const uint8_t* src,
    uint8_t* dst,
    const int32_t* refract_map,
    int32_t src_stride,
    int32_t start_pixel,
    int32_t end_pixel,
    uint8_t adjust_color,
    float sat_factor,
    float bright_add) noexcept;

VN_EXPORT int32_t __stdcall vn_glass_analyze_backdrop_samples(
    const int32_t* rgb_samples,
    int32_t sample_len,
    int32_t columns,
    int32_t rows,
    VnGlassBackdropOpticsOut* out_optics) noexcept;

VN_EXPORT int32_t __stdcall vn_glass_sample_backdrop_optics(
    const uint8_t* dib_bits,
    int32_t src_w,
    int32_t src_h,
    int32_t sample_x,
    int32_t sample_y,
    int32_t sample_w,
    int32_t sample_h,
    VnGlassBackdropOpticsOut* out_optics) noexcept;

VN_EXPORT int32_t __stdcall vn_glass_gaussian_box_radii(
    double sigma,
    int32_t passes,
    int32_t* out_radii,
    int32_t radii_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_glass_compute_capture_interval_ms(
    int32_t configured_fps,
    int32_t display_refresh_hz,
    uint8_t ultimate_gpu,
    double* out_interval_ms) noexcept;

VN_EXPORT int32_t __stdcall vn_glass_should_submit_frame(
    int64_t now_ticks,
    int64_t last_submit_ticks,
    int64_t stopwatch_freq,
    double active_interval_ms,
    uint8_t force_present,
    uint8_t* out_should_submit) noexcept;

VN_EXPORT int32_t __stdcall vn_glass_score_d3d9_adapter(
    const char16_t* description,
    int32_t desc_len,
    uint32_t vendor_id,
    int64_t* out_score) noexcept;

VN_EXPORT int32_t __stdcall vn_glass_compute_squircle_lut(
    double power,
    int32_t segments,
    double* out_pow_cos,
    double* out_pow_sin) noexcept;

#pragma pack(push, 1)

struct VnClipboardEntryCategoryMeta {
    uint8_t is_personal;
    uint8_t is_archived;
    uint8_t is_pinned;
    uint8_t kind;
};

struct VnClipboardCategoryCountsOut {
    int32_t all_count;
    int32_t archive_count;
    int32_t pin_count;
    int32_t text_count;
    int32_t code_count;
    int32_t links_count;
    int32_t images_count;
    int32_t colors_count;
    int32_t files_count;
};

#pragma pack(pop)

VN_EXPORT int32_t __stdcall vn_clipboard_store_accumulate_category_counts(
    const VnClipboardEntryCategoryMeta* entries,
    int32_t entry_count,
    VnClipboardCategoryCountsOut* out_counts) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_tray_compute_reconcile_diff(
    const int32_t* desired_to_existing_index,
    int32_t desired_count,
    uint8_t* out_existing_survivor_mask,
    int32_t existing_count,
    int32_t* out_changes) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_card_compute_age_bucket(
    int64_t now_utc_ticks,
    int64_t copied_utc_ticks,
    int32_t* out_bucket,
    int32_t* out_value) noexcept;

VN_EXPORT int32_t __stdcall vn_ui_smooth_scroll_step(
    double current_offset,
    double target_offset,
    double velocity,
    double max_scroll,
    double friction,
    double min_velocity,
    double dt_seconds,
    double* out_next_offset,
    double* out_next_velocity,
    uint8_t* out_settled) noexcept;

VN_EXPORT int32_t __stdcall vn_ui_compute_progressive_blur_cascade(
    int32_t item_count,
    int32_t base_delay_ms,
    int32_t direction,
    uint8_t is_menu_list,
    VnProgressiveBlurStep* out_steps,
    int32_t out_cap,
    int32_t* out_written) noexcept;


#pragma pack(push, 1)

struct VnVolumeHudLayoutOut
{
    int32_t percentage;
    int32_t active_ticks;
    int32_t highlight_start_tick;
    int32_t icon_codepoint;
    double label_offset_x;
};

#pragma pack(pop)

VN_EXPORT int32_t __stdcall vn_animation_compute_volume_hud_state(
    double volume,
    uint8_t muted,
    int32_t tick_count,
    double track_width,
    double label_width,
    double min_left_x,
    VnVolumeHudLayoutOut* out_layout,
    uint8_t* out_tick_states,
    int32_t tick_cap) noexcept;

#pragma pack(push, 1)

struct VnVolumeHudOverscrollOut
{
    double scale_x;
    double scale_y;
    double translate_x;
    double origin_x;
};

struct VnVolumeHudTransitionParamsOut
{
    double hud_from_scale_x;
    double hud_from_scale_y;
    double hud_to_scale_x;
    double hud_to_scale_y;
    double hud_from_translate_y;
    double hud_to_translate_y;
    double hud_from_blur;
    double hud_to_blur;
    double media_to_scale;
    double media_to_blur;
    double target_width;
    double target_height;
    double target_corner_radius;
    int32_t morph_duration_ms;
    int32_t fade_duration_ms;
};

#pragma pack(pop)

VN_EXPORT int32_t __stdcall vn_audio_sessions_open_all(int64_t* snapshot, int32_t* count, int32_t* hresult) noexcept;
VN_EXPORT int32_t __stdcall vn_local_file_search_create(int64_t* handle) noexcept;
VN_EXPORT int32_t __stdcall vn_local_file_search_cancel(int64_t handle) noexcept;
VN_EXPORT int32_t __stdcall vn_local_file_search_close(int64_t handle) noexcept;
VN_EXPORT int32_t __stdcall vn_local_file_search_run(int64_t handle,
    const char16_t* query, int32_t query_length, const char16_t* roots, int32_t roots_length,
    const char16_t* hidden, int32_t hidden_length, int32_t limit, int32_t budget_ms, int32_t* count) noexcept;
VN_EXPORT int32_t __stdcall vn_local_file_search_get_result(int64_t handle, int32_t index,
    char16_t* path, int32_t capacity, int32_t* length, int32_t* kind, double* score) noexcept;
VN_EXPORT int32_t __stdcall vn_audio_volume_get_state(int64_t volume, int32_t* state) noexcept;
VN_EXPORT int32_t __stdcall vn_media_session_volume_get_process_ids(
    const char16_t* source, int32_t source_length, uint32_t* process_ids, int32_t capacity, int32_t* count) noexcept;
VN_EXPORT int32_t __stdcall vn_media_session_volume_compute_score(
    uint8_t process_match, uint8_t metadata_match, uint8_t same_source, uint32_t pid, uint32_t last_pid,
    uint8_t same_session, int32_t state, double* score) noexcept;

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
    int32_t tick_cap) noexcept;

VN_EXPORT int32_t __stdcall vn_animation_compute_volume_overscroll_spring(
    double current_volume,
    double step_delta,
    VnVolumeHudOverscrollOut* out_overscroll) noexcept;

VN_EXPORT int32_t __stdcall vn_animation_compute_volume_hud_transition(
    uint8_t entering,
    uint8_t island_mode,
    uint8_t enable_blur,
    double collapsed_width,
    double collapsed_height,
    double collapsed_radius,
    VnVolumeHudTransitionParamsOut* out_params) noexcept;
