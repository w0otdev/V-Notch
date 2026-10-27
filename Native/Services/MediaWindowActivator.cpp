#include "vnotch_common.h"
#include <windows.h>
#include <array>
#include <cstdint>
#include <cwctype>
#include <string>
#include <string_view>

namespace vnotch::services {

class MediaWindowActivator final {
public:
    static bool IsBrowserProcess(std::u16string_view process_name) noexcept {
        if (process_name.empty()) return false;
        static constexpr std::array<std::u16string_view, 21> kKnownBrowserProcesses = {
            u"msedge", u"chrome", u"firefox", u"brave", u"opera", u"vivaldi",
            u"browser", u"arc", u"sidekick", u"zen", u"coccoc", u"thorium",
            u"waterfox", u"floorp", u"librewolf", u"chromium", u"whale",
            u"yandex", u"wavebox", u"helium", u"supermium"
        };
        for (auto b : kKnownBrowserProcesses) {
            if (vnotch::equals_ignore_case(process_name, b)) return true;
        }
        return false;
    }

    static bool IsChromiumWindow(HWND hwnd) noexcept {
        if (!hwnd) return false;
        wchar_t class_name[256]{};
        int len = ::GetClassNameW(hwnd, class_name, 256);
        if (len <= 0) return false;
        std::wstring_view sv(class_name, static_cast<size_t>(len));
        return sv.starts_with(L"Chrome_WidgetWin_");
    }

    static bool TryActivateWindow(HWND hwnd) noexcept {
        if (!hwnd) return false;
        if (::IsIconic(hwnd)) {
            ::ShowWindow(hwnd, SW_RESTORE);
        } else {
            ::ShowWindow(hwnd, SW_SHOW);
        }
        return ::SetForegroundWindow(hwnd) != FALSE;
    }

    static std::u16string NormalizeTitle(std::u16string_view value) {
        if (vnotch::is_null_or_whitespace(value)) return {};
        std::u16string out;
        out.reserve(value.size());
        bool in_ws = false;
        for (char16_t ch : value) {
            if (vnotch::is_unicode_whitespace(ch)) {
                if (!out.empty()) in_ws = true;
            } else {
                if (in_ws) {
                    out.push_back(u' ');
                    in_ws = false;
                }
                out.push_back(vnotch::to_lower_invariant(ch));
            }
        }
        return out;
    }

    static std::u16string NormalizeMediaTitle(std::u16string_view value, std::u16string_view source) {
        std::u16string title = NormalizeTitle(value);
        std::u16string platform = NormalizeTitle(source);
        if (!platform.empty()) {
            std::u16string suffix = u" - ";
            suffix.append(platform);
            if (vnotch::ends_with_ordinal(title, suffix)) {
                title.resize(title.size() - suffix.size());
            }
        }
        return title;
    }

    static int32_t ScorePlatformKeywords(std::u16string_view title_lower, std::u16string_view help_lower, vnotch::VnMediaPlatform platform) noexcept {
        auto match = [&](std::u16string_view token) noexcept {
            return vnotch::contains_ordinal(title_lower, token) || vnotch::contains_ordinal(help_lower, token);
        };

        switch (platform) {
            case vnotch::VnMediaPlatform::YouTube: return match(u"youtube") ? 50 : 0;
            case vnotch::VnMediaPlatform::Twitch: return match(u"twitch") ? 50 : 0;
            case vnotch::VnMediaPlatform::Discord: return (match(u"discord") || vnotch::contains_ordinal(title_lower, u"vesktop")) ? 50 : 0;
            case vnotch::VnMediaPlatform::SoundCloud: return match(u"soundcloud") ? 50 : 0;
            case vnotch::VnMediaPlatform::Spotify: return match(u"spotify") ? 50 : 0;
            case vnotch::VnMediaPlatform::Tidal: return match(u"tidal") ? 50 : 0;
            case vnotch::VnMediaPlatform::Deezer: return match(u"deezer") ? 50 : 0;
            case vnotch::VnMediaPlatform::Bandcamp: return match(u"bandcamp") ? 50 : 0;
            case vnotch::VnMediaPlatform::Netflix: return match(u"netflix") ? 50 : 0;
            case vnotch::VnMediaPlatform::Bilibili: return (vnotch::contains_ordinal(title_lower, u"bilibili") || vnotch::contains_ordinal(title_lower, u"\x54D4\x54E9\x54D4\x54E9")) ? 50 : 0;
            case vnotch::VnMediaPlatform::Vimeo: return match(u"vimeo") ? 50 : 0;
            case vnotch::VnMediaPlatform::Instagram: return match(u"instagram") ? 50 : 0;
            case vnotch::VnMediaPlatform::Twitter:
                return (vnotch::contains_ordinal(title_lower, u"twitter") ||
                        vnotch::contains_ordinal(title_lower, u"x.com") ||
                        vnotch::contains_ordinal(title_lower, u" / x") ||
                        vnotch::starts_with_ordinal(title_lower, u"/ x")) ? 50 : 0;
            default: return 0;
        }
    }

    static int32_t ScoreTabItem(
        std::u16string_view tab_title,
        std::u16string_view tab_help,
        std::u16string_view current_track,
        std::u16string_view current_artist,
        std::u16string_view youtube_title,
        std::u16string_view youtube_video_id,
        std::u16string_view media_source,
        int32_t platform)
    {
        if (vnotch::is_null_or_whitespace(tab_title) && vnotch::is_null_or_whitespace(tab_help)) {
            return 0;
        }

        int32_t score = 0;
        std::u16string title_lower = NormalizeTitle(tab_title);
        std::u16string help_lower = vnotch::to_lower_string(tab_help);

        std::u16string track = NormalizeMediaTitle(current_track, media_source);
        std::u16string artist = NormalizeTitle(current_artist);
        std::u16string yt_title = NormalizeMediaTitle(youtube_title, media_source);
        std::u16string video_id = vnotch::to_lower_string(vnotch::trim_view(youtube_video_id));

        if (!video_id.empty() && (vnotch::contains_ordinal(help_lower, video_id) || vnotch::contains_ordinal(title_lower, video_id))) {
            score += 200;
        }

        if (!yt_title.empty() && yt_title.size() > 2 && vnotch::contains_ordinal(title_lower, yt_title)) {
            score += 150;
        }

        if (!track.empty() && track.size() > 2 && vnotch::contains_ordinal(title_lower, track)) {
            score += 140;
        }

        if (!artist.empty() && artist.size() > 2 &&
            artist != u"youtube" && artist != u"browser" && artist != u"spotify" && artist != u"soundcloud" &&
            vnotch::contains_ordinal(title_lower, artist)) {
            score += 70;
        }

        score += ScorePlatformKeywords(title_lower, help_lower, static_cast<vnotch::VnMediaPlatform>(platform));
        return score;
    }

    static int32_t ScoreWindowPlatform(std::u16string_view title, vnotch::VnMediaPlatform platform) noexcept {
        auto match = [&](std::u16string_view token) noexcept {
            return vnotch::contains_ignore_case(title, token);
        };

        switch (platform) {
            case vnotch::VnMediaPlatform::YouTube: return match(u"YouTube") ? 90 : 0;
            case vnotch::VnMediaPlatform::Twitch: return match(u"Twitch") ? 90 : 0;
            case vnotch::VnMediaPlatform::Discord: return (match(u"Discord") || match(u"Vesktop")) ? 90 : 0;
            case vnotch::VnMediaPlatform::SoundCloud: return match(u"SoundCloud") ? 90 : 0;
            case vnotch::VnMediaPlatform::Tidal: return match(u"TIDAL") ? 90 : 0;
            case vnotch::VnMediaPlatform::Deezer: return match(u"Deezer") ? 90 : 0;
            case vnotch::VnMediaPlatform::Bandcamp: return match(u"Bandcamp") ? 90 : 0;
            case vnotch::VnMediaPlatform::Netflix: return match(u"Netflix") ? 90 : 0;
            case vnotch::VnMediaPlatform::Bilibili: return (match(u"bilibili") || vnotch::contains_ordinal(title, u"\x54D4\x54E9\x54D4\x54E9")) ? 90 : 0;
            case vnotch::VnMediaPlatform::Vimeo: return match(u"Vimeo") ? 90 : 0;
            case vnotch::VnMediaPlatform::Instagram: return match(u"Instagram") ? 90 : 0;
            case vnotch::VnMediaPlatform::Twitter: return (match(u"Twitter") || match(u" / X")) ? 90 : 0;
            default: return 0;
        }
    }

    static int32_t ScoreWindow(
        std::u16string_view title,
        std::u16string_view process_name,
        std::u16string_view current_track,
        std::u16string_view current_artist,
        std::u16string_view media_source,
        int32_t platform,
        bool is_video_source,
        bool is_pip_title)
    {
        int32_t score = 0;
        std::u16string track = NormalizeMediaTitle(current_track, media_source);
        std::u16string artist = NormalizeTitle(current_artist);
        std::u16string window = NormalizeTitle(title);

        if (!vnotch::is_null_or_whitespace(media_source) && vnotch::contains_ignore_case(title, media_source)) {
            score += 80;
        }
        if (!track.empty() && vnotch::contains_ignore_case(window, track)) {
            score += 140;
        }
        if (!artist.empty() &&
            artist != u"youtube" && artist != u"browser" && artist != u"spotify" && artist != u"soundcloud" &&
            vnotch::contains_ignore_case(window, artist)) {
            score += 70;
        }

        if (is_pip_title) {
            score += 220;
        }

        auto plat = static_cast<vnotch::VnMediaPlatform>(platform);
        score += ScoreWindowPlatform(title, plat);

        if ((is_video_source || plat == vnotch::VnMediaPlatform::SoundCloud) && IsBrowserProcess(process_name)) {
            score += 25;
        }

        return score;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_mediaactivator_is_browser_process(
    const char16_t* proc_name, int32_t proc_len, uint8_t* out_is_browser) noexcept
{
    if (!out_is_browser) return VN_ERR_INVALID_ARG;
    *out_is_browser = vnotch::services::MediaWindowActivator::IsBrowserProcess(
        vnotch::make_u16_view(proc_name, proc_len)) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_mediaactivator_is_chromium_window(
    intptr_t hwnd, uint8_t* out_is_chromium) noexcept
{
    if (!out_is_chromium) return VN_ERR_INVALID_ARG;
    *out_is_chromium = vnotch::services::MediaWindowActivator::IsChromiumWindow(reinterpret_cast<HWND>(hwnd)) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_mediaactivator_try_activate_window(
    intptr_t hwnd, uint8_t* out_activated) noexcept
{
    if (!out_activated) return VN_ERR_INVALID_ARG;
    *out_activated = vnotch::services::MediaWindowActivator::TryActivateWindow(reinterpret_cast<HWND>(hwnd)) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_mediaactivator_normalize_title(
    const char16_t* val, int32_t val_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (!out_len) return VN_ERR_INVALID_ARG;
    *out_len = 0;
    try {
        auto res = vnotch::services::MediaWindowActivator::NormalizeTitle(vnotch::make_u16_view(val, val_len));
        return vnotch::copy_out_u16(res, out_buf, out_cap, out_len);
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_mediaactivator_normalize_media_title(
    const char16_t* val, int32_t val_len,
    const char16_t* src, int32_t src_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (!out_len) return VN_ERR_INVALID_ARG;
    *out_len = 0;
    try {
        auto res = vnotch::services::MediaWindowActivator::NormalizeMediaTitle(
            vnotch::make_u16_view(val, val_len),
            vnotch::make_u16_view(src, src_len));
        return vnotch::copy_out_u16(res, out_buf, out_cap, out_len);
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_mediaactivator_score_tab_item(
    const char16_t* tab_title, int32_t tab_title_len,
    const char16_t* tab_help, int32_t tab_help_len,
    const char16_t* track, int32_t track_len,
    const char16_t* artist, int32_t artist_len,
    const char16_t* yt_title, int32_t yt_title_len,
    const char16_t* yt_video_id, int32_t yt_video_id_len,
    const char16_t* media_src, int32_t media_src_len,
    int32_t platform,
    int32_t* out_score) noexcept
{
    if (!out_score) return VN_ERR_INVALID_ARG;
    *out_score = 0;
    try {
        *out_score = vnotch::services::MediaWindowActivator::ScoreTabItem(
            vnotch::make_u16_view(tab_title, tab_title_len),
            vnotch::make_u16_view(tab_help, tab_help_len),
            vnotch::make_u16_view(track, track_len),
            vnotch::make_u16_view(artist, artist_len),
            vnotch::make_u16_view(yt_title, yt_title_len),
            vnotch::make_u16_view(yt_video_id, yt_video_id_len),
            vnotch::make_u16_view(media_src, media_src_len),
            platform);
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_mediaactivator_score_window(
    const char16_t* title, int32_t title_len,
    const char16_t* proc_name, int32_t proc_len,
    const char16_t* track, int32_t track_len,
    const char16_t* artist, int32_t artist_len,
    const char16_t* media_src, int32_t media_src_len,
    int32_t platform,
    uint8_t is_video_source,
    uint8_t is_pip_title,
    int32_t* out_score) noexcept
{
    if (!out_score) return VN_ERR_INVALID_ARG;
    *out_score = 0;
    try {
        *out_score = vnotch::services::MediaWindowActivator::ScoreWindow(
            vnotch::make_u16_view(title, title_len),
            vnotch::make_u16_view(proc_name, proc_len),
            vnotch::make_u16_view(track, track_len),
            vnotch::make_u16_view(artist, artist_len),
            vnotch::make_u16_view(media_src, media_src_len),
            platform,
            is_video_source != 0,
            is_pip_title != 0);
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}



VN_EXPORT int32_t vn_mediaactivator_score_window_platform(
    const char16_t* title, int32_t title_len,
    int32_t platform,
    int32_t* out_score) noexcept
{
    if (!out_score) return VN_ERR_INVALID_ARG;
    try {
        *out_score = vnotch::services::MediaWindowActivator::ScoreWindowPlatform(
            vnotch::make_u16_view(title, title_len),
            static_cast<vnotch::VnMediaPlatform>(platform));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
