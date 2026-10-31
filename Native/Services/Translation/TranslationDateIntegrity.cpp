#include "vnotch_common.h"
#include <algorithm>
#include <vector>

namespace vnotch::services
{

#pragma pack(push, 1)
struct VnWeekdaySpan
{
    int32_t index;
    int32_t length;
    int32_t day;
};
#pragma pack(pop)

class TranslationDateIntegrity final
{
    enum class LangId : int32_t
    {
        Unsupported = -1,
        En = 0,
        Vi = 1,
        Zh = 2,
        Ja = 3,
        Ko = 4,
        Fr = 5,
        Es = 6,
        De = 7,
        Ru = 8,
        Th = 9
    };

    static constexpr LangId ParseLang(std::u16string_view lang) noexcept
    {
        if (lang == u"en") return LangId::En;
        if (lang == u"vi") return LangId::Vi;
        if (lang == u"zh") return LangId::Zh;
        if (lang == u"ja") return LangId::Ja;
        if (lang == u"ko") return LangId::Ko;
        if (lang == u"fr") return LangId::Fr;
        if (lang == u"es") return LangId::Es;
        if (lang == u"de") return LangId::De;
        if (lang == u"ru") return LangId::Ru;
        if (lang == u"th") return LangId::Th;
        return LangId::Unsupported;
    }

    static bool IsLetter(char16_t ch) noexcept
    {
        if ((ch >= u'a' && ch <= u'z') || (ch >= u'A' && ch <= u'Z'))
        {
            return true;
        }
        if (ch < 0x80u)
        {
            return false;
        }
        WORD type = 0;
        WCHAR wch = static_cast<WCHAR>(ch);
        if (GetStringTypeW(CT_CTYPE1, &wch, 1, &type))
        {
            return (type & C1_ALPHA) != 0;
        }
        return false;
    }

    static bool CheckWordBoundary(std::u16string_view text, size_t start, size_t len) noexcept
    {
        if (start > 0 && IsLetter(text[start - 1]))
        {
            return false;
        }
        size_t end = start + len;
        if (end < text.size() && IsLetter(text[end]))
        {
            return false;
        }
        return true;
    }

    static bool MatchLiteralIgnoreCase(std::u16string_view text, size_t pos, std::u16string_view lit) noexcept
    {
        if (pos + lit.size() > text.size())
        {
            return false;
        }
        for (size_t i = 0; i < lit.size(); ++i)
        {
            if (vnotch::to_lower_invariant(text[pos + i]) != lit[i])
            {
                return false;
            }
        }
        return true;
    }

    static void AddLiteralMatches(
        std::u16string_view text,
        int32_t day,
        std::u16string_view pattern,
        bool require_boundary,
        std::vector<VnWeekdaySpan>& out)
    {
        if (text.size() < pattern.size())
        {
            return;
        }
        size_t limit = text.size() - pattern.size();
        for (size_t i = 0; i <= limit; )
        {
            if (MatchLiteralIgnoreCase(text, i, pattern) &&
                (!require_boundary || CheckWordBoundary(text, i, pattern.size())))
            {
                out.push_back({ static_cast<int32_t>(i), static_cast<int32_t>(pattern.size()), day });
                i += pattern.size();
            }
            else
            {
                ++i;
            }
        }
    }

public:
    static bool IsSupportedLanguage(std::u16string_view lang) noexcept
    {
        return ParseLang(lang) != LangId::Unsupported;
    }

    static void ExtractSpans(std::u16string_view text, std::u16string_view lang, std::vector<VnWeekdaySpan>& out)
    {
        out.clear();
        LangId id = ParseLang(lang);
        if (id == LangId::Unsupported || text.empty())
        {
            return;
        }

        switch (id)
        {
            case LangId::En:
            {
                static constexpr std::u16string_view kDays[7] = {
                    u"sunday", u"monday", u"tuesday", u"wednesday", u"thursday", u"friday", u"saturday"
                };
                for (int32_t d = 0; d < 7; ++d) AddLiteralMatches(text, d, kDays[d], true, out);
                break;
            }
            case LangId::Vi:
            {
                static constexpr std::u16string_view kDays[7] = {
                    u"ch\u1EE7 nh\u1EADt", u"th\u1EE9 hai", u"th\u1EE9 ba", u"th\u1EE9 t\u01B0", u"th\u1EE9 n\u0103m", u"th\u1EE9 s\u00E1u", u"th\u1EE9 b\u1EA3y"
                };
                for (int32_t d = 0; d < 7; ++d) AddLiteralMatches(text, d, kDays[d], true, out);
                break;
            }
            case LangId::Ko:
            {
                static constexpr std::u16string_view kDays[7] = {
                    u"\uC77C\uC694\uC77C", u"\uC6D4\uC694\uC77C", u"\uD654\uC694\uC77C", u"\uC218\uC694\uC77C", u"\uBAA9\uC694\uC77C", u"\uAE08\uC694\uC77C", u"\uD1A0\uC694\uC77C"
                };
                for (int32_t d = 0; d < 7; ++d) AddLiteralMatches(text, d, kDays[d], false, out);
                break;
            }
            case LangId::Fr:
            {
                static constexpr std::u16string_view kDays[7] = {
                    u"dimanche", u"lundi", u"mardi", u"mercredi", u"jeudi", u"vendredi", u"samedi"
                };
                for (int32_t d = 0; d < 7; ++d) AddLiteralMatches(text, d, kDays[d], true, out);
                break;
            }
            case LangId::Es:
            {
                static constexpr std::u16string_view kDays[7] = {
                    u"domingo", u"lunes", u"martes", u"mi\u00E9rcoles", u"jueves", u"viernes", u"s\u00E1bado"
                };
                for (int32_t d = 0; d < 7; ++d) AddLiteralMatches(text, d, kDays[d], true, out);
                break;
            }
            case LangId::De:
            {
                static constexpr std::u16string_view kDays[7] = {
                    u"sonntag", u"montag", u"dienstag", u"mittwoch", u"donnerstag", u"freitag", u"samstag"
                };
                for (int32_t d = 0; d < 7; ++d) AddLiteralMatches(text, d, kDays[d], true, out);
                break;
            }
            case LangId::Th:
            {
                static constexpr std::u16string_view kDays[7] = {
                    u"\u0E27\u0E31\u0E19\u0E2D\u0E32\u0E17\u0E34\u0E15\u0E22\u0E4C",
                    u"\u0E27\u0E31\u0E19\u0E08\u0E31\u0E19\u0E17\u0E23\u0E4C",
                    u"\u0E27\u0E31\u0E19\u0E2D\u0E31\u0E07\u0E04\u0E32\u0E23",
                    u"\u0E27\u0E31\u0E19\u0E1E\u0E38\u0E18",
                    u"\u0E27\u0E31\u0E19\u0E1E\u0E24\u0E2B\u0E31\u0E2A\u0E1A\u0E14\u0E35",
                    u"\u0E27\u0E31\u0E19\u0E28\u0E38\u0E01\u0E23\u0E4C",
                    u"\u0E27\u0E31\u0E19\u0E40\u0E2A\u0E32\u0E23\u0E4C"
                };
                for (int32_t d = 0; d < 7; ++d) AddLiteralMatches(text, d, kDays[d], false, out);
                break;
            }
            case LangId::Zh:
            {
                // (?:周|星期|礼拜)[日天|一|二|三|四|五|六]
                static constexpr char16_t kDayChars[7] = {
                    0, u'\u4E00', u'\u4E8C', u'\u4E09', u'\u56DB', u'\u4E94', u'\u516D'
                };
                for (int32_t d = 0; d < 7; ++d)
                {
                    for (size_t i = 0; i < text.size(); )
                    {
                        size_t prefix_len = 0;
                        if (text[i] == u'\u5468') prefix_len = 1;
                        else if (i + 1 < text.size() &&
                                 ((text[i] == u'\u661F' && text[i + 1] == u'\u671F') ||
                                  (text[i] == u'\u793C' && text[i + 1] == u'\u62DC')))
                        {
                            prefix_len = 2;
                        }
                        if (prefix_len > 0 && i + prefix_len < text.size())
                        {
                            char16_t suffix = text[i + prefix_len];
                            bool match = (d == 0) ? (suffix == u'\u65E5' || suffix == u'\u5929') : (suffix == kDayChars[d]);
                            if (match)
                            {
                                int32_t total = static_cast<int32_t>(prefix_len + 1);
                                out.push_back({ static_cast<int32_t>(i), total, d });
                                i += static_cast<size_t>(total);
                                continue;
                            }
                        }
                        ++i;
                    }
                }
                break;
            }
            case LangId::Ja:
            {
                // [日|月|火|水|木|金|土]曜(?:日)?
                static constexpr char16_t kFirst[7] = {
                    u'\u65E5', u'\u6708', u'\u706B', u'\u6C34', u'\u6728', u'\u91D1', u'\u571F'
                };
                for (int32_t d = 0; d < 7; ++d)
                {
                    for (size_t i = 0; i + 1 < text.size(); )
                    {
                        if (text[i] == kFirst[d] && text[i + 1] == u'\u66DC')
                        {
                            size_t len = (i + 2 < text.size() && text[i + 2] == u'\u65E5') ? 3 : 2;
                            out.push_back({ static_cast<int32_t>(i), static_cast<int32_t>(len), d });
                            i += len;
                        }
                        else
                        {
                            ++i;
                        }
                    }
                }
                break;
            }
            case LangId::Ru:
            {
                // 0: воскресень[еяю]
                // 1: понедельник(?:а|у|ом)?
                // 2: вторник(?:а|у|ом)?
                // 3: сред[ауы]
                // 4: четверг(?:а|у|ом)?
                // 5: пятниц[ауы]
                // 6: суббот[ауы]
                static constexpr std::u16string_view kStems[7] = {
                    u"\u0432\u043E\u0441\u043A\u0440\u0435\u0441\u0435\u043D\u044C",
                    u"\u043F\u043E\u043D\u0435\u0434\u0435\u043B\u044C\u043D\u0438\u043A",
                    u"\u0432\u0442\u043E\u0440\u043D\u0438\u043A",
                    u"\u0441\u0440\u0435\u0434",
                    u"\u0447\u0435\u0442\u0432\u0435\u0440\u0433",
                    u"\u043F\u044F\u0442\u043D\u0438\u0446",
                    u"\u0441\u0443\u0431\u0431\u043E\u0442"
                };
                for (int32_t d = 0; d < 7; ++d)
                {
                    std::u16string_view stem = kStems[d];
                    if (text.size() < stem.size()) continue;
                    for (size_t i = 0; i + stem.size() <= text.size(); )
                    {
                        if (!MatchLiteralIgnoreCase(text, i, stem))
                        {
                            ++i;
                            continue;
                        }
                        size_t end_pos = i + stem.size();
                        size_t matched_len = 0;
                        if (d == 0)
                        {
                            if (end_pos < text.size())
                            {
                                char16_t c = vnotch::to_lower_invariant(text[end_pos]);
                                if (c == u'\u0435' || c == u'\u044F' || c == u'\u044E')
                                    matched_len = stem.size() + 1;
                            }
                        }
                        else if (d == 1 || d == 2 || d == 4)
                        {
                            // try +2 ("ом"), then +1 ("а" or "у"), then +0, checking boundary
                            if (end_pos + 1 < text.size() &&
                                vnotch::to_lower_invariant(text[end_pos]) == u'\u043E' &&
                                vnotch::to_lower_invariant(text[end_pos + 1]) == u'\u043C' &&
                                CheckWordBoundary(text, i, stem.size() + 2))
                            {
                                matched_len = stem.size() + 2;
                            }
                            else if (end_pos < text.size() &&
                                     (vnotch::to_lower_invariant(text[end_pos]) == u'\u0430' ||
                                      vnotch::to_lower_invariant(text[end_pos]) == u'\u0443') &&
                                     CheckWordBoundary(text, i, stem.size() + 1))
                            {
                                matched_len = stem.size() + 1;
                            }
                            else if (CheckWordBoundary(text, i, stem.size()))
                            {
                                matched_len = stem.size();
                            }
                        }
                        else
                        {
                            if (end_pos < text.size())
                            {
                                char16_t c = vnotch::to_lower_invariant(text[end_pos]);
                                if (c == u'\u0430' || c == u'\u0443' || c == u'\u044B')
                                    matched_len = stem.size() + 1;
                            }
                        }

                        if (matched_len > 0 && CheckWordBoundary(text, i, matched_len))
                        {
                            out.push_back({ static_cast<int32_t>(i), static_cast<int32_t>(matched_len), d });
                            i += matched_len;
                        }
                        else
                        {
                            ++i;
                        }
                    }
                }
                break;
            }
            default:
                break;
        }
    }

    static bool PreservesWeekdays(
        std::u16string_view original,
        std::u16string_view source,
        std::u16string_view translated,
        std::u16string_view target)
    {
        if (!IsSupportedLanguage(source) || !IsSupportedLanguage(target))
        {
            return true;
        }
        std::vector<VnWeekdaySpan> a;
        std::vector<VnWeekdaySpan> b;
        ExtractSpans(original, source, a);
        ExtractSpans(translated, target, b);
        if (a.size() != b.size())
        {
            return false;
        }
        auto cmp = [](const VnWeekdaySpan& lhs, const VnWeekdaySpan& rhs) noexcept
        {
            return lhs.index < rhs.index;
        };
        std::stable_sort(a.begin(), a.end(), cmp);
        std::stable_sort(b.begin(), b.end(), cmp);
        for (size_t i = 0; i < a.size(); ++i)
        {
            if (a[i].day != b[i].day)
            {
                return false;
            }
        }
        return true;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_translation_date_preserves_weekdays(
    const char16_t* orig_ptr, int32_t orig_len,
    const char16_t* src_ptr, int32_t src_len,
    const char16_t* trans_ptr, int32_t trans_len,
    const char16_t* tgt_ptr, int32_t tgt_len,
    uint8_t* out_preserved) noexcept
{
    if (out_preserved == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    try
    {
        bool ok = vnotch::services::TranslationDateIntegrity::PreservesWeekdays(
            vnotch::make_u16_view(orig_ptr, orig_len),
            vnotch::make_u16_view(src_ptr, src_len),
            vnotch::make_u16_view(trans_ptr, trans_len),
            vnotch::make_u16_view(tgt_ptr, tgt_len));
        *out_preserved = ok ? 1u : 0u;
        return VN_OK;
    }
    catch (...)
    {
        *out_preserved = 0u;
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_translation_date_extract_weekdays(
    const char16_t* text_ptr, int32_t text_len,
    const char16_t* lang_ptr, int32_t lang_len,
    uint8_t sort_by_index,
    vnotch::services::VnWeekdaySpan* out_spans,
    int32_t out_cap,
    int32_t* out_count) noexcept
{
    if (out_count == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    try
    {
        std::vector<vnotch::services::VnWeekdaySpan> spans;
        vnotch::services::TranslationDateIntegrity::ExtractSpans(
            vnotch::make_u16_view(text_ptr, text_len),
            vnotch::make_u16_view(lang_ptr, lang_len),
            spans);
        if (sort_by_index != 0)
        {
            std::stable_sort(spans.begin(), spans.end(),
                [](const vnotch::services::VnWeekdaySpan& lhs, const vnotch::services::VnWeekdaySpan& rhs) noexcept
                {
                    return lhs.index < rhs.index;
                });
        }
        int32_t count = static_cast<int32_t>(spans.size());
        *out_count = count;
        if (count == 0)
        {
            return VN_OK;
        }
        if (out_spans == nullptr || out_cap < count)
        {
            return VN_ERR_BUFFER_TOO_SMALL;
        }
        std::memcpy(out_spans, spans.data(), static_cast<size_t>(count) * sizeof(vnotch::services::VnWeekdaySpan));
        return VN_OK;
    }
    catch (...)
    {
        *out_count = 0;
        return VN_ERR_FAIL;
    }
}

} // extern "C"
