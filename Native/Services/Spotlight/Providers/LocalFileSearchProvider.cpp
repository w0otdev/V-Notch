#include "vnotch_common.h"
#include "vnotch_handle_registry.h"
#include <atomic>
#include <deque>
#include <memory>

// Reuse Spotlight's canonical visibility and ranking policies.
extern "C" int32_t vn_spotlight_visibility_should_include(
    const char16_t*, int32_t, const char16_t*, int32_t, uint8_t, uint8_t, uint8_t*) noexcept;
extern "C" int32_t vn_spotlight_ranker_score(
    const char16_t*, int32_t, const char16_t*, int32_t, const char16_t*, int32_t,
    const char16_t*, int32_t, const char16_t*, int32_t, int32_t, const char16_t*, int32_t, double*) noexcept;

namespace vnotch::services::localfiles {
constexpr int32_t kMaximumResults = 200;
constexpr uint32_t kEntriesPerTurn = 64;

struct Result { std::wstring path; int32_t kind; double score; };
struct Search {
    SRWLOCK lock = SRWLOCK_INIT;
    std::atomic<bool> cancelled{false};
    std::vector<Result> results;
};

struct ExclusiveSrwLockGuard {
    SRWLOCK* lock;
    explicit ExclusiveSrwLockGuard(SRWLOCK& l) noexcept : lock(&l) { AcquireSRWLockExclusive(lock); }
    ~ExclusiveSrwLockGuard() noexcept { ReleaseSRWLockExclusive(lock); }
    ExclusiveSrwLockGuard(const ExclusiveSrwLockGuard&) = delete;
    ExclusiveSrwLockGuard& operator=(const ExclusiveSrwLockGuard&) = delete;
};

struct SharedSrwLockGuard {
    SRWLOCK* lock;
    explicit SharedSrwLockGuard(SRWLOCK& l) noexcept : lock(&l) { AcquireSRWLockShared(lock); }
    ~SharedSrwLockGuard() noexcept { ReleaseSRWLockShared(lock); }
    SharedSrwLockGuard(const SharedSrwLockGuard&) = delete;
    SharedSrwLockGuard& operator=(const SharedSrwLockGuard&) = delete;
};

HandleRegistry<Search> searches;

static std::u16string_view View(std::wstring_view value) noexcept {
    return {reinterpret_cast<const char16_t*>(value.data()), value.size()};
}

static bool AbsoluteLocalPath(std::wstring_view path) noexcept {
    return path.size() >= 3 && is_ascii_alpha(static_cast<char16_t>(path[0])) && path[1] == L':' && path[2] == L'\\';
}

static std::wstring FullPath(std::wstring value) {
    for (auto& ch : value) if (ch == L'/') ch = L'\\';
    if (!AbsoluteLocalPath(value)) return {};
    wchar_t buffer[32768];
    DWORD length = GetFullPathNameW(value.c_str(), static_cast<DWORD>(std::size(buffer)), buffer, nullptr);
    if (!length || length >= std::size(buffer)) return {};
    return {buffer, length};
}

static bool SameOrBelow(std::wstring_view path, std::wstring_view root) noexcept {
    while (root.size() > 3 && root.back() == L'\\') root.remove_suffix(1);
    return equals_icase(View(path), View(root)) ||
        (path.size() > root.size() && starts_with_icase(View(path), View(root)) &&
            (root.back() == L'\\' || path[root.size()] == L'\\'));
}

static std::vector<std::wstring> SplitRoots(const char16_t* text, int32_t length) {
    std::vector<std::wstring> roots;
    for (int32_t start = 0; start < length;) {
        int32_t end = start;
        while (end < length && text[end] != 0) ++end;
        if (end > start) {
            auto path = FullPath(std::wstring(reinterpret_cast<const wchar_t*>(text + start), end - start));
            if (!path.empty() && std::none_of(roots.begin(), roots.end(), [&](const auto& root) {
                return equals_icase(View(root), View(path));
            })) roots.push_back(std::move(path));
        }
        start = end + 1;
    }
    return roots;
}

static bool ReadyLocalRoot(const std::wstring& root) noexcept {
    wchar_t volume[32768];
    if (!GetVolumePathNameW(root.c_str(), volume, static_cast<DWORD>(std::size(volume)))) return false;
    UINT type = GetDriveTypeW(volume);
    if (type != DRIVE_FIXED && type != DRIVE_REMOVABLE) return false;
    DWORD attributes = GetFileAttributesW(root.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) &&
        !(attributes & FILE_ATTRIBUTE_REPARSE_POINT);
}

static std::vector<std::wstring> LocalDrives() {
    wchar_t buffer[256];
    DWORD length = GetLogicalDriveStringsW(static_cast<DWORD>(std::size(buffer)), buffer);
    if (!length || length >= std::size(buffer)) return {};
    auto roots = SplitRoots(reinterpret_cast<const char16_t*>(buffer), static_cast<int32_t>(length));
    std::erase_if(roots, [](const auto& root) { return !ReadyLocalRoot(root); });
    return roots;
}

static bool Visible(const std::wstring& path, std::wstring_view query, bool folder,
    const std::vector<std::wstring>& hidden) {
    bool under_hidden = std::any_of(hidden.begin(), hidden.end(), [&](const auto& root) { return SameOrBelow(path, root); });
    uint8_t include = 0;
    return vn_spotlight_visibility_should_include(View(path).data(), static_cast<int32_t>(path.size()),
        View(query).data(), static_cast<int32_t>(query.size()), folder ? 1 : 0, under_hidden ? 1 : 0, &include) == VN_OK && include;
}

static int32_t Kind(const WIN32_FIND_DATAW& data, std::wstring_view name) noexcept {
    if (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) return 2; // Spotlight folder
    auto extension = get_extension(View(name));
    constexpr std::u16string_view apps[] = {u".exe", u".cmd", u".bat", u".msc", u".cpl", u".com", u".ps1", u".appref-ms", u".lnk"};
    for (auto app : apps) if (equals_icase(extension, app)) return 0;
    return 1;
}

static void AddResult(std::vector<Result>& out_results, const std::wstring& path, std::wstring_view name,
    const std::wstring& query, int32_t kind, size_t limit) {
    double score = 0;
    auto title = View(name), target = View(path), q = View(query);
    if (vn_spotlight_ranker_score(title.data(), static_cast<int32_t>(title.size()), title.data(), static_cast<int32_t>(title.size()),
        target.data(), static_cast<int32_t>(target.size()), target.data(), static_cast<int32_t>(target.size()),
        target.data(), static_cast<int32_t>(target.size()), kind, q.data(), static_cast<int32_t>(q.size()), &score) != VN_OK || score <= 0) return;
    for (const auto& result : out_results) if (equals_icase(View(result.path), target)) return;
    auto compare = [](const Result& a, const Result& b) {
        if (a.score != b.score) return a.score > b.score;
        return _wcsicmp(a.path.c_str(), b.path.c_str()) < 0;
    };
    Result result{path, kind, score};
    if (out_results.size() == limit && !compare(result, out_results.back())) return;
    out_results.insert(std::lower_bound(out_results.begin(), out_results.end(), result, compare), std::move(result));
    if (out_results.size() > limit) out_results.pop_back();
}

struct Walk {
    std::deque<std::wstring> pending;
    std::wstring directory;
    HANDLE cursor = INVALID_HANDLE_VALUE;
    WIN32_FIND_DATAW data{};
    explicit Walk(const std::wstring& root) { pending.push_back(root); }
    ~Walk() { if (cursor != INVALID_HANDLE_VALUE) FindClose(cursor); }
    bool Done() const noexcept { return cursor == INVALID_HANDLE_VALUE && pending.empty(); }
};

static bool Step(Walk& walk, const Search& search, std::vector<Result>& out_results, const std::wstring& query, const std::u16string& normalized,
    bool explicit_path, const std::vector<std::wstring>& hidden, size_t limit, ULONGLONG deadline) {
    for (uint32_t count = 0; count < kEntriesPerTurn; ++count) {
        if (search.cancelled.load(std::memory_order_relaxed) || GetTickCount64() >= deadline) return false;
        if (walk.cursor == INVALID_HANDLE_VALUE) {
            if (walk.pending.empty()) return true;
            walk.directory = std::move(walk.pending.front());
            walk.pending.pop_front();
            std::wstring pattern = walk.directory;
            if (pattern.back() != L'\\') pattern.push_back(L'\\');
            pattern.push_back(L'*');
            // Keep traversal working beyond MAX_PATH without changing the displayed result path.
            pattern.insert(0, L"\\\\?\\");
            walk.cursor = FindFirstFileExW(pattern.c_str(), FindExInfoBasic, &walk.data, FindExSearchNameMatch, nullptr, FIND_FIRST_EX_LARGE_FETCH);
            if (walk.cursor == INVALID_HANDLE_VALUE) continue; // Includes access denied and disappearing drives.
        }
        const auto& data = walk.data;
        std::wstring_view name(data.cFileName);
        if (name != L"." && name != L".." && !(data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
            std::wstring path = walk.directory;
            if (path.back() != L'\\') path.push_back(L'\\');
            path.append(name);
            bool folder = (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
            bool visible = Visible(path, query, folder, hidden);
            if (folder && visible && (explicit_path || !(data.dwFileAttributes & (FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM))))
                walk.pending.push_back(path);
            bool matches = explicit_path ? starts_with_icase(View(path), View(query)) :
                normalize_search(View(name)).find(normalized) != std::u16string::npos;
            if (visible && matches && (explicit_path || !(data.dwFileAttributes & (FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM))))
                AddResult(out_results, path, name, query, Kind(data, name), limit);
        }
        if (!FindNextFileW(walk.cursor, &walk.data)) {
            FindClose(walk.cursor);
            walk.cursor = INVALID_HANDLE_VALUE;
        }
    }
    return true;
}
} // namespace vnotch::services::localfiles

namespace lf = vnotch::services::localfiles;

VN_EXPORT int32_t __stdcall vn_local_file_search_create(int64_t* handle) noexcept {
    if (!handle) return VN_ERR_INVALID_ARG;
    *handle = 0;
    try { *handle = lf::searches.Add(std::make_unique<lf::Search>()); return VN_OK; }
    catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_local_file_search_cancel(int64_t handle) noexcept {
    auto lease = lf::searches.Acquire(handle);
    if (!lease.get()) return VN_ERR_INVALID_ARG;
    lease.get()->cancelled.store(true, std::memory_order_relaxed);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_local_file_search_close(int64_t handle) noexcept {
    if (handle <= 0) return VN_ERR_INVALID_ARG;
    lf::searches.Remove(handle);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_local_file_search_run(int64_t handle,
    const char16_t* query_ptr, int32_t query_len, const char16_t* roots_ptr, int32_t roots_len,
    const char16_t* hidden_ptr, int32_t hidden_len, int32_t limit, int32_t budget_ms, int32_t* count) noexcept {
    if (!count || query_len < 0 || roots_len < 0 || hidden_len < 0 || (query_len && !query_ptr) ||
        (roots_len && !roots_ptr) || (hidden_len && !hidden_ptr) || limit <= 0 || budget_ms < 0) return VN_ERR_INVALID_ARG;
    *count = 0;
    auto lease = lf::searches.Acquire(handle);
    auto* search = lease.get();
    if (!search) return VN_ERR_INVALID_ARG;
    {
        lf::ExclusiveSrwLockGuard guard(search->lock);
        search->results.clear();
    }
    auto commit_results = [&](std::vector<lf::Result>& local_results) noexcept {
        lf::ExclusiveSrwLockGuard guard(search->lock);
        search->results.swap(local_results);
        *count = static_cast<int32_t>(search->results.size());
    };
    try {
        auto q = vnotch::trim(vnotch::make_u16_view(query_ptr, query_len));
        while (!q.empty() && q.front() == u'"') q.remove_prefix(1);
        while (!q.empty() && q.back() == u'"') q.remove_suffix(1);
        if (q.size() < 2 || q.size() > 256 || search->cancelled.load()) return VN_OK;
        std::wstring query(q.begin(), q.end());
        for (auto& ch : query) if (ch == L'/') ch = L'\\';
        bool explicit_path = lf::AbsoluteLocalPath(query);
        if (query.starts_with(L"\\\\")) return VN_OK;
        auto roots = roots_len ? lf::SplitRoots(roots_ptr, roots_len) : lf::LocalDrives();
        auto hidden = lf::SplitRoots(hidden_ptr, hidden_len);
        auto normalized = vnotch::normalize_search(q);
        size_t capacity = static_cast<size_t>(std::clamp(limit, 1, lf::kMaximumResults));
        std::vector<lf::Result> local_results;
        if (explicit_path) {
            query = lf::FullPath(std::move(query));
            if (query.empty()) return VN_OK;
            DWORD attributes = GetFileAttributesW(query.c_str());
            if (attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY)) {
                auto name = vnotch::get_file_name(lf::View(query));
                WIN32_FIND_DATAW data{};
                data.dwFileAttributes = attributes;
                if (lf::Visible(query, query, false, hidden)) lf::AddResult(local_results, query,
                    std::wstring_view(reinterpret_cast<const wchar_t*>(name.data()), name.size()), query,
                    lf::Kind(data, std::wstring_view(reinterpret_cast<const wchar_t*>(name.data()), name.size())), capacity);
                commit_results(local_results);
                return VN_OK;
            }
            std::wstring root = attributes != INVALID_FILE_ATTRIBUTES ? query : query.substr(0, query.find_last_of(L'\\') + 1);
            roots = {root};
        }
        std::vector<std::unique_ptr<lf::Walk>> walks;
        for (auto& root : roots) if (lf::ReadyLocalRoot(root) && (explicit_path || lf::Visible(root, query, true, hidden)))
            walks.push_back(std::make_unique<lf::Walk>(root));
        ULONGLONG deadline = GetTickCount64() + static_cast<ULONGLONG>(std::min(budget_ms, 5000));
        // Each volume gets a turn before another volume can monopolize the scan.
        bool pending;
        do {
            pending = false;
            for (auto& walk : walks) if (!walk->Done()) {
                pending = true;
                if (!lf::Step(*walk, *search, local_results, query, normalized, explicit_path, hidden, capacity, deadline)) {
                    commit_results(local_results);
                    return VN_OK;
                }
            }
        } while (pending);
        commit_results(local_results);
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_local_file_search_get_result(int64_t handle, int32_t index,
    char16_t* path, int32_t capacity, int32_t* length, int32_t* kind, double* score) noexcept {
    auto lease = lf::searches.Acquire(handle);
    auto* search = lease.get();
    if (!search || !kind || !score || index < 0) return VN_ERR_INVALID_ARG;
    lf::SharedSrwLockGuard guard(search->lock);
    if (static_cast<size_t>(index) >= search->results.size()) return VN_ERR_INVALID_ARG;
    const auto& result = search->results[static_cast<size_t>(index)];
    *kind = result.kind;
    *score = result.score;
    return vnotch::copy_out_u16(lf::View(result.path), path, capacity, length);
}
