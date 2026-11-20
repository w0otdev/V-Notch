#pragma once
#include "vnotch_api_types.h"

VN_EXPORT int32_t vn_clipboard_index_create(int64_t*) noexcept;
VN_EXPORT int32_t vn_clipboard_index_close(int64_t) noexcept;
VN_EXPORT int32_t vn_clipboard_index_put(int64_t, uint64_t, uint64_t, int32_t, const char16_t*, int32_t, int64_t, uint8_t) noexcept;
VN_EXPORT int32_t vn_clipboard_index_find(int64_t, uint64_t, uint64_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_clipboard_index_remove(int64_t, uint64_t, uint64_t) noexcept;
VN_EXPORT int32_t vn_clipboard_index_duplicate(int64_t, const char16_t*, int32_t, uint8_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_clipboard_directory_scan(const char16_t*, int32_t, const char16_t*, int32_t, int32_t,
    int64_t*, const VnClipboardDirectoryRow**, int32_t*, const char16_t**, int32_t*) noexcept;
VN_EXPORT int32_t vn_clipboard_directory_scan_close(int64_t) noexcept;
VN_EXPORT int32_t vn_clipboard_stream_copy(intptr_t, intptr_t, int32_t (*)(intptr_t, uint8_t*, int32_t),
    int32_t (*)(intptr_t, const uint8_t*, int32_t), intptr_t, int64_t, int64_t*, int32_t*) noexcept;
// A mapped ciphertext file produces authenticated native plaintext; close zeroes it.
VN_EXPORT int32_t vn_clipboard_vault_load_image(intptr_t, const uint8_t*, int32_t, int64_t*, int32_t*) noexcept;
VN_EXPORT int32_t vn_clipboard_vault_read_image(int64_t, int32_t, uint8_t*, int32_t) noexcept;
VN_EXPORT int32_t vn_clipboard_vault_close_image(int64_t) noexcept;
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
VN_EXPORT int32_t __stdcall vn_clipboard_record_open(const wchar_t*, int32_t, int64_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_record_close(int64_t) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_record_migrate(int64_t, const wchar_t*, int32_t) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_record_read(int64_t, const wchar_t*, int32_t, uint8_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t __stdcall vn_clipboard_record_append(int64_t, const wchar_t*, int32_t, const uint8_t*, int32_t) noexcept;
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
