#include "../vnotch_common.h"
#include <algorithm>
#include <cstdint>
#include <cstring>

namespace vnotch::controllers
{

class GlassUploadDelta final
{
public:
    static int32_t CopyChangedRows(
        const uint8_t* source,
        int32_t source_stride,
        uint8_t* destination,
        int32_t destination_stride,
        int32_t row_bytes,
        int32_t height,
        int32_t* out_top,
        int32_t* out_bottom) noexcept
    {
        if (source == nullptr || destination == nullptr || out_top == nullptr || out_bottom == nullptr ||
            row_bytes < 0 || height < 0 || source_stride < row_bytes || destination_stride < row_bytes)
        {
            return VN_ERR_INVALID_ARG;
        }

        const size_t n = static_cast<size_t>(row_bytes);
        if (n == 0 || height == 0)
        {
            *out_top = 0;
            *out_bottom = 0;
            return VN_OK;
        }

        int32_t top = -1;
        for (int32_t y = 0; y < height; ++y)
        {
            const uint8_t* src_row = source + static_cast<int64_t>(y) * source_stride;
            const uint8_t* dst_row = destination + static_cast<int64_t>(y) * destination_stride;
            if (std::memcmp(src_row, dst_row, n) != 0)
            {
                top = y;
                break;
            }
        }

        if (top < 0)
        {
            *out_top = 0;
            *out_bottom = 0;
            return VN_OK;
        }

        int32_t bottom = top + 1;
        for (int32_t y = height - 1; y > top; --y)
        {
            const uint8_t* src_row = source + static_cast<int64_t>(y) * source_stride;
            const uint8_t* dst_row = destination + static_cast<int64_t>(y) * destination_stride;
            if (std::memcmp(src_row, dst_row, n) != 0)
            {
                bottom = y + 1;
                break;
            }
        }

        const int32_t span_rows = bottom - top;
        if (source_stride == destination_stride && source_stride == row_bytes)
        {
            std::memcpy(
                destination + static_cast<int64_t>(top) * destination_stride,
                source + static_cast<int64_t>(top) * source_stride,
                static_cast<size_t>(span_rows) * n);
        }
        else
        {
            for (int32_t y = top; y < bottom; ++y)
            {
                std::memcpy(
                    destination + static_cast<int64_t>(y) * destination_stride,
                    source + static_cast<int64_t>(y) * source_stride,
                    n);
            }
        }

        *out_top = top;
        *out_bottom = bottom;
        return VN_OK;
    }
};

} // namespace vnotch::controllers

extern "C"
{

VN_EXPORT int32_t vn_glass_upload_delta_copy_changed_rows(
    const uint8_t* source,
    int32_t source_stride,
    uint8_t* destination,
    int32_t destination_stride,
    int32_t row_bytes,
    int32_t height,
    int32_t* out_top,
    int32_t* out_bottom) noexcept
{
    return vnotch::controllers::GlassUploadDelta::CopyChangedRows(
        source, source_stride, destination, destination_stride, row_bytes, height, out_top, out_bottom);
}

} // extern "C"
