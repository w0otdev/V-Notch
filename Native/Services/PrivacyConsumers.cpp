#include "vnotch_common.h"
#include "vnotch_handle_registry.h"
#include <unordered_map>
#include <mutex>
#include <appmodel.h>
#include "vnotch_com.h"

VN_EXPORT int32_t vn_privacy_indicator_normalize_app_name_slice(const char16_t*, int32_t, int32_t*, int32_t*) noexcept;
VN_EXPORT int32_t vn_privacy_indicator_is_ignored_mic_process(const char16_t*, int32_t, uint8_t*) noexcept;

namespace {
std::wstring normalize(std::wstring path) {
    auto view = vnotch::trim_u16({reinterpret_cast<const char16_t*>(path.data()), path.size()});
    path.assign(reinterpret_cast<const wchar_t*>(view.data()), view.size());
    while (!path.empty() && path.front() == L'"') path.erase(0, 1);
    while (!path.empty() && path.back() == L'"') path.pop_back();
    std::replace(path.begin(), path.end(), L'#', L'\\');
    const DWORD required = ExpandEnvironmentStringsW(path.c_str(), nullptr, 0);
    if (required == 0 || required > 32768) return {};
    std::wstring expanded(required, L'\0');
    if (ExpandEnvironmentStringsW(path.c_str(), expanded.data(), required) != required) return {};
    expanded.resize(required - 1);
    if (vnotch::starts_with_ignore_case({reinterpret_cast<const char16_t*>(expanded.data()), expanded.size()}, u"\\SystemRoot\\")) {
        wchar_t windows[MAX_PATH]; const auto count = GetWindowsDirectoryW(windows, ARRAYSIZE(windows));
        if (!count || count >= ARRAYSIZE(windows)) return {};
        expanded = std::wstring(windows, count) + L"\\" + expanded.substr(12);
    }
    const DWORD count = GetFullPathNameW(expanded.c_str(), 0, nullptr, nullptr);
    if (!count || count > 32768) return {};
    std::wstring full(count, L'\0');
    const DWORD written = GetFullPathNameW(expanded.c_str(), count, full.data(), nullptr);
    if (!written || written >= count) return {};
    full.resize(written);
    while (!full.empty() && full.back() == L'\\') full.pop_back();
    return full;
}
bool equal(std::wstring_view a, std::wstring_view b) noexcept {
    return a.size() == b.size() && CompareStringOrdinal(a.data(), static_cast<int>(a.size()), b.data(), static_cast<int>(b.size()), TRUE) == CSTR_EQUAL;
}
std::vector<std::wstring> load_service_paths() {
    struct RegKey { HKEY value = nullptr; ~RegKey() { if (value) RegCloseKey(value); } } root;
    std::vector<std::wstring> paths;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SYSTEM\\CurrentControlSet\\Services", 0, KEY_READ, &root.value) != ERROR_SUCCESS) return paths;
    std::vector<wchar_t> command(32768), expanded(32768);
    wchar_t name[256];
    for (DWORD index = 0;; ++index) {
        DWORD length = ARRAYSIZE(name);
        const auto status = RegEnumKeyExW(root.value, index, name, &length, nullptr, nullptr, nullptr, nullptr);
        if (status == ERROR_NO_MORE_ITEMS) break;
        if (status != ERROR_SUCCESS) continue;
        DWORD bytes = static_cast<DWORD>(command.size() * sizeof(wchar_t));
        if (RegGetValueW(root.value, name, L"ImagePath", RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ, nullptr, command.data(), &bytes) != ERROR_SUCCESS) continue;
        const DWORD count = ExpandEnvironmentStringsW(command.data(), expanded.data(), static_cast<DWORD>(expanded.size()));
        if (!count || count > expanded.size()) continue;
        auto view = vnotch::trim_u16({reinterpret_cast<const char16_t*>(expanded.data()), count - 1});
        if (view.empty()) continue;
        if (view.front() == u'"') {
            const auto quote = view.find(u'"', 1);
            if (quote == view.npos || quote <= 1) continue;
            view = view.substr(1, quote - 1);
        } else {
            const int end = FindStringOrdinal(FIND_FROMSTART, reinterpret_cast<const wchar_t*>(view.data()), static_cast<int>(view.size()), L".exe", 4, TRUE);
            if (end < 0) continue;
            view = view.substr(0, end + 4);
        }
        auto path = normalize({reinterpret_cast<const wchar_t*>(view.data()), view.size()});
        if (!path.empty()) paths.push_back(std::move(path));
    }
    return paths;
}
bool ignored_microphone(const std::wstring& raw) {
    const bool desktop = raw.find(L'#') != raw.npos;
    const auto path = desktop ? normalize(raw) : std::wstring{};
    std::u16string_view name;
    if (desktop) {
        const auto slash = path.find_last_of(L"\\/");
        const auto filename = std::wstring_view(path).substr(slash == path.npos ? 0 : slash + 1);
        name = {reinterpret_cast<const char16_t*>(filename.data()), filename.size()};
        if (const auto dot = name.rfind(u'.'); dot != name.npos) name = name.substr(0, dot);
    } else {
        int32_t start = 0, length = 0;
        vn_privacy_indicator_normalize_app_name_slice(reinterpret_cast<const char16_t*>(raw.data()), static_cast<int32_t>(raw.size()), &start, &length);
        name = {reinterpret_cast<const char16_t*>(raw.data()) + start, static_cast<size_t>(length)};
    }
    uint8_t ignored = 0;
    vn_privacy_indicator_is_ignored_mic_process(name.data(), static_cast<int32_t>(name.size()), &ignored);
    if (ignored) return true;
    if (!desktop) return false;
    static const auto services = load_service_paths();
    return std::any_of(services.begin(), services.end(), [&](const auto& service) { return equal(service, path); });
}
struct Identity { std::wstring path, family; bool queried = false; };
struct Package { uint64_t created; ULONGLONG expires; std::wstring family; };
std::mutex package_lock;
std::unordered_map<DWORD, Package> packages;
void cache_package_family(DWORD pid, uint64_t created, std::wstring_view family, ULONGLONG expires, ULONGLONG now) {
    if (!packages.contains(pid) && packages.size() >= 2048) {
        std::erase_if(packages, [now](const auto& item) { return now >= item.second.expires; });
        if (packages.size() >= 2048) {
            packages.erase(std::min_element(packages.begin(), packages.end(),
                [](const auto& a, const auto& b) { return a.second.expires < b.second.expires; }));
        }
    }
    packages[pid] = {created, expires, std::wstring(family)};
}
struct Consumers {
    std::mutex lock;
    std::vector<VnPrivacyProcessEntry> entries;
    std::unordered_map<DWORD, Identity> identities;
    std::unordered_map<std::wstring, bool> running;
    Identity& identity(DWORD pid) {
        auto& result = identities[pid];
        if (result.queried) return result;
        result.queried = true;
        vnotch::UniqueHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
        FILETIME created{}, exited{}, kernel{}, user{}; DWORD exit_code = 0;
        if (!process.valid() || !GetProcessTimes(process.get(), &created, &exited, &kernel, &user) ||
            !GetExitCodeProcess(process.get(), &exit_code) || exit_code != STILL_ACTIVE) return result;
        wchar_t path[32768]; DWORD length = ARRAYSIZE(path);
        if (QueryFullProcessImageNameW(process.get(), 0, path, &length)) result.path = normalize(std::wstring(path, length));
        const auto started = (uint64_t{created.dwHighDateTime} << 32) | created.dwLowDateTime;
        const auto now = GetTickCount64();
        std::lock_guard guard(package_lock);
        auto found = packages.find(pid);
        if (found != packages.end() && found->second.created == started && now < found->second.expires) result.family = found->second.family;
        else {
            wchar_t family[512]; UINT32 count = ARRAYSIZE(family);
            const auto status = GetPackageFamilyName(process.get(), &count, family);
            if (status == ERROR_SUCCESS && count > 1) result.family.assign(family, count - 1);
            cache_package_family(pid, started, result.family, now + (result.family.empty() ? 15000 : 60000), now);
        }
        return result;
    }
    bool matches(std::wstring_view raw, DWORD pid = 0) {
        if (raw.empty() || raw.find(L'\0') != raw.npos) return false;
        const bool desktop = raw.find(L'#') != raw.npos;
        const auto expected = desktop ? normalize(std::wstring(raw)) : std::wstring(raw);
        if (expected.empty()) return false;
        const auto slash = expected.find_last_of(L"\\/");
        const auto name = std::wstring_view(expected).substr(slash == expected.npos ? 0 : slash + 1);
        for (const auto& entry : entries) {
            if (pid && pid != entry.process_id) continue;
            if (desktop) {
                const auto end = std::find(std::begin(entry.executable_file), std::end(entry.executable_file), u'\0');
                if (!equal({reinterpret_cast<const wchar_t*>(entry.executable_file), static_cast<size_t>(end - entry.executable_file)}, name)) continue;
            }
            const auto& info = identity(entry.process_id);
            if (equal(desktop ? info.path : info.family, expected)) return true;
        }
        return false;
    }
    bool is_running(const std::wstring& raw) {
        const auto found = running.find(raw);
        if (found != running.end()) return found->second;
        return running.emplace(raw, matches(raw)).first->second;
    }
};
struct ConsentCache {
    std::mutex lock;
    std::unordered_map<std::u16string, std::vector<VnConsentStoreEntry>> capabilities;
    ULONGLONG next_scan = 0;
};
vnotch::HandleRegistry<Consumers> consumers;
vnotch::HandleRegistry<ConsentCache> consent_caches;
}

VN_EXPORT int32_t vn_privacy_consumers_create(const VnPrivacyProcessEntry* entries, int32_t count, int64_t* handle) noexcept {
    if (!handle || count < 0 || (count && !entries)) return VN_ERR_INVALID_ARG;
    *handle = 0;
    try { auto item = std::make_unique<Consumers>(); if (count) item->entries.assign(entries, entries + count); *handle = consumers.Add(std::move(item)); return VN_OK; }
    catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t vn_privacy_consumers_close(int64_t handle) noexcept { return consumers.Remove(handle) ? VN_OK : VN_ERR_INVALID_ARG; }
VN_EXPORT int32_t vn_privacy_consumers_match(int64_t handle, const char16_t* raw, int32_t length, uint32_t pid, uint8_t* match) noexcept {
    if (!raw || length < 0 || !match) return VN_ERR_INVALID_ARG;
    *match = 0;
    try {
        auto item = consumers.Acquire(handle); if (!item) return VN_ERR_INVALID_ARG;
        std::lock_guard guard(item->lock);
        const std::wstring name(reinterpret_cast<const wchar_t*>(raw), length);
        *match = (pid ? item->matches(name, pid) : item->is_running(name)) ? 1 : 0;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t vn_privacy_consent_cache_create(int64_t* handle) noexcept {
    if (!handle) return VN_ERR_INVALID_ARG;
    *handle = 0;
    try { *handle = consent_caches.Add(std::make_unique<ConsentCache>()); return VN_OK; } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t vn_privacy_consent_cache_close(int64_t handle) noexcept { return consent_caches.Remove(handle) ? VN_OK : VN_ERR_INVALID_ARG; }
VN_EXPORT int32_t vn_privacy_consent_cache_refresh(int64_t handle, uint8_t changed) noexcept {
    auto item = consent_caches.Acquire(handle); if (!item) return VN_ERR_INVALID_ARG;
    std::lock_guard guard(item->lock);
    const auto now = GetTickCount64();
    if (changed || now >= item->next_scan) { item->capabilities.clear(); item->next_scan = now + 30000; }
    return VN_OK;
}
VN_EXPORT int32_t vn_privacy_consumers_scan(int64_t handle, int64_t cache_handle, const char16_t* capability, int32_t length,
    VnConsentStoreEntry* output, int32_t capacity, int32_t* count) noexcept {
    if (!capability || length <= 0 || capacity < 0 || (capacity && !output) || !count) return VN_ERR_INVALID_ARG;
    *count = 0;
    try {
        auto item = consumers.Acquire(handle); auto cache = consent_caches.Acquire(cache_handle);
        if (!item || !cache) return VN_ERR_INVALID_ARG;
        std::scoped_lock guard(item->lock, cache->lock);
        auto [found, inserted] = cache->capabilities.try_emplace(std::u16string(capability, length));
        auto& entries = found->second;
        if (inserted || !entries.empty()) {
            entries.resize(32); int32_t total = 0;
            int status = vn_privacy_indicator_scan_consent_store(capability, length, entries.data(), static_cast<int32_t>(entries.size()), &total);
            for (int retry = 0; status == VN_ERR_BUFFER_TOO_SMALL && retry < 3 && total <= 65536; ++retry) {
                entries.resize(total + 32);
                status = vn_privacy_indicator_scan_consent_store(capability, length, entries.data(), static_cast<int32_t>(entries.size()), &total);
            }
            if (status != VN_OK) { entries.clear(); cache->capabilities.erase(found); return status; }
            entries.resize(total);
        }
        std::vector<VnConsentStoreEntry> active;
        for (const auto& entry : entries) {
            if (entry.raw_len <= 0 || entry.raw_len > 259) continue;
            const std::wstring name(reinterpret_cast<const wchar_t*>(entry.raw_name), entry.raw_len);
            if (!item->is_running(name)) continue;
            if (found->first == u"microphone" && ignored_microphone(name)) continue;
            auto duplicate = std::find_if(active.begin(), active.end(), [&](const auto& previous) {
                return equal(name, {reinterpret_cast<const wchar_t*>(previous.raw_name), static_cast<size_t>(previous.raw_len)});
            });
            if (duplicate == active.end()) active.push_back(entry);
            else if (entry.last_start_file_time > duplicate->last_start_file_time) *duplicate = entry;
        }
        *count = static_cast<int32_t>(active.size());
        if (*count > capacity) return VN_ERR_BUFFER_TOO_SMALL;
        std::copy(active.begin(), active.end(), output);
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t __stdcall vn_privacy_consumers_cache_package(uint32_t pid, const char16_t* family, int32_t length, int64_t expires, int64_t now) noexcept {
    if (length < 0 || (length && !family)) return VN_ERR_INVALID_ARG;
    try {
        std::lock_guard guard(package_lock);
        cache_package_family(pid, 0, {reinterpret_cast<const wchar_t*>(family), static_cast<size_t>(length)},
            expires > 0 ? static_cast<ULONGLONG>(expires) : 0, now > 0 ? static_cast<ULONGLONG>(now) : 0);
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t __stdcall vn_privacy_consumers_query_package_cache(uint32_t pid, int32_t* count, uint8_t* contains) noexcept {
    if (!count || !contains) return VN_ERR_INVALID_ARG;
    std::lock_guard guard(package_lock);
    *count = static_cast<int32_t>(packages.size());
    *contains = packages.contains(pid) ? 1 : 0;
    return VN_OK;
}
VN_EXPORT int32_t __stdcall vn_privacy_consumers_clear_package_cache() noexcept {
    std::lock_guard guard(package_lock);
    packages.clear();
    return VN_OK;
}
