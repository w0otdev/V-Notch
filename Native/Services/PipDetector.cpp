#include "vnotch_common.h"
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <cwctype>
#include <string>
#include <string_view>
#include <vector>

namespace vnotch::services {

class PipDetector final {
public:
    static bool IsPipTitle(std::u16string_view title) {
        std::u16string_view normalized = Trim(title);
        if (normalized.empty()) return false;

        std::u16string lower = ToLower(normalized);

        static constexpr std::array<std::u16string_view, 21> kKeywords = {
            u"picture in picture",
            u"picture-in-picture",
            u"hình trong hình",
            u"hinh trong hinh",
            u"bild-in-bild",
            u"image dans l'image",
            u"pantalla en pantalla",
            u"cuadro en cuadro",
            u"画中画",
            u"畫中畫",
            u"子母画面",
            u"子母畫面",
            u"ピクチャー イン ピクチャー",
            u"ピクチャーインピクチャー",
            u"картинка в картинке",
            u"imagem na imagem",
            u"imagem sobre imagem",
            u"finestra mobile",
            u"gambar dalam gambar",
            u"resim içinde resim",
            u"화면 속 화면"
        };

        for (const auto& kw : kKeywords) {
            if (lower.find(kw) != std::u16string::npos) {
                return true;
            }
        }

        std::u16string_view lv(lower);
        return lv == u"pip"
            || HasEdgeLabel(lv, u"(pip)")
            || HasEdgeLabel(lv, u"[pip]")
            || lv.ends_with(u" - pip");
    }

    static bool IsKnownBrowserProcess(std::u16string_view process_name) {
        std::u16string_view trimmed = Trim(process_name);
        if (trimmed.empty()) return false;

        std::u16string lower = ToLower(trimmed);
        std::u16string_view cleaned(lower);
        if (cleaned.ends_with(u".exe")) {
            cleaned.remove_suffix(4);
        }

        static constexpr std::array<std::u16string_view, 21> kBrowsers = {
            u"chrome", u"msedge", u"firefox", u"brave", u"opera", u"vivaldi", u"zen",
            u"arc", u"coccoc", u"sidekick", u"thorium", u"waterfox", u"floorp", u"librewolf",
            u"chromium", u"whale", u"yandex", u"wavebox", u"helium", u"supermium", u"browser"
        };

        for (const auto& b : kBrowsers) {
            if (cleaned == b) return true;
        }
        return false;
    }

    static bool IsPipWindow(
        HWND hwnd,
        std::u16string_view target_process_name,
        std::u16string_view current_track,
        std::u16string* out_window_title)
    {
        if (out_window_title) out_window_title->clear();
        if (hwnd == nullptr || !IsWindowVisible(hwnd) || IsIconic(hwnd)) {
            return false;
        }

        LONG ex_style = GetWindowLongW(hwnd, GWL_EXSTYLE);
        if ((ex_style & WS_EX_TOPMOST) == 0) {
            return false;
        }

        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        if (pid == 0) {
            return false;
        }

        std::u16string proc_name = TryGetProcessNameLower(pid);
        if (proc_name.empty()) {
            return false;
        }

        bool proc_matches = false;
        if (!target_process_name.empty()) {
            std::u16string target_lower = ToLower(target_process_name);
            proc_matches = (proc_name == target_lower) || (target_lower.find(proc_name) != std::u16string::npos);
        }

        if (!proc_matches && !IsKnownBrowserProcess(proc_name)) {
            return false;
        }

        std::u16string title = GetWindowTitle(hwnd);
        if (IsPipTitle(title)) {
            if (out_window_title) *out_window_title = title;
            return true;
        }

        std::u16string_view trimmed_track = Trim(current_track);
        std::u16string_view trimmed_title = Trim(title);
        if (!trimmed_track.empty() && !trimmed_title.empty()) {
            std::u16string title_lower = ToLower(title);
            std::u16string track_lower = ToLower(trimmed_track);
            if (title_lower.find(track_lower) != std::u16string::npos) {
                if (out_window_title) *out_window_title = title;
                return true;
            }
        }

        return false;
    }

    static bool TryFindPipWindow(
        std::u16string_view target_process_name,
        std::u16string_view current_track,
        HWND* out_hwnd,
        std::u16string* out_title)
    {
        *out_hwnd = nullptr;
        out_title->clear();

        struct SearchCtx {
            std::u16string_view target_process_name;
            std::u16string_view current_track;
            HWND found_hwnd = nullptr;
            std::u16string found_title;
        } ctx{target_process_name, current_track, nullptr, {}};

        EnumWindows([](HWND hwnd, LPARAM lparam) -> BOOL {
            try {
                auto* c = reinterpret_cast<SearchCtx*>(lparam);
                std::u16string title;
                if (PipDetector::IsPipWindow(hwnd, c->target_process_name, c->current_track, &title)) {
                    c->found_hwnd = hwnd;
                    c->found_title = std::move(title);
                    return FALSE;
                }
                return TRUE;
            } catch (...) {
                return FALSE;
            }
        }, reinterpret_cast<LPARAM>(&ctx));

        *out_hwnd = ctx.found_hwnd;
        *out_title = std::move(ctx.found_title);
        return ctx.found_hwnd != nullptr;
    }

private:
    static bool IsWhiteSpace(char16_t ch) noexcept {
        return std::iswspace(static_cast<wint_t>(ch)) != 0 || ch == u'\u200B' || ch == u'\uFEFF';
    }

    static std::u16string_view Trim(std::u16string_view s) noexcept {
        size_t start = 0;
        size_t end = s.size();
        while (start < end && IsWhiteSpace(s[start])) start++;
        while (end > start && IsWhiteSpace(s[end - 1])) end--;
        return s.substr(start, end - start);
    }

    static std::u16string ToLower(std::u16string_view s) {
        std::u16string res(s);
        if (!res.empty()) {
            CharLowerBuffW(reinterpret_cast<LPWSTR>(res.data()), static_cast<DWORD>(res.size()));
        }
        return res;
    }

    static bool HasEdgeLabel(std::u16string_view title_lower, std::u16string_view label_lower) noexcept {
        if (title_lower == label_lower) return true;
        if (title_lower.size() <= label_lower.size()) return false;
        if (title_lower.starts_with(label_lower) && IsWhiteSpace(title_lower[label_lower.size()])) {
            return true;
        }
        if (title_lower.ends_with(label_lower) && IsWhiteSpace(title_lower[title_lower.size() - label_lower.size() - 1])) {
            return true;
        }
        return false;
    }

    static std::u16string TryGetProcessNameLower(DWORD pid) {
        std::u16string full;
        if (!vnotch::try_get_process_image_path(pid, &full) || full.empty()) return {};
        return ToLower(vnotch::get_stem(full));
    }

    static std::u16string GetWindowTitle(HWND hwnd) {
        int length = GetWindowTextLengthW(hwnd);
        if (length <= 0) return {};
        std::u16string buf(static_cast<size_t>(length) + 1, u'\0');
        int actual = GetWindowTextW(hwnd, reinterpret_cast<LPWSTR>(buf.data()), length + 1);
        if (actual <= 0) return {};
        buf.resize(static_cast<size_t>(actual));
        return buf;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_pip_is_pip_title(
    const char16_t* title_ptr, int32_t title_len, uint8_t* out_is_pip) noexcept
{
    if (!out_is_pip) return VN_ERR_INVALID_ARG;
    if (!title_ptr || title_len <= 0) {
        *out_is_pip = 0;
        return VN_OK;
    }
    try {
        *out_is_pip = vnotch::services::PipDetector::IsPipTitle(
            std::u16string_view(title_ptr, static_cast<size_t>(title_len))) ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_pip_is_known_browser_process(
    const char16_t* proc_ptr, int32_t proc_len, uint8_t* out_is_browser) noexcept
{
    if (!out_is_browser) return VN_ERR_INVALID_ARG;
    if (!proc_ptr || proc_len <= 0) {
        *out_is_browser = 0;
        return VN_OK;
    }
    try {
        *out_is_browser = vnotch::services::PipDetector::IsKnownBrowserProcess(
            std::u16string_view(proc_ptr, static_cast<size_t>(proc_len))) ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_pip_is_pip_window(
    intptr_t hwnd,
    const char16_t* target_proc_ptr, int32_t target_proc_len,
    const char16_t* current_track_ptr, int32_t current_track_len,
    uint8_t* out_is_pip) noexcept
{
    if (!out_is_pip) return VN_ERR_INVALID_ARG;
    try {
        std::u16string_view proc_v = (target_proc_ptr && target_proc_len > 0)
            ? std::u16string_view(target_proc_ptr, static_cast<size_t>(target_proc_len))
            : std::u16string_view{};
        std::u16string_view track_v = (current_track_ptr && current_track_len > 0)
            ? std::u16string_view(current_track_ptr, static_cast<size_t>(current_track_len))
            : std::u16string_view{};
        *out_is_pip = vnotch::services::PipDetector::IsPipWindow(
            reinterpret_cast<HWND>(hwnd), proc_v, track_v, nullptr) ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_pip_try_find_pip_window(
    const char16_t* target_proc_ptr, int32_t target_proc_len,
    const char16_t* current_track_ptr, int32_t current_track_len,
    intptr_t* out_hwnd,
    char16_t* title_buf, int32_t title_cap, int32_t* out_title_len,
    uint8_t* out_found) noexcept
{
    if (!out_hwnd || !out_title_len || !out_found) return VN_ERR_INVALID_ARG;
    *out_hwnd = 0;
    *out_title_len = 0;
    *out_found = 0;
    try {
        std::u16string_view proc_v = (target_proc_ptr && target_proc_len > 0)
            ? std::u16string_view(target_proc_ptr, static_cast<size_t>(target_proc_len))
            : std::u16string_view{};
        std::u16string_view track_v = (current_track_ptr && current_track_len > 0)
            ? std::u16string_view(current_track_ptr, static_cast<size_t>(current_track_len))
            : std::u16string_view{};

        HWND found_hwnd = nullptr;
        std::u16string found_title;
        if (!vnotch::services::PipDetector::TryFindPipWindow(proc_v, track_v, &found_hwnd, &found_title)) {
            return VN_OK;
        }

        *out_hwnd = reinterpret_cast<intptr_t>(found_hwnd);
        *out_found = 1;
        int32_t req = static_cast<int32_t>(found_title.size());
        *out_title_len = req;
        if (req == 0) return VN_OK;
        if (!title_buf || title_cap < req) return VN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(title_buf, found_title.data(), static_cast<size_t>(req) * sizeof(char16_t));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
