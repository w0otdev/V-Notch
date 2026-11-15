#include "Parsing/Utf16Text.h"
#include "Parsing/Utf16Json.h"

using namespace vnotch::parsing;

VN_EXPORT int32_t vn_currency_supported(
    const char16_t* code_ptr, int32_t code_len, uint8_t* out_supported) noexcept;

VN_EXPORT int32_t vn_currency_resolve_token(
    const char16_t* token_ptr, int32_t token_len,
    const char16_t* source_ptr, int32_t source_len,
    const char16_t* local_dollar_ptr, int32_t local_dollar_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept;

VN_EXPORT int32_t vn_currency_normalize_amount(
    const char16_t* value_ptr, int32_t value_len,
    const char16_t* currency_ptr, int32_t currency_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len,
    uint8_t* out_valid) noexcept;
namespace {
constexpr std::u16string_view kIsoCurrencyCodes[54] = {
    u"AED", u"ARS", u"AUD", u"BDT", u"BGN", u"BHD", u"BRL", u"CAD", u"CHF", u"CLP",
    u"CNY", u"COP", u"CZK", u"DKK", u"EGP", u"EUR", u"GBP", u"HKD", u"HUF", u"IDR",
    u"ILS", u"INR", u"ISK", u"JPY", u"KRW", u"KWD", u"LAK", u"LKR", u"MAD", u"MMK",
    u"MXN", u"MYR", u"NOK", u"NPR", u"NZD", u"PEN", u"PHP", u"PKR", u"PLN", u"QAR",
    u"RON", u"RSD", u"RUB", u"SAR", u"SEK", u"SGD", u"THB", u"TRY", u"TWD", u"UAH",
    u"USD", u"UZS", u"VND", u"ZAR"
};

constexpr bool is_iso_currency_icase(std::u16string_view s) noexcept
{
    if (s.size() != 3) return false;
    const char16_t c0 = static_cast<char16_t>(s[0] & ~0x20u);
    const char16_t c1 = static_cast<char16_t>(s[1] & ~0x20u);
    const char16_t c2 = static_cast<char16_t>(s[2] & ~0x20u);
    if (c0 < u'A' || c0 > u'Z' || c1 < u'A' || c1 > u'Z' || c2 < u'A' || c2 > u'Z')
        return false;
    const char16_t upper[3] = {c0, c1, c2};
    const std::u16string_view up(upper, 3);
    for (std::u16string_view code : kIsoCurrencyCodes)
    {
        if (code == up) return true;
    }
    return false;
}

constexpr bool is_price_single_symbol(char16_t ch) noexcept
{
    return ch == u'$' || ch == 0x20ACu || ch == 0x00A3u || ch == 0x00A5u ||
           ch == 0xFFE5u || ch == 0x20ABu || ch == 0x0111u || ch == 0x0110u ||
           ch == 0x20A9u || ch == 0x20B9u || ch == 0x0E3Fu || ch == 0x20BDu;
}

size_t match_qualified_symbol_token(std::u16string_view s, size_t pos) noexcept
{
    const size_t rem = s.size() - pos;
    if (rem >= 3)
    {
        const std::u16string_view sub3 = s.substr(pos, 3);
        if (equals_ascii_icase(sub3, u"US$") ||
            equals_ascii_icase(sub3, u"CA$") ||
            equals_ascii_icase(sub3, u"AU$") ||
            equals_ascii_icase(sub3, u"NZ$") ||
            equals_ascii_icase(sub3, u"HK$"))
        {
            return 3;
        }
        if ((ascii_to_lower(sub3[0]) == u'c' && ascii_to_lower(sub3[1]) == u'n' && sub3[2] == 0x00A5u) ||
            (ascii_to_lower(sub3[0]) == u'j' && ascii_to_lower(sub3[1]) == u'p' && sub3[2] == 0x00A5u))
        {
            return 3;
        }
    }
    if (rem >= 2)
    {
        const std::u16string_view sub2 = s.substr(pos, 2);
        if (equals_ascii_icase(sub2, u"S$") || equals_ascii_icase(sub2, u"R$"))
            return 2;
    }
    return 0;
}

constexpr bool is_price_amount_tail(char16_t ch) noexcept
{
    return (ch >= u'0' && ch <= u'9') ||
           ch == u'.' || ch == u',' || ch == u'\'' ||
           ch == 0x00A0u || ch == 0x202Fu || ch == u' ';
}

size_t match_price_affix(std::u16string_view s, size_t pos) noexcept
{
    if (pos >= s.size()) return 0;
    const size_t qual_len = match_qualified_symbol_token(s, pos);
    if (qual_len > 0) return qual_len;

    if (is_unicode_letter(s[pos]))
    {
        size_t cur = pos;
        while (cur < s.size() && is_unicode_letter(s[cur])) ++cur;
        const size_t after_word1 = cur;
        size_t ws = cur;
        while (ws < s.size() && vnotch::is_unicode_whitespace(s[ws])) ++ws;
        if (ws > after_word1 && ws < s.size() && is_unicode_letter(s[ws]))
        {
            size_t cur2 = ws;
            while (cur2 < s.size() && is_unicode_letter(s[cur2])) ++cur2;
            return cur2 - pos;
        }
        return after_word1 - pos;
    }

    if (is_price_single_symbol(s[pos])) return 1;
    return 0;
}

std::u16string resolve_currency_token_helper(
    std::u16string_view token,
    std::u16string_view source_lang,
    std::u16string_view local_dollar) noexcept
{
    const std::u16string_view trimmed = vnotch::trim_view(token);
    const std::u16string_view ld = (trimmed == u"$") ? local_dollar : std::u16string_view{};
    char16_t buf[8]{};
    int32_t len = 0;
    if (vn_currency_resolve_token(
            trimmed.data(), static_cast<int32_t>(trimmed.size()),
            source_lang.data(), static_cast<int32_t>(source_lang.size()),
            ld.data(), static_cast<int32_t>(ld.size()),
            buf, 8, &len) == VN_OK && len > 0)
    {
        return std::u16string(buf, static_cast<size_t>(len));
    }
    return {};
}

constexpr bool is_dollar_qualified_iso(std::u16string_view code) noexcept
{
    return code == u"USD" || code == u"CAD" || code == u"AUD" ||
           code == u"NZD" || code == u"HKD" || code == u"SGD";
}


}

VN_EXPORT int32_t __stdcall vn_selected_price_has_conflicting_currency(
    const wchar_t* text_ptr, int32_t text_len,
    const wchar_t* source_ptr, int32_t source_len,
    const wchar_t* local_dollar_ptr, int32_t local_dollar_len,
    uint8_t* out_conflicting) noexcept
{
    try
    {
        if (out_conflicting == nullptr || text_len < 0 || source_len < 0 || local_dollar_len < 0)
            return VN_ERR_INVALID_ARG;
        *out_conflicting = 0;

        const std::u16string_view text = make_u16_view(text_ptr, text_len);
        const std::u16string_view source = make_u16_view(source_ptr, source_len);
        const std::u16string_view local_dollar = make_u16_view(local_dollar_ptr, local_dollar_len);
        const size_t n = text.size();

        for (size_t pos = 0; pos < n;)
        {
            if (pos > 0 && (is_unicode_letter(text[pos - 1]) || is_unicode_number(text[pos - 1])))
            {
                ++pos;
                continue;
            }

            size_t pfx_len = match_qualified_symbol_token(text, pos);
            if (pfx_len == 0 && pos + 3 <= n && is_iso_currency_icase(text.substr(pos, 3)))
                pfx_len = 3;
            if (pfx_len == 0 && is_price_single_symbol(text[pos]))
                pfx_len = 1;
            if (pfx_len == 0)
            {
                ++pos;
                continue;
            }

            size_t cur = pos + pfx_len;
            while (cur < n && vnotch::is_unicode_whitespace(text[cur])) ++cur;
            if (cur >= n || text[cur] < u'0' || text[cur] > u'9')
            {
                ++pos;
                continue;
            }
            ++cur;
            while (cur < n && is_price_amount_tail(text[cur])) ++cur;
            while (cur < n && vnotch::is_unicode_whitespace(text[cur])) ++cur;

            size_t sfx_len = 0;
            if (cur + 3 <= n && is_iso_currency_icase(text.substr(cur, 3)))
                sfx_len = 3;
            else if (cur < n && is_price_single_symbol(text[cur]))
                sfx_len = 1;

            const size_t match_end = cur + sfx_len;
            if (sfx_len == 0 ||
                (match_end < n && (is_unicode_letter(text[match_end]) || is_unicode_number(text[match_end]))))
            {
                ++pos;
                continue;
            }

            const std::u16string_view prefix = text.substr(pos, pfx_len);
            const std::u16string_view suffix = text.substr(cur, sfx_len);
            const std::u16string left = resolve_currency_token_helper(prefix, source, local_dollar);
            const std::u16string right = resolve_currency_token_helper(suffix, source, local_dollar);

            const bool qualified_dollar = (prefix == u"$" && is_dollar_qualified_iso(right));
            const bool qualified_yen = ((prefix == u"\x00A5" || prefix == u"\xFFE5") &&
                                        (right == u"JPY" || right == u"CNY"));
            if (!qualified_dollar && !qualified_yen && !left.empty() && !right.empty() && left != right)
            {
                *out_conflicting = 1;
                return VN_OK;
            }

            pos = match_end;
        }
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_selected_price_try_parse(
    const wchar_t* text_ptr, int32_t text_len,
    const wchar_t* source_ptr, int32_t source_len,
    const wchar_t* local_dollar_ptr, int32_t local_dollar_len,
    const wchar_t* override_ptr, int32_t override_len,
    wchar_t* out_amount_buf, int32_t amount_cap, int32_t* out_amount_len,
    wchar_t* out_currency_buf, int32_t currency_cap, int32_t* out_currency_len,
    uint8_t* out_assumed_currency, uint8_t* out_ok) noexcept
{
    try
    {
        if (out_amount_len == nullptr || out_currency_len == nullptr ||
            out_assumed_currency == nullptr || out_ok == nullptr ||
            text_len < 0 || source_len < 0 || local_dollar_len < 0 || override_len < 0)
        {
            return VN_ERR_INVALID_ARG;
        }
        *out_amount_len = 0;
        *out_currency_len = 0;
        *out_assumed_currency = 0;
        *out_ok = 0;

        if (text_len == 0 || text_len > 128) return VN_OK;
        const std::u16string_view text = make_u16_view(text_ptr, text_len);
        const std::u16string_view source = make_u16_view(source_ptr, source_len);
        const std::u16string_view local_dollar = make_u16_view(local_dollar_ptr, local_dollar_len);
        const std::u16string_view override_code = make_u16_view(override_ptr, override_len);
        const size_t n = text.size();

        size_t cur = 0;
        while (cur < n && vnotch::is_unicode_whitespace(text[cur])) ++cur;
        if (cur >= n) return VN_OK;

        std::u16string_view prefix{};
        const size_t pfx_len = match_price_affix(text, cur);
        if (pfx_len > 0)
        {
            prefix = vnotch::trim_view(text.substr(cur, pfx_len));
            cur += pfx_len;
            while (cur < n && vnotch::is_unicode_whitespace(text[cur])) ++cur;
        }

        if (cur >= n || text[cur] < u'0' || text[cur] > u'9') return VN_OK;
        const size_t amt_start = cur;
        ++cur;
        while (cur < n && is_price_amount_tail(text[cur])) ++cur;
        const std::u16string_view amount_raw = vnotch::trim_view(text.substr(amt_start, cur - amt_start));

        while (cur < n && vnotch::is_unicode_whitespace(text[cur])) ++cur;
        std::u16string_view suffix{};
        if (cur < n)
        {
            const size_t sfx_len = match_price_affix(text, cur);
            if (sfx_len == 0) return VN_OK;
            suffix = vnotch::trim_view(text.substr(cur, sfx_len));
            cur += sfx_len;
            while (cur < n && vnotch::is_unicode_whitespace(text[cur])) ++cur;
        }
        if (cur != n) return VN_OK;
        if (prefix.empty() && suffix.empty()) return VN_OK;

        std::u16string currency = resolve_currency_token_helper(
            !prefix.empty() ? prefix : suffix, source, local_dollar);
        const std::u16string explicit_currency = (suffix.size() == 3)
            ? resolve_currency_token_helper(suffix, source, local_dollar)
            : std::u16string{};

        const bool qualified_symbol =
            (prefix == u"$" && is_dollar_qualified_iso(explicit_currency)) ||
            ((prefix == u"\x00A5" || prefix == u"\xFFE5") &&
             (explicit_currency == u"JPY" || explicit_currency == u"CNY"));

        if (qualified_symbol) currency = explicit_currency;
        if (!qualified_symbol && !prefix.empty() && !suffix.empty() &&
            resolve_currency_token_helper(suffix, source, local_dollar) != currency)
        {
            return VN_OK;
        }

        uint8_t override_supported = 0;
        if (!override_code.empty())
        {
            vn_currency_supported(
                override_code.data(), static_cast<int32_t>(override_code.size()), &override_supported);
        }
        const bool overridden = override_supported != 0;

        const bool bare_ambiguous_symbol =
            prefix == u"$" || suffix == u"$" ||
            prefix == u"\x00A5" || prefix == u"\xFFE5" ||
            suffix == u"\x00A5" || suffix == u"\xFFE5";

        if (currency.empty() && !bare_ambiguous_symbol) return VN_OK;
        if (overridden) currency.assign(override_code.data(), override_code.size());
        if (currency.empty()) return VN_OK;

        char16_t norm_buf[140]{};
        int32_t norm_len = 0;
        uint8_t norm_valid = 0;
        if (vn_currency_normalize_amount(
                amount_raw.data(), static_cast<int32_t>(amount_raw.size()),
                currency.data(), static_cast<int32_t>(currency.size()),
                norm_buf, 140, &norm_len, &norm_valid) != VN_OK ||
            norm_valid == 0 || norm_len <= 0)
        {
            return VN_OK;
        }

        *out_amount_len = norm_len;
        *out_currency_len = static_cast<int32_t>(currency.size());
        *out_assumed_currency = (!overridden && !qualified_symbol && bare_ambiguous_symbol) ? 1 : 0;

        if (out_amount_buf == nullptr || amount_cap < norm_len ||
            out_currency_buf == nullptr || currency_cap < static_cast<int32_t>(currency.size()))
        {
            return VN_ERR_BUFFER_TOO_SMALL;
        }

        std::memcpy(out_amount_buf, norm_buf, static_cast<size_t>(norm_len) * sizeof(wchar_t));
        std::memcpy(out_currency_buf, currency.data(), currency.size() * sizeof(wchar_t));
        *out_ok = 1;
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

