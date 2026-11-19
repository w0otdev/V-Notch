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

#include "vnotch_abi.h"
#include "Services/Translation/LiteTranslationAbi.h"

// Flat parsing results borrow native-owned storage until their close function.
struct VnLyricWord { int64_t start, end; int32_t offset, length; };
struct VnLyricLine { int64_t start; int32_t offset, length, word_offset, word_count; };
static_assert(sizeof(VnLyricWord) == 24 && sizeof(VnLyricLine) == 24);

struct VnClipboardDirectoryRow { int64_t size, modified; int32_t offset, length; uint32_t attributes, reserved; };

#pragma pack(push, 1)
struct VnYoutubeJsonRow {
    int32_t offset[5]{}, length[5]{};
    int64_t ticks = 0;
    int32_t flags = 0;
};
#pragma pack(pop)
static_assert(sizeof(VnYoutubeJsonRow) == 52);

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

#pragma pack(push, 1)
struct VnPrivacyProcessEntry {
    uint32_t process_id, parent_process_id;
    char16_t executable_file[260];
};
#pragma pack(pop)
struct VnConsentStoreEntry;

#pragma pack(push, 1)
struct VnClipboardSearchSlice { int32_t offset, length; };
struct VnClipboardSearchEntry {
    VnClipboardSearchSlice title, preview, source, full_text;
    int32_t group_start, group_count, kind;
    uint8_t personal, archived, pinned, sensitive;
};
struct VnClipboardSearchLabel { VnClipboardSearchSlice id, name; };
#pragma pack(pop)

#pragma pack(push, 1)
struct VnBrowserTabSnapshot { int32_t url_offset, url_length; uint8_t spotify; };
struct VnBrowserChromeSnapshot {
    int32_t url_offset, url_length, address_offset, address_length, toolbar_count, tab_count;
    uint8_t spotify;
};
#pragma pack(pop)

namespace vnotch::services { struct VnDetection; namespace loopback { struct VnLoopbackFormat; } }

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

#pragma pack(push, 1)
// Caller-owned UTF-16 arena: offset/length are code units, without terminators.
struct VnLocalFileResult { int32_t offset, length, kind; double score; };
#pragma pack(pop)
static_assert(sizeof(VnLocalFileResult) == 20);

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

// ---------------------------------------------------------------------------
// Spotlight Search & Providers Extended Operations
// ---------------------------------------------------------------------------

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

#pragma pack(push, 1)
struct VnNavTabTokenSlice
{
    int32_t offset;
    int32_t length;
};
#pragma pack(pop)

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
