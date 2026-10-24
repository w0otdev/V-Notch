#include "../vnotch_common.h"
#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <string_view>

namespace vnotch::controllers
{

namespace
{

constexpr wchar_t ToLowerAscii(wchar_t c) noexcept
{
    return (c >= L'A' && c <= L'Z') ? static_cast<wchar_t>(c + (L'a' - L'A')) : c;
}

constexpr bool IsAsciiWhitespace(wchar_t c) noexcept
{
    return c == L' ' || c == L'\t' || c == L'\r' || c == L'\n' || c == L'\v' || c == L'\f';
}

std::wstring_view TrimWhitespace(std::wstring_view sv) noexcept
{
    size_t start = 0;
    while (start < sv.size() && IsAsciiWhitespace(sv[start]))
    {
        ++start;
    }
    size_t end = sv.size();
    while (end > start && IsAsciiWhitespace(sv[end - 1]))
    {
        --end;
    }
    return sv.substr(start, end - start);
}

bool EqualsIgnoreCaseAscii(std::wstring_view a, std::wstring_view b) noexcept
{
    if (a.size() != b.size())
    {
        return false;
    }
    for (size_t i = 0; i < a.size(); ++i)
    {
        if (ToLowerAscii(a[i]) != ToLowerAscii(b[i]))
        {
            return false;
        }
    }
    return true;
}

bool TryParseInt32Strict(std::wstring_view sv, int32_t* out_val) noexcept
{
    sv = TrimWhitespace(sv);
    if (sv.empty() || sv.size() > 16)
    {
        return false;
    }
    char buf[20]{};
    for (size_t i = 0; i < sv.size(); ++i)
    {
        if (sv[i] > 0x7F)
        {
            return false;
        }
        buf[i] = static_cast<char>(sv[i]);
    }
    int32_t v = 0;
    auto [ptr, ec] = std::from_chars(buf, buf + sv.size(), v);
    if (ec != std::errc{} || ptr != buf + sv.size())
    {
        return false;
    }
    *out_val = v;
    return true;
}

bool TryParseDoubleStrict(std::wstring_view sv, double* out_val) noexcept
{
    sv = TrimWhitespace(sv);
    if (sv.empty() || sv.size() > 63)
    {
        return false;
    }
    char buf[64]{};
    for (size_t i = 0; i < sv.size(); ++i)
    {
        if (sv[i] > 0x7F)
        {
            return false;
        }
        buf[i] = static_cast<char>(sv[i]);
    }
    double v = 0.0;
    auto [ptr, ec] = std::from_chars(buf, buf + sv.size(), v);
    if (ec != std::errc{} || ptr != buf + sv.size())
    {
        return false;
    }
    *out_val = v;
    return true;
}

bool TryTicksFromSeconds(double total_seconds, int64_t* out_ticks) noexcept
{
    *out_ticks = 0;
    constexpr double kMaxMillis = static_cast<double>(std::numeric_limits<int64_t>::max() / 10000LL);
    if (!std::isfinite(total_seconds) || total_seconds <= 0.0)
    {
        return false;
    }
    double millis = total_seconds * 1000.0;
    if (!std::isfinite(millis) || millis >= kMaxMillis || millis <= 0.0)
    {
        return false;
    }
    double ticks_d = millis * 10000.0;
    if (!std::isfinite(ticks_d) || ticks_d < 1.0 || ticks_d >= static_cast<double>(std::numeric_limits<int64_t>::max()))
    {
        return false;
    }
    int64_t ticks = static_cast<int64_t>(std::llround(ticks_d));
    if (ticks <= 0)
    {
        return false;
    }
    *out_ticks = ticks;
    return true;
}

constexpr uint64_t Avalanche64(uint64_t x) noexcept
{
    x ^= x >> 33;
    x *= 0xff51afd7ed558ccdULL;
    x ^= x >> 33;
    x *= 0xc4ceb9fe1a85ec53ULL;
    x ^= x >> 33;
    return x;
}

} // namespace

} // namespace vnotch::controllers

extern "C"
{

VN_EXPORT int32_t __stdcall vn_glass_frame_hash_128(
    const uint8_t* data,
    int32_t length,
    uint64_t* out_lo,
    uint64_t* out_hi) noexcept
{
    if (!out_lo || !out_hi)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (!data || length <= 0)
    {
        *out_lo = 0ULL;
        *out_hi = 0ULL;
        return VN_OK;
    }

    constexpr uint64_t kPrimeLo = 0x9E3779B185EBCA87ULL;
    constexpr uint64_t kPrimeHi = 0xC2B2AE3D27D4EB4FULL;
    uint64_t lo = 0xCBF29CE484222325ULL ^ static_cast<uint64_t>(length);
    uint64_t hi = 0x84222325CBF29CE4ULL ^ (static_cast<uint64_t>(length) * kPrimeLo);

    int32_t words = length >> 3;
    const uint64_t* word_ptr = reinterpret_cast<const uint64_t*>(data);
    for (int32_t i = 0; i < words; ++i)
    {
        uint64_t w = word_ptr[i];
        uint64_t idx_mix = static_cast<uint64_t>(i + 1) * 0x165667B19E3779F9ULL;
        lo = vnotch::controllers::Avalanche64(lo ^ (w + idx_mix)) * kPrimeLo;
        hi = vnotch::controllers::Avalanche64(hi ^ ((w ^ idx_mix) * kPrimeLo)) * kPrimeHi;
    }

    for (int32_t i = words << 3; i < length; ++i)
    {
        uint64_t b = static_cast<uint64_t>(data[i]) + 1ULL;
        uint64_t pos = static_cast<uint64_t>(i + 1) * 0x27D4EB2F165667C5ULL;
        lo = vnotch::controllers::Avalanche64(lo ^ (b * pos)) * kPrimeLo;
        hi = vnotch::controllers::Avalanche64(hi ^ ((b << 8) ^ pos)) * kPrimeHi;
    }

    *out_lo = vnotch::controllers::Avalanche64(lo ^ hi);
    *out_hi = vnotch::controllers::Avalanche64(hi ^ (lo * kPrimeLo));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_calendar_clamp_index(
    int32_t idx,
    int32_t* out_clamped) noexcept
{
    if (!out_clamped) return VN_ERR_INVALID_ARG;
    constexpr int32_t kTotalDays = 11;
    *out_clamped = std::clamp(idx, 0, kTotalDays - 1);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_calendar_center_index_from_strip_x(
    double strip_x,
    int32_t* out_center_idx) noexcept
{
    if (!out_center_idx) return VN_ERR_INVALID_ARG;
    constexpr int32_t kTotalDays = 11;
    constexpr double kCellWidth = 30.0;
    int32_t center_idx = static_cast<int32_t>(std::nearbyint((30.0 - strip_x) / kCellWidth));
    *out_center_idx = std::clamp(center_idx, 0, kTotalDays - 1);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_calendar_highlight_x_for_index(
    int32_t center_idx,
    double* out_highlight_x) noexcept
{
    if (!out_highlight_x) return VN_ERR_INVALID_ARG;
    constexpr double kCellWidth = 30.0;
    constexpr double kHighlightDiameter = 24.0;
    *out_highlight_x = static_cast<double>(center_idx) * kCellWidth + (kCellWidth - kHighlightDiameter) / 2.0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_calendar_strip_x_for_index(
    int32_t center_idx,
    double* out_strip_x) noexcept
{
    if (!out_strip_x) return VN_ERR_INVALID_ARG;
    constexpr double kCellWidth = 30.0;
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

    constexpr int32_t kTotalDays = 11;
    double acc = accumulator + static_cast<double>(delta);
    int32_t direction = acc > 0.0 ? -1 : 1;
    double abs_acc = std::abs(acc);
    int32_t step_count = static_cast<int32_t>(abs_acc / 120.0);
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

VN_EXPORT int32_t __stdcall vn_spotlight_is_alt_space_key(
    uint32_t vk_code,
    uint32_t flags,
    uint8_t* out_match) noexcept
{
    if (!out_match) return VN_ERR_INVALID_ARG;
    constexpr uint32_t kVkSpace = 0x20u;
    constexpr uint32_t kLlkhfAltDown = 0x20u;
    *out_match = (vk_code == kVkSpace && (flags & kLlkhfAltDown) != 0u) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_spotlight_is_escape_key(
    uint32_t vk_code,
    uint8_t* out_match) noexcept
{
    if (!out_match) return VN_ERR_INVALID_ARG;
    *out_match = (vk_code == 0x1Bu) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_spotlight_should_dispatch_fallback_toggle(
    uint8_t space_already_down,
    uint32_t last_space_time,
    uint32_t current_time,
    uint8_t* out_dispatch) noexcept
{
    if (!out_dispatch) return VN_ERR_INVALID_ARG;
    constexpr uint32_t kStaleFallbackKeyDownMs = 500u;
    uint32_t elapsed = current_time - last_space_time;
    *out_dispatch = (space_already_down == 0 || elapsed > kStaleFallbackKeyDownMs) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_spotlight_kbd_process(
    uint8_t enabled,
    uint8_t fallback,
    uint8_t open,
    int32_t escape_session,
    uint8_t* io_has_observed,
    uint8_t* io_obs_enabled,
    uint8_t* io_obs_fallback,
    int32_t* io_obs_escape_session,
    uint8_t* io_space_down,
    uint8_t* io_escape_down,
    uint32_t* io_last_space_time,
    uint32_t vk_code,
    uint32_t flags,
    uint32_t time,
    int32_t message,
    int32_t* out_action) noexcept
{
    if (!io_has_observed || !io_obs_enabled || !io_obs_fallback || !io_obs_escape_session ||
        !io_space_down || !io_escape_down || !io_last_space_time || !out_action)
    {
        return VN_ERR_INVALID_ARG;
    }

    if (*io_has_observed == 0 || *io_obs_escape_session != escape_session)
    {
        *io_escape_down = 0;
    }
    if (*io_has_observed == 0 || *io_obs_enabled != enabled || *io_obs_fallback != fallback)
    {
        *io_space_down = 0;
        *io_last_space_time = 0;
    }

    *io_has_observed = 1;
    *io_obs_enabled = enabled;
    *io_obs_fallback = fallback;
    *io_obs_escape_session = escape_session;

    if (enabled == 0)
    {
        *out_action = 0; // None
        return VN_OK;
    }

    constexpr int32_t kWmKeyDown = 0x0100;
    constexpr int32_t kWmKeyUp = 0x0101;
    constexpr int32_t kWmSysKeyDown = 0x0104;
    constexpr int32_t kWmSysKeyUp = 0x0105;
    constexpr uint32_t kVkSpace = 0x20u;
    constexpr uint32_t kLlkhfAltDown = 0x20u;
    constexpr uint32_t kStaleFallbackKeyDownMs = 500u;

    bool is_key_up = (message == kWmKeyUp || message == kWmSysKeyUp);
    bool is_key_down = (message == kWmKeyDown || message == kWmSysKeyDown);

    if (vk_code == 0x1Bu)
    {
        if (*io_escape_down != 0 && is_key_up)
        {
            *io_escape_down = 0;
            *out_action = 1; // Suppress
            return VN_OK;
        }
        if (open != 0 && is_key_down)
        {
            if (*io_escape_down != 0)
            {
                *out_action = 1; // Suppress
                return VN_OK;
            }
            *io_escape_down = 1;
            *out_action = 3; // Escape
            return VN_OK;
        }
    }

    if (*io_space_down != 0 && vk_code == kVkSpace && is_key_up)
    {
        *io_space_down = 0;
        *out_action = 1; // Suppress
        return VN_OK;
    }

    if (fallback != 0 && vk_code == kVkSpace && (flags & kLlkhfAltDown) != 0u && is_key_down)
    {
        bool dispatch = (*io_space_down == 0) || ((time - *io_last_space_time) > kStaleFallbackKeyDownMs);
        *io_space_down = 1;
        *io_last_space_time = time;
        *out_action = dispatch ? 2 : 1; // 2 = Toggle, 1 = Suppress
        return VN_OK;
    }

    *out_action = 0; // None
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_hotkey_parse_modifiers(
    const wchar_t* text,
    int32_t text_len,
    uint32_t* out_modifiers,
    int32_t* out_key_start,
    int32_t* out_key_len,
    uint8_t* out_valid) noexcept
{
    if (!out_modifiers || !out_key_start || !out_key_len || !out_valid)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_modifiers = 0;
    *out_key_start = 0;
    *out_key_len = 0;
    *out_valid = 0;

    if (!text || text_len <= 0)
    {
        return VN_OK;
    }

    std::wstring_view full(text, static_cast<size_t>(text_len));
    std::wstring_view tokens[8]{};
    int32_t token_count = 0;

    size_t pos = 0;
    while (pos < full.size())
    {
        size_t plus = full.find(L'+', pos);
        std::wstring_view raw = (plus == std::wstring_view::npos)
            ? full.substr(pos)
            : full.substr(pos, plus - pos);
        std::wstring_view trimmed = vnotch::controllers::TrimWhitespace(raw);
        if (!trimmed.empty())
        {
            if (token_count >= 8)
            {
                return VN_OK;
            }
            tokens[token_count++] = trimmed;
        }
        if (plus == std::wstring_view::npos)
        {
            break;
        }
        pos = plus + 1;
    }

    if (token_count < 2)
    {
        return VN_OK;
    }

    uint32_t mods = 0;
    for (int32_t i = 0; i < token_count - 1; ++i)
    {
        std::wstring_view part = tokens[i];
        uint32_t flag = 0;
        if (vnotch::controllers::EqualsIgnoreCaseAscii(part, L"ctrl") ||
            vnotch::controllers::EqualsIgnoreCaseAscii(part, L"control"))
        {
            flag = 2u;
        }
        else if (vnotch::controllers::EqualsIgnoreCaseAscii(part, L"shift"))
        {
            flag = 4u;
        }
        else if (vnotch::controllers::EqualsIgnoreCaseAscii(part, L"alt"))
        {
            flag = 1u;
        }
        else if (vnotch::controllers::EqualsIgnoreCaseAscii(part, L"win"))
        {
            flag = 8u;
        }

        if (flag == 0u || (mods & flag) != 0u)
        {
            return VN_OK;
        }
        mods |= flag;
    }

    if ((mods & (1u | 2u | 8u)) == 0u)
    {
        return VN_OK;
    }

    std::wstring_view last_token = tokens[token_count - 1];
    // Reject purely numeric tokens like "999999" that Enum.TryParse<Key> would accept as arbitrary enum integers
    bool all_digits = !last_token.empty();
    for (wchar_t c : last_token)
    {
        if (c < L'0' || c > L'9')
        {
            all_digits = false;
            break;
        }
    }
    if (all_digits && last_token.size() > 1)
    {
        return VN_OK;
    }

    *out_modifiers = mods;
    *out_key_start = static_cast<int32_t>(last_token.data() - text);
    *out_key_len = static_cast<int32_t>(last_token.size());
    *out_valid = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_hotkey_is_os_reserved(
    uint32_t modifiers,
    uint32_t vk,
    uint8_t* out_reserved) noexcept
{
    if (!out_reserved) return VN_ERR_INVALID_ARG;
    uint32_t mods = modifiers & ~0x4000u;
    bool reserved =
        (mods == 8u && vk == 0x56u) ||
        (mods == (8u | 4u) && vk == 0x53u) ||
        (mods == 8u && vk == 0x09u) ||
        (mods == 8u && vk == 0x2Cu);
    *out_reserved = reserved ? 1 : 0;
    return VN_OK;
}

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

VN_EXPORT int32_t __stdcall vn_timer_parse_custom_time(
    const wchar_t* input,
    int32_t input_len,
    int64_t* out_ticks,
    uint8_t* out_valid) noexcept
{
    if (!out_ticks || !out_valid)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_ticks = 0;
    *out_valid = 0;

    if (!input || input_len <= 0)
    {
        return VN_OK;
    }

    std::wstring_view s = vnotch::controllers::TrimWhitespace(
        std::wstring_view(input, static_cast<size_t>(input_len)));
    if (s.empty())
    {
        return VN_OK;
    }

    if (s.find(L':') != std::wstring_view::npos)
    {
        std::wstring_view parts[4]{};
        int32_t part_count = 0;
        size_t pos = 0;
        while (pos <= s.size())
        {
            if (part_count >= 4)
            {
                part_count = 5;
                break;
            }
            size_t colon = s.find(L':', pos);
            if (colon == std::wstring_view::npos)
            {
                parts[part_count++] = s.substr(pos);
                break;
            }
            parts[part_count++] = s.substr(pos, colon - pos);
            pos = colon + 1;
        }

        if (part_count == 2)
        {
            int32_t mins = 0, secs = 0;
            if (vnotch::controllers::TryParseInt32Strict(parts[0], &mins) &&
                vnotch::controllers::TryParseInt32Strict(parts[1], &secs) &&
                mins >= 0 && secs >= 0 && secs < 60)
            {
                double total_sec = static_cast<double>(mins) * 60.0 + static_cast<double>(secs);
                if (vnotch::controllers::TryTicksFromSeconds(total_sec, out_ticks))
                {
                    *out_valid = 1;
                }
                return VN_OK;
            }
        }
        else if (part_count == 3)
        {
            int32_t hrs = 0, mins = 0, secs = 0;
            if (vnotch::controllers::TryParseInt32Strict(parts[0], &hrs) &&
                vnotch::controllers::TryParseInt32Strict(parts[1], &mins) &&
                vnotch::controllers::TryParseInt32Strict(parts[2], &secs) &&
                hrs >= 0 && mins >= 0 && mins < 60 && secs >= 0 && secs < 60)
            {
                double total_sec = static_cast<double>(hrs) * 3600.0 +
                                   static_cast<double>(mins) * 60.0 +
                                   static_cast<double>(secs);
                if (vnotch::controllers::TryTicksFromSeconds(total_sec, out_ticks))
                {
                    *out_valid = 1;
                }
                return VN_OK;
            }
        }
        return VN_OK;
    }

    double plain_minutes = 0.0;
    if (vnotch::controllers::TryParseDoubleStrict(s, &plain_minutes))
    {
        if (vnotch::controllers::TryTicksFromSeconds(plain_minutes * 60.0, out_ticks))
        {
            *out_valid = 1;
        }
        return VN_OK;
    }

    double total_seconds = 0.0;
    bool any_match = false;
    size_t i = 0;
    while (i < s.size())
    {
        while (i < s.size() && !(s[i] >= L'0' && s[i] <= L'9'))
        {
            ++i;
        }
        if (i >= s.size())
        {
            break;
        }
        size_t num_start = i;
        while (i < s.size() && (s[i] >= L'0' && s[i] <= L'9'))
        {
            ++i;
        }
        if (i < s.size() && s[i] == L'.')
        {
            size_t dot_pos = i++;
            size_t frac_start = i;
            while (i < s.size() && (s[i] >= L'0' && s[i] <= L'9'))
            {
                ++i;
            }
            if (i == frac_start)
            {
                i = dot_pos;
            }
        }
        std::wstring_view num_sv = s.substr(num_start, i - num_start);
        while (i < s.size() && vnotch::controllers::IsAsciiWhitespace(s[i]))
        {
            ++i;
        }
        if (i < s.size())
        {
            wchar_t unit = vnotch::controllers::ToLowerAscii(s[i]);
            if (unit == L's' || unit == L'm' || unit == L'h' || unit == L'd')
            {
                double val = 0.0;
                if (vnotch::controllers::TryParseDoubleStrict(num_sv, &val) && std::isfinite(val))
                {
                    any_match = true;
                    switch (unit)
                    {
                    case L's': total_seconds += val; break;
                    case L'm': total_seconds += val * 60.0; break;
                    case L'h': total_seconds += val * 3600.0; break;
                    case L'd': total_seconds += val * 86400.0; break;
                    default: break;
                    }
                }
                ++i;
            }
        }
    }

    if (any_match && vnotch::controllers::TryTicksFromSeconds(total_seconds, out_ticks))
    {
        *out_valid = 1;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_timer_format_remaining(
    int64_t remaining_ticks,
    wchar_t* out_buf,
    int32_t buf_cap,
    int32_t* out_len) noexcept
{
    if (!out_buf || buf_cap <= 0 || !out_len)
    {
        return VN_ERR_INVALID_ARG;
    }

    constexpr int64_t kTicksPerSecond = 10000000LL;
    constexpr int64_t kTicksPerMinute = 60LL * kTicksPerSecond;
    constexpr int64_t kTicksPerHour = 60LL * kTicksPerMinute;
    constexpr int64_t kTicksPerDay = 24LL * kTicksPerHour;

    int64_t ticks = std::max<int64_t>(0LL, remaining_ticks);
    int32_t written = 0;

    if (ticks >= kTicksPerDay)
    {
        int32_t total_days = static_cast<int32_t>(ticks / kTicksPerDay);
        int32_t hours = static_cast<int32_t>((ticks % kTicksPerDay) / kTicksPerHour);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%dd %02dh", total_days, hours);
    }
    else if (ticks >= kTicksPerHour)
    {
        int32_t total_hours = static_cast<int32_t>(ticks / kTicksPerHour);
        int32_t minutes = static_cast<int32_t>((ticks % kTicksPerHour) / kTicksPerMinute);
        int32_t seconds = static_cast<int32_t>((ticks % kTicksPerMinute) / kTicksPerSecond);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%02d:%02d:%02d", total_hours, minutes, seconds);
    }
    else
    {
        int32_t total_minutes = static_cast<int32_t>(ticks / kTicksPerMinute);
        int32_t seconds = static_cast<int32_t>((ticks % kTicksPerMinute) / kTicksPerSecond);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%02d:%02d", total_minutes, seconds);
    }

    *out_len = (written > 0) ? written : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_timer_compute_adjust_ticks(
    int64_t current_duration_ticks,
    int32_t direction,
    int64_t* out_next_ticks,
    uint8_t* out_changed) noexcept
{
    if (!out_next_ticks || !out_changed)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (direction == 0)
    {
        *out_next_ticks = current_duration_ticks;
        *out_changed = 0;
        return VN_OK;
    }

    constexpr int64_t kTicksPerMinute = 60LL * 10000000LL;
    constexpr int64_t kTicksPerHour = 60LL * kTicksPerMinute;
    constexpr int64_t kTicksPerDay = 24LL * kTicksPerHour;
    constexpr int64_t kMinimumTicks = kTicksPerMinute;
    constexpr int64_t kMaximumTicks = 7LL * kTicksPerDay;

    int64_t step = (current_duration_ticks >= kTicksPerDay)
        ? kTicksPerHour
        : ((current_duration_ticks >= kTicksPerHour) ? (5LL * kTicksPerMinute) : kTicksPerMinute);

    int64_t next = current_duration_ticks + (direction > 0 ? step : -step);
    next = std::clamp(next, kMinimumTicks, kMaximumTicks);
    *out_next_ticks = next;
    *out_changed = (next != current_duration_ticks) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_timer_compute_progress(
    int64_t duration_ticks,
    int64_t remaining_ticks,
    double* out_progress) noexcept
{
    if (!out_progress)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (duration_ticks <= 0)
    {
        *out_progress = 0.0;
        return VN_OK;
    }
    double ratio = static_cast<double>(remaining_ticks) / static_cast<double>(duration_ticks);
    *out_progress = 1.0 - std::clamp(ratio, 0.0, 1.0);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_progress_interpolate_ticks(
    int64_t last_position_ticks,
    int64_t duration_ticks,
    int64_t elapsed_ticks,
    double playback_rate,
    uint8_t is_playing,
    uint8_t is_throttled,
    uint8_t is_web_or_youtube,
    int64_t* out_display_ticks,
    double* out_ratio) noexcept
{
    if (!out_display_ticks || !out_ratio)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (duration_ticks <= 0)
    {
        *out_display_ticks = 0;
        *out_ratio = 0.0;
        return VN_OK;
    }

    int64_t display = last_position_ticks;
    if (is_playing != 0)
    {
        constexpr int64_t kTicksPerSecond = 10000000LL;
        int64_t cap_seconds = (is_throttled != 0)
            ? 3600LL
            : ((is_web_or_youtube != 0) ? 600LL : 30LL);
        int64_t max_elapsed = cap_seconds * kTicksPerSecond;
        int64_t clamped_elapsed = std::min(elapsed_ticks, max_elapsed);
        if (clamped_elapsed > 0)
        {
            display += static_cast<int64_t>(static_cast<double>(clamped_elapsed) * playback_rate);
        }
    }

    display = std::clamp<int64_t>(display, 0LL, duration_ticks);
    *out_display_ticks = display;
    *out_ratio = static_cast<double>(display) / static_cast<double>(duration_ticks);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_progress_format_time(
    int64_t time_ticks,
    wchar_t* out_buf,
    int32_t buf_cap,
    int32_t* out_len) noexcept
{
    if (!out_buf || buf_cap <= 0 || !out_len)
    {
        return VN_ERR_INVALID_ARG;
    }

    constexpr int64_t kTicksPerSecond = 10000000LL;
    constexpr int64_t kTicksPerMinute = 60LL * kTicksPerSecond;
    constexpr int64_t kTicksPerHour = 60LL * kTicksPerMinute;

    int64_t ticks = std::max<int64_t>(0LL, time_ticks);
    int32_t written = 0;

    if (ticks >= kTicksPerHour)
    {
        int32_t hours = static_cast<int32_t>(ticks / kTicksPerHour);
        int32_t minutes = static_cast<int32_t>((ticks % kTicksPerHour) / kTicksPerMinute);
        int32_t seconds = static_cast<int32_t>((ticks % kTicksPerMinute) / kTicksPerSecond);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%d:%02d:%02d", hours, minutes, seconds);
    }
    else
    {
        int32_t minutes = static_cast<int32_t>(ticks / kTicksPerMinute);
        int32_t seconds = static_cast<int32_t>((ticks % kTicksPerMinute) / kTicksPerSecond);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%d:%02d", minutes, seconds);
    }

    *out_len = (written > 0) ? written : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_audio_mixer_select_icon_glyph(
    float volume,
    uint8_t muted,
    int32_t* out_codepoint) noexcept
{
    if (!out_codepoint)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (muted != 0 || volume <= 0.01f)
    {
        *out_codepoint = 0xE74F;
    }
    else if (volume < 0.33f)
    {
        *out_codepoint = 0xE993;
    }
    else if (volume < 0.66f)
    {
        *out_codepoint = 0xE994;
    }
    else
    {
        *out_codepoint = 0xE995;
    }
    return VN_OK;
}

} // extern "C"
