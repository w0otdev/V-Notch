#include "vnotch_ext_core.h"

#include <tlhelp32.h>
#include <devpropdef.h>
#include <cfgmgr32.h>
#include <cwchar>

#pragma comment(lib, "cfgmgr32.lib")

#include "Parsing/WindowsText.h"
using namespace vnotch::windows_text;

VN_EXPORT int32_t __stdcall vn_appnotify_compute_icon_crop_bounds(
    const uint8_t* bgra,
    int32_t width,
    int32_t height,
    int32_t stride,
    uint8_t alpha_threshold,
    VnCoreRect* out_crop,
    uint8_t* out_has_opaque,
    uint8_t* out_should_crop) noexcept
{
    if (!out_crop || !out_has_opaque || !out_should_crop)
        return VN_ERR_INVALID_ARG;

    *out_crop = {};
    *out_has_opaque = 0;
    *out_should_crop = 0;

    if (!bgra || width <= 0 || height <= 0 || stride < width * 4)
        return VN_ERR_INVALID_ARG;

    int32_t left = width;
    int32_t top = height;
    int32_t right = -1;
    int32_t bottom = -1;

    for (int32_t y = 0; y < height; ++y)
    {
        const uint8_t* row = bgra + static_cast<size_t>(y) * static_cast<size_t>(stride);
        for (int32_t x = 0; x < width; ++x)
        {
            if (row[x * 4 + 3] <= alpha_threshold)
                continue;
            if (x < left) left = x;
            if (y < top) top = y;
            if (x > right) right = x;
            if (y > bottom) bottom = y;
        }
    }

    if (right < left)
        return VN_OK;

    *out_has_opaque = 1;
    const int32_t max_span = std::max(right - left + 1, bottom - top + 1);
    const int32_t padding = std::max(1, static_cast<int32_t>(std::ceil(static_cast<double>(max_span) * 0.04)));

    left = std::max(0, left - padding);
    top = std::max(0, top - padding);
    right = std::min(width - 1, right + padding);
    bottom = std::min(height - 1, bottom + padding);

    out_crop->x = left;
    out_crop->y = top;
    out_crop->width = right - left + 1;
    out_crop->height = bottom - top + 1;
    *out_should_crop = (left == 0 && top == 0 && right == width - 1 && bottom == height - 1) ? 0 : 1;
    return VN_OK;
}



