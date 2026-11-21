#pragma once
#include "vnotch_api_types.h"

VN_EXPORT int32_t __stdcall vn_translation_lite_open(const wchar_t*, int32_t, int32_t, int64_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_translation_lite_translate(int64_t, const wchar_t*, int32_t, const wchar_t*, int32_t,
    VnTranslationCancelled, intptr_t, wchar_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_translation_lite_close(int64_t) noexcept;
VN_EXPORT int32_t __stdcall vn_translation_lite_release_idle(int64_t, int32_t, uint8_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_translation_lite_set_natural(int64_t, uint8_t) noexcept;
VN_EXPORT int32_t __stdcall vn_update_parse_atom(const uint8_t*, int32_t, char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_setup_show_message(const wchar_t*, int32_t, const wchar_t*, int32_t, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_catalog_select_utf8(const uint8_t*, int32_t, int32_t,
    const char16_t*, int32_t, const char16_t*, int32_t, const char16_t*, int32_t, int64_t, const uint8_t*, int32_t, int64_t,
    char16_t*, int32_t, int32_t*, char16_t*, int32_t, int32_t*, int64_t*, int32_t*, int32_t*) noexcept;
// Native selection results retain their UIA element/range until vn_selection_close.
VN_EXPORT int32_t vn_selection_read(intptr_t, const int32_t*, int32_t, intptr_t, int64_t, uint8_t, uint8_t, int32_t,
    char16_t*, int32_t, int32_t*, double*, uint8_t*, uint8_t*, uint8_t*, int64_t*) noexcept;
VN_EXPORT int32_t vn_selection_close(int64_t) noexcept;
VN_EXPORT int32_t vn_selection_same(int64_t, int64_t, uint8_t*) noexcept;
VN_EXPORT int32_t vn_selection_prepare_replace(int64_t, const char16_t*, int32_t, const char16_t*, int32_t, int32_t*, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_system_metrics_sample(VnSystemMetricsState*, VnSystemMetricsSnapshot*) noexcept;
VN_EXPORT int32_t __stdcall vn_gpu_monitor_sample(VnSystemMetricsSnapshot*) noexcept;
VN_EXPORT int32_t __stdcall vn_gpu_monitor_reset() noexcept;
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
VN_EXPORT int32_t __stdcall vn_registry_watch_create(HKEY, const wchar_t*, int32_t, HANDLE, int64_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_registry_watch_consume(int64_t, uint8_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_registry_watch_close(int64_t) noexcept;
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
VN_EXPORT int32_t __stdcall vn_local_file_search_copy_results(int64_t handle,
    VnLocalFileResult* results, int32_t capacity, char16_t* text, int32_t text_capacity,
    int32_t* count, int32_t* text_length) noexcept;
VN_EXPORT int32_t __stdcall vn_local_file_search_create(int64_t* handle) noexcept;
VN_EXPORT int32_t __stdcall vn_local_file_search_cancel(int64_t handle) noexcept;
VN_EXPORT int32_t __stdcall vn_local_file_search_close(int64_t handle) noexcept;
VN_EXPORT int32_t __stdcall vn_local_file_search_run(int64_t handle,
    const char16_t* query, int32_t query_length, const char16_t* roots, int32_t roots_length,
    const char16_t* hidden, int32_t hidden_length, int32_t limit, int32_t budget_ms, int32_t* count) noexcept;
VN_EXPORT int32_t __stdcall vn_local_file_search_get_result(int64_t handle, int32_t index,
    char16_t* path, int32_t capacity, int32_t* length, int32_t* kind, double* score) noexcept;
VN_EXPORT int32_t __stdcall vn_damped_spring_build_segments(double damping_ratio, double* out_coefficients, int32_t coefficient_count) noexcept;
VN_EXPORT int32_t __stdcall vn_ffmpeg_read_process_command_line(
    uint32_t process_id,
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
VN_EXPORT int32_t __stdcall vn_translation_runtime_is_allowed_uri(
    const char16_t* scheme_ptr, int32_t scheme_len,
    int32_t user_info_len,
    uint8_t is_default_port,
    const char16_t* host_ptr, int32_t host_len,
    uint8_t* out_allowed) noexcept;
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
VN_EXPORT int32_t __stdcall vn_highlighted_text_fill_mask(
    const wchar_t* text_ptr, int32_t text_len, const wchar_t* query_ptr, int32_t query_len,
    uint8_t* out_mask, int32_t mask_cap, uint8_t* out_matched) noexcept;
VN_EXPORT int32_t __stdcall vn_translation_compute_popup_limits(
    double work_height, double dpi_scale_y, double* out_max_height, double* out_panel_max_height) noexcept;
VN_EXPORT int32_t __stdcall vn_translation_compute_viewport_height(
    double panel_max_height, double chrome_height, double current_max_height, double* out_height, uint8_t* out_changed) noexcept;
VN_EXPORT int32_t __stdcall vn_translation_compute_drag_position(
    double origin_x, double origin_y, double start_x, double start_y, double screen_x, double screen_y,
    int32_t* out_x, int32_t* out_y) noexcept;
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
