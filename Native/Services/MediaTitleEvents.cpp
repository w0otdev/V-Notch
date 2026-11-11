#include "../vnotch_native.h"
#include "../vnotch_handle_registry.h"
#include "MediaTitleEvents.h"
#include <array>
#include <chrono>
#include <condition_variable>
#include <cwchar>
#include <memory>
#include <mutex>
#include <string_view>

namespace vnotch::services {
std::atomic<uint64_t> title_revision{1};
std::atomic<int32_t> spotify_watchers{0};
uint64_t MediaTitleRevision() noexcept { return title_revision.load(std::memory_order_acquire); }
bool MediaTitleNotificationsActive() noexcept { return spotify_watchers.load(std::memory_order_acquire) > 0; }
}

namespace {
constexpr UINT kMsgApplyTargets = WM_APP + 1;
constexpr size_t kMaxTargetPids = 4;

struct TitleWatch;
thread_local TitleWatch* current = nullptr;

enum class MediaExeKind : uint8_t {
    None = 0,
    Spotify,
    Chrome,
    Edge,
    Firefox,
    Brave,
    Opera,
    Vivaldi,
    Arc,
    Zen
};

struct PidKindCacheEntry {
    DWORD pid = 0;
    MediaExeKind kind = MediaExeKind::None;
    ULONGLONG expires_at = 0;
};

struct TargetEntry {
    DWORD pid = 0;
    bool is_spotify = false;
};

struct TargetSet {
    std::array<TargetEntry, kMaxTargetPids> entries{};
    size_t count = 0;

    bool Equals(const TargetSet& other) const noexcept {
        if (count != other.count) return false;
        for (size_t i = 0; i < count; ++i) {
            if (entries[i].pid != other.entries[i].pid ||
                entries[i].is_spotify != other.entries[i].is_spotify) {
                return false;
            }
        }
        return true;
    }
};

struct HookSlot {
    DWORD pid = 0;
    HWINEVENTHOOK hook = nullptr;
    bool is_spotify = false;
};

struct TitleCacheEntry {
    HWND hwnd = nullptr;
    uint64_t hash = 0;
};

constexpr char16_t ToLowerAscii(char16_t ch) noexcept {
    return (ch >= u'A' && ch <= u'Z') ? static_cast<char16_t>(ch + (u'a' - u'A')) : ch;
}

bool ContainsIgnoreCase(std::u16string_view haystack, std::u16string_view needle) noexcept {
    if (needle.empty() || haystack.size() < needle.size()) return false;
    const size_t limit = haystack.size() - needle.size();
    for (size_t i = 0; i <= limit; ++i) {
        bool match = true;
        for (size_t j = 0; j < needle.size(); ++j) {
            if (ToLowerAscii(haystack[i + j]) != ToLowerAscii(needle[j])) {
                match = false;
                break;
            }
        }
        if (match) return true;
    }
    return false;
}

MediaExeKind ClassifyExecutable(const wchar_t* exe_name) noexcept {
    if (!exe_name || !*exe_name) return MediaExeKind::None;
    if (_wcsicmp(exe_name, L"Spotify.exe") == 0) return MediaExeKind::Spotify;
    if (_wcsicmp(exe_name, L"chrome.exe") == 0) return MediaExeKind::Chrome;
    if (_wcsicmp(exe_name, L"msedge.exe") == 0 || _wcsicmp(exe_name, L"msedgewebview2.exe") == 0) return MediaExeKind::Edge;
    if (_wcsicmp(exe_name, L"firefox.exe") == 0) return MediaExeKind::Firefox;
    if (_wcsicmp(exe_name, L"brave.exe") == 0) return MediaExeKind::Brave;
    if (_wcsicmp(exe_name, L"opera.exe") == 0) return MediaExeKind::Opera;
    if (_wcsicmp(exe_name, L"vivaldi.exe") == 0) return MediaExeKind::Vivaldi;
    if (_wcsicmp(exe_name, L"arc.exe") == 0) return MediaExeKind::Arc;
    if (_wcsicmp(exe_name, L"zen.exe") == 0) return MediaExeKind::Zen;
    return MediaExeKind::None;
}

MediaExeKind QueryProcessMediaKindUncached(DWORD process_id) noexcept {
    if (!process_id || process_id == GetCurrentProcessId()) return MediaExeKind::None;
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
    if (!process) return MediaExeKind::None;
    wchar_t path[1024]{};
    DWORD size = static_cast<DWORD>(std::size(path));
    const BOOL found = QueryFullProcessImageNameW(process, 0, path, &size);
    CloseHandle(process);
    if (!found) return MediaExeKind::None;
    const wchar_t* name = wcsrchr(path, L'\\');
    return ClassifyExecutable(name ? name + 1 : path);
}

bool MatchesSourceApp(std::u16string_view source_app, MediaExeKind kind) noexcept {
    if (source_app.empty() || kind == MediaExeKind::None) return false;
    switch (kind) {
    case MediaExeKind::Spotify:
        return ContainsIgnoreCase(source_app, u"spotify");
    case MediaExeKind::Chrome:
        return ContainsIgnoreCase(source_app, u"chrome") || ContainsIgnoreCase(source_app, u"chromium");
    case MediaExeKind::Edge:
        return ContainsIgnoreCase(source_app, u"msedge") || ContainsIgnoreCase(source_app, u"edge") || ContainsIgnoreCase(source_app, u"webview");
    case MediaExeKind::Firefox:
        return ContainsIgnoreCase(source_app, u"firefox") || ContainsIgnoreCase(source_app, u"mozilla") || ContainsIgnoreCase(source_app, u"308046b0af4a39cb");
    case MediaExeKind::Brave:
        return ContainsIgnoreCase(source_app, u"brave");
    case MediaExeKind::Opera:
        return ContainsIgnoreCase(source_app, u"opera");
    case MediaExeKind::Vivaldi:
        return ContainsIgnoreCase(source_app, u"vivaldi");
    case MediaExeKind::Arc:
        return ContainsIgnoreCase(source_app, u"arc");
    case MediaExeKind::Zen:
        return ContainsIgnoreCase(source_app, u"zen");
    default:
        return false;
    }
}

uint64_t HashWindowTitle(std::wstring_view text, bool visible) noexcept {
    uint64_t hash = 14695981039346656037ull ^ (visible ? 1ull : 0ull);
    for (const wchar_t ch : text) {
        hash = (hash ^ static_cast<uint16_t>(ch)) * 1099511628211ull;
    }
    return hash;
}

struct TitleWatch {
    HANDLE signal = nullptr, ready = nullptr, thread = nullptr;
    DWORD thread_id = 0;
    bool installed = false;

    std::mutex target_mutex;
    std::condition_variable target_cv;
    TargetSet desired_targets{};
    uint64_t request_gen = 0;
    uint64_t applied_gen = 0;
    uint32_t active_hook_count = 0;

    // Resolver cache (guarded by resolver_mutex so SetSource/SetTargetPid never contend with Changed).
    std::mutex resolver_mutex;
    std::array<PidKindCacheEntry, 32> pid_cache{};
    size_t next_pid_slot = 0;

    // Hook-thread-only state:
    std::array<HookSlot, kMaxTargetPids> hooks{};
    bool counted_spotify = false;
    // shortcut: 32-HWND ring buffer; >32 active media root windows may evict and cause 1 benign extra refresh.
    std::array<TitleCacheEntry, 32> title_cache{};
    size_t next_title_slot = 0;

    ~TitleWatch() {
        if (thread) {
            PostThreadMessageW(thread_id, WM_QUIT, 0, 0);
            WaitForSingleObject(thread, INFINITE);
            CloseHandle(thread);
        }
        if (ready) CloseHandle(ready);
    }

    MediaExeKind GetProcessMediaKind(DWORD process_id) noexcept {
        if (!process_id || process_id == GetCurrentProcessId()) return MediaExeKind::None;
        const ULONGLONG now = GetTickCount64();
        {
            std::lock_guard lock(resolver_mutex);
            for (const auto& entry : pid_cache) {
                if (entry.pid == process_id && now < entry.expires_at) return entry.kind;
            }
        }
        const MediaExeKind kind = QueryProcessMediaKindUncached(process_id);
        {
            std::lock_guard lock(resolver_mutex);
            pid_cache[next_pid_slot++ & (pid_cache.size() - 1)] = { process_id, kind, now + 10000 };
        }
        return kind;
    }

    TargetSet ResolveSourceTargets(std::u16string_view source_app) noexcept {
        TargetSet out{};
        if (source_app.empty()) return out;

        struct EnumCtx {
            TitleWatch* watch;
            std::u16string_view source_app;
            TargetSet* out;
            DWORD self_pid;
            std::array<DWORD, 64> seen_pids{};
            size_t seen_count = 0;
        } ctx{ this, source_app, &out, GetCurrentProcessId() };

        EnumWindows([](HWND hwnd, LPARAM lparam) noexcept -> BOOL {
            auto* c = reinterpret_cast<EnumCtx*>(lparam);
            if (!hwnd || GetAncestor(hwnd, GA_ROOT) != hwnd) return TRUE;
            DWORD pid = 0;
            GetWindowThreadProcessId(hwnd, &pid);
            if (!pid || pid == c->self_pid) return TRUE;

            for (size_t i = 0; i < c->seen_count; ++i) {
                if (c->seen_pids[i] == pid) return TRUE;
            }
            if (c->seen_count < c->seen_pids.size()) {
                c->seen_pids[c->seen_count++] = pid;
            }

            const MediaExeKind kind = c->watch->GetProcessMediaKind(pid);
            if (!MatchesSourceApp(c->source_app, kind)) return TRUE;

            c->out->entries[c->out->count++] = { pid, kind == MediaExeKind::Spotify };
            return c->out->count < kMaxTargetPids ? TRUE : FALSE;
        }, reinterpret_cast<LPARAM>(&ctx));

        return out;
    }

    bool PublishTargets(const TargetSet& targets, uint32_t& out_hooked) noexcept {
        uint64_t gen = 0;
        {
            std::lock_guard lock(target_mutex);
            if (desired_targets.Equals(targets) && applied_gen == request_gen) {
                out_hooked = active_hook_count;
                return true;
            }
            desired_targets = targets;
            gen = ++request_gen;
        }

        if (!PostThreadMessageW(thread_id, kMsgApplyTargets, 0, 0)) {
            out_hooked = 0;
            return false;
        }

        std::unique_lock lock(target_mutex);
        target_cv.wait_for(lock, std::chrono::milliseconds(250), [&] { return applied_gen >= gen; });
        out_hooked = active_hook_count;
        return applied_gen >= gen;
    }

    bool RecordTitleChanged(HWND window, uint64_t hash) noexcept {
        for (auto& entry : title_cache) {
            if (entry.hwnd == window) {
                if (entry.hash == hash) return false;
                entry.hash = hash;
                return true;
            }
        }
        title_cache[next_title_slot++ & (title_cache.size() - 1)] = { window, hash };
        return true;
    }

    void ClearHooksOnThread() noexcept {
        for (auto& slot : hooks) {
            if (slot.hook) {
                UnhookWinEvent(slot.hook);
                slot = {};
            }
        }
        if (counted_spotify) {
            counted_spotify = false;
            vnotch::services::spotify_watchers.fetch_sub(1, std::memory_order_release);
        }
    }

    void ApplyTargetsOnThread() noexcept {
        TargetSet targets{};
        uint64_t gen = 0;
        {
            std::lock_guard lock(target_mutex);
            targets = desired_targets;
            gen = request_gen;
        }

        bool changed = false;
        // Unhook slots that are no longer in the desired target PID list.
        for (auto& slot : hooks) {
            if (!slot.hook) continue;
            bool keep = false;
            for (size_t i = 0; i < targets.count; ++i) {
                if (targets.entries[i].pid != 0 && targets.entries[i].pid == slot.pid) {
                    slot.is_spotify = targets.entries[i].is_spotify;
                    keep = true;
                    break;
                }
            }
            if (!keep) {
                UnhookWinEvent(slot.hook);
                slot = {};
                changed = true;
            }
        }

        // Hook any newly requested non-zero PIDs (never idProcess == 0).
        for (size_t i = 0; i < targets.count; ++i) {
            const DWORD pid = targets.entries[i].pid;
            if (pid == 0 || pid == GetCurrentProcessId()) continue;

            bool already_hooked = false;
            for (const auto& slot : hooks) {
                if (slot.hook && slot.pid == pid) {
                    already_hooked = true;
                    break;
                }
            }
            if (already_hooked) continue;

            for (auto& slot : hooks) {
                if (!slot.hook) {
                    const HWINEVENTHOOK hook = SetWinEventHook(
                        EVENT_OBJECT_NAMECHANGE,
                        EVENT_OBJECT_NAMECHANGE,
                        nullptr,
                        Changed,
                        pid,
                        0,
                        WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                    if (hook) {
                        slot = { pid, hook, targets.entries[i].is_spotify };
                        changed = true;
                    }
                    break;
                }
            }
        }

        uint32_t count = 0;
        bool has_spotify = false;
        for (const auto& slot : hooks) {
            if (slot.hook) {
                ++count;
                if (slot.is_spotify) has_spotify = true;
            }
        }

        if (has_spotify != counted_spotify) {
            counted_spotify = has_spotify;
            if (has_spotify) {
                vnotch::services::spotify_watchers.fetch_add(1, std::memory_order_release);
            } else {
                vnotch::services::spotify_watchers.fetch_sub(1, std::memory_order_release);
            }
        }

        if (changed) {
            title_cache = {};
            vnotch::services::title_revision.fetch_add(1, std::memory_order_release);
        }

        {
            std::lock_guard lock(target_mutex);
            active_hook_count = count;
            applied_gen = gen;
        }
        target_cv.notify_all();
    }

    static void CALLBACK Changed(HWINEVENTHOOK, DWORD, HWND window, LONG object, LONG child, DWORD, DWORD) noexcept {
        if (!current || !window || object != OBJID_WINDOW || child != CHILDID_SELF || GetAncestor(window, GA_ROOT) != window) return;
        const bool visible = IsWindowVisible(window) != FALSE;
        wchar_t title[512]{};
        const int len = GetWindowTextW(window, title, static_cast<int>(std::size(title)));
        if (len <= 0 && !visible) return;
        const uint64_t hash = HashWindowTitle(std::wstring_view(title, len > 0 ? static_cast<size_t>(len) : 0), visible);
        if (!current->RecordTitleChanged(window, hash)) return;
        vnotch::services::title_revision.fetch_add(1, std::memory_order_release);
        SetEvent(current->signal);
    }

    static DWORD WINAPI Main(void* parameter) noexcept {
        auto* self = static_cast<TitleWatch*>(parameter);
        current = self;
        MSG message{};
        PeekMessageW(&message, nullptr, WM_USER, WM_USER, PM_NOREMOVE);
        self->installed = true;
        SetEvent(self->ready);
        while (GetMessageW(&message, nullptr, 0, 0) > 0) {
            if (!message.hwnd && message.message == kMsgApplyTargets) {
                self->ApplyTargetsOnThread();
                continue;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        self->ClearHooksOnThread();
        current = nullptr;
        return 0;
    }
};

vnotch::HandleRegistry<TitleWatch> watches;
}

VN_EXPORT int32_t __stdcall vn_media_title_watch_create(HANDLE signal, int64_t* handle) noexcept {
    if (!signal || !handle) return VN_ERR_INVALID_ARG;
    *handle = 0;
    try {
        auto watch = std::make_unique<TitleWatch>();
        watch->signal = signal;
        watch->ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!watch->ready) return VN_ERR_FAIL;
        watch->thread = CreateThread(nullptr, 0, TitleWatch::Main, watch.get(), 0, &watch->thread_id);
        if (!watch->thread) return VN_ERR_FAIL;
        WaitForSingleObject(watch->ready, INFINITE);
        if (!watch->installed) return VN_ERR_FAIL;
        *handle = watches.Add(std::move(watch));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_media_title_watch_set_target_pid(int64_t handle, uint32_t process_id) noexcept {
    auto watch = watches.Acquire(handle);
    if (!watch) return VN_ERR_INVALID_ARG;
    try {
        TargetSet targets{};
        if (process_id != 0 && process_id != GetCurrentProcessId()) {
            const MediaExeKind kind = watch->GetProcessMediaKind(process_id);
            targets.entries[0] = { process_id, kind == MediaExeKind::Spotify };
            targets.count = 1;
        }
        uint32_t hooked = 0;
        if (!watch->PublishTargets(targets, hooked)) return VN_ERR_FAIL;
        return (process_id == 0 || hooked > 0) ? VN_OK : VN_ERR_FAIL;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_media_title_watch_set_source(
    int64_t handle,
    const char16_t* source_app,
    int32_t source_app_len,
    uint8_t* out_active) noexcept {
    if (!out_active || source_app_len < 0 || (source_app_len > 0 && !source_app)) return VN_ERR_INVALID_ARG;
    *out_active = 0;
    auto watch = watches.Acquire(handle);
    if (!watch) return VN_ERR_INVALID_ARG;
    try {
        const std::u16string_view view(source_app ? source_app : u"", static_cast<size_t>(source_app_len));
        const TargetSet targets = watch->ResolveSourceTargets(view);
        uint32_t hooked = 0;
        if (!watch->PublishTargets(targets, hooked)) return VN_ERR_FAIL;
        *out_active = hooked > 0 ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_media_title_watch_close(int64_t handle) noexcept {
    watches.Remove(handle);
    return VN_OK;
}
