#include "../vnotch_native.h"

namespace {

enum class TrayAppIconKind : int32_t {
    Notes = 0,
    Finder = 1,
    Safari = 2,
    Chrome = 3,
    Photos = 4,
    Colors = 5,
    Figma = 6,
    Terminal = 7,
    Document = 8,
    Pdf = 9,
    Archive = 10,
    Audio = 11,
    Video = 12,
    Folder = 13
};

enum class ClipboardKind : int32_t {
    Text = 0,
    Code = 1,
    Link = 2,
    Image = 3,
    Color = 4,
    File = 5
};

constexpr bool is_unicode_whitespace(wchar_t c) noexcept {
    if (c <= L' ') {
        return c == L' ' || c == L'\t' || c == L'\n' || c == L'\r' || c == L'\v' || c == L'\f';
    }
    return c == 0x0085 || c == 0x00A0 || c == 0x1680 ||
           (c >= 0x2000 && c <= 0x200A) ||
           c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000 || c == 0xFEFF;
}

constexpr std::wstring_view trim_whitespace(std::wstring_view sv) noexcept {
    size_t start = 0;
    size_t end = sv.size();
    while (start < end && is_unicode_whitespace(sv[start])) {
        ++start;
    }
    while (end > start && is_unicode_whitespace(sv[end - 1])) {
        --end;
    }
    return sv.substr(start, end - start);
}

constexpr wchar_t ascii_lower(wchar_t c) noexcept {
    return (c >= L'A' && c <= L'Z') ? static_cast<wchar_t>(c + (L'a' - L'A')) : c;
}

constexpr bool iequals_ascii(std::wstring_view input, std::wstring_view lower_literal) noexcept {
    if (input.size() != lower_literal.size()) {
        return false;
    }
    for (size_t i = 0; i < input.size(); ++i) {
        if (ascii_lower(input[i]) != lower_literal[i]) {
            return false;
        }
    }
    return true;
}

struct IconMapping {
    std::wstring_view key;
    TrayAppIconKind kind;
};

constexpr IconMapping kSourceMappings[] = {
    {L"notes", TrayAppIconKind::Notes},
    {L"notepad", TrayAppIconKind::Notes},
    {L"windows notepad", TrayAppIconKind::Notes},
    {L"finder", TrayAppIconKind::Finder},
    {L"explorer", TrayAppIconKind::Finder},
    {L"file explorer", TrayAppIconKind::Finder},
    {L"windows explorer", TrayAppIconKind::Finder},
    {L"safari", TrayAppIconKind::Safari},
    {L"chrome", TrayAppIconKind::Chrome},
    {L"google chrome", TrayAppIconKind::Chrome},
    {L"photos", TrayAppIconKind::Photos},
    {L"microsoft photos", TrayAppIconKind::Photos},
    {L"microsoft.photos", TrayAppIconKind::Photos},
    {L"figma", TrayAppIconKind::Figma},
    {L"terminal", TrayAppIconKind::Terminal},
    {L"windows terminal", TrayAppIconKind::Terminal},
    {L"windowsterminal", TrayAppIconKind::Terminal},
    {L"powershell", TrayAppIconKind::Terminal},
    {L"pwsh", TrayAppIconKind::Terminal},
    {L"cmd", TrayAppIconKind::Terminal},
    {L"command prompt", TrayAppIconKind::Terminal},
};

constexpr IconMapping kExtensionMappings[] = {
    {L".pdf", TrayAppIconKind::Pdf},
    {L".zip", TrayAppIconKind::Archive},
    {L".rar", TrayAppIconKind::Archive},
    {L".7z", TrayAppIconKind::Archive},
    {L".tar", TrayAppIconKind::Archive},
    {L".gz", TrayAppIconKind::Archive},
    {L".bz2", TrayAppIconKind::Archive},
    {L".mp3", TrayAppIconKind::Audio},
    {L".wav", TrayAppIconKind::Audio},
    {L".flac", TrayAppIconKind::Audio},
    {L".m4a", TrayAppIconKind::Audio},
    {L".aac", TrayAppIconKind::Audio},
    {L".ogg", TrayAppIconKind::Audio},
    {L".mp4", TrayAppIconKind::Video},
    {L".mkv", TrayAppIconKind::Video},
    {L".mov", TrayAppIconKind::Video},
    {L".avi", TrayAppIconKind::Video},
    {L".wmv", TrayAppIconKind::Video},
    {L".webm", TrayAppIconKind::Video},
    {L".png", TrayAppIconKind::Photos},
    {L".jpg", TrayAppIconKind::Photos},
    {L".jpeg", TrayAppIconKind::Photos},
    {L".gif", TrayAppIconKind::Photos},
    {L".webp", TrayAppIconKind::Photos},
    {L".heic", TrayAppIconKind::Photos},
    {L".bmp", TrayAppIconKind::Photos},
    {L".tiff", TrayAppIconKind::Photos},
    {L".cs", TrayAppIconKind::Terminal},
    {L".js", TrayAppIconKind::Terminal},
    {L".ts", TrayAppIconKind::Terminal},
    {L".tsx", TrayAppIconKind::Terminal},
    {L".jsx", TrayAppIconKind::Terminal},
    {L".py", TrayAppIconKind::Terminal},
    {L".cpp", TrayAppIconKind::Terminal},
    {L".c", TrayAppIconKind::Terminal},
    {L".h", TrayAppIconKind::Terminal},
    {L".json", TrayAppIconKind::Terminal},
    {L".xml", TrayAppIconKind::Terminal},
    {L".html", TrayAppIconKind::Terminal},
    {L".css", TrayAppIconKind::Terminal},
    {L".sql", TrayAppIconKind::Terminal},
    {L".doc", TrayAppIconKind::Document},
    {L".docx", TrayAppIconKind::Document},
    {L".rtf", TrayAppIconKind::Document},
    {L".odt", TrayAppIconKind::Document},
    {L".txt", TrayAppIconKind::Document},
    {L".md", TrayAppIconKind::Document},
    {L".xls", TrayAppIconKind::Document},
    {L".xlsx", TrayAppIconKind::Document},
    {L".csv", TrayAppIconKind::Document},
    {L".ppt", TrayAppIconKind::Document},
    {L".pptx", TrayAppIconKind::Document},
};

constexpr bool match_source_name(std::wstring_view name, int32_t& out_kind) noexcept {
    for (const auto& entry : kSourceMappings) {
        if (iequals_ascii(name, entry.key)) {
            out_kind = static_cast<int32_t>(entry.kind);
            return true;
        }
    }
    return false;
}

constexpr std::wstring_view extract_executable_name(std::wstring_view path) noexcept {
    size_t sep_pos = std::wstring_view::npos;
    for (size_t i = 0; i < path.size(); ++i) {
        if (path[i] == L'/' || path[i] == L'\\') {
            sep_pos = i;
        }
    }
    std::wstring_view name = (sep_pos == std::wstring_view::npos) ? path : path.substr(sep_pos + 1);
    if (name.size() >= 4 && iequals_ascii(name.substr(name.size() - 4), L".exe")) {
        name.remove_suffix(4);
    }
    return name;
}

} // namespace

VN_EXPORT int32_t __stdcall vn_tray_icons_match_source(
    const wchar_t* app_ptr,
    int32_t app_len,
    const wchar_t* exe_ptr,
    int32_t exe_len,
    int32_t* out_icon_kind,
    uint8_t* out_matched) noexcept {
    if (!out_icon_kind || !out_matched || app_len < 0 || exe_len < 0 ||
        (app_len > 0 && !app_ptr) || (exe_len > 0 && !exe_ptr)) {
        return VN_ERR_INVALID_ARG;
    }

    *out_icon_kind = 0;
    *out_matched = 0;

    const std::wstring_view app_sv = (app_len > 0) ? std::wstring_view(app_ptr, static_cast<size_t>(app_len)) : std::wstring_view{};
    int32_t kind = 0;
    if (match_source_name(trim_whitespace(app_sv), kind)) {
        *out_icon_kind = kind;
        *out_matched = 1;
        return VN_OK;
    }

    const std::wstring_view exe_sv = (exe_len > 0) ? std::wstring_view(exe_ptr, static_cast<size_t>(exe_len)) : std::wstring_view{};
    if (match_source_name(extract_executable_name(exe_sv), kind)) {
        *out_icon_kind = kind;
        *out_matched = 1;
        return VN_OK;
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_tray_icons_match_extension(
    const wchar_t* ext_ptr,
    int32_t ext_len,
    int32_t* out_icon_kind,
    uint8_t* out_matched) noexcept {
    if (!out_icon_kind || !out_matched || ext_len < 0 || (ext_len > 0 && !ext_ptr)) {
        return VN_ERR_INVALID_ARG;
    }

    *out_icon_kind = 0;
    *out_matched = 0;

    const std::wstring_view ext_sv = (ext_len > 0) ? std::wstring_view(ext_ptr, static_cast<size_t>(ext_len)) : std::wstring_view{};
    for (const auto& entry : kExtensionMappings) {
        if (iequals_ascii(ext_sv, entry.key)) {
            *out_icon_kind = static_cast<int32_t>(entry.kind);
            *out_matched = 1;
            return VN_OK;
        }
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_tray_icons_for_clipboard_kind(
    int32_t clipboard_kind,
    int32_t* out_icon_kind) noexcept {
    if (!out_icon_kind) {
        return VN_ERR_INVALID_ARG;
    }

    switch (static_cast<ClipboardKind>(clipboard_kind)) {
        case ClipboardKind::File:
            *out_icon_kind = static_cast<int32_t>(TrayAppIconKind::Finder);
            break;
        case ClipboardKind::Image:
            *out_icon_kind = static_cast<int32_t>(TrayAppIconKind::Photos);
            break;
        case ClipboardKind::Link:
            *out_icon_kind = static_cast<int32_t>(TrayAppIconKind::Safari);
            break;
        case ClipboardKind::Code:
            *out_icon_kind = static_cast<int32_t>(TrayAppIconKind::Terminal);
            break;
        case ClipboardKind::Color:
            *out_icon_kind = static_cast<int32_t>(TrayAppIconKind::Colors);
            break;
        default:
            *out_icon_kind = static_cast<int32_t>(TrayAppIconKind::Notes);
            break;
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_tray_icons_compute_color_sector(
    int32_t index,
    int32_t count,
    double cx,
    double cy,
    double radius,
    double* out_x1,
    double* out_y1,
    double* out_x2,
    double* out_y2) noexcept {
    if (!out_x1 || !out_y1 || !out_x2 || !out_y2 || count <= 0 || index < 0 || index >= count || !(radius >= 0.0)) {
        return VN_ERR_INVALID_ARG;
    }

    constexpr double kTwoPi = 6.283185307179586476925286766559;
    const double step = kTwoPi / static_cast<double>(count);
    const double start = static_cast<double>(index) * step;
    const double end = static_cast<double>(index + 1) * step;

    *out_x1 = cx + radius * std::cos(start);
    *out_y1 = cy + radius * std::sin(start);
    *out_x2 = cx + radius * std::cos(end);
    *out_y2 = cy + radius * std::sin(end);
    return VN_OK;
}
