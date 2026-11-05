#include "vnotch_common.h"
#include <windows.h>
#include <algorithm>
#include <array>
#include <cstdint>
#include <cwctype>
#include <string>
#include <string_view>
#include <vector>

#pragma comment(lib, "user32.lib")
#ifndef VN_VISIBLE_WINDOW_ENTRY_DEFINED
#define VN_VISIBLE_WINDOW_ENTRY_DEFINED

#pragma pack(push, 1)
struct VnVisibleWindowEntry {
    int64_t hwnd;
    uint32_t process_id;
    int32_t title_len;
    uint8_t matches_platform_title;
    char16_t title[256];
};
#pragma pack(pop)

#endif
namespace vnotch::services {

class WindowTitleScanner final {
public:
    static bool ContainsIgnoreCase(std::u16string_view haystack, std::wstring_view needle) noexcept {
        if (needle.empty()) return true;
        if (haystack.size() < needle.size()) return false;
        return ::FindStringOrdinal(
            FIND_FROMSTART,
            reinterpret_cast<LPCWSTR>(haystack.data()),
            static_cast<int>(haystack.size()),
            needle.data(),
            static_cast<int>(needle.size()),
            TRUE) >= 0;
    }

    static bool StartsWithIgnoreCase(std::u16string_view str, std::wstring_view prefix) noexcept {
        if (str.size() < prefix.size()) return false;
        return ::CompareStringOrdinal(
            reinterpret_cast<LPCWCH>(str.data()),
            static_cast<int>(prefix.size()),
            prefix.data(),
            static_cast<int>(prefix.size()),
            TRUE) == CSTR_EQUAL;
    }

    static bool MatchesPlatformTitle(std::u16string_view title) noexcept {
        if (title.empty()) return false;
        // Spotify Web uses "track • artists" without the site name while playing.
        if (title.find(u" \u2022 ") != title.npos) {
            const auto separator = title.find_last_of(u"-\u2014");
            if (separator != title.npos && vnotch::is_browser_app_view(title.substr(separator + 1)))
                return true;
        }
        static constexpr std::array<std::wstring_view, 38> kPlatformKeywords = {
            L"spotify", L"youtube", L"soundcloud", L"instagram", L"twitter", L" / x",
            L"apple music", L"apple", L"music", L"twitch", L"discord", L"vesktop",
            L"netflix", L"tidal", L"deezer", L"bandcamp", L"bilibili", L"\x54D4\x54E9\x54D4\x54E9",
            L"vimeo", L"crunchyroll", L"prime video", L"disney",
            L"picture in picture", L"picture-in-picture", L"h\x00ECnh trong h\x00ECnh", L"hinh trong hinh",
            L"bild-in-bild", L"image dans l'image", L"pantalla en pantalla", L"cuadro en cuadro",
            L"\x753B\x4E2D\x753B", L"\x756B\x4E2D\x756B", L"\x5B50\x6BCD\x753B\x9762", L"\x5B50\x6BCD\x756B\x9762",
            L"\x30D4\x30AF\x30C1\x30E3\x30FC \x30A4\x30F3 \x30D4\x30AF\x30C1\x30E3\x30FC",
            L"\x30D4\x30AF\x30C1\x30E3\x30FC\x30A4\x30F3\x30D4\x30AF\x30C1\x30E3\x30FC",
            L"\x043A\x0430\x0440\x0442\x0438\x043D\x043A\x0430 \x0432 \x043A\x0430\x0440\x0442\x0438\x043D\x043A\x0435",
            L"pip"
        };
        static constexpr std::array<std::wstring_view, 6> kMorePlatformKeywords = {
            L"imagem na imagem", L"imagem sobre imagem", L"finestra mobile",
            L"gambar dalam gambar", L"resim i\x00E7inde resim", L"\xD654\xBA74 \xC18D \xD654\xBA74"
        };

        for (auto kw : kPlatformKeywords) {
            if (ContainsIgnoreCase(title, kw)) return true;
        }
        for (auto kw : kMorePlatformKeywords) {
            if (ContainsIgnoreCase(title, kw)) return true;
        }
        return false;
    }

    static bool IsBrowserProcess(std::u16string_view process_name) noexcept {
        if (process_name.empty()) return false;
        static constexpr std::array<std::wstring_view, 21> kBrowserNames = {
            L"chrome", L"msedge", L"firefox", L"brave", L"opera", L"vivaldi", L"zen",
            L"arc", L"coccoc", L"sidekick", L"thorium", L"waterfox", L"floorp", L"librewolf",
            L"chromium", L"whale", L"yandex", L"wavebox", L"helium", L"supermium", L"browser"
        };
        for (auto name : kBrowserNames) {
            if (ContainsIgnoreCase(process_name, name)) return true;
        }
        return false;
    }

    static bool IsAddressBarValue(std::u16string_view val) noexcept {
        if (val.empty()) return false;
        return ContainsIgnoreCase(val, L"youtube.com/watch") ||
               ContainsIgnoreCase(val, L"youtu.be/") ||
               ContainsIgnoreCase(val, L"soundcloud.com/") ||
               StartsWithIgnoreCase(val, L"http://") ||
               StartsWithIgnoreCase(val, L"https://") ||
               ContainsIgnoreCase(val, L".com/") ||
               ContainsIgnoreCase(val, L".org/");
    }

    static bool IsWhiteSpaceOnly(std::u16string_view s) noexcept {
        for (char16_t ch : s) {
            if (!vnotch::is_unicode_whitespace(ch)) return false;
        }
        return true;
    }

    static bool LooksLikeUrl(std::u16string_view s) noexcept {
        if (IsWhiteSpaceOnly(s)) return false;
        return StartsWithIgnoreCase(s, L"http://") ||
               StartsWithIgnoreCase(s, L"https://") ||
               ContainsIgnoreCase(s, L"youtube.com/") ||
               ContainsIgnoreCase(s, L"youtu.be/") ||
               ContainsIgnoreCase(s, L"twitch.tv/") ||
               ContainsIgnoreCase(s, L"discord.com/") ||
               ContainsIgnoreCase(s, L"soundcloud.com/") ||
               ContainsIgnoreCase(s, L"open.spotify.com/") ||
               ContainsIgnoreCase(s, L"music.apple.com/") ||
               ContainsIgnoreCase(s, L"tidal.com/") ||
               ContainsIgnoreCase(s, L"deezer.com/") ||
               ContainsIgnoreCase(s, L"bandcamp.com/") ||
               ContainsIgnoreCase(s, L"netflix.com/") ||
               ContainsIgnoreCase(s, L"bilibili.com/") ||
               ContainsIgnoreCase(s, L"vimeo.com/") ||
               ContainsIgnoreCase(s, L"crunchyroll.com/");
    }

    static std::u16string NormalizeUrl(std::u16string_view s) {
        size_t start = 0;
        while (start < s.size() && vnotch::is_unicode_whitespace(s[start])) start++;
        size_t end = s.size();
        while (end > start && vnotch::is_unicode_whitespace(s[end - 1])) end--;

        std::u16string_view trimmed = s.substr(start, end - start);
        bool has_dot = trimmed.find(u'.') != std::u16string_view::npos;
        if (!StartsWithIgnoreCase(trimmed, L"http") && has_dot) {
            std::u16string out(u"https://");
            out.append(trimmed);
            return out;
        }
        return std::u16string(trimmed);
    }

    static bool IsMediaUrl(std::u16string_view url) noexcept {
        if (url.empty()) return false;
        return ContainsIgnoreCase(url, L"youtube.com/watch") ||
               ContainsIgnoreCase(url, L"youtu.be/") ||
               ContainsIgnoreCase(url, L"music.youtube.com/watch") ||
               ContainsIgnoreCase(url, L"twitch.tv/") ||
               ContainsIgnoreCase(url, L"discord.com/channels/") ||
               ContainsIgnoreCase(url, L"discord.com/app") ||
               ContainsIgnoreCase(url, L"soundcloud.com/") ||
               ContainsIgnoreCase(url, L"open.spotify.com/") ||
               ContainsIgnoreCase(url, L"music.apple.com/") ||
               ContainsIgnoreCase(url, L"listen.tidal.com/") ||
               ContainsIgnoreCase(url, L"deezer.com/") ||
               ContainsIgnoreCase(url, L"bandcamp.com/") ||
               ContainsIgnoreCase(url, L"netflix.com/watch") ||
               ContainsIgnoreCase(url, L"bilibili.com/video") ||
               ContainsIgnoreCase(url, L"vimeo.com/") ||
               ContainsIgnoreCase(url, L"crunchyroll.com/watch");
    }

    struct EnumWindowsContext {
        uint32_t exclude_pid;
        uint8_t only_platform_titles;
        VnVisibleWindowEntry* out_entries;
        int32_t entries_cap;
        int32_t total_count;
    };

    static BOOL CALLBACK EnumVisibleWindowsCallback(HWND hwnd, LPARAM lparam) noexcept {
        auto* ctx = reinterpret_cast<EnumWindowsContext*>(lparam);
        if (!ctx) return FALSE;
        if (!::IsWindowVisible(hwnd)) return TRUE;

        DWORD pid = 0;
        ::GetWindowThreadProcessId(hwnd, &pid);
        if (pid == 0 || (ctx->exclude_pid != 0 && pid == ctx->exclude_pid)) {
            return TRUE;
        }

        const int text_len = ::GetWindowTextLengthW(hwnd);
        if (text_len <= 0) return TRUE;

        wchar_t title_buf[512];
        const int copied = ::GetWindowTextW(hwnd, title_buf, 512);
        if (copied <= 0) return TRUE;

        const std::u16string_view title_sv(reinterpret_cast<const char16_t*>(title_buf), static_cast<size_t>(copied));
        const bool is_platform = MatchesPlatformTitle(title_sv);
        if (ctx->only_platform_titles != 0 && !is_platform) {
            return TRUE;
        }

        if (ctx->total_count < ctx->entries_cap && ctx->out_entries != nullptr) {
            VnVisibleWindowEntry& dst = ctx->out_entries[ctx->total_count];
            dst.hwnd = static_cast<int64_t>(reinterpret_cast<intptr_t>(hwnd));
            dst.process_id = static_cast<uint32_t>(pid);
            const int32_t clamped_len = std::min<int32_t>(copied, 255);
            dst.title_len = clamped_len;
            dst.matches_platform_title = is_platform ? 1u : 0u;
            for (int32_t i = 0; i < clamped_len; ++i) {
                dst.title[i] = static_cast<char16_t>(title_buf[i]);
            }
            dst.title[clamped_len] = u'\0';
        }
        ++ctx->total_count;
        return TRUE;
    }

    static int32_t CollectVisibleWindows(
        uint32_t exclude_pid,
        uint8_t only_platform_titles,
        VnVisibleWindowEntry* out_entries,
        int32_t entries_cap,
        int32_t& out_count) noexcept {
        out_count = 0;
        EnumWindowsContext ctx{
            exclude_pid,
            only_platform_titles,
            out_entries,
            entries_cap,
            0
        };
        ::EnumWindows(&EnumVisibleWindowsCallback, reinterpret_cast<LPARAM>(&ctx));
        out_count = ctx.total_count;
        return ctx.total_count > entries_cap ? VN_ERR_BUFFER_TOO_SMALL : VN_OK;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_wtscanner_matches_platform_title(
    const char16_t* title, int32_t title_len, uint8_t* out_matches) noexcept
{
    if (!out_matches) return VN_ERR_INVALID_ARG;
    std::u16string_view sv(title ? title : u"", title && title_len > 0 ? static_cast<size_t>(title_len) : 0);
    *out_matches = vnotch::services::WindowTitleScanner::MatchesPlatformTitle(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_wtscanner_is_browser_process(
    const char16_t* proc_name, int32_t proc_len, uint8_t* out_is_browser) noexcept
{
    if (!out_is_browser) return VN_ERR_INVALID_ARG;
    std::u16string_view sv(proc_name ? proc_name : u"", proc_name && proc_len > 0 ? static_cast<size_t>(proc_len) : 0);
    *out_is_browser = vnotch::services::WindowTitleScanner::IsBrowserProcess(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_wtscanner_is_address_bar_value(
    const char16_t* val, int32_t val_len, uint8_t* out_is_addr) noexcept
{
    if (!out_is_addr) return VN_ERR_INVALID_ARG;
    std::u16string_view sv(val ? val : u"", val && val_len > 0 ? static_cast<size_t>(val_len) : 0);
    *out_is_addr = vnotch::services::WindowTitleScanner::IsAddressBarValue(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_wtscanner_looks_like_url(
    const char16_t* str, int32_t str_len, uint8_t* out_looks_like) noexcept
{
    if (!out_looks_like) return VN_ERR_INVALID_ARG;
    std::u16string_view sv(str ? str : u"", str && str_len > 0 ? static_cast<size_t>(str_len) : 0);
    *out_looks_like = vnotch::services::WindowTitleScanner::LooksLikeUrl(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_wtscanner_normalize_url(
    const char16_t* str, int32_t str_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (!out_len) return VN_ERR_INVALID_ARG;
    *out_len = 0;
    try {
        std::u16string_view sv(str ? str : u"", str && str_len > 0 ? static_cast<size_t>(str_len) : 0);
        auto res = vnotch::services::WindowTitleScanner::NormalizeUrl(sv);
        return vnotch::copy_out_u16(res, out_buf, out_cap, out_len);
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_wtscanner_is_media_url(
    const char16_t* url, int32_t url_len, uint8_t* out_is_media) noexcept
{
    if (!out_is_media) return VN_ERR_INVALID_ARG;
    std::u16string_view sv(url ? url : u"", url && url_len > 0 ? static_cast<size_t>(url_len) : 0);
    *out_is_media = vnotch::services::WindowTitleScanner::IsMediaUrl(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_wtscanner_collect_visible_windows(
    uint32_t exclude_pid,
    uint8_t only_platform_titles,
    VnVisibleWindowEntry* out_entries,
    int32_t entries_cap,
    int32_t* out_count) noexcept
{
    if (!out_count || entries_cap < 0 || (!out_entries && entries_cap > 0)) {
        return VN_ERR_INVALID_ARG;
    }
    return vnotch::services::WindowTitleScanner::CollectVisibleWindows(
        exclude_pid, only_platform_titles, out_entries, entries_cap, *out_count);
}

} // extern "C"
