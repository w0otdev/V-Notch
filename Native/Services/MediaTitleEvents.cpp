#include "../vnotch_native.h"
#include "../vnotch_handle_registry.h"
#include "MediaTitleEvents.h"
#include <array>
#include <memory>
#include <string_view>

namespace vnotch::services {
std::atomic<uint64_t> title_revision{1};
std::atomic<int32_t> title_watchers{0};
uint64_t MediaTitleRevision() noexcept { return title_revision.load(std::memory_order_acquire); }
bool MediaTitleNotificationsActive() noexcept { return title_watchers.load(std::memory_order_acquire) > 0; }
}

namespace {
struct TitleWatch;
thread_local TitleWatch* current = nullptr;
struct PidCacheEntry {
    DWORD pid = 0;
    bool is_media = false;
    ULONGLONG expires_at = 0;
};
struct TitleCacheEntry {
    HWND hwnd = nullptr;
    uint64_t hash = 0;
};
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
    // shortcut: 16-PID / 32-HWND ring buffers; >32 active media root windows may evict and cause 1 benign extra refresh.
    std::array<PidCacheEntry, 16> pid_cache{};
    size_t next_pid_slot = 0;
    std::array<TitleCacheEntry, 32> title_cache{};
    size_t next_title_slot = 0;
    ~TitleWatch() {
        if (thread) { PostThreadMessageW(thread_id, WM_QUIT, 0, 0); WaitForSingleObject(thread, INFINITE); CloseHandle(thread); }
        if (ready) CloseHandle(ready);
    }
    bool IsMediaProcess(DWORD process_id) noexcept {
        const ULONGLONG now = GetTickCount64();
        for (const auto& entry : pid_cache) {
            if (entry.pid == process_id && now < entry.expires_at) return entry.is_media;
        }
        bool matched = false;
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
        if (process) {
            wchar_t path[1024]; DWORD size = static_cast<DWORD>(std::size(path));
            const BOOL found = QueryFullProcessImageNameW(process, 0, path, &size);
            CloseHandle(process);
            if (found) {
                const wchar_t* name = wcsrchr(path, L'\\'); name = name ? name + 1 : path;
                constexpr const wchar_t* media_processes[]{L"Spotify.exe", L"chrome.exe", L"msedge.exe", L"firefox.exe",
                    L"brave.exe", L"opera.exe", L"vivaldi.exe", L"arc.exe", L"zen.exe", L"msedgewebview2.exe"};
                for (const auto* candidate : media_processes) {
                    if (_wcsicmp(name, candidate) == 0) { matched = true; break; }
                }
            }
        }
        pid_cache[next_pid_slot++ & (pid_cache.size() - 1)] = { process_id, matched, now + 10000 };
        return matched;
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
    static void CALLBACK Changed(HWINEVENTHOOK, DWORD, HWND window, LONG object, LONG child, DWORD, DWORD) noexcept {
        if (!current || !window || object != OBJID_WINDOW || child != CHILDID_SELF || GetAncestor(window, GA_ROOT) != window) return;
        DWORD process_id = 0;
        GetWindowThreadProcessId(window, &process_id);
        if (!process_id || process_id == GetCurrentProcessId()) return;
        if (!current->IsMediaProcess(process_id)) return;
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
        const auto hook = SetWinEventHook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, nullptr,
            Changed, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        self->installed = hook != nullptr;
        if (hook) {
            vnotch::services::title_watchers.fetch_add(1, std::memory_order_release);
            vnotch::services::title_revision.fetch_add(1, std::memory_order_release);
        }
        SetEvent(self->ready);
        if (hook) {
            while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
            UnhookWinEvent(hook);
            vnotch::services::title_watchers.fetch_sub(1, std::memory_order_release);
        }
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
        watch->signal = signal; watch->ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!watch->ready) return VN_ERR_FAIL;
        watch->thread = CreateThread(nullptr, 0, TitleWatch::Main, watch.get(), 0, &watch->thread_id);
        if (!watch->thread) return VN_ERR_FAIL;
        WaitForSingleObject(watch->ready, INFINITE);
        if (!watch->installed) return VN_ERR_FAIL;
        *handle = watches.Add(std::move(watch)); return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t __stdcall vn_media_title_watch_close(int64_t handle) noexcept {
    watches.Remove(handle); return VN_OK;
}
