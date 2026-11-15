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
namespace {

constexpr std::array<std::u16string_view, 12> kEnOnes = {
    u"Twelve", u"One", u"Two", u"Three", u"Four", u"Five",
    u"Six", u"Seven", u"Eight", u"Nine", u"Ten", u"Eleven"
};

constexpr std::array<std::u16string_view, 10> kEnTeens = {
    u"Ten", u"Eleven", u"Twelve", u"Thirteen", u"Fourteen",
    u"Fifteen", u"Sixteen", u"Seventeen", u"Eighteen", u"Nineteen"
};

constexpr std::array<std::u16string_view, 6> kEnTens = {
    u"", u"", u"Twenty", u"Thirty", u"Forty", u"Fifty"
};

constexpr std::array<std::u16string_view, 10> kViUnits = {
    u"không", u"một", u"hai", u"ba", u"bốn",
    u"năm", u"sáu", u"bảy", u"tám", u"chín"
};

constexpr std::array<std::u16string_view, 30> kEsSmall = {
    u"cero", u"uno", u"dos", u"tres", u"cuatro", u"cinco", u"seis", u"siete", u"ocho", u"nueve",
    u"diez", u"once", u"doce", u"trece", u"catorce", u"quince", u"dieciséis", u"diecisiete", u"dieciocho", u"diecinueve",
    u"veinte", u"veintiuno", u"veintidós", u"veintitrés", u"veinticuatro", u"veinticinco", u"veintiséis", u"veintisiete", u"veintiocho", u"veintinueve"
};

constexpr std::array<std::u16string_view, 6> kEsTens = {
    u"", u"", u"veinte", u"treinta", u"cuarenta", u"cincuenta"
};

constexpr std::array<std::u16string_view, 20> kFrSmall = {
    u"zéro", u"un", u"deux", u"trois", u"quatre", u"cinq", u"six", u"sept", u"huit", u"neuf",
    u"dix", u"onze", u"douze", u"treize", u"quatorze", u"quinze", u"seize", u"dix-sept", u"dix-huit", u"dix-neuf"
};

constexpr std::array<std::u16string_view, 6> kFrTens = {
    u"", u"", u"vingt", u"trente", u"quarante", u"cinquante"
};

constexpr std::array<std::u16string_view, 20> kDeSmall = {
    u"null", u"eins", u"zwei", u"drei", u"vier", u"fünf", u"sechs", u"sieben", u"acht", u"neun",
    u"zehn", u"elf", u"zwölf", u"dreizehn", u"vierzehn", u"fünfzehn", u"sechzehn", u"siebzehn", u"achtzehn", u"neunzehn"
};

constexpr std::array<std::u16string_view, 6> kDeTens = {
    u"", u"", u"zwanzig", u"dreißig", u"vierzig", u"fünfzig"
};

constexpr std::array<std::u16string_view, 10> kJaDigits = {
    u"零", u"一", u"二", u"三", u"四", u"五", u"六", u"七", u"八", u"九"
};

constexpr std::array<std::u16string_view, 60> kHiNumbers = {
    u"शून्य", u"एक", u"दो", u"तीन", u"चार", u"पाँच", u"छह", u"सात", u"आठ", u"नौ",
    u"दस", u"ग्यारह", u"बारह", u"तेरह", u"चौदह", u"पंद्रह", u"सोलह", u"सत्रह", u"अठारह", u"उन्नीस",
    u"बीस", u"इक्कीस", u"बाईस", u"तेईस", u"चौबीस", u"पच्चीस", u"छब्बीस", u"सत्ताईस", u"अट्ठाईस", u"उनतीस",
    u"तीस", u"इकतीस", u"बत्तीस", u"तैंतीस", u"चौंतीस", u"पैंतीस", u"छत्तीस", u"सैंतीस", u"अड़तीस", u"उनतालीस",
    u"चालीस", u"इकतालीस", u"बयालीस", u"तैंतालीस", u"चवालीस", u"पैंतालीस", u"छियालीस", u"सैंतालीस", u"अड़तालीस", u"उनचास",
    u"पचास", u"इक्यावन", u"बावन", u"तिरपन", u"चौवन", u"पचपन", u"छप्पन", u"सत्तावन", u"अट्ठावन", u"उनसठ"
};

void AppendVietnameseNumber(Utf16Writer& w, int32_t value) noexcept
{
    if (value < 10)
    {
        w.Append(kViUnits[static_cast<size_t>(value)]);
        return;
    }
    if (value < 20)
    {
        if (value == 10)
        {
            w.Append(u"mười");
            return;
        }
        w.Append(u"mười ");
        w.Append(value == 15 ? u"lăm" : kViUnits[static_cast<size_t>(value - 10)]);
        return;
    }

    const int32_t tens = value / 10;
    const int32_t unit = value % 10;
    w.Append(kViUnits[static_cast<size_t>(tens)]);
    if (unit == 0)
    {
        w.Append(u" mươi");
        return;
    }
    w.Append(u" mươi ");
    if (unit == 1) w.Append(u"mốt");
    else if (unit == 5) w.Append(u"lăm");
    else w.Append(kViUnits[static_cast<size_t>(unit)]);
}

void AppendSpanishNumber(Utf16Writer& w, int32_t value) noexcept
{
    if (value < static_cast<int32_t>(kEsSmall.size()))
    {
        w.Append(kEsSmall[static_cast<size_t>(value)]);
        return;
    }
    const int32_t unit = value % 10;
    w.Append(kEsTens[static_cast<size_t>(value / 10)]);
    if (unit != 0)
    {
        w.Append(u" y ");
        w.Append(kEsSmall[static_cast<size_t>(unit)]);
    }
}

void AppendFrenchNumber(Utf16Writer& w, int32_t value, bool feminine_one) noexcept
{
    if (value < static_cast<int32_t>(kFrSmall.size()))
    {
        if (feminine_one && value == 1)
            w.Append(u"une");
        else
            w.Append(kFrSmall[static_cast<size_t>(value)]);
        return;
    }
    const int32_t unit = value % 10;
    w.Append(kFrTens[static_cast<size_t>(value / 10)]);
    if (unit == 0)
        return;
    if (unit == 1)
    {
        w.Append(feminine_one ? u"-et-une" : u"-et-un");
    }
    else
    {
        w.AppendChar(u'-');
        w.Append(kFrSmall[static_cast<size_t>(unit)]);
    }
}

void AppendGermanNumber(Utf16Writer& w, int32_t value) noexcept
{
    if (value < static_cast<int32_t>(kDeSmall.size()))
    {
        w.Append(kDeSmall[static_cast<size_t>(value)]);
        return;
    }
    const int32_t unit = value % 10;
    if (unit == 0)
    {
        w.Append(kDeTens[static_cast<size_t>(value / 10)]);
        return;
    }
    w.Append(unit == 1 ? u"ein" : kDeSmall[static_cast<size_t>(unit)]);
    w.Append(u"und");
    w.Append(kDeTens[static_cast<size_t>(value / 10)]);
}

void AppendJapaneseNumber(Utf16Writer& w, int32_t value) noexcept
{
    if (value < 10)
    {
        w.Append(kJaDigits[static_cast<size_t>(value)]);
        return;
    }
    const int32_t tens = value / 10;
    const int32_t unit = value % 10;
    if (tens > 1)
        w.Append(kJaDigits[static_cast<size_t>(tens)]);
    w.AppendChar(u'十');
    if (unit != 0)
        w.Append(kJaDigits[static_cast<size_t>(unit)]);
}

void AppendEnglishMinute(Utf16Writer& w, int32_t minute, std::u16string_view oclock) noexcept
{
    if (minute == 0)
    {
        w.Append(oclock);
        return;
    }
    if (minute < 10)
    {
        w.Append(u"Oh ");
        w.Append(kEnOnes[static_cast<size_t>(minute)]);
        return;
    }
    if (minute < 20)
    {
        w.Append(kEnTeens[static_cast<size_t>(minute - 10)]);
        return;
    }
    w.Append(kEnTens[static_cast<size_t>(minute / 10)]);
    const int32_t unit = minute % 10;
    if (unit != 0)
    {
        w.AppendChar(u' ');
        w.Append(kEnOnes[static_cast<size_t>(unit)]);
    }
}

}

VN_EXPORT int32_t __stdcall vn_word_clock_format_time(
    const wchar_t* lang,
    int32_t lang_len,
    int32_t hour24,
    int32_t minute,
    const wchar_t* prefix,
    int32_t prefix_len,
    const wchar_t* oclock,
    int32_t oclock_len,
    wchar_t* out_buf,
    int32_t cap,
    int32_t* out_len) noexcept
{
    if (!out_buf || cap <= 0 || !out_len || lang_len < 0 || prefix_len < 0 || oclock_len < 0)
        return VN_ERR_INVALID_ARG;

    *out_len = 0;

    const std::u16string_view lang_sv = (lang && lang_len > 0)
        ? std::u16string_view(reinterpret_cast<const char16_t*>(lang), static_cast<size_t>(lang_len))
        : std::u16string_view{};
    const std::u16string_view prefix_sv = (prefix && prefix_len > 0)
        ? std::u16string_view(reinterpret_cast<const char16_t*>(prefix), static_cast<size_t>(prefix_len))
        : std::u16string_view{};
    const std::u16string_view oclock_sv = (oclock && oclock_len > 0)
        ? std::u16string_view(reinterpret_cast<const char16_t*>(oclock), static_cast<size_t>(oclock_len))
        : std::u16string_view{};

    const int32_t h24 = ((hour24 % 24) + 24) % 24;
    const int32_t min_clamped = std::clamp(minute, 0, 59);
    int32_t hour12 = h24 % 12;
    if (hour12 == 0)
        hour12 = 12;

    Utf16Writer w{ reinterpret_cast<char16_t*>(out_buf), cap };

    if (lang_sv == u"vi")
    {
        w.Append(prefix_sv);
        w.AppendChar(u'\n');
        AppendVietnameseNumber(w, hour12);
        w.Append(u" giờ");
        if (min_clamped != 0)
        {
            w.AppendChar(u'\n');
            if (min_clamped < 10)
                w.Append(u"lẻ ");
            AppendVietnameseNumber(w, min_clamped);
        }
    }
    else if (lang_sv == u"es")
    {
        w.Append(hour12 == 1 ? u"Es la" : prefix_sv);
        w.AppendChar(u'\n');
        if (hour12 == 1)
            w.Append(u"una");
        else
            AppendSpanishNumber(w, hour12);
        w.AppendChar(u'\n');
        if (min_clamped == 0)
        {
            w.Append(oclock_sv);
        }
        else
        {
            w.Append(u"y ");
            AppendSpanishNumber(w, min_clamped);
        }
    }
    else if (lang_sv == u"fr")
    {
        w.Append(prefix_sv);
        w.AppendChar(u'\n');
        if (hour12 == 1)
        {
            w.Append(u"une heure");
        }
        else
        {
            AppendFrenchNumber(w, hour12, false);
            w.Append(u" heures");
        }
        w.AppendChar(u'\n');
        if (min_clamped == 0)
            w.Append(oclock_sv);
        else
            AppendFrenchNumber(w, min_clamped, true);
    }
    else if (lang_sv == u"de")
    {
        w.Append(prefix_sv);
        w.AppendChar(u'\n');
        if (hour12 == 1)
            w.Append(u"ein");
        else
            AppendGermanNumber(w, hour12);
        w.Append(u" Uhr");
        if (min_clamped != 0)
        {
            w.AppendChar(u'\n');
            AppendGermanNumber(w, min_clamped);
        }
    }
    else if (lang_sv == u"ja")
    {
        w.Append(prefix_sv);
        w.AppendChar(u'\n');
        if (min_clamped == 0)
        {
            w.Append(oclock_sv);
            AppendJapaneseNumber(w, hour12);
            w.AppendChar(u'時');
        }
        else
        {
            AppendJapaneseNumber(w, hour12);
            w.AppendChar(u'時');
            w.AppendChar(u'\n');
            AppendJapaneseNumber(w, min_clamped);
            w.AppendChar(u'分');
        }
    }
    else if (lang_sv == u"hi")
    {
        w.Append(prefix_sv);
        w.AppendChar(u'\n');
        w.Append(kHiNumbers[static_cast<size_t>(hour12)]);
        if (min_clamped == 0)
        {
            w.AppendChar(u' ');
            w.Append(oclock_sv);
        }
        else
        {
            w.Append(u" बजकर\n");
            w.Append(kHiNumbers[static_cast<size_t>(min_clamped)]);
            w.Append(u" मिनट");
        }
    }
    else
    {
        w.Append(prefix_sv);
        w.AppendChar(u'\n');
        w.Append(kEnOnes[static_cast<size_t>(h24 % 12)]);
        w.AppendChar(u'\n');
        AppendEnglishMinute(w, min_clamped, oclock_sv);
    }

    if (w.overflow)
        return VN_ERR_BUFFER_TOO_SMALL;

    *out_len = w.len;
    return VN_OK;
}

