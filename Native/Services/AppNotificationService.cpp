#include "../vnotch_common.h"
#include <windows.h>
#include <dwmapi.h>
#include <tlhelp32.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <string>
#include <string_view>

namespace vnotch::services {

namespace {

struct SeenNotificationEntry {
    uint32_t notification_id = 0;
    uint64_t content_hash = 0;
    int64_t timestamp_ms = 0;
    bool occupied = false;
};

constexpr size_t kSeenRingCapacity = 64;
constexpr int64_t kDuplicateContentWindowMs = 8000;
constexpr int64_t kMaxNotificationAgeMs = 30000;

SRWLOCK g_app_notify_lock = SRWLOCK_INIT;
std::array<SeenNotificationEntry, kSeenRingCapacity> g_seen_ring{};
size_t g_seen_write_index = 0;
int64_t g_seeded_watermark_ms = 0;

uint64_t ComputeFnv1aHash(std::u16string_view a, std::u16string_view b, std::u16string_view c) noexcept {
    constexpr uint64_t kOffsetBasis = 14695981039346656037ULL;
    constexpr uint64_t kPrime = 1099511628211ULL;
    uint64_t hash = kOffsetBasis;

    auto mix = [&](std::u16string_view sv) noexcept {
        for (char16_t ch : sv) {
            char16_t lower = vnotch::to_lower_invariant(ch);
            hash ^= static_cast<uint64_t>(lower & 0xFFU);
            hash *= kPrime;
            hash ^= static_cast<uint64_t>((lower >> 8) & 0xFFU);
            hash *= kPrime;
        }
        hash ^= 0xFFULL;
        hash *= kPrime;
    };

    mix(a);
    mix(b);
    mix(c);
    return hash;
}

std::u16string CollapseSingleLine(std::u16string_view input, size_t max_chars) {
    std::u16string out;
    out.reserve(std::min(input.size(), max_chars));
    bool pending_space = false;

    for (char16_t ch : input) {
        if (ch == u'\r' || ch == u'\n' || ch == u'\t' || vnotch::is_unicode_whitespace(ch)) {
            if (!out.empty()) {
                pending_space = true;
            }
            continue;
        }
        if (pending_space) {
            if (out.size() + 1 >= max_chars) break;
            out.push_back(u' ');
            pending_space = false;
        }
        if (out.size() >= max_chars) break;
        out.push_back(ch);
    }
    return out;
}

std::u16string NormalizeBodyText(std::u16string_view input, size_t max_chars) {
    std::u16string out;
    out.reserve(std::min(input.size(), max_chars));
    bool pending_space = false;
    bool pending_newline = false;
    int newline_count = 0;

    for (size_t i = 0; i < input.size(); ++i) {
        char16_t ch = input[i];
        if (ch == u'\r') {
            if (i + 1 < input.size() && input[i + 1] == u'\n') {
                ++i;
            }
            if (!out.empty()) {
                pending_newline = true;
                pending_space = false;
            }
            continue;
        }
        if (ch == u'\n') {
            if (!out.empty()) {
                pending_newline = true;
                pending_space = false;
            }
            continue;
        }
        if (ch == u'\t' || vnotch::is_unicode_whitespace(ch)) {
            if (!out.empty() && !pending_newline) {
                pending_space = true;
            }
            continue;
        }

        if (pending_newline) {
            if (out.size() + 1 >= max_chars) break;
            if (newline_count < 1) {
                out.push_back(u'\n');
                ++newline_count;
            } else {
                out.push_back(u' ');
            }
            pending_newline = false;
            pending_space = false;
        } else if (pending_space) {
            if (out.size() + 1 >= max_chars) break;
            out.push_back(u' ');
            pending_space = false;
        }

        if (out.size() >= max_chars) break;
        out.push_back(ch);
    }
    return out;
}

std::u16string ExtractFriendlyAppNameFromAumid(std::u16string_view app_id) {
    std::u16string_view trimmed = vnotch::trim_view(app_id);
    if (trimmed.empty()) return {};

    size_t bang_pos = trimmed.find(u'!');
    if (bang_pos != std::u16string_view::npos && bang_pos > 0) {
        trimmed = trimmed.substr(0, bang_pos);
    }

    size_t underscore_pos = trimmed.rfind(u'_');
    if (underscore_pos != std::u16string_view::npos && underscore_pos > 0 &&
        (trimmed.size() - underscore_pos) >= 10) {
        trimmed = trimmed.substr(0, underscore_pos);
    }

    size_t slash_pos = trimmed.find_last_of(u"\\/");
    if (slash_pos != std::u16string_view::npos && slash_pos + 1 < trimmed.size()) {
        trimmed = trimmed.substr(slash_pos + 1);
    }

    if (vnotch::ends_with_ignore_case(trimmed, u".exe") && trimmed.size() > 4) {
        trimmed = trimmed.substr(0, trimmed.size() - 4);
    }

    size_t dot_pos = trimmed.rfind(u'.');
    if (dot_pos != std::u16string_view::npos && dot_pos + 1 < trimmed.size()) {
        trimmed = trimmed.substr(dot_pos + 1);
    }

    return CollapseSingleLine(trimmed, 48);
}

bool IsSelfNotification(std::u16string_view app_id, std::u16string_view app_name) noexcept {
    if (vnotch::contains_ignore_case(app_id, u"v-notch") ||
        vnotch::contains_ignore_case(app_id, u"vnotch") ||
        vnotch::equals_ignore_case(app_name, u"v-notch") ||
        vnotch::equals_ignore_case(app_name, u"vnotch")) {
        return true;
    }
    return false;
}

int32_t CopyToCallerBuffer(std::u16string_view src, wchar_t* dst, int32_t cap) noexcept {
    if (!dst || cap <= 0) return 0;
    int32_t to_copy = static_cast<int32_t>(std::min(src.size(), static_cast<size_t>(cap)));
    for (int32_t i = 0; i < to_copy; ++i) {
        dst[i] = static_cast<wchar_t>(src[static_cast<size_t>(i)]);
    }
    return to_copy;
}

struct WindowMatchContext {
    std::u16string app_id_lower;
    std::u16string app_token_lower;
    std::u16string app_name_lower;
    DWORD self_pid = 0;
    HWND best_hwnd = nullptr;
    int32_t best_score = 0;
    std::u16string best_proc_path;
};

bool IsGenericHostProcess(std::u16string_view stem_or_path_lower) noexcept {
    if (vnotch::ends_with_ignore_case(stem_or_path_lower, u".exe")) {
        stem_or_path_lower.remove_suffix(4);
    }
    constexpr std::u16string_view kGenericHosts[] = {
        u"applicationframehost",
        u"explorer",
        u"shellexperiencehost",
        u"svchost",
        u"rundll32",
        u"wwahost",
        u"sihost",
    };
    for (std::u16string_view host : kGenericHosts) {
        if (vnotch::ends_with_ignore_case(stem_or_path_lower, host)) {
            return true;
        }
    }
    return false;
}

bool IsExistingRegularLocalFile(const std::u16string& path) noexcept {
    if (path.size() < 3 || path.size() >= MAX_PATH) return false;
    char16_t c0 = path[0];
    bool is_drive = (c0 >= u'A' && c0 <= u'Z') || (c0 >= u'a' && c0 <= u'z');
    if (!is_drive || path[1] != u':' || (path[2] != u'\\' && path[2] != u'/')) {
        return false;
    }
    DWORD attrs = ::GetFileAttributesW(reinterpret_cast<const wchar_t*>(path.c_str()));
    return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

std::u16string NormalizeIconCandidatePath(std::u16string_view raw) {
    std::u16string_view trimmed = vnotch::trim_view(raw);
    if (trimmed.size() >= 2 && trimmed.front() == u'"' && trimmed.back() == u'"') {
        trimmed = vnotch::trim_view(trimmed.substr(1, trimmed.size() - 2));
    }
    if (trimmed.empty() || vnotch::starts_with_ignore_case(trimmed, u"ms-appx") ||
        vnotch::starts_with_ignore_case(trimmed, u"http://") ||
        vnotch::starts_with_ignore_case(trimmed, u"https://")) {
        return {};
    }

    std::u16string working(trimmed);
    if (vnotch::starts_with_ignore_case(working, u"file:///")) {
        working.erase(0, 8);
    } else if (vnotch::starts_with_ignore_case(working, u"file://")) {
        working.erase(0, 7);
    }

    std::u16string unescaped;
    unescaped.reserve(working.size());
    for (size_t i = 0; i < working.size(); ++i) {
        if (working[i] == u'%' && i + 2 < working.size() && working[i + 1] == u'2' && working[i + 2] == u'0') {
            unescaped.push_back(u' ');
            i += 2;
        } else if (working[i] == u'/') {
            unescaped.push_back(u'\\');
        } else {
            unescaped.push_back(working[i]);
        }
    }

    size_t comma_pos = unescaped.rfind(u',');
    if (comma_pos != std::u16string::npos && comma_pos > 3) {
        bool numeric_suffix = true;
        for (size_t i = comma_pos + 1; i < unescaped.size(); ++i) {
            char16_t ch = unescaped[i];
            if (ch != u'-' && ch != u' ' && (ch < u'0' || ch > u'9')) {
                numeric_suffix = false;
                break;
            }
        }
        if (numeric_suffix) {
            unescaped.resize(comma_pos);
        }
    }

    if (unescaped.find(u'%') != std::u16string::npos) {
        wchar_t expanded_buf[MAX_PATH]{};
        DWORD exp_len = ::ExpandEnvironmentStringsW(
            reinterpret_cast<const wchar_t*>(unescaped.c_str()), expanded_buf, MAX_PATH);
        if (exp_len > 0 && exp_len < MAX_PATH) {
            unescaped.assign(reinterpret_cast<const char16_t*>(expanded_buf), static_cast<size_t>(exp_len - 1));
        }
    }

    if (!IsExistingRegularLocalFile(unescaped) || IsGenericHostProcess(unescaped)) {
        return {};
    }
    return unescaped;
}

std::u16string TryResolveRegistryAumidIcon(std::u16string_view app_id) {
    std::u16string_view trimmed = vnotch::trim_view(app_id);
    if (trimmed.empty() || trimmed.size() > 180) return {};

    std::u16string subkey = u"Software\\Classes\\AppUserModelId\\";
    subkey.append(trimmed);

    const HKEY roots[2] = { HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE };
    for (HKEY root : roots) {
        wchar_t val_buf[MAX_PATH]{};
        DWORD byte_size = sizeof(val_buf);
        LSTATUS st = ::RegGetValueW(
            root,
            reinterpret_cast<const wchar_t*>(subkey.c_str()),
            L"IconUri",
            RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ,
            nullptr,
            val_buf,
            &byte_size);
        if (st == ERROR_SUCCESS && val_buf[0] != L'\0') {
            std::u16string candidate = NormalizeIconCandidatePath(
                std::u16string_view(reinterpret_cast<const char16_t*>(val_buf)));
            if (!candidate.empty()) {
                return candidate;
            }
        }
    }
    return {};
}

std::u16string TryResolveProcessSnapshotIcon(const WindowMatchContext& ctx) {
    HANDLE snap = ::CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return {};

    PROCESSENTRY32W pe{};
    pe.dwSize = sizeof(pe);
    int32_t best_score = 0;
    std::u16string best_path;

    if (::Process32FirstW(snap, &pe)) {
        do {
            if (pe.th32ProcessID == 0 || pe.th32ProcessID == ctx.self_pid) continue;
            std::u16string_view exe_sv(reinterpret_cast<const char16_t*>(pe.szExeFile));
            if (exe_sv.empty() || IsGenericHostProcess(exe_sv)) continue;

            std::u16string_view stem_sv = exe_sv;
            if (vnotch::ends_with_ignore_case(stem_sv, u".exe") && stem_sv.size() > 4) {
                stem_sv = stem_sv.substr(0, stem_sv.size() - 4);
            }
            std::u16string stem_lower = vnotch::to_lower_string(stem_sv);
            if (stem_lower.empty()) continue;

            int32_t score = 0;
            if (!ctx.app_name_lower.empty() && stem_lower == ctx.app_name_lower) {
                score = 300;
            } else if (!ctx.app_token_lower.empty() && stem_lower == ctx.app_token_lower) {
                score = 280;
            } else if (!ctx.app_id_lower.empty() && stem_lower.size() >= 3 &&
                       vnotch::contains_ordinal(ctx.app_id_lower, stem_lower)) {
                score = 220;
            } else if (!ctx.app_name_lower.empty() && stem_lower.size() >= 3 &&
                       ctx.app_name_lower.size() >= 3 &&
                       (vnotch::contains_ordinal(stem_lower, ctx.app_name_lower) ||
                        vnotch::contains_ordinal(ctx.app_name_lower, stem_lower))) {
                score = 180;
            }

            if (score <= best_score) continue;

            HANDLE hproc = ::OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pe.th32ProcessID);
            if (!hproc) continue;
            wchar_t path_buf[MAX_PATH]{};
            DWORD path_len = MAX_PATH;
            bool ok = ::QueryFullProcessImageNameW(hproc, 0, path_buf, &path_len) && path_len > 0;
            ::CloseHandle(hproc);
            if (!ok) continue;

            std::u16string full_path(reinterpret_cast<const char16_t*>(path_buf), static_cast<size_t>(path_len));
            if (vnotch::contains_ignore_case(full_path, u"\\windowsapps\\")) continue;
            if (!IsExistingRegularLocalFile(full_path)) continue;

            best_score = score;
            best_path = std::move(full_path);
        } while (::Process32NextW(snap, &pe));
    }

    ::CloseHandle(snap);
    return best_path;
}

std::u16string TryResolveStartMenuShortcutIcon(std::u16string_view app_name, std::u16string_view app_token) {
    const wchar_t* env_roots[2] = {
        L"%APPDATA%\\Microsoft\\Windows\\Start Menu\\Programs",
        L"%ProgramData%\\Microsoft\\Windows\\Start Menu\\Programs"
    };

    std::u16string_view names[2] = { vnotch::trim_view(app_name), vnotch::trim_view(app_token) };
    for (const wchar_t* env_root : env_roots) {
        wchar_t root_buf[MAX_PATH]{};
        DWORD exp_len = ::ExpandEnvironmentStringsW(env_root, root_buf, MAX_PATH);
        if (exp_len == 0 || exp_len >= MAX_PATH) continue;
        std::u16string base_dir(reinterpret_cast<const char16_t*>(root_buf), static_cast<size_t>(exp_len - 1));

        for (std::u16string_view name : names) {
            if (name.empty() || name.size() > 64) continue;
            bool has_invalid = false;
            for (char16_t ch : name) {
                if (ch == u'\\' || ch == u'/' || ch == u':' || ch == u'*' || ch == u'?' || ch == u'"' || ch == u'<' || ch == u'>' || ch == u'|') {
                    has_invalid = true;
                    break;
                }
            }
            if (has_invalid) continue;

            std::u16string direct = base_dir + u"\\" + std::u16string(name) + u".lnk";
            if (IsExistingRegularLocalFile(direct)) return direct;

            std::u16string nested = base_dir + u"\\" + std::u16string(name) + u"\\" + std::u16string(name) + u".lnk";
            if (IsExistingRegularLocalFile(nested)) return nested;
        }
    }
    return {};
}

BOOL CALLBACK EnumWindowsForAppActivate(HWND hwnd, LPARAM lparam) noexcept {
    auto* ctx = reinterpret_cast<WindowMatchContext*>(lparam);
    if (!ctx || !::IsWindowVisible(hwnd)) return TRUE;

    LONG_PTR ex_style = ::GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
    if ((ex_style & WS_EX_TOOLWINDOW) != 0 && (ex_style & WS_EX_APPWINDOW) == 0) {
        return TRUE;
    }

    DWORD cloaked = 0;
    if (SUCCEEDED(::DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked != 0) {
        return TRUE;
    }

    DWORD pid = 0;
    ::GetWindowThreadProcessId(hwnd, &pid);
    if (pid == 0 || pid == ctx->self_pid) return TRUE;

    wchar_t title_buf[256]{};
    int title_len = ::GetWindowTextW(hwnd, title_buf, 256);
    if (title_len <= 0) return TRUE;

    std::u16string title_lower = vnotch::to_lower_string(
        std::u16string_view(reinterpret_cast<const char16_t*>(title_buf), static_cast<size_t>(title_len)));

    std::u16string proc_stem_lower;
    std::u16string proc_full_path;
    HANDLE hproc = ::OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (hproc) {
        wchar_t path_buf[MAX_PATH]{};
        DWORD path_len = MAX_PATH;
        if (::QueryFullProcessImageNameW(hproc, 0, path_buf, &path_len) && path_len > 0) {
            proc_full_path.assign(reinterpret_cast<const char16_t*>(path_buf), static_cast<size_t>(path_len));
            std::u16string_view full_sv(proc_full_path);
            size_t slash = full_sv.find_last_of(u"\\/");
            std::u16string_view base = (slash != std::u16string_view::npos) ? full_sv.substr(slash + 1) : full_sv;
            if (vnotch::ends_with_ignore_case(base, u".exe") && base.size() > 4) {
                base = base.substr(0, base.size() - 4);
            }
            proc_stem_lower = vnotch::to_lower_string(base);
        }
        ::CloseHandle(hproc);
    }

    int32_t score = 0;
    if (!proc_stem_lower.empty()) {
        if (!ctx->app_token_lower.empty() && proc_stem_lower == ctx->app_token_lower) {
            score += 200;
        } else if (!ctx->app_name_lower.empty() && proc_stem_lower == ctx->app_name_lower) {
            score += 190;
        } else if (!ctx->app_id_lower.empty() && proc_stem_lower.size() >= 3 &&
                   vnotch::contains_ordinal(ctx->app_id_lower, proc_stem_lower)) {
            score += 140;
        } else if (!ctx->app_name_lower.empty() && proc_stem_lower.size() >= 3 &&
                   (vnotch::contains_ordinal(proc_stem_lower, ctx->app_name_lower) ||
                    vnotch::contains_ordinal(ctx->app_name_lower, proc_stem_lower))) {
            score += 120;
        }
    }

    if (!ctx->app_name_lower.empty() && ctx->app_name_lower.size() >= 2 &&
        vnotch::contains_ordinal(title_lower, ctx->app_name_lower)) {
        score += 90;
    }
    if (!ctx->app_token_lower.empty() && ctx->app_token_lower.size() >= 3 &&
        vnotch::contains_ordinal(title_lower, ctx->app_token_lower)) {
        score += 75;
    }

    if (score > ctx->best_score) {
        ctx->best_score = score;
        ctx->best_hwnd = hwnd;
        if (!proc_full_path.empty() &&
            !IsGenericHostProcess(proc_stem_lower) &&
            !vnotch::contains_ignore_case(proc_full_path, u"\\windowsapps\\")) {
            ctx->best_proc_path = std::move(proc_full_path);
        }
    }
    return TRUE;
}

} // namespace

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t __stdcall vn_appnotify_reset_state() noexcept {
    ::AcquireSRWLockExclusive(&vnotch::services::g_app_notify_lock);
    for (auto& slot : vnotch::services::g_seen_ring) {
        slot = {};
    }
    vnotch::services::g_seen_write_index = 0;
    vnotch::services::g_seeded_watermark_ms = 0;
    ::ReleaseSRWLockExclusive(&vnotch::services::g_app_notify_lock);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_appnotify_seed_existing_id(
    uint32_t notification_id,
    int64_t creation_unix_ms) noexcept
{
    ::AcquireSRWLockExclusive(&vnotch::services::g_app_notify_lock);
    if (creation_unix_ms > vnotch::services::g_seeded_watermark_ms) {
        vnotch::services::g_seeded_watermark_ms = creation_unix_ms;
    }
    if (notification_id != 0) {
        bool exists = false;
        for (const auto& entry : vnotch::services::g_seen_ring) {
            if (entry.occupied && entry.notification_id == notification_id) {
                exists = true;
                break;
            }
        }
        if (!exists) {
            auto& slot = vnotch::services::g_seen_ring[vnotch::services::g_seen_write_index];
            slot.notification_id = notification_id;
            slot.content_hash = 0;
            slot.timestamp_ms = creation_unix_ms;
            slot.occupied = true;
            vnotch::services::g_seen_write_index =
                (vnotch::services::g_seen_write_index + 1) % vnotch::services::kSeenRingCapacity;
        }
    }
    ::ReleaseSRWLockExclusive(&vnotch::services::g_app_notify_lock);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_appnotify_evaluate_and_format(
    uint32_t notification_id,
    int64_t creation_unix_ms,
    int64_t now_unix_ms,
    const wchar_t* app_id_ptr, int32_t app_id_len,
    const wchar_t* app_name_ptr, int32_t app_name_len,
    const wchar_t* title_ptr, int32_t title_len,
    const wchar_t* body_ptr, int32_t body_len,
    wchar_t* out_app_buf, int32_t app_cap,
    wchar_t* out_title_buf, int32_t title_cap,
    wchar_t* out_body_buf, int32_t body_cap,
    VnAppNotificationFilterResult* out_result) noexcept
{
    if (!out_result) return VN_ERR_INVALID_ARG;
    *out_result = {};

    try {
        std::u16string_view app_id_sv = vnotch::make_u16_view(reinterpret_cast<const char16_t*>(app_id_ptr), app_id_len);
        std::u16string_view app_name_sv = vnotch::make_u16_view(reinterpret_cast<const char16_t*>(app_name_ptr), app_name_len);
        std::u16string_view title_sv = vnotch::make_u16_view(reinterpret_cast<const char16_t*>(title_ptr), title_len);
        std::u16string_view body_sv = vnotch::make_u16_view(reinterpret_cast<const char16_t*>(body_ptr), body_len);

        if (vnotch::services::IsSelfNotification(app_id_sv, app_name_sv)) {
            return VN_OK;
        }

        if (creation_unix_ms > 0 && now_unix_ms > creation_unix_ms &&
            (now_unix_ms - creation_unix_ms) > vnotch::services::kMaxNotificationAgeMs) {
            return VN_OK;
        }

        std::u16string norm_app = vnotch::services::CollapseSingleLine(app_name_sv, 48);
        if (norm_app.empty()) {
            norm_app = vnotch::services::ExtractFriendlyAppNameFromAumid(app_id_sv);
        }

        std::u16string norm_title = vnotch::services::CollapseSingleLine(title_sv, 120);
        std::u16string norm_body = vnotch::services::NormalizeBodyText(body_sv, 220);

        if (!norm_app.empty() && !norm_title.empty()) {
            if (vnotch::equals_ignore_case(norm_title, norm_app)) {
                if (!norm_body.empty()) {
                    norm_title.clear();
                }
            } else if (vnotch::starts_with_ignore_case(norm_title, norm_app) &&
                       norm_title.size() > norm_app.size()) {
                std::u16string_view rem = vnotch::trim_view(
                    std::u16string_view(norm_title).substr(norm_app.size()));
                if (!rem.empty() && (rem.front() == u':' || rem.front() == u'-' || rem.front() == u'|' ||
                                     rem.front() == u'\u2013' || rem.front() == u'\u2014')) {
                    rem = vnotch::trim_view(rem.substr(1));
                    if (!rem.empty()) {
                        norm_title.assign(rem);
                    }
                }
            }
        }

        if (norm_title.empty() && !norm_body.empty()) {
            size_t nl = norm_body.find(u'\n');
            if (nl != std::u16string::npos) {
                norm_title = norm_body.substr(0, nl);
                norm_body = norm_body.substr(nl + 1);
            } else {
                norm_title = std::move(norm_body);
                norm_body.clear();
            }
        }

        if (!norm_body.empty() &&
            (vnotch::equals_ignore_case(norm_body, norm_title) ||
             vnotch::equals_ignore_case(norm_body, norm_app))) {
            norm_body.clear();
        }

        if (norm_title.empty() && norm_body.empty()) {
            return VN_OK;
        }

        uint64_t content_hash = vnotch::services::ComputeFnv1aHash(norm_app, norm_title, norm_body);
        int64_t effective_time = now_unix_ms > 0 ? now_unix_ms : creation_unix_ms;

        bool duplicate = false;
        ::AcquireSRWLockExclusive(&vnotch::services::g_app_notify_lock);
        for (const auto& entry : vnotch::services::g_seen_ring) {
            if (!entry.occupied) continue;
            if (notification_id != 0 && entry.notification_id == notification_id) {
                duplicate = true;
                break;
            }
            if (entry.content_hash == content_hash &&
                effective_time >= entry.timestamp_ms &&
                (effective_time - entry.timestamp_ms) <= vnotch::services::kDuplicateContentWindowMs) {
                duplicate = true;
                break;
            }
        }

        if (!duplicate) {
            auto& slot = vnotch::services::g_seen_ring[vnotch::services::g_seen_write_index];
            slot.notification_id = notification_id;
            slot.content_hash = content_hash;
            slot.timestamp_ms = effective_time;
            slot.occupied = true;
            vnotch::services::g_seen_write_index =
                (vnotch::services::g_seen_write_index + 1) % vnotch::services::kSeenRingCapacity;
        }
        ::ReleaseSRWLockExclusive(&vnotch::services::g_app_notify_lock);

        if (duplicate) {
            return VN_OK;
        }

        out_result->accepted = 1;
        out_result->content_hash = content_hash;
        out_result->normalized_app_len = vnotch::services::CopyToCallerBuffer(norm_app, out_app_buf, app_cap);
        out_result->normalized_title_len = vnotch::services::CopyToCallerBuffer(norm_title, out_title_buf, title_cap);
        out_result->normalized_body_len = vnotch::services::CopyToCallerBuffer(norm_body, out_body_buf, body_cap);
        return VN_OK;
    } catch (...) {
        *out_result = {};
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_appnotify_compute_banner_metrics(
    double collapsed_width,
    double collapsed_height,
    double expanded_width,
    double corner_radius_collapsed,
    double corner_radius_expanded,
    uint8_t is_dynamic_island,
    uint8_t reduce_motion,
    int32_t configured_duration_ms,
    int32_t title_len,
    int32_t body_len,
    uint8_t has_newline_in_body,
    VnAppNotificationBannerMetrics* out_metrics) noexcept
{
    if (!out_metrics) return VN_ERR_INVALID_ARG;

    double safe_collapsed_w = (std::isfinite(collapsed_width) && collapsed_width > 100.0) ? collapsed_width : 230.0;
    double safe_collapsed_h = (std::isfinite(collapsed_height) && collapsed_height > 20.0) ? collapsed_height : 34.0;
    double safe_expanded_w = (std::isfinite(expanded_width) && expanded_width > 320.0) ? expanded_width : 580.0;
    double safe_cr_expanded = (std::isfinite(corner_radius_expanded) && corner_radius_expanded > 8.0) ? corner_radius_expanded : 20.0;
    (void)corner_radius_collapsed;
    (void)title_len;

    bool has_body = body_len > 0;
    bool multiline_body = has_body && (has_newline_in_body != 0 || body_len > 46);

    double max_banner_w = std::max(388.0, safe_expanded_w - 32.0);
    double desired_w = std::max(safe_collapsed_w * 1.76, 404.0);
    double target_w = std::clamp(desired_w, 388.0, std::min(436.0, max_banner_w));

    double island_extra_h = is_dynamic_island ? 4.0 : 0.0;
    double target_h = safe_collapsed_h + 54.0 + island_extra_h;
    if (multiline_body) {
        target_h = safe_collapsed_h + 70.0 + island_extra_h;
    } else if (has_body) {
        target_h = safe_collapsed_h + 58.0 + island_extra_h;
    }
    target_h = std::max(target_h, 90.0);

    double target_cr = is_dynamic_island
        ? std::clamp(safe_cr_expanded + 2.0, 22.0, 26.0)
        : std::clamp(safe_cr_expanded + 2.0, 20.0, 24.0);

    int32_t dismiss_ms = std::clamp(configured_duration_ms > 0 ? configured_duration_ms : 5000, 1500, 15000);

    out_metrics->target_width = target_w;
    out_metrics->target_height = target_h;
    out_metrics->target_corner_radius = target_cr;
    out_metrics->morph_duration_ms = reduce_motion ? 120 : 380;
    out_metrics->content_fade_ms = reduce_motion ? 90 : 240;
    out_metrics->dismiss_duration_ms = dismiss_ms;
    out_metrics->has_body = has_body ? 1 : 0;
    out_metrics->is_multiline_body = multiline_body ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_appnotify_try_activate_app(
    const wchar_t* app_id_ptr, int32_t app_id_len,
    const wchar_t* app_name_ptr, int32_t app_name_len,
    uint8_t* out_activated) noexcept
{
    if (!out_activated) return VN_ERR_INVALID_ARG;
    *out_activated = 0;

    try {
        std::u16string_view app_id_sv = vnotch::make_u16_view(reinterpret_cast<const char16_t*>(app_id_ptr), app_id_len);
        std::u16string_view app_name_sv = vnotch::make_u16_view(reinterpret_cast<const char16_t*>(app_name_ptr), app_name_len);
        if (app_id_sv.empty() && app_name_sv.empty()) {
            return VN_OK;
        }

        vnotch::services::WindowMatchContext ctx{};
        ctx.app_id_lower = vnotch::to_lower_string(vnotch::trim_view(app_id_sv));
        ctx.app_token_lower = vnotch::to_lower_string(
            vnotch::services::ExtractFriendlyAppNameFromAumid(app_id_sv));
        ctx.app_name_lower = vnotch::to_lower_string(vnotch::trim_view(app_name_sv));
        ctx.self_pid = ::GetCurrentProcessId();

        ::EnumWindows(vnotch::services::EnumWindowsForAppActivate, reinterpret_cast<LPARAM>(&ctx));
        if (!ctx.best_hwnd || ctx.best_score <= 0) {
            return VN_OK;
        }

        if (::IsIconic(ctx.best_hwnd)) {
            ::ShowWindow(ctx.best_hwnd, SW_RESTORE);
        } else {
            ::ShowWindow(ctx.best_hwnd, SW_SHOW);
        }

        BOOL focused = ::SetForegroundWindow(ctx.best_hwnd);
        if (!focused) {
            HWND fg = ::GetForegroundWindow();
            DWORD fg_tid = fg ? ::GetWindowThreadProcessId(fg, nullptr) : 0;
            DWORD cur_tid = ::GetCurrentThreadId();
            if (fg_tid != 0 && fg_tid != cur_tid) {
                if (::AttachThreadInput(cur_tid, fg_tid, TRUE)) {
                    ::BringWindowToTop(ctx.best_hwnd);
                    focused = ::SetForegroundWindow(ctx.best_hwnd);
                    ::AttachThreadInput(cur_tid, fg_tid, FALSE);
                }
            }
        }

        *out_activated = focused ? 1 : 0;
        return VN_OK;
    } catch (...) {
        *out_activated = 0;
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_appnotify_resolve_app_icon_source(
    const wchar_t* app_id_ptr, int32_t app_id_len,
    const wchar_t* app_name_ptr, int32_t app_name_len,
    wchar_t* out_path_buf, int32_t path_cap,
    int32_t* out_path_len) noexcept
{
    if (!out_path_len) return VN_ERR_INVALID_ARG;
    *out_path_len = 0;
    if (!out_path_buf || path_cap <= 0) return VN_ERR_INVALID_ARG;

    try {
        std::u16string_view app_id_sv = vnotch::make_u16_view(reinterpret_cast<const char16_t*>(app_id_ptr), app_id_len);
        std::u16string_view app_name_sv = vnotch::make_u16_view(reinterpret_cast<const char16_t*>(app_name_ptr), app_name_len);
        if (app_id_sv.empty() && app_name_sv.empty()) {
            return VN_OK;
        }

        std::u16string resolved = vnotch::services::NormalizeIconCandidatePath(app_id_sv);
        if (resolved.empty() && !app_id_sv.empty()) {
            resolved = vnotch::services::TryResolveRegistryAumidIcon(app_id_sv);
        }

        std::u16string friendly_token = vnotch::services::ExtractFriendlyAppNameFromAumid(app_id_sv);

        if (resolved.empty()) {
            vnotch::services::WindowMatchContext ctx{};
            ctx.app_id_lower = vnotch::to_lower_string(vnotch::trim_view(app_id_sv));
            ctx.app_token_lower = vnotch::to_lower_string(friendly_token);
            ctx.app_name_lower = vnotch::to_lower_string(vnotch::trim_view(app_name_sv));
            ctx.self_pid = ::GetCurrentProcessId();

            ::EnumWindows(vnotch::services::EnumWindowsForAppActivate, reinterpret_cast<LPARAM>(&ctx));
            if (!ctx.best_proc_path.empty() && ctx.best_score >= 120) {
                resolved = std::move(ctx.best_proc_path);
            }

            if (resolved.empty()) {
                resolved = vnotch::services::TryResolveProcessSnapshotIcon(ctx);
            }
        }

        if (resolved.empty()) {
            resolved = vnotch::services::TryResolveStartMenuShortcutIcon(app_name_sv, friendly_token);
        }

        if (!resolved.empty()) {
            *out_path_len = vnotch::services::CopyToCallerBuffer(resolved, out_path_buf, path_cap);
        }
        return VN_OK;
    } catch (...) {
        *out_path_len = 0;
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_appnotify_extract_initials(
    const wchar_t* title_ptr, int32_t title_len,
    const wchar_t* app_name_ptr, int32_t app_name_len,
    wchar_t* out_buf, int32_t buf_cap,
    int32_t* out_len) noexcept
{
    if (!out_len) return VN_ERR_INVALID_ARG;
    *out_len = 0;
    if (!out_buf || buf_cap <= 0) return VN_ERR_INVALID_ARG;

    auto to_upper = [](char16_t ch) noexcept -> char16_t {
        if (ch >= u'a' && ch <= u'z') return static_cast<char16_t>(ch - u'a' + u'A');
        return ch;
    };

    auto is_word_char = [](char16_t ch) noexcept -> bool {
        return (ch >= u'A' && ch <= u'Z') ||
               (ch >= u'a' && ch <= u'z') ||
               (ch >= u'0' && ch <= u'9') ||
               (ch >= 0x00C0 && ch <= 0x1EFF);
    };

    struct InitialsPair {
        char16_t chars[2]{};
        size_t len = 0;
    };

    auto extract_two = [&](std::u16string_view text) noexcept -> InitialsPair {
        std::u16string_view trimmed = vnotch::trim_view(text);
        if (trimmed.empty()) return {};

        if (vnotch::equals_ignore_case(trimmed, u"antigravity")) {
            return InitialsPair{{u'A', u'G'}, 2};
        }

        char16_t first_word_ch = 0;
        char16_t second_word_ch = 0;
        char16_t first_word_second_ch = 0;
        char16_t camel_second_ch = 0;
        bool in_word = false;
        int word_index = 0;

        for (size_t i = 0; i < trimmed.size(); ++i) {
            char16_t ch = trimmed[i];
            if (is_word_char(ch)) {
                if (!in_word) {
                    in_word = true;
                    ++word_index;
                    if (word_index == 1) {
                        first_word_ch = to_upper(ch);
                    } else if (word_index == 2 && second_word_ch == 0) {
                        second_word_ch = to_upper(ch);
                    }
                } else if (word_index == 1) {
                    if (first_word_second_ch == 0) {
                        first_word_second_ch = to_upper(ch);
                    }
                    if (camel_second_ch == 0 && ch >= u'A' && ch <= u'Z') {
                        camel_second_ch = ch;
                    }
                }
            } else if (ch == u' ' || ch == u'\t' || ch == u'-' || ch == u'_' || ch == u'.') {
                in_word = false;
            }
        }

        if (first_word_ch == 0) return {};

        const char16_t second = second_word_ch != 0
            ? second_word_ch
            : (camel_second_ch != 0 ? camel_second_ch : first_word_second_ch);
        if (second != 0) {
            return InitialsPair{{first_word_ch, second}, 2};
        }
        return InitialsPair{{first_word_ch, 0}, 1};
    };

    std::u16string_view title_sv = vnotch::trim_view(
        vnotch::make_u16_view(reinterpret_cast<const char16_t*>(title_ptr), title_len));
    std::u16string_view app_sv = vnotch::trim_view(
        vnotch::make_u16_view(reinterpret_cast<const char16_t*>(app_name_ptr), app_name_len));

    bool title_is_sender_name = false;
    if (!title_sv.empty() && title_sv.size() <= 28 && !vnotch::equals_ignore_case(title_sv, app_sv)) {
        bool has_punct = false;
        int words = 0;
        bool in_w = false;
        for (char16_t ch : title_sv) {
            if (ch == u':' || ch == u'?' || ch == u'!' || ch == u';' || ch == u'/' || ch == u'\\') {
                has_punct = true;
                break;
            }
            if (ch == u' ' || ch == u'\t') {
                in_w = false;
            } else if (!in_w) {
                in_w = true;
                ++words;
            }
        }
        if (!has_punct && words >= 2 && words <= 3) {
            title_is_sender_name = true;
        }
    }

    InitialsPair initials = title_is_sender_name
        ? extract_two(title_sv)
        : extract_two(!app_sv.empty() ? app_sv : title_sv);

    if (initials.len == 0) {
        initials = InitialsPair{{u'V', u'N'}, 2};
    }

    *out_len = vnotch::services::CopyToCallerBuffer(
        std::u16string_view(initials.chars, initials.len), out_buf, buf_cap);
    return VN_OK;
}

} // extern "C"
