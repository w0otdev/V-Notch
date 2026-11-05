#include "../vnotch_native.h"

VN_EXPORT int32_t __stdcall vn_clipboard_category_eval_ghost(
    double point_x,
    double point_y,
    double grip_x,
    double scroll_width,
    double scroll_height,
    double source_height,
    uint8_t state_valid,
    double* out_ghost_left,
    double* out_ghost_top,
    double* out_ghost_opacity,
    uint8_t* out_reorder_valid) noexcept
{
    if (!out_ghost_left || !out_ghost_top || !out_ghost_opacity || !out_reorder_valid)
        return VN_ERR_INVALID_ARG;

    const bool valid = state_valid != 0 &&
        point_x >= 0.0 && point_x <= scroll_width &&
        point_y >= 0.0 && point_y <= scroll_height;

    *out_ghost_left = point_x - grip_x;
    *out_ghost_top = std::max(0.0, (scroll_height - source_height) / 2.0);
    *out_ghost_opacity = valid ? 1.0 : 0.5;
    *out_reorder_valid = valid ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_category_resolve_move(
    int32_t count,
    int32_t source_index,
    int32_t before_index,
    int32_t* out_destination,
    uint8_t* out_changed) noexcept
{
    if (!out_destination || !out_changed)
        return VN_ERR_INVALID_ARG;

    if (count <= 0 || source_index < 0 || source_index >= count ||
        before_index == source_index || before_index >= count)
    {
        *out_destination = source_index;
        *out_changed = 0u;
        return VN_OK;
    }

    int32_t destination = before_index < 0 ? count - 1 : before_index;
    if (before_index >= 0 && destination > source_index)
        --destination;

    *out_destination = destination;
    *out_changed = (source_index != destination) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_category_compute_preview_shift(
    int32_t item_index,
    int32_t source_index,
    int32_t destination_index,
    double span,
    double* out_shift) noexcept
{
    if (!out_shift)
        return VN_ERR_INVALID_ARG;

    double shift = 0.0;
    if (source_index < destination_index && item_index > source_index && item_index <= destination_index)
        shift = -span;
    else if (source_index > destination_index && item_index >= destination_index && item_index < source_index)
        shift = span;

    *out_shift = shift;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_category_compute_gap_x(
    int32_t source_index,
    int32_t before_index,
    double before_origin_x,
    double last_origin_x,
    double last_width,
    double last_margin_right,
    double span,
    double* out_gap_x) noexcept
{
    if (!out_gap_x)
        return VN_ERR_INVALID_ARG;

    if (before_index >= 0)
        *out_gap_x = before_origin_x - (before_index > source_index ? span : 0.0);
    else
        *out_gap_x = last_origin_x + last_width + last_margin_right - span;

    return VN_OK;
}
