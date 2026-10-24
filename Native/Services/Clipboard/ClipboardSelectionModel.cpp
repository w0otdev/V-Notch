#include "vnotch_common.h"
#include <algorithm>
#include <cstdint>

namespace vnotch::services::clipboard {

class ClipboardSelectionModel final {
public:
    ClipboardSelectionModel() = delete;

    [[nodiscard]] static constexpr void ClampIncludeRange(
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
    [[nodiscard]] static constexpr void ResolveSelectAction(
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

} // extern "C"
