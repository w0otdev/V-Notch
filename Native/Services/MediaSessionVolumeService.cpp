#include "vnotch_common.h"
#include "vnotch_com.h"
#include <tlhelp32.h>
#include <shobjidl.h>
#include <shellapi.h>
#include <propkey.h>

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "propsys.lib")

namespace vnotch::services
{

class MediaSessionVolumeService final
{
    static constexpr bool IsExeNameChar(char16_t ch) noexcept
    {
        return vnotch::is_ascii_alpha(ch) || vnotch::is_ascii_digit(ch) || ch == u'_' || ch == u'-';
    }

    static bool ContainsSource(std::u16string_view left, std::u16string_view right) noexcept
    {
        left = vnotch::trim_u16(left);
        right = vnotch::trim_u16(right);
        if (left.empty() || right.empty())
        {
            return false;
        }
        for (size_t pos = 0; pos + right.size() <= left.size(); ++pos) {
            if (pos > 0 && IsExeNameChar(left[pos - 1])) continue;
            size_t after = pos + right.size();
            if (after < left.size() && (IsExeNameChar(left[after]) || left[after] == u'.')) continue;
            if (vnotch::equals_icase_ascii(left.substr(pos, right.size()), right)) return true;
        }
        return false;
    }

    static void AppendCandidate(std::u16string& out_list, std::u16string_view name)
    {
        if (name.empty()) return;
        if (!out_list.empty())
        {
            out_list.push_back(u';');
        }
        out_list.append(name.data(), name.size());
    }

public:
    static bool MatchesExecutable(std::u16string_view metadata, std::u16string_view candidates) noexcept
    {
        // Core Audio identifiers contain an executable path, while SMTC may expose an AUMID.
        for (size_t start = 0; start < candidates.size();) {
            size_t end = candidates.find(u';', start);
            if (end == std::u16string_view::npos) end = candidates.size();
            auto name = candidates.substr(start, end - start);
            for (size_t pos = 0; pos + name.size() + 4 <= metadata.size(); ++pos) {
                if (pos > 0 && IsExeNameChar(metadata[pos - 1])) continue;
                if (!vnotch::equals_icase_ascii(metadata.substr(pos, name.size()), name)) continue;
                if (!vnotch::equals_icase_ascii(metadata.substr(pos + name.size(), 4), u".exe")) continue;
                size_t after = pos + name.size() + 4;
                if (after == metadata.size() || !IsExeNameChar(metadata[after])) return true;
            }
            start = end + 1;
        }
        return false;
    }

    static bool SessionMatchesSourceAppId(
        std::u16string_view display_name,
        std::u16string_view session_id,
        std::u16string_view instance_id,
        std::u16string_view source_app_id)
    {
        if (vnotch::trim_u16(source_app_id).empty())
        {
            return false;
        }
        auto candidates = ExtractProcessNameCandidates(source_app_id);
        return ContainsSource(display_name, source_app_id) ||
               ContainsSource(session_id, source_app_id) ||
               ContainsSource(instance_id, source_app_id) ||
               MatchesExecutable(session_id, candidates) || MatchesExecutable(instance_id, candidates);
    }

    static double ComputeBaseSessionScore(
        bool matched_by_process,
        bool matched_by_metadata,
        bool source_matches_last,
        uint32_t process_id,
        uint32_t last_process_id,
        bool session_id_matches_last) noexcept
    {
        if (!matched_by_process && !matched_by_metadata)
        {
            return -1e300;
        }
        double score = 0.0;
        if (matched_by_process) score += 1000.0;
        if (matched_by_metadata) score += 200.0;
        if (source_matches_last)
        {
            if (process_id != 0u && process_id == last_process_id) score += 300.0;
            if (session_id_matches_last) score += 400.0;
        }
        return score;
    }

    static std::u16string ExtractProcessNameCandidates(std::u16string_view source_app_id)
    {
        std::u16string result;
        if (vnotch::trim_u16(source_app_id).empty())
        {
            return result;
        }

        // Scan for ([A-Za-z0-9_\-]+)\.exe (case-insensitive)
        size_t n = source_app_id.size();
        for (size_t i = 0; i + 4 <= n; ++i)
        {
            if (source_app_id[i] == u'.' &&
                vnotch::to_lower_ascii(source_app_id[i + 1]) == u'e' &&
                vnotch::to_lower_ascii(source_app_id[i + 2]) == u'x' &&
                vnotch::to_lower_ascii(source_app_id[i + 3]) == u'e')
            {
                size_t start = i;
                while (start > 0 && IsExeNameChar(source_app_id[start - 1]))
                {
                    --start;
                }
                if (start < i)
                {
                    std::u16string_view candidate = vnotch::trim_u16(source_app_id.substr(start, i - start));
                    if (!candidate.empty())
                    {
                        AppendCandidate(result, candidate);
                    }
                }
            }
        }

        struct Alias { std::u16string_view token; std::u16string_view proc; };
        static constexpr Alias kAliases[] = {
            { u"spotify", u"Spotify" },
            { u"msedge", u"msedge" },
            { u"edge", u"msedge" },
            { u"chrome", u"chrome" },
            { u"chromium", u"chromium" },
            { u"firefox", u"firefox" },
            { u"opera", u"opera" },
            { u"brave", u"brave" },
            { u"vivaldi", u"vivaldi" },
            { u"coccoc", u"browser" },
            { u"arc", u"arc" },
            { u"sidekick", u"sidekick" },
            { u"zen", u"zen" },
            { u"floorp", u"floorp" },
            { u"librewolf", u"librewolf" },
            { u"waterfox", u"waterfox" },
            { u"mullvad", u"mullvadbrowser" },
            { u"thorium", u"thorium" },
            { u"applemusic", u"AppleMusic" },
            { u"apple music", u"AppleMusic" }
        };

        for (const auto& a : kAliases)
        {
            if (vnotch::contains_icase_ascii(source_app_id, a.token))
            {
                AppendCandidate(result, a.proc);
            }
        }

        // Unlisted browsers commonly publish their executable name without a suffix.
        if (result.empty() && std::all_of(source_app_id.begin(), source_app_id.end(), IsExeNameChar))
            AppendCandidate(result, source_app_id);
        return result;
    }
};

struct WindowSource {
    std::wstring app_id;
    std::vector<std::wstring> images;
    bool failed = false;
};

static std::wstring ProcessImage(DWORD pid) {
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return {};
    wchar_t buffer[32768];
    DWORD length = static_cast<DWORD>(std::size(buffer));
    BOOL ok = QueryFullProcessImageNameW(process, 0, buffer, &length);
    CloseHandle(process);
    return ok ? std::wstring(buffer, length) : std::wstring();
}

static BOOL CALLBACK MatchWindowSource(HWND window, LPARAM param) noexcept {
    auto& source = *reinterpret_cast<WindowSource*>(param);
    try {
        vnotch::com::ComPtr<IPropertyStore> store;
        if (FAILED(SHGetPropertyStoreForWindow(window, IID_PPV_ARGS(store.Put())))) return TRUE;
        PROPVARIANT value{};
        HRESULT hr = store.ptr->GetValue(PKEY_AppUserModel_ID, &value);
        bool matches = SUCCEEDED(hr) && value.vt == VT_LPWSTR && value.pwszVal &&
            _wcsicmp(source.app_id.c_str(), value.pwszVal) == 0;
        PropVariantClear(&value);
        if (matches) {
            DWORD pid = 0;
            GetWindowThreadProcessId(window, &pid);
            auto image = ProcessImage(pid);
            if (!image.empty()) source.images.push_back(std::move(image));
        }
        return TRUE;
    } catch (...) { source.failed = true; return FALSE; }
}

static std::vector<uint32_t> ResolveProcessIds(std::u16string_view source) {
    vnotch::com::EnsureMta();
    auto candidates = MediaSessionVolumeService::ExtractProcessNameCandidates(source);
    WindowSource window_source{std::wstring(source.begin(), source.end())};
    // Exact window AUMIDs also cover hashed Firefox IDs, profiles, PWAs and browser forks.
    EnumWindows(MatchWindowSource, reinterpret_cast<LPARAM>(&window_source));
    if (window_source.failed) throw std::bad_alloc();
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return {};
    std::vector<uint32_t> result;
    try {
        PROCESSENTRY32W entry{sizeof(entry)};
        if (Process32FirstW(snapshot, &entry)) do {
            auto exe = std::u16string_view(reinterpret_cast<const char16_t*>(entry.szExeFile));
            bool matches = MediaSessionVolumeService::MatchesExecutable(exe, candidates);
            if (!matches && !window_source.images.empty()) {
                auto image = ProcessImage(entry.th32ProcessID);
                for (const auto& owner_image : window_source.images)
                    if (!image.empty() && _wcsicmp(image.c_str(), owner_image.c_str()) == 0) { matches = true; break; }
            }
            if (matches) result.push_back(entry.th32ProcessID);
        } while (Process32NextW(snapshot, &entry));
        CloseHandle(snapshot);
        return result;
    } catch (...) { CloseHandle(snapshot); throw; }
}

} // namespace vnotch::services

VN_EXPORT int32_t vn_media_session_volume_matches_source(
    const char16_t* display_ptr, int32_t display_len,
    const char16_t* session_id_ptr, int32_t session_id_len,
    const char16_t* instance_id_ptr, int32_t instance_id_len,
    const char16_t* source_app_ptr, int32_t source_app_len,
    uint8_t* out_matches) noexcept
{
    if (out_matches == nullptr) return VN_ERR_INVALID_ARG;
    try {
    *out_matches = vnotch::services::MediaSessionVolumeService::SessionMatchesSourceAppId(
        vnotch::make_u16_view(display_ptr, display_len),
        vnotch::make_u16_view(session_id_ptr, session_id_len),
        vnotch::make_u16_view(instance_id_ptr, instance_id_len),
        vnotch::make_u16_view(source_app_ptr, source_app_len)) ? 1u : 0u;
    return VN_OK;
    } catch (...) { *out_matches = 0; return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_media_session_volume_compute_base_score(
    uint8_t matched_by_process,
    uint8_t matched_by_metadata,
    uint8_t source_matches_last,
    uint32_t process_id,
    uint32_t last_process_id,
    uint8_t session_id_matches_last,
    double* out_score) noexcept
{
    if (out_score == nullptr) return VN_ERR_INVALID_ARG;
    *out_score = vnotch::services::MediaSessionVolumeService::ComputeBaseSessionScore(
        matched_by_process != 0u,
        matched_by_metadata != 0u,
        source_matches_last != 0u,
        process_id,
        last_process_id,
        session_id_matches_last != 0u);
    return VN_OK;
}

VN_EXPORT int32_t vn_media_session_volume_extract_candidates(
    const char16_t* source_app_ptr,
    int32_t source_app_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    try {
    std::u16string res = vnotch::services::MediaSessionVolumeService::ExtractProcessNameCandidates(
        vnotch::make_u16_view(source_app_ptr, source_app_len));
    return vnotch::copy_out_u16(res, out_buf, out_cap, out_len);
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_media_session_volume_compute_score(
    uint8_t process_match, uint8_t metadata_match, uint8_t same_source, uint32_t pid, uint32_t last_pid,
    uint8_t same_session, int32_t state, double* out_score) noexcept {
    if (!out_score) return VN_ERR_INVALID_ARG;
    *out_score = (state == 2 || (!process_match && !metadata_match)) ? -1e300 :
        vnotch::services::MediaSessionVolumeService::ComputeBaseSessionScore(
            process_match != 0, metadata_match != 0, same_source != 0, pid, last_pid, same_session != 0)
            + (state == 1 ? 10000.0 : 0.0);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_media_session_volume_get_process_ids(
    const char16_t* source, int32_t source_length, uint32_t* process_ids, int32_t capacity, int32_t* out_count) noexcept {
    if (!out_count || source_length < 0 || (source_length && !source) || capacity < 0 || (capacity && !process_ids))
        return VN_ERR_INVALID_ARG;
    *out_count = 0;
    try {
        auto id = vnotch::trim_u16(vnotch::make_u16_view(source, source_length));
        if (id.empty()) return VN_OK;
        auto ids = vnotch::services::ResolveProcessIds(id);
        *out_count = static_cast<int32_t>(ids.size());
        if (*out_count > capacity) return VN_ERR_BUFFER_TOO_SMALL;
        std::copy(ids.begin(), ids.end(), process_ids);
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
