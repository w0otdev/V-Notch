#include "vnotch_ext_ui.h"

#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string_view>

#include "Controls/UiValueMath.h"
#include "Parsing/Utf16Writer.h"
using namespace vnotch::ui;

VN_EXPORT int32_t __stdcall vn_ai_markdown_close_stream_spans(
    const wchar_t* md,
    int32_t len,
    const VnMarkdownBoundarySummary* summary,
    int32_t* out_insert_pos,
    wchar_t* out_closer,
    int32_t cap,
    int32_t* out_len) noexcept
{
    if (!summary || !out_insert_pos || !out_closer || cap <= 0 || !out_len)
        return VN_ERR_INVALID_ARG;

    *out_insert_pos = len;
    *out_len = 0;

    if (!md || len <= 0)
        return VN_OK;

    const auto* chars = reinterpret_cast<const char16_t*>(md);
    Utf16Writer w{ reinterpret_cast<char16_t*>(out_closer), cap };

    if (summary->has_unclosed_fence != 0)
    {
        const char16_t fence_ch = summary->fence_char != 0
            ? static_cast<char16_t>(summary->fence_char)
            : u'`';
        const int32_t fence_count = std::max<int32_t>(3, summary->fence_count);
        w.AppendChar(u'\n');
        for (int32_t i = 0; i < fence_count; ++i)
            w.AppendChar(fence_ch);
        if (w.overflow)
            return VN_ERR_BUFFER_TOO_SMALL;
        *out_insert_pos = len;
        *out_len = w.len;
        return VN_OK;
    }

    if (summary->has_unclosed_inline_code != 0)
    {
        const int32_t ticks = std::max<int32_t>(1, summary->inline_code_ticks);
        if (chars[len - 1] == u'`')
            w.AppendChar(u' ');
        for (int32_t i = 0; i < ticks; ++i)
            w.AppendChar(u'`');
        if (w.overflow)
            return VN_ERR_BUFFER_TOO_SMALL;
        *out_insert_pos = len;
        *out_len = w.len;
        return VN_OK;
    }

    if (summary->has_unclosed_bold == 0 && summary->has_unclosed_italic == 0)
        return VN_OK;

    int32_t trim_end = len;
    while (trim_end > 0 && IsUtf16WhiteSpace(chars[trim_end - 1]))
        --trim_end;

    if (trim_end == 0)
        return VN_OK;

    const char16_t bold_ch = summary->bold_char != 0 ? static_cast<char16_t>(summary->bold_char) : u'*';
    const char16_t italic_ch = summary->italic_char != 0 ? static_cast<char16_t>(summary->italic_char) : u'*';

    if (summary->bold_outer != 0)
    {
        if (summary->has_unclosed_italic != 0)
            w.AppendChar(italic_ch);
        if (summary->has_unclosed_bold != 0)
        {
            w.AppendChar(bold_ch);
            w.AppendChar(bold_ch);
        }
    }
    else
    {
        if (summary->has_unclosed_bold != 0)
        {
            w.AppendChar(bold_ch);
            w.AppendChar(bold_ch);
        }
        if (summary->has_unclosed_italic != 0)
            w.AppendChar(italic_ch);
    }

    if (w.overflow)
        return VN_ERR_BUFFER_TOO_SMALL;

    *out_insert_pos = trim_end;
    *out_len = w.len;
    return VN_OK;
}

