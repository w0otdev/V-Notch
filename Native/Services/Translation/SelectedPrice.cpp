#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class SelectedPrice final
    {
    public:
        static constexpr std::u16string_view kSupportedCodes[] = {
                u"AED", u"ARS", u"AUD", u"BDT", u"BGN", u"BHD", u"BRL", u"CAD", u"CHF", u"CLP",
                u"CNY", u"COP", u"CZK", u"DKK", u"EGP", u"EUR", u"GBP", u"HKD", u"HUF", u"IDR",
                u"ILS", u"INR", u"ISK", u"JPY", u"KRW", u"KWD", u"LAK", u"LKR", u"MAD", u"MMK",
                u"MXN", u"MYR", u"NOK", u"NPR", u"NZD", u"PEN", u"PHP", u"PKR", u"PLN", u"QAR",
                u"RON", u"RSD", u"RUB", u"SAR", u"SEK", u"SGD", u"THB", u"TRY", u"TWD", u"UAH",
                u"USD", u"UZS", u"VND", u"ZAR"
            };
        
            static bool is_supported_code(std::u16string_view code) noexcept
            {
                if (code.size() != 3) return false;
                for (std::u16string_view c : kSupportedCodes)
                {
                    if (code == c) return true;
                }
                return false;
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
        
            static int32_t decimal_places_impl(std::u16string_view currency) noexcept
            {
                if (currency == u"VND" || currency == u"JPY" || currency == u"KRW" || currency == u"CLP")
                    return 0;
                if (currency == u"KWD" || currency == u"BHD")
                    return 3;
                return 2;
            }

        static int32_t vn_currency_supported_impl(const char16_t* code_ptr, int32_t code_len,
    uint8_t* out_supported)
        {
    if (!out_supported) return VN_ERR_INVALID_ARG;
    if (!code_ptr || code_len <= 0)
    {
        *out_supported = 0;
        return VN_OK;
    }
    *out_supported = is_supported_code(vnotch::make_view(code_ptr, code_len)) ? 1 : 0;
    return VN_OK;
}

        static int32_t vn_currency_decimal_places_impl(const char16_t* currency_ptr, int32_t currency_len,
    int32_t* out_places)
        {
    if (!out_places) return VN_ERR_INVALID_ARG;
    *out_places = decimal_places_impl(vnotch::make_view(currency_ptr, currency_len));
    return VN_OK;
}

        static int32_t vn_currency_for_language_impl(const char16_t* lang_ptr, int32_t lang_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len)
        {
    if (!out_len) return VN_ERR_INVALID_ARG;
    *out_len = 0;
    std::u16string_view l = vnotch::make_view(lang_ptr, lang_len);

    std::u16string_view code;
    if (l == u"vi") code = u"VND";
    else if (l == u"en") code = u"USD";
    else if (l == u"ja") code = u"JPY";
    else if (l == u"zh") code = u"CNY";
    else if (l == u"ko") code = u"KRW";
    else if (l == u"de" || l == u"fr" || l == u"es" || l == u"it" || l == u"nl" || l == u"el" ||
             l == u"fi" || l == u"et" || l == u"lv" || l == u"lt" || l == u"ga" || l == u"sk" ||
             l == u"sl" || l == u"ca") code = u"EUR";
    else if (l == u"pt") code = u"BRL";
    else if (l == u"ar") code = u"SAR";
    else if (l == u"ru" || l == u"ba") code = u"RUB";
    else if (l == u"bn") code = u"BDT";
    else if (l == u"hi" || l == u"gu" || l == u"kn" || l == u"ml" || l == u"mr" || l == u"or" ||
             l == u"pa" || l == u"ta") code = u"INR";
    else if (l == u"th") code = u"THB";
    else if (l == u"id" || l == u"jv" || l == u"su") code = u"IDR";
    else if (l == u"ms") code = u"MYR";
    else if (l == u"tr") code = u"TRY";
    else if (l == u"pl") code = u"PLN";
    else if (l == u"cs") code = u"CZK";
    else if (l == u"da") code = u"DKK";
    else if (l == u"no") code = u"NOK";
    else if (l == u"sv") code = u"SEK";
    else if (l == u"hu") code = u"HUF";
    else if (l == u"ro") code = u"RON";
    else if (l == u"uk") code = u"UAH";
    else if (l == u"he" || l == u"yi") code = u"ILS";
    else if (l == u"ur" || l == u"sd") code = u"PKR";
    else if (l == u"ne") code = u"NPR";
    else if (l == u"my") code = u"MMK";
    else if (l == u"lo") code = u"LAK";
    else if (l == u"si") code = u"LKR";
    else if (l == u"tl" || l == u"ceb" || l == u"ilo") code = u"PHP";
    else if (l == u"af" || l == u"zu" || l == u"xh" || l == u"ns") code = u"ZAR";
    else if (l == u"is") code = u"ISK";
    else if (l == u"sr") code = u"RSD";
    else if (l == u"uz") code = u"UZS";
    else if (l == u"cy" || l == u"gd") code = u"GBP";

    if (code.empty()) return VN_OK;
    return vnotch::write_u16_out(code, out_buf, out_cap, out_len);
}

        static int32_t vn_currency_resolve_token_impl(const char16_t* token_ptr, int32_t token_len,
    const char16_t* source_ptr, int32_t source_len,
    const char16_t* local_dollar_ptr, int32_t local_dollar_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len)
        {
    if (!out_len) return VN_ERR_INVALID_ARG;
    *out_len = 0;

    std::u16string_view t = vnotch::trim(vnotch::make_view(token_ptr, token_len));
    std::u16string_view source = vnotch::make_view(source_ptr, source_len);
    std::u16string_view local_dollar = vnotch::make_view(local_dollar_ptr, local_dollar_len);

    std::u16string_view code;
    if (t == u"$") code = local_dollar;
    else if (equals_unicode_icase(t, u"US$") || equals_unicode_icase(t, u"DOLLAR") ||
             equals_unicode_icase(t, u"DOLLARS") || equals_unicode_icase(t, u"BUCK") ||
             equals_unicode_icase(t, u"BUCKS") || equals_unicode_icase(t, u"ĐÔ") ||
             equals_unicode_icase(t, u"ĐÔ LA")) code = u"USD";
    else if (equals_unicode_icase(t, u"CA$")) code = u"CAD";
    else if (equals_unicode_icase(t, u"AU$")) code = u"AUD";
    else if (equals_unicode_icase(t, u"NZ$")) code = u"NZD";
    else if (equals_unicode_icase(t, u"HK$")) code = u"HKD";
    else if (equals_unicode_icase(t, u"S$")) code = u"SGD";
    else if (equals_unicode_icase(t, u"R$")) code = u"BRL";
    else if (t == u"€" || equals_unicode_icase(t, u"EURO") || equals_unicode_icase(t, u"EUROS")) code = u"EUR";
    else if (t == u"£" || equals_unicode_icase(t, u"POUND") || equals_unicode_icase(t, u"POUNDS")) code = u"GBP";
    else if (t == u"₫" || equals_unicode_icase(t, u"Đ") || equals_unicode_icase(t, u"ĐỒNG") ||
             equals_unicode_icase(t, u"DONG")) code = u"VND";
    else if (t == u"₩" || equals_unicode_icase(t, u"WON")) code = u"KRW";
    else if (t == u"₹" || equals_unicode_icase(t, u"RUPEE") || equals_unicode_icase(t, u"RUPEES")) code = u"INR";
    else if (t == u"฿" || equals_unicode_icase(t, u"BAHT")) code = u"THB";
    else if (t == u"₽" || equals_unicode_icase(t, u"RUBLE") || equals_unicode_icase(t, u"RUBLES")) code = u"RUB";
    else if (equals_unicode_icase(t, u"CN¥") || equals_unicode_icase(t, u"YUAN") || equals_unicode_icase(t, u"RMB")) code = u"CNY";
    else if (equals_unicode_icase(t, u"JP¥") || equals_unicode_icase(t, u"YEN")) code = u"JPY";
    else if (t == u"¥" || t == u"￥")
    {
        if (source == u"zh") code = u"CNY";
        else if (source == u"ja") code = u"JPY";
    }
    else if (t.size() == 3)
    {
        char16_t upper[3] = {
            vnotch::to_upper_ascii(t[0]),
            vnotch::to_upper_ascii(t[1]),
            vnotch::to_upper_ascii(t[2])
        };
        std::u16string_view u(upper, 3);
        for (std::u16string_view sc : kSupportedCodes)
        {
            if (u == sc)
            {
                code = sc;
                break;
            }
        }
    }

    if (code.empty()) return VN_OK;
    return vnotch::write_u16_out(code, out_buf, out_cap, out_len);
}

        static int32_t vn_currency_normalize_amount_impl(const char16_t* value_ptr, int32_t value_len,
    const char16_t* currency_ptr, int32_t currency_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len,
    uint8_t* out_valid)
        {
    if (!out_len || !out_valid) return VN_ERR_INVALID_ARG;
    *out_len = 0;
    *out_valid = 0;

    std::u16string_view raw = vnotch::make_view(value_ptr, value_len);
    std::u16string_view currency = vnotch::make_view(currency_ptr, currency_len);
    if (raw.empty() || raw.size() > 128) return VN_OK;

    std::u16string val(raw);
    for (char16_t& c : val)
    {
        if (c == u'\u00A0' || c == u'\u202F') c = u' ';
    }

    int32_t dot = -1;
    int32_t comma = -1;
    for (size_t i = 0; i < val.size(); ++i)
    {
        if (val[i] == u'.') dot = static_cast<int32_t>(i);
        else if (val[i] == u',') comma = static_cast<int32_t>(i);
    }

    int32_t max_places = decimal_places_impl(currency);
    char16_t decimal_mark = u'\0';
    if (dot >= 0 && comma >= 0)
    {
        decimal_mark = (dot > comma) ? u'.' : u',';
    }
    else
    {
        int32_t sep = std::max(dot, comma);
        if (sep >= 0)
        {
            int32_t decimals = static_cast<int32_t>(val.size()) - sep - 1;
            if (decimals == 3 && max_places == 3) return VN_OK;
            if (decimals == 1 || decimals == 2) decimal_mark = val[sep];
        }
    }

    std::u16string_view integer_part = val;
    std::u16string_view fraction_part;
    if (decimal_mark != u'\0')
    {
        size_t split = val.rfind(decimal_mark);
        integer_part = std::u16string_view(val).substr(0, split);
        fraction_part = std::u16string_view(val).substr(split + 1);
        if (static_cast<int32_t>(fraction_part.size()) > max_places) return VN_OK;
        for (char16_t c : fraction_part)
        {
            if (!vnotch::is_ascii_digit(c)) return VN_OK;
        }
        if (integer_part.find(decimal_mark) != std::u16string_view::npos) return VN_OK;
    }

    char16_t group_sep = u'\0';
    for (char16_t c : integer_part)
    {
        if (!vnotch::is_ascii_digit(c))
        {
            if (group_sep == u'\0') group_sep = c;
            else if (group_sep != c) return VN_OK;
        }
    }

    std::u16string clean_int;
    clean_int.reserve(integer_part.size());
    if (group_sep != u'\0')
    {
        if (group_sep != u'.' && group_sep != u',' && group_sep != u' ' && group_sep != u'\'')
            return VN_OK;

        size_t group_idx = 0;
        size_t start = 0;
        for (size_t i = 0; i <= integer_part.size(); ++i)
        {
            if (i == integer_part.size() || integer_part[i] == group_sep)
            {
                std::u16string_view g = integer_part.substr(start, i - start);
                if (group_idx == 0)
                {
                    if (g.size() < 1 || g.size() > 3) return VN_OK;
                }
                else
                {
                    if (g.size() != 3) return VN_OK;
                }
                for (char16_t c : g)
                {
                    if (!vnotch::is_ascii_digit(c)) return VN_OK;
                }
                clean_int.append(g);
                ++group_idx;
                start = i + 1;
            }
        }
    }
    else
    {
        if (integer_part.empty()) return VN_OK;
        clean_int.append(integer_part);
    }

    if (!fraction_part.empty())
    {
        clean_int.push_back(u'.');
        clean_int.append(fraction_part);
    }

    *out_valid = 1;
    return vnotch::write_u16_out(clean_int, out_buf, out_cap, out_len);
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_currency_supported(const char16_t* code_ptr, int32_t code_len,
    uint8_t* out_supported) noexcept
{
    try { return vnotch::services::SelectedPrice::vn_currency_supported_impl(code_ptr, code_len, out_supported);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_currency_decimal_places(const char16_t* currency_ptr, int32_t currency_len,
    int32_t* out_places) noexcept
{
    try { return vnotch::services::SelectedPrice::vn_currency_decimal_places_impl(currency_ptr, currency_len, out_places);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_currency_for_language(const char16_t* lang_ptr, int32_t lang_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::SelectedPrice::vn_currency_for_language_impl(lang_ptr, lang_len, out_buf, out_cap, out_len);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_currency_resolve_token(const char16_t* token_ptr, int32_t token_len,
    const char16_t* source_ptr, int32_t source_len,
    const char16_t* local_dollar_ptr, int32_t local_dollar_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::SelectedPrice::vn_currency_resolve_token_impl(token_ptr, token_len, source_ptr, source_len, local_dollar_ptr, local_dollar_len, out_buf, out_cap, out_len);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_currency_normalize_amount(const char16_t* value_ptr, int32_t value_len,
    const char16_t* currency_ptr, int32_t currency_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len,
    uint8_t* out_valid) noexcept
{
    try { return vnotch::services::SelectedPrice::vn_currency_normalize_amount_impl(value_ptr, value_len, currency_ptr, currency_len, out_buf, out_cap, out_len, out_valid);
 } catch (...) { return VN_ERR_FAIL; }
}

