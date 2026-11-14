#include "vnotch_ext_ui.h"

#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string_view>

namespace {

constexpr double ClampF64(double value, double min_val, double max_val) noexcept
{
    if (value < min_val) return min_val;
    if (value > max_val) return max_val;
    return value;
}

inline double MathF64Max(double a, double b) noexcept
{
    if (std::isnan(a) || std::isnan(b))
        return std::numeric_limits<double>::quiet_NaN();
    return a > b ? a : b;
}

inline double MathF64Min(double a, double b) noexcept
{
    if (std::isnan(a) || std::isnan(b))
        return std::numeric_limits<double>::quiet_NaN();
    return a < b ? a : b;
}

inline int32_t UncheckedRoundToInt32(double value) noexcept
{
    if (!std::isfinite(value))
        return std::numeric_limits<int32_t>::min();
    const double rounded = std::rint(value);
    if (rounded < -2147483648.0 || rounded > 2147483647.0)
        return std::numeric_limits<int32_t>::min();
    return static_cast<int32_t>(rounded);
}

constexpr bool IsUtf16WhiteSpace(char16_t ch) noexcept
{
    return (ch >= 0x0009 && ch <= 0x000D) ||
           ch == 0x0020 ||
           ch == 0x0085 ||
           ch == 0x00A0 ||
           ch == 0x1680 ||
           (ch >= 0x2000 && ch <= 0x200A) ||
           ch == 0x2028 ||
           ch == 0x2029 ||
           ch == 0x202F ||
           ch == 0x205F ||
           ch == 0x3000;
}

constexpr bool IsAsciiHexDigit(char16_t ch) noexcept
{
    return (ch >= u'0' && ch <= u'9') ||
           (ch >= u'a' && ch <= u'f') ||
           (ch >= u'A' && ch <= u'F');
}

constexpr bool IsAsciiAlpha(char16_t ch) noexcept
{
    return (ch >= u'a' && ch <= u'z') || (ch >= u'A' && ch <= u'Z');
}

constexpr char16_t ToAsciiLower(char16_t ch) noexcept
{
    return (ch >= u'A' && ch <= u'Z') ? static_cast<char16_t>(ch + 32) : ch;
}

constexpr bool EqualsAsciiIgnoreCase(std::u16string_view a, std::u16string_view b) noexcept
{
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i)
    {
        if (ToAsciiLower(a[i]) != ToAsciiLower(b[i]))
            return false;
    }
    return true;
}

// ---------------------------------------------------------------------------
// WordClock UTF-16 Stack Formatter
// ---------------------------------------------------------------------------

struct Utf16Writer
{
    char16_t* buf;
    int32_t cap;
    int32_t len = 0;
    bool overflow = false;

    void Append(std::u16string_view sv) noexcept
    {
        if (overflow || sv.empty()) return;
        if (sv.size() > static_cast<size_t>(cap - len))
        {
            overflow = true;
            return;
        }
        std::memcpy(buf + len, sv.data(), sv.size() * sizeof(char16_t));
        len += static_cast<int32_t>(sv.size());
    }

    void AppendChar(char16_t ch) noexcept
    {
        if (overflow) return;
        if (len >= cap)
        {
            overflow = true;
            return;
        }
        buf[len++] = ch;
    }
};

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

// ---------------------------------------------------------------------------
// DesktopEdgeReveal Obscuration Helpers
// ---------------------------------------------------------------------------

struct DesktopObscureProbeCtx
{
    HWND notch_hwnd;
    DWORD my_pid;
    RECT notch_rect;
    uint8_t obscured;
};

bool IsWindowCloakedLocal(HWND hwnd) noexcept
{
    int cloaked = 0;
    return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(int)) == S_OK && cloaked != 0;
}

bool IsBlockedDesktopClassLocal(HWND hwnd) noexcept
{
    wchar_t buffer[160]{};
    const int len = GetClassNameW(hwnd, buffer, 160);
    if (len <= 0)
        return false;

    const std::u16string_view class_name(reinterpret_cast<const char16_t*>(buffer), static_cast<size_t>(len));

    static constexpr std::array<std::u16string_view, 16> kExact = {
        u"Progman",
        u"WorkerW",
        u"Shell_TrayWnd",
        u"Shell_SecondaryTrayWnd",
        u"NotifyIconOverflowWindow",
        u"TaskListThumbnailWnd",
        u"MultitaskingViewFrame",
        u"ForegroundStaging",
        u"XamlExplorerHostIslandWindow",
        u"Windows.UI.Input.InputSite.WindowClass",
        u"Search_app",
        u"Tooltips_Class32",
        u"TaskSwitcherWnd",
        u"TaskSwitcherOverlayWnd",
        u"Windows.Internal.Shell.TabProxyWindow",
        u"Microsoft.UI.Content.DesktopChildSiteBridge"
    };

    for (const auto& exact : kExact)
    {
        if (class_name == exact)
            return true;
    }

    static constexpr std::array<std::u16string_view, 4> kPrefixes = {
        u"Windows.UI.Core",
        u"ImmersiveLauncher",
        u"TaskListOverlay",
        u"MSCTFIME"
    };

    for (const auto& prefix : kPrefixes)
    {
        if (class_name.starts_with(prefix))
            return true;
    }

    return false;
}

bool TryGetWindowBoundsLocal(HWND hwnd, RECT* out_rect) noexcept
{
    if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out_rect, sizeof(RECT)) == S_OK)
        return true;
    return GetWindowRect(hwnd, out_rect) != FALSE;
}

BOOL CALLBACK EnumObscuringWindowsProc(HWND hwnd, LPARAM lparam) noexcept
{
    auto* ctx = reinterpret_cast<DesktopObscureProbeCtx*>(lparam);
    if (hwnd == ctx->notch_hwnd)
        return TRUE;

    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == ctx->my_pid)
        return TRUE;

    if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsWindowCloakedLocal(hwnd))
        return TRUE;

    if (IsBlockedDesktopClassLocal(hwnd))
        return TRUE;

    const LONG ex_style = GetWindowLongW(hwnd, GWL_EXSTYLE);
    if ((ex_style & WS_EX_TOOLWINDOW) != 0 && (ex_style & WS_EX_APPWINDOW) == 0)
        return TRUE;
    if ((ex_style & WS_EX_TRANSPARENT) != 0)
        return TRUE;

    RECT win_rect{};
    if (TryGetWindowBoundsLocal(hwnd, &win_rect) &&
        win_rect.left < ctx->notch_rect.right &&
        win_rect.right > ctx->notch_rect.left &&
        win_rect.top < ctx->notch_rect.bottom &&
        win_rect.bottom > ctx->notch_rect.top)
    {
        ctx->obscured = 1;
        return FALSE;
    }

    return TRUE;
}

} // namespace

// ---------------------------------------------------------------------------
// Exported C ABI Implementations
// ---------------------------------------------------------------------------





































// ---------------------------------------------------------------------------
// Controls Implementations
// ---------------------------------------------------------------------------

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

VN_EXPORT int32_t __stdcall vn_ai_usage_resolve_account_windows(
    const VnAiUsageWindowEntry* entries,
    int32_t count,
    int32_t* out_short_idx,
    int32_t* out_long_idx) noexcept
{
    if (!out_short_idx || !out_long_idx)
        return VN_ERR_INVALID_ARG;

    *out_short_idx = -1;
    *out_long_idx = -1;

    if (!entries || count <= 0)
        return VN_OK;

    bool use_codex_bucket = false;
    for (int32_t i = 0; i < count; ++i)
    {
        if (entries[i].is_codex_bucket != 0)
        {
            use_codex_bucket = true;
            break;
        }
    }

    int32_t five_hour_idx = -1;
    int32_t fallback_idx = -1;
    int32_t long_idx = -1;

    for (int32_t i = 0; i < count; ++i)
    {
        const auto& e = entries[i];
        const bool in_target_bucket = use_codex_bucket
            ? (e.is_codex_bucket != 0)
            : (e.matches_first_bucket != 0);
        if (!in_target_bucket)
            continue;

        if (e.has_duration != 0 && e.duration_minutes == 10080)
        {
            if (long_idx < 0)
                long_idx = i;
            continue;
        }

        if (fallback_idx < 0)
            fallback_idx = i;

        if (e.has_duration != 0 && e.duration_minutes == 300 && five_hour_idx < 0)
            five_hour_idx = i;
    }

    *out_short_idx = five_hour_idx >= 0 ? five_hour_idx : fallback_idx;
    *out_long_idx = long_idx;
    return VN_OK;
}

// ---------------------------------------------------------------------------
// Windows & Setup Implementations
// ---------------------------------------------------------------------------

VN_EXPORT int32_t __stdcall vn_mainwindow_step_volume_wheel(
    VnVolumeWheelState* io_state,
    int32_t raw_delta,
    int64_t now_utc_ticks,
    uint8_t is_indicator_active,
    int32_t* out_volume_delta,
    uint8_t* out_accepted,
    uint8_t* out_suppressed) noexcept
{
    if (!io_state || !out_volume_delta || !out_accepted || !out_suppressed)
        return VN_ERR_INVALID_ARG;

    *out_volume_delta = 0;
    *out_accepted = 0;
    *out_suppressed = 0;

    if (raw_delta == 0)
        return VN_OK;

    if (now_utc_ticks < io_state->suppress_until_utc_ticks)
    {
        io_state->accumulator = 0.0;
        io_state->last_wheel_utc_ticks = 0;
        io_state->is_wheel_active = 0;
        *out_suppressed = 1;
        return VN_OK;
    }

    constexpr int64_t kSessionResetTicks = 450LL * 10000LL;
    if (now_utc_ticks - io_state->last_wheel_utc_ticks > kSessionResetTicks)
    {
        io_state->accumulator = 0.0;
        io_state->is_wheel_active = 0;
    }
    io_state->last_wheel_utc_ticks = now_utc_ticks;

    if (is_indicator_active != 0 || io_state->is_wheel_active != 0)
    {
        io_state->is_wheel_active = 1;
        *out_volume_delta = raw_delta;
        *out_accepted = 1;
        return VN_OK;
    }

    constexpr int64_t kImmediateWheelDelta = 120;
    const int64_t abs_raw = raw_delta < 0 ? -static_cast<int64_t>(raw_delta) : static_cast<int64_t>(raw_delta);
    if (abs_raw >= kImmediateWheelDelta)
    {
        io_state->is_wheel_active = 1;
        *out_volume_delta = raw_delta;
        *out_accepted = 1;
        return VN_OK;
    }

    const int32_t acc_sign = io_state->accumulator > 0.0 ? 1 : (io_state->accumulator < 0.0 ? -1 : 0);
    const int32_t delta_sign = raw_delta > 0 ? 1 : -1;
    if (acc_sign != 0 && acc_sign != delta_sign)
        io_state->accumulator = 0.0;

    io_state->accumulator += static_cast<double>(raw_delta);
    if (std::abs(io_state->accumulator) < 240.0)
        return VN_OK;

    io_state->accumulator = 0.0;
    io_state->is_wheel_active = 1;
    *out_volume_delta = delta_sign * static_cast<int32_t>(kImmediateWheelDelta);
    *out_accepted = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_desktop_edge_probe_notch_obscured(
    intptr_t self_hwnd,
    uint8_t* out_obscured) noexcept
{
    if (!out_obscured)
        return VN_ERR_INVALID_ARG;

    *out_obscured = 0;
    const HWND notch_hwnd = reinterpret_cast<HWND>(self_hwnd);
    if (notch_hwnd == nullptr)
        return VN_OK;

    RECT notch_rect{};
    if (!GetWindowRect(notch_hwnd, &notch_rect))
        return VN_OK;
    if (notch_rect.right <= notch_rect.left || notch_rect.bottom <= notch_rect.top)
        return VN_OK;

    DWORD my_pid = 0;
    GetWindowThreadProcessId(notch_hwnd, &my_pid);

    DesktopObscureProbeCtx ctx{ notch_hwnd, my_pid, notch_rect, 0 };
    EnumWindows(EnumObscuringWindowsProc, reinterpret_cast<LPARAM>(&ctx));
    *out_obscured = ctx.obscured;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_appnotify_step_drag_interaction(
    VnAppNotifyDragState* io_state,
    double point_x,
    double point_y,
    double origin_x,
    double origin_y,
    int64_t timestamp,
    double* out_translate_x,
    double* out_translate_y,
    double* out_opacity,
    uint8_t* out_moved,
    uint8_t* out_started_dragging) noexcept
{
    if (!io_state || !out_translate_x || !out_translate_y || !out_opacity || !out_moved || !out_started_dragging)
        return VN_ERR_INVALID_ARG;

    *out_translate_x = 0.0;
    *out_translate_y = 0.0;
    *out_opacity = 1.0;
    *out_moved = 0;
    *out_started_dragging = 0;

    const double dx = point_x - origin_x;
    const double dy = point_y - origin_y;
    if (io_state->is_dragging == 0 && std::abs(dx) < 5.0 && std::abs(dy) < 5.0)
        return VN_OK;

    if (io_state->is_dragging == 0)
    {
        io_state->is_dragging = 1;
        *out_started_dragging = 1;
    }

    const int64_t elapsed = std::max<int64_t>(1, timestamp - io_state->last_timestamp);
    io_state->velocity = (point_x - io_state->last_x) / static_cast<double>(elapsed);
    io_state->last_x = point_x;
    io_state->last_y = point_y;
    io_state->last_timestamp = timestamp;

    *out_translate_x = dx / (1.0 + std::abs(dx) / 280.0);
    *out_translate_y = ClampF64(dy * 0.12, -12.0, 12.0);
    *out_opacity = ClampF64(1.0 - std::abs(dx) / 360.0, 0.45, 1.0);
    *out_moved = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_appnotify_eval_drag_release(
    double point_x,
    double origin_x,
    int64_t last_timestamp,
    int64_t timestamp,
    double velocity,
    uint8_t is_dragging,
    uint8_t* out_dismiss,
    int32_t* out_dismiss_direction,
    uint8_t* out_is_click) noexcept
{
    if (!out_dismiss || !out_dismiss_direction || !out_is_click)
        return VN_ERR_INVALID_ARG;

    const double delta = point_x - origin_x;
    const double eff_velocity = (timestamp - last_timestamp <= 100) ? velocity : 0.0;
    const int32_t delta_sign = delta > 0.0 ? 1 : (delta < 0.0 ? -1 : 0);
    const int32_t vel_sign = eff_velocity > 0.0 ? 1 : (eff_velocity < 0.0 ? -1 : 0);

    const bool dismiss = (is_dragging != 0) &&
                         (std::abs(delta) >= 72.0 ||
                          (std::abs(delta) >= 16.0 && std::abs(eff_velocity) >= 0.65 && delta_sign == vel_sign));

    *out_dismiss = dismiss ? 1 : 0;
    *out_dismiss_direction = delta_sign;
    *out_is_click = (is_dragging == 0) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_translation_clamp_placement(
    double sel_left,
    double sel_top,
    double sel_bottom,
    double work_left,
    double work_top,
    double work_right,
    double work_bottom,
    double width,
    double height,
    double* out_x,
    double* out_y) noexcept
{
    if (!out_x || !out_y)
        return VN_ERR_INVALID_ARG;

    const double max_x = MathF64Max(work_left, work_right - width);
    *out_x = ClampF64(sel_left, work_left, max_x);

    double y = sel_bottom + 8.0;
    if (y + height > work_bottom)
        y = sel_top - height - 8.0;

    const double max_y = MathF64Max(work_top, work_bottom - height);
    *out_y = ClampF64(y, work_top, max_y);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_window_calc_rounded_region(
    double width_dip,
    double height_dip,
    double margin_dip,
    double corner_radius_dip,
    double dpi_scale,
    VnRoundedWindowRegion* out_region) noexcept
{
    if (!out_region)
        return VN_ERR_INVALID_ARG;

    const double scale = (dpi_scale > 0.0 && std::isfinite(dpi_scale)) ? dpi_scale : 1.0;
    const double min_dim_dip = margin_dip * 2.0 + 1.0;

    const int32_t left = UncheckedRoundToInt32(margin_dip * scale);
    const int32_t top = left;
    const int32_t right = std::max(
        left + 1,
        UncheckedRoundToInt32((MathF64Max(width_dip, min_dim_dip) - margin_dip) * scale));
    const int32_t bottom = std::max(
        top + 1,
        UncheckedRoundToInt32((MathF64Max(height_dip, min_dim_dip) - margin_dip) * scale));
    const int32_t corner_diameter = std::max(
        1,
        UncheckedRoundToInt32(corner_radius_dip * 2.0 * scale));

    out_region->left = left;
    out_region->top = top;
    out_region->right = right;
    out_region->bottom = bottom;
    out_region->corner_diameter = corner_diameter;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_install_cleanup_validate_manifest_entry(
    const wchar_t* rel_path,
    int32_t path_len,
    const wchar_t* sha256,
    int32_t sha_len,
    uint8_t* out_valid) noexcept
{
    if (!out_valid)
        return VN_ERR_INVALID_ARG;

    *out_valid = 0;

    if (!rel_path || path_len <= 0 || !sha256 || sha_len != 64)
        return VN_OK;

    const std::u16string_view path_sv(reinterpret_cast<const char16_t*>(rel_path), static_cast<size_t>(path_len));
    const std::u16string_view sha_sv(reinterpret_cast<const char16_t*>(sha256), 64);

    for (char16_t ch : sha_sv)
    {
        if (!IsAsciiHexDigit(ch))
            return VN_OK;
    }

    bool any_non_ws = false;
    for (char16_t ch : path_sv)
    {
        if (ch == 0)
            return VN_OK;
        if (!IsUtf16WhiteSpace(ch))
            any_non_ws = true;
    }
    if (!any_non_ws)
        return VN_OK;

    // Path.IsPathRooted on Windows: leading slash/backslash or drive-letter colon
    if (path_sv[0] == u'/' || path_sv[0] == u'\\')
        return VN_OK;
    if (path_sv.size() >= 2 && IsAsciiAlpha(path_sv[0]) && path_sv[1] == u':')
        return VN_OK;

    //Equals("installed-files.json", OrdinalIgnoreCase)
    if (EqualsAsciiIgnoreCase(path_sv, u"installed-files.json"))
        return VN_OK;

    // Split(['/', '\\']).Any(part => part is "" or "." or ".." || part.Contains(':'))
    size_t seg_start = 0;
    for (size_t i = 0; i <= path_sv.size(); ++i)
    {
        const bool at_sep = (i == path_sv.size()) || path_sv[i] == u'/' || path_sv[i] == u'\\';
        if (!at_sep)
        {
            if (path_sv[i] == u':')
                return VN_OK;
            continue;
        }

        const size_t seg_len = i - seg_start;
        if (seg_len == 0)
            return VN_OK;
        if (seg_len == 1 && path_sv[seg_start] == u'.')
            return VN_OK;
        if (seg_len == 2 && path_sv[seg_start] == u'.' && path_sv[seg_start + 1] == u'.')
            return VN_OK;

        seg_start = i + 1;
    }

    *out_valid = 1;
    return VN_OK;
}
