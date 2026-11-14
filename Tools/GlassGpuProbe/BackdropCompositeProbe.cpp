#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <fstream>

// Run with OWNER_PID OUTPUT.bmp [--verify-overlays]. Match the app's physical
// coordinates and exclusion identity while testing the real capture code.
DWORD probeOwnerPid = 0;
DWORD WINAPI ProbeOwnerPid() { return probeOwnerPid ? probeOwnerPid : ::GetCurrentProcessId(); }
bool useFixedScreenPixel = false;
COLORREF WINAPI ProbeGetPixel(HDC dc, int x, int y) {
    return useFixedScreenPixel ? RGB(32, 32, 32) : ::GetPixel(dc, x, y);
}
#define GetCurrentProcessId ProbeOwnerPid
#define GetPixel ProbeGetPixel
#include "../../Native/Controllers/BackdropComposite.cpp"
#undef GetCurrentProcessId
#undef GetPixel

int main(int argc, char** argv) {
    if (argc < 3 || argc > 4 || (argc == 4 && std::string(argv[3]) != "--verify-overlays")) return 2;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    probeOwnerPid = std::stoul(argv[1]);
    if (!bd::IsUnredirectedToolOverlay(WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP) ||
        bd::IsUnredirectedToolOverlay(WS_EX_NOREDIRECTIONBITMAP | WS_EX_WINDOWEDGE) ||
        bd::IsUnredirectedToolOverlay(WS_EX_TOOLWINDOW | WS_EX_TOPMOST)) return 1;
    const RECT area{0, 0, GetSystemMetrics(SM_CXSCREEN), 340};
    bd::EnumState state;
    state.area = area; state.self_pid = ProbeOwnerPid();
    EnumWindows(bd::CollectLayer, reinterpret_cast<LPARAM>(&state));
    HDC screen = GetDC(nullptr);
    if (!screen) return 1;
    struct ScreenLease { HDC dc; ~ScreenLease() { ReleaseDC(nullptr, dc); } } lease{screen};
    bd::Scratch scratch;
    if (!scratch.target.Ensure(screen, area.right, area.bottom)) return 1;
    HWND overlay = nullptr;
    int overlayIndex = -1;
    for (int i = 0; i < state.count; ++i) {
        const auto& layer = state.layers[i];
        wchar_t name[128]{}; GetClassNameW(layer.hwnd, name, 128);
        printf("Layer %d: class=%ls frame=(%ld,%ld)-(%ld,%ld)\n", i, name,
            layer.visible.left, layer.visible.top, layer.visible.right, layer.visible.bottom);
        if (!overlay && bd::IsUnredirectedToolOverlay(GetWindowLongPtrW(layer.hwnd, GWL_EXSTYLE))) {
            overlay = layer.hwnd; overlayIndex = i;
        }
    }
    if (overlay) {
        // Simulate an overlay that was accepted while opaque, but has changed
        // material without changing HWND. Its old verification must not survive.
        bd::EnumState changed = state;
        changed.layers[0] = state.layers[overlayIndex]; changed.count = 1;
        auto* target = static_cast<uint32_t*>(scratch.target.bits);
        RECT hit{}; IntersectRect(&hit, &area, &changed.layers[0].visible);
        const int width = hit.right - hit.left;
        int samples = 0;
        for (int y : {hit.top + 3, hit.top + (hit.bottom - hit.top) / 2}) {
            for (int x : {hit.left + width / 10, hit.left + width / 2, hit.right - width / 10 - 1}) {
                if (!bd::PointInRect(hit, x, y) || bd::IsPointCoveredBeforeLayer(changed, 0, x, y)) continue;
                target[y * area.right + x] = 0xffff00ffu;
                ++samples;
            }
        }
        scratch.verified.push_back(overlay);
        useFixedScreenPixel = true;
        const HWND mismatchedOverlay = bd::FindMismatchedLayer(screen, scratch.target, area, changed, scratch);
        useFixedScreenPixel = false;
        if (!samples || mismatchedOverlay != overlay) {
            printf("FAIL: stale overlay acceptance hid a changed material (%d samples).\n", samples);
            return 1;
        }
        printf("PASS: cached acceptance cannot hide a changed overlay material.\n");
        // Also seed rejection from a previous transparent frame. The next frame
        // must retry the same HWND, so an opaque bar is not replaced by wallpaper.
        scratch.bad.push_back(overlay);
    }
    std::vector<uint32_t> pixels;
    bool staticOnly = false;
    if (!bd::Compose(scratch, area, pixels, staticOnly)) return 1;
    int compared = 0, mismatched = 0;
    if (overlay) {
        RECT hit{}; IntersectRect(&hit, &area, &state.layers[overlayIndex].visible);
        const int width = hit.right - hit.left;
        for (int y : {hit.top + 3, hit.top + (hit.bottom - hit.top) / 2}) {
            for (int x : {hit.left + width / 10, hit.left + width / 2, hit.right - width / 10 - 1}) {
                if (!bd::PointInRect(hit, x, y) || bd::IsPointCoveredBeforeLayer(state, overlayIndex, x, y)) continue;
                const COLORREF real = GetPixel(screen, x, y);
                if (real == CLR_INVALID) continue;
                ++compared;
                if (bd::PixelDiffExceedsThreshold(pixels[y * area.right + x], real)) ++mismatched;
            }
        }
    }
    BITMAPFILEHEADER file{};
    BITMAPINFOHEADER info{};
    file.bfType = 0x4d42; file.bfOffBits = sizeof(file) + sizeof(info);
    file.bfSize = file.bfOffBits + static_cast<DWORD>(pixels.size() * 4);
    info.biSize = sizeof(info); info.biWidth = area.right; info.biHeight = -area.bottom;
    info.biPlanes = 1; info.biBitCount = 32; info.biCompression = BI_RGB;
    std::ofstream output(argv[2], std::ios::binary);
    output.write(reinterpret_cast<char*>(&file), sizeof(file));
    output.write(reinterpret_cast<char*>(&info), sizeof(info));
    output.write(reinterpret_cast<char*>(pixels.data()), pixels.size() * 4);
    printf("Composed: compared=%d mismatched=%d rejected=%zu verified=%zu\n",
        compared, mismatched, scratch.bad.size(), scratch.verified.size());
    if (argc == 4 && overlay && (!compared || mismatched * 2 >= compared)) {
        printf("FAIL: backdrop disagrees with the visible shell material.\n");
        return 1;
    }
    printf("PASS: capture follows the live shell material without sticky acceptance or rejection.\n");
    return output ? 0 : 1;
}
