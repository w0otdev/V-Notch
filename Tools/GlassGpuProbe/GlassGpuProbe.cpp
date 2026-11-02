// Hardware integration probe. Only synthetic fixtures are read back to RAM.
// Including the implementation exercises the shipped shader/capture renderer.
#include "../../Native/Controllers/GpuGlass.cpp"
#include <cstdio>
#include <stdexcept>

#pragma comment(lib, "d3d9.lib")
void Require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
bool NearPixels(const std::vector<uint32_t>& actual, const std::vector<uint32_t>& expected) {
    if (actual.size() != expected.size()) return false;
    for (size_t i = 0; i < actual.size(); ++i) {
        if ((actual[i] >> 24) != (expected[i] >> 24)) return false;
        for (int shift = 0; shift < 24; shift += 8)
            if (abs(static_cast<int>((actual[i] >> shift) & 255) - static_cast<int>((expected[i] >> shift) & 255)) > 1) return false;
    }
    return true;
}

std::vector<uint32_t> RenderFixture(GpuGlass& gpu, Frame frame, const std::vector<uint32_t>& pixels, IDirect3DSurface9* presentation = nullptr) {
    auto fixture = gpu.MakeTarget(frame.width, frame.height);
    gpu.context->UpdateSubresource(fixture.texture.Get(), 0, nullptr, pixels.data(), frame.width * 4, 0);
    gpu.desktopTexture = fixture.texture; gpu.desktopView = fixture.srv;
    gpu.outputDesc.DesktopCoordinates = {0, 0, frame.width, frame.height};
    gpu.outputDesc.Rotation = DXGI_MODE_ROTATION_IDENTITY;
    UINT outputIndex = gpu.nextOutput;
    ULONGLONG deadline = GetTickCount64() + 5000;
    while (gpu.RenderCaptured(frame) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
    Require(gpu.pending, "GPU completion query missing");
    Check(gpu.WaitReady(4));
    ComPtr<IDirect3DSurface9> ready;
    if (presentation) {
        HRESULT hr;
        while ((hr = gpu.PresentReady(presentation)) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
        Check(hr); Require(hr == S_OK, "Persistent presentation did not finish");
    } else {
        while (gpu.TakeReady(&ready) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
        Require(ready != nullptr, "GPU did not finish within 5 seconds");
    }
    Require(gpu.nextOutput != outputIndex, "Shared output did not alternate");
    if (presentation) {
        // Verify the actual persistent WPF surface, including cleared pixels
        // outside a shrinking frame. Only synthetic fixtures are read back.
        ComPtr<IDirect3DDevice9> device9;
        Check(presentation->GetDevice(&device9));
        D3DSURFACE_DESC desc{}; Check(presentation->GetDesc(&desc));
        ComPtr<IDirect3DSurface9> systemMemory;
        Check(device9->CreateOffscreenPlainSurface(desc.Width, desc.Height, desc.Format, D3DPOOL_SYSTEMMEM, &systemMemory, nullptr));
        Check(device9->GetRenderTargetData(presentation, systemMemory.Get()));
        D3DLOCKED_RECT mapped{}; Check(systemMemory->LockRect(&mapped, nullptr, D3DLOCK_READONLY));
        std::vector<uint32_t> result(frame.width * frame.height);
        bool outsideClear = true;
        for (UINT y = 0; y < desc.Height; ++y) {
            auto* row = reinterpret_cast<const uint32_t*>(static_cast<const uint8_t*>(mapped.pBits) + y * mapped.Pitch);
            if (y < static_cast<UINT>(frame.height)) memcpy(result.data() + y * frame.width, row, frame.width * 4);
            for (UINT x = 0; x < desc.Width; ++x)
                if ((x >= static_cast<UINT>(frame.width) || y >= static_cast<UINT>(frame.height)) && row[x] != 0) outsideClear = false;
        }
        Check(systemMemory->UnlockRect());
        Require(outsideClear, "Persistent D3D9 surface retained pixels outside the active crop");
        return result;
    }
    D3D11_TEXTURE2D_DESC desc{}; gpu.output[outputIndex].texture->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.MiscFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> readback;
    Check(gpu.device->CreateTexture2D(&desc, nullptr, &readback));
    gpu.context->CopyResource(readback.Get(), gpu.output[outputIndex].texture.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    Check(gpu.context->Map(readback.Get(), 0, D3D11_MAP_READ, 0, &mapped));
    std::vector<uint32_t> result(frame.width * frame.height);
    for (int y = 0; y < frame.height; ++y)
        memcpy(result.data() + y * frame.width, static_cast<uint8_t*>(mapped.pData) + y * mapped.RowPitch, frame.width * 4);
    gpu.context->Unmap(readback.Get(), 0);
    return result;
}

int main(int argc, char** argv) {
    HWND hwnd = CreateWindowExW(0, L"STATIC", L"VNotch GPU probe", WS_POPUP, -32000, -32000, 1, 1, nullptr, nullptr, nullptr, nullptr);
    try {
        static_assert(sizeof(Frame) == 536, "Managed/native frame layout changed");
        ComPtr<IDirect3D9Ex> direct9; Check(Direct3DCreate9Ex(D3D_SDK_VERSION, &direct9));
        D3DPRESENT_PARAMETERS present{};
        present.Windowed = TRUE; present.SwapEffect = D3DSWAPEFFECT_DISCARD; present.hDeviceWindow = hwnd;
        ComPtr<IDirect3DDevice9Ex> device9;
        Check(direct9->CreateDeviceEx(vn_glass_gpu_find_adapter(direct9.Get(), hwnd), D3DDEVTYPE_HAL, hwnd,
            D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE | D3DCREATE_HARDWARE_VERTEXPROCESSING,
            &present, nullptr, &device9));
        if (argc > 1 && strcmp(argv[1], "--resize") == 0) {
            GpuGlass resizing; resizing.Initialize(device9.Get(), 1920, 1080);
            auto fixture = resizing.MakeTarget(1920, 1080);
            const float color[] = { .12f, .35f, .6f, 1 };
            resizing.context->ClearRenderTargetView(fixture.rtv.Get(), color);
            resizing.desktopTexture = fixture.texture; resizing.desktopView = fixture.srv;
            resizing.outputDesc.DesktopCoordinates = { 0, 0, 1920, 1080 };
            resizing.outputDesc.Rotation = DXGI_MODE_ROTATION_IDENTITY;
            uint64_t initial = resizing.targetAllocations, bytes = resizing.targetAllocatedBytes;
            for (int cycle = 0; cycle < 24; ++cycle) {
                Frame size{}; size.blur = 5;
                size.width = cycle % 2 ? 400 : 1920;
                size.height = cycle % 2 ? 160 : 900;
                Check(resizing.RenderCaptured(size));
                ComPtr<IDirect3DSurface9> ready;
                ULONGLONG deadline = GetTickCount64() + 5000;
                while (resizing.TakeReady(&ready) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
                Require(ready != nullptr, "Resize did not finish");
            }
            printf("RESIZE 24 large/small frames: %llu texture allocations, %.2f MiB allocation traffic\n",
                resizing.targetAllocations - initial, (resizing.targetAllocatedBytes - bytes) / 1048576.0);
            Require(resizing.targetAllocations - initial <= 8, "View transitions recreated warmed blur textures");
            DestroyWindow(hwnd); return 0;
        }
        GpuGlass gpu; gpu.Initialize(device9.Get(), 128, 96);
        printf("GPU completion fence: context=%d fence=%d event=%d\n", gpu.fenceContext != nullptr,
            gpu.completionFence != nullptr, static_cast<bool>(gpu.completionEvent));
        Frame f{}; f.width = 96; f.height = 64;
        std::vector<uint32_t> uniform(f.width * f.height, 0xffa06020);
        auto sharp = RenderFixture(gpu, f, uniform);
        Require(NearPixels(sharp, uniform), "Shared BGRA texture changed uniform pixels");
        f.blur = 8;
        auto blurred = RenderFixture(gpu, f, uniform);
        Require(NearPixels(blurred, uniform), "Kawase weights/edge clamping changed a uniform image");
        std::vector<uint32_t> checker(uniform.size());
        for (size_t i = 0; i < checker.size(); ++i) checker[i] = (i + i / f.width) % 2 ? 0xff000000 : 0xffffffff;
        blurred = RenderFixture(gpu, f, checker);
        for (int y = 8; y < f.height - 8; ++y)
            for (int x = 8; x < f.width - 8; ++x)
                Require((blurred[y*f.width+x] & 255) >= 183 && (blurred[y*f.width+x] & 255) <= 192, "Linear-light Kawase did not preserve checkerboard energy");
        f.blur = 0; f.refract = 1;
        const float values[] = {96,64,80,48,8,8,12,12,3,.7f,2.3f,5.2f,6.9f,1,.06f,.25f,0,.15f,0,0,1,0,.5f,.5f,0,0,0,1.1f,.15f,-.15f,1.65f,0};
        for (int i = 0; i < 32; ++i) f.lens[i][0] = values[i];
        sharp = RenderFixture(gpu, f, uniform);
        Require(NearPixels({sharp[24*96+40]}, {0xffa06020}), "Refraction changed the neutral center");
        Require(sharp[63*96+95] == 0, "Refraction output was not transparent outside the lens");
        Require((sharp[0] >> 24) == 0, "Rounded lens corner was not transparent");
        // A gradient exposes RGB dispersion: only red/blue sampling changes.
        for (int y = 0; y < f.height; ++y)
            for (int x = 0; x < f.width; ++x) checker[y*f.width+x] = 0xff000000 | (x*2 << 16) | (x*2 << 8) | x*2;
        auto neutral = RenderFixture(gpu, f, checker);
        f.lens[19][0] = 2;
        auto chromatic = RenderFixture(gpu, f, checker);
        bool dispersed = false;
        for (size_t i = 0; i < neutral.size(); ++i) {
            Require((neutral[i] & 0xff00ff00) == (chromatic[i] & 0xff00ff00), "Dispersion modified green/alpha");
            dispersed |= neutral[i] != chromatic[i];
        }
        Require(dispersed, "RGB dispersion produced no change");
        f.refract = 0;
        for (int y = 0; y < f.height; ++y)
            for (int x = 0; x < f.width; ++x) checker[y*f.width+x] = x < f.width / 2 ? 0xff000000 : 0xffffffff;
        f.blur = 1;
        auto oldLow = RenderFixture(gpu, f, checker);
        double previousLeak = -1;
        for (float sigma : { 0.0f, .07f, .14f, .28f, .56f, 1.0f }) {
            f.blur = sigma; auto low = RenderFixture(gpu, f, checker);
            double leak = 0, oldLeak = 0;
            for (int y = 8; y < f.height - 8; ++y)
                for (int x = 0; x < f.width / 2; ++x) {
                    leak += low[y * f.width + x] & 255;
                    oldLeak += oldLow[y * f.width + x] & 255;
                }
            printf("Subpixel blur %.2f: edge bleed %.3f of previous 1px minimum\n", sigma, leak / oldLeak);
            Require(leak >= previousLeak, "Subpixel blur did not increase monotonically");
            if (sigma > 0 && sigma <= .14f) Require(leak < oldLeak * .2, "5% blur still imposes a heavy minimum");
            if (sigma == 0) Require(NearPixels(low, checker), "Zero blur altered a sharp edge");
            previousLeak = leak;
        }
        for (int boundary : { 2, 4, 8, 16 }) {
            f.blur = static_cast<float>(boundary) - .01f; auto before = RenderFixture(gpu, f, checker);
            f.blur = static_cast<float>(boundary) + .01f; auto after = RenderFixture(gpu, f, checker);
            double difference = 0;
            for (size_t i = 0; i < before.size(); ++i)
                difference += abs(static_cast<int>(before[i] & 255) - static_cast<int>(after[i] & 255));
            printf("Blur %d +/- 0.01 mean difference %.2f/255\n", boundary, difference / before.size());
            Require(difference / before.size() < .3, "Blur popped when crossing a pyramid level");
        }
        f.refract = 0; f.width = 95; f.height = 63; f.blur = 60;
        uniform.assign(f.width * f.height, 0xffa06020);
        Require(NearPixels(RenderFixture(gpu, f, uniform), uniform), "Odd-size resize or maximum blur corrupted pixels");
        f.width = 31; f.height = 17; f.blur = 5;
        uniform.assign(f.width * f.height, 0xff204080);
        Require(NearPixels(RenderFixture(gpu, f, uniform), uniform), "Shrinking the active region sampled stale capacity pixels");
        f.width = 95; f.height = 63;
        ComPtr<IDirect3DSurface9> stable, readback9;
        Check(device9->CreateRenderTarget(128, 96, D3DFMT_A8R8G8B8, D3DMULTISAMPLE_NONE, 0, FALSE, &stable, nullptr));
        Check(device9->CreateOffscreenPlainSurface(128, 96, D3DFMT_A8R8G8B8, D3DPOOL_SYSTEMMEM, &readback9, nullptr));
        f.refract = 0; f.blur = 5;
        for (int cycle = 0; cycle < 120; ++cycle) {
            uint32_t color = cycle % 2 ? 0xffa06020 : 0xff2060a0;
            uniform.assign(f.width * f.height, color);
            RenderFixture(gpu, f, uniform, stable.Get());
            Check(device9->GetRenderTargetData(stable.Get(), readback9.Get()));
            D3DLOCKED_RECT locked{}; Check(readback9->LockRect(&locked, nullptr, D3DLOCK_READONLY));
            uint32_t center = reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(locked.pBits) + locked.Pitch * 24)[40];
            readback9->UnlockRect();
            Require(NearPixels({center}, {color}), "Persistent WPF surface presented a black/stale frame");
        }
        printf("PASS: 120 alternating frames through one persistent D3D9 surface, without black/stale frames.\n");
        if (gpu.completionFence && gpu.completionEvent) {
            // Simulate a leftover notification while the current fence is still
            // unsignaled. The worker must wait for its bounded deadline.
            ++gpu.fenceValue; gpu.pending = true;
            SetEvent(gpu.completionEvent.get());
            auto started = std::chrono::steady_clock::now();
            Require(gpu.WaitReady(4) == S_FALSE, "A stale event was accepted as GPU completion");
            double waited = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started).count();
            Require(waited >= 3, "A stale GPU event caused an immediate UI polling retry");
            Check(gpu.fenceContext->Signal(gpu.completionFence.Get(), gpu.fenceValue)); gpu.context->Flush();
            ULONGLONG deadline = GetTickCount64() + 5000;
            while (gpu.WaitReady(4) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
            Require(gpu.CompletionReady() == S_OK, "Fence completion never resumed after a timeout");
            gpu.pending = false;
            printf("PASS: stale GPU notification ignored; wait bounded at %.2f ms and completion recovered.\n", waited);
        }
        // Older drivers still use the event-query presentation path.
        {
            auto fence = std::move(gpu.completionFence);
            auto event = std::move(gpu.completionEvent);
            auto fenceContext = std::move(gpu.fenceContext);
            uniform.assign(f.width * f.height, 0xffa06020);
            Require(NearPixels(RenderFixture(gpu, f, uniform, stable.Get()), uniform), "Legacy query completion corrupted a frame");
            gpu.completionFence = std::move(fence);
            gpu.completionEvent = std::move(event);
            gpu.fenceContext = std::move(fenceContext);
        }
        printf("PASS: query completion fallback without D3D11 fences.\n");
        gpu.ResetCapture();
        gpu.retryAt = GetTickCount64() + 10000;
        gpu.captureWaitStarted = GetTickCount64() - 5001;
        bool timedOut = false;
        try { gpu.Render(f); }
        catch (HRESULT hr) { timedOut = hr == HRESULT_FROM_WIN32(ERROR_TIMEOUT); }
        Require(timedOut, "Missing capture must report failure instead of waiting indefinitely");
        gpu.retryAt = 0; gpu.captureWaitStarted = 0;
        printf("PASS: shader compilation, D3D9/D3D11 shared BGRA, double buffering, blur, refraction, alpha, RGB dispersion.\n");
        if (argc > 1) {
            gpu.ResetCapture();
            Require(SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) != FALSE, "Capture exclusion failed");
            DwmFlush();
            MONITORINFO mi{sizeof(mi)}; GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &mi);
            f.x = mi.rcMonitor.left; f.y = mi.rcMonitor.top;
            const bool forceWgc = std::find_if(argv + 1, argv + argc, [](const char* arg) {
                return strcmp(arg, "--wgc") == 0;
            }) != argv + argc;
            if (forceWgc) gpu.StartGraphicsCapture(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
            ULONGLONG deadline = GetTickCount64() + 5000;
            HRESULT hr = S_FALSE;
            while ((hr = gpu.Render(f)) == S_FALSE && GetTickCount64() < deadline) Sleep(10);
            Check(hr); Require(hr == S_OK, "No DXGI frame acquired on the input desktop");
            printf("PASS: live %s capture with exclusion (no desktop readback).\n", gpu.framePool ? "WGC" : "DXGI");
            if (gpu.captureSession) {
                if (auto pacing = gpu.captureSession.try_as<Capture::IGraphicsCaptureSession5>()) {
                    gpu.SetCaptureInterval(1000.0 / 30);
                    Require(abs(pacing.MinUpdateInterval().count() - 333333) <= 1, "WGC did not receive the 30 FPS target");
                    gpu.SetCaptureInterval(1000.0 / 120);
                    Require(abs(pacing.MinUpdateInterval().count() - 83333) <= 1, "WGC did not receive a live 120 FPS update");
                    printf("PASS: WGC capture interval changes live from 30 to 120 FPS.\n");
                }
                printf("WGC borderless access=0x%08lx required=%d\n", gpu.borderlessResult, gpu.captureSession.IsBorderRequired());
                Require(gpu.borderlessResult == S_OK && !gpu.captureSession.IsBorderRequired(), "Borderless capture was not allowed by Windows");
            }
        }
        DestroyWindow(hwnd); return 0;
    } catch (HRESULT hr) { printf("FAIL HRESULT 0x%08lx\n", hr); }
      catch (const std::exception& e) { printf("FAIL %s\n", e.what()); }
    DestroyWindow(hwnd); return 1;
}
