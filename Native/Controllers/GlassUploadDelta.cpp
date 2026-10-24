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
            row_bytes < 0 || height < 0)
        {
            return VN_ERR_INVALID_ARG;
        }

        int32_t top = height;
        int32_t bottom = 0;
        size_t n = static_cast<size_t>(row_bytes);

        for (int32_t y = 0; y < height; ++y)
        {
            const uint8_t* src_row = source + static_cast<int64_t>(y) * source_stride;
            uint8_t* dst_row = destination + static_cast<int64_t>(y) * destination_stride;
            if (std::memcmp(src_row, dst_row, n) == 0)
            {
                continue;
            }
            std::memcpy(dst_row, src_row, n);
            top = std::min(top, y);
            bottom = y + 1;
        }

        if (bottom == 0)
        {
            *out_top = 0;
            *out_bottom = 0;
        }
        else
        {
            *out_top = top;
            *out_bottom = bottom;
        }
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
