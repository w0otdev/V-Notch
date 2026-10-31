#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class TranslationTextFormatting final
    {
    public:
        static bool is_line_break(char16_t c) noexcept
            {
                return c == u'\r' || c == u'\n' || c == u'\f' || c == u'\u0085' || c == u'\u2028' || c == u'\u2029';
            }
        
            static bool try_get_opening_fence(std::u16string_view trimmed, char16_t& fence_char, int32_t& fence_len) noexcept
            {
                fence_char = u'\0';
                fence_len = 0;
                if (trimmed.size() < 3) return false;
                char16_t first = trimmed[0];
                if (first != u'`' && first != u'~') return false;
                int32_t len = 0;
                while (static_cast<size_t>(len) < trimmed.size() && trimmed[len] == first) ++len;
                if (len < 3) return false;
                if (first == u'`' && trimmed.substr(len).find(u'`') != std::u16string_view::npos) return false;
                fence_char = first;
                fence_len = len;
                return true;
            }
        
            static bool is_closing_fence(std::u16string_view trimmed, char16_t fence_char, int32_t min_len) noexcept
            {
                if (static_cast<int32_t>(trimmed.size()) < min_len) return false;
                int32_t len = 0;
                while (static_cast<size_t>(len) < trimmed.size() && trimmed[len] == fence_char) ++len;
                if (len < min_len) return false;
                for (size_t i = static_cast<size_t>(len); i < trimmed.size(); ++i)
                {
                    if (!vnotch::is_whitespace(trimmed[i])) return false;
                }
                return true;
            }
        
            static bool is_list_item(std::u16string_view trimmed) noexcept
            {
                if (trimmed.size() >= 2 && (trimmed[0] == u'-' || trimmed[0] == u'*' || trimmed[0] == u'+') &&
                    (trimmed[1] == u' ' || trimmed[1] == u'\t'))
                {
                    return true;
                }
                size_t digits = 0;
                while (digits < trimmed.size() && vnotch::is_ascii_digit(trimmed[digits])) ++digits;
                return digits > 0 && digits + 1 < trimmed.size() &&
                       (trimmed[digits] == u'.' || trimmed[digits] == u')') &&
                       (trimmed[digits + 1] == u' ' || trimmed[digits + 1] == u'\t');
            }
        
            static int32_t classify_block_line(std::u16string_view trimmed) noexcept
            {
                if (!trimmed.empty() && trimmed[0] == u'|') return 1;
                if (!trimmed.empty() && trimmed[0] == u'>') return 3;
                if (is_list_item(trimmed)) return 2;
                return 0;
            }
        
            static void append_block(std::vector<std::u16string>& paragraphs, std::vector<std::u16string_view>& current_block)
            {
                if (current_block.empty()) return;
                std::u16string joined;
                for (size_t i = 0; i < current_block.size(); ++i)
                {
                    if (i > 0) joined.push_back(u'\n');
                    joined.append(current_block[i]);
                }
                paragraphs.push_back(std::move(joined));
                current_block.clear();
            }

        static int32_t vn_translation_format_text_impl(const char16_t* text_ptr, int32_t text_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len)
        {
    if (!out_len) return VN_ERR_INVALID_ARG;
    std::u16string_view text = vnotch::make_view(text_ptr, text_len);

    std::vector<std::u16string> paragraphs;
    std::vector<std::u16string_view> current_block;
    char16_t fence_char = u'\0';
    int32_t fence_len = 0;
    int32_t last_kind = 0;

    auto flush_group = [&]() {
        append_block(paragraphs, current_block);
        last_kind = 0;
    };

    size_t pos = 0;
    bool more = true;
    while (more)
    {
        size_t line_start = pos;
        while (pos < text.size() && !is_line_break(text[pos])) ++pos;
        std::u16string_view raw_line = text.substr(line_start, pos - line_start);

        if (pos < text.size())
        {
            if (text[pos] == u'\r' && pos + 1 < text.size() && text[pos + 1] == u'\n')
                pos += 2;
            else
                pos += 1;
            more = true;
        }
        else
        {
            more = false;
        }

        std::u16string_view trimmed = vnotch::trim(raw_line);

        if (fence_len > 0)
        {
            current_block.push_back(raw_line);
            if (is_closing_fence(trimmed, fence_char, fence_len))
            {
                append_block(paragraphs, current_block);
                fence_char = u'\0';
                fence_len = 0;
            }
            continue;
        }

        char16_t open_char = u'\0';
        int32_t open_len = 0;
        if (try_get_opening_fence(trimmed, open_char, open_len))
        {
            flush_group();
            fence_char = open_char;
            fence_len = open_len;
            current_block.push_back(raw_line);
            continue;
        }

        if (trimmed.empty())
        {
            flush_group();
            continue;
        }

        int32_t kind = classify_block_line(trimmed);
        if (kind != 0)
        {
            if (last_kind != 0 && last_kind != kind) flush_group();
            last_kind = kind;
            current_block.push_back(raw_line);
        }
        else
        {
            flush_group();
            paragraphs.emplace_back(raw_line);
        }
    }

    flush_group();

    std::u16string result;
    for (size_t i = 0; i < paragraphs.size(); ++i)
    {
        if (i > 0) result.append(u"\n\n");
        result.append(paragraphs[i]);
    }

    return vnotch::write_u16_out(result, out_buf, out_cap, out_len);
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_translation_format_text(const char16_t* text_ptr, int32_t text_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::TranslationTextFormatting::vn_translation_format_text_impl(text_ptr, text_len, out_buf, out_cap, out_len);
 } catch (...) { return VN_ERR_FAIL; }
}

