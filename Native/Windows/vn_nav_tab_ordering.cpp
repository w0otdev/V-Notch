#include "../vnotch_native.h"

static_assert(sizeof(VnNavTabTokenSlice) == 8, "VnNavTabTokenSlice must be 8 bytes packed");

namespace
{
    constexpr bool is_unicode_whitespace(wchar_t ch) noexcept
    {
        if (ch <= 0x0020)
        {
            return ch == L' ' || (ch >= 0x0009 && ch <= 0x000D);
        }
        return ch == 0x0085 ||
               ch == 0x00A0 ||
               ch == 0x1680 ||
               (ch >= 0x2000 && ch <= 0x200A) ||
               ch == 0x2028 ||
               ch == 0x2029 ||
               ch == 0x202F ||
               ch == 0x205F ||
               ch == 0x3000;
    }
}

VN_EXPORT int32_t __stdcall vn_nav_tabs_calculate_target_slot_hysteresis(
    int32_t current_target,
    double visual_pos,
    double pitch,
    int32_t total_slots,
    int32_t* out_slot) noexcept
{
    if (!out_slot || total_slots <= 0 || !(pitch > 0.0) || !std::isfinite(visual_pos) || !std::isfinite(pitch))
    {
        return VN_ERR_INVALID_ARG;
    }

    const double current_slot_center = static_cast<double>(current_target) * pitch;
    const double diff = visual_pos - current_slot_center;

    int32_t proposed_slot = current_target;
    if (diff > pitch * 0.55)
    {
        proposed_slot = static_cast<int32_t>(std::floor((visual_pos + pitch * 0.45) / pitch));
    }
    else if (diff < -pitch * 0.55)
    {
        proposed_slot = static_cast<int32_t>(std::ceil((visual_pos - pitch * 0.45) / pitch));
    }

    *out_slot = std::clamp(proposed_slot, 0, total_slots - 1);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_nav_tabs_calculate_neighbor_offset(
    int32_t index,
    int32_t initial_slot,
    int32_t target_slot,
    double pitch,
    double* out_offset) noexcept
{
    if (!out_offset || !std::isfinite(pitch))
    {
        return VN_ERR_INVALID_ARG;
    }

    if (target_slot < initial_slot && index >= target_slot && index < initial_slot)
    {
        *out_offset = pitch;
        return VN_OK;
    }

    if (target_slot > initial_slot && index > initial_slot && index <= target_slot)
    {
        *out_offset = -pitch;
        return VN_OK;
    }

    *out_offset = 0.0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_nav_tabs_parse_unique_tokens(
    const wchar_t* raw_ptr,
    int32_t raw_len,
    VnNavTabTokenSlice* out_slices,
    int32_t slice_cap,
    int32_t* out_written) noexcept
{
    if (!out_written)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_written = 0;

    if (raw_len < 0 || (raw_len > 0 && !raw_ptr) || slice_cap < 0 || (slice_cap > 0 && !out_slices))
    {
        return VN_ERR_INVALID_ARG;
    }

    if (raw_len == 0)
    {
        return VN_OK;
    }

    int32_t written = 0;
    int32_t i = 0;

    while (i < raw_len)
    {
        int32_t comma = i;
        while (comma < raw_len && raw_ptr[comma] != L',')
        {
            ++comma;
        }

        int32_t start = i;
        int32_t end = comma;
        i = comma + 1;

        while (start < end && is_unicode_whitespace(raw_ptr[start]))
        {
            ++start;
        }
        while (end > start && is_unicode_whitespace(raw_ptr[end - 1]))
        {
            --end;
        }

        const int32_t tok_len = end - start;
        if (tok_len <= 0)
        {
            continue;
        }

        bool exists = false;
        for (int32_t j = 0; j < written; ++j)
        {
            if (out_slices[j].length == tok_len &&
                CompareStringOrdinal(
                    raw_ptr + out_slices[j].offset,
                    tok_len,
                    raw_ptr + start,
                    tok_len,
                    TRUE) == CSTR_EQUAL)
            {
                exists = true;
                break;
            }
        }

        if (!exists)
        {
            if (written >= slice_cap)
            {
                return VN_ERR_BUFFER_TOO_SMALL;
            }
            out_slices[written++] = VnNavTabTokenSlice{ start, tok_len };
        }
    }

    *out_written = written;
    return VN_OK;
}
