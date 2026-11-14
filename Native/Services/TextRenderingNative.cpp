#include "../vnotch_common.h"
#include <array>

namespace
{

static_assert(sizeof(wchar_t) == sizeof(char16_t));

constexpr char16_t fold_ascii_lower(char16_t ch) noexcept
{
    return (ch >= u'A' && ch <= u'Z') ? static_cast<char16_t>(ch + (u'a' - u'A')) : ch;
}

constexpr bool equals_font_token_icase(std::u16string_view a, std::u16string_view b) noexcept
{
    if (a.size() != b.size())
    {
        return false;
    }
    for (size_t i = 0; i < a.size(); ++i)
    {
        if (fold_ascii_lower(a[i]) != fold_ascii_lower(b[i]))
        {
            return false;
        }
    }
    return true;
}

constexpr std::u16string_view trim_font_token(std::u16string_view sv) noexcept
{
    size_t start = 0;
    size_t end = sv.size();
    while (start < end && vnotch::is_unicode_whitespace(sv[start]))
    {
        ++start;
    }
    while (end > start && vnotch::is_unicode_whitespace(sv[end - 1]))
    {
        --end;
    }
    return sv.substr(start, end - start);
}

constexpr std::u16string_view kPrimaryBundledFace = u"./#SF Pro Display";

// Ordered so modern anti-aliased DirectWrite vector UI faces resolve Latin, Vietnamese,
// symbols, emoji, CJK, Thai, and Indic scripts without hitting legacy 1-bit bitmap fonts
// (SimSun, MS UI Gothic, PMingLiU, Gulim) or letting Nirmala UI hijack Latin punctuation.
constexpr std::array<std::u16string_view, 10> kCanonicalFallbacks = {
    u"Segoe UI Variable Text",
    u"Segoe UI",
    u"Segoe UI Emoji",
    u"Segoe UI Symbol",
    u"Yu Gothic UI",
    u"Malgun Gothic",
    u"Microsoft JhengHei UI",
    u"Microsoft YaHei UI",
    u"Leelawadee UI",
    u"Nirmala UI",
};

constexpr std::u16string_view kFinalSafetyFallback = u"Arial";

constexpr bool is_builtin_or_bundled_token(std::u16string_view token) noexcept
{
    if (token.empty() ||
        equals_font_token_icase(token, kPrimaryBundledFace) ||
        equals_font_token_icase(token, u"SF Pro Display") ||
        equals_font_token_icase(token, u"#SF Pro Display") ||
        equals_font_token_icase(token, u"SF Pro Text") ||
        equals_font_token_icase(token, kFinalSafetyFallback))
    {
        return true;
    }
    for (const auto builtin : kCanonicalFallbacks)
    {
        if (equals_font_token_icase(token, builtin))
        {
            return true;
        }
    }
    return false;
}

struct SpecWriter
{
    char16_t* buf;
    int32_t cap;
    int32_t len;

    constexpr void append_raw(std::u16string_view piece) noexcept
    {
        const int32_t count = static_cast<int32_t>(piece.size());
        if (buf != nullptr && len + count <= cap)
        {
            for (int32_t i = 0; i < count; ++i)
            {
                buf[len + i] = piece[static_cast<size_t>(i)];
            }
        }
        len += count;
    }

    constexpr void append_family(std::u16string_view family) noexcept
    {
        if (len > 0)
        {
            append_raw(u", ");
        }
        append_raw(family);
    }
};

} // namespace

VN_EXPORT int32_t __stdcall vn_text_build_font_spec(
    const wchar_t* fallbacks_ptr,
    int32_t fallbacks_len,
    wchar_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    if (!out_len)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_len = 0;

    if (fallbacks_len < 0 || (fallbacks_len > 0 && !fallbacks_ptr) ||
        out_cap < 0 || (out_cap > 0 && !out_buf))
    {
        return VN_ERR_INVALID_ARG;
    }

    SpecWriter writer{reinterpret_cast<char16_t*>(out_buf), out_cap, 0};
    writer.append_family(kPrimaryBundledFace);

    for (const auto family : kCanonicalFallbacks)
    {
        writer.append_family(family);
    }

    if (fallbacks_len > 0)
    {
        const std::u16string_view raw(reinterpret_cast<const char16_t*>(fallbacks_ptr), static_cast<size_t>(fallbacks_len));
        std::array<std::u16string_view, 32> extras{};
        size_t extra_count = 0;
        size_t pos = 0;

        while (pos <= raw.size())
        {
            const size_t comma = raw.find(u',', pos);
            const size_t end = (comma == std::u16string_view::npos) ? raw.size() : comma;
            const std::u16string_view token = trim_font_token(raw.substr(pos, end - pos));

            if (!is_builtin_or_bundled_token(token) && extra_count < extras.size())
            {
                bool duplicate = false;
                for (size_t i = 0; i < extra_count; ++i)
                {
                    if (equals_font_token_icase(extras[i], token))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                {
                    extras[extra_count++] = token;
                    writer.append_family(token);
                }
            }

            if (comma == std::u16string_view::npos)
            {
                break;
            }
            pos = comma + 1;
        }
    }

    writer.append_family(kFinalSafetyFallback);
    *out_len = writer.len;

    if (writer.len > out_cap)
    {
        return VN_ERR_BUFFER_TOO_SMALL;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_text_snap_bundled_weight(
    int32_t requested_weight,
    uint8_t is_bundled_sf_pro,
    int32_t* out_weight) noexcept
{
    if (!out_weight)
    {
        return VN_ERR_INVALID_ARG;
    }

    const int32_t clamped = std::clamp(requested_weight, 1, 999);
    if (is_bundled_sf_pro == 0)
    {
        *out_weight = clamped;
        return VN_OK;
    }

    if (clamped < 450)
    {
        *out_weight = 400;
    }
    else if (clamped < 550)
    {
        *out_weight = 500;
    }
    else if (clamped < 650)
    {
        *out_weight = 600;
    }
    else
    {
        *out_weight = 700;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_text_resolve_rendering_policy(
    int32_t element_role,
    uint8_t has_explicit_rendering_mode,
    int32_t current_rendering_mode,
    int32_t* out_formatting_mode,
    int32_t* out_rendering_mode,
    int32_t* out_hinting_mode,
    uint8_t* out_use_layout_rounding,
    uint8_t* out_snaps_to_device_pixels) noexcept
{
    if (!out_formatting_mode || !out_rendering_mode || !out_hinting_mode ||
        !out_use_layout_rounding || !out_snaps_to_device_pixels)
    {
        return VN_ERR_INVALID_ARG;
    }

    (void)element_role;
    // Ideal formatting + Animated hinting + fractional layout across containers
    // keeps glyph metrics and origins identical during and after animations.
    *out_formatting_mode = 0; // TextFormattingMode.Ideal
    *out_rendering_mode = has_explicit_rendering_mode != 0 ? current_rendering_mode : 1; // Grayscale default
    *out_hinting_mode = 2; // TextHintingMode.Animated
    *out_use_layout_rounding = 0;
    *out_snaps_to_device_pixels = 0;
    return VN_OK;
}
