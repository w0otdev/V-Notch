#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class ShelfViewportVirtualizer final
    {
    public:
        static int32_t vn_shelf_viewport_calculate_impl(int32_t item_count,
    int32_t row_count,
    double cell_width,
    double horizontal_offset,
    double viewport_width,
    int32_t overscan_columns,
    VnShelfViewportRange* out_range) noexcept
        {
    if (out_range == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (item_count <= 0 || row_count <= 0 || cell_width <= 0.0 || !std::isfinite(cell_width))
    {
        out_range->start_index = 0;
        out_range->end_index_exclusive = 0;
        return VN_OK;
    }

    int32_t column_count = (item_count + row_count - 1) / row_count;
    double safe_offset = std::isfinite(horizontal_offset) ? std::max(0.0, horizontal_offset) : 0.0;
    double safe_viewport = (std::isfinite(viewport_width) && viewport_width > 0.0)
        ? viewport_width
        : (cell_width * 4.0);
    int32_t overscan = std::max(0, overscan_columns);

    int32_t first_visible = static_cast<int32_t>(std::floor(safe_offset / cell_width));
    int32_t end_visible_excl = static_cast<int32_t>(std::ceil((safe_offset + safe_viewport) / cell_width));

    int32_t start_col = std::clamp(first_visible - overscan, 0, column_count - 1);
    int32_t end_col_excl = std::clamp(end_visible_excl + overscan, start_col + 1, column_count);

    out_range->start_index = start_col * row_count;
    out_range->end_index_exclusive = std::min(item_count, end_col_excl * row_count);
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_shelf_viewport_calculate(int32_t item_count,
    int32_t row_count,
    double cell_width,
    double horizontal_offset,
    double viewport_width,
    int32_t overscan_columns,
    VnShelfViewportRange* out_range) noexcept
{
    return vnotch::services::ShelfViewportVirtualizer::vn_shelf_viewport_calculate_impl(item_count, row_count, cell_width, horizontal_offset, viewport_width, overscan_columns, out_range);
}

