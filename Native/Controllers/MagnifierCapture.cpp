#include "../vnotch_common.h"
#include "../vnotch_handle_registry.h"
#include <magnification.h>
#include <algorithm>
#include <cstdio>
#include <cstdint>
#include <memory>
#include <vector>

// Magnification API capture of the desktop for the Liquid Glass backdrop. The native object owns the
// host window, the Magnifier control, the pump thread, the scaling callback and the completed frame.
// Magnification.dll is bound at run time so the DLL still loads where the API is missing.

namespace vnotch::controllers::magcap {

constexpr uint32_t kInitTimeoutMs = 2000;
constexpr uint32_t kJoinTimeoutMs = 500;
constexpr int64_t kFrameFreshMs = 500;
constexpr int32_t kMaxFrameRows = 16384;
constexpr int32_t kMaxStride = 1 << 18;
constexpr DWORD kFilterModeExclude = 0;
constexpr wchar_t kHostClass[] = L"VNotchMagHost";

enum InitStage : int32_t {
    kInitOk = 0,
    kInitMagInitializeFailed = 1,
    kInitHostCreateFailed = 2,
    kInitControlCreateFailed = 3,
    kInitCallbackUnsupported = 4,
};

struct MagApi {
    HMODULE module = nullptr;
    BOOL(WINAPI* initialize)() = nullptr;
    BOOL(WINAPI* uninitialize)() = nullptr;
    BOOL(WINAPI* set_window_source)(HWND, RECT) = nullptr;
    BOOL(WINAPI* set_window_filter_list)(HWND, DWORD, int, HWND*) = nullptr;
    BOOL(WINAPI* set_image_scaling_callback)(HWND, MagImageScalingCallback) = nullptr;
    bool Complete() const noexcept {
        return module && initialize && uninitialize && set_window_source && set_window_filter_list && set_image_scaling_callback;
    }
};

INIT_ONCE g_api_once = INIT_ONCE_STATIC_INIT;
MagApi g_api;

BOOL CALLBACK LoadApi(PINIT_ONCE, PVOID, PVOID*) noexcept {
    HMODULE module = LoadLibraryExW(L"Magnification.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) return TRUE;
    g_api.module = module;
    g_api.initialize = reinterpret_cast<decltype(g_api.initialize)>(GetProcAddress(module, "MagInitialize"));
    g_api.uninitialize = reinterpret_cast<decltype(g_api.uninitialize)>(GetProcAddress(module, "MagUninitialize"));
    g_api.set_window_source = reinterpret_cast<decltype(g_api.set_window_source)>(GetProcAddress(module, "MagSetWindowSource"));
    g_api.set_window_filter_list = reinterpret_cast<decltype(g_api.set_window_filter_list)>(GetProcAddress(module, "MagSetWindowFilterList"));
    g_api.set_image_scaling_callback = reinterpret_cast<decltype(g_api.set_image_scaling_callback)>(GetProcAddress(module, "MagSetImageScalingCallback"));
    return TRUE;
}

const MagApi& Api() noexcept {
    InitOnceExecuteOnce(&g_api_once, LoadApi, nullptr, nullptr);
    return g_api;
}

struct CaptureRequest {
    int32_t x = 0, y = 0, width = 0, height = 0;
    constexpr bool operator==(const CaptureRequest&) const noexcept = default;
};

struct VirtualScreenBounds {
    int left;
    int top;
    int right;
    int bottom;
};

VirtualScreenBounds QueryVirtualScreenBounds() noexcept {
    const int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
    const int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
    return { vx, vy, vx + GetSystemMetrics(SM_CXVIRTUALSCREEN), vy + GetSystemMetrics(SM_CYVIRTUALSCREEN) };
}

void DrainThreadMessages() noexcept {
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
}

struct ScopedLock {
    SRWLOCK* lock;
    explicit ScopedLock(SRWLOCK* l) noexcept : lock(l) { AcquireSRWLockExclusive(lock); }
    ScopedLock(const ScopedLock&) = delete;
    ScopedLock& operator=(const ScopedLock&) = delete;
    ~ScopedLock() noexcept { ReleaseSRWLockExclusive(lock); }
};

bool IsCompleteFrame(int32_t requested_w, int32_t requested_h, int32_t received_w, int32_t received_h,
                     int32_t received_stride, int32_t buffer_length) noexcept {
    if (requested_w <= 0 || requested_h <= 0 || received_w < requested_w || received_h < requested_h) return false;
    const int64_t row_bytes = static_cast<int64_t>(requested_w) * 4;
    if (received_stride < row_bytes) return false;
    const int64_t required = (static_cast<int64_t>(requested_h) - 1) * received_stride + row_bytes;
    return required <= buffer_length;
}

class Source;
thread_local Source* t_current = nullptr;

class Source {
public:
    Source() noexcept {
        init_done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        request = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        frame_received = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        unfiltered_received = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    }
    Source(const Source&) = delete;
    Source& operator=(const Source&) = delete;
    ~Source() {
        for (HANDLE h : {init_done, request, frame_received, unfiltered_received})
            if (h) CloseHandle(h);
        if (thread) CloseHandle(thread);
    }

    // ---- state shared with the pump thread ----
    HANDLE thread = nullptr;
    SRWLOCK stop_lock = SRWLOCK_INIT;
    HANDLE init_done = nullptr;
    HANDLE request = nullptr;
    HANDLE frame_received = nullptr;
    HANDLE unfiltered_received = nullptr;
    std::atomic<bool> running{false};
    std::atomic<bool> ready{false};
    std::atomic<bool> capture_enabled{true};
    std::atomic<int32_t> init_stage{kInitOk};
    std::atomic<int64_t> source_fail_count{0};
    std::atomic<int64_t> callback_enter_count{0};
    std::atomic<bool> teardown_queued{false};

    HWND host = nullptr;
    HWND mag = nullptr;
    bool mag_initialized = false;

    SRWLOCK filter_lock = SRWLOCK_INIT;
    std::vector<HWND> filters;
    std::atomic<bool> filter_dirty{true};
    std::atomic<int32_t> filter_version{0};
    int32_t applied_filter_version = 0;

    CaptureRequest active;
    CaptureRequest requested;

    SRWLOCK frame_lock = SRWLOCK_INIT;
    std::vector<uint8_t> completed;
    int32_t completed_width = 0, completed_height = 0, completed_x = 0, completed_y = 0;
    bool has_completed = false;
    bool has_unfiltered = false;
    int64_t completed_tick = 0;
    int32_t completed_filter_version = 0;
    uint64_t frame_counter = 0;

    // ---- filters ----
    // The filter helpers allocate and may throw std::bad_alloc. Exports and the pump thread catch at their boundary.
    void AddFilter(HWND hwnd) {
        if (!hwnd) return;
        {
            ScopedLock guard(&filter_lock);
            if (std::find(filters.begin(), filters.end(), hwnd) != filters.end()) return;
            filters.push_back(hwnd);
            filter_dirty = true;
            filter_version.fetch_add(1);
        }
        SetEvent(request);
    }

    void RemoveFilter(HWND hwnd) noexcept {
        if (!hwnd) return;
        {
            ScopedLock guard(&filter_lock);
            const auto it = std::find(filters.begin(), filters.end(), hwnd);
            if (it == filters.end()) return;
            filters.erase(it);
            filter_dirty = true;
            filter_version.fetch_add(1);
        }
        SetEvent(request);
    }

    void ApplyFiltersIfDirtyOrThrow() {
        if (!filter_dirty.load() || !mag) return;
        std::vector<HWND> list;
        int32_t version = 0;
        {
            ScopedLock guard(&filter_lock);
            list.reserve(filters.size() + 1);
            for (HWND h : filters) {
                if (!h || !IsWindow(h) || std::find(list.begin(), list.end(), h) != list.end()) continue;
                // WDA_EXCLUDEFROMCAPTURE already hides the window; listing it makes DWM paint black regions.
                DWORD affinity = 0;
                if (GetWindowDisplayAffinity(h, &affinity) && affinity == WDA_EXCLUDEFROMCAPTURE) continue;
                list.push_back(h);
            }
            filter_dirty = false;
            version = filter_version.load();
        }
        if (host && std::find(list.begin(), list.end(), host) == list.end()) list.push_back(host);

        if (Api().set_window_filter_list(mag, kFilterModeExclude, static_cast<int>(list.size()), list.data())) {
            applied_filter_version = version;
        } else {
            // Keep the last texture until exclusion works; capturing the glass itself causes recursive feedback.
            applied_filter_version = -1;
            filter_dirty = true;
        }
    }

    // Pump thread entry point: an allocation failure keeps the last texture and retries on the next request.
    void ApplyFiltersIfDirty() noexcept {
        try {
            ApplyFiltersIfDirtyOrThrow();
        } catch (...) {
            applied_filter_version = -1;
            filter_dirty = true;
        }
    }

    // ---- frames ----
    void ClearFrameLocked() noexcept {
        has_completed = false;
        has_unfiltered = false;
        completed.clear();
        completed.shrink_to_fit();
    }

    bool HasValidFrameBufferLocked() const noexcept {
        return completed_width > 0 && completed_height > 0 &&
               static_cast<int64_t>(completed_width) * completed_height * 4 <= static_cast<int64_t>(completed.size());
    }

    bool IsUninitializedZeroCrop(int32_t x, int32_t y, int32_t width, int32_t height) const noexcept {
        if (!HasValidFrameBufferLocked()) return true;
        // Only all-zero BGRA (uninitialized DWM output) is rejected; opaque black has A=255.
        constexpr int kSamples = 6;
        const auto* pixels = reinterpret_cast<const uint32_t*>(completed.data());
        for (int row = 0; row < kSamples; ++row) {
            const int sy = std::clamp(y - completed_y + (height - 1) * row / (kSamples - 1), 0, completed_height - 1);
            const uint32_t* row_pixels = pixels + static_cast<size_t>(sy) * completed_width;
            for (int col = 0; col < kSamples; ++col) {
                const int sx = std::clamp(x - completed_x + (width - 1) * col / (kSamples - 1), 0, completed_width - 1);
                if (row_pixels[sx] != 0u) return false;
            }
        }
        return true;
    }

    bool HasUsableFrameLocked(int32_t x, int32_t y, int32_t width, int32_t height) const noexcept {
        const VirtualScreenBounds vs = QueryVirtualScreenBounds();
        if (std::max(x, vs.left) < completed_x || std::max(y, vs.top) < completed_y ||
            std::min(x + width, vs.right) > completed_x + completed_width ||
            std::min(y + height, vs.bottom) > completed_y + completed_height) return false;
        return has_completed && completed_filter_version == filter_version.load() &&
               static_cast<int64_t>(GetTickCount64()) - completed_tick <= kFrameFreshMs &&
               !IsUninitializedZeroCrop(x, y, width, height);
    }

    // Copies a crop of the desktop frame, extending edge pixels where the margin leaves the virtual desktop.
    bool CopyCropLocked(int32_t x, int32_t y, int32_t width, int32_t height, uint32_t* destination) const noexcept {
        if (!destination || width <= 0 || height <= 0 || !HasValidFrameBufferLocked())
            return false;
        const auto* src = reinterpret_cast<const uint32_t*>(completed.data());
        const int64_t offset_x = static_cast<int64_t>(x) - completed_x;
        const int64_t offset_y = static_cast<int64_t>(y) - completed_y;
        const int32_t left_pad = static_cast<int32_t>(std::clamp<int64_t>(-offset_x, 0, width));
        const int64_t src_x = std::max<int64_t>(offset_x, 0);
        const int64_t avail_w = std::max<int64_t>(0, static_cast<int64_t>(completed_width) - src_x);
        const int32_t mid_count = static_cast<int32_t>(std::min<int64_t>(static_cast<int64_t>(width) - left_pad, avail_w));
        const int32_t right_pad = width - left_pad - mid_count;

        for (int32_t row = 0; row < height; ++row) {
            const int32_t sy = static_cast<int32_t>(std::clamp<int64_t>(offset_y + row, 0, completed_height - 1));
            uint32_t* dst_row = destination + static_cast<size_t>(row) * width;
            const uint32_t* src_row = src + static_cast<size_t>(sy) * completed_width;
            if (left_pad > 0) {
                std::fill_n(dst_row, left_pad, src_row[0]);
            }
            if (mid_count > 0) {
                std::memcpy(dst_row + left_pad, src_row + src_x, static_cast<size_t>(mid_count) * sizeof(uint32_t));
            }
            if (right_pad > 0) {
                std::fill_n(dst_row + left_pad + mid_count, right_pad, src_row[completed_width - 1]);
            }
        }
        return true;
    }

    bool OnScalingCallback(const void* srcdata, const MAGIMAGEHEADER& header) noexcept {
        try {
            callback_enter_count.fetch_add(1);
            if (!srcdata || !capture_enabled.load()) return false;
            const int32_t w = static_cast<int32_t>(header.width);
            const int32_t rows = static_cast<int32_t>(header.height);
            const int32_t src_stride = static_cast<int32_t>(header.stride);
            if (w <= 0 || rows <= 0 || src_stride <= 0 || rows > kMaxFrameRows || src_stride > kMaxStride) return false;
            const uint64_t available = header.cbSize;
            if (header.offset > available) return false;
            const int32_t usable = static_cast<int32_t>(std::min<uint64_t>(INT32_MAX, available - header.offset));
            if (!IsCompleteFrame(w, rows, w, rows, src_stride, usable) || w < active.width || rows < active.height) return false;

            const int32_t dst_stride = w * 4;
            const size_t needed = static_cast<size_t>(dst_stride) * rows;
            const auto* src = static_cast<const uint8_t*>(srcdata) + header.offset;

            {
                ScopedLock guard(&frame_lock);
                if (!capture_enabled.load()) return false;
                if (completed.size() < needed) completed.resize(needed);
                if (src_stride == dst_stride) {
                    std::memcpy(completed.data(), src, needed);
                } else {
                    for (int32_t row = 0; row < rows; ++row)
                        std::memcpy(completed.data() + static_cast<size_t>(row) * dst_stride,
                                    src + static_cast<size_t>(row) * src_stride, static_cast<size_t>(dst_stride));
                }
                completed_width = w;
                completed_height = rows;
                completed_x = active.x;
                completed_y = active.y;
                completed_filter_version = applied_filter_version;
                has_completed = true;
                completed_tick = static_cast<int64_t>(GetTickCount64());
                ++frame_counter;
                if (applied_filter_version == 0) {
                    has_unfiltered = true;
                    SetEvent(unfiltered_received);
                }
            }
            SetEvent(frame_received);
            return true;
        } catch (...) {
            return false;
        }
    }

    // ---- pump thread ----
    void Cleanup() noexcept {
        ready = false;
        {
            ScopedLock guard(&frame_lock);
            ClearFrameLocked();
        }
        if (mag) { DestroyWindow(mag); mag = nullptr; }
        if (host) { DestroyWindow(host); host = nullptr; }
        if (mag_initialized) {
            Api().uninitialize();
            mag_initialized = false;
        }
    }

    void Fail(int32_t stage) noexcept {
        init_stage = stage;
        Cleanup();
        SetEvent(init_done);
    }

    static BOOL CALLBACK ScalingTrampoline(HWND, void* srcdata, MAGIMAGEHEADER srcheader, void*, MAGIMAGEHEADER,
                                           RECT, RECT, HRGN) noexcept;

    void HandleCaptureDisabled(bool& host_shown) noexcept {
        if (host_shown) {
            ShowWindow(host, SW_HIDE);
            host_shown = false;
        }
        // Release desktop-sized buffers while no consumer is active.
        ScopedLock guard(&frame_lock);
        ClearFrameLocked();
        ResetEvent(frame_received);
        ResetEvent(unfiltered_received);
    }

    void HandleCaptureRequest(const MagApi& api, bool& host_shown, int& mag_w, int& mag_h) noexcept {
        if (!host_shown) {
            ShowWindow(host, SW_SHOWNA);
            host_shown = true;
        }
        ApplyFiltersIfDirty();

        CaptureRequest req;
        {
            ScopedLock guard(&frame_lock);
            req = requested;
        }
        if (req.width <= 0 || req.height <= 0) return;

        if (!(active == req)) {
            ScopedLock guard(&frame_lock);
            has_completed = false;
        }
        active = req;
        SetWindowPos(host, nullptr, req.x, req.y, req.width, req.height, SWP_NOACTIVATE | SWP_NOZORDER);
        SetWindowPos(mag, nullptr, 0, 0, req.width, req.height, SWP_NOACTIVATE | SWP_NOZORDER);
        if (req.width != mag_w || req.height != mag_h) {
            mag_w = req.width;
            mag_h = req.height;
            MoveWindow(host, req.x, req.y, mag_w, mag_h, FALSE);
            MoveWindow(mag, 0, 0, mag_w, mag_h, FALSE);
        }
        const RECT rect{req.x, req.y, req.x + req.width, req.y + req.height};
        if (!api.set_window_source(mag, rect)) {
            source_fail_count.fetch_add(1);
        } else {
            InvalidateRect(mag, nullptr, FALSE);
            UpdateWindow(mag);
        }
        if (filter_dirty.load()) {
            SetEvent(request);
        }
    }

    void Pump() noexcept {
        (void)CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        struct ComUninitGuard {
            ~ComUninitGuard() noexcept { CoUninitialize(); }
        } com_guard;
        t_current = this;
        const MagApi& api = Api();
        if (!api.Complete() || !api.initialize()) {
            Fail(kInitMagInitializeFailed);
            return;
        }
        mag_initialized = true;

        HINSTANCE instance = GetModuleHandleW(nullptr);
        WNDCLASSW wc{};
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = instance;
        wc.lpszClassName = kHostClass;
        RegisterClassW(&wc); // harmless when already registered

        const VirtualScreenBounds vs = QueryVirtualScreenBounds();
        int mag_w = 1;
        int mag_h = 1;

        host = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW, kHostClass, L"VNotchMagHost", WS_POPUP,
                               vs.left, vs.top, mag_w, mag_h, nullptr, nullptr, instance, nullptr);
        if (!host) { Fail(kInitHostCreateFailed); return; }

        // Alpha 1 is practically invisible yet keeps DWM composing at the monitor refresh rate.
        SetLayeredWindowAttributes(host, 0, 1, LWA_ALPHA);
        ShowWindow(host, SW_SHOWNA);

        mag = CreateWindowExW(0, L"Magnifier", L"VNotchMag", WS_CHILD | WS_VISIBLE, 0, 0, mag_w, mag_h, host, nullptr, instance, nullptr);
        if (!mag) { Fail(kInitControlCreateFailed); return; }

        ApplyFiltersIfDirty();

        if (!api.set_image_scaling_callback(mag, reinterpret_cast<MagImageScalingCallback>(&Source::ScalingTrampoline))) {
            Fail(kInitCallbackUnsupported);
            return;
        }

        ready = true;
        SetEvent(init_done);

        bool host_shown = true;
        DrainThreadMessages();
        while (running.load()) {
            const DWORD wait_res = MsgWaitForMultipleObjects(1, &request, FALSE, INFINITE, QS_ALLINPUT);
            if (!running.load()) break;
            if (wait_res == WAIT_OBJECT_0) {
                if (!capture_enabled.load()) {
                    HandleCaptureDisabled(host_shown);
                } else {
                    HandleCaptureRequest(api, host_shown, mag_w, mag_h);
                }
            }
            DrainThreadMessages();
        }
        Cleanup();
    }

    static DWORD WINAPI PumpMain(LPVOID param) noexcept {
        static_cast<Source*>(param)->Pump();
        return 0;
    }

    // May throw std::bad_alloc before the pump thread exists; vn_magcap_initialize reports that as VN_ERR_FAIL.
    bool Start(HWND exclude) {
        if (!init_done || !request || !frame_received || !unfiltered_received) return false;
        if (running.load() || thread != nullptr) {
            if (!Stop()) return false;
        }
        if (exclude) AddFilterNoSignal(exclude);
        {
            ScopedLock stop_guard(&stop_lock);
            if (running.load() || thread != nullptr) return false;
            ResetEvent(init_done);
            init_stage = kInitOk;
            filter_dirty = true;
            running.store(true, std::memory_order_release);
            thread = CreateThread(nullptr, 0, PumpMain, this, 0, nullptr);
            if (!thread) {
                running.store(false, std::memory_order_release);
                return false;
            }
            SetThreadPriority(thread, THREAD_PRIORITY_ABOVE_NORMAL);
        }
        WaitForSingleObject(init_done, kInitTimeoutMs);
        if (!ready.load()) { Stop(); return false; }
        return true;
    }

    void AddFilterNoSignal(HWND hwnd) {
        ScopedLock guard(&filter_lock);
        if (std::find(filters.begin(), filters.end(), hwnd) == filters.end()) filters.push_back(hwnd);
    }

    // True when the pump thread is gone and every native handle can be released.
    bool Stop() noexcept {
        ScopedLock stop_guard(&stop_lock);
        running.store(false, std::memory_order_release);
        ready.store(false, std::memory_order_release);
        SetEvent(request);
        SetEvent(frame_received);
        if (thread) {
            if (const DWORD tid = GetThreadId(thread))
                PostThreadMessageW(tid, WM_NULL, 0, 0);
            if (WaitForSingleObject(thread, kJoinTimeoutMs) != WAIT_OBJECT_0) return false;
            CloseHandle(thread);
            thread = nullptr;
        }
        ScopedLock guard(&frame_lock);
        ClearFrameLocked();
        return true;
    }

    HANDLE DuplicateThreadForCleanup() noexcept {
        ScopedLock guard(&stop_lock);
        HANDLE duplicate = nullptr;
        if (thread) DuplicateHandle(GetCurrentProcess(), thread, GetCurrentProcess(), &duplicate, SYNCHRONIZE, FALSE, 0);
        return duplicate;
    }
};

BOOL CALLBACK Source::ScalingTrampoline(HWND, void* srcdata, MAGIMAGEHEADER srcheader, void*, MAGIMAGEHEADER, RECT, RECT, HRGN) noexcept {
    Source* source = t_current;
    return (source != nullptr && source->OnScalingCallback(srcdata, srcheader)) ? TRUE : FALSE;
}

using SourceLease = vnotch::HandleRegistry<Source>::Lease;

vnotch::HandleRegistry<Source> g_sources;
SRWLOCK g_epoch_lock = SRWLOCK_INIT;
std::vector<uint32_t> g_slot_epochs;

int32_t Register(std::unique_ptr<Source> source) {
    const int64_t raw = g_sources.Add(std::move(source));
    const uint32_t slot = static_cast<uint32_t>(static_cast<uint64_t>(raw) & 0xFFFFFFFFull);
    const uint32_t gen = static_cast<uint32_t>(static_cast<uint64_t>(raw) >> 32);
    if (slot == 0u || slot > 0xFFFFu || gen == 0u) {
        g_sources.Remove(raw);
        return 0;
    }
    const uint32_t gen15 = ((gen - 1u) % 0x7FFFu) + 1u;
    {
        ScopedLock guard(&g_epoch_lock);
        if (g_slot_epochs.size() < slot) {
            g_slot_epochs.resize(slot, 0u);
        }
        g_slot_epochs[slot - 1u] = gen - gen15;
    }
    return static_cast<int32_t>((gen15 << 16) | slot);
}

int64_t UnpackHandle(int32_t handle) noexcept {
    if (handle <= 0) return 0;
    const uint32_t u = static_cast<uint32_t>(handle);
    const uint32_t slot = u & 0xFFFFu;
    const uint32_t gen15 = (u >> 16) & 0x7FFFu;
    if (slot == 0u || gen15 == 0u) return 0;
    uint32_t epoch = 0u;
    {
        ScopedLock guard(&g_epoch_lock);
        if (slot > g_slot_epochs.size()) return 0;
        epoch = g_slot_epochs[slot - 1u];
    }
    const uint32_t gen = epoch + gen15;
    return (static_cast<int64_t>(gen) << 32) | static_cast<int64_t>(slot);
}

SourceLease Find(int32_t handle) noexcept {
    const int64_t raw = UnpackHandle(handle);
    return raw > 0 ? g_sources.Acquire(raw) : SourceLease();
}

void Remove(int32_t handle) noexcept {
    const int64_t raw = UnpackHandle(handle);
    if (raw > 0) g_sources.Remove(raw);
}

HWND ToHwnd(int64_t value) noexcept {
    return reinterpret_cast<HWND>(static_cast<intptr_t>(value));
}

} // namespace vnotch::controllers::magcap

namespace mc = vnotch::controllers::magcap;

extern "C" {

VN_EXPORT int32_t __stdcall vn_magcap_create(int32_t* out_handle) noexcept {
    if (!out_handle) return VN_ERR_INVALID_ARG;
    *out_handle = 0;
    try {
        const int32_t handle = mc::Register(std::make_unique<mc::Source>());
        if (handle <= 0) return VN_ERR_FAIL;
        *out_handle = handle;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

// Starts the pump thread and waits for the Magnifier control. out_stage: 0 ok, else the failing init stage.
VN_EXPORT int32_t __stdcall vn_magcap_initialize(int32_t handle, int64_t exclude_hwnd, uint8_t* out_ready, int32_t* out_stage) noexcept {
    auto source = mc::Find(handle);
    if (!source || !out_ready || !out_stage) return VN_ERR_INVALID_ARG;
    *out_ready = 0;
    *out_stage = source->init_stage.load();
    try {
        *out_ready = source->Start(mc::ToHwnd(exclude_hwnd)) ? 1 : 0;
    } catch (...) {
        *out_ready = 0;
        return VN_ERR_FAIL;
    }
    *out_stage = source->init_stage.load();
    return VN_OK;
}

// Stops the pump thread. out_exited = 0 means the thread is still alive and the object must stay allocated.
VN_EXPORT int32_t __stdcall vn_magcap_stop(int32_t handle, uint8_t* out_exited) noexcept {
    auto source = mc::Find(handle);
    if (!source || !out_exited) return VN_ERR_INVALID_ARG;
    *out_exited = source->Stop() ? 1 : 0;
    return VN_OK;
}

// Releases the object after a successful stop. A source whose thread would not exit is deliberately kept.
VN_EXPORT int32_t __stdcall vn_magcap_destroy(int32_t handle, uint8_t* out_released) noexcept {
    if (!out_released) return VN_ERR_INVALID_ARG;
    *out_released = 0;
    auto source = mc::Find(handle);
    if (!source) {
        *out_released = 1;
        return VN_OK;
    }
    if (!source->Stop()) {
        // The pump may be inside DWM when the bounded UI-side stop expires.
        // Keep a lease until it exits, then retire it without leaking the buffers.
        if (!source->teardown_queued.exchange(true)) {
            struct Reap {
                int32_t handle;
                mc::SourceLease source;
                HANDLE waitThread;
                Reap(int32_t h, mc::SourceLease&& s, HANDLE w) noexcept
                    : handle(h), source(std::move(s)), waitThread(w) {}
                ~Reap() { if (waitThread) CloseHandle(waitThread); }
            };
            HANDLE waitThread = source->DuplicateThreadForCleanup();
            if (!waitThread) { source->teardown_queued = false; return VN_OK; }
            try {
                auto reap = std::make_unique<Reap>(handle, std::move(source), waitThread);
                if (QueueUserWorkItem([](void* parameter) -> DWORD {
                    std::unique_ptr<Reap> item(static_cast<Reap*>(parameter));
                    WaitForSingleObject(item->waitThread, INFINITE);
                    item->source.Reset();
                    uint8_t released = 0;
                    vn_magcap_destroy(item->handle, &released);
                    return 0;
                }, reap.get(), WT_EXECUTELONGFUNCTION)) {
                    (void)reap.release();
                    return VN_OK;
                }
                reap->source->teardown_queued = false;
            } catch (...) {
                if (source) source->teardown_queued = false;
                CloseHandle(waitThread);
            }
        }
        return VN_OK;
    }
    source.Reset();
    mc::Remove(handle);
    *out_released = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_magcap_set_capture_enabled(int32_t handle, uint8_t enabled) noexcept {
    auto source = mc::Find(handle);
    if (!source) return VN_ERR_INVALID_ARG;
    source->capture_enabled = enabled != 0;
    SetEvent(source->request);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_magcap_get_state(int32_t handle, uint8_t* out_ready, uint8_t* out_running) noexcept {
    auto source = mc::Find(handle);
    if (!source || !out_ready || !out_running) return VN_ERR_INVALID_ARG;
    *out_ready = source->ready.load() ? 1 : 0;
    *out_running = source->running.load() ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_magcap_add_exclude(int32_t handle, int64_t hwnd) noexcept {
    auto source = mc::Find(handle);
    if (!source) return VN_ERR_INVALID_ARG;
    try {
        source->AddFilter(mc::ToHwnd(hwnd));
    } catch (...) {
        return VN_ERR_FAIL;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_magcap_remove_exclude(int32_t handle, int64_t hwnd) noexcept {
    auto source = mc::Find(handle);
    if (!source) return VN_ERR_INVALID_ARG;
    source->RemoveFilter(mc::ToHwnd(hwnd));
    return VN_OK;
}

// Copies the freshest desktop crop into dest (width*height BGRA). out_ok = 0 when no usable frame exists.
VN_EXPORT int32_t __stdcall vn_magcap_capture_into(
    int32_t handle, int32_t x, int32_t y, int32_t width, int32_t height, void* destination, uint8_t* out_ok) noexcept {
    auto source = mc::Find(handle);
    if (!source || !out_ok) return VN_ERR_INVALID_ARG;
    *out_ok = 0;
    if (!source->ready.load() || !source->running.load() || !destination || width <= 0 || height <= 0) return VN_OK;

    const mc::VirtualScreenBounds vs = mc::QueryVirtualScreenBounds();
    const int left = std::clamp(x, vs.left, vs.right - 1);
    const int top = std::clamp(y, vs.top, vs.bottom - 1);
    const mc::CaptureRequest req{
        left,
        top,
        std::max(1, std::min(x + width, vs.right) - left),
        std::max(1, std::min(y + height, vs.bottom) - top)
    };

    uint64_t previous;
    {
        mc::ScopedLock guard(&source->frame_lock);
        previous = source->frame_counter;
        source->requested = req;
    }
    ResetEvent(source->frame_received);
    SetEvent(source->request);

    DWORD wait_ms = 0;
    {
        mc::ScopedLock guard(&source->frame_lock);
        const bool has_matching_frame =
            source->has_completed && source->completed_filter_version == source->filter_version.load();
        if (!has_matching_frame) {
            wait_ms = 12;
        } else if (source->frame_counter == previous) {
            wait_ms = 2;
        }
    }
    if (wait_ms > 0) {
        WaitForSingleObject(source->frame_received, wait_ms);
    }

    mc::ScopedLock guard(&source->frame_lock);
    if (!source->HasUsableFrameLocked(x, y, width, height)) return VN_OK;
    *out_ok = source->CopyCropLocked(x, y, width, height, static_cast<uint32_t*>(destination)) ? 1 : 0;
    return VN_OK;
}

// Recovery probe: asks for a repaint but never waits for the callback.
VN_EXPORT int32_t __stdcall vn_magcap_has_usable_frame(
    int32_t handle, int32_t x, int32_t y, int32_t width, int32_t height, uint8_t* out_usable) noexcept {
    auto source = mc::Find(handle);
    if (!source || !out_usable) return VN_ERR_INVALID_ARG;
    *out_usable = 0;
    if (!source->ready.load() || !source->running.load() || width <= 0 || height <= 0) return VN_OK;
    SetEvent(source->request);
    mc::ScopedLock guard(&source->frame_lock);
    *out_usable = source->HasUsableFrameLocked(x, y, width, height) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_magcap_debug_status(
    int32_t handle, int32_t x, int32_t y, int32_t width, int32_t height,
    char16_t* buffer, int32_t capacity, int32_t* out_length) noexcept {
    auto source = mc::Find(handle);
    if (!source || !buffer || capacity <= 0 || !out_length) return VN_ERR_INVALID_ARG;
    size_t filter_count;
    {
        mc::ScopedLock guard(&source->filter_lock);
        filter_count = source->filters.size();
    }
    mc::ScopedLock guard(&source->frame_lock);
    const int64_t age = source->has_completed ? static_cast<int64_t>(GetTickCount64()) - source->completed_tick : -1;
    const bool zero_crop = source->has_completed && source->IsUninitializedZeroCrop(x, y, width, height);
    const int written = _snwprintf_s(
        reinterpret_cast<wchar_t*>(buffer), static_cast<size_t>(capacity), _TRUNCATE,
        L"ready=%d running=%d enabled=%d hasFrame=%d frames=%llu cbEnter=%lld srcFail=%lld filters=%zu "
        L"filterVer=%d/%d (applied=%d) ageMs=%lld zeroCrop=%d frame=%dx%d@(%d,%d) req=(%d,%d %dx%d)",
        source->ready.load() ? 1 : 0, source->running.load() ? 1 : 0, source->capture_enabled.load() ? 1 : 0,
        source->has_completed ? 1 : 0, static_cast<unsigned long long>(source->frame_counter),
        static_cast<long long>(source->callback_enter_count.load()), static_cast<long long>(source->source_fail_count.load()),
        filter_count, source->completed_filter_version, source->filter_version.load(), source->applied_filter_version,
        static_cast<long long>(age), zero_crop ? 1 : 0, source->completed_width, source->completed_height,
        source->completed_x, source->completed_y, x, y, width, height);
    *out_length = written < 0 ? capacity - 1 : written;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_magcap_is_complete_frame(
    int32_t requested_width, int32_t requested_height, int32_t received_width, int32_t received_height,
    int32_t received_stride, int32_t buffer_length, uint8_t* out_complete) noexcept {
    if (!out_complete) return VN_ERR_INVALID_ARG;
    *out_complete = mc::IsCompleteFrame(requested_width, requested_height, received_width, received_height,
                                        received_stride, buffer_length) ? 1 : 0;
    return VN_OK;
}

// Test seam: installs a completed frame on a source without starting the pump thread.
VN_EXPORT int32_t __stdcall vn_magcap_test_install_frame(
    int32_t handle, uint8_t ready, uint8_t running, int32_t width, int32_t height,
    const uint8_t* pixels, int32_t pixel_bytes, int64_t frame_age_ms) noexcept {
    auto source = mc::Find(handle);
    if (!source || width < 0 || height < 0 || (!pixels && pixel_bytes > 0) || pixel_bytes < 0) return VN_ERR_INVALID_ARG;
    try {
        mc::ScopedLock guard(&source->frame_lock);
        source->ready = ready != 0;
        source->running = running != 0;
        if (width == 0 || height == 0) {
            source->ClearFrameLocked();
            return VN_OK;
        }
        source->completed.assign(pixels, pixels + pixel_bytes);
        source->completed_width = width;
        source->completed_height = height;
        source->completed_x = 0;
        source->completed_y = 0;
        source->completed_filter_version = source->filter_version.load();
        source->has_completed = true;
        source->completed_tick = static_cast<int64_t>(GetTickCount64()) - frame_age_ms;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
