#pragma once
#include "vnotch_api_types.h"

VN_EXPORT int32_t vn_lyrics_parse(const uint8_t*, int32_t, int32_t, int32_t,
    int64_t*, const VnLyricLine**, int32_t*, const VnLyricWord**, const char16_t**) noexcept;
VN_EXPORT int32_t vn_lyrics_parsed_close(int64_t) noexcept;
VN_EXPORT int32_t vn_lyrics_parse_enhanced(const char16_t*, int32_t, int64_t,
    char16_t*, int32_t, int32_t*, VnLyricWord*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_youtube_extract_api_key_utf8(const uint8_t*, int32_t, char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_wtscanner_media_title(const char16_t*, int32_t, uint32_t, uint8_t, char16_t*, int32_t,
    int32_t*, int32_t*, int32_t*, int32_t*) noexcept;
VN_EXPORT int32_t vn_wtscanner_has_youtube_title(const char16_t*, int32_t, uint32_t, uint8_t*) noexcept;
// All returned pointers borrow the parsed response handle's lifetime.
VN_EXPORT int32_t vn_youtube_parse_utf8(const uint8_t*, int32_t, int32_t, const char16_t*, int32_t,
    int64_t*, const VnYoutubeJsonRow**, int32_t*, const char16_t**, uint8_t*) noexcept;
VN_EXPORT int32_t vn_youtube_parsed_close(int64_t) noexcept;
VN_EXPORT int32_t __stdcall vn_audio_get_sessions_batch(VnAudioSessionState*, int32_t, char16_t*, int32_t, int32_t*, int32_t*, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_audio_resolve_session_metadata(uint32_t, const char16_t*, int32_t, VnAudioSessionState*, char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_audio_enumerate_devices_batch(int32_t, VnAudioDeviceState*, int32_t, char16_t*, int32_t, int32_t*, int32_t*, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_wtscanner_scan_browser(intptr_t, uint8_t, VnBrowserChromeSnapshot*, VnBrowserTabSnapshot*, int32_t,
    char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_media_browser_tab_action(const intptr_t*, int32_t, uint8_t,
    const char16_t*, int32_t, const char16_t*, int32_t, const char16_t*, int32_t,
    const char16_t*, int32_t, const char16_t*, int32_t, int32_t, uint8_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_smartcrop_preprocess_bgra32(const uint8_t*, int32_t, int32_t, int32_t, int32_t, float*, int32_t, float*) noexcept;
VN_EXPORT int32_t __stdcall vn_smartcrop_session_create(const wchar_t*, int32_t, const wchar_t*, int32_t, uint8_t, int64_t*, uint8_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_smartcrop_session_destroy(int64_t) noexcept;
VN_EXPORT int32_t __stdcall vn_smartcrop_session_infer(int64_t, const uint8_t*, int32_t, int32_t, int32_t, int32_t,
    vnotch::services::VnDetection*, int32_t, int32_t*, uint8_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_media_title_watch_create(HANDLE, int64_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_media_title_watch_close(int64_t) noexcept;
VN_EXPORT int32_t __stdcall vn_media_title_watch_set_source(int64_t handle, const char16_t* source_app, int32_t source_app_len, uint8_t* out_active) noexcept;
VN_EXPORT int32_t __stdcall vn_loopback_start_levels(const char16_t*, int32_t,
    void(__stdcall*)(int64_t, const float*, float), void(__stdcall*)(int64_t, int32_t), int64_t,
    int64_t*, vnotch::services::loopback::VnLoopbackFormat*, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_audio_sessions_open_all(int64_t* snapshot, int32_t* count, int32_t* hresult) noexcept;
VN_EXPORT int32_t __stdcall vn_audio_volume_get_state(int64_t volume, int32_t* state) noexcept;
VN_EXPORT int32_t __stdcall vn_media_session_volume_get_process_ids(
    const char16_t* source, int32_t source_length, uint32_t* process_ids, int32_t capacity, int32_t* count) noexcept;
VN_EXPORT int32_t __stdcall vn_media_session_volume_compute_score(
    uint8_t process_match, uint8_t metadata_match, uint8_t same_source, uint32_t pid, uint32_t last_pid,
    uint8_t same_session, int32_t state, double* score) noexcept;
VN_EXPORT int32_t __stdcall vn_lyrics_normalize_for_matching(
    const char16_t* text_ptr,
    int32_t text_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept;
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
VN_EXPORT int32_t __stdcall vn_smartcrop_compute_saliency_subject_bounds(
    const uint8_t* pixels, int32_t w, int32_t h, int32_t stride,
    VnSubjectBounds* out_bounds, uint8_t* out_found) noexcept;
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
