#define NOMINMAX
#include <windows.h>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <vector>

// Treat this probe's source window as an external app. Capture still uses the
// production compositor, including its worker, strip cache and PrintWindow.
DWORD WINAPI MotionProbeOwnerPid() { return MAXDWORD; }
std::atomic<unsigned> printCalls{0};
std::atomic<long long> printMicroseconds{0};
std::atomic<unsigned> printMode{0};
BOOL WINAPI MotionProbePrintWindow(HWND hwnd, HDC dc, UINT flags) {
    const auto start = std::chrono::steady_clock::now();
    BOOL result;
    if (flags == 0 && printMode.load() != 0) {
        RECT client{};
        GetClientRect(hwnd, &client);
        HBRUSH brush = CreateSolidBrush(printMode.load() == 1 ? RGB(0, 0, 0) : RGB(240, 0, 0));
        FillRect(dc, &client, brush);
        DeleteObject(brush);
        result = TRUE;
    } else result = ::PrintWindow(hwnd, dc, flags);
    printMicroseconds.fetch_add(std::chrono::duration_cast<std::chrono::microseconds>(
        std::chrono::steady_clock::now() - start).count());
    printCalls.fetch_add(1);
    return result;
}
#define GetCurrentProcessId MotionProbeOwnerPid
#define PrintWindow MotionProbePrintWindow
#include "../../Native/Controllers/BackdropComposite.cpp"
#undef GetCurrentProcessId
#undef PrintWindow

std::atomic<unsigned> sourceFrame{0};

COLORREF FrameColor(unsigned frame) {
    return RGB(32 + frame % 192, 96, 160);
}

LRESULT CALLBACK SourceProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_PAINT || message == WM_PRINT || message == WM_PRINTCLIENT) {
        PAINTSTRUCT paint{};
        HDC dc = message == WM_PAINT ? BeginPaint(hwnd, &paint) : reinterpret_cast<HDC>(wparam);
        RECT client{};
        GetClientRect(hwnd, &client);
        HBRUSH brush = CreateSolidBrush(FrameColor(sourceFrame.load()));
        FillRect(dc, &client, brush);
        DeleteObject(brush);
        RECT marker{240, 80, 248, 144};
        brush = CreateSolidBrush(RGB(12, 52, 92));
        FillRect(dc, &marker, brush);
        DeleteObject(brush);
        if (message == WM_PAINT) EndPaint(hwnd, &paint);
        return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

void PumpUntil(std::chrono::steady_clock::time_point deadline) {
    do {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        if (std::chrono::steady_clock::now() >= deadline) return;
        Sleep(0);
    } while (true);
}

bool CheckCropsAndCompatibility() {
    const RECT area{80, 80, 400, 144};
    for (unsigned mode : {0u, 1u, 2u}) {
        printMode.store(mode);
        bd::Scratch scratch;
        scratch.use_window_capture = false;
        std::vector<uint32_t> pixels;
        bool staticOnly = false;
        if (!bd::Compose(scratch, area, pixels, staticOnly) || staticOnly ||
            pixels[320 * 32] != 0xFF2060A0u || pixels[320 * 32 + 160] != 0xFF0C345Cu) {
            printf("FAIL: offset crop / compatibility mode %u returned wrong pixels.\n", mode);
            return false;
        }
        if (scratch.window.width != 320 || scratch.window.height != 64) {
            printf("FAIL: small crop retained a full-window print bitmap.\n");
            return false;
        }
        if (mode == 2 && scratch.full_content.empty()) {
            printf("FAIL: incompatible ordinary print did not retain the full-content path.\n");
            return false;
        }
    }
    printMode.store(0);
    printf("PASS: offset crop pixels, bounded bitmap, empty-print and incorrect-print fallbacks.\n");
    return true;
}

int main() {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    WNDCLASSW cls{};
    cls.lpfnWndProc = SourceProc;
    cls.hInstance = GetModuleHandleW(nullptr);
    cls.lpszClassName = L"VNotchBackdropMotionProbe";
    if (!RegisterClassW(&cls)) return 2;
    HWND source = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        cls.lpszClassName, L"Liquid glass backdrop motion probe", WS_POPUP,
        0, 0, GetSystemMetrics(SM_CXSCREEN), 700, nullptr, nullptr, cls.hInstance, nullptr);
    if (!source) return 2;
    ShowWindow(source, SW_SHOWNOACTIVATE);
    UpdateWindow(source);
    PumpUntil(std::chrono::steady_clock::now() + std::chrono::milliseconds(300));
    if (!CheckCropsAndCompatibility()) { DestroyWindow(source); return 1; }

    constexpr int width = 320, height = 64, samples = 144;
    std::vector<uint32_t> pixels(width * height);
    uint64_t publication = 0;
    auto capture = [&]() {
        uint8_t ok = 0;
        return vn_backdrop_composite_frame(80, 80, width, height, pixels.data(),
            &publication, 1000 / samples, &ok) == VN_OK && ok && publication != 0;
    };
    // The public compositor queues cold captures on its worker. Keep this
    // source window pumping messages while WGC/PrintWindow warms up.
    bool warmed = false;
    const auto warmup = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    while (!(warmed = capture()) && std::chrono::steady_clock::now() < warmup)
        PumpUntil(std::chrono::steady_clock::now() + std::chrono::milliseconds(10));
    if (!warmed) { printf("FAIL: asynchronous capture did not warm up.\n"); DestroyWindow(source); return 2; }
    PumpUntil(std::chrono::steady_clock::now() + std::chrono::milliseconds(100));
    if (!capture() || pixels[width * height / 2] != 0xFF2060A0u) {
        printf("FAIL: controlled source is not visible in the production capture.\n");
        DestroyWindow(source);
        return 1;
    }
    unsigned changes = 0;
    uint32_t previous = pixels[width * height / 2];
    printCalls.store(0);
    printMicroseconds.store(0);
    const auto start = std::chrono::steady_clock::now();
    // In the app the backdrop worker and the window being sampled have
    // independent clocks. A capture wait must never delay the next WM_PAINT.
    bool captureFailed = false;
    std::thread consumer([&] {
        for (unsigned sample = 1; sample <= samples; ++sample) {
            PumpUntil(start + std::chrono::microseconds(sample * 1000000LL / samples));
            if (!capture()) { captureFailed = true; return; }
            const uint32_t pixel = pixels[width * height / 2];
            if (pixel != previous) ++changes;
            previous = pixel;
        }
    });
    for (unsigned frame = 1; frame <= samples; ++frame) {
        sourceFrame.store(frame);
        InvalidateRect(source, nullptr, FALSE);
        UpdateWindow(source);
        PumpUntil(start + std::chrono::microseconds(frame * 1000000LL / samples));
    }
    consumer.join();
    if (captureFailed) { DestroyWindow(source); return 2; }
    const double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
    printf("Elapsed: %.3f s; PrintWindow calls: %u; mean PrintWindow: %.2f ms.\n",
        seconds, printCalls.load(), printCalls.load() ? printMicroseconds.load() / 1000.0 / printCalls.load() : 0.0);
    DestroyWindow(source);
    printf("Source: %d frames at 144 Hz; distinct captured frames: %u (%.1f%%).\n",
        samples, changes, changes * 100.0 / samples);
    const bool passed = changes >= samples * 9 / 10;
    printf("%s: moving backdrop must retain at least 90%% of source updates.\n", passed ? "PASS" : "FAIL");
    return passed ? 0 : 1;
}
