#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <stdexcept>
#include <atomic>

// Deterministic legacy source; no visible windows or desktop pixels required.
std::atomic<unsigned> printCalls{0};
COLORREF printColor = RGB(32, 96, 160);
bool fullOnly = true, printFailure = false;
HANDLE printEntered = nullptr, printRelease = nullptr;
BOOL WINAPI CadencePrintWindow(HWND hwnd, HDC dc, UINT flags) {
    ++printCalls;
    if (printEntered) { SetEvent(printEntered); WaitForSingleObject(printRelease, 1000); }
    if (printFailure || (fullOnly && !(flags & 2))) return FALSE;
    RECT rect{};
    GetWindowRect(hwnd, &rect);
    OffsetRect(&rect, -rect.left, -rect.top);
    HBRUSH brush = CreateSolidBrush(printColor);
    FillRect(dc, &rect, brush);
    DeleteObject(brush);
    return TRUE;
}
#define PrintWindow CadencePrintWindow
#include "../../Native/Controllers/BackdropComposite.cpp"
#undef PrintWindow

void Require(bool ok, const char* message) {
    if (!ok) throw std::runtime_error(message);
}

int main() {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    HWND hwnd = CreateWindowExW(0, L"STATIC", L"Cadence probe", WS_POPUP,
        100, 100, 80, 60, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    HDC screen = GetDC(nullptr);
    if (!hwnd || !screen) return 1;
    struct Cleanup { HWND hwnd; HDC dc; ~Cleanup() { ReleaseDC(nullptr, dc); DestroyWindow(hwnd); } } cleanup{hwnd, screen};
    try {
        bd::StripSlot slot{};
        slot.valid = slot.live_capture = true; slot.capture_version = 10; slot.last_access_ms = 1000;
        Require(bd::NeedsLiveRefresh(slot, 11, 1050), "new source frame did not wake a live strip");
        Require(!bd::NeedsLiveRefresh(slot, 10, 1050), "unchanged source caused a busy loop");
        Require(!bd::NeedsLiveRefresh(slot, 11, 1101), "closed consumer kept composing");
        slot.in_flight = true;
        Require(!bd::NeedsLiveRefresh(slot, 11, 1050), "source event overlapped an in-flight composition");
        slot.in_flight = false; slot.live_capture = false;
        Require(!bd::NeedsLiveRefresh(slot, 11, 1050), "unrelated source bypassed legacy-only pacing");
        bd::ComposeMetrics mixed{};
        mixed.static_only = false; mixed.any_live_wgc = true; mixed.any_full_print = true;
        Require(bd::ChooseStripTtlMs(mixed) == 16, "legacy layer throttled live WGC fallback polling");
        mixed.any_fresh_wgc = true;
        slot.live_capture = true;
        Require(bd::NeedsLiveRefresh(slot, 11, 1001),
            "fresh source frame waited for the fallback polling interval");
        mixed.any_live_wgc = false;
        Require(bd::ChooseStripTtlMs(mixed) == 66, "legacy-only capture lost its budget");
        mixed.static_only = true;
        Require(bd::ChooseStripTtlMs(mixed) == 80, "static wallpaper lost idle suppression");

        bd::Scratch scratch;
        scratch.use_window_capture = false;
        bd::Layer layer{hwnd};
        GetWindowRect(hwnd, &layer.window); layer.visible = layer.window;
        RECT hit{110, 112, 130, 128};
        Require(scratch.window.Ensure(screen, 20, 16), "crop allocation failed");
        bd::ComposeMetrics metrics{};
        Require(bd::CopyPrintedLayer(scratch, screen, layer, hit, false, metrics), "initial full print failed");
        Require(printCalls == 2 && metrics.any_full_print, "full-content fallback was not exercised");
        const uint32_t first = static_cast<uint32_t*>(scratch.window.bits)[0];
        Require((first & 0xffffff) == 0x2060a0, "full-window crop has wrong pixels");
        printColor = RGB(180, 96, 160);
        Require(bd::CopyPrintedLayer(scratch, screen, layer, hit, false, metrics), "cached print failed");
        Require(printCalls == 2 && static_cast<uint32_t*>(scratch.window.bits)[0] == first,
            "live strip needlessly reprinted its legacy layer");
        scratch.printed_layers.back().updated -= 66;
        Require(bd::CopyPrintedLayer(scratch, screen, layer, hit, false, metrics), "expired layer did not refresh");
        Require(printCalls == 4 && static_cast<uint32_t*>(scratch.window.bits)[0] != first, "legacy content froze");

        OffsetRect(&hit, 1, 0);
        Require(bd::CopyPrintedLayer(scratch, screen, layer, hit, false, metrics) && printCalls == 6,
            "moving crop reused pixels from the old location");
        Require(bd::CopyPrintedLayer(scratch, screen, layer, hit, true, metrics) && printCalls == 7,
            "full-content promotion reused the incompatible cache");
        layer.window.right++;
        Require(bd::CopyPrintedLayer(scratch, screen, layer, hit, true, metrics) && printCalls == 8,
            "resized window reused stale geometry");
        Require(scratch.printed_layers.size() == 1, "moving/resizing accumulated cached bitmaps");

        scratch.printed_layers.clear(); printFailure = true;
        Require(!bd::CopyPrintedLayer(scratch, screen, layer, hit, true, metrics), "failed capture reported success");
        unsigned failedCalls = printCalls;
        Require(!bd::CopyPrintedLayer(scratch, screen, layer, hit, true, metrics) && printCalls == failedCalls,
            "unavailable legacy source was retried every frame");
        printFailure = false; scratch.printed_layers.back().updated -= 66;
        Require(bd::CopyPrintedLayer(scratch, screen, layer, hit, true, metrics), "failed capture never recovered");

        scratch.printed_layers.clear(); fullOnly = false;
        Require(bd::CopyPrintedLayer(scratch, screen, layer, hit, false, metrics), "ordinary print failed");
        const unsigned fastCalls = printCalls;
        scratch.printed_layers.back().updated -= 16;
        Require(bd::CopyPrintedLayer(scratch, screen, layer, hit, false, metrics) && printCalls == fastCalls + 1,
            "ordinary print was throttled to full-content cadence");

        // Hold the legacy source inside PrintWindow. The composition caller must
        // keep returning its last good crop while the independent request waits.
        scratch.printed_layers.clear(); fullOnly = true;
        Require(bd::CopyPrintedLayer(scratch, screen, layer, hit, true, metrics), "async fixture failed");
        scratch.async_legacy_refresh = true;
        scratch.printed_layers.back().updated -= 66;
        printEntered = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        printRelease = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        const auto before = GetTickCount64();
        Require(bd::CopyPrintedLayer(scratch, screen, layer, hit, true, metrics), "pending refresh lost last good crop");
        Require(GetTickCount64() - before < 100, "legacy source blocked live composition");
        const bool entered = WaitForSingleObject(printEntered, 1000) == WAIT_OBJECT_0;
        auto job = scratch.printed_layers.back().refresh;
        SetEvent(printRelease);
        Require(entered && job != nullptr, "background legacy refresh did not start");
        for (int i = 0; i < 1000 && !job->done.load(std::memory_order_acquire); ++i) Sleep(1);
        Require(job->done.load(std::memory_order_acquire), "background legacy refresh did not complete");
        CloseHandle(printEntered); CloseHandle(printRelease);
        printEntered = printRelease = nullptr;
        Require(bd::CopyPrintedLayer(scratch, screen, layer, hit, true, metrics) && !scratch.printed_layers.back().refresh,
            "completed legacy refresh was not consumed");
        puts("PASS: mixed capture cadence, bounded legacy refresh, crop/resize/promotion invalidation and recovery.");
        return 0;
    } catch (const std::exception& ex) { printf("FAIL: %s\n", ex.what()); return 1; }
}
