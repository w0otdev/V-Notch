#include "vnotch_common.h"
#include <array>

VN_EXPORT int32_t vn_platform_extract_title_from_window(const char16_t*, int32_t, const char16_t*, int32_t, char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_platform_parse_soundcloud_title(const char16_t*, int32_t, char16_t*, int32_t, int32_t*, char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_media_detection_parse_youtube_window_title(const char16_t*, int32_t, char16_t*, int32_t, int32_t*, char16_t*, int32_t, int32_t*) noexcept;

namespace {
struct Match {
    int32_t platform = 0, extracted_length = 0, track_length = 0, artist_length = 0;
    std::array<char16_t, 4096> extracted{}, track{}, artist{};
};
bool parse(std::u16string_view title, bool recovery, Match& match) noexcept {
    using namespace vnotch;
    for (const auto platform : {std::u16string_view(u"YouTube"), std::u16string_view(u"SoundCloud")}) {
        if (recovery && platform == u"SoundCloud") continue;
        if (!contains_ignore_case(title, platform) || equals_ignore_case(title, platform) ||
            starts_with_ignore_case(title, platform == u"YouTube" ? u"YouTube -" : u"SoundCloud -")) continue;
        if (vn_platform_extract_title_from_window(title.data(), static_cast<int32_t>(title.size()), platform.data(),
            static_cast<int32_t>(platform.size()), match.extracted.data(), 4096, &match.extracted_length) != VN_OK) continue;
        const auto extracted = std::u16string_view(match.extracted.data(), match.extracted_length);
        if (is_null_or_whitespace(extracted)) continue;
        match.platform = platform == u"YouTube" ? 2 : 3;
        if (match.platform == 3) {
            if (vn_platform_parse_soundcloud_title(extracted.data(), match.extracted_length, match.artist.data(), 4096,
                &match.artist_length, match.track.data(), 4096, &match.track_length) != VN_OK) continue;
        } else if (recovery) {
            if (vn_media_detection_parse_youtube_window_title(extracted.data(), match.extracted_length, match.track.data(), 4096,
                &match.track_length, match.artist.data(), 4096, &match.artist_length) != VN_OK) continue;
            const auto track = std::u16string_view(match.track.data(), match.track_length);
            if (is_null_or_whitespace(track) || equals_ignore_case(track, u"YouTube") || starts_with_ignore_case(track, u"- YouTube")) continue;
        } else {
            std::copy_n(match.extracted.data(), match.extracted_length, match.track.data());
            match.track_length = match.extracted_length;
            std::copy_n(u"YouTube", 7, match.artist.data()); match.artist_length = 7;
        }
        return true;
    }
    match.platform = 0;
    return false;
}
struct Scan { uint32_t excluded; bool recovery; Match* match; };
BOOL CALLBACK scan(HWND window, LPARAM parameter) noexcept {
    auto& state = *reinterpret_cast<Scan*>(parameter);
    if (!IsWindowVisible(window)) return TRUE;
    DWORD pid = 0; GetWindowThreadProcessId(window, &pid);
    if (!pid || pid == state.excluded) return TRUE;
    wchar_t title[4096];
    const int length = GetWindowTextW(window, title, ARRAYSIZE(title));
    // Do not use a truncated caption as an authoritative track name.
    if (length <= 0 || length == ARRAYSIZE(title) - 1) return TRUE;
    return parse({reinterpret_cast<const char16_t*>(title), static_cast<size_t>(length)}, state.recovery, *state.match) ? FALSE : TRUE;
}
struct TitleMatch { uint32_t excluded; std::u16string track; bool found = false, failed = false; };
BOOL CALLBACK match_youtube(HWND window, LPARAM parameter) noexcept {
    auto& state = *reinterpret_cast<TitleMatch*>(parameter);
    if (!IsWindowVisible(window)) return TRUE;
    DWORD pid = 0; GetWindowThreadProcessId(window, &pid);
    if (!pid || pid == state.excluded) return TRUE;
    wchar_t buffer[4096]; const int length = GetWindowTextW(window, buffer, ARRAYSIZE(buffer));
    if (length <= 0 || length == ARRAYSIZE(buffer) - 1) return TRUE;
    const auto title = std::u16string_view(reinterpret_cast<const char16_t*>(buffer), length);
    if (!vnotch::contains_ignore_case(title, u"YouTube")) return TRUE;
    try {
        state.found = vnotch::normalize_for_loose_match_impl(title).find(state.track) != std::u16string::npos;
        return state.found ? FALSE : TRUE;
    } catch (...) { state.failed = true; return FALSE; }
}
}

VN_EXPORT int32_t vn_wtscanner_has_youtube_title(const char16_t* track, int32_t length, uint32_t excluded_pid, uint8_t* found) noexcept {
    if (!track || length < 0 || !found) return VN_ERR_INVALID_ARG;
    *found = 0;
    try {
        TitleMatch state{excluded_pid, vnotch::normalize_for_loose_match_impl({track, static_cast<size_t>(length)})};
        if (state.track.empty()) return VN_OK;
        EnumWindows(match_youtube, reinterpret_cast<LPARAM>(&state));
        if (state.failed) return VN_ERR_FAIL;
        *found = state.found ? 1 : 0;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_wtscanner_media_title(const char16_t* title, int32_t length, uint32_t excluded_pid, uint8_t recovery,
    char16_t* output, int32_t capacity, int32_t* platform, int32_t* extracted_length, int32_t* track_length, int32_t* artist_length) noexcept {
    if (length < 0 || (!title && length) || !output || capacity < 0 || !platform || !extracted_length || !track_length || !artist_length) return VN_ERR_INVALID_ARG;
    *platform = *extracted_length = *track_length = *artist_length = 0;
    Match match;
    if (title) parse({title, static_cast<size_t>(length)}, recovery != 0, match);
    else { Scan state{excluded_pid, recovery != 0, &match}; EnumWindows(scan, reinterpret_cast<LPARAM>(&state)); }
    if (!match.platform) return VN_OK;
    const int total = match.extracted_length + match.track_length + match.artist_length;
    if (capacity < total) return VN_ERR_BUFFER_TOO_SMALL;
    std::copy_n(match.extracted.data(), match.extracted_length, output);
    std::copy_n(match.track.data(), match.track_length, output + match.extracted_length);
    std::copy_n(match.artist.data(), match.artist_length, output + match.extracted_length + match.track_length);
    *platform = match.platform; *extracted_length = match.extracted_length;
    *track_length = match.track_length; *artist_length = match.artist_length;
    return VN_OK;
}
