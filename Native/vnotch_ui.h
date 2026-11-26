#pragma once
#include "vnotch_api_types.h"

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
VN_EXPORT int32_t __stdcall vn_spotlight_search_compare_keys(
    double score_a, const char16_t* title_a_ptr, int32_t title_a_len,
    const char16_t* id_a_ptr, int32_t id_a_len,
    double score_b, const char16_t* title_b_ptr, int32_t title_b_len,
    const char16_t* id_b_ptr, int32_t id_b_len, int32_t* out_cmp) noexcept;

// Rows contain offsets into a caller-owned UTF-16 arena; calls retain no pointers.
struct VnAppRankRow {
    int32_t offset, raw_title, title, subtitle, stem, filename, bonus;
};
struct VnAppRankMatch { double score; int32_t index, reserved; };
static_assert(sizeof(VnAppRankRow) == 28 && sizeof(VnAppRankMatch) == 16);
VN_EXPORT int32_t vn_spotlight_prepare_app(
    const char16_t* display, int32_t display_len, const char16_t* raw, int32_t raw_len,
    const char16_t* subtitle, int32_t subtitle_len, const char16_t* target, int32_t target_len,
    char16_t* buffer, int32_t capacity, VnAppRankRow* row, int32_t* written) noexcept;
VN_EXPORT int32_t vn_spotlight_rank_apps(
    const VnAppRankRow* rows, int32_t count, const char16_t* arena, int32_t arena_len,
    const char16_t* query, int32_t query_len, VnAppRankMatch* matches, int32_t limit, int32_t* found) noexcept;
VN_EXPORT int32_t __stdcall vn_spotlight_launcher_reveal_in_explorer(
    const char16_t* path,
    int32_t path_len) noexcept;
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
VN_EXPORT int32_t __stdcall vn_markdown_reveal_prefix(
    const char16_t* text, int32_t length, int32_t words, int32_t* end) noexcept;
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
VN_EXPORT int32_t __stdcall vn_notchshell_compute_expanded_width(
    uint8_t is_no_widget_mode,
    uint8_t is_lyrics_active,
    uint8_t is_music_expanded,
    double* out_width) noexcept;
VN_EXPORT int32_t __stdcall vn_nav_tabs_calculate_target_slot_hysteresis(
    int32_t current_target, double visual_pos, double pitch, int32_t total_slots, int32_t* out_slot) noexcept;
VN_EXPORT int32_t __stdcall vn_nav_tabs_calculate_neighbor_offset(
    int32_t index, int32_t initial_slot, int32_t target_slot, double pitch, double* out_offset) noexcept;
VN_EXPORT int32_t __stdcall vn_nav_tabs_parse_unique_tokens(
    const wchar_t* raw_ptr, int32_t raw_len, VnNavTabTokenSlice* out_slices, int32_t slice_cap, int32_t* out_written) noexcept;
VN_EXPORT int32_t __stdcall vn_ai_quota_notifications_create(void** out_tracker) noexcept;
VN_EXPORT int32_t __stdcall vn_ai_quota_notifications_destroy(void* tracker) noexcept;
VN_EXPORT int32_t __stdcall vn_ai_quota_notifications_evaluate(
    void* tracker, int32_t provider, int32_t state,
    const wchar_t* account_ptr, int32_t account_len,
    const wchar_t* bucket_ptr, int32_t bucket_len,
    const wchar_t* label_ptr, int32_t label_len,
    int32_t duration_minutes, double used_percent,
    int64_t reset_utc_ticks, int64_t updated_utc_ticks, int64_t now_utc_ticks,
    int32_t* out_threshold) noexcept;
VN_EXPORT int32_t __stdcall vn_ai_usage_compute_window_visual(
    double used_percent,
    int32_t duration_minutes,
    uint8_t has_resets_at,
    int64_t resets_at_utc_ticks,
    uint8_t has_updated_at,
    int64_t updated_at_utc_ticks,
    int64_t now_utc_ticks,
    VnAiQuotaWindowVisualOut* out_visual) noexcept;
VN_EXPORT int32_t __stdcall vn_calendar_get_geometry(
    int32_t* total_days, double* cell_width, double* highlight_diameter) noexcept;
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
