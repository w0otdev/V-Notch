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

#include "Parsing/AsciiNumber.h"

extern "C" {
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


}
