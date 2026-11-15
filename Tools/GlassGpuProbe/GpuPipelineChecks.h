#pragma once
#include "../../Native/Controllers/WebcamGpu.cpp"
#include <thread>
#include <atomic>
#include <chrono>

struct ProbeFrameOwner : winrt::implements<ProbeFrameOwner, winrt::Windows::Foundation::IClosable> {
    std::atomic<int>* closes;
    explicit ProbeFrameOwner(std::atomic<int>* value) : closes(value) {}
    void Close() { closes->fetch_add(1); }
};
inline void VerifyCameraGpu(GpuGlass& gpu, IDirect3DDevice9Ex* device9) {
    constexpr int w = 32, h = 16;
    auto input = gpu.MakeTarget(w, h);
    std::vector<uint32_t> pixels(w * h, 0xff563412);
    gpu.context->UpdateSubresource(input.texture.Get(), 0, nullptr, pixels.data(), w * 4, 0);
    ComPtr<IDXGISurface> dxgi; Check(input.texture.As(&dxgi));
    winrt::com_ptr<IInspectable> surface;
    Check(CreateDirect3D11SurfaceFromDXGISurface(dxgi.Get(), surface.put()));
    ComPtr<IDirect3DSurface9> destination, readback;
    Check(device9->CreateRenderTarget(w, h, D3DFMT_X8R8G8B8, D3DMULTISAMPLE_NONE, 0, TRUE, &destination, nullptr));
    Check(device9->CreateOffscreenPlainSurface(w, h, D3DFMT_X8R8G8B8, D3DPOOL_SYSTEMMEM, &readback, nullptr));
    int64_t handle = 0; Check(vn_webcam_gpu_create(device9, w, h, &handle));
    for (int i = 0; i < 30; ++i) {
        std::atomic<int> closes{0};
        auto owner = winrt::make<ProbeFrameOwner>(&closes);
        const auto rawOwner = reinterpret_cast<IUnknown*>(winrt::get_abi(owner));
        ULONGLONG deadline = GetTickCount64() + 5000;
        HRESULT hr;
        while ((hr = vn_webcam_gpu_submit(handle, surface.get(), rawOwner)) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
        Check(hr); Require(hr == S_OK, "Camera upload remained busy");
        Require(closes == 0, "Camera frame closed before its GPU copy retired");
        Require(vn_webcam_gpu_submit(handle, surface.get(), rawOwner) == S_FALSE, "Camera queued an unbounded second frame");
        while ((hr = vn_webcam_gpu_present(handle, destination.Get())) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
        Check(hr); Require(hr == S_OK && closes == 1, "Camera lease was not closed exactly once after completion");
        Check(device9->GetRenderTargetData(destination.Get(), readback.Get()));
        D3DLOCKED_RECT mapped{}; Check(readback->LockRect(&mapped, nullptr, D3DLOCK_READONLY));
        bool equal = true;
        for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x)
            equal &= (reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(mapped.pBits) + y * mapped.Pitch)[x] & 0xffffff) == 0x563412;
        Check(readback->UnlockRect()); Require(equal, "Camera GPU transfer changed channel order or frame pixels");
    }
    vn_webcam_gpu_destroy(handle);
    std::atomic<int> closes{0};
    auto owner = winrt::make<ProbeFrameOwner>(&closes);
    Check(vn_webcam_gpu_create(device9, w, h, &handle));
    Check(vn_webcam_gpu_submit(handle, surface.get(), reinterpret_cast<IUnknown*>(winrt::get_abi(owner))));
    vn_webcam_gpu_destroy(handle);
    const ULONGLONG deadline = GetTickCount64() + 5000;
    while (closes == 0 && GetTickCount64() < deadline) Sleep(1);
    Require(closes == 1, "Camera shutdown leaked or prematurely recycled its source frame");
    printf("PASS: camera GPU pixel transfer, bounded queue, frame leases, pending shutdown (synthetic frames).\n");
}
inline void VerifyOpaqueWindowShader(GpuGlass& gpu) {
    constexpr int w = 32, h = 16;
    auto source = gpu.MakeTarget(w, h), destination = gpu.MakeTarget(w, h);
    std::vector<uint32_t> pixels(w * h, 0xff123456);
    ComPtr<ID3D11Query> query;
    D3D11_QUERY_DESC desc{D3D11_QUERY_OCCLUSION, 0}; Check(gpu.device->CreateQuery(&desc, &query));
    for (int transparent : {0, 1, w * h}) {
        std::fill(pixels.begin(), pixels.end(), 0xff123456);
        std::fill_n(pixels.begin(), transparent, 0);
        gpu.context->UpdateSubresource(source.texture.Get(), 0, nullptr, pixels.data(), w * 4, 0);
        Constants data{}; data.crop[2] = w; data.crop[3] = h; data.desktop[0] = w; data.desktop[1] = h;
        gpu.context->Begin(query.Get());
        gpu.Draw(source.Input(w, h), destination.rtv.Get(), w, h, gpu.sharedDevice->opaqueWindow.Get(), data);
        gpu.context->End(query.Get()); gpu.context->Flush();
        UINT64 samples = 0; HRESULT hr;
        const ULONGLONG deadline = GetTickCount64() + 5000;
        while ((hr = gpu.context->GetData(query.Get(), &samples, sizeof(samples), D3D11_ASYNC_GETDATA_DONOTFLUSH)) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
        Check(hr); Require(hr == S_OK && samples == w * h - transparent, "GPU opacity proof accepted a transparent/empty window frame");
    }
    printf("PASS: GPU window coverage rejects one-pixel holes and fully empty captures without pixel readback.\n");
}
inline void VerifySharedDevice(GpuGlass& first, IDirect3DDevice9Ex* device9) {
    const auto begin = std::chrono::steady_clock::now();
    GpuGlass second; second.Initialize(device9, 128, 96);
    double milliseconds = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - begin).count();
    Require(first.device == second.device && first.vertex == second.vertex, "Presenters duplicated adapter device/shaders");
    std::exception_ptr failure[2]{};
    auto work = [&](GpuGlass& renderer, int index) {
        try {
            Frame frame{}; frame.width = 48; frame.height = 32;
            std::vector<uint32_t> expected(frame.width * frame.height, index ? 0xff276399 : 0xff993721);
            for (int i = 0; i < 20; ++i) Require(NearPixels(RenderFixture(renderer, frame, expected), expected), "Shared immediate context mixed presenter commands");
        } catch (...) { failure[index] = std::current_exception(); }
    };
    std::thread a(work, std::ref(first), 0), b(work, std::ref(second), 1);
    a.join(); b.join();
    for (auto error : failure) if (error) std::rethrow_exception(error);
    printf("PASS: shared adapter resources, 40 concurrent presenter frames; second presenter init %.2f ms.\n", milliseconds);
}
