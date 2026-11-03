#include "vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <cstdint>

namespace vnotch::services::clipboard {

class ClipboardSelectionModel final {
public:
    ClipboardSelectionModel() = delete;

    static constexpr void ClampIncludeRange(
        int32_t item_count,
        int32_t first,
        int32_t last,
        int32_t& out_start,
        int32_t& out_end) noexcept {
        out_start = std::max(0, first);
        out_end = std::min(item_count - 1, last);
    }

    // Actions:
    // 0 = NoOp
    // 1 = IncludeRange [out_start, out_end]
    // 2 = ToggleSingle
    // 3 = ReplaceSingle
    static constexpr void ResolveSelectAction(
        bool shift,
        bool has_anchor,
        int32_t anchor_index,
        int32_t item_index,
        bool control,
        bool already_in_targets,
        int32_t& out_action,
        int32_t& out_start,
        int32_t& out_end) noexcept {
        out_action = 0;
        out_start = 0;
        out_end = -1;

        if (shift && has_anchor) {
            if (anchor_index >= 0 && item_index >= 0) {
                out_action = 1;
                out_start = std::min(anchor_index, item_index);
                out_end = std::max(anchor_index, item_index);
            }
            return;
        }
        if (control) {
            out_action = 2;
            return;
        }
        if (!already_in_targets) {
            out_action = 3;
        }
    }

    [[nodiscard]] static double ComputeScrollSpeed(double x, double width) noexcept {
        if (width <= 0.0) return 0.0;
        const double edge = std::min(48.0, width * 0.5);
        const double right_strength = (x > width - edge)
            ? std::clamp((x - width + edge) / edge, 0.0, 1.0)
            : 0.0;
        const double strength = (x < edge)
            ? -std::clamp((edge - x) / edge, 0.0, 1.0)
            : right_strength;
        return 720.0 * strength * std::abs(strength);
    }

    static void ComputeLassoRange(
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
        bool swept,
        int32_t& out_first,
        int32_t& out_last,
        bool& out_hits_row,
        bool& out_expand_vertical) noexcept {
        const double pitch = container_width + margin_left + margin_right;
        if (pitch <= 0.0 || card_count <= 0) {
            out_first = 0;
            out_last = -1;
            out_hits_row = false;
            out_expand_vertical = false;
            return;
        }
        const double first_x = origin_x + scroll_offset - static_cast<double>(ref_index) * pitch;
        out_first = std::max(0, static_cast<int32_t>(std::ceil((rect_left - first_x - container_width) / pitch)));
        out_last = std::min(card_count - 1, static_cast<int32_t>(std::floor((rect_right - first_x) / pitch)));
        out_hits_row = swept && rect_bottom >= row_top && rect_top <= row_bottom;
        out_expand_vertical = out_hits_row && (rect_bottom < origin_y || rect_top > origin_y + container_height);
    }

    static void FilterOrderedIndices(
        const int32_t* selected_indices,
        int32_t selected_count,
        int32_t card_count,
        uint8_t* scratch_mask,
        int32_t* out_ordered_indices,
        int32_t& out_count) noexcept {
        out_count = 0;
        if (selected_count <= 0 || card_count <= 0) return;
        for (int32_t i = 0; i < card_count; ++i) {
            scratch_mask[i] = 0;
        }
        for (int32_t i = 0; i < selected_count; ++i) {
            const int32_t idx = selected_indices[i];
            if (idx >= 0 && idx < card_count) {
                scratch_mask[idx] = 1;
            }
        }
        int32_t write = 0;
        for (int32_t i = 0; i < card_count; ++i) {
            if (scratch_mask[i] != 0) {
                out_ordered_indices[write++] = i;
            }
        }
        out_count = write;
    }

    static void MergeOptimisticOrder(
        int32_t desired_count,
        const int32_t* optimistic_to_desired_index,
        int32_t optimistic_count,
        uint8_t* scratch_taken_mask,
        int32_t* out_merged_desired_indices,
        int32_t& out_merged_count) noexcept {
        out_merged_count = 0;
        if (desired_count <= 0) return;
        for (int32_t i = 0; i < desired_count; ++i) {
            scratch_taken_mask[i] = 0;
        }
        int32_t write = 0;
        for (int32_t i = 0; i < optimistic_count; ++i) {
            const int32_t idx = optimistic_to_desired_index[i];
            if (idx >= 0 && idx < desired_count && scratch_taken_mask[idx] == 0) {
                scratch_taken_mask[idx] = 1;
                out_merged_desired_indices[write++] = idx;
            }
        }
        for (int32_t i = 0; i < desired_count; ++i) {
            if (scratch_taken_mask[i] == 0) {
                out_merged_desired_indices[write++] = i;
            }
        }
        out_merged_count = write;
    }
};

} // namespace vnotch::services::clipboard

extern "C" {

VN_EXPORT int32_t vn_clipboard_selection_clamp_range(
    int32_t item_count,
    int32_t first,
    int32_t last,
    int32_t* out_start,
    int32_t* out_end) noexcept {
    if (!out_start || !out_end) return VN_E_INVALID_ARG;
    int32_t s = 0, e = -1;
    vnotch::services::clipboard::ClipboardSelectionModel::ClampIncludeRange(item_count, first, last, s, e);
    *out_start = s;
    *out_end = e;
    return VN_OK;
}

VN_EXPORT int32_t vn_clipboard_selection_resolve_action(
    uint8_t shift,
    uint8_t has_anchor,
    int32_t anchor_index,
    int32_t item_index,
    uint8_t control,
    uint8_t already_in_targets,
    int32_t* out_action,
    int32_t* out_start,
    int32_t* out_end) noexcept {
    if (!out_action || !out_start || !out_end) return VN_E_INVALID_ARG;
    int32_t act = 0, s = 0, e = -1;
    vnotch::services::clipboard::ClipboardSelectionModel::ResolveSelectAction(
        shift != 0, has_anchor != 0, anchor_index, item_index, control != 0, already_in_targets != 0, act, s, e);
    *out_action = act;
    *out_start = s;
    *out_end = e;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_selection_scroll_speed(
    double x,
    double width,
    double* out_speed) noexcept {
    if (!out_speed) return VN_E_INVALID_ARG;
    *out_speed = vnotch::services::clipboard::ClipboardSelectionModel::ComputeScrollSpeed(x, width);
    return VN_OK;
}

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
    uint8_t* out_expand_vertical) noexcept {
    if (!out_first || !out_last || !out_hits_row || !out_expand_vertical) return VN_E_INVALID_ARG;
    int32_t f = 0, l = -1;
    bool hits = false, expand = false;
    vnotch::services::clipboard::ClipboardSelectionModel::ComputeLassoRange(
        card_count, ref_index, container_width, margin_left, margin_right,
        origin_x, origin_y, container_height, scroll_offset,
        rect_left, rect_top, rect_right, rect_bottom,
        row_top, row_bottom, swept != 0,
        f, l, hits, expand);
    *out_first = f;
    *out_last = l;
    *out_hits_row = hits ? 1 : 0;
    *out_expand_vertical = expand ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_selection_filter_ordered_indices(
    const int32_t* selected_indices,
    int32_t selected_count,
    int32_t card_count,
    uint8_t* scratch_mask,
    int32_t* out_ordered_indices,
    int32_t* out_count) noexcept {
    if (!out_count || selected_count < 0 || card_count < 0 ||
        (selected_count > 0 && !selected_indices) ||
        (card_count > 0 && (!scratch_mask || !out_ordered_indices))) {
        return VN_E_INVALID_ARG;
    }
    vnotch::services::clipboard::ClipboardSelectionModel::FilterOrderedIndices(
        selected_indices, selected_count, card_count, scratch_mask, out_ordered_indices, *out_count);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_selection_merge_optimistic_order(
    int32_t desired_count,
    const int32_t* optimistic_to_desired_index,
    int32_t optimistic_count,
    uint8_t* scratch_taken_mask,
    int32_t* out_merged_desired_indices,
    int32_t* out_merged_count) noexcept {
    if (!out_merged_count || desired_count < 0 || optimistic_count < 0 ||
        (optimistic_count > 0 && !optimistic_to_desired_index) ||
        (desired_count > 0 && (!scratch_taken_mask || !out_merged_desired_indices))) {
        return VN_E_INVALID_ARG;
    }
    vnotch::services::clipboard::ClipboardSelectionModel::MergeOptimisticOrder(
        desired_count, optimistic_to_desired_index, optimistic_count,
        scratch_taken_mask, out_merged_desired_indices, *out_merged_count);
    return VN_OK;
}

} // extern "C"
