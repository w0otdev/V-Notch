#include "../vnotch_common.h"
#include "../vnotch_com.h"
#include <winstring.h>
#include <windows.media.control.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cwctype>
#include <string_view>

#pragma comment(lib, "runtimeobject.lib")

namespace vnotch::services {

#pragma pack(push, 1)
struct VnMediaPublishParams {
    uint8_t force_refresh;
    uint8_t metadata_changed;
    uint8_t thumbnail_changed;
    uint8_t playback_changed;
    uint8_t timeline_changed;
    uint8_t source_changed;
    uint8_t seek_capability_changed;
    uint8_t pip_changed;
    uint8_t throttle_changed;
    uint8_t is_throttled;
    uint8_t is_playing;
    int64_t position_ticks;
    int64_t last_position_ticks;
    int64_t duration_ticks;
    int64_t now_utc_ticks;
    int64_t startup_sync_until_utc_ticks;
};
#pragma pack(pop)

class MediaDetectionService final {
public:
    static constexpr double kTicksPerSecond = 10000000.0;

    static constexpr int32_t ResolveHeartbeatIntervalMs(int32_t detection_mode) noexcept {
        switch (detection_mode) {
            case 0: return 5000; // Idle
            case 1: return 3000; // AwaitingMetadata
            case 2: return 3000; // EventDriven
            case 3: return 1500; // ThrottledMedia
            default: return 3000;
        }
    }

    static constexpr bool ShouldSkipHeartbeat(
        int32_t detection_mode,
        int64_t last_event_ticks,
        int64_t now_utc_ticks) noexcept {
        if (detection_mode == 1) return true; // AwaitingMetadata
        if (detection_mode == 0 && last_event_ticks > 0) {
            const double elapsed_sec = (static_cast<double>(now_utc_ticks) - static_cast<double>(last_event_ticks)) / kTicksPerSecond;
            if (elapsed_sec > 10.0) return true;
        }
        return false;
    }

    static bool EvaluateShouldPublish(const VnMediaPublishParams& p) noexcept {
        const double pos_diff_sec = std::abs((static_cast<double>(p.position_ticks) - static_cast<double>(p.last_position_ticks)) / kTicksPerSecond);
        const bool is_throttled = p.is_throttled != 0;
        const bool significant_jump = pos_diff_sec >= (is_throttled ? 5.0 : 1.5);
        const bool in_startup_sync = p.now_utc_ticks <= p.startup_sync_until_utc_ticks;
        const bool startup_timeline_sync = in_startup_sync &&
                                           (p.is_playing != 0) &&
                                           (p.duration_ticks > 0 || p.position_ticks > 0) &&
                                           pos_diff_sec >= 0.2;

        return (p.force_refresh != 0) ||
               (p.metadata_changed != 0) ||
               (p.thumbnail_changed != 0) ||
               (p.playback_changed != 0) ||
               (p.timeline_changed != 0) ||
               (p.source_changed != 0) ||
               (p.pip_changed != 0) ||
               (significant_jump && !is_throttled) ||
               (p.seek_capability_changed != 0) ||
               (p.throttle_changed != 0) ||
               startup_timeline_sync;
    }

    static constexpr bool IsProcNameChar(char16_t c) noexcept {
        return (c >= u'A' && c <= u'Z') ||
               (c >= u'a' && c <= u'z') ||
               (c >= u'0' && c <= u'9') ||
               c == u'_' || c == u'-';
    }

    static constexpr bool IsVideoIdChar(char16_t c) noexcept {
        return IsProcNameChar(c);
    }

    static int32_t ExtractProcessName(std::u16string_view source_app, char16_t* out_buf, int32_t out_cap) noexcept {
        if (source_app.size() < 5 || out_cap <= 0) return 0;
        for (size_t i = 0; i + 4 <= source_app.size(); ++i) {
            if (source_app[i] == u'.' &&
                vnotch::to_lower_ascii(source_app[i + 1]) == u'e' &&
                vnotch::to_lower_ascii(source_app[i + 2]) == u'x' &&
                vnotch::to_lower_ascii(source_app[i + 3]) == u'e') {
                size_t start = i;
                while (start > 0 && IsProcNameChar(source_app[start - 1])) {
                    --start;
                }
                if (start < i) {
                    const int32_t len = std::min(static_cast<int32_t>(i - start), out_cap);
                    for (int32_t k = 0; k < len; ++k) {
                        out_buf[k] = source_app[start + static_cast<size_t>(k)];
                    }
                    return len;
                }
            }
        }
        return 0;
    }

    static int32_t ExtractYouTubeVideoId(std::u16string_view url, char16_t* out_buf, int32_t out_cap) noexcept {
        if (url.size() < 11 || out_cap < 11) return 0;

        // Match youtube.com/watch?.*v= or music.youtube.com/watch?.*v= or youtu.be/
        const size_t watch_pos = url.find(u"youtube.com/watch?");
        if (watch_pos != std::u16string_view::npos) {
            const size_t search_pos = watch_pos + 18;
            size_t best_v = std::u16string_view::npos;
            for (size_t i = search_pos; i + 13 <= url.size(); ++i) {
                if (url[i] == u'v' && url[i + 1] == u'=') {
                    bool valid = true;
                    for (size_t k = 0; k < 11; ++k) {
                        if (!IsVideoIdChar(url[i + 2 + k])) {
                            valid = false;
                            break;
                        }
                    }
                    if (valid) best_v = i + 2;
                }
            }
            if (best_v != std::u16string_view::npos) {
                for (int32_t k = 0; k < 11; ++k) out_buf[k] = url[best_v + static_cast<size_t>(k)];
                return 11;
            }
        }

        const size_t short_pos = url.find(u"youtu.be/");
        if (short_pos != std::u16string_view::npos && short_pos + 9 + 11 <= url.size()) {
            const size_t id_pos = short_pos + 9;
            bool valid = true;
            for (size_t k = 0; k < 11; ++k) {
                if (!IsVideoIdChar(url[id_pos + k])) {
                    valid = false;
                    break;
                }
            }
            if (valid) {
                for (int32_t k = 0; k < 11; ++k) out_buf[k] = url[id_pos + static_cast<size_t>(k)];
                return 11;
            }
        }
        return 0;
    }

    static int32_t CountWords(std::u16string_view s) noexcept {
        int32_t count = 0;
        bool in_word = false;
        for (char16_t c : s) {
            if (c == u' ') {
                in_word = false;
            } else if (!in_word) {
                in_word = true;
                ++count;
            }
        }
        return count;
    }

    static void ParseYouTubeWindowTitle(
        std::u16string_view extracted_title,
        char16_t* out_track, int32_t track_cap, int32_t* out_track_len,
        char16_t* out_artist, int32_t artist_cap, int32_t* out_artist_len) noexcept {
        std::u16string_view track = extracted_title;
        std::u16string_view artist = u"YouTube";

        size_t last_sep = extracted_title.rfind(u" - ");
        if (last_sep != std::u16string_view::npos) {
            const std::u16string_view first_part = vnotch::trim_view(extracted_title.substr(0, last_sep));
            const std::u16string_view second_part = vnotch::trim_view(extracted_title.substr(last_sep + 3));
            const int32_t first_words = CountWords(first_part);
            const int32_t second_words = CountWords(second_part);

            const bool likely_track_first =
                (first_words <= 3 && second_words > first_words) ||
                vnotch::starts_with_ignore_case(second_part, u"by ") ||
                vnotch::contains_ignore_case(second_part, u" by ");

            if (likely_track_first) {
                track = first_part;
                artist = second_part;
            } else {
                track = second_part;
                artist = first_part;
            }
        }

        const int32_t t_len = std::min(static_cast<int32_t>(track.size()), track_cap);
        for (int32_t i = 0; i < t_len; ++i) out_track[i] = track[static_cast<size_t>(i)];
        *out_track_len = t_len;

        const int32_t a_len = std::min(static_cast<int32_t>(artist.size()), artist_cap);
        for (int32_t i = 0; i < a_len; ++i) out_artist[i] = artist[static_cast<size_t>(i)];
        *out_artist_len = a_len;
    }

    static bool EvaluateSpotifySecondaryCorrection(
        std::u16string_view ground_truth,
        std::u16string_view current_track,
        std::u16string_view current_artist,
        char16_t* out_track, int32_t track_cap, int32_t* out_track_len,
        char16_t* out_artist, int32_t artist_cap, int32_t* out_artist_len) noexcept {
        *out_track_len = 0;
        *out_artist_len = 0;
        if (ground_truth.empty() || current_artist.empty()) return false;

        bool needs_correction = false;
        std::u16string_view corrected_track{};
        std::u16string_view corrected_artist{};

        const bool artist_has_sep = current_artist.find(u" - ") != std::u16string_view::npos;
        const bool gt_starts_with_artist_prefix =
            ground_truth.size() >= current_artist.size() + 3 &&
            vnotch::equals_ignore_case(ground_truth.substr(0, current_artist.size()), current_artist) &&
            vnotch::equals_ignore_case(ground_truth.substr(current_artist.size(), 3), u" - ");

        if (artist_has_sep && !gt_starts_with_artist_prefix) {
            const size_t first_sep = ground_truth.find(u" - ");
            if (first_sep != std::u16string_view::npos && first_sep > 0) {
                const std::u16string_view wt_artist = vnotch::trim_view(ground_truth.substr(0, first_sep));
                const std::u16string_view wt_track = vnotch::trim_view(ground_truth.substr(first_sep + 3));
                if (wt_artist.find(u" - ") == std::u16string_view::npos && !wt_track.empty()) {
                    corrected_artist = wt_artist;
                    corrected_track = wt_track;
                    needs_correction = true;
                }
            }
        }

        if (!needs_correction && !current_track.empty() && gt_starts_with_artist_prefix) {
            const std::u16string_view real_track = vnotch::trim_view(ground_truth.substr(current_artist.size() + 3));
            if (!real_track.empty() &&
                !vnotch::equals_ignore_case(real_track, current_track) &&
                vnotch::contains_ignore_case(real_track, current_track) &&
                real_track.size() > current_track.size() + 3) {
                corrected_track = real_track;
                corrected_artist = current_artist;
                needs_correction = true;
            }
        }

        if (!needs_correction) return false;

        const int32_t t_len = std::min(static_cast<int32_t>(corrected_track.size()), track_cap);
        for (int32_t i = 0; i < t_len; ++i) out_track[i] = corrected_track[static_cast<size_t>(i)];
        *out_track_len = t_len;

        const int32_t a_len = std::min(static_cast<int32_t>(corrected_artist.size()), artist_cap);
        for (int32_t i = 0; i < a_len; ++i) out_artist[i] = corrected_artist[static_cast<size_t>(i)];
        *out_artist_len = a_len;
        return true;
    }
};



struct SpotifySearchContext {
    char16_t* out_buf;
    int32_t out_cap;
    int32_t out_len;
};

BOOL CALLBACK EnumSpotifyWindowsProc(HWND hwnd, LPARAM lparam) noexcept {
    if (!IsWindowVisible(hwnd)) return TRUE;
    wchar_t title[512]{};
    const int title_len = GetWindowTextW(hwnd, title, 511);
    if (title_len <= 0) return TRUE;

    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    std::u16string exe_path;
    if (!vnotch::try_get_process_image_path(pid, &exe_path) || exe_path.size() < 11) return TRUE;

    std::u16string_view path_sv(exe_path);
    if (!vnotch::ends_with_ignore_case(path_sv, u"\\spotify.exe") &&
        !vnotch::equals_ignore_case(path_sv, u"spotify.exe")) {
        return TRUE;
    }

    std::u16string_view title_sv(reinterpret_cast<const char16_t*>(title), static_cast<size_t>(title_len));
    if (title_sv == u"Spotify" ||
        title_sv == u"Spotify Premium" ||
        title_sv == u"Spotify Free" ||
        vnotch::ends_with_ignore_case(title_sv, u"spotify")) {
        return TRUE;
    }

    auto* ctx = reinterpret_cast<SpotifySearchContext*>(lparam);
    const int32_t copy_len = std::min(static_cast<int32_t>(title_sv.size()), ctx->out_cap);
    for (int32_t i = 0; i < copy_len; ++i) {
        ctx->out_buf[i] = title_sv[static_cast<size_t>(i)];
    }
    ctx->out_len = copy_len;
    return FALSE;
}

template <typename TAsyncOp>
bool WaitWinRtAsync(TAsyncOp* op, DWORD timeout_ms = 3000) noexcept {
    if (!op) return false;
    vnotch::com::ComPtr<ABI::Windows::Foundation::IAsyncInfo> info;
    if (FAILED(op->QueryInterface(__uuidof(ABI::Windows::Foundation::IAsyncInfo), reinterpret_cast<void**>(info.Put()))) || !info.ptr) {
        return false;
    }
    ABI::Windows::Foundation::AsyncStatus status = ABI::Windows::Foundation::AsyncStatus::Started;
    const ULONGLONG deadline = GetTickCount64() + timeout_ms;
    while (SUCCEEDED(info.ptr->get_Status(&status)) && status == ABI::Windows::Foundation::AsyncStatus::Started) {
        if (GetTickCount64() >= deadline) {
            info.ptr->Cancel();
            return false;
        }
        Sleep(1);
    }
    return status == ABI::Windows::Foundation::AsyncStatus::Completed;
}

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_media_detection_resolve_heartbeat_interval_ms(
    int32_t detection_mode,
    int32_t* out_interval_ms) noexcept {
    if (!out_interval_ms) return VN_ERR_INVALID_ARG;
    *out_interval_ms = vnotch::services::MediaDetectionService::ResolveHeartbeatIntervalMs(detection_mode);
    return VN_OK;
}

VN_EXPORT int32_t vn_media_detection_should_skip_heartbeat(
    int32_t detection_mode,
    int64_t last_event_ticks,
    int64_t now_utc_ticks,
    uint8_t* out_skip) noexcept {
    if (!out_skip) return VN_ERR_INVALID_ARG;
    *out_skip = vnotch::services::MediaDetectionService::ShouldSkipHeartbeat(
        detection_mode, last_event_ticks, now_utc_ticks) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_detection_evaluate_should_publish(
    const vnotch::services::VnMediaPublishParams* params,
    uint8_t* out_should_publish) noexcept {
    if (!params || !out_should_publish) return VN_ERR_INVALID_ARG;
    *out_should_publish = vnotch::services::MediaDetectionService::EvaluateShouldPublish(*params) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_media_detection_build_session_instance_key(
    intptr_t unk_ptr,
    const char16_t* source_app,
    int32_t source_app_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept {
    if (!unk_ptr || !out_buf || out_cap <= 0 || !out_len || source_app_len < 0) return VN_ERR_INVALID_ARG;

    auto* unk = reinterpret_cast<IUnknown*>(unk_ptr);
    vnotch::com::ComPtr<IUnknown> identity;
    if (FAILED(unk->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(identity.Put()))) || !identity) {
        return VN_ERR_FAIL;
    }
    const auto addr = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(identity.get()));
    identity.Reset();

    char16_t hex[16]{};
    int32_t hex_len = 0;
    uint64_t val = addr;
    if (val == 0) {
        hex[hex_len++] = u'0';
    } else {
        while (val > 0 && hex_len < 16) {
            const uint32_t digit = static_cast<uint32_t>(val & 0xFu);
            hex[hex_len++] = static_cast<char16_t>(digit < 10 ? (u'0' + digit) : (u'A' + (digit - 10)));
            val >>= 4;
        }
    }

    const int32_t required = source_app_len + 1 + hex_len;
    *out_len = required;
    if (out_cap < required) return VN_ERR_BUFFER_TOO_SMALL;

    int32_t pos = 0;
    if (source_app && source_app_len > 0) {
        for (int32_t i = 0; i < source_app_len; ++i) out_buf[pos++] = source_app[i];
    }
    out_buf[pos++] = u'|';
    for (int32_t i = hex_len - 1; i >= 0; --i) {
        out_buf[pos++] = hex[i];
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_media_detection_extract_process_name(
    const char16_t* source_app,
    int32_t source_app_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept {
    if (!out_buf || out_cap <= 0 || !out_len || source_app_len < 0) return VN_ERR_INVALID_ARG;
    std::u16string_view sv(source_app ? source_app : u"", static_cast<size_t>(source_app_len));
    *out_len = vnotch::services::MediaDetectionService::ExtractProcessName(sv, out_buf, out_cap);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_media_detection_extract_youtube_video_id(
    const char16_t* url,
    int32_t url_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept {
    if (!out_buf || out_cap <= 0 || !out_len || url_len < 0) return VN_ERR_INVALID_ARG;
    std::u16string_view sv(url ? url : u"", static_cast<size_t>(url_len));
    *out_len = vnotch::services::MediaDetectionService::ExtractYouTubeVideoId(sv, out_buf, out_cap);
    return VN_OK;
}



VN_EXPORT int32_t __stdcall vn_media_detection_get_spotify_window_title(
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept {
    if (!out_buf || out_cap <= 0 || !out_len) return VN_ERR_INVALID_ARG;
    try {
        vnotch::services::SpotifySearchContext ctx{ out_buf, out_cap, 0 };
        EnumWindows(vnotch::services::EnumSpotifyWindowsProc, reinterpret_cast<LPARAM>(&ctx));
        *out_len = ctx.out_len;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_media_detection_parse_youtube_window_title(
    const char16_t* extracted_title,
    int32_t extracted_title_len,
    char16_t* out_track,
    int32_t track_cap,
    int32_t* out_track_len,
    char16_t* out_artist,
    int32_t artist_cap,
    int32_t* out_artist_len) noexcept {
    if (!out_track || track_cap < 0 || !out_track_len || !out_artist || artist_cap < 0 || !out_artist_len || extracted_title_len < 0) {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view sv(extracted_title ? extracted_title : u"", static_cast<size_t>(extracted_title_len));
    vnotch::services::MediaDetectionService::ParseYouTubeWindowTitle(
        sv, out_track, track_cap, out_track_len, out_artist, artist_cap, out_artist_len);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_media_detection_evaluate_spotify_secondary_correction(
    const char16_t* ground_truth,
    int32_t ground_truth_len,
    const char16_t* current_track,
    int32_t current_track_len,
    const char16_t* current_artist,
    int32_t current_artist_len,
    char16_t* out_track,
    int32_t track_cap,
    int32_t* out_track_len,
    char16_t* out_artist,
    int32_t artist_cap,
    int32_t* out_artist_len,
    uint8_t* out_corrected) noexcept {
    if (!out_track || track_cap < 0 || !out_track_len || !out_artist || artist_cap < 0 || !out_artist_len || !out_corrected ||
        ground_truth_len < 0 || current_track_len < 0 || current_artist_len < 0) {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view gt_sv(ground_truth ? ground_truth : u"", static_cast<size_t>(ground_truth_len));
    std::u16string_view tr_sv(current_track ? current_track : u"", static_cast<size_t>(current_track_len));
    std::u16string_view ar_sv(current_artist ? current_artist : u"", static_cast<size_t>(current_artist_len));
    const bool corrected = vnotch::services::MediaDetectionService::EvaluateSpotifySecondaryCorrection(
        gt_sv, tr_sv, ar_sv, out_track, track_cap, out_track_len, out_artist, artist_cap, out_artist_len);
    *out_corrected = corrected ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_gsmtc_get_source_app_id(
    intptr_t session_ptr,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept {
    if (!session_ptr || !out_buf || out_cap < 0 || !out_len) return VN_ERR_INVALID_ARG;
    *out_len = 0;

    auto* unk = reinterpret_cast<IUnknown*>(session_ptr);
    vnotch::com::ComPtr<ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSession> session;
    if (FAILED(unk->QueryInterface(__uuidof(ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSession), reinterpret_cast<void**>(session.Put()))) || !session.ptr) {
        return VN_ERR_FAIL;
    }

    HSTRING hstr = nullptr;
    if (FAILED(session.ptr->get_SourceAppUserModelId(&hstr))) return VN_ERR_FAIL;
    if (!hstr) return VN_OK;

    UINT32 raw_len = 0;
    const wchar_t* raw = WindowsGetStringRawBuffer(hstr, &raw_len);
    const int32_t copy_len = std::min(static_cast<int32_t>(raw_len), out_cap);
    for (int32_t i = 0; i < copy_len; ++i) {
        out_buf[i] = static_cast<char16_t>(raw[i]);
    }
    *out_len = copy_len;
    WindowsDeleteString(hstr);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_gsmtc_get_controls(
    intptr_t session_ptr,
    uint8_t* out_has_controls,
    uint8_t* out_is_next_enabled,
    uint8_t* out_is_prev_enabled) noexcept {
    if (!session_ptr || !out_has_controls || !out_is_next_enabled || !out_is_prev_enabled) return VN_ERR_INVALID_ARG;
    *out_has_controls = 0;
    *out_is_next_enabled = 0;
    *out_is_prev_enabled = 0;

    auto* unk = reinterpret_cast<IUnknown*>(session_ptr);
    vnotch::com::ComPtr<ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSession> session;
    if (FAILED(unk->QueryInterface(__uuidof(ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSession), reinterpret_cast<void**>(session.Put()))) || !session.ptr) {
        return VN_ERR_FAIL;
    }

    vnotch::com::ComPtr<ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSessionPlaybackInfo> pb_info;
    if (FAILED(session.ptr->GetPlaybackInfo(pb_info.Put())) || !pb_info.ptr) return VN_OK;

    vnotch::com::ComPtr<ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSessionPlaybackControls> controls;
    if (FAILED(pb_info.ptr->get_Controls(controls.Put())) || !controls.ptr) return VN_OK;

    boolean next_enabled = 0;
    boolean prev_enabled = 0;
    if (FAILED(controls.ptr->get_IsNextEnabled(&next_enabled)) || FAILED(controls.ptr->get_IsPreviousEnabled(&prev_enabled))) {
        return VN_ERR_FAIL;
    }
    *out_has_controls = 1;
    *out_is_next_enabled = next_enabled ? 1u : 0u;
    *out_is_prev_enabled = prev_enabled ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_gsmtc_get_timeline(
    intptr_t session_ptr,
    uint8_t* out_has_timeline,
    int64_t* out_start_ticks,
    int64_t* out_end_ticks,
    int64_t* out_position_ticks) noexcept {
    if (!session_ptr || !out_has_timeline || !out_start_ticks || !out_end_ticks || !out_position_ticks) return VN_ERR_INVALID_ARG;
    *out_has_timeline = 0;
    *out_start_ticks = 0;
    *out_end_ticks = 0;
    *out_position_ticks = 0;

    auto* unk = reinterpret_cast<IUnknown*>(session_ptr);
    vnotch::com::ComPtr<ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSession> session;
    if (FAILED(unk->QueryInterface(__uuidof(ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSession), reinterpret_cast<void**>(session.Put()))) || !session.ptr) {
        return VN_ERR_FAIL;
    }

    vnotch::com::ComPtr<ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSessionTimelineProperties> timeline;
    if (FAILED(session.ptr->GetTimelineProperties(timeline.Put())) || !timeline.ptr) return VN_OK;

    ABI::Windows::Foundation::TimeSpan start{};
    ABI::Windows::Foundation::TimeSpan end{};
    ABI::Windows::Foundation::TimeSpan pos{};
    if (FAILED(timeline.ptr->get_StartTime(&start)) || FAILED(timeline.ptr->get_EndTime(&end)) || FAILED(timeline.ptr->get_Position(&pos))) {
        return VN_ERR_FAIL;
    }
    *out_has_timeline = 1;
    *out_start_ticks = start.Duration;
    *out_end_ticks = end.Duration;
    *out_position_ticks = pos.Duration;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_gsmtc_try_action(
    intptr_t session_ptr,
    int32_t action,
    int64_t position_ticks,
    uint8_t* out_success) noexcept {
    if (!session_ptr || !out_success) return VN_ERR_INVALID_ARG;
    *out_success = 0;

    vnotch::com::EnsureMta();
    auto* unk = reinterpret_cast<IUnknown*>(session_ptr);
    vnotch::com::ComPtr<ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSession> session;
    if (FAILED(unk->QueryInterface(__uuidof(ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSession), reinterpret_cast<void**>(session.Put()))) || !session.ptr) {
        return VN_ERR_FAIL;
    }

    vnotch::com::ComPtr<ABI::Windows::Foundation::IAsyncOperation<bool>> op;
    HRESULT hr = E_INVALIDARG;
    switch (action) {
        case 0: hr = session.ptr->TryTogglePlayPauseAsync(op.Put()); break;
        case 1: hr = session.ptr->TrySkipNextAsync(op.Put()); break;
        case 2: hr = session.ptr->TrySkipPreviousAsync(op.Put()); break;
        case 3: hr = session.ptr->TryChangePlaybackPositionAsync(position_ticks, op.Put()); break;
        default: return VN_ERR_INVALID_ARG;
    }
    if (FAILED(hr) || !op.ptr) return VN_ERR_FAIL;
    if (!vnotch::services::WaitWinRtAsync(op.ptr)) return VN_ERR_FAIL;

    boolean res = 0;
    if (FAILED(op.ptr->GetResults(&res))) return VN_ERR_FAIL;
    *out_success = res ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_gsmtc_get_title(
    intptr_t session_ptr,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len,
    uint8_t* out_has_title) noexcept {
    if (!session_ptr || !out_buf || out_cap < 0 || !out_len || !out_has_title) return VN_ERR_INVALID_ARG;
    *out_len = 0;
    *out_has_title = 0;

    vnotch::com::EnsureMta();
    auto* unk = reinterpret_cast<IUnknown*>(session_ptr);
    vnotch::com::ComPtr<ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSession> session;
    if (FAILED(unk->QueryInterface(__uuidof(ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSession), reinterpret_cast<void**>(session.Put()))) || !session.ptr) {
        return VN_ERR_FAIL;
    }

    vnotch::com::ComPtr<ABI::Windows::Foundation::IAsyncOperation<ABI::Windows::Media::Control::GlobalSystemMediaTransportControlsSessionMediaProperties*>> op;
    if (FAILED(session.ptr->TryGetMediaPropertiesAsync(op.Put())) || !op.ptr) return VN_ERR_FAIL;
    if (!vnotch::services::WaitWinRtAsync(op.ptr)) return VN_ERR_FAIL;

    vnotch::com::ComPtr<ABI::Windows::Media::Control::IGlobalSystemMediaTransportControlsSessionMediaProperties> props;
    if (FAILED(op.ptr->GetResults(props.Put())) || !props.ptr) return VN_OK;

    HSTRING hstr = nullptr;
    if (FAILED(props.ptr->get_Title(&hstr))) return VN_ERR_FAIL;
    *out_has_title = 1;
    if (!hstr) return VN_OK;

    UINT32 raw_len = 0;
    const wchar_t* raw = WindowsGetStringRawBuffer(hstr, &raw_len);
    const int32_t copy_len = std::min(static_cast<int32_t>(raw_len), out_cap);
    for (int32_t i = 0; i < copy_len; ++i) {
        out_buf[i] = static_cast<char16_t>(raw[i]);
    }
    *out_len = copy_len;
    WindowsDeleteString(hstr);
    return VN_OK;
}

} // extern "C"
