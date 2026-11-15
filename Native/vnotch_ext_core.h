#pragma once

#include "vnotch_native.h"

#pragma pack(push, 1)

struct VnCoreDetection
{
    float x1;
    float y1;
    float x2;
    float y2;
    float confidence;
    int32_t class_id;
};
static_assert(sizeof(VnCoreDetection) == 24);

struct VnCoreRect
{
    int32_t x;
    int32_t y;
    int32_t width;
    int32_t height;
};
static_assert(sizeof(VnCoreRect) == 16);

struct VnCoreAnimClockState
{
    int64_t last_tick;
    double next_tick;
    double target_fps;
};
static_assert(sizeof(VnCoreAnimClockState) == 24);

struct VnCoreTextSlice
{
    int32_t offset;
    int32_t length;
};
static_assert(sizeof(VnCoreTextSlice) == 8);

struct VnCoreBtBatteryDevice
{
    int32_t percentage;
    uint8_t is_connected;
    uint8_t reserved;
    uint16_t id_len;
    uint16_t name_len;
    wchar_t id[260];
    wchar_t name[512];
};
static_assert(sizeof(VnCoreBtBatteryDevice) == 1554);

#pragma pack(pop)

// SmartThumbnailCropService
VN_EXPORT int32_t __stdcall vn_smartcrop_select_single_prominent_person(
    const VnCoreDetection* detections,
    int32_t count,
    VnCoreDetection* out_person,
    uint8_t* out_found) noexcept;

VN_EXPORT int32_t __stdcall vn_smartcrop_translate_crop_detections(
    const VnCoreDetection* src,
    int32_t src_count,
    int32_t crop_x,
    int32_t crop_y,
    int32_t crop_width,
    int32_t crop_height,
    VnCoreDetection* out_dst,
    int32_t dst_cap,
    int32_t* out_written) noexcept;

VN_EXPORT int32_t __stdcall vn_smartcrop_partition_hybrid_candidates(
    const VnCoreDetection* src,
    int32_t src_count,
    VnCoreDetection* out_candidates,
    int32_t cap,
    int32_t* out_person_count,
    int32_t* out_object_count,
    int32_t* out_kept_count,
    uint8_t* out_is_person_mode) noexcept;

VN_EXPORT int32_t __stdcall vn_smartcrop_select_portrait_face(
    const VnCoreRect* face_boxes,
    int32_t face_count,
    double scale,
    const VnCoreDetection* person,
    VnCoreDetection* out_best_face,
    uint8_t* out_found) noexcept;

// AnimationFrameClock
VN_EXPORT int32_t __stdcall vn_anim_clock_try_advance(
    VnCoreAnimClockState* io_state,
    int64_t now_tick,
    int64_t stopwatch_freq,
    double target_fps,
    double* out_seconds,
    uint8_t* out_advanced) noexcept;

// AppNotificationService.Icons
VN_EXPORT int32_t __stdcall vn_appnotify_compute_icon_crop_bounds(
    const uint8_t* bgra,
    int32_t width,
    int32_t height,
    int32_t stride,
    uint8_t alpha_threshold,
    VnCoreRect* out_crop,
    uint8_t* out_has_opaque,
    uint8_t* out_should_crop) noexcept;

// GlassGrainBrush


// LyricsProviderOrder
VN_EXPORT int32_t __stdcall vn_lyrics_provider_order_parse(
    const wchar_t* raw,
    int32_t raw_len,
    uint8_t is_null,
    uint8_t* out_indices,
    int32_t cap,
    int32_t* out_count) noexcept;

// SoundCloudMatching
VN_EXPORT int32_t __stdcall vn_soundcloud_extract_anchor_hrefs(
    const wchar_t* html,
    int32_t html_len,
    VnCoreTextSlice* out_slices,
    int32_t slice_cap,
    int32_t* out_total_count) noexcept;

// SpotifyCanvasProtocol
VN_EXPORT int32_t __stdcall vn_spotify_canvas_may_be_mp4_utf8(
    const uint8_t* utf8,
    int32_t len,
    uint8_t* out_maybe) noexcept;

VN_EXPORT int32_t __stdcall vn_spotify_canvas_is_trusted_uri_parts(
    const wchar_t* scheme,
    int32_t scheme_len,
    const wchar_t* host,
    int32_t host_len,
    const wchar_t* abs_path,
    int32_t abs_path_len,
    uint8_t* out_trusted) noexcept;

// SettingsService
VN_EXPORT int32_t __stdcall vn_settings_is_generated_backup_name(
    const wchar_t* name,
    int32_t len,
    uint8_t* out_valid) noexcept;

// FileIconProvider
VN_EXPORT int32_t __stdcall vn_file_icon_can_probe_local_path(
    const wchar_t* path,
    int32_t len,
    uint8_t* out_can_probe) noexcept;

// PerformanceDiagnosticService
VN_EXPORT int32_t __stdcall vn_perf_diag_sample_thread_and_handle_counts(
    int64_t now_ticks,
    int64_t* io_last_thread_ticks,
    int32_t* io_cached_thread_count,
    int64_t stopwatch_freq,
    int32_t* out_thread_count,
    int32_t* out_handle_count) noexcept;

VN_EXPORT int32_t __stdcall vn_perf_diag_sample_global_memory(
    uint64_t* out_ram_used,
    uint64_t* out_ram_total,
    uint64_t* out_ram_avail,
    double* out_ram_percent) noexcept;

// BluetoothBatteryReader
VN_EXPORT int32_t __stdcall vn_bluetooth_battery_parse_percentage(
    const uint8_t* buf,
    int32_t len,
    int32_t* out_percentage) noexcept;

VN_EXPORT int32_t __stdcall vn_bluetooth_battery_read_devices(
    VnCoreBtBatteryDevice* out_devices,
    int32_t cap,
    int32_t* out_total_count) noexcept;
