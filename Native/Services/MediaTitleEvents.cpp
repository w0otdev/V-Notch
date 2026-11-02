#include "../vnotch_native.h"
#include "../vnotch_handle_registry.h"
#include "MediaTitleEvents.h"
#include <memory>

namespace vnotch::services {
std::atomic<uint64_t> title_revision{1};
std::atomic<int32_t> title_watchers{0};
uint64_t MediaTitleRevision() noexcept { return title_revision.load(std::memory_order_acquire); }
bool MediaTitleNotificationsActive() noexcept { return title_watchers.load(std::memory_order_acquire) > 0; }
}

namespace {
struct TitleWatch;
thread_local TitleWatch* current = nullptr;
struct TitleWatch {
    HANDLE signal = nullptr, ready = nullptr, thread = nullptr;
    DWORD thread_id = 0;
    bool installed = false;
    ~TitleWatch() {
        if (thread) { PostThreadMessageW(thread_id, WM_QUIT, 0, 0); WaitForSingleObject(thread, INFINITE); CloseHandle(thread); }
        if (ready) CloseHandle(ready);
    }
    static void CALLBACK Changed(HWINEVENTHOOK, DWORD, HWND window, LONG object, LONG child, DWORD, DWORD) noexcept {
        if (!current || !window || object != OBJID_WINDOW || child != CHILDID_SELF || GetAncestor(window, GA_ROOT) != window) return;
        DWORD process_id = 0;
        GetWindowThreadProcessId(window, &process_id);
        if (!process_id || process_id == GetCurrentProcessId()) return;
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
        if (!process) return;
        wchar_t path[32768]; DWORD size = 32768;
        const BOOL found = QueryFullProcessImageNameW(process, 0, path, &size);
        CloseHandle(process);
        if (!found) return;
        const wchar_t* name = wcsrchr(path, L'\\'); name = name ? name + 1 : path;
        constexpr const wchar_t* media_processes[]{L"Spotify.exe", L"chrome.exe", L"msedge.exe", L"firefox.exe",
            L"brave.exe", L"opera.exe", L"vivaldi.exe", L"arc.exe", L"zen.exe", L"msedgewebview2.exe"};
        for (const auto* candidate : media_processes) {
            if (_wcsicmp(name, candidate) == 0) {
                vnotch::services::title_revision.fetch_add(1, std::memory_order_release);
                SetEvent(current->signal); return;
            }
        }
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
