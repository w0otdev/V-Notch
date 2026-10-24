#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class TranslationMeaningIntegrity final
    {
    public:
        static bool is_word_char(char16_t c) noexcept
            {
                return vnotch::is_unicode_letter_or_digit(c) || c == u'_' || vnotch::is_non_spacing_mark(c);
            }
        
            static bool equals_unicode_icase(std::u16string_view a, std::u16string_view b) noexcept
            {
                if (a.size() != b.size()) return false;
                if (a.empty()) return true;
                return CompareStringOrdinal(
                    reinterpret_cast<LPCWCH>(a.data()), static_cast<int>(a.size()),
                    reinterpret_cast<LPCWCH>(b.data()), static_cast<int>(b.size()),
                    TRUE) == CSTR_EQUAL;
            }
        
            static bool has_question_mark(std::u16string_view text) noexcept
            {
                for (char16_t c : text)
                {
                    if (c == u'?' || c == u'？' || c == u'؟')
                        return true;
                }
                return false;
            }
        
            static bool match_word_at(std::u16string_view text, size_t pos, std::u16string_view word) noexcept
            {
                if (pos + word.size() > text.size()) return false;
                if (pos > 0 && is_word_char(text[pos - 1])) return false;
                if (pos + word.size() < text.size() && is_word_char(text[pos + word.size()])) return false;
                return equals_unicode_icase(text.substr(pos, word.size()), word);
            }
        
            static bool contains_any_word(std::u16string_view text, std::span<const std::u16string_view> words) noexcept
            {
                for (size_t i = 0; i < text.size(); ++i)
                {
                    if (i > 0 && is_word_char(text[i - 1])) continue;
                    for (std::u16string_view w : words)
                    {
                        if (match_word_at(text, i, w)) return true;
                    }
                }
                return false;
            }
        
            static bool has_co_khong_chua(std::u16string_view text) noexcept
            {
                // \bcó\b.+\b(không|chưa)\b (where .+ does not cross \n)
                for (size_t i = 0; i < text.size(); ++i)
                {
                    if (!match_word_at(text, i, u"có")) continue;
                    size_t after_co = i + 2;
                    for (size_t j = after_co + 1; j < text.size(); ++j)
                    {
                        if (text[j - 1] == u'\n') break;
                        if (match_word_at(text, j, u"không") || match_word_at(text, j, u"chưa"))
                            return true;
                    }
                }
                return false;
            }
        
            static bool has_trailing_particle(std::u16string_view text) noexcept
            {
                // \b(không|chưa|à|ư|hở)\s*[.!]*$
                size_t end = text.size();
                while (end > 0 && (text[end - 1] == u'.' || text[end - 1] == u'!'))
                    --end;
                while (end > 0 && vnotch::is_whitespace(text[end - 1]))
                    --end;
        
                constexpr std::u16string_view kParticles[] = {
                    u"không", u"chưa", u"à", u"ư", u"hở"
                };
                for (std::u16string_view p : kParticles)
                {
                    if (end >= p.size())
                    {
                        size_t start = end - p.size();
                        if ((start == 0 || !is_word_char(text[start - 1])) &&
                            equals_unicode_icase(text.substr(start, p.size()), p))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        
            struct ViSubjectMap
            {
                std::u16string_view vi;
                std::u16string_view en;
            };
        
            static constexpr ViSubjectMap kViSubjects[] = {
                { u"các bạn", u"You" },
                { u"bạn", u"You" },
                { u"chúng tôi", u"We" },
                { u"chúng ta", u"We" },
                { u"tôi", u"I" },
                { u"họ", u"They" },
                { u"anh ấy", u"He" },
                { u"cô ấy", u"She" }
            };
        
            static bool match_vi_declarative_start(std::u16string_view text, std::u16string_view* out_en_subject = nullptr) noexcept
            {
                // ^\s*(các bạn|bạn|chúng tôi|chúng ta|tôi|họ|anh ấy|cô ấy)\s+(đang|đã|sẽ|không)\b
                size_t i = 0;
                while (i < text.size() && vnotch::is_whitespace(text[i])) ++i;
        
                constexpr std::u16string_view kAux[] = { u"đang", u"đã", u"sẽ", u"không" };
        
                for (const auto& subj : kViSubjects)
                {
                    if (i + subj.vi.size() <= text.size() &&
                        equals_unicode_icase(text.substr(i, subj.vi.size()), subj.vi))
                    {
                        size_t j = i + subj.vi.size();
                        if (j < text.size() && is_word_char(text[j])) continue;
                        size_t ws_start = j;
                        while (j < text.size() && vnotch::is_whitespace(text[j])) ++j;
                        if (j == ws_start) continue;
        
                        for (std::u16string_view aux : kAux)
                        {
                            if (match_word_at(text, j, aux))
                            {
                                if (out_en_subject) *out_en_subject = subj.en;
                                return true;
                            }
                        }
                    }
                }
                return false;
            }
        
            static bool match_en_declarative_start(std::u16string_view text) noexcept
            {
                // ^\s*(you|we|they|I|he|she|it)\s+(are|is|am|was|were|will|have|has|do not|does not|did not)\b
                size_t i = 0;
                while (i < text.size() && vnotch::is_whitespace(text[i])) ++i;
        
                constexpr std::u16string_view kEnSubjects[] = {
                    u"you", u"we", u"they", u"I", u"he", u"she", u"it"
                };
                constexpr std::u16string_view kEnVerbs[] = {
                    u"are", u"is", u"am", u"was", u"were", u"will", u"have", u"has",
                    u"do not", u"does not", u"did not"
                };
        
                for (std::u16string_view subj : kEnSubjects)
                {
                    if (i + subj.size() <= text.size() &&
                        equals_unicode_icase(text.substr(i, subj.size()), subj))
                    {
                        size_t j = i + subj.size();
                        if (j < text.size() && is_word_char(text[j])) continue;
                        size_t ws_start = j;
                        while (j < text.size() && vnotch::is_whitespace(text[j])) ++j;
                        if (j == ws_start) continue;
        
                        for (std::u16string_view verb : kEnVerbs)
                        {
                            if (match_word_at(text, j, verb))
                                return true;
                        }
                    }
                }
                return false;
            }
        
            static bool is_clear_statement_impl(std::u16string_view text, std::u16string_view resolved_source, std::u16string_view* out_en_subject = nullptr) noexcept
            {
                if (has_question_mark(text)) return false;
        
                if (resolved_source == u"vi")
                {
                    constexpr std::u16string_view kInterrogatives[] = {
                        u"ai", u"gì", u"sao", u"đâu", u"nào", u"bao nhiêu", u"mấy", u"liệu", u"hả", u"nhỉ"
                    };
                    if (contains_any_word(text, kInterrogatives) ||
                        has_co_khong_chua(text) ||
                        has_trailing_particle(text))
                    {
                        return false;
                    }
                    return match_vi_declarative_start(text, out_en_subject);
                }
        
                if (resolved_source == u"en")
                {
                    return match_en_declarative_start(text);
                }
        
                return false;
            }

        static int32_t vn_translation_is_clear_statement_impl(const char16_t* text_ptr, int32_t text_len,
    const char16_t* resolved_source_ptr, int32_t resolved_source_len,
    uint8_t* out_result) noexcept
        {
    if (!out_result) return VN_ERR_INVALID_ARG;
    *out_result = is_clear_statement_impl(
        vnotch::make_view(text_ptr, text_len),
        vnotch::make_view(resolved_source_ptr, resolved_source_len)) ? 1 : 0;
    return VN_OK;
}

        static int32_t vn_translation_preserves_statement_impl(const char16_t* text_ptr, int32_t text_len,
    const char16_t* resolved_source_ptr, int32_t resolved_source_len,
    const char16_t* translated_ptr, int32_t translated_len,
    uint8_t* out_result) noexcept
        {
    if (!out_result) return VN_ERR_INVALID_ARG;
    std::u16string_view text = vnotch::make_view(text_ptr, text_len);
    std::u16string_view source = vnotch::make_view(resolved_source_ptr, resolved_source_len);
    std::u16string_view translated = vnotch::make_view(translated_ptr, translated_len);

    bool ok = !is_clear_statement_impl(text, source) || !has_question_mark(translated);
    *out_result = ok ? 1 : 0;
    return VN_OK;
}

        static int32_t vn_translation_english_statement_subject_impl(const char16_t* text_ptr, int32_t text_len,
    const char16_t* resolved_source_ptr, int32_t resolved_source_len,
    const char16_t* target_ptr, int32_t target_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
        {
    if (!out_len) return VN_ERR_INVALID_ARG;
    *out_len = 0;

    std::u16string_view text = vnotch::make_view(text_ptr, text_len);
    std::u16string_view source = vnotch::make_view(resolved_source_ptr, resolved_source_len);
    std::u16string_view target = vnotch::make_view(target_ptr, target_len);

    if (target != u"en" || source != u"vi" || text.find(u'\n') != std::u16string_view::npos)
        return VN_OK;

    std::u16string_view subj;
    if (!is_clear_statement_impl(text, source, &subj) || subj.empty())
        return VN_OK;

    return vnotch::write_u16_out(subj, out_buf, out_cap, out_len);
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_translation_is_clear_statement(const char16_t* text_ptr, int32_t text_len,
    const char16_t* resolved_source_ptr, int32_t resolved_source_len,
    uint8_t* out_result) noexcept
{
    return vnotch::services::TranslationMeaningIntegrity::vn_translation_is_clear_statement_impl(text_ptr, text_len, resolved_source_ptr, resolved_source_len, out_result);
}

VN_EXPORT int32_t vn_translation_preserves_statement(const char16_t* text_ptr, int32_t text_len,
    const char16_t* resolved_source_ptr, int32_t resolved_source_len,
    const char16_t* translated_ptr, int32_t translated_len,
    uint8_t* out_result) noexcept
{
    return vnotch::services::TranslationMeaningIntegrity::vn_translation_preserves_statement_impl(text_ptr, text_len, resolved_source_ptr, resolved_source_len, translated_ptr, translated_len, out_result);
}

VN_EXPORT int32_t vn_translation_english_statement_subject(const char16_t* text_ptr, int32_t text_len,
    const char16_t* resolved_source_ptr, int32_t resolved_source_len,
    const char16_t* target_ptr, int32_t target_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    return vnotch::services::TranslationMeaningIntegrity::vn_translation_english_statement_subject_impl(text_ptr, text_len, resolved_source_ptr, resolved_source_len, target_ptr, target_len, out_buf, out_cap, out_len);
}

