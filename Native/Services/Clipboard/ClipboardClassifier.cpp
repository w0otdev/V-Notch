#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class ClipboardClassifier final
    {
    public:
        static inline int32_t hex_digit_val(char16_t c) noexcept
        {
            if (c >= u'0' && c <= u'9') return c - u'0';
            if (c >= u'a' && c <= u'f') return c - u'a' + 10;
            if (c >= u'A' && c <= u'F') return c - u'A' + 10;
            return -1;
        }
        
        static inline bool is_word_or_hash(char16_t c) noexcept
        {
            return vn_is_letter_or_digit(c) || c == u'_' || c == u'#';
        }
        
        static inline bool contains_phrase_ignore_case(std::u16string_view text, std::u16string_view phrase) noexcept
        {
            size_t offset = 0;
            bool phrase_ends_alnum = !phrase.empty() && vn_is_letter_or_digit(phrase.back());
            while (offset < text.size())
            {
                int32_t rel = index_of_ignore_case(text.substr(offset), phrase);
                if (rel < 0)
                {
                    return false;
                }
                size_t index = offset + static_cast<size_t>(rel);
                size_t end = index + phrase.size();
                bool start_ok = (index == 0) || !vn_is_letter_or_digit(text[index - 1]);
                bool end_ok = !phrase_ends_alnum || (end == text.size()) || !vn_is_letter_or_digit(text[end]);
                if (start_ok && end_ok)
                {
                    return true;
                }
                offset = end;
            }
            return false;
        }
        
        static inline bool matches_group_rule(
            std::u16string_view text,
            std::span<const std::u16string_view> strong_list,
            std::span<const std::u16string_view> context_list) noexcept
        {
            for (std::u16string_view s : strong_list)
            {
                if (contains_phrase_ignore_case(text, s))
                {
                    return true;
                }
            }
            int32_t context_count = 0;
            for (std::u16string_view c : context_list)
            {
                if (contains_phrase_ignore_case(text, c))
                {
                    if (++context_count >= 2)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        static int32_t vn_clipboard_is_image_path_impl(const char16_t* path_ptr,
    int32_t path_len,
    uint8_t* out_is_image) noexcept
        {
    if (out_is_image == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_is_image = 0u;
    std::u16string_view path = make_u16_view(path_ptr, path_len);
    size_t last_dot = std::u16string_view::npos;
    for (size_t i = path.size(); i > 0; --i)
    {
        char16_t c = path[i - 1];
        if (c == u'.')
        {
            last_dot = i - 1;
            break;
        }
        if (c == u'\\' || c == u'/' || c == u':')
        {
            break;
        }
    }
    if (last_dot == std::u16string_view::npos || last_dot + 1 >= path.size())
    {
        return VN_OK;
    }
    std::u16string_view ext = path.substr(last_dot);
    static constexpr std::u16string_view IMAGE_EXTS[] = {
        u".png", u".jpg", u".jpeg", u".bmp", u".gif", u".tif", u".tiff", u".webp", u".ico"
    };
    for (std::u16string_view ie : IMAGE_EXTS)
    {
        if (equals_ignore_case(ext, ie))
        {
            *out_is_image = 1u;
            return VN_OK;
        }
    }
    return VN_OK;
}

        static int32_t vn_clipboard_try_parse_color_impl(const char16_t* val_ptr,
    int32_t val_len,
    uint32_t* out_argb,
    uint8_t* out_ok) noexcept
        {
    if (out_argb == nullptr || out_ok == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_argb = 0u;
    *out_ok = 0u;

    std::u16string_view text = trim_view(make_u16_view(val_ptr, val_len));
    if (text.empty())
    {
        return VN_OK;
    }

    if (text[0] == u'#')
    {
        std::u16string_view hex = text.substr(1);
        if (hex.size() != 3 && hex.size() != 4 && hex.size() != 6 && hex.size() != 8)
        {
            return VN_OK;
        }
        uint32_t color = 0;
        for (char16_t c : hex)
        {
            int32_t d = hex_digit_val(c);
            if (d < 0)
            {
                return VN_OK;
            }
            if (hex.size() == 3 || hex.size() == 4)
            {
                color = (color << 8) | static_cast<uint32_t>((d << 4) | d);
            }
            else
            {
                color = (color << 4) | static_cast<uint32_t>(d);
            }
        }
        size_t expanded_len = (hex.size() == 3 || hex.size() == 4) ? (hex.size() * 2) : hex.size();
        *out_argb = (expanded_len == 6) ? (0xFF000000u | color) : ((color >> 8) | ((color & 0xFFu) << 24));
        *out_ok = 1u;
        return VN_OK;
    }

    size_t open_paren = 0;
    if (starts_with_ordinal(text, u"rgb("))
    {
        open_paren = 3;
    }
    else if (starts_with_ordinal(text, u"rgba("))
    {
        open_paren = 4;
    }
    else
    {
        return VN_OK;
    }
    if (text.back() != u')')
    {
        return VN_OK;
    }

    std::u16string_view inner = text.substr(open_paren + 1, text.size() - open_paren - 2);
    std::u16string_view parts[4]{};
    size_t part_count = 0;
    size_t start = 0;
    for (size_t i = 0; i <= inner.size(); ++i)
    {
        if (i == inner.size() || inner[i] == u',')
        {
            if (part_count >= 4)
            {
                return VN_OK;
            }
            parts[part_count++] = trim_view(inner.substr(start, i - start));
            start = i + 1;
        }
    }
    if (part_count != 3 && part_count != 4)
    {
        return VN_OK;
    }

    uint8_t rgb[3]{};
    for (size_t p = 0; p < 3; ++p)
    {
        std::u16string_view ps = parts[p];
        if (ps.empty())
        {
            return VN_OK;
        }
        uint32_t val = 0;
        for (char16_t c : ps)
        {
            if (c < u'0' || c > u'9')
            {
                return VN_OK;
            }
            val = val * 10u + static_cast<uint32_t>(c - u'0');
            if (val > 255u)
            {
                return VN_OK;
            }
        }
        rgb[p] = static_cast<uint8_t>(val);
    }

    double alpha = 1.0;
    if (part_count == 4)
    {
        std::u16string_view as = parts[3];
        if (as.empty())
        {
            return VN_OK;
        }
        bool seen_dot = false;
        bool seen_digit = false;
        double int_part = 0.0;
        double frac_part = 0.0;
        double frac_div = 1.0;
        for (char16_t c : as)
        {
            if (c == u'.')
            {
                if (seen_dot)
                {
                    return VN_OK;
                }
                seen_dot = true;
            }
            else if (c >= u'0' && c <= u'9')
            {
                seen_digit = true;
                if (!seen_dot)
                {
                    int_part = int_part * 10.0 + static_cast<double>(c - u'0');
                }
                else
                {
                    frac_div *= 10.0;
                    frac_part += static_cast<double>(c - u'0') / frac_div;
                }
            }
            else
            {
                return VN_OK;
            }
        }
        if (!seen_digit)
        {
            return VN_OK;
        }
        alpha = int_part + frac_part;
        if (alpha < 0.0 || alpha > 1.0)
        {
            return VN_OK;
        }
    }

    uint32_t a_byte = static_cast<uint32_t>(std::nearbyint(alpha * 255.0));
    *out_argb = (a_byte << 24) | (static_cast<uint32_t>(rgb[0]) << 16) | (static_cast<uint32_t>(rgb[1]) << 8) | static_cast<uint32_t>(rgb[2]);
    *out_ok = 1u;
    return VN_OK;
}

        static int32_t vn_clipboard_try_extract_hex_color_impl(const char16_t* text_ptr,
    int32_t text_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len,
    uint8_t* out_ok) noexcept
        {
    if (out_len == nullptr || out_ok == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_len = 0;
    *out_ok = 0u;
    std::u16string_view text = make_u16_view(text_ptr, text_len);
    std::u16string_view found_match{};
    int32_t match_count = 0;

    for (size_t i = 0; i < text.size(); ++i)
    {
        if (text[i] != u'#')
        {
            continue;
        }
        if (i > 0 && is_word_or_hash(text[i - 1]))
        {
            continue;
        }
        size_t hex_len = 0;
        while (i + 1 + hex_len < text.size() && hex_digit_val(text[i + 1 + hex_len]) >= 0)
        {
            ++hex_len;
        }
        // Try longest valid length (8, 6, 4, 3) that is not followed by [\w#]
        static constexpr size_t VALID_LENS[] = { 8, 6, 4, 3 };
        size_t matched_len = 0;
        for (size_t vl : VALID_LENS)
        {
            if (hex_len >= vl)
            {
                size_t end_idx = i + 1 + vl;
                if (end_idx == text.size() || !is_word_or_hash(text[end_idx]))
                {
                    matched_len = vl;
                    break;
                }
            }
        }
        if (matched_len > 0)
        {
            ++match_count;
            if (match_count > 1)
            {
                return VN_OK;
            }
            found_match = text.substr(i, 1 + matched_len);
            i += matched_len;
        }
    }

    if (match_count != 1)
    {
        return VN_OK;
    }

    char16_t upper_hex[10]{};
    for (size_t i = 0; i < found_match.size(); ++i)
    {
        char16_t c = found_match[i];
        upper_hex[i] = (c >= u'a' && c <= u'f') ? static_cast<char16_t>(c - 32) : c;
    }
    *out_ok = 1u;
    return copy_u16_out(std::u16string_view(upper_hex, found_match.size()), out_buf, out_cap, out_len);
}

        static int32_t vn_clipboard_classify_groups_impl(const char16_t* text_ptr,
    int32_t text_len,
    uint32_t* out_group_mask) noexcept
        {
    if (out_group_mask == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_group_mask = 0u;
    std::u16string_view text = make_u16_view(text_ptr, text_len);
    if (is_null_or_whitespace(text))
    {
        return VN_OK;
    }

    static constexpr std::u16string_view WORK_STRONG[] = { u"project", u"meeting", u"deadline", u"pull request", u"d\u1EF1 \u00E1n", u"c\u00F4ng vi\u1EC7c", u"cu\u1ED9c h\u1ECDp" };
    static constexpr std::u16string_view WORK_CTX[] = { u"agenda", u"task", u"commit" };

    static constexpr std::u16string_view IDEAS_STRONG[] = { u"idea", u"brainstorm", u"inspiration", u"sketch", u"\u00FD t\u01B0\u1EDFng", u"c\u1EA3m h\u1EE9ng" };
    static constexpr std::u16string_view IDEAS_CTX[] = { u"concept", u"palette" };

    static constexpr std::u16string_view EMAIL_STRONG[] = { u"subject:", u"mailto:", u"k\u00EDnh g\u1EEDi" };
    static constexpr std::u16string_view EMAIL_CTX[] = { u"from:", u"to:", u"dear ", u"regards", u"tr\u00E2n tr\u1ECDng" };

    static constexpr std::u16string_view SHOP_STRONG[] = { u"shopping", u"add to cart", u"amazon.", u"shopee.", u"lazada.", u"mua h\u00E0ng", u"gi\u1ECF h\u00E0ng" };
    static constexpr std::u16string_view SHOP_CTX[] = { u"checkout", u"product" };

    static constexpr std::u16string_view RECEIPT_STRONG[] = { u"receipt", u"invoice", u"total paid", u"h\u00F3a \u0111\u01A1n", u"ho\u00E1 \u0111\u01A1n" };
    static constexpr std::u16string_view RECEIPT_CTX[] = { u"subtotal", u"order total", u"thanh to\u00E1n" };

    static constexpr std::u16string_view IMP_STRONG[] = { u"important", u"urgent", u"critical", u"asap", u"quan tr\u1ECDng", u"kh\u1EA9n c\u1EA5p" };

    uint32_t mask = 0u;
    if (matches_group_rule(text, WORK_STRONG, WORK_CTX)) mask |= (1u << 0);
    if (matches_group_rule(text, IDEAS_STRONG, IDEAS_CTX)) mask |= (1u << 1);
    if (matches_group_rule(text, EMAIL_STRONG, EMAIL_CTX)) mask |= (1u << 2);
    if (matches_group_rule(text, SHOP_STRONG, SHOP_CTX)) mask |= (1u << 3);
    if (matches_group_rule(text, RECEIPT_STRONG, RECEIPT_CTX)) mask |= (1u << 4);
    if (matches_group_rule(text, IMP_STRONG, {})) mask |= (1u << 5);

    *out_group_mask = mask;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_clipboard_is_image_path(const char16_t* path_ptr,
    int32_t path_len,
    uint8_t* out_is_image) noexcept
{
    return vnotch::services::ClipboardClassifier::vn_clipboard_is_image_path_impl(path_ptr, path_len, out_is_image);
}

VN_EXPORT int32_t vn_clipboard_try_parse_color(const char16_t* val_ptr,
    int32_t val_len,
    uint32_t* out_argb,
    uint8_t* out_ok) noexcept
{
    return vnotch::services::ClipboardClassifier::vn_clipboard_try_parse_color_impl(val_ptr, val_len, out_argb, out_ok);
}

VN_EXPORT int32_t vn_clipboard_try_extract_hex_color(const char16_t* text_ptr,
    int32_t text_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len,
    uint8_t* out_ok) noexcept
{
    return vnotch::services::ClipboardClassifier::vn_clipboard_try_extract_hex_color_impl(text_ptr, text_len, out_buf, out_cap, out_len, out_ok);
}

VN_EXPORT int32_t vn_clipboard_classify_groups(const char16_t* text_ptr,
    int32_t text_len,
    uint32_t* out_group_mask) noexcept
{
    return vnotch::services::ClipboardClassifier::vn_clipboard_classify_groups_impl(text_ptr, text_len, out_group_mask);
}

