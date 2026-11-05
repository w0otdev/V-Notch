#include "../vnotch_native.h"

static_assert(sizeof(VnClockTickSegment) == 33);

VN_EXPORT int32_t __stdcall vn_analog_clock_calculate_face_radius(
    double width,
    double height,
    double face_padding,
    double* out_radius) noexcept
{
    if (!out_radius)
        return VN_ERR_INVALID_ARG;

    const double min_dim = width < height ? width : height;
    const double candidate = min_dim / 2.0 - face_padding;
    *out_radius = candidate > 0.0 ? candidate : 0.0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_analog_clock_compute_hand_angles(
    int32_t hour,
    int32_t minute,
    int32_t second,
    int32_t millisecond,
    double* out_hour_angle,
    double* out_minute_angle,
    double* out_second_angle) noexcept
{
    if (!out_hour_angle || !out_minute_angle || !out_second_angle)
        return VN_ERR_INVALID_ARG;

    const double seconds = static_cast<double>(second) + static_cast<double>(millisecond) / 1000.0;
    const double minutes = static_cast<double>(minute) + seconds / 60.0;
    const double hours = static_cast<double>(hour % 12) + minutes / 60.0;

    *out_hour_angle = hours * 30.0;
    *out_minute_angle = minutes * 6.0;
    *out_second_angle = seconds * 6.0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_analog_clock_compute_ticks(
    double center_x,
    double center_y,
    double radius,
    VnClockTickSegment* out_segments,
    int32_t segment_cap,
    int32_t* out_written) noexcept
{
    if (out_written)
        *out_written = 0;

    if (!out_segments || !out_written || segment_cap < 0)
        return VN_ERR_INVALID_ARG;

    if (segment_cap < 60)
        return VN_ERR_BUFFER_TOO_SMALL;

    constexpr double kPi = 3.14159265358979323846;
    const double outer = radius * 0.97;

    for (int32_t i = 0; i < 60; ++i)
    {
        const bool is_five = (i % 5) == 0;
        const double angle = static_cast<double>(i) * 6.0 * kPi / 180.0;
        const double sin_val = std::sin(angle);
        const double cos_val = std::cos(angle);
        const double inner_len = is_five ? (radius * 0.86) : (radius * 0.91);

        out_segments[i] = VnClockTickSegment{
            center_x + sin_val * inner_len,
            center_y - cos_val * inner_len,
            center_x + sin_val * outer,
            center_y - cos_val * outer,
            static_cast<uint8_t>(is_five ? 1u : 0u)
        };
    }

    *out_written = 60;
    return VN_OK;
}
