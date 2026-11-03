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
VN_EXPORT int32_t __stdcall vn_setup_show_message(const wchar_t*, int32_t, const wchar_t*, int32_t, int32_t, int32_t*) noexcept;

constexpr int32_t VN_OK = 0;
constexpr int32_t VN_ERR_INVALID_ARG = -1;
constexpr int32_t VN_ERR_BUFFER_TOO_SMALL = -2;
constexpr int32_t VN_ERR_FAIL = -3;

VN_EXPORT int32_t __stdcall vn_clipboard_record_open(const wchar_t*, int32_t, int64_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_record_close(int64_t) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_record_migrate(int64_t, const wchar_t*, int32_t) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_record_read(int64_t, const wchar_t*, int32_t, uint8_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_record_append(int64_t, const wchar_t*, int32_t, const uint8_t*, int32_t) noexcept;

namespace vnotch::services { struct VnDetection; namespace loopback { struct VnLoopbackFormat; } }
VN_EXPORT int32_t __stdcall vn_smartcrop_preprocess_bgra32(const uint8_t*, int32_t, int32_t, int32_t, int32_t, float*, int32_t, float*) noexcept;
VN_EXPORT int32_t __stdcall vn_smartcrop_session_create(const wchar_t*, int32_t, const wchar_t*, int32_t, uint8_t, int64_t*, uint8_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_smartcrop_session_destroy(int64_t) noexcept;
VN_EXPORT int32_t __stdcall vn_smartcrop_session_infer(int64_t, const uint8_t*, int32_t, int32_t, int32_t, int32_t,
    vnotch::services::VnDetection*, int32_t, int32_t*, uint8_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_registry_watch_create(HKEY, const wchar_t*, int32_t, HANDLE, int64_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_registry_watch_consume(int64_t, uint8_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_registry_watch_close(int64_t) noexcept;
VN_EXPORT int32_t __stdcall vn_media_title_watch_create(HANDLE, int64_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_media_title_watch_close(int64_t) noexcept;
VN_EXPORT int32_t __stdcall vn_visualizer_stream_create(int32_t, int64_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_visualizer_stream_close(int64_t) noexcept;
VN_EXPORT int32_t __stdcall vn_visualizer_stream_push(int64_t, const uint8_t*, int32_t, int32_t, int32_t, uint8_t, float*, float*, float*) noexcept;
VN_EXPORT int32_t __stdcall vn_dicolor_convert_oklab(const uint8_t*, int32_t, float*, int32_t, uint8_t) noexcept;
VN_EXPORT int32_t __stdcall vn_loopback_start_levels(const char16_t*, int32_t,
    void(__stdcall*)(int64_t, const float*, float), void(__stdcall*)(int64_t, int32_t), int64_t,
    int64_t*, vnotch::services::loopback::VnLoopbackFormat*, int32_t*) noexcept;

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

#ifndef VN_SETTINGS_SWITCH_MOTION_DEFINED
#define VN_SETTINGS_SWITCH_MOTION_DEFINED
struct VnSettingsSwitchMotionOut
{
    double target_x;
    double peak_scale_x;
    double dip_scale_y;
    double rebound_scale_x;
    double rebound_scale_y;
    double press_scale_x;
    double press_scale_y;
    double press_shift_x;
    int32_t peak_time_ms;
    int32_t rebound_time_ms;
    int32_t duration_ms;
};
#endif

#ifndef VN_NAV_TAB_MOTION_DEFINED
#define VN_NAV_TAB_MOTION_DEFINED
struct VnNavTabMotionOut
{
    double target_y;
    double target_height;
    double peak_scale_y;
    double dip_scale_x;
    double rebound_scale_y;
    double rebound_scale_x;
    double content_shift_x;
    int32_t peak_time_ms;
    int32_t rebound_time_ms;
    int32_t duration_ms;
};
#endif

#ifndef VN_QUICK_ACTIONS_SLIDE_MOTION_DEFINED
#define VN_QUICK_ACTIONS_SLIDE_MOTION_DEFINED
struct VnQuickActionsSlideMotionOut
{
    double results_from_x;
    double results_to_x;
    double actions_from_x;
    double actions_to_x;
    double item_slide_x;
    int32_t exit_duration_ms;
    int32_t enter_duration_ms;
    int32_t stagger_step_ms;
};
#endif

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

VN_EXPORT int32_t __stdcall vn_clipboard_validate_file_handle(
    intptr_t file_handle,
    const char16_t* expected_full_path_ptr,
    int32_t expected_full_path_len,
    int32_t* out_error_kind,
    int32_t* out_win32_error) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_sensitive_try_decode_hex_payload(
    const char16_t* text_ptr,
    int32_t text_len,
    char16_t* out_wide,
    int32_t out_cap,
    int32_t* out_len,
    uint8_t* out_decoded) noexcept;

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

#ifndef VN_CATEGORY_PILL_MOTION_DEFINED
#define VN_CATEGORY_PILL_MOTION_DEFINED
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
#endif

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

VN_EXPORT int32_t __stdcall vn_animation_compute_category_pill_motion(
    double current_x,
    double current_width,
    double target_x,
    double target_width,
    uint8_t reduced_motion,
    VnCategoryPillMotionOut* out_motion) noexcept;

VN_EXPORT int32_t __stdcall vn_animation_compute_settings_switch_motion(
    double track_width,
    double thumb_width,
    double margin_x,
    uint8_t is_checked,
    uint8_t is_pressed,
    uint8_t reduced_motion,
    VnSettingsSwitchMotionOut* out_motion) noexcept;

VN_EXPORT int32_t __stdcall vn_animation_compute_nav_tab_motion(
    double current_y,
    double target_y,
    double item_height,
    uint8_t reduced_motion,
    VnNavTabMotionOut* out_motion) noexcept;

VN_EXPORT int32_t __stdcall vn_animation_compute_elastic_slider_overscroll(
    double overflow_px,
    double usable_width,
    uint8_t reduced_motion,
    double* out_scale_x,
    double* out_scale_y) noexcept;

VN_EXPORT int32_t __stdcall vn_animation_compute_spotlight_quick_actions_motion(
    uint8_t entering,
    double viewport_width,
    uint8_t reduced_motion,
    VnQuickActionsSlideMotionOut* out_motion) noexcept;

VN_EXPORT int32_t vn_settings_search_score_row(
    const char16_t* norm_title_ptr,
    int32_t norm_title_len,
    const char16_t* norm_content_ptr,
    int32_t norm_content_len,
    const char16_t* norm_combined_ptr,
    int32_t norm_combined_len,
    const char16_t* norm_query_ptr,
    int32_t norm_query_len,
    uint8_t allow_fuzzy,
    int32_t* out_score) noexcept;

VN_EXPORT int32_t __stdcall vn_ffmpeg_read_process_command_line(
    uint32_t process_id,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_lyrics_normalize_for_matching(
    const char16_t* text_ptr,
    int32_t text_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_yt_next_scrape_candidate(
    const char16_t* json,
    int32_t json_len,
    int32_t start_pos,
    int32_t* out_next_pos,
    char16_t* out_video_id,
    int32_t video_id_cap,
    int32_t* out_video_id_len,
    char16_t* out_title,
    int32_t title_cap,
    int32_t* out_title_len,
    char16_t* out_channel,
    int32_t channel_cap,
    int32_t* out_channel_len) noexcept;

#ifndef VN_MARKDOWN_NATIVE_DEFINED
#define VN_MARKDOWN_NATIVE_DEFINED

#pragma pack(push, 1)

enum VnMarkdownBlockKind : uint8_t
{
    VN_MD_BLOCK_EMPTY = 0,
    VN_MD_BLOCK_HEADING = 1,
    VN_MD_BLOCK_HORIZONTAL_RULE = 2,
    VN_MD_BLOCK_TABLE_ROW = 3,
    VN_MD_BLOCK_TABLE_SEPARATOR = 4,
    VN_MD_BLOCK_BLOCKQUOTE = 5,
    VN_MD_BLOCK_CHECKLIST_ITEM = 6,
    VN_MD_BLOCK_BULLET_ITEM = 7,
    VN_MD_BLOCK_NUMBERED_ITEM = 8,
    VN_MD_BLOCK_PARAGRAPH = 9,
};

struct VnMarkdownBlockToken
{
    uint8_t kind;
    uint8_t level;
    uint8_t is_checked;
    uint8_t hard_break;
    int32_t text_start;
    int32_t text_len;
    int32_t marker_start;
    int32_t marker_len;
};

struct VnMarkdownSlice
{
    int32_t start;
    int32_t len;
};

enum VnMarkdownInlineKind : uint8_t
{
    VN_MD_INLINE_TEXT = 0,
    VN_MD_INLINE_BOLD = 1,
    VN_MD_INLINE_ITALIC = 2,
    VN_MD_INLINE_CODE = 3,
    VN_MD_INLINE_LINK = 4,
    VN_MD_INLINE_URL = 5,
};

struct VnMarkdownInlineToken
{
    uint8_t kind;
    int32_t text_start;
    int32_t text_len;
    int32_t aux_start;
    int32_t aux_len;
};

#pragma pack(pop)

VN_EXPORT int32_t __stdcall vn_markdown_classify_line(
    const char16_t* line_ptr,
    int32_t line_len,
    VnMarkdownBlockToken* out_token) noexcept;

VN_EXPORT int32_t __stdcall vn_markdown_tokenize_blocks(
    const char16_t* text_ptr,
    int32_t text_len,
    VnMarkdownBlockToken* out_tokens,
    int32_t token_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_markdown_split_table_cells(
    const char16_t* line_ptr,
    int32_t line_len,
    VnMarkdownSlice* out_cells,
    int32_t cell_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_markdown_tokenize_inlines(
    const char16_t* text_ptr,
    int32_t text_len,
    VnMarkdownInlineToken* out_tokens,
    int32_t token_cap,
    int32_t* out_written) noexcept;

#endif




// ---------------------------------------------------------------------------
// UI Motion, Particles, Morphing & Visualizer Bars (UiMotionAndParticles.cpp)
// ---------------------------------------------------------------------------

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

VN_EXPORT int32_t __stdcall vn_dust_particles_init(
    VnDustParticle* particles,
    int32_t capacity,
    double width,
    double height,
    uint32_t* io_rng_state,
    int32_t* io_r2_sequence,
    int32_t* out_active_count) noexcept;

VN_EXPORT int32_t __stdcall vn_dust_particles_step(
    VnDustParticle* particles,
    int32_t active_count,
    double width,
    double height,
    float dt,
    uint8_t is_dissolving,
    double dissolve_progress,
    double reform_progress,
    uint32_t* io_rng_state,
    int32_t* io_r2_sequence) noexcept;

VN_EXPORT int32_t __stdcall vn_dust_particles_dissolve_impulse(
    VnDustParticle* particles,
    int32_t active_count,
    double width,
    double height,
    float origin_x,
    float origin_y,
    uint32_t* io_rng_state) noexcept;

VN_EXPORT int32_t __stdcall vn_morph_settings_icon_sample_contour(
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
    double* out_bbox_area) noexcept;

VN_EXPORT int32_t __stdcall vn_morph_settings_icon_compute_points(
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
    int32_t* out_point_count) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_motion_compute_stagger(
    const int32_t* candidate_indices,
    int32_t count,
    int32_t base_delay_ms,
    int32_t* out_sorted_positions,
    int32_t* out_delays_ms) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_motion_card_delay(
    int32_t item_index,
    int32_t animated_count,
    int32_t base_delay_ms,
    int32_t* out_delay_ms) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_motion_compute_reflow_offset(
    double origin_x,
    double rendered_offset_x,
    double scroll_offset,
    double previous_x,
    uint8_t has_previous_layout,
    double previous_layout_x,
    uint8_t same_transform,
    double* out_layout_x,
    double* out_offset_x,
    uint8_t* out_should_animate) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_motion_step_scroll_physics(
    double* io_position,
    double* io_velocity,
    double min_bound,
    double max_bound,
    double dt,
    double friction,
    double spring_stiffness,
    double spring_damping,
    uint8_t* out_is_settled) noexcept;

VN_EXPORT int32_t __stdcall vn_visualizer_step_bars(
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
    uint8_t* out_draw_settled) noexcept;

VN_EXPORT int32_t __stdcall vn_visualizer_compute_morph_quads(
    const double* bar_rects_xywh,
    int32_t bar_count,
    double width,
    double height,
    double play_mix,
    double check_mix,
    double icon_mix,
    double* out_quad_points_xy,
    int32_t out_cap_doubles) noexcept;

// ---------------------------------------------------------------------------
// Spotlight Search & Providers Extended Operations
// ---------------------------------------------------------------------------

VN_EXPORT int32_t __stdcall vn_app_search_provider_scan_start_menu(
    const char16_t* start_menu_ptr,
    int32_t start_menu_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_everything_provider_read_reply_item_count(
    const uint8_t* list_ptr,
    int32_t byte_count,
    int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_everything_provider_extract_reply_item(
    const uint8_t* list_ptr,
    int32_t byte_count,
    int32_t index,
    int32_t* out_name_offset,
    int32_t* out_name_len,
    int32_t* out_path_offset,
    int32_t* out_path_len,
    uint8_t* out_is_folder) noexcept;

VN_EXPORT int32_t __stdcall vn_everything_provider_pack_ipc_query(
    int64_t reply_hwnd,
    uint32_t query_id,
    uint32_t search_flags,
    int32_t max_results,
    const char16_t* query_ptr,
    int32_t query_len,
    uint8_t* out_buf,
    int32_t out_cap,
    int32_t* out_bytes) noexcept;

VN_EXPORT int32_t __stdcall vn_windows_search_build_query_sql(
    const char16_t* sanitized_ptr, int32_t sanitized_len,
    const char16_t* original_ptr, int32_t original_len,
    const char16_t* scope_ptr, int32_t scope_len,
    uint8_t is_explicit_path,
    int32_t fetch,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept;

VN_EXPORT int32_t __stdcall vn_windows_search_normalize_result_path(
    const char16_t* raw_path_ptr,
    int32_t raw_path_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept;

VN_EXPORT int32_t __stdcall vn_spotlight_search_compare_keys(
    double score_a,
    const char16_t* title_a_ptr,
    int32_t title_a_len,
    const char16_t* id_a_ptr,
    int32_t id_a_len,
    double score_b,
    const char16_t* title_b_ptr,
    int32_t title_b_len,
    const char16_t* id_b_ptr,
    int32_t id_b_len,
    int32_t* out_cmp) noexcept;

// ---------------------------------------------------------------------------
// Clipboard Store, Sensitive Data & Selection Extended Operations
// ---------------------------------------------------------------------------

#ifndef VN_CLIPBOARD_PRUNE_CANDIDATE_DEFINED
#define VN_CLIPBOARD_PRUNE_CANDIDATE_DEFINED
#pragma pack(push, 1)
struct VnClipboardPruneCandidate {
    int32_t entry_index;
    uint8_t is_personal;
    uint8_t is_pinned;
    uint8_t is_archived;
    int64_t copied_utc_ticks;
    int64_t byte_size;
};
#pragma pack(pop)
#endif

VN_EXPORT int32_t __stdcall vn_clipboard_store_tokenize_query(
    const char16_t* query,
    int32_t query_len,
    int32_t* out_starts,
    int32_t* out_lengths,
    int32_t max_tokens,
    int32_t* out_token_count) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_contains_icase(
    const char16_t* haystack,
    int32_t haystack_len,
    const char16_t* needle,
    int32_t needle_len,
    uint8_t* out_contains) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_equals_icase(
    const char16_t* a,
    int32_t a_len,
    const char16_t* b,
    int32_t b_len,
    uint8_t* out_equals) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_entry_matches_metadata_token(
    uint8_t is_sensitive,
    const char16_t* title,
    int32_t title_len,
    const char16_t* preview,
    int32_t preview_len,
    const char16_t* source_app,
    int32_t source_app_len,
    const char16_t* token,
    int32_t token_len,
    uint8_t* out_matches) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_plan_prune(
    const VnClipboardPruneCandidate* candidates,
    int32_t candidate_count,
    int64_t cutoff_utc_ticks,
    int32_t max_unpinned_entries,
    int64_t max_unpinned_bytes,
    int32_t* out_ordered_indices,
    int32_t max_indices,
    int32_t* out_expired_count,
    int32_t* out_evict_candidate_count,
    int64_t* out_total_active_bytes) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_evaluate_sensitive_expiry(
    uint8_t has_entry,
    uint8_t is_pinned,
    uint8_t is_archived,
    uint8_t is_personal,
    uint8_t has_personal_dir,
    uint8_t ticks_valid,
    int64_t marker_ticks,
    int64_t expiry_cutoff_ticks,
    int32_t* out_action) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_is_vault_stream_magic(
    const uint8_t* magic,
    int32_t magic_len,
    uint8_t* out_is_magic) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_format_prefixed_sha256_hex(
    const char16_t* prefix,
    int32_t prefix_len,
    const uint8_t* hash_bytes,
    int32_t hash_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_are_signatures_equal(
    const char16_t* sig_a,
    int32_t len_a,
    const char16_t* sig_b,
    int32_t len_b,
    uint8_t* out_equal) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_is_probable_duplicate_image_candidate(
    int32_t kind,
    uint8_t is_personal,
    int64_t capture_ticks,
    int64_t entry_ticks,
    int64_t best_ticks,
    uint8_t has_best,
    uint8_t* out_is_better) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_is_export_path_protected(
    const char16_t* directory,
    int32_t directory_len,
    const char16_t* active_lease,
    int32_t active_lease_len,
    uint16_t dir_sep,
    uint8_t* out_protected) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_sensitive_analyze(
    const char16_t* text_ptr, int32_t text_len,
    const char16_t* app_ptr, int32_t app_len,
    const char16_t* exe_ptr, int32_t exe_len,
    int32_t* out_reason, int32_t* out_confidence) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_sensitive_format_mask(
    const char16_t* text_ptr, int32_t text_len,
    char16_t* out_mask, int32_t out_cap,
    int32_t* out_len) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_selection_scroll_speed(
    double x,
    double width,
    double* out_speed) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_selection_compute_lasso_range(
    int32_t card_count,
    int32_t ref_index,
    double container_width,
    double margin_left,
    double margin_right,
    double origin_x,
    double origin_y,
    double container_height,
    double scroll_offset,
    double rect_left,
    double rect_top,
    double rect_right,
    double rect_bottom,
    double row_top,
    double row_bottom,
    uint8_t swept,
    int32_t* out_first,
    int32_t* out_last,
    uint8_t* out_hits_row,
    uint8_t* out_expand_vertical) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_selection_filter_ordered_indices(
    const int32_t* selected_indices,
    int32_t selected_count,
    int32_t card_count,
    uint8_t* scratch_mask,
    int32_t* out_ordered_indices,
    int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_selection_merge_optimistic_order(
    int32_t desired_count,
    const int32_t* optimistic_to_desired_index,
    int32_t optimistic_count,
    uint8_t* scratch_taken_mask,
    int32_t* out_merged_desired_indices,
    int32_t* out_merged_count) noexcept;

// ---------------------------------------------------------------------------
// Privacy Indicator, Window Title Scanner, Lyrics, YouTube & Media Metadata
// ---------------------------------------------------------------------------

#ifndef VN_PRIVACY_INDICATOR_BATCH_DEFINED
#define VN_PRIVACY_INDICATOR_BATCH_DEFINED
#pragma pack(push, 1)
struct VnConsentStoreEntry {
    int64_t last_start_file_time;
    int32_t raw_len;
    int32_t display_start;
    int32_t display_len;
    char16_t raw_name[260];
};

struct VnActiveAppSlice {
    int32_t name_offset;
    int32_t name_len;
    int64_t last_start_file_time;
};
#pragma pack(pop)
#endif

#ifndef VN_VISIBLE_WINDOW_ENTRY_DEFINED
#define VN_VISIBLE_WINDOW_ENTRY_DEFINED
#pragma pack(push, 1)
struct VnVisibleWindowEntry {
    int64_t hwnd;
    uint32_t process_id;
    int32_t title_len;
    uint8_t matches_platform_title;
    char16_t title[256];
};
#pragma pack(pop)
#endif

#ifndef VN_CAPTION_TRACK_META_DEFINED
#define VN_CAPTION_TRACK_META_DEFINED
#pragma pack(push, 1)
struct VnCaptionTrackMeta {
    int32_t lang_offset;
    int32_t lang_len;
    uint8_t is_auto_generated;
    uint8_t is_vss_a_prefix;
};
#pragma pack(pop)
#endif

VN_EXPORT int32_t __stdcall vn_privacy_indicator_scan_consent_store(
    const char16_t* capability,
    int32_t capability_len,
    VnConsentStoreEntry* out_entries,
    int32_t entries_cap,
    int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_privacy_indicator_merge_active_apps(
    const VnActiveAppSlice* items,
    int32_t item_count,
    const char16_t* char_pool,
    int32_t char_pool_len,
    uint8_t sort_by_name,
    int32_t* out_indices,
    int32_t out_cap,
    int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_wtscanner_collect_visible_windows(
    uint32_t exclude_pid,
    uint8_t only_platform_titles,
    VnVisibleWindowEntry* out_entries,
    int32_t entries_cap,
    int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_lyrics_strip_title_extras(
    const char16_t* title_ptr,
    int32_t title_len,
    uint8_t is_turkic_culture,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_youtube_subtitle_normalize_cue_text(
    const char16_t* raw_text,
    int32_t raw_len,
    uint8_t strip_xml_tags,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_youtube_subtitle_clamp_timestamp_ticks(
    int64_t raw_ms,
    double raw_seconds,
    uint8_t use_seconds,
    int64_t* out_ticks,
    uint8_t* out_valid) noexcept;

VN_EXPORT int32_t __stdcall vn_youtube_subtitle_order_candidate_tracks(
    const VnCaptionTrackMeta* tracks,
    int32_t track_count,
    const char16_t* lang_pool,
    int32_t lang_pool_len,
    const uint8_t* priority_modes,
    int32_t priority_count,
    const char16_t* native_lang_hint,
    int32_t native_lang_hint_len,
    uint8_t ignore_auto_generated,
    int32_t* out_indices,
    int32_t out_cap,
    int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_media_metadata_extract_youtube_video_id(
    const char16_t* input,
    int32_t input_len,
    uint8_t mode,
    int32_t* out_start,
    int32_t* out_len,
    uint8_t* out_found) noexcept;

VN_EXPORT int32_t __stdcall vn_media_metadata_score_youtube_track_match(
    const char16_t* candidate_title,
    int32_t candidate_title_len,
    const char16_t* target_title,
    int32_t target_title_len,
    int64_t candidate_duration_sec,
    int64_t target_duration_sec,
    int32_t* out_score,
    int32_t* out_duration_delta,
    uint8_t* out_is_match) noexcept;

// ---------------------------------------------------------------------------
// AI Streaming, Markdown Extended & Settings Search Batch
// ---------------------------------------------------------------------------

#ifndef VN_MARKDOWN_EXTENDED_DEFINED
#define VN_MARKDOWN_EXTENDED_DEFINED
#pragma pack(push, 1)
struct VnMarkdownCodeFenceHeader
{
    uint8_t is_fence;
    uint8_t fence_char;
    uint8_t fence_count;
    uint8_t reserved;
    int32_t lang_start;
    int32_t lang_length;
};

struct VnMarkdownListPrefix
{
    uint8_t list_kind;
    uint8_t is_checked;
    uint16_t ordered_number;
    int32_t prefix_start;
    int32_t prefix_length;
    int32_t content_start;
    int32_t content_length;
};

struct VnMarkdownSpanBoundarySummary
{
    uint8_t has_unclosed_fence;
    uint8_t has_unclosed_inline_code;
    uint8_t has_unclosed_bold;
    uint8_t has_unclosed_italic;
    int32_t fence_count;
    int32_t inline_code_count;
    int32_t bold_marker_count;
    int32_t italic_marker_count;
};
#pragma pack(pop)
#endif

#ifndef VN_SETTINGS_SEARCH_BATCH_DEFINED
#define VN_SETTINGS_SEARCH_BATCH_DEFINED
#pragma pack(push, 1)
struct VnSettingsSearchRowSpan
{
    int32_t title_offset;
    int32_t title_len;
    int32_t content_offset;
    int32_t content_len;
    int32_t combined_offset;
    int32_t combined_len;
};

struct VnSettingsSearchMatchResult
{
    int32_t row_index;
    int32_t score;
};
#pragma pack(pop)
#endif

VN_EXPORT int32_t __stdcall vn_spotlight_ai_parse_sse_data_line(
    const char16_t* line_ptr,
    int32_t line_len,
    int32_t* out_payload_offset,
    int32_t* out_payload_len,
    uint8_t* out_is_data,
    uint8_t* out_is_done) noexcept;

VN_EXPORT int32_t __stdcall vn_spotlight_ai_extract_json_delta(
    const char16_t* payload_ptr,
    int32_t payload_len,
    const char16_t* provider_ptr,
    int32_t provider_len,
    char16_t* out_delta_buf,
    int32_t delta_cap,
    int32_t* out_delta_len,
    uint8_t* out_status,
    uint8_t* out_completed) noexcept;

VN_EXPORT int32_t __stdcall vn_markdown_scan_word_spans(
    const char16_t* text_ptr,
    int32_t text_len,
    VnMarkdownSlice* out_spans,
    int32_t span_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_markdown_parse_code_fence_header(
    const char16_t* line_ptr,
    int32_t line_len,
    VnMarkdownCodeFenceHeader* out_header) noexcept;

VN_EXPORT int32_t __stdcall vn_markdown_strip_list_prefix(
    const char16_t* line_ptr,
    int32_t line_len,
    VnMarkdownListPrefix* out_prefix) noexcept;

VN_EXPORT int32_t __stdcall vn_markdown_scan_span_boundaries(
    const char16_t* text_ptr,
    int32_t text_len,
    VnMarkdownSpanBoundarySummary* out_summary,
    VnMarkdownSlice* out_code_spans,
    int32_t code_span_cap,
    int32_t* out_code_span_count) noexcept;

VN_EXPORT int32_t __stdcall vn_settings_search_normalize_row(
    const char16_t* title_ptr,
    int32_t title_len,
    const char16_t* content_ptr,
    int32_t content_len,
    const char16_t* section_ptr,
    int32_t section_len,
    char16_t* out_arena_buf,
    int32_t arena_cap,
    VnSettingsSearchRowSpan* out_span,
    int32_t* out_arena_len) noexcept;

VN_EXPORT int32_t __stdcall vn_settings_search_evaluate_batch(
    const char16_t* arena_ptr,
    int32_t arena_len,
    const VnSettingsSearchRowSpan* rows_ptr,
    int32_t row_count,
    const char16_t* query_ptr,
    int32_t query_len,
    VnSettingsSearchMatchResult* out_results,
    int32_t results_cap,
    int32_t* out_match_count) noexcept;

// ---------------------------------------------------------------------------
// UI Motion, Particles, Morphing & Visualizer Bars (UiMotionAndParticles.cpp)
// ---------------------------------------------------------------------------

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

VN_EXPORT int32_t __stdcall vn_dust_particles_init(
    VnDustParticle* particles,
    int32_t capacity,
    double width,
    double height,
    uint32_t* io_rng_state,
    int32_t* io_r2_sequence,
    int32_t* out_active_count) noexcept;

VN_EXPORT int32_t __stdcall vn_dust_particles_step(
    VnDustParticle* particles,
    int32_t active_count,
    double width,
    double height,
    float dt,
    uint8_t is_dissolving,
    double dissolve_progress,
    double reform_progress,
    uint32_t* io_rng_state,
    int32_t* io_r2_sequence) noexcept;

VN_EXPORT int32_t __stdcall vn_dust_particles_dissolve_impulse(
    VnDustParticle* particles,
    int32_t active_count,
    double width,
    double height,
    float origin_x,
    float origin_y,
    uint32_t* io_rng_state) noexcept;

VN_EXPORT int32_t __stdcall vn_morph_settings_icon_sample_contour(
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
    double* out_bbox_area) noexcept;

VN_EXPORT int32_t __stdcall vn_morph_settings_icon_compute_points(
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
    int32_t* out_point_count) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_motion_compute_stagger(
    const int32_t* candidate_indices,
    int32_t count,
    int32_t base_delay_ms,
    int32_t* out_sorted_positions,
    int32_t* out_delays_ms) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_motion_card_delay(
    int32_t item_index,
    int32_t animated_count,
    int32_t base_delay_ms,
    int32_t* out_delay_ms) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_motion_compute_reflow_offset(
    double origin_x,
    double rendered_offset_x,
    double scroll_offset,
    double previous_x,
    uint8_t has_previous_layout,
    double previous_layout_x,
    uint8_t same_transform,
    double* out_layout_x,
    double* out_offset_x,
    uint8_t* out_should_animate) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_motion_step_scroll_physics(
    double* io_position,
    double* io_velocity,
    double min_bound,
    double max_bound,
    double dt,
    double friction,
    double spring_stiffness,
    double spring_damping,
    uint8_t* out_is_settled) noexcept;

VN_EXPORT int32_t __stdcall vn_visualizer_step_bars(
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
    uint8_t* out_draw_settled) noexcept;

VN_EXPORT int32_t __stdcall vn_visualizer_compute_morph_quads(
    const double* bar_rects_xywh,
    int32_t bar_count,
    double width,
    double height,
    double play_mix,
    double check_mix,
    double icon_mix,
    double* out_quad_points_xy,
    int32_t out_cap_doubles) noexcept;

// ---------------------------------------------------------------------------
// Spotlight Search & Providers Extended Operations
// ---------------------------------------------------------------------------

VN_EXPORT int32_t __stdcall vn_app_search_provider_scan_start_menu(
    const char16_t* start_menu_ptr,
    int32_t start_menu_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_everything_provider_read_reply_item_count(
    const uint8_t* list_ptr,
    int32_t byte_count,
    int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_everything_provider_extract_reply_item(
    const uint8_t* list_ptr,
    int32_t byte_count,
    int32_t index,
    int32_t* out_name_offset,
    int32_t* out_name_len,
    int32_t* out_path_offset,
    int32_t* out_path_len,
    uint8_t* out_is_folder) noexcept;

VN_EXPORT int32_t __stdcall vn_everything_provider_pack_ipc_query(
    int64_t reply_hwnd,
    uint32_t query_id,
    uint32_t search_flags,
    int32_t max_results,
    const char16_t* query_ptr,
    int32_t query_len,
    uint8_t* out_buf,
    int32_t out_cap,
    int32_t* out_bytes) noexcept;

VN_EXPORT int32_t __stdcall vn_windows_search_build_query_sql(
    const char16_t* sanitized_ptr, int32_t sanitized_len,
    const char16_t* original_ptr, int32_t original_len,
    const char16_t* scope_ptr, int32_t scope_len,
    uint8_t is_explicit_path,
    int32_t fetch,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept;

VN_EXPORT int32_t __stdcall vn_windows_search_normalize_result_path(
    const char16_t* raw_path_ptr,
    int32_t raw_path_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept;

VN_EXPORT int32_t __stdcall vn_spotlight_search_compare_keys(
    double score_a,
    const char16_t* title_a_ptr,
    int32_t title_a_len,
    const char16_t* id_a_ptr,
    int32_t id_a_len,
    double score_b,
    const char16_t* title_b_ptr,
    int32_t title_b_len,
    const char16_t* id_b_ptr,
    int32_t id_b_len,
    int32_t* out_cmp) noexcept;

// ---------------------------------------------------------------------------
// Clipboard Store, Sensitive Data & Selection Extended Operations
// ---------------------------------------------------------------------------

#ifndef VN_CLIPBOARD_PRUNE_CANDIDATE_DEFINED
#define VN_CLIPBOARD_PRUNE_CANDIDATE_DEFINED
#pragma pack(push, 1)
struct VnClipboardPruneCandidate {
    int32_t entry_index;
    uint8_t is_personal;
    uint8_t is_pinned;
    uint8_t is_archived;
    int64_t copied_utc_ticks;
    int64_t byte_size;
};
#pragma pack(pop)
#endif

VN_EXPORT int32_t __stdcall vn_clipboard_store_tokenize_query(
    const char16_t* query,
    int32_t query_len,
    int32_t* out_starts,
    int32_t* out_lengths,
    int32_t max_tokens,
    int32_t* out_token_count) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_contains_icase(
    const char16_t* haystack,
    int32_t haystack_len,
    const char16_t* needle,
    int32_t needle_len,
    uint8_t* out_contains) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_equals_icase(
    const char16_t* a,
    int32_t a_len,
    const char16_t* b,
    int32_t b_len,
    uint8_t* out_equals) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_entry_matches_metadata_token(
    uint8_t is_sensitive,
    const char16_t* title,
    int32_t title_len,
    const char16_t* preview,
    int32_t preview_len,
    const char16_t* source_app,
    int32_t source_app_len,
    const char16_t* token,
    int32_t token_len,
    uint8_t* out_matches) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_plan_prune(
    const VnClipboardPruneCandidate* candidates,
    int32_t candidate_count,
    int64_t cutoff_utc_ticks,
    int32_t max_unpinned_entries,
    int64_t max_unpinned_bytes,
    int32_t* out_ordered_indices,
    int32_t max_indices,
    int32_t* out_expired_count,
    int32_t* out_evict_candidate_count,
    int64_t* out_total_active_bytes) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_evaluate_sensitive_expiry(
    uint8_t has_entry,
    uint8_t is_pinned,
    uint8_t is_archived,
    uint8_t is_personal,
    uint8_t has_personal_dir,
    uint8_t ticks_valid,
    int64_t marker_ticks,
    int64_t expiry_cutoff_ticks,
    int32_t* out_action) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_is_vault_stream_magic(
    const uint8_t* magic,
    int32_t magic_len,
    uint8_t* out_is_magic) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_format_prefixed_sha256_hex(
    const char16_t* prefix,
    int32_t prefix_len,
    const uint8_t* hash_bytes,
    int32_t hash_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_are_signatures_equal(
    const char16_t* sig_a,
    int32_t len_a,
    const char16_t* sig_b,
    int32_t len_b,
    uint8_t* out_equal) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_is_probable_duplicate_image_candidate(
    int32_t kind,
    uint8_t is_personal,
    int64_t capture_ticks,
    int64_t entry_ticks,
    int64_t best_ticks,
    uint8_t has_best,
    uint8_t* out_is_better) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_store_is_export_path_protected(
    const char16_t* directory,
    int32_t directory_len,
    const char16_t* active_lease,
    int32_t active_lease_len,
    uint16_t dir_sep,
    uint8_t* out_protected) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_sensitive_analyze(
    const char16_t* text_ptr, int32_t text_len,
    const char16_t* app_ptr, int32_t app_len,
    const char16_t* exe_ptr, int32_t exe_len,
    int32_t* out_reason, int32_t* out_confidence) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_sensitive_format_mask(
    const char16_t* text_ptr, int32_t text_len,
    char16_t* out_mask, int32_t out_cap,
    int32_t* out_len) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_selection_scroll_speed(
    double x,
    double width,
    double* out_speed) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_selection_compute_lasso_range(
    int32_t card_count,
    int32_t ref_index,
    double container_width,
    double margin_left,
    double margin_right,
    double origin_x,
    double origin_y,
    double container_height,
    double scroll_offset,
    double rect_left,
    double rect_top,
    double rect_right,
    double rect_bottom,
    double row_top,
    double row_bottom,
    uint8_t swept,
    int32_t* out_first,
    int32_t* out_last,
    uint8_t* out_hits_row,
    uint8_t* out_expand_vertical) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_selection_filter_ordered_indices(
    const int32_t* selected_indices,
    int32_t selected_count,
    int32_t card_count,
    uint8_t* scratch_mask,
    int32_t* out_ordered_indices,
    int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_selection_merge_optimistic_order(
    int32_t desired_count,
    const int32_t* optimistic_to_desired_index,
    int32_t optimistic_count,
    uint8_t* scratch_taken_mask,
    int32_t* out_merged_desired_indices,
    int32_t* out_merged_count) noexcept;

// ---------------------------------------------------------------------------
// Privacy Indicator, Window Title Scanner, Lyrics, YouTube & Media Metadata
// ---------------------------------------------------------------------------

#ifndef VN_PRIVACY_INDICATOR_BATCH_DEFINED
#define VN_PRIVACY_INDICATOR_BATCH_DEFINED
#pragma pack(push, 1)
struct VnConsentStoreEntry {
    int64_t last_start_file_time;
    int32_t raw_len;
    int32_t display_start;
    int32_t display_len;
    char16_t raw_name[260];
};

struct VnActiveAppSlice {
    int32_t name_offset;
    int32_t name_len;
    int64_t last_start_file_time;
};
#pragma pack(pop)
#endif

#ifndef VN_VISIBLE_WINDOW_ENTRY_DEFINED
#define VN_VISIBLE_WINDOW_ENTRY_DEFINED
#pragma pack(push, 1)
struct VnVisibleWindowEntry {
    int64_t hwnd;
    uint32_t process_id;
    int32_t title_len;
    uint8_t matches_platform_title;
    char16_t title[256];
};
#pragma pack(pop)
#endif

#ifndef VN_CAPTION_TRACK_META_DEFINED
#define VN_CAPTION_TRACK_META_DEFINED
#pragma pack(push, 1)
struct VnCaptionTrackMeta {
    int32_t lang_offset;
    int32_t lang_len;
    uint8_t is_auto_generated;
    uint8_t is_vss_a_prefix;
};
#pragma pack(pop)
#endif

VN_EXPORT int32_t __stdcall vn_privacy_indicator_scan_consent_store(
    const char16_t* capability,
    int32_t capability_len,
    VnConsentStoreEntry* out_entries,
    int32_t entries_cap,
    int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_privacy_indicator_merge_active_apps(
    const VnActiveAppSlice* items,
    int32_t item_count,
    const char16_t* char_pool,
    int32_t char_pool_len,
    uint8_t sort_by_name,
    int32_t* out_indices,
    int32_t out_cap,
    int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_wtscanner_collect_visible_windows(
    uint32_t exclude_pid,
    uint8_t only_platform_titles,
    VnVisibleWindowEntry* out_entries,
    int32_t entries_cap,
    int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_lyrics_strip_title_extras(
    const char16_t* title_ptr,
    int32_t title_len,
    uint8_t is_turkic_culture,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_youtube_subtitle_normalize_cue_text(
    const char16_t* raw_text,
    int32_t raw_len,
    uint8_t strip_xml_tags,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_youtube_subtitle_clamp_timestamp_ticks(
    int64_t raw_ms,
    double raw_seconds,
    uint8_t use_seconds,
    int64_t* out_ticks,
    uint8_t* out_valid) noexcept;

VN_EXPORT int32_t __stdcall vn_youtube_subtitle_order_candidate_tracks(
    const VnCaptionTrackMeta* tracks,
    int32_t track_count,
    const char16_t* lang_pool,
    int32_t lang_pool_len,
    const uint8_t* priority_modes,
    int32_t priority_count,
    const char16_t* native_lang_hint,
    int32_t native_lang_hint_len,
    uint8_t ignore_auto_generated,
    int32_t* out_indices,
    int32_t out_cap,
    int32_t* out_count) noexcept;

VN_EXPORT int32_t __stdcall vn_media_metadata_extract_youtube_video_id(
    const char16_t* input,
    int32_t input_len,
    uint8_t mode,
    int32_t* out_start,
    int32_t* out_len,
    uint8_t* out_found) noexcept;

VN_EXPORT int32_t __stdcall vn_media_metadata_score_youtube_track_match(
    const char16_t* candidate_title,
    int32_t candidate_title_len,
    const char16_t* target_title,
    int32_t target_title_len,
    int64_t candidate_duration_sec,
    int64_t target_duration_sec,
    int32_t* out_score,
    int32_t* out_duration_delta,
    uint8_t* out_is_match) noexcept;

// ---------------------------------------------------------------------------
// AI Streaming, Markdown Extended & Settings Search Batch
// ---------------------------------------------------------------------------

#ifndef VN_MARKDOWN_EXTENDED_DEFINED
#define VN_MARKDOWN_EXTENDED_DEFINED
#pragma pack(push, 1)
struct VnMarkdownCodeFenceHeader
{
    uint8_t is_fence;
    uint8_t fence_char;
    uint8_t fence_count;
    uint8_t reserved;
    int32_t lang_start;
    int32_t lang_length;
};

struct VnMarkdownListPrefix
{
    uint8_t list_kind;
    uint8_t is_checked;
    uint16_t ordered_number;
    int32_t prefix_start;
    int32_t prefix_length;
    int32_t content_start;
    int32_t content_length;
};

struct VnMarkdownSpanBoundarySummary
{
    uint8_t has_unclosed_fence;
    uint8_t has_unclosed_inline_code;
    uint8_t has_unclosed_bold;
    uint8_t has_unclosed_italic;
    int32_t fence_count;
    int32_t inline_code_count;
    int32_t bold_marker_count;
    int32_t italic_marker_count;
};
#pragma pack(pop)
#endif

#ifndef VN_SETTINGS_SEARCH_BATCH_DEFINED
#define VN_SETTINGS_SEARCH_BATCH_DEFINED
#pragma pack(push, 1)
struct VnSettingsSearchRowSpan
{
    int32_t title_offset;
    int32_t title_len;
    int32_t content_offset;
    int32_t content_len;
    int32_t combined_offset;
    int32_t combined_len;
};

struct VnSettingsSearchMatchResult
{
    int32_t row_index;
    int32_t score;
};
#pragma pack(pop)
#endif

VN_EXPORT int32_t __stdcall vn_spotlight_ai_parse_sse_data_line(
    const char16_t* line_ptr,
    int32_t line_len,
    int32_t* out_payload_offset,
    int32_t* out_payload_len,
    uint8_t* out_is_data,
    uint8_t* out_is_done) noexcept;

VN_EXPORT int32_t __stdcall vn_spotlight_ai_extract_json_delta(
    const char16_t* payload_ptr,
    int32_t payload_len,
    const char16_t* provider_ptr,
    int32_t provider_len,
    char16_t* out_delta_buf,
    int32_t delta_cap,
    int32_t* out_delta_len,
    uint8_t* out_status,
    uint8_t* out_completed) noexcept;

VN_EXPORT int32_t __stdcall vn_markdown_scan_word_spans(
    const char16_t* text_ptr,
    int32_t text_len,
    VnMarkdownSlice* out_spans,
    int32_t span_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_markdown_parse_code_fence_header(
    const char16_t* line_ptr,
    int32_t line_len,
    VnMarkdownCodeFenceHeader* out_header) noexcept;

VN_EXPORT int32_t __stdcall vn_markdown_strip_list_prefix(
    const char16_t* line_ptr,
    int32_t line_len,
    VnMarkdownListPrefix* out_prefix) noexcept;

VN_EXPORT int32_t __stdcall vn_markdown_scan_span_boundaries(
    const char16_t* text_ptr,
    int32_t text_len,
    VnMarkdownSpanBoundarySummary* out_summary,
    VnMarkdownSlice* out_code_spans,
    int32_t code_span_cap,
    int32_t* out_code_span_count) noexcept;

VN_EXPORT int32_t __stdcall vn_settings_search_normalize_row(
    const char16_t* title_ptr,
    int32_t title_len,
    const char16_t* content_ptr,
    int32_t content_len,
    const char16_t* section_ptr,
    int32_t section_len,
    char16_t* out_arena_buf,
    int32_t arena_cap,
    VnSettingsSearchRowSpan* out_span,
    int32_t* out_arena_len) noexcept;

VN_EXPORT int32_t __stdcall vn_settings_search_evaluate_batch(
    const char16_t* arena_ptr,
    int32_t arena_len,
    const VnSettingsSearchRowSpan* rows_ptr,
    int32_t row_count,
    const char16_t* query_ptr,
    int32_t query_len,
    VnSettingsSearchMatchResult* out_results,
    int32_t results_cap,
    int32_t* out_match_count) noexcept;
