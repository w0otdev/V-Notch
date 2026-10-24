#include "../vnotch_common.h"
#include <psapi.h>
#include <cstdint>

#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "shcore.lib")
#pragma comment(lib, "winmm.lib")
#include <shellscalingapi.h>
#include <mmsystem.h>

// Direct Win32 surface for the WPF shell. Every call the C# layer used to P/Invoke
// from user32/kernel32/psapi/dwmapi lands here, so the managed side never touches
// those DLLs. Handles cross the ABI as int64_t, results as int32_t status codes.

namespace vnotch::services {

#pragma pack(push, 1)
struct VnPoint {
    int32_t x;
    int32_t y;
};

struct VnRect {
    int32_t left;
    int32_t top;
    int32_t right;
    int32_t bottom;
};

struct VnFileTime {
    uint32_t low;
    uint32_t high;
};

struct VnMemoryStatus {
    uint32_t length;
    uint32_t memory_load;
    uint64_t total_phys;
    uint64_t avail_phys;
    uint64_t total_page_file;
    uint64_t avail_page_file;
    uint64_t total_virtual;
    uint64_t avail_virtual;
    uint64_t avail_extended_virtual;
};

struct VnProcessMemoryCounters {
    uint32_t cb;
    uint32_t page_fault_count;
    uint64_t peak_working_set_size;
    uint64_t working_set_size;
    uint64_t quota_peak_paged_pool_usage;
    uint64_t quota_paged_pool_usage;
    uint64_t quota_peak_non_paged_pool_usage;
    uint64_t quota_non_paged_pool_usage;
    uint64_t pagefile_usage;
    uint64_t peak_pagefile_usage;
    uint64_t private_usage;
};

struct VnMonitorInfo {
    int32_t size;
    VnRect monitor;
    VnRect work;
    uint32_t flags;
};

struct VnDibSurface {
    int64_t memory_dc;
    int64_t bitmap;
    int64_t previous_bitmap;
    int64_t bits;
    int32_t width;
    int32_t height;
};

struct VnLargeWindow {
    int64_t hwnd;
    uint32_t process_id;
    uint8_t class_matches;
};

struct VnKeyboardHookEvent {
    uint32_t vk_code;
    uint32_t scan_code;
    uint32_t flags;
    uint32_t time;
    int64_t extra_info;
    int32_t message;
};
#pragma pack(pop)

static_assert(sizeof(VnPoint) == sizeof(POINT));
static_assert(sizeof(VnRect) == sizeof(RECT));
static_assert(sizeof(VnMonitorInfo) == sizeof(MONITORINFO));
static_assert(sizeof(VnFileTime) == sizeof(FILETIME));
static_assert(sizeof(VnMemoryStatus) == sizeof(MEMORYSTATUSEX));
static_assert(sizeof(VnProcessMemoryCounters) == sizeof(PROCESS_MEMORY_COUNTERS_EX));

using KeyboardHookCallback = int32_t(__stdcall*)(int64_t context, const VnKeyboardHookEvent* event);
using ForegroundHookCallback = void(__stdcall*)(int64_t context, uint32_t event_type, int64_t hwnd);

namespace win32 {

inline HWND ToHwnd(int64_t value) noexcept {
    return reinterpret_cast<HWND>(static_cast<intptr_t>(value));
}

inline int64_t FromHandle(const void* handle) noexcept {
    return static_cast<int64_t>(reinterpret_cast<intptr_t>(handle));
}

inline int32_t Status(BOOL ok) noexcept {
    return ok ? VN_OK : VN_ERR_FAIL;
}

// ---- Desktop layer (the notch sits directly above the desktop icons) ----

SRWLOCK g_desktop_lock = SRWLOCK_INIT;
HWND g_desktop_host = nullptr;

BOOL CALLBACK FindDesktopHostProc(HWND top_level, LPARAM param) noexcept {
    if (FindWindowExW(top_level, nullptr, L"SHELLDLL_DefView", nullptr) == nullptr) return TRUE;
    *reinterpret_cast<HWND*>(param) = top_level;
    return FALSE;
}

bool HostHasShellView(HWND host) noexcept {
    return host != nullptr && IsWindow(host) &&
           FindWindowExW(host, nullptr, L"SHELLDLL_DefView", nullptr) != nullptr;
}

HWND GetCachedDesktopHost() noexcept {
    AcquireSRWLockExclusive(&g_desktop_lock);
    HWND result = g_desktop_host;
    if (!HostHasShellView(result)) {
        result = nullptr;
        EnumWindows(FindDesktopHostProc, reinterpret_cast<LPARAM>(&result));
        g_desktop_host = result;
    }
    ReleaseSRWLockExclusive(&g_desktop_lock);
    return result;
}

struct LargeWindowScan {
    int32_t min_size;
    std::u16string_view class_needle;
    VnLargeWindow* buffer;
    int32_t capacity;
    int32_t written;
};

bool ClassNameContains(HWND hwnd, std::u16string_view needle) noexcept {
    if (needle.empty()) return false;
    wchar_t name[256];
    const int length = GetClassNameW(hwnd, name, static_cast<int>(std::size(name)));
    if (length <= 0) return false;
    const std::u16string lowered = to_lower_string(
        std::u16string_view(reinterpret_cast<const char16_t*>(name), static_cast<size_t>(length)));
    const std::u16string needle_lowered = to_lower_string(needle);
    return lowered.find(needle_lowered) != std::u16string::npos;
}

BOOL CALLBACK CollectLargeWindowProc(HWND hwnd, LPARAM param) noexcept {
    auto& scan = *reinterpret_cast<LargeWindowScan*>(param);
    if (scan.written >= scan.capacity) return FALSE;
    RECT rect{};
    if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, &rect)) return TRUE;
    if (rect.right - rect.left < scan.min_size || rect.bottom - rect.top < scan.min_size) return TRUE;
    DWORD process_id = 0;
    GetWindowThreadProcessId(hwnd, &process_id);
    scan.buffer[scan.written++] = VnLargeWindow{
        FromHandle(hwnd), process_id, static_cast<uint8_t>(ClassNameContains(hwnd, scan.class_needle) ? 1 : 0)};
    return TRUE;
}

inline HWND NotTopmost() noexcept {
    return reinterpret_cast<HWND>(static_cast<intptr_t>(-2));
}

// ---- Low-level keyboard hook slots ----
// A WH_KEYBOARD_LL procedure carries no context, so each live hook owns a distinct thunk.

constexpr int kHookSlots = 4;

struct HookSlot {
    std::atomic<HHOOK> hook{nullptr};
    std::atomic<KeyboardHookCallback> callback{nullptr};
    std::atomic<int64_t> context{0};
};

HookSlot g_hook_slots[kHookSlots];
SRWLOCK g_hook_lock = SRWLOCK_INIT;

template <int N>
LRESULT CALLBACK KeyboardThunk(int code, WPARAM message, LPARAM data) noexcept {
    HookSlot& slot = g_hook_slots[N];
    if (code >= 0 && data != 0) {
        const KeyboardHookCallback callback = slot.callback.load(std::memory_order_acquire);
        if (callback != nullptr) {
            const auto* key = reinterpret_cast<const KBDLLHOOKSTRUCT*>(data);
            const VnKeyboardHookEvent event{
                key->vkCode, key->scanCode, key->flags, key->time,
                static_cast<int64_t>(key->dwExtraInfo), static_cast<int32_t>(message)};
            if (callback(slot.context.load(std::memory_order_relaxed), &event) != 0) return 1;
        }
    }
    return CallNextHookEx(slot.hook.load(std::memory_order_acquire), code, message, data);
}

constexpr HOOKPROC kThunks[kHookSlots] = {
    KeyboardThunk<0>, KeyboardThunk<1>, KeyboardThunk<2>, KeyboardThunk<3>};

// ---- Foreground WinEvent hook slots (EVENT_SYSTEM_FOREGROUND, out-of-context) ----

struct EventSlot {
    std::atomic<HWINEVENTHOOK> hook{nullptr};
    std::atomic<ForegroundHookCallback> callback{nullptr};
    std::atomic<int64_t> context{0};
};

constexpr int kEventSlots = 8;
EventSlot g_event_slots[kEventSlots];
SRWLOCK g_event_lock = SRWLOCK_INIT;

template <int N>
void CALLBACK ForegroundThunk(HWINEVENTHOOK, DWORD event_type, HWND hwnd, LONG, LONG, DWORD, DWORD) noexcept {
    EventSlot& slot = g_event_slots[N];
    const ForegroundHookCallback callback = slot.callback.load(std::memory_order_acquire);
    if (callback != nullptr)
        callback(slot.context.load(std::memory_order_relaxed), event_type, FromHandle(hwnd));
}

constexpr WINEVENTPROC kEventThunks[kEventSlots] = {
    ForegroundThunk<0>, ForegroundThunk<1>, ForegroundThunk<2>, ForegroundThunk<3>,
    ForegroundThunk<4>, ForegroundThunk<5>, ForegroundThunk<6>, ForegroundThunk<7>};

} // namespace win32
} // namespace vnotch::services

namespace svc = vnotch::services;
namespace w32 = vnotch::services::win32;

extern "C" {

// ---------------------------------------------------------------- user32: windows

VN_EXPORT int32_t __stdcall vn_win32_get_foreground_window(int64_t* out_hwnd) noexcept {
    if (!out_hwnd) return VN_ERR_INVALID_ARG;
    *out_hwnd = w32::FromHandle(GetForegroundWindow());
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_set_foreground_window(int64_t hwnd) noexcept {
    return w32::Status(SetForegroundWindow(w32::ToHwnd(hwnd)));
}

VN_EXPORT int32_t __stdcall vn_win32_get_window(int64_t hwnd, uint32_t command, int64_t* out_hwnd) noexcept {
    if (!out_hwnd) return VN_ERR_INVALID_ARG;
    *out_hwnd = w32::FromHandle(GetWindow(w32::ToHwnd(hwnd), command));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_window_from_point(int32_t x, int32_t y, int64_t* out_hwnd) noexcept {
    if (!out_hwnd) return VN_ERR_INVALID_ARG;
    *out_hwnd = w32::FromHandle(WindowFromPoint(POINT{x, y}));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_window_long(int64_t hwnd, int32_t index, int32_t* out_value) noexcept {
    if (!out_value) return VN_ERR_INVALID_ARG;
    *out_value = static_cast<int32_t>(GetWindowLongW(w32::ToHwnd(hwnd), index));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_set_window_long(int64_t hwnd, int32_t index, int32_t value, int32_t* out_previous) noexcept {
    const LONG previous = SetWindowLongW(w32::ToHwnd(hwnd), index, value);
    if (out_previous) *out_previous = static_cast<int32_t>(previous);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_set_window_pos(
    int64_t hwnd, int64_t insert_after, int32_t x, int32_t y, int32_t cx, int32_t cy, uint32_t flags) noexcept {
    return w32::Status(SetWindowPos(w32::ToHwnd(hwnd), w32::ToHwnd(insert_after), x, y, cx, cy, flags));
}

VN_EXPORT int32_t __stdcall vn_win32_get_window_rect(int64_t hwnd, svc::VnRect* out_rect) noexcept {
    if (!out_rect) return VN_ERR_INVALID_ARG;
    RECT rect{};
    if (!GetWindowRect(w32::ToHwnd(hwnd), &rect)) return VN_ERR_FAIL;
    *out_rect = svc::VnRect{rect.left, rect.top, rect.right, rect.bottom};
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_window_thread_process_id(
    int64_t hwnd, uint32_t* out_process_id, uint32_t* out_thread_id) noexcept {
    DWORD process_id = 0;
    const DWORD thread_id = GetWindowThreadProcessId(w32::ToHwnd(hwnd), &process_id);
    if (out_process_id) *out_process_id = process_id;
    if (out_thread_id) *out_thread_id = thread_id;
    return thread_id != 0 ? VN_OK : VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_win32_get_dpi_for_window(int64_t hwnd, uint32_t* out_dpi) noexcept {
    if (!out_dpi) return VN_ERR_INVALID_ARG;
    *out_dpi = GetDpiForWindow(w32::ToHwnd(hwnd));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_cursor_pos(svc::VnPoint* out_point) noexcept {
    if (!out_point) return VN_ERR_INVALID_ARG;
    POINT point{};
    if (!GetCursorPos(&point)) return VN_ERR_FAIL;
    *out_point = svc::VnPoint{point.x, point.y};
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_system_metrics(int32_t index, int32_t* out_value) noexcept {
    if (!out_value) return VN_ERR_INVALID_ARG;
    *out_value = GetSystemMetrics(index);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_async_key_state(int32_t vk, int16_t* out_state) noexcept {
    if (!out_state) return VN_ERR_INVALID_ARG;
    *out_state = GetAsyncKeyState(vk);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_send_extended_key_tap(uint8_t vk) noexcept {
    keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, 0);
    keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, 0);
    return VN_OK;
}

// Returns Win32 error code in out_last_error when the call fails.
VN_EXPORT int32_t __stdcall vn_win32_register_hotkey(
    int64_t hwnd, int32_t id, uint32_t modifiers, uint32_t vk, int32_t* out_last_error) noexcept {
    SetLastError(0);
    if (RegisterHotKey(w32::ToHwnd(hwnd), id, modifiers, vk)) return VN_OK;
    if (out_last_error) *out_last_error = static_cast<int32_t>(GetLastError());
    return VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_win32_unregister_hotkey(int64_t hwnd, int32_t id) noexcept {
    return w32::Status(UnregisterHotKey(w32::ToHwnd(hwnd), id));
}

VN_EXPORT int32_t __stdcall vn_win32_set_window_display_affinity(
    int64_t hwnd, uint32_t affinity, int32_t* out_last_error) noexcept {
    SetLastError(0);
    if (SetWindowDisplayAffinity(w32::ToHwnd(hwnd), affinity)) return VN_OK;
    if (out_last_error) *out_last_error = static_cast<int32_t>(GetLastError());
    return VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_win32_dwm_set_window_attribute_int(
    int64_t hwnd, int32_t attribute, int32_t value, int32_t* out_hresult) noexcept {
    const HRESULT hr = DwmSetWindowAttribute(w32::ToHwnd(hwnd), static_cast<DWORD>(attribute), &value, sizeof(value));
    if (out_hresult) *out_hresult = static_cast<int32_t>(hr);
    return SUCCEEDED(hr) ? VN_OK : VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_win32_delete_gdi_object(int64_t handle) noexcept {
    return w32::Status(DeleteObject(reinterpret_cast<HGDIOBJ>(static_cast<intptr_t>(handle))));
}

VN_EXPORT int32_t __stdcall vn_win32_destroy_icon(int64_t handle) noexcept {
    return w32::Status(DestroyIcon(reinterpret_cast<HICON>(static_cast<intptr_t>(handle))));
}

// Current display frequency in Hz for the named device (null/empty = primary display).
VN_EXPORT int32_t __stdcall vn_win32_get_display_frequency(
    const char16_t* device_name, int32_t device_name_length, uint32_t* out_hz) noexcept {
    if (!out_hz || device_name_length < 0) return VN_ERR_INVALID_ARG;
    wchar_t name[64]{};
    const wchar_t* name_ptr = nullptr;
    if (device_name != nullptr && device_name_length > 0) {
        if (device_name_length >= static_cast<int32_t>(std::size(name))) return VN_ERR_INVALID_ARG;
        std::copy_n(reinterpret_cast<const wchar_t*>(device_name), device_name_length, name);
        name_ptr = name;
    }
    DEVMODEW mode{};
    mode.dmSize = sizeof(mode);
    if (!EnumDisplaySettingsW(name_ptr, ENUM_CURRENT_SETTINGS, &mode)) return VN_ERR_FAIL;
    *out_hz = mode.dmDisplayFrequency;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_is_window_visible(int64_t hwnd, uint8_t* out_visible) noexcept {
    if (!out_visible) return VN_ERR_INVALID_ARG;
    *out_visible = IsWindowVisible(w32::ToHwnd(hwnd)) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_is_iconic(int64_t hwnd, uint8_t* out_iconic) noexcept {
    if (!out_iconic) return VN_ERR_INVALID_ARG;
    *out_iconic = IsIconic(w32::ToHwnd(hwnd)) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_add_clipboard_format_listener(int64_t hwnd, int32_t* out_last_error) noexcept {
    SetLastError(0);
    if (AddClipboardFormatListener(w32::ToHwnd(hwnd))) return VN_OK;
    if (out_last_error) *out_last_error = static_cast<int32_t>(GetLastError());
    return VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_win32_remove_clipboard_format_listener(int64_t hwnd) noexcept {
    return w32::Status(RemoveClipboardFormatListener(w32::ToHwnd(hwnd)));
}

// DPI of the monitor nearest to the window (MDT_EFFECTIVE_DPI).
VN_EXPORT int32_t __stdcall vn_win32_get_monitor_dpi_for_window(
    int64_t hwnd, uint32_t monitor_flags, uint32_t* out_dpi_x, uint32_t* out_dpi_y) noexcept {
    if (!out_dpi_x || !out_dpi_y) return VN_ERR_INVALID_ARG;
    HMONITOR monitor = MonitorFromWindow(w32::ToHwnd(hwnd), monitor_flags);
    if (monitor == nullptr) return VN_ERR_FAIL;
    return SUCCEEDED(GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out_dpi_x, out_dpi_y)) ? VN_OK : VN_ERR_FAIL;
}

// True when the window class name contains needle (ordinal ignore-case).
VN_EXPORT int32_t __stdcall vn_win32_window_class_contains(
    int64_t hwnd, const char16_t* needle, int32_t needle_length, uint8_t* out_matches) noexcept {
    if (!out_matches || !needle || needle_length <= 0) return VN_ERR_INVALID_ARG;
    *out_matches = svc::win32::ClassNameContains(
        w32::ToHwnd(hwnd), std::u16string_view(needle, static_cast<size_t>(needle_length))) ? 1 : 0;
    return VN_OK;
}

// Visible top-level windows at least min_size in both dimensions (screen-capture tool detection).
VN_EXPORT int32_t __stdcall vn_win32_enum_large_visible_windows(
    int32_t min_size, const char16_t* class_needle, int32_t class_needle_length,
    svc::VnLargeWindow* buffer, int32_t capacity, int32_t* out_written) noexcept {
    if (!buffer || capacity <= 0 || !out_written || class_needle_length < 0) return VN_ERR_INVALID_ARG;
    svc::win32::LargeWindowScan scan{
        min_size,
        class_needle ? std::u16string_view(class_needle, static_cast<size_t>(class_needle_length)) : std::u16string_view(),
        buffer, capacity, 0};
    EnumWindows(svc::win32::CollectLargeWindowProc, reinterpret_cast<LPARAM>(&scan));
    *out_written = scan.written;
    return VN_OK;
}

// ---------------------------------------------------------------- timers, DWM and GDI capture

VN_EXPORT int32_t __stdcall vn_win32_time_begin_period(uint32_t period_ms, uint32_t* out_result) noexcept {
    const MMRESULT result = timeBeginPeriod(period_ms);
    if (out_result) *out_result = result;
    return result == TIMERR_NOERROR ? VN_OK : VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_win32_time_end_period(uint32_t period_ms) noexcept {
    return timeEndPeriod(period_ms) == TIMERR_NOERROR ? VN_OK : VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_win32_dwm_flush(int32_t* out_hresult) noexcept {
    const HRESULT hr = DwmFlush();
    if (out_hresult) *out_hresult = static_cast<int32_t>(hr);
    return SUCCEEDED(hr) ? VN_OK : VN_ERR_FAIL;
}

// Auto-reset high-resolution waitable timer. Close with CloseHandle (SafeWaitHandle owns it).
VN_EXPORT int32_t __stdcall vn_win32_create_high_resolution_timer(int64_t* out_handle) noexcept {
    if (!out_handle) return VN_ERR_INVALID_ARG;
    HANDLE timer = CreateWaitableTimerExW(
        nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    *out_handle = w32::FromHandle(timer);
    return timer != nullptr ? VN_OK : VN_ERR_FAIL;
}

// due_100ns negative = relative time.
VN_EXPORT int32_t __stdcall vn_win32_set_waitable_timer(int64_t timer, int64_t due_100ns) noexcept {
    LARGE_INTEGER due{};
    due.QuadPart = due_100ns;
    return w32::Status(SetWaitableTimer(reinterpret_cast<HANDLE>(static_cast<intptr_t>(timer)), &due, 0, nullptr, nullptr, FALSE));
}

VN_EXPORT int32_t __stdcall vn_win32_cancel_waitable_timer(int64_t timer) noexcept {
    return w32::Status(CancelWaitableTimer(reinterpret_cast<HANDLE>(static_cast<intptr_t>(timer))));
}

// Waits on either handle; out_index 0/1 = which signaled, 258 = timeout, -1 = failure.
VN_EXPORT int32_t __stdcall vn_win32_wait_for_any_of_two(
    int64_t first, int64_t second, uint32_t timeout_ms, int32_t* out_result) noexcept {
    if (!out_result) return VN_ERR_INVALID_ARG;
    const HANDLE handles[2] = {
        reinterpret_cast<HANDLE>(static_cast<intptr_t>(first)),
        reinterpret_cast<HANDLE>(static_cast<intptr_t>(second))};
    const DWORD result = WaitForMultipleObjects(2, handles, FALSE, timeout_ms);
    if (result == WAIT_FAILED) {
        *out_result = -1;
        return VN_ERR_FAIL;
    }
    *out_result = static_cast<int32_t>(result);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_screen_dc(int64_t* out_dc) noexcept {
    if (!out_dc) return VN_ERR_INVALID_ARG;
    HDC dc = GetDC(nullptr);
    *out_dc = w32::FromHandle(dc);
    return dc != nullptr ? VN_OK : VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_win32_release_screen_dc(int64_t dc) noexcept {
    return ReleaseDC(nullptr, reinterpret_cast<HDC>(static_cast<intptr_t>(dc))) != 0 ? VN_OK : VN_ERR_FAIL;
}

// Copies a screen rectangle into dest_dc and flushes the GDI batch so the DIB is readable.
VN_EXPORT int32_t __stdcall vn_win32_capture_screen_rect(
    int64_t dest_dc, int32_t width, int32_t height, int64_t source_dc, int32_t src_x, int32_t src_y) noexcept {
    if (!BitBlt(reinterpret_cast<HDC>(static_cast<intptr_t>(dest_dc)), 0, 0, width, height,
                reinterpret_cast<HDC>(static_cast<intptr_t>(source_dc)), src_x, src_y, SRCCOPY))
        return VN_ERR_FAIL;
    GdiFlush();
    return VN_OK;
}

// ---------------------------------------------------------------- desktop layer

VN_EXPORT int32_t __stdcall vn_win32_invalidate_desktop_host_cache() noexcept {
    AcquireSRWLockExclusive(&svc::win32::g_desktop_lock);
    svc::win32::g_desktop_host = nullptr;
    ReleaseSRWLockExclusive(&svc::win32::g_desktop_lock);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_is_window_directly_above_desktop(
    int64_t window, uint8_t* out_is_above, int64_t* out_required_anchor) noexcept {
    if (!out_is_above || !out_required_anchor) return VN_ERR_INVALID_ARG;
    *out_is_above = 0;
    *out_required_anchor = w32::FromHandle(svc::win32::NotTopmost());
    HWND host = svc::win32::GetCachedDesktopHost();
    if (host == nullptr) return VN_OK;
    HWND anchor = GetWindow(host, GW_HWNDPREV);
    if (anchor == w32::ToHwnd(window)) {
        *out_is_above = 1;
        *out_required_anchor = window;
        return VN_OK;
    }
    *out_required_anchor = w32::FromHandle(anchor != nullptr ? anchor : svc::win32::NotTopmost());
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_desktop_layer_insert_after(int64_t window, int64_t* out_insert_after) noexcept {
    if (!out_insert_after) return VN_ERR_INVALID_ARG;
    HWND host = svc::win32::GetCachedDesktopHost();
    // Explorer rebuilds its window tree now and then; NOTOPMOST avoids burying the notch under desktop icons.
    if (host == nullptr) {
        *out_insert_after = w32::FromHandle(svc::win32::NotTopmost());
        return VN_OK;
    }
    HWND anchor = GetWindow(host, GW_HWNDPREV);
    if (anchor != w32::ToHwnd(window)) {
        *out_insert_after = w32::FromHandle(anchor != nullptr ? anchor : svc::win32::NotTopmost());
        return VN_OK;
    }
    // Already directly above the desktop: anchor on the window above it so SetWindowPos keeps that relationship.
    anchor = GetWindow(w32::ToHwnd(window), GW_HWNDPREV);
    *out_insert_after = w32::FromHandle(anchor != nullptr ? anchor : svc::win32::NotTopmost());
    return VN_OK;
}

// ---------------------------------------------------------------- low-level keyboard hook

// Install from a thread that pumps messages. slot identifies the hook for uninstall.
VN_EXPORT int32_t __stdcall vn_win32_keyboard_hook_install(
    svc::KeyboardHookCallback callback, int64_t context, int32_t* out_slot, int32_t* out_last_error) noexcept {
    if (!callback || !out_slot) return VN_ERR_INVALID_ARG;
    *out_slot = -1;
    int32_t result = VN_ERR_FAIL;
    AcquireSRWLockExclusive(&svc::win32::g_hook_lock);
    for (int32_t i = 0; i < svc::win32::kHookSlots; ++i) {
        svc::win32::HookSlot& slot = svc::win32::g_hook_slots[i];
        if (slot.hook.load() != nullptr) continue;
        slot.context.store(context);
        slot.callback.store(callback, std::memory_order_release);
        SetLastError(0);
        HHOOK hook = SetWindowsHookExW(WH_KEYBOARD_LL, svc::win32::kThunks[i], GetModuleHandleW(nullptr), 0);
        if (hook == nullptr) {
            if (out_last_error) *out_last_error = static_cast<int32_t>(GetLastError());
            slot.callback.store(nullptr);
            break;
        }
        slot.hook.store(hook, std::memory_order_release);
        *out_slot = i;
        result = VN_OK;
        break;
    }
    ReleaseSRWLockExclusive(&svc::win32::g_hook_lock);
    return result;
}

VN_EXPORT int32_t __stdcall vn_win32_keyboard_hook_uninstall(int32_t slot_index) noexcept {
    if (slot_index < 0 || slot_index >= svc::win32::kHookSlots) return VN_ERR_INVALID_ARG;
    AcquireSRWLockExclusive(&svc::win32::g_hook_lock);
    svc::win32::HookSlot& slot = svc::win32::g_hook_slots[slot_index];
    HHOOK hook = slot.hook.load();
    slot.callback.store(nullptr, std::memory_order_release);
    int32_t result = VN_OK;
    if (hook != nullptr) result = w32::Status(UnhookWindowsHookEx(hook));
    slot.hook.store(nullptr, std::memory_order_release);
    ReleaseSRWLockExclusive(&svc::win32::g_hook_lock);
    return result;
}

// Out-of-context WinEvent hook for [event_min, event_max]. Install from a thread that pumps messages;
// the callback runs on that thread.
VN_EXPORT int32_t __stdcall vn_win32_win_event_hook_install(
    uint32_t event_min, uint32_t event_max, svc::ForegroundHookCallback callback, int64_t context, int32_t* out_slot) noexcept {
    if (!callback || !out_slot) return VN_ERR_INVALID_ARG;
    *out_slot = -1;
    int32_t result = VN_ERR_FAIL;
    AcquireSRWLockExclusive(&svc::win32::g_event_lock);
    for (int32_t i = 0; i < svc::win32::kEventSlots; ++i) {
        svc::win32::EventSlot& slot = svc::win32::g_event_slots[i];
        if (slot.hook.load() != nullptr) continue;
        slot.context.store(context);
        slot.callback.store(callback, std::memory_order_release);
        HWINEVENTHOOK hook = SetWinEventHook(
            event_min, event_max, nullptr,
            svc::win32::kEventThunks[i], 0, 0, WINEVENT_OUTOFCONTEXT);
        if (hook == nullptr) {
            slot.callback.store(nullptr);
            break;
        }
        slot.hook.store(hook, std::memory_order_release);
        *out_slot = i;
        result = VN_OK;
        break;
    }
    ReleaseSRWLockExclusive(&svc::win32::g_event_lock);
    return result;
}

VN_EXPORT int32_t __stdcall vn_win32_win_event_hook_uninstall(int32_t slot_index) noexcept {
    if (slot_index < 0 || slot_index >= svc::win32::kEventSlots) return VN_ERR_INVALID_ARG;
    AcquireSRWLockExclusive(&svc::win32::g_event_lock);
    svc::win32::EventSlot& slot = svc::win32::g_event_slots[slot_index];
    HWINEVENTHOOK hook = slot.hook.load();
    slot.callback.store(nullptr, std::memory_order_release);
    int32_t result = VN_OK;
    if (hook != nullptr) result = w32::Status(UnhookWinEvent(hook));
    slot.hook.store(nullptr, std::memory_order_release);
    ReleaseSRWLockExclusive(&svc::win32::g_event_lock);
    return result;
}

// ---------------------------------------------------------------- threads, monitors, window enumeration

VN_EXPORT int32_t __stdcall vn_win32_get_current_thread_id(uint32_t* out_thread_id) noexcept {
    if (!out_thread_id) return VN_ERR_INVALID_ARG;
    *out_thread_id = GetCurrentThreadId();
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_attach_thread_input(uint32_t attach_id, uint32_t attach_to, uint8_t attach) noexcept {
    return w32::Status(AttachThreadInput(attach_id, attach_to, attach != 0));
}

VN_EXPORT int32_t __stdcall vn_win32_bring_window_to_top(int64_t hwnd) noexcept {
    return w32::Status(BringWindowToTop(w32::ToHwnd(hwnd)));
}

VN_EXPORT int32_t __stdcall vn_win32_show_window(int64_t hwnd, int32_t command) noexcept {
    return w32::Status(ShowWindow(w32::ToHwnd(hwnd), command));
}

VN_EXPORT int32_t __stdcall vn_win32_monitor_from_point(int32_t x, int32_t y, uint32_t flags, int64_t* out_monitor) noexcept {
    if (!out_monitor) return VN_ERR_INVALID_ARG;
    *out_monitor = w32::FromHandle(MonitorFromPoint(POINT{x, y}, flags));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_monitor_from_window(int64_t hwnd, uint32_t flags, int64_t* out_monitor) noexcept {
    if (!out_monitor) return VN_ERR_INVALID_ARG;
    *out_monitor = w32::FromHandle(MonitorFromWindow(w32::ToHwnd(hwnd), flags));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_monitor_info(int64_t monitor, svc::VnMonitorInfo* info) noexcept {
    if (!info) return VN_ERR_INVALID_ARG;
    MONITORINFO native{};
    native.cbSize = sizeof(native);
    if (!GetMonitorInfoW(reinterpret_cast<HMONITOR>(static_cast<intptr_t>(monitor)), &native)) return VN_ERR_FAIL;
    *info = svc::VnMonitorInfo{
        static_cast<int32_t>(native.cbSize),
        svc::VnRect{native.rcMonitor.left, native.rcMonitor.top, native.rcMonitor.right, native.rcMonitor.bottom},
        svc::VnRect{native.rcWork.left, native.rcWork.top, native.rcWork.right, native.rcWork.bottom},
        native.dwFlags};
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_dpi_for_monitor(
    int64_t monitor, int32_t dpi_type, uint32_t* out_dpi_x, uint32_t* out_dpi_y) noexcept {
    if (!out_dpi_x || !out_dpi_y) return VN_ERR_INVALID_ARG;
    return SUCCEEDED(GetDpiForMonitor(reinterpret_cast<HMONITOR>(static_cast<intptr_t>(monitor)),
                                      static_cast<MONITOR_DPI_TYPE>(dpi_type), out_dpi_x, out_dpi_y))
        ? VN_OK : VN_ERR_FAIL;
}

namespace {
BOOL CALLBACK CollectTopLevelProc(HWND hwnd, LPARAM param) noexcept {
    auto* list = reinterpret_cast<std::pair<int64_t*, std::pair<int32_t, int32_t>>*>(param);
    if (list->second.second >= list->second.first) return FALSE;
    list->first[list->second.second++] = w32::FromHandle(hwnd);
    return TRUE;
}
} // namespace

// Snapshot of top-level windows in z-order (topmost first). Stops silently once the buffer is full.
VN_EXPORT int32_t __stdcall vn_win32_enum_top_level_windows(int64_t* buffer, int32_t capacity, int32_t* out_written) noexcept {
    if (!buffer || capacity <= 0 || !out_written) return VN_ERR_INVALID_ARG;
    std::pair<int64_t*, std::pair<int32_t, int32_t>> list{buffer, {capacity, 0}};
    EnumWindows(CollectTopLevelProc, reinterpret_cast<LPARAM>(&list));
    *out_written = list.second.second;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_window_text_length(int64_t hwnd, int32_t* out_length) noexcept {
    if (!out_length) return VN_ERR_INVALID_ARG;
    *out_length = GetWindowTextLengthW(w32::ToHwnd(hwnd));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_window_text(
    int64_t hwnd, char16_t* buffer, int32_t capacity, int32_t* out_written) noexcept {
    if (!buffer || capacity <= 0 || !out_written) return VN_ERR_INVALID_ARG;
    *out_written = GetWindowTextW(w32::ToHwnd(hwnd), reinterpret_cast<wchar_t*>(buffer), capacity);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_class_name(
    int64_t hwnd, char16_t* buffer, int32_t capacity, int32_t* out_written) noexcept {
    if (!buffer || capacity <= 0 || !out_written) return VN_ERR_INVALID_ARG;
    *out_written = GetClassNameW(w32::ToHwnd(hwnd), reinterpret_cast<wchar_t*>(buffer), capacity);
    return VN_OK;
}

// ---------------------------------------------------------------- GDI DIB surface (backdrop capture target)

VN_EXPORT int32_t __stdcall vn_gdi_destroy_dib_surface(svc::VnDibSurface* surface) noexcept {
    if (!surface) return VN_ERR_INVALID_ARG;
    HDC dc = reinterpret_cast<HDC>(static_cast<intptr_t>(surface->memory_dc));
    if (dc != nullptr && surface->previous_bitmap != 0)
        SelectObject(dc, reinterpret_cast<HGDIOBJ>(static_cast<intptr_t>(surface->previous_bitmap)));
    if (surface->bitmap != 0) DeleteObject(reinterpret_cast<HGDIOBJ>(static_cast<intptr_t>(surface->bitmap)));
    if (dc != nullptr) DeleteDC(dc);
    *surface = {};
    return VN_OK;
}

// Top-down 32-bit BGRA DIB selected into a compatible memory DC with halftone stretching.
VN_EXPORT int32_t __stdcall vn_gdi_create_dib_surface(
    int64_t screen_dc, int32_t width, int32_t height, svc::VnDibSurface* out_surface) noexcept {
    if (!out_surface || width <= 0 || height <= 0) return VN_ERR_INVALID_ARG;
    *out_surface = {};
    HDC reference = reinterpret_cast<HDC>(static_cast<intptr_t>(screen_dc));
    HDC memory_dc = CreateCompatibleDC(reference);
    if (memory_dc == nullptr) return VN_ERR_FAIL;
    SetStretchBltMode(memory_dc, HALFTONE);

    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = width;
    info.bmiHeader.biHeight = -height;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;

    void* bits = nullptr;
    HBITMAP bitmap = CreateDIBSection(reference, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (bitmap == nullptr || bits == nullptr) {
        if (bitmap != nullptr) DeleteObject(bitmap);
        DeleteDC(memory_dc);
        return VN_ERR_FAIL;
    }

    out_surface->memory_dc = w32::FromHandle(memory_dc);
    out_surface->bitmap = w32::FromHandle(bitmap);
    out_surface->previous_bitmap = w32::FromHandle(SelectObject(memory_dc, bitmap));
    out_surface->bits = w32::FromHandle(bits);
    out_surface->width = width;
    out_surface->height = height;
    return VN_OK;
}

// ---------------------------------------------------------------- shell, clipboard, files

VN_EXPORT int32_t __stdcall vn_win32_get_clipboard_sequence_number(uint32_t* out_sequence) noexcept {
    if (!out_sequence) return VN_ERR_INVALID_ARG;
    *out_sequence = GetClipboardSequenceNumber();
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_clipboard_owner(int64_t* out_hwnd) noexcept {
    if (!out_hwnd) return VN_ERR_INVALID_ARG;
    *out_hwnd = w32::FromHandle(GetClipboardOwner());
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_lock_workstation(int32_t* out_last_error) noexcept {
    SetLastError(0);
    if (LockWorkStation()) return VN_OK;
    if (out_last_error) *out_last_error = static_cast<int32_t>(GetLastError());
    return VN_ERR_FAIL;
}

// Rounded-rectangle window region. The window owns the region on success; it is released on failure.
VN_EXPORT int32_t __stdcall vn_win32_set_rounded_window_region(
    int64_t hwnd, int32_t left, int32_t top, int32_t right, int32_t bottom,
    int32_t ellipse_width, int32_t ellipse_height) noexcept {
    HRGN region = CreateRoundRectRgn(left, top, right, bottom, ellipse_width, ellipse_height);
    if (region == nullptr) return VN_ERR_FAIL;
    if (SetWindowRgn(w32::ToHwnd(hwnd), region, TRUE) == 0) {
        DeleteObject(region);
        return VN_ERR_FAIL;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_create_hard_link(
    const char16_t* destination, int32_t destination_length,
    const char16_t* existing, int32_t existing_length, int32_t* out_last_error) noexcept {
    if (!destination || !existing || destination_length <= 0 || existing_length <= 0 ||
        destination_length >= 32768 || existing_length >= 32768)
        return VN_ERR_INVALID_ARG;
    std::wstring dest(reinterpret_cast<const wchar_t*>(destination), static_cast<size_t>(destination_length));
    std::wstring src(reinterpret_cast<const wchar_t*>(existing), static_cast<size_t>(existing_length));
    SetLastError(0);
    if (CreateHardLinkW(dest.c_str(), src.c_str(), nullptr)) return VN_OK;
    if (out_last_error) *out_last_error = static_cast<int32_t>(GetLastError());
    return VN_ERR_FAIL;
}
// ---------------------------------------------------------------- selection helpers and input

VN_EXPORT int32_t __stdcall vn_win32_get_gui_thread_focus(uint32_t thread_id, int64_t* out_focus) noexcept {
    if (!out_focus) return VN_ERR_INVALID_ARG;
    *out_focus = 0;
    GUITHREADINFO info{};
    info.cbSize = sizeof(info);
    if (thread_id == 0 || !GetGUIThreadInfo(thread_id, &info)) return VN_ERR_FAIL;
    *out_focus = w32::FromHandle(info.hwndFocus);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_is_child(int64_t parent, int64_t child, uint8_t* out_is_child) noexcept {
    if (!out_is_child) return VN_ERR_INVALID_ARG;
    *out_is_child = IsChild(w32::ToHwnd(parent), w32::ToHwnd(child)) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_ancestor(int64_t hwnd, uint32_t flags, int64_t* out_ancestor) noexcept {
    if (!out_ancestor) return VN_ERR_INVALID_ARG;
    *out_ancestor = w32::FromHandle(GetAncestor(w32::ToHwnd(hwnd), flags));
    return VN_OK;
}

namespace {
BOOL CALLBACK CollectChildProc(HWND hwnd, LPARAM param) noexcept {
    auto* list = reinterpret_cast<std::pair<int64_t*, std::pair<int32_t, int32_t>>*>(param);
    if (list->second.second >= list->second.first) return FALSE;
    list->first[list->second.second++] = w32::FromHandle(hwnd);
    return TRUE;
}
} // namespace

// Snapshot of the child windows of parent (z-order). Stops silently once the buffer is full.
VN_EXPORT int32_t __stdcall vn_win32_enum_child_windows(
    int64_t parent, int64_t* buffer, int32_t capacity, int32_t* out_written) noexcept {
    if (!buffer || capacity <= 0 || !out_written) return VN_ERR_INVALID_ARG;
    std::pair<int64_t*, std::pair<int32_t, int32_t>> list{buffer, {capacity, 0}};
    EnumChildWindows(w32::ToHwnd(parent), CollectChildProc, reinterpret_cast<LPARAM>(&list));
    *out_written = list.second.second;
    return VN_OK;
}

// Types each UTF-16 code unit as a KEYEVENTF_UNICODE down/up pair. out_sent counts accepted INPUT records.
VN_EXPORT int32_t __stdcall vn_win32_send_unicode_text(
    const char16_t* text, int32_t length, int32_t* out_sent, int32_t* out_expected) noexcept {
    if (!text || length <= 0 || length > 65536 || !out_sent || !out_expected) return VN_ERR_INVALID_ARG;
    std::vector<INPUT> inputs;
    inputs.reserve(static_cast<size_t>(length) * 2);
    for (int32_t i = 0; i < length; ++i) {
        INPUT down{};
        down.type = INPUT_KEYBOARD;
        down.ki.wScan = text[i];
        down.ki.dwFlags = KEYEVENTF_UNICODE;
        INPUT up = down;
        up.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        inputs.push_back(down);
        inputs.push_back(up);
    }
    *out_expected = static_cast<int32_t>(inputs.size());
    *out_sent = static_cast<int32_t>(SendInput(static_cast<UINT>(inputs.size()), inputs.data(), sizeof(INPUT)));
    return *out_sent == *out_expected ? VN_OK : VN_ERR_FAIL;
}
// ---------------------------------------------------------------- kernel32 / psapi metrics

VN_EXPORT int32_t __stdcall vn_win32_get_current_process(int64_t* out_handle) noexcept {
    if (!out_handle) return VN_ERR_INVALID_ARG;
    *out_handle = w32::FromHandle(GetCurrentProcess());
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_system_times(
    svc::VnFileTime* idle, svc::VnFileTime* kernel, svc::VnFileTime* user) noexcept {
    if (!idle || !kernel || !user) return VN_ERR_INVALID_ARG;
    FILETIME i{}, k{}, u{};
    if (!GetSystemTimes(&i, &k, &u)) return VN_ERR_FAIL;
    *idle = svc::VnFileTime{i.dwLowDateTime, i.dwHighDateTime};
    *kernel = svc::VnFileTime{k.dwLowDateTime, k.dwHighDateTime};
    *user = svc::VnFileTime{u.dwLowDateTime, u.dwHighDateTime};
    return VN_OK;
}

// CPU time consumed by the calling thread, in 100 ns units.
VN_EXPORT int32_t __stdcall vn_win32_get_current_thread_times(svc::VnFileTime* kernel, svc::VnFileTime* user) noexcept {
    if (!kernel || !user) return VN_ERR_INVALID_ARG;
    FILETIME created{}, exited{}, k{}, u{};
    if (!GetThreadTimes(GetCurrentThread(), &created, &exited, &k, &u)) return VN_ERR_FAIL;
    *kernel = svc::VnFileTime{k.dwLowDateTime, k.dwHighDateTime};
    *user = svc::VnFileTime{u.dwLowDateTime, u.dwHighDateTime};
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_process_times(
    int64_t process, svc::VnFileTime* kernel, svc::VnFileTime* user) noexcept {
    if (!kernel || !user) return VN_ERR_INVALID_ARG;
    FILETIME created{}, exited{}, k{}, u{};
    if (!GetProcessTimes(reinterpret_cast<HANDLE>(static_cast<intptr_t>(process)), &created, &exited, &k, &u))
        return VN_ERR_FAIL;
    *kernel = svc::VnFileTime{k.dwLowDateTime, k.dwHighDateTime};
    *user = svc::VnFileTime{u.dwLowDateTime, u.dwHighDateTime};
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_process_handle_count(int64_t process, uint32_t* out_count) noexcept {
    if (!out_count) return VN_ERR_INVALID_ARG;
    DWORD count = 0;
    if (!GetProcessHandleCount(reinterpret_cast<HANDLE>(static_cast<intptr_t>(process)), &count)) return VN_ERR_FAIL;
    *out_count = count;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_get_process_memory_info(
    int64_t process, svc::VnProcessMemoryCounters* out_counters) noexcept {
    if (!out_counters) return VN_ERR_INVALID_ARG;
    PROCESS_MEMORY_COUNTERS_EX counters{};
    counters.cb = sizeof(counters);
    if (!GetProcessMemoryInfo(reinterpret_cast<HANDLE>(static_cast<intptr_t>(process)),
                              reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters), sizeof(counters)))
        return VN_ERR_FAIL;
    *out_counters = svc::VnProcessMemoryCounters{
        counters.cb, counters.PageFaultCount, counters.PeakWorkingSetSize, counters.WorkingSetSize,
        counters.QuotaPeakPagedPoolUsage, counters.QuotaPagedPoolUsage,
        counters.QuotaPeakNonPagedPoolUsage, counters.QuotaNonPagedPoolUsage,
        counters.PagefileUsage, counters.PeakPagefileUsage, counters.PrivateUsage};
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_win32_global_memory_status(svc::VnMemoryStatus* out_status) noexcept {
    if (!out_status) return VN_ERR_INVALID_ARG;
    MEMORYSTATUSEX status{};
    status.dwLength = sizeof(status);
    if (!GlobalMemoryStatusEx(&status)) return VN_ERR_FAIL;
    *out_status = svc::VnMemoryStatus{
        status.dwLength, status.dwMemoryLoad, status.ullTotalPhys, status.ullAvailPhys,
        status.ullTotalPageFile, status.ullAvailPageFile, status.ullTotalVirtual,
        status.ullAvailVirtual, status.ullAvailExtendedVirtual};
    return VN_OK;
}

} // extern "C"
