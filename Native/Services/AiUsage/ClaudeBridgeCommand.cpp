#include "Parsing/Utf16Text.h"
#include "Parsing/Utf16Json.h"

using namespace vnotch::parsing;

VN_EXPORT int32_t __stdcall vn_claude_bridge_build_command(
    const wchar_t* script_ptr, int32_t script_len,
    wchar_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (out_len == nullptr || script_len < 0 || (script_len > 0 && script_ptr == nullptr))
        return VN_ERR_INVALID_ARG;

    constexpr std::u16string_view kCmdPrefix =
        u"powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";
    constexpr char kBase64Alphabet[65] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    const std::u16string_view script = make_u16_view(script_ptr, script_len);
    size_t invoke_u16_len = 4; // "& '" + "'"
    for (char16_t ch : script)
    {
        invoke_u16_len += (ch == u'\'') ? 2u : 1u;
    }

    const size_t byte_len = invoke_u16_len * 2u;
    const size_t b64_len = ((byte_len + 2u) / 3u) * 4u;
    const size_t total_len = kCmdPrefix.size() + b64_len;
    if (total_len > static_cast<size_t>(INT32_MAX)) return VN_ERR_FAIL;

    *out_len = static_cast<int32_t>(total_len);
    if (out_buf == nullptr || out_cap < static_cast<int32_t>(total_len))
        return VN_ERR_BUFFER_TOO_SMALL;

    std::memcpy(out_buf, kCmdPrefix.data(), kCmdPrefix.size() * sizeof(wchar_t));
    size_t write_idx = kCmdPrefix.size();

    uint8_t triad[3]{};
    int triad_count = 0;
    auto push_byte = [&](uint8_t b) noexcept
    {
        triad[triad_count++] = b;
        if (triad_count == 3)
        {
            const uint32_t v = (static_cast<uint32_t>(triad[0]) << 16) |
                               (static_cast<uint32_t>(triad[1]) << 8) |
                               static_cast<uint32_t>(triad[2]);
            out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 18) & 0x3Fu]);
            out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 12) & 0x3Fu]);
            out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 6) & 0x3Fu]);
            out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[v & 0x3Fu]);
            triad_count = 0;
        }
    };
    auto push_u16_le = [&](char16_t ch) noexcept
    {
        push_byte(static_cast<uint8_t>(ch & 0xFFu));
        push_byte(static_cast<uint8_t>((ch >> 8) & 0xFFu));
    };

    push_u16_le(u'&');
    push_u16_le(u' ');
    push_u16_le(u'\'');
    for (char16_t ch : script)
    {
        if (ch == u'\'')
        {
            push_u16_le(u'\'');
            push_u16_le(u'\'');
        }
        else
        {
            push_u16_le(ch);
        }
    }
    push_u16_le(u'\'');

    if (triad_count == 1)
    {
        const uint32_t v = static_cast<uint32_t>(triad[0]) << 16;
        out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 18) & 0x3Fu]);
        out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 12) & 0x3Fu]);
        out_buf[write_idx++] = L'=';
        out_buf[write_idx++] = L'=';
    }
    else if (triad_count == 2)
    {
        const uint32_t v = (static_cast<uint32_t>(triad[0]) << 16) |
                           (static_cast<uint32_t>(triad[1]) << 8);
        out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 18) & 0x3Fu]);
        out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 12) & 0x3Fu]);
        out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 6) & 0x3Fu]);
        out_buf[write_idx++] = L'=';
    }

    return VN_OK;
}

