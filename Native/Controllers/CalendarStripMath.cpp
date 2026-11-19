#include "vnotch_common.h"
#include "vnotch_cpu.h"
#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <immintrin.h>
#include <limits>
#include <string_view>


namespace {
constexpr int32_t kTotalDays = 11;
constexpr double kCellWidth = 30.0;
constexpr double kHighlightDiameter = 24.0;
}

extern "C" {
VN_EXPORT int32_t __stdcall vn_calendar_get_geometry(
    int32_t* total_days, double* cell_width, double* highlight_diameter) noexcept
{
    if (!total_days || !cell_width || !highlight_diameter) return VN_ERR_INVALID_ARG;
    *total_days = kTotalDays;
    *cell_width = kCellWidth;
    *highlight_diameter = kHighlightDiameter;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_calendar_clamp_index(
    int32_t idx,
    int32_t* out_clamped) noexcept
{
    if (!out_clamped) return VN_ERR_INVALID_ARG;
    *out_clamped = std::clamp(idx, 0, kTotalDays - 1);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_calendar_center_index_from_strip_x(
    double strip_x,
    int32_t* out_center_idx) noexcept
{
    if (!out_center_idx) return VN_ERR_INVALID_ARG;
    if (!std::isfinite(strip_x)) strip_x = 0.0;
    double raw = std::clamp(std::nearbyint((kCellWidth - strip_x) / kCellWidth), 0.0, static_cast<double>(kTotalDays - 1));
    *out_center_idx = static_cast<int32_t>(raw);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_calendar_highlight_x_for_index(
    int32_t center_idx,
    double* out_highlight_x) noexcept
{
    if (!out_highlight_x) return VN_ERR_INVALID_ARG;
    *out_highlight_x = static_cast<double>(center_idx) * kCellWidth + (kCellWidth - kHighlightDiameter) / 2.0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_calendar_strip_x_for_index(
    int32_t center_idx,
    double* out_strip_x) noexcept
{
    if (!out_strip_x) return VN_ERR_INVALID_ARG;
    *out_strip_x = kCellWidth - (static_cast<double>(center_idx) * kCellWidth);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_calendar_compute_scroll_step(
    double accumulator,
    int32_t delta,
    int32_t current_center_idx,
    double* out_remaining,
    int32_t* out_new_idx,
    int32_t* out_old_idx,
    int32_t* out_step_count) noexcept
{
    if (!out_remaining || !out_new_idx || !out_old_idx || !out_step_count)
    {
        return VN_ERR_INVALID_ARG;
    }

    if (!std::isfinite(accumulator)) accumulator = 0.0;
    double acc = accumulator + static_cast<double>(delta);
    int32_t direction = acc > 0.0 ? -1 : 1;
    double abs_acc = std::abs(acc);
    int32_t step_count = static_cast<int32_t>(std::clamp(abs_acc / 120.0, 0.0, 1000.0));
    if (step_count == 0 && abs_acc >= 72.0)
    {
        step_count = 1;
    }

    if (step_count == 0)
    {
        *out_remaining = acc;
        *out_new_idx = current_center_idx;
        *out_old_idx = current_center_idx;
        *out_step_count = 0;
        return VN_OK;
    }

    double sign = acc > 0.0 ? 1.0 : (acc < 0.0 ? -1.0 : 0.0);
    *out_remaining = acc - sign * static_cast<double>(step_count) * 120.0;
    *out_old_idx = current_center_idx;
    *out_new_idx = std::clamp(current_center_idx + (direction * step_count), 0, kTotalDays - 1);
    *out_step_count = step_count;
    return VN_OK;
}


}
