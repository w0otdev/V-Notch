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


extern "C" {
VN_EXPORT int32_t __stdcall vn_gesture_evaluate_move(
    double start_x,
    double start_y,
    double current_x,
    double current_y,
    double elapsed_since_last_swipe_ms,
    double* out_delta_x,
    double* out_delta_y,
    int32_t* out_gesture_kind) noexcept
{
    if (!out_delta_x || !out_delta_y || !out_gesture_kind)
    {
        return VN_ERR_INVALID_ARG;
    }

    constexpr double kHorizontalSwipeThreshold = 40.0;
    constexpr double kVerticalSwipeThreshold = 35.0;
    constexpr double kDirectionLockRatio = 1.6;
    constexpr double kSwipeCooldownMs = 400.0;
    constexpr double kDeadZone = 4.0;

    double dx = current_x - start_x;
    double dy = current_y - start_y;
    *out_delta_x = dx;
    *out_delta_y = dy;
    *out_gesture_kind = 0;

    double abs_x = std::abs(dx);
    double abs_y = std::abs(dy);
    if (abs_x < kDeadZone && abs_y < kDeadZone)
    {
        return VN_OK;
    }
    if (elapsed_since_last_swipe_ms < kSwipeCooldownMs)
    {
        return VN_OK;
    }

    if (abs_x >= kHorizontalSwipeThreshold && abs_x > abs_y * kDirectionLockRatio)
    {
        *out_gesture_kind = (dx < 0.0) ? 1 : 2; // 1 = SwipeLeft, 2 = SwipeRight
        return VN_OK;
    }

    if (abs_y >= kVerticalSwipeThreshold && dy > 0.0 && abs_y > abs_x * kDirectionLockRatio)
    {
        *out_gesture_kind = 3; // 3 = SwipeDown
        return VN_OK;
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_gesture_evaluate_end(
    double start_x,
    double start_y,
    double end_x,
    double end_y,
    double elapsed_since_last_tap_ms,
    uint8_t* out_is_tap,
    uint8_t* out_is_double_tap) noexcept
{
    if (!out_is_tap || !out_is_double_tap)
    {
        return VN_ERR_INVALID_ARG;
    }

    constexpr double kDeadZone = 4.0;
    constexpr double kDoubleTapMaxMs = 350.0;

    double total_movement = std::abs(end_x - start_x) + std::abs(end_y - start_y);
    if (total_movement < kDeadZone * 2.0)
    {
        *out_is_tap = 1;
        *out_is_double_tap = (elapsed_since_last_tap_ms >= 0.0 && elapsed_since_last_tap_ms <= kDoubleTapMaxMs) ? 1 : 0;
    }
    else
    {
        *out_is_tap = 0;
        *out_is_double_tap = 0;
    }
    return VN_OK;
}


}
