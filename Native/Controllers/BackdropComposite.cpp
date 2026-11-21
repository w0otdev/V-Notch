#include "../vnotch_common.h"
#include <dwmapi.h>
#include <algorithm>
#include <array>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <thread>
#include <vector>
#include "WindowBackdropCapture.h"
#include "BackdropLayers.h"

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
// crop out of the latest finished strip.
//
// Some windows (overlay bars, layered widgets) print as opaque even though
// they are not visible on screen. Every composition is checked against the
// real screen at points outside this process's windows, and layers that do not
// match are dropped and remembered.
namespace vnotch::controllers::backdrop {
namespace {

constexpr int kMismatchThreshold = 40;
constexpr ULONGLONG kWallpaperTtlMs = 500;
constexpr ULONGLONG kOverlayCacheTtlMs = 500;
constexpr ULONGLONG kFullPrintIntervalMs = 66;
constexpr int kMaxStrips = 4;
constexpr LONG kMaxMergedStripPixels = 600000;

struct ComposeMetrics {
    bool static_only = true;
    bool any_live_wgc = false;
    bool any_fresh_wgc = false;
    bool any_fast_print = false;
    bool any_full_print = false;
};

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

struct PrintedLayerRefresh {
    Layer layer;
    RECT hit;
    bool needs_full = false, printed_full = false, ok = false;
    std::vector<uint32_t> pixels;
    ULONGLONG updated = 0;
    std::atomic<bool> done{false};
};
std::atomic<unsigned> pending_legacy_refreshes{0};

bool ReserveLegacyRefresh() noexcept {
    unsigned count = pending_legacy_refreshes.load(std::memory_order_relaxed);
    while (count < kMaxWindows) {
        if (pending_legacy_refreshes.compare_exchange_weak(count, count + 1, std::memory_order_relaxed)) return true;
    }
    return false;
}

struct PrintedLayer {
    HWND hwnd = nullptr;
    DWORD pid = 0, thread = 0;
    RECT window{}, hit{};
    ULONGLONG updated = 0, used = 0;
    bool needs_full = false, printed_full = false, ok = false;
    std::vector<uint32_t> pixels;
    std::shared_ptr<PrintedLayerRefresh> refresh;
};

struct LiveWindow {
    HWND hwnd;
    DWORD pid, thread;
    ULONGLONG used;
};

struct Scratch {
    Dib target;
    Dib window;
    Dib full_window;
    Dib screen_samples;
    Dib wallpaper;
    RECT wallpaper_area{};
    ULONGLONG wallpaper_tick = 0;
    capture::WindowCaptureCache window_capture;
    bool use_window_capture = true;
    std::vector<HWND> bad; // windows that printed opaque but are not visible on screen
    std::vector<HWND> verified; // windows already verified against screen pixels
    std::vector<HWND> full_content; // apps whose ordinary print did not match the desktop
    std::vector<OverlayCheck> overlay_checks; // bounded TTL cache for unredirected shell overlays
    std::vector<PrintedLayer> printed_layers;
    bool async_legacy_refresh = false;
    std::vector<LiveWindow> live_windows;
};

bool TrackLiveCapture(Scratch& scratch, HWND hwnd, bool captured) {
    const ULONGLONG now = GetTickCount64();
    DWORD pid = 0;
    const DWORD thread = GetWindowThreadProcessId(hwnd, &pid);
    auto& windows = scratch.live_windows;
    windows.erase(std::remove_if(windows.begin(), windows.end(), [&](const LiveWindow& entry) {
        return now - entry.used > 2000 || !IsWindow(entry.hwnd) ||
            (entry.hwnd == hwnd && (entry.pid != pid || entry.thread != thread));
    }), windows.end());
    for (auto& entry : windows) {
        if (entry.hwnd == hwnd) { entry.used = now; return true; }
    }
    if (captured && pid && thread) {
        if (windows.size() >= kMaxWindows)
            windows.erase(std::min_element(windows.begin(), windows.end(), [](const auto& a, const auto& b) { return a.used < b.used; }));
        windows.push_back({hwnd, pid, thread, now});
    }
    return captured;
}

// An unredirected tool overlay that WGC captured recently is real, visible
// content (e.g. MyDockFinder's top bar), not an invisible shell ghost. If its
// frame is momentarily missing, keep the last complete strip instead of
// composing without it, which made the bar blink in and out.
bool WasLiveCapture(const Scratch& scratch, HWND hwnd) noexcept {
    for (const auto& entry : scratch.live_windows)
        if (entry.hwnd == hwnd) return true;
    return false;
}

bool MayOmitFailedLayer(const Scratch& scratch, HWND hwnd) noexcept {
    return IsUnredirectedToolOverlay(GetWindowLongPtrW(hwnd, GWL_EXSTYLE)) && !WasLiveCapture(scratch, hwnd);
}

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
    bool screen_copied = false;
    RECT sampled_area{};
    RECT sample_bounds{};
    bool have_samples = false;
    const auto is_verified = [&](const Layer& layer, bool liveOverlay) noexcept {
        if (layer.from_wgc) return true; // WGC captures real DWM visual directly; never compare against raced screen snapshot
        if (liveOverlay) {
            for (const auto& check : scratch.overlay_checks) {
                if (check.hwnd == layer.hwnd) return !check.bad;
            }
            return false;
        }
        return std::find(scratch.verified.begin(), scratch.verified.end(), layer.hwnd) != scratch.verified.end();
    };
    for (int i = 0; i < state.count; ++i) {
        const auto& layer = state.layers[i];
        const bool liveOverlay = IsUnredirectedToolOverlay(GetWindowLongPtrW(layer.hwnd, GWL_EXSTYLE));
        if (is_verified(layer, liveOverlay)) continue;
        RECT hit{};
        if (!IntersectRect(&hit, &layer.visible, &area)) continue;
        const int hit_w = hit.right - hit.left;
        for (int y : {hit.top + 3, hit.top + (hit.bottom - hit.top) / 2}) {
            for (int x : {hit.left + hit_w / 10, hit.left + hit_w / 2, hit.right - hit_w / 10 - 1}) {
                if (!PointInRect(hit, x, y) || IsPointCoveredBeforeLayer(state, i, x, y)) continue;
                const RECT point{x, y, x + 1, y + 1};
                if (!have_samples) sample_bounds = point;
                else UnionRect(&sample_bounds, &sample_bounds, &point);
                have_samples = true;
            }
        }
    }
    for (int i = 0; i < state.count; ++i) {
        const Layer& layer = state.layers[i];
        const bool liveOverlay = IsUnredirectedToolOverlay(GetWindowLongPtrW(layer.hwnd, GWL_EXSTYLE));
        if (is_verified(layer, liveOverlay)) continue;
        RECT hit{};
        if (!IntersectRect(&hit, &layer.visible, &area)) continue;
        int samples = 0, mismatches = 0;
        const int hit_w = hit.right - hit.left;
        const int xs[] = { hit.left + hit_w / 10, hit.left + hit_w / 2, hit.right - hit_w / 10 - 1 };
        const int ys[] = { hit.top + 3, hit.top + (hit.bottom - hit.top) / 2 };
        for (int y : ys) {
            for (int x : xs) {
                if (!PointInRect(hit, x, y) || IsPointCoveredBeforeLayer(state, i, x, y)) continue;
                // GetPixel performs a desktop/GPU round trip for every point.
                // Snapshot once, then validate all overlay points from CPU memory.
                if (!screen_copied) {
                    const RECT desktop{GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
                        GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN),
                        GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN)};
                    if (!have_samples || !IntersectRect(&sampled_area, &sample_bounds, &desktop)) return nullptr;
                    const int sample_width = sampled_area.right - sampled_area.left;
                    const int sample_height = sampled_area.bottom - sampled_area.top;
                    const auto debug_start = std::chrono::steady_clock::now();
                    if (!scratch.screen_samples.Ensure(screen, sample_width, sample_height)) return nullptr;
                    // Validation runs only when an overlay cache expires. Starting
                    // a continuous full-monitor WGC session for these few samples
                    // competes with the actual window capture between validations.
                    if (!BitBlt(scratch.screen_samples.dc, 0, 0, sample_width, sample_height,
                        screen, sampled_area.left, sampled_area.top, SRCCOPY)) return nullptr;
                    GdiFlush();
                    capture::debug_info[18] += static_cast<int>(std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - debug_start).count());
                    ++capture::debug_info[19];
                    screen_copied = true;
                }
                if (!PointInRect(sampled_area, x, y)) continue;
                const uint32_t sample = static_cast<const uint32_t*>(scratch.screen_samples.bits)[
                    static_cast<size_t>(y - sampled_area.top) * scratch.screen_samples.width + x - sampled_area.left];
                const COLORREF real = RGB((sample >> 16) & 255, (sample >> 8) & 255, sample & 255);
                const uint32_t mine = pixels[static_cast<size_t>(y - area.top) * width + (x - area.left)];
                ++samples;
                if (PixelDiffExceedsThreshold(mine, real)) ++mismatches;
            }
        }
        if ((samples > 0 && mismatches * 2 >= samples) || (liveOverlay && samples == 0)) return layer.hwnd;
        if (samples > 0) {
            if (liveOverlay) scratch.overlay_checks.push_back({ layer.hwnd, GetTickCount64() + kOverlayCacheTtlMs, false });
            else scratch.verified.push_back(layer.hwnd);
        }
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

// Legacy layers have their own refresh budget. A slow PrintWindow overlay must
// not impose its 66 ms interval on a live WGC window in the same glass strip.
bool CopyPrintedLayer(Scratch& scratch, HDC screen, const Layer& layer, const RECT& hit,
    bool needs_full, ComposeMetrics& metrics) {
    const ULONGLONG now = GetTickCount64();
    DWORD pid = 0;
    const DWORD thread = GetWindowThreadProcessId(layer.hwnd, &pid);
    if (!thread || !pid) return false;
    auto& cache = scratch.printed_layers;
    cache.erase(std::remove_if(cache.begin(), cache.end(), [&](const PrintedLayer& entry) {
        return now - entry.used > 2000 || !IsWindow(entry.hwnd);
    }), cache.end());
    auto found = std::find_if(cache.begin(), cache.end(), [&](const PrintedLayer& entry) {
        return entry.hwnd == layer.hwnd;
    });
    const int hw = hit.right - hit.left, hh = hit.bottom - hit.top;
    if (found != cache.end()) {
        found->used = now;
        const ULONGLONG interval = found->printed_full ? kFullPrintIntervalMs : 16;
        const bool same_geometry = found->pid == pid && found->thread == thread &&
            EqualRect(&found->window, &layer.window) && EqualRect(&found->hit, &hit) &&
            found->needs_full == needs_full;
        if (same_geometry && found->refresh && found->refresh->done.load(std::memory_order_acquire)) {
            auto& ready = *found->refresh;
            if (ready.ok || !found->ok) {
                found->ok = ready.ok; found->printed_full = ready.printed_full;
                found->pixels = std::move(ready.pixels);
            }
            found->updated = ready.updated;
            found->refresh.reset();
        }
        if (same_geometry && now >= found->updated && now - found->updated >= interval &&
            scratch.async_legacy_refresh && found->printed_full && !found->refresh) {
            auto job = std::make_shared<PrintedLayerRefresh>();
            job->layer = layer; job->hit = hit; job->needs_full = needs_full;
            job->printed_full = found->printed_full;
            auto* owner = new (std::nothrow) std::shared_ptr<PrintedLayerRefresh>(job);
            const bool reserved = owner && ReserveLegacyRefresh();
            if (reserved && TrySubmitThreadpoolCallback([](PTP_CALLBACK_INSTANCE, void* context) noexcept {
                struct Pending { ~Pending() { pending_legacy_refreshes.fetch_sub(1, std::memory_order_relaxed); } } pending;
                std::unique_ptr<std::shared_ptr<PrintedLayerRefresh>> owner(static_cast<std::shared_ptr<PrintedLayerRefresh>*>(context));
                auto& job = **owner;
                try {
                    // Isolated DCs and buffers: PrintWindow may block on another
                    // process, but never blocks the WGC composition worker.
                    HDC screen = GetDC(nullptr);
                    struct Release { HDC dc; ~Release() { if (dc) ReleaseDC(nullptr, dc); } } release{screen};
                    Scratch temp;
                    if (screen && IsWindow(job.layer.hwnd) && temp.window.Ensure(screen,
                        job.hit.right - job.hit.left, job.hit.bottom - job.hit.top)) {
                        ComposeMetrics ignored{};
                        job.ok = CopyPrintedLayer(temp, screen, job.layer, job.hit, job.needs_full, ignored);
                        if (!temp.printed_layers.empty()) {
                            auto& result = temp.printed_layers.back();
                            job.pixels = std::move(result.pixels);
                            job.printed_full = result.printed_full;
                        }
                    }
                } catch (...) { job.ok = false; }
                job.updated = GetTickCount64();
                job.done.store(true, std::memory_order_release);
            }, owner, nullptr)) found->refresh = std::move(job);
            else {
                if (reserved) pending_legacy_refreshes.fetch_sub(1, std::memory_order_relaxed);
                delete owner;
                found->updated = now; // retain the last frame and retry later if the pool is busy
            }
        }
        if (same_geometry && (found->refresh || now < found->updated || now - found->updated < interval)) {
            metrics.any_full_print |= found->printed_full;
            metrics.any_fast_print |= !found->printed_full;
            if (found->ok) std::memcpy(scratch.window.bits, found->pixels.data(), found->pixels.size() * 4);
            return found->ok;
        }
    }
    if (found == cache.end()) {
        if (cache.size() >= kMaxWindows) {
            cache.erase(std::min_element(cache.begin(), cache.end(), [](const auto& a, const auto& b) { return a.used < b.used; }));
        }
        cache.emplace_back();
        found = std::prev(cache.end());
    }
    auto& entry = *found;
    entry.hwnd = layer.hwnd; entry.pid = pid; entry.thread = thread;
    entry.window = layer.window; entry.hit = hit; entry.needs_full = needs_full;
    entry.used = now; entry.ok = false;
    entry.refresh.reset();
    const int ww = layer.window.right - layer.window.left, wh = layer.window.bottom - layer.window.top;
    const int ox = hit.left - layer.window.left, oy = hit.top - layer.window.top;
    const auto print = [&](bool full) {
        Dib& captured = full ? scratch.full_window : scratch.window;
        entry.printed_full = full;
        if (full) metrics.any_full_print = true;
        else metrics.any_fast_print = true;
        if (!captured.Ensure(screen, full ? ww : hw, full ? wh : hh)) return false;
        const int sx = full ? ox : 0, sy = full ? oy : 0;
        std::memset(static_cast<uint32_t*>(captured.bits) + static_cast<size_t>(sy) * captured.width,
            0, static_cast<size_t>(hh) * captured.width * 4);
        if (!full) SetViewportOrgEx(captured.dc, -ox, -oy, nullptr);
        const auto started = std::chrono::steady_clock::now();
        const bool printed = PrintWindow(layer.hwnd, captured.dc, full ? PW_RENDERFULLCONTENT : 0) != FALSE;
        if (!full) SetViewportOrgEx(captured.dc, 0, 0, nullptr);
        GdiFlush();
        capture::debug_info[full ? 15 : 14] += static_cast<int>(std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - started).count());
        ++capture::debug_info[full ? 17 : 16];
        if (!printed) return false;
        const auto* pixels = static_cast<const uint32_t*>(captured.bits);
        if (!InspectLayerHit(pixels, captured.width, sx, sy, hw, hh).any_pixel) return false;
        if (full) {
            for (int row = 0; row < hh; ++row)
                std::memcpy(static_cast<uint32_t*>(scratch.window.bits) + static_cast<size_t>(row) * hw,
                    pixels + static_cast<size_t>(sy + row) * captured.width + sx, static_cast<size_t>(hw) * 4);
        }
        return true;
    };
    entry.ok = print(needs_full);
    if (!entry.ok && !needs_full) entry.ok = print(true);
    entry.updated = GetTickCount64();
    if (entry.ok) {
        const auto* pixels = static_cast<const uint32_t*>(scratch.window.bits);
        entry.pixels.assign(pixels, pixels + static_cast<size_t>(hw) * hh);
    } else entry.pixels.clear();
    return entry.ok;
}

bool CompositeWindowLayer(Scratch& scratch, HDC screen, const RECT& area, int width, Layer& layer, ComposeMetrics& metrics, bool* out_all_opaque = nullptr) {
    layer.from_wgc = false;
    // Copy only the visible frame; the invisible resize border prints black.
    RECT hit{};
    if (!IntersectRect(&hit, &layer.visible, &area)) return false;
    const int ww = layer.window.right - layer.window.left;
    const int wh = layer.window.bottom - layer.window.top;
    if (ww <= 0 || wh <= 0) return false;
    const int ox = hit.left - layer.window.left;
    const int oy = hit.top - layer.window.top;
    const int hw = hit.right - hit.left;
    const int hh = hit.bottom - hit.top;
    if (ox < 0 || oy < 0 || ox + hw > ww || oy + hh > wh) return false;
    // PW_RENDERFULLCONTENT forces an expensive synchronous application redraw.
    // Ordinary printing can copy its current redirected frame without that wait.
    const bool needs_full = std::find(scratch.full_content.begin(), scratch.full_content.end(), layer.hwnd) != scratch.full_content.end();
    capture::WindowCaptureCache::PixelRegion source{};
    // DirectComposition tool windows (including MyDockFinder's top bar) have
    // no GDI redirection bitmap. WGC captures their actual DWM visual, while
    // PrintWindow may succeed with an empty image. Try WGC for these too;
    // keep the legacy overlay validation only for the print fallback.
    const bool live_frame = scratch.use_window_capture &&
        scratch.window_capture.ReadWindow(layer.hwnd, hit, layer.visible, layer.window, source);
    if (live_frame) {
        layer.from_wgc = true;
        metrics.any_live_wgc = true;
        if (source.fresh) metrics.any_fresh_wgc = true;
    }
    const bool previously_live = TrackLiveCapture(scratch, layer.hwnd, live_frame);
    if (!live_frame && scratch.use_window_capture &&
        (previously_live || scratch.window_capture.HasWindow(layer.hwnd))) {
        // A WGC pool can be empty during resize/recovery. PrintWindow often
        // returns a successful grey/black placeholder for Chromium at that point.
        // Let the compositor retain its last complete strip until WGC resumes.
        return false;
    }
    if (!live_frame) {
        if (!scratch.window.Ensure(screen, hw, hh) ||
            !CopyPrintedLayer(scratch, screen, layer, hit, needs_full, metrics)) return false;
        source.pixels = static_cast<const uint32_t*>(scratch.window.bits);
        source.stride = hw;
    }

    // Windows that draw with alpha (or nothing at all) print as black in the
    // transparent parts, so inspect the pixels instead of copying blindly.
    HitPixelSummary summary = InspectLayerHit(source.pixels, source.stride, 0, 0, hw, hh);
    if (!summary.any_pixel) return false; // fully transparent window
    if (out_all_opaque) *out_all_opaque = summary.all_opaque;

    const int dst_x = hit.left - area.left;
    const int dst_y = hit.top - area.top;
    auto* target = static_cast<uint32_t*>(scratch.target.bits);
    if (!summary.needs_blend) {
        for (int row = 0; row < hh; ++row)
            std::memcpy(target + static_cast<size_t>(dst_y + row) * width + dst_x,
                source.pixels + static_cast<size_t>(row) * source.stride, static_cast<size_t>(hw) * 4);
    } else {
        BlendPremultipliedHit(source.pixels, source.stride, 0, 0, target, width, dst_x, dst_y, hw, hh);
    }
    return true;
}

// Composes `area` into out (area-sized, opaque BGRA) and reports capture metrics
// so the strip cache can pick an appropriate refresh TTL without spinning.
bool Compose(Scratch& scratch, const RECT& area, std::vector<uint32_t>& out, ComposeMetrics& out_metrics) {
    struct DebugTiming {
        std::chrono::steady_clock::time_point started = std::chrono::steady_clock::now();
        ~DebugTiming() { capture::debug_info[20] += static_cast<int>(std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - started).count()); ++capture::debug_info[21]; }
    } debug_timing;
    const int width = area.right - area.left, height = area.bottom - area.top;
    HDC screen = GetDC(nullptr);
    if (!screen) return false;
    struct Release { HDC dc; ~Release() noexcept { ReleaseDC(nullptr, dc); } } release{ screen };
    if (!scratch.target.Ensure(screen, width, height)) return false;

    const ULONGLONG check_now = GetTickCount64();
    scratch.overlay_checks.erase(std::remove_if(scratch.overlay_checks.begin(), scratch.overlay_checks.end(),
        [&](const OverlayCheck& c) { return check_now >= c.until || !IsWindow(c.hwnd); }), scratch.overlay_checks.end());
    const auto forget = [](HWND h) {
        return !IsWindow(h) || IsUnredirectedToolOverlay(GetWindowLongPtrW(h, GWL_EXSTYLE));
    };
    scratch.bad.erase(std::remove_if(scratch.bad.begin(), scratch.bad.end(), forget), scratch.bad.end());
    scratch.verified.erase(std::remove_if(scratch.verified.begin(), scratch.verified.end(), forget), scratch.verified.end());
    scratch.full_content.erase(std::remove_if(scratch.full_content.begin(), scratch.full_content.end(), forget), scratch.full_content.end());

    const DWORD self_pid = GetCurrentProcessId();
    bool had_full_print = false;
    bool complete = false;
    // Each collected layer may need one compatibility retry and one rejection.
    // Leave a final pass to compose after the last rejected layer is removed.
    for (int attempt = 0; attempt < kMaxWindows * 2 + 1; ++attempt) {
        ComposeMetrics metrics{};
        metrics.any_full_print = had_full_print;
        EnumState state;
        state.area = area;
        state.self_pid = self_pid;
        state.bad = &scratch.bad;
        state.overlay_checks = &scratch.overlay_checks;
        const auto debug_start = std::chrono::steady_clock::now();
        EnumWindows(CollectLayer, reinterpret_cast<LPARAM>(&state));
        capture::debug_info[26] += static_cast<int>(std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - debug_start).count());
        ++capture::debug_info[27];
        capture::debug_info[22] = state.count;

        // Fast single-layer shortcut when the topmost window completely covers `area`:
        // avoids PaintDesktop + PrintWindow on buried background windows underneath it.
        bool covered = false;
        if (state.count > 0 && Contains(state.layers[0].visible, area)) {
            bool top_opaque = false;
            const bool captured = CompositeWindowLayer(scratch, screen, area, width, state.layers[0], metrics, &top_opaque);
            if (!captured && !MayOmitFailedLayer(scratch, state.layers[0].hwnd)) return false;
            if (captured && top_opaque) {
                metrics.static_only = false;
                covered = true;
            }
        }

        bool ok = covered;
        if (!covered) {
            // PaintDesktop rebuilds the wallpaper even when applications cover
            // it. Retain the same bounded static lifetime as a desktop-only strip.
            // Paint a padded area and serve sub-rects from it: while the notch
            // animates, `area` changes every frame and repainting each time made
            // this the slowest step. Same wallpaper pixels, copied 1:1.
            const ULONGLONG now = GetTickCount64();
            const bool cached = scratch.wallpaper.bits && Contains(scratch.wallpaper_area, area) &&
                now - scratch.wallpaper_tick < kWallpaperTtlMs;
            ok = true;
            if (!cached) {
                constexpr LONG kWallpaperPad = 160;
                const LONG vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
                const LONG vr = vx + GetSystemMetrics(SM_CXVIRTUALSCREEN), vb = vy + GetSystemMetrics(SM_CYVIRTUALSCREEN);
                RECT padded{ std::max(vx, area.left - kWallpaperPad), std::max(vy, area.top - kWallpaperPad),
                    std::min(vr, area.right + kWallpaperPad), std::min(vb, area.bottom + kWallpaperPad) };
                // The virtual screen always contains `area`; keep it if metrics disagree.
                if (!Contains(padded, area)) padded = area;
                const int pw = padded.right - padded.left, ph = padded.bottom - padded.top;
                ok = scratch.wallpaper.Ensure(screen, pw, ph);
                if (ok) {
                    // PaintDesktop treats DC coordinates as screen coordinates.
                    SetViewportOrgEx(scratch.wallpaper.dc, -padded.left, -padded.top, nullptr);
                    ok = PaintDesktop(scratch.wallpaper.dc) != FALSE;
                    SetViewportOrgEx(scratch.wallpaper.dc, 0, 0, nullptr);
                    GdiFlush();
                }
                if (ok) { scratch.wallpaper_area = padded; scratch.wallpaper_tick = now; }
                else scratch.wallpaper_area = {};
            }
            if (ok) {
                const int ox = area.left - scratch.wallpaper_area.left, oy = area.top - scratch.wallpaper_area.top;
                const auto* src = static_cast<const uint8_t*>(scratch.wallpaper.bits);
                auto* dst = static_cast<uint8_t*>(scratch.target.bits);
                const size_t srcStride = static_cast<size_t>(scratch.wallpaper.width) * 4, rowBytes = static_cast<size_t>(width) * 4;
                for (int row = 0; row < height; ++row)
                    std::memcpy(dst + row * rowBytes, src + (static_cast<size_t>(oy) + row) * srcStride + static_cast<size_t>(ox) * 4, rowBytes);
            }

            if (!ok) return false;
            int start_idx = state.count - 1;
            for (int i = 0; i < state.count; ++i) {
                if (Contains(state.layers[i].visible, area)) {
                    start_idx = i;
                    break;
                }
            }
            for (int i = start_idx; i >= 0; --i) {
                if (CompositeWindowLayer(scratch, screen, area, width, state.layers[i], metrics)) {
                    metrics.static_only = false;
                    ok = true;
                } else if (!MayOmitFailedLayer(scratch, state.layers[i].hwnd)) {
                    // Wallpaper or lower windows are not a valid replacement
                    // for an opaque window whose capture is temporarily missing.
                    return false;
                }
            }
        }
        had_full_print = had_full_print || metrics.any_full_print;
        if (!ok) return false;
        if (state.count > 0) {
            if (HWND bad = FindMismatchedLayer(screen, scratch.target, area, state, scratch)) {
                const LONG_PTR bad_ex = GetWindowLongPtrW(bad, GWL_EXSTYLE);
                const bool is_overlay = IsUnredirectedToolOverlay(bad_ex);
                const bool can_be_ghost_overlay = is_overlay ||
                    ((bad_ex & (WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP | WS_EX_TRANSPARENT)) != 0);
                if (!is_overlay &&
                    std::find(scratch.full_content.begin(), scratch.full_content.end(), bad) == scratch.full_content.end()) {
                    scratch.full_content.push_back(bad);
                    continue;
                }
                if (!can_be_ghost_overlay) {
                    // Real overlapped app windows are never invisible shell overlays;
                    // a post-PW_RENDERFULLCONTENT diff means live animation occurred between snapshots.
                    scratch.verified.push_back(bad);
                    out_metrics = metrics;
                    complete = true;
                    break;
                }
                if (is_overlay) {
                    scratch.overlay_checks.push_back({ bad, GetTickCount64() + kOverlayCacheTtlMs, true });
                } else {
                    scratch.bad.push_back(bad);
                }
                continue; // compose again without it
            }
        }
        out_metrics = metrics;
        complete = true;
        break;
    }

    if (!complete) return false;
    const size_t total = static_cast<size_t>(width) * static_cast<size_t>(height);
    out.resize(total);
    const auto* src = static_cast<const uint32_t*>(scratch.target.bits);
    for (size_t p = 0; p < total; ++p) out[p] = src[p] | 0xFF000000u;
    return true;
}

[[maybe_unused]] bool Compose(Scratch& scratch, const RECT& area, std::vector<uint32_t>& out, bool& static_only) {
    ComposeMetrics metrics{};
    const bool ok = Compose(scratch, area, out, metrics);
    static_only = metrics.static_only;
    return ok;
}

int64_t NowSteadyMs() noexcept {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

// Moderate padding around each active glass area so hover/resize transitions stay
// inside the cached strip while D3D11 staging readbacks remain small.
RECT StripFor(const RECT& area) noexcept {
    RECT strip = area;
    if (HMONITOR monitor = MonitorFromRect(&area, MONITOR_DEFAULTTONEAREST)) {
        MONITORINFO info{ sizeof(info) };
        if (GetMonitorInfoW(monitor, &info)) {
            strip = {
                std::max<LONG>(info.rcMonitor.left, area.left - 128),
                std::max<LONG>(info.rcMonitor.top, area.top - 32),
                std::min<LONG>(info.rcMonitor.right, area.right + 128),
                std::min<LONG>(info.rcMonitor.bottom, area.bottom + 32)
            };
            UnionRect(&strip, &strip, &area);
        }
    }
    return strip;
}

int64_t RectPixels(const RECT& r) noexcept {
    return static_cast<int64_t>(std::max<LONG>(0, r.right - r.left)) *
           static_cast<int64_t>(std::max<LONG>(0, r.bottom - r.top));
}

struct StripSlot {
    HMONITOR monitor = nullptr;
    RECT area{};
    std::vector<uint32_t> pixels;
    int64_t updated_ms = 0;
    int64_t last_access_ms = 0;
    int64_t ttl_ms = 16;
    bool valid = false;

    RECT next_wanted{};
    bool has_next_wanted = false;
    bool queued = false;
    bool in_flight = false;
    uint64_t request_gen = 0;
    uint64_t completed_gen = 0;
    uint64_t publication = 0;
    uint64_t capture_version = 0;
    bool live_capture = false;
};

bool NeedsLiveRefresh(const StripSlot& slot, uint64_t version, int64_t now) noexcept {
    return slot.valid && slot.live_capture && !slot.in_flight && slot.capture_version != version &&
        now - slot.last_access_ms <= 100;
}

struct Shared {
    SRWLOCK lock = SRWLOCK_INIT;
    CONDITION_VARIABLE done = CONDITION_VARIABLE_INIT;
    std::array<StripSlot, kMaxStrips> slots{};
    HANDLE wake = nullptr;
    bool worker_started = false;
    uint64_t global_seq = 0;
};
// shortcut: process-lifetime state with a detached worker, never joined; fine for a single-process DLL, revisit if the DLL is ever unloaded at runtime.
Shared g_shared;

struct SrwExclusiveGuard {
    SRWLOCK* lock;
    explicit SrwExclusiveGuard(SRWLOCK& l) noexcept : lock(&l) { AcquireSRWLockExclusive(lock); }
    ~SrwExclusiveGuard() noexcept { if (lock) ReleaseSRWLockExclusive(lock); }
    SrwExclusiveGuard(const SrwExclusiveGuard&) = delete;
    SrwExclusiveGuard& operator=(const SrwExclusiveGuard&) = delete;
};

int64_t ChooseStripTtlMs(const ComposeMetrics& m) noexcept {
    if (m.static_only) return 80;
    // FrameArrived refreshes live strips immediately. Poll only as a fallback
    // for z-order/geometry changes, avoiding duplicate GPU readbacks between events.
    if (m.any_live_wgc) return 16;
    if (m.any_full_print) return kFullPrintIntervalMs;
    return 16;
}

void WorkerMain() noexcept {
    Scratch scratch;
    scratch.async_legacy_refresh = true;
    scratch.window_capture.SetFrameWakeEvent(g_shared.wake);
    std::vector<uint32_t> pixels;
    int64_t next_cleanup = 0;
    for (;;) {
        const int64_t now = NowSteadyMs();
        if (now >= next_cleanup) {
            // FrameArrived can keep waking us after a consumer closes, so cleanup
            // must not depend on the event wait timing out.
            scratch.window_capture.ExpireUnused();
            next_cleanup = now + 500;
        }
        const uint64_t capture_version = scratch.window_capture.FrameVersion();
        int pick = -1;
        RECT wanted{};
        uint64_t gen = 0;
        uint64_t publication = 0;
        {
            SrwExclusiveGuard guard(g_shared.lock);
            uint64_t best_gen = UINT64_MAX;
            for (int i = 0; i < kMaxStrips; ++i) {
                auto& s = g_shared.slots[i];
                if (s.queued && !s.in_flight && s.request_gen < best_gen) {
                    best_gen = s.request_gen;
                    pick = i;
                }
            }
            if (pick < 0) {
                uint64_t oldest_version = UINT64_MAX;
                for (int i = 0; i < kMaxStrips; ++i) {
                    const auto& s = g_shared.slots[i];
                    if (NeedsLiveRefresh(s, capture_version, now) && s.capture_version < oldest_version) {
                        oldest_version = s.capture_version;
                        pick = i;
                    }
                }
            }
            if (pick >= 0) {
                auto& s = g_shared.slots[pick];
                wanted = s.has_next_wanted ? s.next_wanted : s.area;
                s.has_next_wanted = false;
                s.queued = false;
                s.in_flight = true;
                gen = s.request_gen;
                publication = ++g_shared.global_seq;
            }
        }

        if (pick < 0) {
            // Window capture sessions must also stop when the last glass consumer
            // disappears; otherwise a hidden notch would keep capturing at full FPS.
            WaitForSingleObject(g_shared.wake, scratch.window_capture.HasEntries() ? 500 : INFINITE);
            continue;
        }

        ComposeMetrics metrics{};
        bool ok = false;
        try { ok = Compose(scratch, wanted, pixels, metrics); } catch (...) { ok = false; }

        {
            SrwExclusiveGuard guard(g_shared.lock);
            auto& s = g_shared.slots[pick];
            if (ok) {
                s.pixels.swap(pixels);
                s.publication = publication;
                s.area = wanted;
                s.updated_ms = NowSteadyMs();
                s.ttl_ms = ChooseStripTtlMs(metrics);
                s.valid = true;
                s.live_capture = metrics.any_live_wgc;
            }
            s.capture_version = capture_version;
            if (gen > s.completed_gen) s.completed_gen = gen;
            s.in_flight = false;
            WakeAllConditionVariable(&g_shared.done);
        }
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

void CopyCrop(const StripSlot& strip, const RECT& area, uint32_t* dest) noexcept {
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

template<class Ready>
void WaitSlotUntil(Shared& shared, int64_t budget_ms, Ready ready) noexcept {
    if (budget_ms <= 0) return;
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(budget_ms);
    while (!ready()) {
        const auto now = std::chrono::steady_clock::now();
        if (now >= deadline) break;
        const auto rem = std::chrono::duration_cast<std::chrono::milliseconds>(
            deadline - now + std::chrono::microseconds(999)).count();
        const DWORD wait_ms = static_cast<DWORD>(rem > 0 ? rem : 1);
        if (!SleepConditionVariableSRW(&shared.done, &shared.lock, wait_ms, 0) &&
            GetLastError() == ERROR_TIMEOUT) break;
    }
}

} // namespace
} // namespace vnotch::controllers::backdrop

namespace bd = vnotch::controllers::backdrop;

// [DEBUG-glass-freshness]
VN_EXPORT int32_t __stdcall vn_backdrop_freshness_debug(intptr_t hwnd, int32_t* values) noexcept {
    if (!values) return E_POINTER;
    bd::capture::debug_target = hwnd;
    for (int i = 0; i < 28; ++i) values[i] = bd::capture::debug_info[i].load();
    return 0;
}

// Fills destination (width*height top-down BGRA) with the desktop under the
// given screen rect, excluding this process's windows. A consumer may wait
// briefly for a newer publication. Only the worker runs D3D11/PrintWindow.
VN_EXPORT int32_t __stdcall vn_backdrop_composite_frame(
    int32_t x, int32_t y, int32_t width, int32_t height, void* destination,
    uint64_t* publication, int32_t wait_ms, uint8_t* out_ok) noexcept {
    if (!out_ok) return VN_ERR_INVALID_ARG;
    *out_ok = 0;
    if (!destination || width <= 0 || height <= 0 || width > 16384 || height > 16384) return VN_ERR_INVALID_ARG;
    try {
        const RECT area{ x, y, x + width, y + height };
        auto* dest = static_cast<uint32_t*>(destination);
        auto& shared = bd::g_shared;
        const RECT tight = bd::StripFor(area);
        const HMONITOR mon = MonitorFromRect(&area, MONITOR_DEFAULTTONEAREST);

        bd::SrwExclusiveGuard guard(shared.lock);
        if (!bd::EnsureWorker()) return VN_ERR_FAIL;
        const int64_t now_ms = bd::NowSteadyMs();
        // A new consumer (or one returning from another capture source) must
        // wait for composition started after the handoff, including on retries.
        if (publication && !*publication) *publication = ++shared.global_seq;
        const uint64_t minimum = publication ? *publication : 0;
        const auto usable = [&](const bd::StripSlot& s) {
            return s.monitor == mon && s.valid && s.publication >= minimum && bd::Contains(s.area, area);
        };

        // Widening and moving two glass hosts can leave overlapping strips.
        // Read the newest covering publication, regardless of slot order.
        const auto newestCovering = [&]() {
            bd::StripSlot* newest = nullptr;
            for (auto& s : shared.slots) {
                if (usable(s) && (!newest || s.publication > newest->publication)) newest = &s;
            }
            return newest;
        };
        if (auto* newest = newestCovering()) {
            auto& slot = *newest;

            slot.last_access_ms = now_ms;
            if (!slot.has_next_wanted) {
                slot.next_wanted = tight;
                slot.has_next_wanted = true;
            } else {
                RECT merged{};
                UnionRect(&merged, &slot.next_wanted, &tight);
                if (bd::RectPixels(merged) <= bd::kMaxMergedStripPixels) {
                    slot.next_wanted = merged;
                }
            }

            const int64_t age_ms = now_ms - slot.updated_ms;
            uint64_t wait_gen = 0;
            if (age_ms >= slot.ttl_ms && !slot.queued && !slot.in_flight) {
                slot.queued = true;
                slot.request_gen = ++shared.global_seq;
                wait_gen = slot.request_gen;
                SetEvent(shared.wake);
            } else if (slot.queued || slot.in_flight) {
                wait_gen = slot.request_gen;
            }

            // When the consumer clock lands just ahead of WGC, let the worker
            // finish the new strip within this frame's budget. Each consumer
            // tracks its own publication, even when two lenses share a strip.
            if (wait_ms > 0 && publication && slot.live_capture && slot.publication == *publication) {
                bd::WaitSlotUntil(shared, std::min(wait_ms, 8), [&] {
                    auto* current = newestCovering();
                    return current && current->publication > minimum;
                });
            } else if (age_ms >= 25 && wait_gen > 0) {
                bd::WaitSlotUntil(shared, 2, [&] { return slot.completed_gen >= wait_gen; });
            }

            // The condition wait releases the lock. Another covering strip
            // may now hold a more recent publication than the one we waited on.
            if (auto* current = newestCovering()) {
                bd::CopyCrop(*current, area, dest);
                if (publication) *publication = current->publication;
                *out_ok = 1;
                return VN_OK;
            }
        }

        // 2. Cold start or geometry expanded outside all current strips:
        // assign/widen a strip slot and wait briefly on the single background worker.
        int idx = -1;
        for (int i = 0; i < bd::kMaxStrips; ++i) {
            auto& s = shared.slots[i];
            if (s.monitor != mon) continue;
            const RECT ref = s.has_next_wanted ? s.next_wanted : s.area;
            RECT hit{}, merged{};
            if ((s.valid || s.queued || s.in_flight) && IntersectRect(&hit, &ref, &tight)) {
                UnionRect(&merged, &ref, &tight);
                if (bd::RectPixels(merged) <= bd::kMaxMergedStripPixels) {
                    idx = i;
                    break;
                }
            }
        }
        if (idx < 0) {
            for (int i = 0; i < bd::kMaxStrips; ++i) {
                const auto& s = shared.slots[i];
                if (!s.valid && !s.queued && !s.in_flight) { idx = i; break; }
            }
        }
        if (idx < 0) {
            int64_t oldest = INT64_MAX;
            idx = 0;
            for (int i = 0; i < bd::kMaxStrips; ++i) {
                if (shared.slots[i].last_access_ms < oldest) {
                    oldest = shared.slots[i].last_access_ms;
                    idx = i;
                }
            }
        }

        auto& slot = shared.slots[idx];
        slot.monitor = mon;
        slot.last_access_ms = now_ms;
        RECT wanted = tight;
        if (slot.valid) {
            RECT hit{}, merged{};
            if (IntersectRect(&hit, &slot.area, &tight)) {
                UnionRect(&merged, &slot.area, &tight);
                if (bd::RectPixels(merged) <= bd::kMaxMergedStripPixels) wanted = merged;
            }
        }
        if (slot.has_next_wanted) {
            RECT hit{}, merged{};
            if (IntersectRect(&hit, &slot.next_wanted, &wanted)) {
                UnionRect(&merged, &slot.next_wanted, &wanted);
                if (bd::RectPixels(merged) <= bd::kMaxMergedStripPixels) wanted = merged;
            }
        }
        slot.next_wanted = wanted;
        slot.has_next_wanted = true;
        if (!slot.queued) {
            slot.queued = true;
            slot.request_gen = ++shared.global_seq;
            SetEvent(shared.wake);
        }
        const uint64_t req = slot.request_gen;
        bd::WaitSlotUntil(shared, 15, [&] { return slot.completed_gen >= req; });
        if (auto* current = newestCovering()) {
            bd::CopyCrop(*current, area, dest);
            if (publication) *publication = current->publication;
            *out_ok = 1;
        }
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}
