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

// Flat parsing results borrow native-owned storage until their close function.
struct VnLyricWord { int64_t start, end; int32_t offset, length; };
struct VnLyricLine { int64_t start; int32_t offset, length, word_offset, word_count; };
static_assert(sizeof(VnLyricWord) == 24 && sizeof(VnLyricLine) == 24);
VN_EXPORT int32_t vn_lyrics_parse(const uint8_t*, int32_t, int32_t, int32_t,
    int64_t*, const VnLyricLine**, int32_t*, const VnLyricWord**, const char16_t**) noexcept;
VN_EXPORT int32_t vn_lyrics_parsed_close(int64_t) noexcept;
VN_EXPORT int32_t vn_lyrics_parse_enhanced(const char16_t*, int32_t, int64_t,
    char16_t*, int32_t, int32_t*, VnLyricWord*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_catalog_select_utf8(const uint8_t*, int32_t, int32_t,
    const char16_t*, int32_t, const char16_t*, int32_t, const char16_t*, int32_t, int64_t, const uint8_t*, int32_t, int64_t,
    char16_t*, int32_t, int32_t*, char16_t*, int32_t, int32_t*, int64_t*, int32_t*, int32_t*) noexcept;
VN_EXPORT int32_t vn_youtube_extract_api_key_utf8(const uint8_t*, int32_t, char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_clipboard_index_create(int64_t*) noexcept;
VN_EXPORT int32_t vn_clipboard_index_close(int64_t) noexcept;
VN_EXPORT int32_t vn_clipboard_index_put(int64_t, uint64_t, uint64_t, int32_t, const char16_t*, int32_t, int64_t, uint8_t) noexcept;
VN_EXPORT int32_t vn_clipboard_index_find(int64_t, uint64_t, uint64_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_clipboard_index_remove(int64_t, uint64_t, uint64_t) noexcept;
VN_EXPORT int32_t vn_clipboard_index_duplicate(int64_t, const char16_t*, int32_t, uint8_t, int32_t*) noexcept;
struct VnClipboardDirectoryRow { int64_t size, modified; int32_t offset, length; uint32_t attributes, reserved; };
VN_EXPORT int32_t vn_clipboard_directory_scan(const char16_t*, int32_t, const char16_t*, int32_t, int32_t,
    int64_t*, const VnClipboardDirectoryRow**, int32_t*, const char16_t**, int32_t*) noexcept;
VN_EXPORT int32_t vn_clipboard_directory_scan_close(int64_t) noexcept;
VN_EXPORT int32_t vn_clipboard_stream_copy(intptr_t, intptr_t, int32_t (*)(intptr_t, uint8_t*, int32_t),
    int32_t (*)(intptr_t, const uint8_t*, int32_t), intptr_t, int64_t, int64_t*, int32_t*) noexcept;

// Native selection results retain their UIA element/range until vn_selection_close.
VN_EXPORT int32_t vn_selection_read(intptr_t, const int32_t*, int32_t, intptr_t, int64_t, uint8_t, uint8_t, int32_t,
    char16_t*, int32_t, int32_t*, double*, uint8_t*, uint8_t*, uint8_t*, int64_t*) noexcept;
VN_EXPORT int32_t vn_selection_close(int64_t) noexcept;
VN_EXPORT int32_t vn_selection_same(int64_t, int64_t, uint8_t*) noexcept;
VN_EXPORT int32_t vn_selection_prepare_replace(int64_t, const char16_t*, int32_t, const char16_t*, int32_t, int32_t*, int32_t*) noexcept;
VN_EXPORT int32_t vn_wtscanner_media_title(const char16_t*, int32_t, uint32_t, uint8_t, char16_t*, int32_t,
    int32_t*, int32_t*, int32_t*, int32_t*) noexcept;
VN_EXPORT int32_t vn_wtscanner_has_youtube_title(const char16_t*, int32_t, uint32_t, uint8_t*) noexcept;

// A mapped ciphertext file produces authenticated native plaintext; close zeroes it.
VN_EXPORT int32_t vn_clipboard_vault_open_image(intptr_t, const uint8_t*, int32_t, int64_t*, uint8_t**, int32_t*) noexcept;
VN_EXPORT int32_t vn_clipboard_vault_close_image(int64_t) noexcept;

#pragma pack(push, 1)
struct VnYoutubeJsonRow {
    int32_t offset[5]{}, length[5]{};
    int64_t ticks = 0;
    int32_t flags = 0;
};
#pragma pack(pop)
static_assert(sizeof(VnYoutubeJsonRow) == 52);
// All returned pointers borrow the parsed response handle's lifetime.
VN_EXPORT int32_t vn_youtube_parse_utf8(const uint8_t*, int32_t, int32_t, const char16_t*, int32_t,
    int64_t*, const VnYoutubeJsonRow**, int32_t*, const char16_t**, uint8_t*) noexcept;
VN_EXPORT int32_t vn_youtube_parsed_close(int64_t) noexcept;

#pragma pack(push, 1)
struct VnSystemMetricsState {
    uint64_t process_time, idle_time, kernel_time, user_time;
    int64_t timestamp;
    double process_cpu, global_cpu;
};
struct VnSystemMetricsSnapshot {
    double process_cpu, global_cpu;
    uint64_t working_set, ram_used, ram_total;
    double ram_percent, process_gpu, global_gpu;
};
#pragma pack(pop)
VN_EXPORT int32_t __stdcall vn_system_metrics_sample(VnSystemMetricsState*, VnSystemMetricsSnapshot*) noexcept;
VN_EXPORT int32_t __stdcall vn_gpu_monitor_sample(VnSystemMetricsSnapshot*) noexcept;
VN_EXPORT int32_t __stdcall vn_gpu_monitor_reset() noexcept;

#pragma pack(push, 1)
struct VnAudioSessionState {
    uint32_t process_id;
    float volume;
    uint8_t muted, system_sounds;
    int32_t name_offset, name_length, path_offset, path_length, raw_offset, raw_length;
};
struct VnAudioDeviceState {
    int32_t id_offset, id_length, name_offset, name_length;
    uint8_t is_default;
};
#pragma pack(pop)
VN_EXPORT int32_t __stdcall vn_audio_get_sessions_batch(VnAudioSessionState*, int32_t, char16_t*, int32_t, int32_t*, int32_t*, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_audio_resolve_session_metadata(uint32_t, const char16_t*, int32_t, VnAudioSessionState*, char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_audio_enumerate_devices_batch(int32_t, VnAudioDeviceState*, int32_t, char16_t*, int32_t, int32_t*, int32_t*, int32_t*) noexcept;

#pragma pack(push, 1)
struct VnPrivacyProcessEntry {
    uint32_t process_id, parent_process_id;
    char16_t executable_file[260];
};
#pragma pack(pop)
struct VnConsentStoreEntry;
VN_EXPORT int32_t vn_privacy_consumers_create(const VnPrivacyProcessEntry*, int32_t, int64_t*) noexcept;
VN_EXPORT int32_t vn_privacy_consumers_close(int64_t) noexcept;
VN_EXPORT int32_t vn_privacy_consumers_match(int64_t, const char16_t*, int32_t, uint32_t, uint8_t*) noexcept;
VN_EXPORT int32_t vn_privacy_consent_cache_create(int64_t*) noexcept;
VN_EXPORT int32_t vn_privacy_consent_cache_close(int64_t) noexcept;
VN_EXPORT int32_t vn_privacy_consent_cache_refresh(int64_t, uint8_t) noexcept;
VN_EXPORT int32_t vn_privacy_consumers_scan(int64_t, int64_t, const char16_t*, int32_t, VnConsentStoreEntry*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_privacy_consumers_cache_package(uint32_t, const char16_t*, int32_t, int64_t, int64_t) noexcept;
VN_EXPORT int32_t __stdcall vn_privacy_consumers_query_package_cache(uint32_t, int32_t*, uint8_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_privacy_consumers_clear_package_cache() noexcept;
VN_EXPORT int32_t __stdcall vn_privacy_poll_recorders(const VnPrivacyProcessEntry*, int32_t, uint8_t, uint8_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_privproc_contains_consumer(const VnPrivacyProcessEntry*, int32_t, const char16_t*, int32_t, uint8_t, uint8_t*) noexcept;

#pragma pack(push, 1)
struct VnClipboardSearchSlice { int32_t offset, length; };
struct VnClipboardSearchEntry {
    VnClipboardSearchSlice title, preview, source, full_text;
    int32_t group_start, group_count, kind;
    uint8_t personal, archived, pinned, sensitive;
};
struct VnClipboardSearchLabel { VnClipboardSearchSlice id, name; };
#pragma pack(pop)
VN_EXPORT int32_t __stdcall vn_clipboard_store_filter_entries_batch(const VnClipboardSearchEntry*, int32_t,
    const VnClipboardSearchSlice*, int32_t, const VnClipboardSearchLabel*, int32_t, const char16_t*, int32_t,
    const char16_t*, int32_t, const char16_t*, int32_t, uint8_t, uint8_t*, int32_t) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_hash_create(int64_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_hash_close(int64_t) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_hash_append(int64_t, const uint8_t*, int32_t) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_hash_append_utf16(int64_t, const char16_t*, int32_t, uint8_t) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_hash_finish(int64_t, uint8_t*, int32_t) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_hash_bytes(const uint8_t*, int32_t, uint8_t*, int32_t) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_hash_pixels(const uint8_t*, int32_t, int32_t, int32_t, uint8_t*, int32_t) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_hash_image(const uint8_t*, int32_t, const char16_t*, int32_t, uint8_t*, int32_t) noexcept;

#pragma pack(push, 1)
struct VnBrowserTabSnapshot { int32_t url_offset, url_length; uint8_t spotify; };
struct VnBrowserChromeSnapshot {
    int32_t url_offset, url_length, address_offset, address_length, toolbar_count, tab_count;
    uint8_t spotify;
};
#pragma pack(pop)
VN_EXPORT int32_t __stdcall vn_wtscanner_scan_browser(intptr_t, uint8_t, VnBrowserChromeSnapshot*, VnBrowserTabSnapshot*, int32_t,
    char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_media_browser_tab_action(const intptr_t*, int32_t, uint8_t,
    const char16_t*, int32_t, const char16_t*, int32_t, const char16_t*, int32_t,
    const char16_t*, int32_t, const char16_t*, int32_t, int32_t, uint8_t*) noexcept;

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
VN_EXPORT int32_t __stdcall vn_media_title_watch_set_source(int64_t handle, const char16_t* source_app, int32_t source_app_len, uint8_t* out_active) noexcept;
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
    double refraction;
    double z_radius;
    int32_t bevel_mode;
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

#ifndef VN_NAV_PILL_SPRING_DEFINED
#define VN_NAV_PILL_SPRING_DEFINED
#pragma pack(push, 1)
struct VnNavPillSpringState
{
    double top;
    double bottom;
    double top_velocity;
    double bottom_velocity;
    double width_scale;
    double width_velocity;
};

struct VnNavPillVisual
{
    double translate_y;
    double scale_x;
    double scale_y;
};

struct VnNavHoverSpringState
{
    double y, y_velocity;
    double opacity, opacity_velocity;
    double selected_opacity, selected_velocity;
};

struct VnMenuSurfaceMotionState
{
    double scale_x, scale_y, x, y, opacity;
    double scale_x_velocity, scale_y_velocity, x_velocity, y_velocity, opacity_velocity;
};
#pragma pack(pop)
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

VN_EXPORT int32_t __stdcall vn_damped_spring_build_segments(double damping_ratio, double* out_coefficients, int32_t coefficient_count) noexcept;

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

VN_EXPORT int32_t __stdcall vn_animation_step_nav_hover(
    VnNavHoverSpringState* state, double target_y, uint8_t visible, uint8_t selected,
    double elapsed_seconds, uint8_t reduced_motion, uint8_t* out_settled) noexcept;

VN_EXPORT int32_t __stdcall vn_animation_step_menu_highlight(
    VnNavPillSpringState*, double, double, double, uint8_t, VnNavPillVisual*, uint8_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_animation_step_menu_surface(
    VnMenuSurfaceMotionState*, double, uint8_t, uint8_t*) noexcept;

VN_EXPORT int32_t __stdcall vn_animation_step_nav_pill(
    VnNavPillSpringState* state,
    double target_y,
    double item_height,
    double elapsed_seconds,
    uint8_t reduced_motion,
    VnNavPillVisual* out_visual,
    uint8_t* out_settled) noexcept;

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
struct VnMarkdownSlice
{
    int32_t start;
    int32_t len;
};
#pragma pack(pop)
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

VN_EXPORT int32_t __stdcall vn_spotlight_launcher_reveal_in_explorer(
    const char16_t* path,
    int32_t path_len) noexcept;

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

VN_EXPORT int32_t __stdcall vn_clipboard_store_equals_icase(
    const char16_t* a,
    int32_t a_len,
    const char16_t* b,
    int32_t b_len,
    uint8_t* out_equals) noexcept;

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

extern "C" int32_t __stdcall vn_privacy_indicator_scan_consent_store(
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

VN_EXPORT int32_t __stdcall vn_lyrics_is_credit_header_line(const char16_t*, int32_t, int64_t, uint8_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_lyrics_decode_krc_payload(const uint8_t*, int32_t, uint8_t*, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_lyrics_strip_title_extras(
    const char16_t* title_ptr,
    int32_t title_len,
    uint8_t is_turkic_culture,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept;

extern "C" int32_t __stdcall vn_youtube_subtitle_normalize_cue_text(
    const char16_t* raw_text,
    int32_t raw_len,
    uint8_t strip_xml_tags,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept;

extern "C" int32_t __stdcall vn_youtube_subtitle_clamp_timestamp_ticks(
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

// ---------------------------------------------------------------------------
// AI Streaming, Markdown Extended & Settings Search Batch
// ---------------------------------------------------------------------------

#ifndef VN_MARKDOWN_BOUNDARY_SUMMARY_DEFINED
#define VN_MARKDOWN_BOUNDARY_SUMMARY_DEFINED
#pragma pack(push, 1)
struct VnMarkdownBoundarySummary
{
    uint8_t has_unclosed_fence;
    uint8_t fence_char;
    uint8_t fence_count;
    uint8_t has_unclosed_inline_code;
    uint8_t inline_code_ticks;
    uint8_t has_unclosed_bold;
    uint8_t bold_char;
    uint8_t has_unclosed_italic;
    uint8_t italic_char;
    uint8_t bold_outer;
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

VN_EXPORT int32_t __stdcall vn_markdown_scan_span_boundaries(
    const char16_t* text_ptr,
    int32_t text_len,
    VnMarkdownBoundarySummary* out_summary) noexcept;

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
// Saliency Subject Bounds, Single-Call Blur Pipelines & Working-Set Trimmer
// ---------------------------------------------------------------------------

#ifndef VN_SUBJECT_BOUNDS_DEFINED
#define VN_SUBJECT_BOUNDS_DEFINED
#pragma pack(push, 1)
struct VnSubjectBounds
{
    float center_x;
    float center_y;
    float width;
    float height;
    float confidence;
    int32_t class_id;
};
#pragma pack(pop)
#endif

VN_EXPORT int32_t __stdcall vn_smartcrop_compute_saliency_subject_bounds(
    const uint8_t* pixels, int32_t w, int32_t h, int32_t stride,
    VnSubjectBounds* out_bounds, uint8_t* out_found) noexcept;

VN_EXPORT int32_t __stdcall vn_fast_blur_process_bgra32(
    uint8_t* pixels, uint8_t* scratch, int32_t w, int32_t h,
    int32_t radius, int32_t passes, float darken_factor) noexcept;

VN_EXPORT int32_t __stdcall vn_subject_blur_process_bgra32(
    uint8_t* background, uint8_t* subject_layer, uint8_t* scratch,
    int32_t w, int32_t h, int32_t stride,
    float center_x, float center_y, float sub_w, float sub_h,
    int32_t bg_radius, int32_t sub_radius,
    float bg_darken, float sub_brighten) noexcept;

VN_EXPORT int32_t __stdcall vn_memory_trim_working_set() noexcept;

VN_EXPORT int32_t __stdcall vn_notchshell_compute_expanded_width(
    uint8_t is_no_widget_mode,
    uint8_t is_lyrics_active,
    uint8_t is_music_expanded,
    double* out_width) noexcept;

VN_EXPORT int32_t __stdcall vn_media_metadata_format_spotify_artists(
    const char16_t* raw_artist,
    int32_t raw_artist_len,
    const char16_t* primary_artist,
    int32_t primary_artist_len,
    const char16_t* cand_title,
    int32_t cand_title_len,
    const char16_t* exp_title,
    int32_t exp_title_len,
    int64_t cand_dur_ms,
    int64_t exp_dur_ms,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len,
    int32_t* out_score) noexcept;

VN_EXPORT int32_t __stdcall vn_spotify_read_local_track(
    uint8_t include_track_id,
    char16_t* out_title, int32_t title_cap, int32_t* out_title_len,
    char16_t* out_artists, int32_t artists_cap, int32_t* out_artists_len,
    char16_t* out_track_url, int32_t url_cap, int32_t* out_url_len) noexcept;

VN_EXPORT int32_t __stdcall vn_translation_runtime_is_allowed_uri(
    const char16_t* scheme_ptr, int32_t scheme_len,
    int32_t user_info_len,
    uint8_t is_default_port,
    const char16_t* host_ptr, int32_t host_len,
    uint8_t* out_allowed) noexcept;

#pragma pack(push, 1)
struct VnClockTickSegment
{
    double x1;
    double y1;
    double x2;
    double y2;
    uint8_t is_major;
};
#pragma pack(pop)
VN_EXPORT int32_t __stdcall vn_analog_clock_calculate_face_radius(
    double width, double height, double face_padding, double* out_radius) noexcept;
VN_EXPORT int32_t __stdcall vn_analog_clock_compute_hand_angles(
    int32_t hour, int32_t minute, int32_t second, int32_t millisecond,
    double* out_hour_angle, double* out_minute_angle, double* out_second_angle) noexcept;
VN_EXPORT int32_t __stdcall vn_analog_clock_compute_ticks(
    double center_x, double center_y, double radius,
    VnClockTickSegment* out_segments, int32_t segment_cap, int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_elastic_slider_compute_metrics(
    double value, double min_val, double max_val, double tick_freq,
    double* out_fraction, double* out_effective_step, int32_t* out_tick_count, double* out_snapped_value) noexcept;
VN_EXPORT int32_t __stdcall vn_elastic_slider_compute_indicator_layout(
    double value, double min_val, double max_val, double width, double track_edge_pad, double indicator_width,
    double* out_target_left, double* out_fill_width) noexcept;
VN_EXPORT int32_t __stdcall vn_elastic_slider_eval_mouse(
    double mouse_x, double width, double track_edge_pad, double min_val, double max_val, double tick_freq,
    double* out_drag_overflow, double* out_clamped_fraction, double* out_snapped_value, int32_t* out_overscroll_dir) noexcept;

#pragma pack(push, 1)
struct VnCpuRimOpacities
{
    double active_fresnel;
    double rim;
    double depth_rim;
    double fresnel_bloom;
    double fresnel;
    double inner_fresnel;
    double specular;
    double cool_rim;
    double warm_rim;
};

struct VnDynamicFresnelStep
{
    double fresnel_x;
    double fresnel_y;
    double fresnel_contrast;
    uint8_t tint_r;
    uint8_t tint_g;
    uint8_t tint_b;
    uint8_t bright_r;
    uint8_t bright_g;
    uint8_t bright_b;
    uint8_t mid_r;
    uint8_t mid_g;
    uint8_t mid_b;
    uint8_t inner_shadow_a;
    double origin_x;
    double origin_y;
    double radius_x;
    double radius_y;
    double bloom_opacity;
    double fresnel_opacity;
    double inner_fresnel_opacity;
};
#pragma pack(pop)
VN_EXPORT int32_t __stdcall vn_glass_binder_compute_cpu_rim_opacities(
    double edge_highlight, double specular, double fresnel, double chroma, VnCpuRimOpacities* out_opacities) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_binder_step_dynamic_fresnel(
    int64_t now_ticks, int64_t last_ticks, double active_fresnel_level,
    double light_x, double light_y, double contrast,
    uint8_t optic_r, uint8_t optic_g, uint8_t optic_b,
    double cur_x, double cur_y, double cur_contrast,
    uint8_t cur_tint_r, uint8_t cur_tint_g, uint8_t cur_tint_b,
    VnDynamicFresnelStep* out_step) noexcept;
VN_EXPORT int32_t __stdcall vn_glass_binder_read_system_transparency(uint8_t* out_enabled) noexcept;

VN_EXPORT int32_t __stdcall vn_highlighted_text_fill_mask(
    const wchar_t* text_ptr, int32_t text_len, const wchar_t* query_ptr, int32_t query_len,
    uint8_t* out_mask, int32_t mask_cap, uint8_t* out_matched) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_scroll_category_drop_speed(
    double x, double width, double* out_speed) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_scroll_category_overscroll(
    double excess, uint8_t reduce_motion, double* out_elastic_x) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_scroll_step_category_frame(
    double scroll_target, double scrollable_width, double current_offset, double current_elastic_x,
    double seconds, uint8_t reduce_motion,
    double* out_clamped_target, double* out_next_offset, double* out_next_elastic_x, uint8_t* out_stop) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_scroll_step_cards_frame(
    double scroll_target, double scrollable_width, double current_pos, double seconds, uint8_t reduce_motion,
    double* out_clamped_target, double* out_next_pos, uint8_t* out_stop) noexcept;

#pragma pack(push, 1)
struct VnNavTabTokenSlice
{
    int32_t offset;
    int32_t length;
};
#pragma pack(pop)
VN_EXPORT int32_t __stdcall vn_nav_tabs_calculate_target_slot_hysteresis(
    int32_t current_target, double visual_pos, double pitch, int32_t total_slots, int32_t* out_slot) noexcept;
VN_EXPORT int32_t __stdcall vn_nav_tabs_calculate_neighbor_offset(
    int32_t index, int32_t initial_slot, int32_t target_slot, double pitch, double* out_offset) noexcept;
VN_EXPORT int32_t __stdcall vn_nav_tabs_parse_unique_tokens(
    const wchar_t* raw_ptr, int32_t raw_len, VnNavTabTokenSlice* out_slices, int32_t slice_cap, int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_tray_icons_match_source(
    const wchar_t* app_ptr, int32_t app_len, const wchar_t* exe_ptr, int32_t exe_len,
    int32_t* out_icon_kind, uint8_t* out_matched) noexcept;
VN_EXPORT int32_t __stdcall vn_tray_icons_match_extension(
    const wchar_t* ext_ptr, int32_t ext_len, int32_t* out_icon_kind, uint8_t* out_matched) noexcept;
VN_EXPORT int32_t __stdcall vn_tray_icons_for_clipboard_kind(
    int32_t clipboard_kind, int32_t* out_icon_kind) noexcept;
VN_EXPORT int32_t __stdcall vn_tray_icons_compute_color_sector(
    int32_t index, int32_t count, double cx, double cy, double radius,
    double* out_x1, double* out_y1, double* out_x2, double* out_y2) noexcept;

VN_EXPORT int32_t __stdcall vn_translation_compute_character_fades(
    double elapsed, double interval, double fade_ms, int32_t brush_count, int32_t completed_index,
    double* out_opacities, int32_t opacity_cap, int32_t* out_start_index, int32_t* out_end_index, int32_t* out_new_completed) noexcept;
VN_EXPORT int32_t __stdcall vn_translation_compute_popup_limits(
    double work_height, double dpi_scale_y, double* out_max_height, double* out_panel_max_height) noexcept;
VN_EXPORT int32_t __stdcall vn_translation_compute_viewport_height(
    double panel_max_height, double chrome_height, double current_max_height, double* out_height, uint8_t* out_changed) noexcept;
VN_EXPORT int32_t __stdcall vn_translation_compute_drag_position(
    double origin_x, double origin_y, double start_x, double start_y, double screen_x, double screen_y,
    int32_t* out_x, int32_t* out_y) noexcept;

VN_EXPORT int32_t __stdcall vn_clipboard_category_eval_ghost(
    double point_x, double point_y, double grip_x, double scroll_width, double scroll_height, double source_height,
    uint8_t state_valid, double* out_ghost_left, double* out_ghost_top, double* out_ghost_opacity, uint8_t* out_reorder_valid) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_category_resolve_move(
    int32_t count, int32_t source_index, int32_t before_index, int32_t* out_destination, uint8_t* out_changed) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_category_compute_preview_shift(
    int32_t item_index, int32_t source_index, int32_t destination_index, double span, double* out_shift) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_category_compute_gap_x(
    int32_t source_index, int32_t before_index, double before_origin_x, double last_origin_x,
    double last_width, double last_margin_right, double span, double* out_gap_x) noexcept;

#pragma pack(push, 1)
struct VnAppNotificationBannerMetrics
{
    double target_width;
    double target_height;
    double target_corner_radius;
    int32_t morph_duration_ms;
    int32_t content_fade_ms;
    int32_t dismiss_duration_ms;
    uint8_t has_body;
    uint8_t is_multiline_body;
};

struct VnAppNotificationFilterResult
{
    uint8_t accepted;
    uint64_t content_hash;
    int32_t normalized_app_len;
    int32_t normalized_title_len;
    int32_t normalized_body_len;
};
#pragma pack(pop)

VN_EXPORT int32_t __stdcall vn_appnotify_reset_state() noexcept;
VN_EXPORT int32_t __stdcall vn_appnotify_seed_existing_id(uint32_t notification_id, int64_t creation_unix_ms) noexcept;
VN_EXPORT int32_t __stdcall vn_appnotify_evaluate_and_format(
    uint32_t notification_id,
    int64_t creation_unix_ms,
    int64_t now_unix_ms,
    const wchar_t* app_id_ptr, int32_t app_id_len,
    const wchar_t* app_name_ptr, int32_t app_name_len,
    const wchar_t* title_ptr, int32_t title_len,
    const wchar_t* body_ptr, int32_t body_len,
    wchar_t* out_app_buf, int32_t app_cap,
    wchar_t* out_title_buf, int32_t title_cap,
    wchar_t* out_body_buf, int32_t body_cap,
    VnAppNotificationFilterResult* out_result) noexcept;
VN_EXPORT int32_t __stdcall vn_appnotify_compute_banner_metrics(
    double collapsed_width,
    double collapsed_height,
    double expanded_width,
    double corner_radius_collapsed,
    double corner_radius_expanded,
    uint8_t is_dynamic_island,
    uint8_t reduce_motion,
    int32_t configured_duration_ms,
    int32_t title_len,
    int32_t body_len,
    uint8_t has_newline_in_body,
    VnAppNotificationBannerMetrics* out_metrics) noexcept;
VN_EXPORT int32_t __stdcall vn_appnotify_try_activate_app(
    const wchar_t* app_id_ptr, int32_t app_id_len,
    const wchar_t* app_name_ptr, int32_t app_name_len,
    uint8_t* out_activated) noexcept;
VN_EXPORT int32_t __stdcall vn_appnotify_resolve_app_icon_source(
    const wchar_t* app_id_ptr, int32_t app_id_len,
    const wchar_t* app_name_ptr, int32_t app_name_len,
    wchar_t* out_path_buf, int32_t path_cap,
    int32_t* out_path_len) noexcept;
VN_EXPORT int32_t __stdcall vn_appnotify_extract_initials(
    const wchar_t* title_ptr, int32_t title_len,
    const wchar_t* app_name_ptr, int32_t app_name_len,
    wchar_t* out_buf, int32_t buf_cap,
    int32_t* out_len) noexcept;
VN_EXPORT int32_t __stdcall vn_gpu_monitor_find_best_adapter(
    wchar_t* out_name,
    int32_t out_name_cap,
    int32_t* out_name_len,
    uint64_t* out_dedicated_vram) noexcept;

#pragma pack(push, 1)
struct VnAiQuotaWindowVisualOut
{
    double clamped_used_percent;
    double clamped_remaining_percent;
    double row_opacity;
    uint8_t is_expired;
    uint8_t is_stale;
    uint8_t severity_tier;
    uint8_t reset_mode;
    int32_t reset_hours;
    int32_t reset_minutes;
};

struct VnAiProviderCardSummaryOut
{
    uint8_t show_quota_rows;
    int32_t visible_window_count;
    uint8_t is_stale;
    uint8_t action_kind;
};

struct VnPerformancePresetState
{
    uint8_t auto_animation_fps;
    int32_t animation_fps;
    uint8_t enable_blur_effects;
    uint8_t enable_subject_blur;
    uint8_t enable_smart_crop;
    uint8_t show_media_art_background;
    uint8_t enable_shadow;
    uint8_t is_liquid_glass;
    uint8_t use_gpu_refraction;
    uint8_t use_ultimate_gpu;
    int32_t glass_target_fps;
    int32_t gpu_preference;
    int32_t process_priority;
};
static_assert(sizeof(VnPerformancePresetState) == 25);
#pragma pack(pop)

enum VnPerformancePresetId : int32_t
{
    VN_PERF_SELECTION_BACKUP = -2,
    VN_PERF_SELECTION_CUSTOM = -1,
    VN_PERF_PRESET_ECO = 0,
    VN_PERF_PRESET_EVERYDAY = 1,
    VN_PERF_PRESET_BALANCED = 2,
    VN_PERF_PRESET_HIGH_END = 3,
    VN_PERF_PRESET_COUNT = 4,
};

VN_EXPORT int32_t __stdcall vn_perf_preset_apply(
    int32_t preset_id,
    const VnPerformancePresetState* current_state,
    VnPerformancePresetState* out_state) noexcept;

VN_EXPORT int32_t __stdcall vn_perf_preset_match(
    const VnPerformancePresetState* state,
    int32_t* out_preset_id) noexcept;

VN_EXPORT int32_t __stdcall vn_perf_preset_decide_selection(
    const VnPerformancePresetState* current_state,
    uint8_t has_backup,
    uint8_t matches_backup_full,
    uint8_t matches_last_preset_full,
    int32_t preferred_selection,
    int32_t* out_active_item,
    uint8_t* out_should_backup_on_preset_select) noexcept;

VN_EXPORT int32_t __stdcall vn_perf_preset_recommend_hardware_tier(
    int32_t* out_preset_id) noexcept;

VN_EXPORT int32_t __stdcall vn_ai_usage_compute_window_visual(
    double used_percent,
    int32_t duration_minutes,
    uint8_t has_resets_at,
    int64_t resets_at_utc_ticks,
    uint8_t has_updated_at,
    int64_t updated_at_utc_ticks,
    int64_t now_utc_ticks,
    VnAiQuotaWindowVisualOut* out_visual) noexcept;

VN_EXPORT int32_t __stdcall vn_ai_usage_compute_card_summary(
    int32_t state,
    int32_t window_count,
    uint8_t has_updated_at,
    int64_t updated_at_utc_ticks,
    int64_t now_utc_ticks,
    VnAiProviderCardSummaryOut* out_summary) noexcept;

VN_EXPORT int32_t __stdcall vn_ai_usage_resolve_claude_setup_state(
    int32_t usage_state,
    uint8_t has_settings_file,
    uint8_t has_status_line,
    uint8_t matches_bridge_command,
    uint8_t has_custom_api_endpoint,
    uint8_t* out_action_kind,
    int32_t* out_prompt_mode) noexcept;

VN_EXPORT int32_t __stdcall vn_settings_migrator_append_csv_token_if_missing(
    const wchar_t* csv_ptr,
    int32_t csv_len,
    const wchar_t* token_ptr,
    int32_t token_len,
    wchar_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept;

VN_EXPORT int32_t __stdcall vn_text_build_font_spec(
    const wchar_t* fallbacks_ptr,
    int32_t fallbacks_len,
    wchar_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept;

VN_EXPORT int32_t __stdcall vn_text_snap_bundled_weight(
    int32_t requested_weight,
    uint8_t is_bundled_sf_pro,
    int32_t* out_weight) noexcept;

VN_EXPORT int32_t __stdcall vn_text_resolve_rendering_policy(
    int32_t element_role,
    uint8_t has_explicit_rendering_mode,
    int32_t current_rendering_mode,
    int32_t* out_formatting_mode,
    int32_t* out_rendering_mode,
    int32_t* out_hinting_mode,
    uint8_t* out_use_layout_rounding,
    uint8_t* out_snaps_to_device_pixels) noexcept;

#include "vnotch_ext_core.h"
#include "vnotch_ext_sub.h"
#include "vnotch_ext_ui.h"



