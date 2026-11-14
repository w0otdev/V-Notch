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
// LiquidGlass Pure Optical & Geometry Kernels
// ---------------------------------------------------------------------------

constexpr double Smoother01Impl(double x) noexcept
{
    const double t = ClampF64(x, 0.0, 1.0);
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
}

inline double LensProfileImpl(double normalized_distance, bool broad) noexcept
{
    const double s = Smoother01Impl(normalized_distance);
    const double one_minus_s = 1.0 - s;
    const double profile = (256.0 / 27.0) * s * one_minus_s * one_minus_s * one_minus_s;
    return broad ? std::sqrt(profile) : profile;
}

inline double EdgeBendGainImpl(double edge_bend) noexcept
{
    const double bend = std::isfinite(edge_bend) ? std::max(edge_bend, 0.0) : 0.0;
    return 0.58 * std::pow(bend, 1.5);
}

inline double RefractionAmplitudeImpl(
    double rim_width,
    double refraction,
    int32_t bevel_mode,
    double edge_bend) noexcept
{
    const double r = MathF64Max(refraction, 0.0);
    const double response = r / MathF64Max(0.65 + 0.35 * r, 0.001);
    const double travel_ratio = EdgeBendGainImpl(edge_bend) * response * (bevel_mode >= 1 ? 1.08 : 1.0);
    return rim_width * travel_ratio;
}

inline double DirectionalLensWidthImpl(
    double rim_width,
    double inward_normal_x,
    double edge_bend) noexcept
{
    const double bend = std::isfinite(edge_bend) ? std::max(edge_bend, 0.0) : 0.0;
    const double side_axis = std::pow(ClampF64(std::abs(inward_normal_x), 0.0, 1.0), 2.6);
    return rim_width * (1.0 + 0.5 * bend * side_axis);
}

constexpr double ComputeRimWidthImpl(
    double normalized_radius,
    double output_scale,
    double min_half) noexcept
{
    const double eff_max = min_half < 3.0 ? 3.0 : min_half;
    return ClampF64(ClampF64(normalized_radius, 0.02, 0.95) * 100.0 * output_scale, 3.0, eff_max);
}

inline int32_t ComputeSamplingMarginImpl(
    double rim_width,
    double refraction,
    double chroma,
    double distortion,
    int32_t bevel_mode,
    double edge_bend) noexcept
{
    const double amplitude = RefractionAmplitudeImpl(
        rim_width, ClampF64(refraction, 0.0, 3.0), bevel_mode, edge_bend);
    const double chroma_offset = MathF64Min(ClampF64(chroma, 0.0, 2.0) * (1.25 + rim_width * 0.085), 8.0);
    const double fluid_offset = ClampF64(distortion, 0.0, 2.0) * 2.25;
    const double total = std::ceil(amplitude + chroma_offset + fluid_offset + 3.0);
    if (!std::isfinite(total))
        return 12;
    if (total <= 12.0)
        return 12;
    if (total >= 512.0)
        return 512;
    return static_cast<int32_t>(total);
}

constexpr int32_t ClampCaptureOriginImpl(
    int32_t requested,
    int32_t capture_len,
    int32_t desktop_origin,
    int32_t desktop_len) noexcept
{
    if (capture_len <= 0 || desktop_len <= 0)
        return requested;
    if (capture_len >= desktop_len)
        return desktop_origin;

    const int64_t lo = static_cast<int64_t>(desktop_origin);
    const int64_t hi = lo + static_cast<int64_t>(desktop_len) - static_cast<int64_t>(capture_len);
    const int64_t req = static_cast<int64_t>(requested);
    return static_cast<int32_t>(req < lo ? lo : (req > hi ? hi : req));
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

VN_EXPORT int32_t __stdcall vn_liquid_glass_build_optical_lut(
    float refraction_amount,
    float rim_width,
    float* out_lut1024) noexcept
{
    if (!out_lut1024)
        return VN_ERR_INVALID_ARG;

    const double amp = RefractionAmplitudeImpl(
        static_cast<double>(rim_width),
        static_cast<double>(refraction_amount),
        0,
        1.0);

    for (int32_t i = 0; i < 1024; ++i)
    {
        const double t = static_cast<double>(i) / 1023.0;
        out_lut1024[i] = static_cast<float>(LensProfileImpl(t, false) * amp);
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_compute_frame_dims(
    const VnLiquidGlassFrameDimsInput* input,
    VnLiquidGlassFrameDimsOut* out_dims) noexcept
{
    if (!input || !out_dims)
        return VN_ERR_INVALID_ARG;

    const double bitmap_dpi = input->bitmap_dpi <= 0.0 ? 96.0 : input->bitmap_dpi;
    const double dpi_ratio = bitmap_dpi / 96.0;

    const int32_t out_w = std::max(8, input->display_w);
    const int32_t out_h = std::max(8, input->display_h);
    const double out_scale = input->display_w > 0
        ? (static_cast<double>(out_w) / static_cast<double>(input->display_w)) * dpi_ratio
        : 1.0;

    const int32_t overscan = input->gpu_mode != 0
        ? 0
        : std::clamp(UncheckedRoundToInt32(80.0 * dpi_ratio), 64, 150);
    const int32_t buf_w = out_w + overscan * 2;
    const int32_t buf_h = out_h + overscan;
    const int32_t notch_off_x = overscan;
    constexpr int32_t notch_off_y = 0;

    const double min_half = static_cast<double>(std::min(out_w, out_h)) * 0.5;
    const double rim_width = ComputeRimWidthImpl(input->z_radius, out_scale, min_half);
    const int32_t required_margin = ComputeSamplingMarginImpl(
        rim_width,
        input->refraction,
        input->chromatic_aberration,
        input->distortion,
        input->bevel_mode,
        input->edge_bend);

    constexpr int32_t kGpuSamplingMarginLimit = 160;
    const bool full_surface = input->full_surface != 0;
    const int32_t margin = input->gpu_mode != 0
        ? (full_surface ? kGpuSamplingMarginLimit : std::clamp(required_margin + 32, 32, kGpuSamplingMarginLimit))
        : required_margin;

    const int32_t src_w = full_surface ? input->surface_w : buf_w + margin * 2;
    const int32_t src_h = full_surface ? input->surface_h : buf_h + margin * 2;
    const int32_t phys_src_w = src_w;
    const int32_t phys_src_h = src_h;

    int32_t requested_src_x = input->region_x - notch_off_x - margin;
    if (full_surface)
        requested_src_x -= (input->max_region_w - input->display_w) / 2;
    const int32_t requested_src_y = input->region_y - margin;

    const int32_t desktop_x = GetSystemMetrics(SM_XVIRTUALSCREEN);
    const int32_t desktop_w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    const int32_t desktop_y = GetSystemMetrics(SM_YVIRTUALSCREEN);
    const int32_t desktop_h = GetSystemMetrics(SM_CYVIRTUALSCREEN);

    int32_t src_x = ClampCaptureOriginImpl(requested_src_x, phys_src_w, desktop_x, desktop_w);
    int32_t src_y = ClampCaptureOriginImpl(requested_src_y, phys_src_h, desktop_y, desktop_h);

    if (full_surface)
    {
        const int32_t sampling_pad = std::min(required_margin, margin);
        const int32_t padded_w = input->display_w + sampling_pad * 2;
        const int32_t padded_h = input->display_h + sampling_pad * 2;
        const int32_t needed_left = ClampCaptureOriginImpl(input->region_x - sampling_pad, padded_w, desktop_x, desktop_w);
        const int32_t needed_top = ClampCaptureOriginImpl(input->region_y - sampling_pad, padded_h, desktop_y, desktop_h);

        if (input->has_prev_bounds != 0 &&
            input->prev_bounds_w == phys_src_w &&
            input->prev_bounds_h == phys_src_h &&
            ClampCaptureOriginImpl(input->prev_bounds_x, phys_src_w, desktop_x, desktop_w) == input->prev_bounds_x &&
            ClampCaptureOriginImpl(input->prev_bounds_y, phys_src_h, desktop_y, desktop_h) == input->prev_bounds_y &&
            needed_left >= input->prev_bounds_x &&
            needed_top >= input->prev_bounds_y &&
            needed_left + padded_w <= input->prev_bounds_x + phys_src_w &&
            needed_top + padded_h <= input->prev_bounds_y + phys_src_h)
        {
            src_x = input->prev_bounds_x;
            src_y = input->prev_bounds_y;
        }

        out_dims->has_next_bounds = 1;
        out_dims->next_bounds_x = src_x;
        out_dims->next_bounds_y = src_y;
        out_dims->next_bounds_w = phys_src_w;
        out_dims->next_bounds_h = phys_src_h;
    }
    else
    {
        out_dims->has_next_bounds = 0;
        out_dims->next_bounds_x = 0;
        out_dims->next_bounds_y = 0;
        out_dims->next_bounds_w = 0;
        out_dims->next_bounds_h = 0;
    }

    out_dims->pad0 = 0;
    out_dims->pad1 = 0;
    out_dims->pad2 = 0;

    const int32_t capture_shift_x = src_x - requested_src_x;
    const int32_t capture_shift_y = src_y - requested_src_y;

    out_dims->out_w = out_w;
    out_dims->out_h = out_h;
    out_dims->out_scale = out_scale;
    out_dims->buf_w = buf_w;
    out_dims->buf_h = buf_h;
    out_dims->notch_off_x = notch_off_x;
    out_dims->notch_off_y = notch_off_y;
    out_dims->min_half = min_half;
    out_dims->margin = margin;
    out_dims->src_w = src_w;
    out_dims->src_h = src_h;
    out_dims->phys_src_w = phys_src_w;
    out_dims->phys_src_h = phys_src_h;
    out_dims->src_x = src_x;
    out_dims->src_y = src_y;
    out_dims->capture_shift_x = capture_shift_x;
    out_dims->capture_shift_y = capture_shift_y;
    out_dims->map_capture_shift_y = capture_shift_y;

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_create_gpu_geometry(
    const VnLiquidGlassGpuGeometryInput* input,
    VnLiquidGlassGpuGeometryOut* out_geom) noexcept
{
    if (!input || !out_geom)
        return VN_ERR_INVALID_ARG;

    const double top_r = ClampF64(input->top_corner_radius * input->out_scale, 0.0, input->min_half);
    const double bottom_r = ClampF64(input->bottom_corner_radius * input->out_scale, 0.0, input->min_half);
    const double off_x = static_cast<double>(input->region_x - input->src_x) + input->sub_x;
    const double off_y = static_cast<double>(input->region_y - input->src_y) + input->sub_y;

    const double clamped_refract = ClampF64(input->refraction, 0.0, 3.0);
    const double z_safe = std::isfinite(input->z_radius) ? input->z_radius : 0.25;
    const double edge_bend_safe = std::isfinite(input->edge_bend) ? std::max(0.0, input->edge_bend) : 0.0;

    out_geom->src_w = static_cast<double>(input->src_w);
    out_geom->src_h = static_cast<double>(input->src_h);
    out_geom->notch_w = static_cast<double>(input->out_w);
    out_geom->notch_h = static_cast<double>(input->out_h);
    out_geom->off_x = off_x;
    out_geom->off_y = off_y;
    out_geom->top_corner_r = top_r;
    out_geom->bottom_corner_r = bottom_r;
    out_geom->power_factor = input->power_factor;
    out_geom->a = input->refraction_a;
    out_geom->b = input->refraction_b * (input->refraction > 1e-4 ? (0.15 + 1.15 * clamped_refract) : 0.0);
    out_geom->c = input->refraction_c;
    out_geom->d = ClampF64(input->refraction_d * (0.25 / ClampF64(z_safe, 0.04, 1.0)), 1.2, 24.0);
    out_geom->f_power = input->f_power;
    out_geom->noise = input->noise;
    out_geom->glow_weight = ClampF64(input->specular * 1.15 + input->edge_highlight * 0.65 + input->glow_weight * 0.12, 0.0, 2.5);
    out_geom->glow_bias = ClampF64(input->fresnel * 1.15 + input->edge_highlight * 0.40 + MathF64Max(0.0, input->glow_bias), 0.0, 2.5);
    out_geom->glow_edge0 = ClampF64(input->distortion, 0.0, 2.0);
    out_geom->glow_edge1 = input->glow_edge1;
    out_geom->chroma = ClampF64(input->chromatic_aberration, 0.0, 2.0);
    out_geom->edge_bend = edge_bend_safe * (input->refraction > 1e-4 ? (0.25 + 0.95 * clamped_refract) : 0.0);
    out_geom->sat_factor = 1.0 + input->saturation;
    out_geom->bright_add = input->brightness;
    out_geom->bevel_mode = input->bevel_mode >= 1 ? 1.0 : 0.0;
    out_geom->capture_origin_x = input->src_x;
    out_geom->capture_origin_y = input->src_y;

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_grow_present_capacity(
    int32_t current_cap,
    int32_t required,
    int32_t slack,
    int32_t* out_cap) noexcept
{
    if (!out_cap)
        return VN_ERR_INVALID_ARG;

    if (required <= 0)
    {
        *out_cap = 0;
        return VN_OK;
    }

    const int64_t req4 = static_cast<int64_t>(required) * 4;
    if (req4 > std::numeric_limits<int32_t>::max() || req4 < std::numeric_limits<int32_t>::min())
        return VN_ERR_OVERFLOW;

    int32_t cur = current_cap;
    if (cur > static_cast<int32_t>(req4))
        cur = 0;
    if (cur >= required)
    {
        *out_cap = cur;
        return VN_OK;
    }

    int32_t target = 0;
    if (cur > 0)
    {
        const int64_t cur_plus_slack = static_cast<int64_t>(cur) + static_cast<int64_t>(slack);
        if (cur_plus_slack > std::numeric_limits<int32_t>::max() || cur_plus_slack < std::numeric_limits<int32_t>::min())
            return VN_ERR_OVERFLOW;
        target = std::max(required, static_cast<int32_t>(cur_plus_slack));
    }
    else
    {
        const int64_t req_plus_slack = static_cast<int64_t>(required) + static_cast<int64_t>(slack);
        if (req_plus_slack > std::numeric_limits<int32_t>::max() || req_plus_slack < std::numeric_limits<int32_t>::min())
            return VN_ERR_OVERFLOW;
        target = static_cast<int32_t>(req_plus_slack);
    }

    constexpr int32_t kQuantum = 64;
    const int64_t numerator = static_cast<int64_t>(target) + (kQuantum - 1);
    if (numerator > std::numeric_limits<int32_t>::max() || numerator < std::numeric_limits<int32_t>::min())
        return VN_ERR_OVERFLOW;

    const int64_t rounded = static_cast<int64_t>(static_cast<int32_t>(numerator) / kQuantum) * kQuantum;
    if (rounded > std::numeric_limits<int32_t>::max() || rounded < std::numeric_limits<int32_t>::min())
        return VN_ERR_OVERFLOW;

    *out_cap = static_cast<int32_t>(rounded);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_grow_pixel_buffer_capacity(
    int32_t current_cap,
    int32_t required,
    int32_t* out_cap) noexcept
{
    if (!out_cap)
        return VN_ERR_INVALID_ARG;

    if (current_cap >= required)
    {
        *out_cap = current_cap;
        return VN_OK;
    }

    const int32_t slack = std::clamp(current_cap / 4, 4096, 65536);
    const int64_t grown = static_cast<int64_t>(current_cap) + static_cast<int64_t>(slack);
    if (grown > std::numeric_limits<int32_t>::max() || grown < std::numeric_limits<int32_t>::min())
        return VN_ERR_OVERFLOW;

    *out_cap = std::max(required, static_cast<int32_t>(grown));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_clamp_capture_origin(
    int32_t requested,
    int32_t capture_len,
    int32_t desktop_origin,
    int32_t desktop_len,
    int32_t* out_origin) noexcept
{
    if (!out_origin)
        return VN_ERR_INVALID_ARG;
    *out_origin = ClampCaptureOriginImpl(requested, capture_len, desktop_origin, desktop_len);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_compute_rim_width(
    double normalized_radius,
    double output_scale,
    double min_half,
    double* out_rim_width) noexcept
{
    if (!out_rim_width)
        return VN_ERR_INVALID_ARG;
    if (min_half < 3.0)
        return VN_ERR_INVALID_ARG;
    *out_rim_width = ClampF64(ClampF64(normalized_radius, 0.02, 0.95) * 100.0 * output_scale, 3.0, min_half);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_lens_profile(
    double normalized_distance,
    uint8_t broad,
    double* out_profile) noexcept
{
    if (!out_profile)
        return VN_ERR_INVALID_ARG;
    *out_profile = LensProfileImpl(normalized_distance, broad != 0);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_refraction_amplitude(
    double rim_width,
    double refraction,
    int32_t bevel_mode,
    double edge_bend,
    double* out_amplitude) noexcept
{
    if (!out_amplitude)
        return VN_ERR_INVALID_ARG;
    *out_amplitude = RefractionAmplitudeImpl(rim_width, refraction, bevel_mode, edge_bend);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_edge_bend_gain(
    double edge_bend,
    double* out_gain) noexcept
{
    if (!out_gain)
        return VN_ERR_INVALID_ARG;
    *out_gain = EdgeBendGainImpl(edge_bend);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_directional_lens_width(
    double rim_width,
    double inward_normal_x,
    double edge_bend,
    double* out_width) noexcept
{
    if (!out_width)
        return VN_ERR_INVALID_ARG;
    *out_width = DirectionalLensWidthImpl(rim_width, inward_normal_x, edge_bend);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_is_gpu_geometry_valid(
    double src_w,
    double src_h,
    double notch_w,
    double notch_h,
    uint8_t* out_valid) noexcept
{
    if (!out_valid)
        return VN_ERR_INVALID_ARG;
    const bool ok = src_w >= 1.0 && src_h >= 1.0 && notch_w >= 1.0 && notch_h >= 1.0 &&
                    (src_w + 1.5) >= notch_w && (src_h + 1.5) >= notch_h;
    *out_valid = ok ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_compute_sampling_margin(
    double rim_width,
    double refraction,
    double chroma,
    double distortion,
    int32_t bevel_mode,
    double edge_bend,
    int32_t* out_margin) noexcept
{
    if (!out_margin)
        return VN_ERR_INVALID_ARG;
    *out_margin = ComputeSamplingMarginImpl(rim_width, refraction, chroma, distortion, bevel_mode, edge_bend);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_exponential_refract(
    double x,
    double a,
    double b,
    double c,
    double d,
    double* out_value) noexcept
{
    if (!out_value)
        return VN_ERR_INVALID_ARG;
    const double exponent = -d * x - a;
    const double base_val = MathF64Max(c * 2.718281828459045, 0.0001);
    *out_value = 1.0 - b * std::pow(base_val, exponent);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_choose_frame_interval_ms(
    double active_interval_ms,
    uint8_t idle,
    uint8_t animating,
    uint8_t has_frame,
    int64_t unchanged_ms,
    uint8_t force_refresh,
    double* out_interval_ms) noexcept
{
    if (!out_interval_ms)
        return VN_ERR_INVALID_ARG;
    const bool use_idle_interval = (idle != 0) && (animating == 0) && (has_frame != 0) &&
                                   (unchanged_ms >= 250) && (force_refresh == 0);
    *out_interval_ms = use_idle_interval ? MathF64Max(100.0, active_interval_ms) : active_interval_ms;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_choose_processing_interval_ms(
    double interval_ms,
    double work_ms,
    uint8_t gpu_mode,
    double* out_interval_ms) noexcept
{
    if (!out_interval_ms)
        return VN_ERR_INVALID_ARG;
    *out_interval_ms = (gpu_mode != 0)
        ? interval_ms
        : MathF64Max(interval_ms, MathF64Min(100.0, work_ms * 1.25));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_blur_amount_to_sigma(
    double amount,
    double dpi_scale,
    double* out_sigma) noexcept
{
    if (!out_sigma)
        return VN_ERR_INVALID_ARG;
    const double normalized = std::isfinite(amount) ? ClampF64(amount, 0.0, 1.0) : 0.0;
    *out_sigma = 28.0 * normalized * normalized * ClampF64(dpi_scale, 0.5, 4.0);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_compute_next_deadline_ms(
    double prev_deadline_ms,
    double interval_ms,
    double now_ms,
    double* out_deadline_ms) noexcept
{
    if (!out_deadline_ms)
        return VN_ERR_INVALID_ARG;
    *out_deadline_ms = MathF64Min(prev_deadline_ms + interval_ms, now_ms + interval_ms);
    return VN_OK;
}

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
