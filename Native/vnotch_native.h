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

struct VnShelfViewportRange
{
    int32_t start_index;
    int32_t end_index_exclusive;
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

#pragma pack(pop)
