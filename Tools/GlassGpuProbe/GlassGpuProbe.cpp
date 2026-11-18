// Hardware integration probe. Only synthetic fixtures are read back to RAM.
// Including the implementation exercises the shipped shader/capture renderer.
#include "../../Native/Controllers/GpuGlass.cpp"
#include <cstdio>
#include <stdexcept>

#pragma comment(lib, "d3d9.lib")
bool useUploadedFixtures = false;
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

void PresentProbeFrame(GpuGlass& gpu, IDirect3DSurface9* destination = nullptr) {
    ComPtr<IDirect3DSurface9> temporary;
    if (!destination) {
        Check(gpu.presentationDevice->CreateRenderTarget(gpu.width, gpu.height, D3DFMT_A8R8G8B8,
            D3DMULTISAMPLE_NONE, 0, FALSE, &temporary, nullptr));
        destination = temporary.Get();
    }
    const ULONGLONG deadline = GetTickCount64() + 5000;
    HRESULT hr;
    while ((hr = gpu.PresentReady(destination)) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
    Check(hr); Require(hr == S_OK, "GPU frame did not reach the presentation surface");
}

std::vector<uint32_t> RenderFixture(GpuGlass& gpu, Frame frame, const std::vector<uint32_t>& pixels,
    IDirect3DSurface9* presentation = nullptr, UINT stride = 0) {
    if (!stride) stride = frame.width * 4;
    if (useUploadedFixtures) {
        // Pixel assertions require a submission even for an identical fixture.
        frame.refract |= 2;
    } else {
        auto fixture = gpu.MakeTarget(frame.width, frame.height);
        gpu.context->UpdateSubresource(fixture.texture.Get(), 0, nullptr, pixels.data(), stride, 0);
        gpu.desktopTexture = fixture.texture; gpu.desktopView = fixture.srv;
        gpu.outputDesc.DesktopCoordinates = {frame.x, frame.y, frame.x + frame.width, frame.y + frame.height};
        gpu.outputDesc.Rotation = DXGI_MODE_ROTATION_IDENTITY;
        gpu.usingUploadedBackdrop = false;
    }
    UINT outputIndex = gpu.nextOutput;
    ULONGLONG deadline = GetTickCount64() + 5000;
    while ((useUploadedFixtures ? gpu.RenderPixels(frame, reinterpret_cast<const uint8_t*>(pixels.data()), stride)
        : gpu.RenderCaptured(frame)) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
    Require(gpu.pending, "GPU completion query missing");
    Check(gpu.WaitReady(4));
    PresentProbeFrame(gpu, presentation);
    Require(!gpu.pending && (gpu.crossAdapter.readback || gpu.nextOutput != outputIndex), "Output was not released after presentation");
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

// Compare a strongly compressed one-pixel grating against the area under
// the continuous warp. Point sampling the centre alone aliases this pattern.
void VerifyFilteredBend(GpuGlass& gpu, Frame frame) {
    frame.lens[7][0] = 0; // attached top; the test scanline sees a straight side
    frame.lens[30][0] = 6;
    std::vector<uint32_t> stripes(frame.width * frame.height);
    for (int y = 0; y < frame.height; ++y)
        for (int x = 0; x < frame.width; ++x)
            stripes[y * frame.width + x] = x % 2 ? 0xffffffff : 0xff000000;
    auto filtered = RenderFixture(gpu, frame, stripes);
    // Normalize out the original hairline highlight at the silhouette.
    std::fill(stripes.begin(), stripes.end(), 0xff000000);
    auto black = RenderFixture(gpu, frame, stripes);
    std::fill(stripes.begin(), stripes.end(), 0xffffffff);
    auto white = RenderFixture(gpu, frame, stripes);
    const double width = frame.lens[3][1], lever = width * 1.5 * frame.lens[30][0];
    const auto sample = [&](double x) {
        const double t = std::clamp(1.0 - x / width, 0.0, 1.0);
        const double depth = 1.0 - t;
        const double loss = 2.3 * std::pow(5.2 * std::exp(1.0), -0.7 - 6.9 * depth * depth);
        const double ray = std::clamp(1.0 - loss - 0.16 * t * t, 0.02, 1.25);
        const double axis = 1.0 - 2 * x / frame.lens[2][0];
        const double texel = std::clamp(x - 0.5 + frame.lens[4][0] + axis * (1.0 - ray) * lever * t,
            0.0, static_cast<double>(frame.width - 1));
        return 255.0 * std::abs(std::remainder(texel, 2.0));
    };
    double pointError = 0, filteredError = 0;
    constexpr int testedPixels = 8;
    for (int x = 0; x < testedPixels; ++x) {
        double reference = 0;
        for (int sub = 0; sub < 64; ++sub)
            reference += sample(x + (sub + .5) / 64) / 64;
        pointError += std::abs(sample(x + .5) - reference);
        const int i = 16 * frame.width + x;
        const double low = black[i] & 255, high = white[i] & 255;
        const double observed = ((filtered[i] & 255) - low) * 255.0 / (high - low);
        filteredError += std::abs(observed - reference);
    }
    printf("Compressed grating mean absolute error: %.1f point, %.1f filtered (levels/255).\n",
        pointError / testedPixels, filteredError / testedPixels);
    Require(filteredError < pointError * 0.8, "Compressed grating aliased on the sharp bevel");
}

void VerifyLiveCapture(GpuGlass& gpu, Frame& frame, HWND hwnd, bool forceWgc) {
    gpu.ResetCapture();
    Require(SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) != FALSE, "Capture exclusion failed");
    DwmFlush();
    const auto monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi{sizeof(mi)}; GetMonitorInfoW(monitor, &mi);
    frame.x = mi.rcMonitor.left; frame.y = mi.rcMonitor.top;
    if (forceWgc) gpu.StartGraphicsCapture(monitor);
    ULONGLONG deadline = GetTickCount64() + 5000;
    HRESULT hr = S_FALSE;
    while ((hr = gpu.Render(frame)) == S_FALSE && GetTickCount64() < deadline) Sleep(10);
    Check(hr); Require(hr == S_OK, "No desktop frame acquired on the input desktop");
    Check(gpu.WaitReady(1000));
    PresentProbeFrame(gpu);
    printf("PASS: live %s capture with exclusion (no desktop readback).\n", gpu.framePool ? "WGC" : "DXGI");
    if (gpu.captureSession) {
        if (auto pacing = gpu.captureSession.try_as<Capture::IGraphicsCaptureSession5>()) {
            for (double fps : {30.0, 60.0, 120.0, 144.0}) {
                gpu.SetCaptureInterval(1000.0 / fps);
                Require(pacing.MinUpdateInterval().count() == 10000,
                    "WGC imposed an additional frame interval instead of letting the worker pace it");
            }
            printf("PASS: WGC keeps the supported 1 ms minimum across worker FPS changes.\n");
        }
        printf("WGC borderless access=0x%08lx required=%d\n", gpu.borderlessResult, gpu.captureSession.IsBorderRequired());
        Require(gpu.borderlessResult == S_OK && !gpu.captureSession.IsBorderRequired(), "Borderless capture was not allowed by Windows");
    }
}

#include "GpuPipelineChecks.h"
#include "ExactWindowChecks.h"

int main(int argc, char** argv) {
    if (argc > 1 && strcmp(argv[1], "--exact-window-host") == 0) return RunExactWindowHost();
    const bool ultimate = argc > 1 && strcmp(argv[1], "--ultimate") == 0;
    useUploadedFixtures = ultimate || (argc > 1 && strcmp(argv[1], "--uploaded-backdrop") == 0);
    HWND hwnd = CreateWindowExW(0, L"STATIC", L"VNotch GPU probe", WS_POPUP, -32000, -32000, 1, 1, nullptr, nullptr, nullptr, nullptr);
    try {
        static_assert(sizeof(Frame) == 536, "Managed/native frame layout changed");
        ComPtr<IDirect3D9Ex> direct9; Check(Direct3DCreate9Ex(D3D_SDK_VERSION, &direct9));
        D3DPRESENT_PARAMETERS present{};
        present.Windowed = TRUE; present.SwapEffect = D3DSWAPEFFECT_DISCARD; present.hDeviceWindow = hwnd;
        ComPtr<IDirect3DDevice9Ex> device9;
        LUID requested{};
        const UINT ordinal = ultimate ? vn_glass_gpu_find_preferred_adapter(direct9.Get(), hwnd, &requested)
                                     : vn_glass_gpu_find_adapter(direct9.Get(), hwnd);
        Check(direct9->CreateDeviceEx(ordinal, D3DDEVTYPE_HAL, hwnd,
            D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE | D3DCREATE_HARDWARE_VERTEXPROCESSING,
            &present, nullptr, &device9));
        if (argc > 1 && strcmp(argv[1], "--wgc-only") == 0) {
            GpuGlass capture; capture.Initialize(device9.Get(), 256, 128);
            Frame frame{}; frame.width = 128; frame.height = 64;
            VerifyLiveCapture(capture, frame, hwnd, true);
            DestroyWindow(hwnd); return 0;
        }
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
                PresentProbeFrame(resizing);
            }
            printf("RESIZE 24 large/small frames: %llu texture allocations, %.2f MiB allocation traffic\n",
                resizing.targetAllocations - initial, (resizing.targetAllocatedBytes - bytes) / 1048576.0);
            Require(resizing.targetAllocations - initial <= 8, "View transitions recreated warmed blur textures");
            DestroyWindow(hwnd); return 0;
        }
        const auto initBegin = std::chrono::steady_clock::now();
        GpuGlass gpu; gpu.Initialize(device9.Get(), 128, 96, requested);
        printf("First presenter init: %.2f ms (precompiled shaders).\n", std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - initBegin).count());
        if (ultimate) {
            ComPtr<IDXGIDevice> dxgiDevice; Check(gpu.device.As(&dxgiDevice));
            ComPtr<IDXGIAdapter> actual; Check(dxgiDevice->GetAdapter(&actual));
            DXGI_ADAPTER_DESC render{}; Check(actual->GetDesc(&render));
            D3DADAPTER_IDENTIFIER9 presentId{}; Check(direct9->GetAdapterIdentifier(ordinal, 0, &presentId));
            printf("Ultimate shader GPU: %ls; D3D9 presentation GPU: %s\n", render.Description, presentId.Description);
            Require(SameLuid(render.AdapterLuid, requested), "Ultimate rendered on the wrong physical GPU");
            ComPtr<IDXCoreAdapterFactory> core;
            Check(DXCoreCreateAdapterFactory(IID_IDXCoreAdapterFactory, reinterpret_cast<void**>(core.GetAddressOf())));
            ComPtr<IDXCoreAdapterList> list;
            Check(core->CreateAdapterList(1, &DXCORE_ADAPTER_ATTRIBUTE_D3D11_GRAPHICS, IID_IDXCoreAdapterList,
                reinterpret_cast<void**>(list.GetAddressOf())));
            bool hasDiscrete = false, rendersOnDiscrete = false;
            for (UINT i = 0; i < list->GetAdapterCount(); ++i) {
                ComPtr<IDXCoreAdapter> physical;
                Check(list->GetAdapter(i, IID_IDXCoreAdapter, reinterpret_cast<void**>(physical.GetAddressOf())));
                bool hardware{}, integrated{};
                LUID luid{};
                Check(physical->GetProperty(DXCoreAdapterProperty::IsHardware, &hardware));
                Check(physical->GetProperty(DXCoreAdapterProperty::IsIntegrated, &integrated));
                Check(physical->GetProperty(DXCoreAdapterProperty::InstanceLuid, &luid));
                if (hardware && !integrated) { hasDiscrete = true; rendersOnDiscrete |= SameLuid(luid, render.AdapterLuid); }
            }
            Require(!hasDiscrete || rendersOnDiscrete, "Ultimate chose an iGPU with a discrete GPU available");
        } else {
            VerifySharedDevice(gpu, device9.Get());
            VerifyOpaqueWindowShader(gpu);
            VerifyCameraGpu(gpu, device9.Get());
        }
        if (argc > 1 && strcmp(argv[1], "--exact-wgc") == 0) { VerifyExactWindows(gpu, hwnd); DestroyWindow(hwnd); return 0; }
        int64_t validationHandle = 0;
        Check(vn_glass_gpu_create(device9.Get(), 128, 96, &validationHandle));
        Frame invalid{}; invalid.width = 8; invalid.height = 4;
        std::array<uint8_t, 128> smallPixels{};
        Require(vn_glass_gpu_render_pixels(validationHandle, &invalid, smallPixels.data(), 127, 32) == E_INVALIDARG,
            "Upload accepted a truncated last row");
        Require(vn_glass_gpu_render_pixels(validationHandle, &invalid, smallPixels.data(), 128, 31) == E_INVALIDARG,
            "Upload accepted a stride shorter than its row");
        vn_glass_gpu_destroy(validationHandle);
        if (useUploadedFixtures) {
            Frame uploaded{};
            ComPtr<IDirect3DSurface9> presentation;
            Check(device9->CreateRenderTarget(128, 96, D3DFMT_A8R8G8B8, D3DMULTISAMPLE_NONE, 0, FALSE, &presentation, nullptr));
            for (int cycle = 0; cycle < 120; ++cycle) {
                uploaded.x = -500; uploaded.y = 120;
                uploaded.width = cycle % 2 ? 95 : 31;
                uploaded.height = cycle % 2 ? 63 : 17;
                uploaded.blur = cycle % 3 == 0 ? 0.0f : cycle % 3 == 1 ? 5.0f : 60.0f;
                uint32_t color = cycle % 2 ? 0xffa06020 : 0xff204080;
                const int rowPixels = uploaded.width + 8;
                std::vector<uint32_t> padded(rowPixels * uploaded.height, 0xff00ff00);
                for (int y = 0; y < uploaded.height; ++y)
                    std::fill_n(padded.begin() + y * rowPixels, uploaded.width, color);
                std::vector<uint32_t> expected(uploaded.width * uploaded.height, color);
                Require(NearPixels(RenderFixture(gpu, uploaded, padded, presentation.Get(), rowPixels * 4), expected),
                    "Uploaded backdrop corrupted pixels, stride, crop origin or pooled capacity");
            }
            uploaded.width = 96; uploaded.height = 64; uploaded.blur = 0; uploaded.refract = 1;
            const float lens[] = {96,64,80,48,8,8,12,12,3,.7f,2.3f,5.2f,6.9f,1,.06f,.25f,0,.15f,0,0,1,0,.5f,.5f,0,0,0,1.1f,.15f,-.15f,1.65f,0};
            for (int i = 0; i < 32; ++i) uploaded.lens[i][0] = lens[i];
            std::vector<uint32_t> uniform(uploaded.width * uploaded.height, 0xffa06020);
            auto lensPixels = RenderFixture(gpu, uploaded, uniform, presentation.Get());
            Require(NearPixels({lensPixels[24*96+40]}, {0xffa06020}), "Uploaded lens changed the neutral center");
            Require(lensPixels[63*96+95] == 0 && (lensPixels[0] >> 24) == 0, "Uploaded lens lost rounded transparency");
            for (int y = 0; y < uploaded.height; ++y)
                for (int x = 0; x < uploaded.width; ++x) uniform[y*uploaded.width+x] = 0xff000000 | (x*2 << 16) | (x*2 << 8) | x*2;
            auto neutral = RenderFixture(gpu, uploaded, uniform, presentation.Get());
            uploaded.lens[19][0] = 2;
            auto chromatic = RenderFixture(gpu, uploaded, uniform, presentation.Get());
            bool dispersed = false;
            for (size_t i = 0; i < neutral.size(); ++i) {
                Require((neutral[i] & 0xff00ff00) == (chromatic[i] & 0xff00ff00), "Uploaded dispersion changed green/alpha");
                dispersed |= neutral[i] != chromatic[i];
            }
            Require(dispersed, "Uploaded lens lost RGB dispersion");
            printf("PASS: uploaded backdrop validation, 120 padded/moving/resizing frames, and refraction/alpha/dispersion.\n");
            DestroyWindow(hwnd); return 0;
        }
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
        // Light only the backdrop outside the right edge. A transmitted ray
        // can't see that stripe; a reflected ray on the right bevel can.
        auto reflectionFrame = f;
        reflectionFrame.lens[14][0] = reflectionFrame.lens[15][0] = 0; // grain, specular
        reflectionFrame.lens[17][0] = reflectionFrame.lens[19][0] = 0; // distortion, dispersion
        reflectionFrame.lens[3][1] = 20; // native optical rim in pixels
        std::vector<uint32_t> stripe(f.width * f.height, 0xff000000);
        for (int y = 0; y < f.height; ++y)
            std::fill_n(stripe.begin() + (y + 1) * f.width - 6, 6, 0xffffffff);
        auto transmitted = RenderFixture(gpu, reflectionFrame, stripe);
        reflectionFrame.lens[16][0] = 1; // Fresnel
        auto reflected = RenderFixture(gpu, reflectionFrame, stripe);
        const int rightBevel = 24 * f.width + 77;
        Require((reflected[rightBevel] & 255) > (transmitted[rightBevel] & 255) + 64,
            "Glass bevel failed to reflect bright content outside the lens");
        Require(reflected[24 * f.width + 40] == transmitted[24 * f.width + 40],
            "Rim reflection leaked into the flat center");
        // A small warm light outside the lens should move along the bevel
        // with the backdrop. Give the mirror ray real padding on each side.
        auto lampFrame = reflectionFrame;
        lampFrame.lens[0][0] = static_cast<float>(lampFrame.width = 128);
        lampFrame.lens[1][0] = static_cast<float>(lampFrame.height = 96);
        lampFrame.lens[4][0] = lampFrame.lens[5][0] = 24;
        lampFrame.lens[16][0] = 0.14f;
        auto renderLamp = [&](int centreY) {
            std::vector<uint32_t> backdrop(lampFrame.width * lampFrame.height, 0xff101010);
            for (int y = centreY - 6; y <= centreY + 6; ++y)
                for (int x = 115; x <= 127; ++x)
                    if ((x-121)*(x-121) + (y-centreY)*(y-centreY) <= 36)
                        backdrop[y * lampFrame.width + x] = 0xffffbb68;
            return RenderFixture(gpu, lampFrame, backdrop);
        };
        auto upper = renderLamp(39), lower = renderLamp(56);
        const int upperLip = 15 * lampFrame.width + 78, lowerLip = 32 * lampFrame.width + 78;
        auto red = [](uint32_t pixel) { return static_cast<int>((pixel >> 16) & 255); };
        Require(red(upper[upperLip]) >= red(lower[upperLip]) + 38 &&
                red(lower[lowerLip]) >= red(upper[lowerLip]) + 38,
            "A local backdrop light did not form a travelling rim highlight");
        Require(red(upper[upperLip]) > static_cast<int>(upper[upperLip] & 255) + 12 &&
                upper[15 * lampFrame.width + 40] == lower[15 * lampFrame.width + 40],
            "Reflected light lost its source colour or spilled into the lens centre");
        // On a 2-levels-per-texel ramp the green channel directly measures
        // source travel. Test the inset bend independently of the bright lip.
        auto bendFrame = reflectionFrame;
        bendFrame.lens[16][0] = bendFrame.lens[30][0] = 0;
        auto flat = RenderFixture(gpu, bendFrame, checker);
        bendFrame.lens[30][0] = 1.65f;
        auto bent = RenderFixture(gpu, bendFrame, checker);
        auto green = [&](const std::vector<uint32_t>& pixels, int x) {
            return static_cast<int>((pixels[24 * f.width + x] >> 8) & 255);
        };
        Require(green(bent, 2) >= green(flat, 2) + 10, "The outer lip lost its original fold");
        Require(std::abs(green(bent, 12) - green(flat, 12)) <= 2, "The thin fold spread into the body");
        Require(green(bent, 31) == green(flat, 31), "Bending moved the flat interior");
        // A horizontal ramp in Y used to stay straight across a vertical
        // side. Its two ends should now turn towards the centre of the tray.
        for (int y = 0; y < f.height; ++y)
            std::fill_n(checker.begin() + y * f.width, f.width, 0xff000000u | (y * 3u * 0x010101u));
        bendFrame.lens[30][0] = 0;
        auto straightRows = RenderFixture(gpu, bendFrame, checker);
        bendFrame.lens[30][0] = 1.65f;
        auto curvedRows = RenderFixture(gpu, bendFrame, checker);
        auto shift = [&](int x, int y) {
            const int i = y * f.width + x;
            return int((curvedRows[i] >> 8) & 255) - int((straightRows[i] >> 8) & 255);
        };
        Require(shift(4, 12) >= 5 && shift(4, 35) <= -5 && shift(40, 24) == 0,
            "Crossing lines did not bend on the straight side of the lens");
        VerifyFilteredBend(gpu, bendFrame);
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
            const bool forceWgc = std::find_if(argv + 1, argv + argc, [](const char* arg) {
                return strcmp(arg, "--wgc") == 0;
            }) != argv + argc;
            VerifyLiveCapture(gpu, f, hwnd, forceWgc);
        }
        DestroyWindow(hwnd); return 0;
    } catch (HRESULT hr) { printf("FAIL HRESULT 0x%08lx\n", hr); }
      catch (const std::exception& e) { printf("FAIL %s\n", e.what()); }
    DestroyWindow(hwnd); return 1;
}
