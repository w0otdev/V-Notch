#include "../vnotch_common.h"
#include <dwmapi.h>
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <thread>
#include <vector>

#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "user32.lib")

// Liquid Glass backdrop used when the Magnifier callback returns empty frames
// and Ghost Mode is off (so the notch cannot be excluded from screen capture).
// The desktop is rebuilt from the wallpaper plus every window below the notch,
// skipping windows owned by this process, so the notch never samples itself.
//
// PrintWindow on maximized windows takes tens of milliseconds, so a worker
// thread composes one wide strip of the monitor top and callers only copy a
// crop out of the latest finished strip. The render loop never waits on it.
//
// Some windows (overlay bars, layered widgets) print as opaque even though
// they are not visible on screen. Every composition is checked against the
// real screen at points outside this process's windows, and layers that do not
// match are dropped and remembered.
namespace vnotch::controllers::backdrop {
namespace {

constexpr int kMaxWindows = 8;
constexpr int kMaxSelf = 8;
constexpr int kStripMinHeight = 340;
constexpr int kMismatchThreshold = 40;
constexpr ULONGLONG kLiveTtlMs = 4;
constexpr ULONGLONG kStaticTtlMs = 500;

struct Layer { HWND hwnd; RECT window; RECT visible; };

struct EnumState {
    RECT area{};
    DWORD self_pid = 0;
    const std::vector<HWND>* bad = nullptr;
    std::array<Layer, kMaxWindows> layers{};
    int count = 0;
    std::array<RECT, kMaxSelf> self_rects{};
    int self_count = 0;
};

bool IsCloaked(HWND hwnd) noexcept {
    DWORD cloaked = 0;
    return SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked != 0;
}

bool IsDesktopShell(HWND hwnd) noexcept {
    wchar_t cls[16]{};
    if (GetClassNameW(hwnd, cls, 16) <= 0) return false;
    return wcscmp(cls, L"Progman") == 0 || wcscmp(cls, L"WorkerW") == 0;
}

constexpr bool IsUnredirectedToolOverlay(LONG_PTR extendedStyle) noexcept {
    const LONG_PTR overlay = WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP;
    return (extendedStyle & overlay) == overlay;
}

// EnumWindows walks top to bottom, so layers[0] is the topmost window.
BOOL CALLBACK CollectLayer(HWND hwnd, LPARAM param) noexcept {
    auto& state = *reinterpret_cast<EnumState*>(param);
    if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsDesktopShell(hwnd)) return TRUE;
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == state.self_pid) {
        RECT own{};
        if (state.self_count < kMaxSelf && GetWindowRect(hwnd, &own)) state.self_rects[state.self_count++] = own;
        return TRUE;
    }
    if (state.bad && std::find(state.bad->begin(), state.bad->end(), hwnd) != state.bad->end()) return TRUE;
    const LONG_PTR ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
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
    RECT window{};
    if (!GetWindowRect(hwnd, &window)) return TRUE;
    if (state.count == kMaxWindows) return FALSE;

    state.layers[state.count++] = { hwnd, window, frame };
    const bool covers = !(ex & WS_EX_LAYERED) && EqualRect(&hit, &state.area);
    // An opaque window covering the whole strip hides everything below it.
    return covers ? FALSE : TRUE;
}

// Reusable top-down 32-bit DIB; reallocated only when the size changes.
struct Dib {
    HDC dc = nullptr;
    HBITMAP bitmap = nullptr;
    HGDIOBJ old = nullptr;
    void* bits = nullptr;
    int width = 0, height = 0;

    bool Ensure(HDC compatible, int w, int h) noexcept {
        if (bits && w == width && h == height) return true;
        Release();
        BITMAPINFO bi{};
        bi.bmiHeader.biSize = sizeof(bi.bmiHeader);
        bi.bmiHeader.biWidth = w;
        bi.bmiHeader.biHeight = -h;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        dc = CreateCompatibleDC(compatible);
        if (!dc) return false;
        bitmap = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (!bitmap || !bits) { Release(); return false; }
        old = SelectObject(dc, bitmap);
        width = w; height = h;
        return true;
    }
    void Release() noexcept {
        if (dc && old) SelectObject(dc, old);
        if (bitmap) DeleteObject(bitmap);
        if (dc) DeleteDC(dc);
        dc = nullptr; bitmap = nullptr; old = nullptr; bits = nullptr; width = height = 0;
    }
    ~Dib() noexcept { Release(); }
};

struct Scratch {
    Dib target;
    Dib window;
    std::vector<HWND> bad; // windows that printed opaque but are not visible on screen
    std::vector<HWND> verified; // windows already verified against screen pixels
};

constexpr bool Contains(const RECT& outer, const RECT& inner) noexcept {
    return inner.left >= outer.left && inner.top >= outer.top && inner.right <= outer.right && inner.bottom <= outer.bottom;
}

constexpr bool PointInRect(const RECT& r, int x, int y) noexcept {
    return x >= r.left && x < r.right && y >= r.top && y < r.bottom;
}

bool IsPointCoveredBeforeLayer(const EnumState& state, int layer_index, int x, int y) noexcept {
    for (int s = 0; s < state.self_count; ++s) {
        if (PointInRect(state.self_rects[s], x, y)) return true;
    }
    for (int h = 0; h < layer_index; ++h) {
        if (PointInRect(state.layers[h].visible, x, y)) return true;
    }
    return false;
}

bool PixelDiffExceedsThreshold(uint32_t mine, COLORREF real) noexcept {
    const int dr = std::abs(static_cast<int>((mine >> 16) & 0xFF) - static_cast<int>(GetRValue(real)));
    const int dg = std::abs(static_cast<int>((mine >> 8) & 0xFF) - static_cast<int>(GetGValue(real)));
    const int db = std::abs(static_cast<int>(mine & 0xFF) - static_cast<int>(GetBValue(real)));
    return (dr + dg + db) / 3 > kMismatchThreshold;
}

// Returns the hwnd of a layer whose composited pixels disagree with the real
// screen at points no higher layer or window of this process covers, or null.
HWND FindMismatchedLayer(HDC screen, const Dib& target, const RECT& area, const EnumState& state, Scratch& scratch) {
    const int width = area.right - area.left;
    const auto* pixels = static_cast<const uint32_t*>(target.bits);
    for (int i = 0; i < state.count; ++i) {
        const Layer& layer = state.layers[i];
        const bool liveOverlay = IsUnredirectedToolOverlay(GetWindowLongPtrW(layer.hwnd, GWL_EXSTYLE));
        // Shell materials can switch between opaque and transparent without a
        // new HWND. A previous successful comparison is not valid for this frame.
        if (!liveOverlay && std::find(scratch.verified.begin(), scratch.verified.end(), layer.hwnd) != scratch.verified.end())
            continue;
        RECT hit{};
        if (!IntersectRect(&hit, &layer.visible, &area)) continue;
        int samples = 0, mismatches = 0;
        const int hit_w = hit.right - hit.left;
        const int xs[] = { hit.left + hit_w / 10, hit.left + hit_w / 2, hit.right - hit_w / 10 - 1 };
        const int ys[] = { hit.top + 3, hit.top + (hit.bottom - hit.top) / 2 };
        for (int y : ys) {
            for (int x : xs) {
                if (!PointInRect(hit, x, y) || IsPointCoveredBeforeLayer(state, i, x, y)) continue;
                const COLORREF real = GetPixel(screen, x, y);
                if (real == CLR_INVALID) continue;
                const uint32_t mine = pixels[static_cast<size_t>(y - area.top) * width + (x - area.left)];
                ++samples;
                if (PixelDiffExceedsThreshold(mine, real)) ++mismatches;
            }
        }
        if ((samples > 0 && mismatches * 2 >= samples) || (liveOverlay && samples == 0)) return layer.hwnd;
        if (samples > 0 && !liveOverlay) scratch.verified.push_back(layer.hwnd);
    }
    return nullptr;
}

struct HitPixelSummary {
    bool any_pixel = false;
    bool needs_blend = false;
    bool all_opaque = true;
};

HitPixelSummary InspectLayerHit(const uint32_t* wpx, int ww, int ox, int oy, int hw, int hh) noexcept {
    HitPixelSummary summary{};
    bool saw_alpha = false;
    bool saw_zero_alpha = false;
    for (int row = 0; row < hh; ++row) {
        const uint32_t* line = wpx + static_cast<size_t>(oy + row) * ww + ox;
        for (int col = 0; col < hw; ++col) {
            const uint32_t px = line[col];
            if (px == 0) {
                saw_zero_alpha = true;
                summary.all_opaque = false;
                if (saw_alpha) {
                    summary.needs_blend = true;
                    return summary;
                }
                continue;
            }
            summary.any_pixel = true;
            const uint32_t a = px >> 24;
            if (a == 0) {
                saw_zero_alpha = true;
                if (saw_alpha) {
                    summary.all_opaque = false;
                    summary.needs_blend = true;
                    return summary;
                }
            } else {
                saw_alpha = true;
                if (a < 255u || saw_zero_alpha) {
                    summary.all_opaque = false;
                    summary.needs_blend = true;
                    return summary;
                }
            }
        }
    }
    return summary;
}

constexpr uint32_t BlendPremultipliedChannel(uint32_t src_ch, uint32_t dst_ch, uint32_t inv_alpha) noexcept {
    const uint32_t sum = src_ch + (dst_ch * inv_alpha + 127u) / 255u;
    return sum < 255u ? sum : 255u;
}

void BlendPremultipliedHit(
    const uint32_t* wpx, int ww, int ox, int oy,
    uint32_t* tpx, int target_w, int dst_x, int dst_y,
    int hw, int hh) noexcept {
    for (int row = 0; row < hh; ++row) {
        const uint32_t* s = wpx + static_cast<size_t>(oy + row) * ww + ox;
        uint32_t* t = tpx + static_cast<size_t>(dst_y + row) * target_w + dst_x;
        for (int col = 0; col < hw; ++col) {
            const uint32_t sp = s[col];
            if (sp == 0) {
                t[col] |= 0xFF000000u;
                continue;
            }
            const uint32_t a = sp >> 24;
            if (a == 255u) {
                t[col] = sp;
                continue;
            }
            const uint32_t inv = 255u - a;
            const uint32_t tp = t[col];
            // Source is premultiplied by DWM; keep the target opaque.
            const uint32_t r = BlendPremultipliedChannel((sp >> 16) & 0xFFu, (tp >> 16) & 0xFFu, inv);
            const uint32_t g = BlendPremultipliedChannel((sp >> 8) & 0xFFu, (tp >> 8) & 0xFFu, inv);
            const uint32_t b = BlendPremultipliedChannel(sp & 0xFFu, tp & 0xFFu, inv);
            t[col] = 0xFF000000u | (r << 16) | (g << 8) | b;
        }
    }
}

bool CompositeWindowLayer(Scratch& scratch, HDC screen, HDC dc, const RECT& area, int width, const Layer& layer, bool* out_all_opaque = nullptr) noexcept {
    // Copy only the visible frame; the invisible resize border prints black.
    RECT hit{};
    if (!IntersectRect(&hit, &layer.visible, &area)) return false;
    const int ww = layer.window.right - layer.window.left;
    const int wh = layer.window.bottom - layer.window.top;
    if (ww <= 0 || wh <= 0) return false;
    if (!scratch.window.Ensure(screen, ww, wh)) return false;
    const int ox = hit.left - layer.window.left;
    const int oy = hit.top - layer.window.top;
    const int hw = hit.right - hit.left;
    const int hh = hit.bottom - hit.top;
    if (ox < 0 || oy < 0 || ox + hw > ww || oy + hh > wh) return false;
    std::memset(
        static_cast<uint8_t*>(scratch.window.bits) + static_cast<size_t>(oy) * ww * sizeof(uint32_t),
        0,
        static_cast<size_t>(hh) * ww * sizeof(uint32_t));
    if (!PrintWindow(layer.hwnd, scratch.window.dc, PW_RENDERFULLCONTENT)) return false;
    GdiFlush();

    // Windows that draw with alpha (or nothing at all) print as black in the
    // transparent parts, so inspect the pixels instead of copying blindly.
    const auto* wpx = static_cast<const uint32_t*>(scratch.window.bits);
    const HitPixelSummary summary = InspectLayerHit(wpx, ww, ox, oy, hw, hh);
    if (!summary.any_pixel) return false; // fully transparent window
    if (out_all_opaque) *out_all_opaque = summary.all_opaque;

    const int dst_x = hit.left - area.left;
    const int dst_y = hit.top - area.top;
    if (!summary.needs_blend) {
        BitBlt(dc, dst_x, dst_y, hw, hh, scratch.window.dc, ox, oy, SRCCOPY);
    } else {
        BlendPremultipliedHit(wpx, ww, ox, oy, static_cast<uint32_t*>(scratch.target.bits), width, dst_x, dst_y, hw, hh);
    }
    return true;
}

// Composes `area` into out (area-sized, opaque BGRA). static_only is true when
// no application window sits under the area, so the result can live longer.
bool Compose(Scratch& scratch, const RECT& area, std::vector<uint32_t>& out, bool& static_only) {
    const int width = area.right - area.left, height = area.bottom - area.top;
    HDC screen = GetDC(nullptr);
    if (!screen) return false;
    struct Release { HDC dc; ~Release() noexcept { ReleaseDC(nullptr, dc); } } release{ screen };
    if (!scratch.target.Ensure(screen, width, height)) return false;
    HDC dc = scratch.target.dc;

    // Never keep shell overlays permanently accepted or rejected: their material
    // changes when a window is maximized or the desktop is revealed.
    const auto forget = [](HWND h) {
        return !IsWindow(h) || IsUnredirectedToolOverlay(GetWindowLongPtrW(h, GWL_EXSTYLE));
    };
    scratch.bad.erase(std::remove_if(scratch.bad.begin(), scratch.bad.end(), forget), scratch.bad.end());
    scratch.verified.erase(std::remove_if(scratch.verified.begin(), scratch.verified.end(), forget), scratch.verified.end());

    const DWORD self_pid = GetCurrentProcessId();
    for (int attempt = 0; attempt < 3; ++attempt) {
        static_only = true;
        EnumState state;
        state.area = area;
        state.self_pid = self_pid;
        state.bad = &scratch.bad;
        EnumWindows(CollectLayer, reinterpret_cast<LPARAM>(&state));

        // Fast single-layer shortcut when the topmost window completely covers `area`:
        // avoids PaintDesktop + PrintWindow on buried background windows underneath it.
        bool covered = false;
        if (state.count > 0 && Contains(state.layers[0].visible, area)) {
            bool top_opaque = false;
            if (CompositeWindowLayer(scratch, screen, dc, area, width, state.layers[0], &top_opaque) && top_opaque) {
                static_only = false;
                covered = true;
            }
        }

        bool ok = covered;
        if (!covered) {
            // PaintDesktop treats DC coordinates as screen coordinates.
            SetViewportOrgEx(dc, -area.left, -area.top, nullptr);
            ok = PaintDesktop(dc) != FALSE;
            SetViewportOrgEx(dc, 0, 0, nullptr);

            int start_idx = state.count - 1;
            for (int i = 0; i < state.count; ++i) {
                if (Contains(state.layers[i].visible, area)) {
                    start_idx = i;
                    break;
                }
            }
            for (int i = start_idx; i >= 0; --i) {
                if (CompositeWindowLayer(scratch, screen, dc, area, width, state.layers[i])) {
                    static_only = false;
                    ok = true;
                }
            }
        }
        if (!ok) return false;
        GdiFlush();

        if (state.count > 0) {
            if (HWND bad = FindMismatchedLayer(screen, scratch.target, area, state, scratch)) {
                scratch.bad.push_back(bad);
                continue; // compose again without it
            }
        }
        break;
    }

    const size_t total = static_cast<size_t>(width) * static_cast<size_t>(height);
    out.resize(total);
    const auto* src = static_cast<const uint32_t*>(scratch.target.bits);
    for (size_t p = 0; p < total; ++p) out[p] = src[p] | 0xFF000000u;
    return true;
}

// A wide strip of the monitor top so notch resizes stay inside one composition.
RECT StripFor(const RECT& area) noexcept {
    RECT strip = area;
    if (HMONITOR monitor = MonitorFromRect(&area, MONITOR_DEFAULTTONEAREST)) {
        MONITORINFO info{ sizeof(info) };
        if (GetMonitorInfoW(monitor, &info)) {
            strip = { info.rcMonitor.left, info.rcMonitor.top, info.rcMonitor.right,
                std::min<LONG>(info.rcMonitor.bottom, std::max<LONG>(area.bottom + 64, info.rcMonitor.top + kStripMinHeight)) };
            UnionRect(&strip, &strip, &area);
        }
    }
    return strip;
}

struct Strip {
    RECT area{};
    ULONGLONG tick = 0;
    ULONGLONG ttl = 0;
    bool valid = false;
    std::vector<uint32_t> pixels;
};

struct Shared {
    SRWLOCK lock = SRWLOCK_INIT;
    Strip strip;
    HANDLE wake = nullptr;
    bool worker_started = false;
    bool busy = false;
    RECT wanted{};
};
// shortcut: process-lifetime state with a detached worker, never joined; fine for a single-process DLL, revisit if the DLL is ever unloaded at runtime.
Shared g_shared;

struct SrwExclusiveGuard {
    SRWLOCK* lock;
    explicit SrwExclusiveGuard(SRWLOCK& l) noexcept : lock(&l) { AcquireSRWLockExclusive(lock); }
    ~SrwExclusiveGuard() noexcept { if (lock) ReleaseSRWLockExclusive(lock); }
    SrwExclusiveGuard(const SrwExclusiveGuard&) = delete;
    SrwExclusiveGuard& operator=(const SrwExclusiveGuard&) = delete;
    void Unlock() noexcept {
        if (lock) {
            ReleaseSRWLockExclusive(lock);
            lock = nullptr;
        }
    }
};

void CommitStrip(Strip& strip, const RECT& area, std::vector<uint32_t>& pixels, bool static_only) noexcept {
    strip.pixels.swap(pixels);
    strip.area = area;
    strip.tick = GetTickCount64();
    strip.ttl = static_only ? kStaticTtlMs : kLiveTtlMs;
    strip.valid = true;
}

void WorkerMain() noexcept {
    Scratch scratch;
    std::vector<uint32_t> pixels;
    for (;;) {
        WaitForSingleObject(g_shared.wake, INFINITE);
        RECT wanted;
        {
            SrwExclusiveGuard guard(g_shared.lock);
            wanted = g_shared.wanted;
        }

        bool static_only = false;
        bool ok = false;
        try { ok = Compose(scratch, wanted, pixels, static_only); } catch (...) { ok = false; }

        SrwExclusiveGuard guard(g_shared.lock);
        if (ok) CommitStrip(g_shared.strip, wanted, pixels, static_only);
        g_shared.busy = false;
    }
}

// Caller holds the lock. Returns false when the worker cannot be started.
bool EnsureWorker() {
    if (g_shared.worker_started) return true;
    g_shared.wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!g_shared.wake) return false;
    std::thread(WorkerMain).detach();
    g_shared.worker_started = true;
    return true;
}

void CopyCrop(const Strip& strip, const RECT& area, uint32_t* dest) noexcept {
    const size_t stripW = static_cast<size_t>(strip.area.right - strip.area.left);
    const size_t width = static_cast<size_t>(area.right - area.left);
    const size_t xOffset = static_cast<size_t>(area.left - strip.area.left);
    const size_t yOffset = static_cast<size_t>(area.top - strip.area.top);
    const int height = area.bottom - area.top;
    const uint32_t* srcRowPtr = strip.pixels.data() + yOffset * stripW + xOffset;
    for (int row = 0; row < height; ++row) {
        std::memcpy(dest + static_cast<size_t>(row) * width, srcRowPtr + static_cast<size_t>(row) * stripW, width * sizeof(uint32_t));
    }
}

} // namespace
} // namespace vnotch::controllers::backdrop

namespace bd = vnotch::controllers::backdrop;

// Fills destination (width*height top-down BGRA) with the desktop under the
// given screen rect, excluding this process's windows. Serves the newest strip
// the worker finished and asks it for a refresh when that strip is stale.
VN_EXPORT int32_t __stdcall vn_backdrop_composite(
    int32_t x, int32_t y, int32_t width, int32_t height, void* destination, uint8_t* out_ok) noexcept {
    if (!out_ok) return VN_ERR_INVALID_ARG;
    *out_ok = 0;
    if (!destination || width <= 0 || height <= 0 || width > 16384 || height > 16384) return VN_ERR_INVALID_ARG;
    try {
        const RECT area{ x, y, x + width, y + height };
        auto* dest = static_cast<uint32_t*>(destination);
        auto& shared = bd::g_shared;

        {
            bd::SrwExclusiveGuard guard(shared.lock);
            if (!bd::EnsureWorker()) return VN_ERR_FAIL;
            if (shared.strip.valid && bd::Contains(shared.strip.area, area)) {
                bd::CopyCrop(shared.strip, area, dest);
                if (!shared.busy && GetTickCount64() - shared.strip.tick >= shared.strip.ttl) {
                    shared.busy = true;
                    shared.wanted = shared.strip.area;
                    SetEvent(shared.wake);
                }
                *out_ok = 1;
                return VN_OK;
            }
        }

        // First frame, or the area left the strip (monitor change): compose once here.
        const RECT wanted = bd::StripFor(area);
        bd::Scratch scratch;
        std::vector<uint32_t> pixels;
        bool static_only = false;
        if (!bd::Compose(scratch, wanted, pixels, static_only)) return VN_OK;

        bd::SrwExclusiveGuard guard(shared.lock);
        bd::CommitStrip(shared.strip, wanted, pixels, static_only);
        bd::CopyCrop(shared.strip, area, dest);
        *out_ok = 1;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}
