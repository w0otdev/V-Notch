#pragma once
#include <windows.h>
#include <dwmapi.h>
#include <array>
#include <algorithm>
#include <vector>
namespace vnotch::controllers::backdrop {
inline constexpr int kMaxWindows = 8;
inline constexpr int kMaxSelf = 8;
struct Layer { HWND hwnd; RECT window; RECT visible; bool from_wgc = false; DWORD pid = 0, thread = 0; };
struct OverlayCheck { HWND hwnd; ULONGLONG until; bool bad; };

struct EnumState {
    RECT area{};
    DWORD self_pid = 0;
    const std::vector<HWND>* bad = nullptr;
    const std::vector<OverlayCheck>* overlay_checks = nullptr;
    std::array<Layer, kMaxWindows> layers{};
    int count = 0;
    std::array<RECT, kMaxSelf> self_rects{};
    int self_count = 0;
    std::array<RECT, kMaxWindows * 4 + 1> uncovered{};
    int uncovered_count = 0;
    bool coverage_initialized = false;
    bool coverage_overflow = false;
    bool require_opaque = false, unsupported = false;
};

inline bool IntersectsUncovered(const EnumState& state, const RECT& frame) noexcept {
    if (state.coverage_overflow) return true;
    for (int i = 0; i < state.uncovered_count; ++i) {
        RECT hit{};
        if (IntersectRect(&hit, &frame, &state.uncovered[i])) return true;
    }
    return false;
}

inline void CoverOpaqueLayer(EnumState& state, const RECT& frame) noexcept {
    if (state.coverage_overflow) return;
    std::array<RECT, kMaxWindows * 4 + 1> next{};
    int count = 0;
    const auto add = [&](RECT rect) {
        if (rect.right <= rect.left || rect.bottom <= rect.top) return;
        if (count == static_cast<int>(next.size())) { state.coverage_overflow = true; return; }
        next[count++] = rect;
    };
    for (int i = 0; i < state.uncovered_count; ++i) {
        const RECT rect = state.uncovered[i];
        RECT hit{};
        if (!IntersectRect(&hit, &rect, &frame)) { add(rect); continue; }
        add({rect.left, rect.top, rect.right, hit.top});
        add({rect.left, hit.bottom, rect.right, rect.bottom});
        add({rect.left, hit.top, hit.left, hit.bottom});
        add({hit.right, hit.top, rect.right, hit.bottom});
    }
    if (!state.coverage_overflow) { state.uncovered = next; state.uncovered_count = count; }
}

inline bool IsCloaked(HWND hwnd) noexcept {
    DWORD cloaked = 0;
    return SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked != 0;
}

inline bool IsDesktopShell(HWND hwnd) noexcept {
    wchar_t cls[16]{};
    if (GetClassNameW(hwnd, cls, 16) <= 0) return false;
    return wcscmp(cls, L"Progman") == 0 || wcscmp(cls, L"WorkerW") == 0;
}

constexpr bool IsUnredirectedToolOverlay(LONG_PTR extendedStyle) noexcept {
    const LONG_PTR overlay = WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP;
    return (extendedStyle & overlay) == overlay;
}

// EnumWindows walks top to bottom, so layers[0] is the topmost window.
inline BOOL CALLBACK CollectLayer(HWND hwnd, LPARAM param) noexcept {
    auto& state = *reinterpret_cast<EnumState*>(param);
    if (!state.coverage_initialized) {
        state.uncovered[0] = state.area;
        state.uncovered_count = 1;
        state.coverage_initialized = true;
    }
    if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsDesktopShell(hwnd)) return TRUE;
    DWORD pid = 0;
    const DWORD thread = GetWindowThreadProcessId(hwnd, &pid);
    if (pid == state.self_pid) {
        RECT own{};
        if (state.self_count < kMaxSelf && GetWindowRect(hwnd, &own)) state.self_rects[state.self_count++] = own;
        return TRUE;
    }
    if (state.bad && std::find(state.bad->begin(), state.bad->end(), hwnd) != state.bad->end()) return TRUE;
    const LONG_PTR ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
    if (state.overlay_checks && IsUnredirectedToolOverlay(ex)) {
        for (const auto& check : *state.overlay_checks) {
            if (check.hwnd == hwnd && check.bad) return TRUE;
        }
    }
    if (state.require_opaque && (ex & (WS_EX_LAYERED | WS_EX_NOREDIRECTIONBITMAP))) {
        RECT bounds{}, hit{};
        if (GetWindowRect(hwnd, &bounds) && IntersectRect(&hit, &bounds, &state.area) && IntersectsUncovered(state, bounds)) {
            state.unsupported = true;
            return FALSE;
        }
    }
    // Per-pixel-alpha layered windows print as black where they are transparent
    // and PrintWindow returns no alpha, so only plain opaque layered windows are used.
    if (ex & WS_EX_LAYERED) {
        BYTE alpha = 0; DWORD flags = 0;
        if (!GetLayeredWindowAttributes(hwnd, nullptr, &alpha, &flags) || (flags & LWA_COLORKEY) || alpha < 255) return TRUE;
    }
    if (IsCloaked(hwnd)) return TRUE;

    RECT frame{};
    if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &frame, sizeof(frame))) && !GetWindowRect(hwnd, &frame))
        return TRUE;
    RECT hit{};
    if (!IntersectRect(&hit, &frame, &state.area)) return TRUE;
    // A top bar can occupy only the first rows while the frontmost app covers
    // the rest. Do not start capture sessions for every buried maximized app.
    if (!IntersectsUncovered(state, frame)) return TRUE;
    RECT window{};
    if (!GetWindowRect(hwnd, &window)) return TRUE;
    if (state.count == kMaxWindows) return FALSE;

    state.layers[state.count++] = { hwnd, window, frame, false, pid, thread };
    if (!(ex & WS_EX_LAYERED) && !IsUnredirectedToolOverlay(ex)) CoverOpaqueLayer(state, frame);
    const bool covers = !state.coverage_overflow && state.uncovered_count == 0;
    // An opaque window covering the whole strip hides everything below it.
    return covers ? FALSE : TRUE;
}

}
