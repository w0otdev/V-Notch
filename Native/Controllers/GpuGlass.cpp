#include "vnotch_native.h"
#include "vnotch_handle_registry.h"
#include "Graphics/GlassDevice.h"
#include "Graphics/CrossAdapterGlassOutput.h"
#include "WindowBackdropCapture.h"
#include "BackdropLayers.h"
#include "Graphics/WindowBackdrop.h"
#include <d3d9.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi1_6.h>
#include <dxcore.h>
#include <wrl/client.h>
#include <array>
#include <memory>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Security.Authorization.AppCapabilityAccess.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include "vnotch_com.h"
#include <mutex>
#include <chrono>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dxcore.lib")
#pragma comment(lib, "dxguid.lib")
#pragma comment(lib, "windowsapp.lib")

namespace {
using Microsoft::WRL::ComPtr;
namespace Capture = winrt::Windows::Graphics::Capture;
namespace DirectX = winrt::Windows::Graphics::DirectX;
void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
bool SameLuid(LUID a, LUID b) noexcept { return a.HighPart == b.HighPart && a.LowPart == b.LowPart; }
LUID HighPerformanceGlassAdapter() noexcept {
    ComPtr<IDXGIFactory6> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return {};
    ComPtr<IDXCoreAdapterFactory> core;
    DXCoreCreateAdapterFactory(IID_IDXCoreAdapterFactory, reinterpret_cast<void**>(core.GetAddressOf()));
    LUID firstHardware{};
    for (UINT i = 0; ; ++i) {
        ComPtr<IDXGIAdapter1> adapter;
        if (FAILED(factory->EnumAdapterByGpuPreference(i, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                IID_PPV_ARGS(&adapter)))) return firstHardware;
        DXGI_ADAPTER_DESC1 desc{};
        if (FAILED(adapter->GetDesc1(&desc)) || (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) continue;
        if (SameLuid(firstHardware, {})) firstHardware = desc.AdapterLuid;
        // Windows can still rank its per-process power-saving override first.
        // Keep the performance order among discrete GPUs and use the first
        // hardware GPU on systems with no identifiable discrete adapter.
        ComPtr<IDXCoreAdapter> physical;
        bool integrated = true;
        if (core && SUCCEEDED(core->GetAdapterByLuid(desc.AdapterLuid, IID_IDXCoreAdapter,
                reinterpret_cast<void**>(physical.GetAddressOf()))) &&
            SUCCEEDED(physical->GetProperty(DXCoreAdapterProperty::IsIntegrated, &integrated)) && !integrated)
            return desc.AdapterLuid;
    }
}
using namespace vnotch::graphics;
struct SharedTarget {
    ComPtr<IDirect3DTexture9> texture9;
    ComPtr<IDirect3DSurface9> surface;
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11RenderTargetView> rtv;
    ComPtr<IDirect3DQuery9> consumed;
    bool awaitingConsumer = false;
    bool initialized = false;
    int activeWidth = 0, activeHeight = 0;
};
// All entry points are serialized by the presenter's surface lock. Handle leases
// also keep resources alive if shutdown races an in-flight P/Invoke.
class GpuGlass {
public:
    std::mutex stateMutex;
    std::shared_ptr<vnotch::graphics::GlassDevice> sharedDevice;
    ComPtr<ID3D11Device> device;
    ComPtr<IDirect3DDevice9Ex> presentationDevice;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11DeviceContext1> context1;
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<IDXGIOutputDuplication> duplication;
    DirectX::Direct3D11::IDirect3DDevice captureDevice{ nullptr };
    Capture::GraphicsCaptureItem captureItem{ nullptr };
    Capture::Direct3D11CaptureFramePool framePool{ nullptr };
    Capture::GraphicsCaptureSession captureSession{ nullptr };
    Capture::Direct3D11CaptureFrame capturedFrame{ nullptr };
    RECT copiedBox{}; // desktopTexture texels valid for capturedFrame (WGC only)
    std::vector<std::pair<ComPtr<ID3D11Texture2D>, ComPtr<ID3D11ShaderResourceView>>> captureViews;
    winrt::Windows::Graphics::SizeInt32 captureSize{};
    ULONGLONG captureWaitStarted = 0;
    ComPtr<ID3D11Texture2D> desktopTexture;
    ComPtr<ID3D11ShaderResourceView> desktopView;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> crop, down, up, upResolve, resolve, copy, refraction;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11RasterizerState> rasterizer;
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11Query> completed;
    ComPtr<ID3D11DeviceContext4> fenceContext;
    ComPtr<ID3D11Fence> completionFence;
    winrt::handle completionEvent;
    uint64_t fenceValue = 0;
    HRESULT fenceResult = E_NOINTERFACE;
    // Three shared targets: D3D9 can still copy one while D3D11 renders another,
    // so a slow consumer no longer drops the next frame (consumerStalls).
    std::array<SharedTarget, 3> output;
    CrossAdapterGlassOutput crossAdapter;
    std::vector<Target> pyramid;
    std::vector<Target> reconstruction;
    Target resolved;
    Target uploadedBackdrop;
    std::unique_ptr<WindowBackdrop> windowBackdrop;
    winrt::handle windowFrameWake{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
    uint64_t uploadedPixelsHash = 0;
    UINT uploadedPixelsWidth = 0, uploadedPixelsHeight = 0;
    bool hasUploadedPixelsHash = false;
    bool uploadMatchesSubmitted = false; // uploadedBackdrop == last S_OK pixel frame
    bool uploadedDirty = true;
    bool usingUploadedBackdrop = false;
    UINT workWidth = 0, workHeight = 0;
    DXGI_OUTPUT_DESC outputDesc{};
    HMONITOR monitor = nullptr;
    UINT nextOutput = 0;
    int pendingWidth = 0, pendingHeight = 0, presentedWidth = 0, presentedHeight = 0;
    int width = 0, height = 0;
    bool pending = false;
    bool desktopChanged = false;
    bool sourceDirty = true;
    bool hasRenderedFrame = false;
    Frame lastFrame{};
    ULONGLONG retryAt = 0;
    HRESULT captureResult = S_FALSE;
    HRESULT borderlessResult = E_NOINTERFACE;
    int64_t captureIntervalTicks = 166667;
    uint64_t captureFrames = 0;
    uint64_t consumerStalls = 0;
    uint64_t targetAllocations = 0, targetAllocatedBytes = 0;
    std::vector<RECT> dirtyRects;
    std::vector<DXGI_OUTDUPL_MOVE_RECT> moveRects;
    ULONGLONG smallSince = 0;

    void Initialize(IDirect3DDevice9Ex* device9, int w, int h, LUID renderLuid = {}) {
        presentationDevice = device9;
        width = w; height = h;
        D3DDEVICE_CREATION_PARAMETERS creation{};
        Check(device9->GetCreationParameters(&creation));
        ComPtr<IDirect3D9> base9;
        ComPtr<IDirect3D9Ex> direct9;
        Check(device9->GetDirect3D(&base9));
        Check(base9.As(&direct9));
        LUID luid{};
        Check(direct9->GetAdapterLUID(creation.AdapterOrdinal, &luid));
        const bool hasPreference = renderLuid.HighPart || renderLuid.LowPart;
        const bool separateAdapters = hasPreference && !SameLuid(renderLuid, luid);
        ComPtr<IDXGIFactory4> factory;
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
        Check(factory->EnumAdapterByLuid(hasPreference ? renderLuid : luid, IID_PPV_ARGS(&adapter)));
        sharedDevice = vnotch::graphics::GlassDevice::Acquire(adapter.Get());
        device = sharedDevice->device; context = sharedDevice->context;
        vertex = sharedDevice->vertex; crop = sharedDevice->crop;
        down = sharedDevice->down; up = sharedDevice->up; resolve = sharedDevice->resolve;
        copy = sharedDevice->copy; refraction = sharedDevice->refraction; upResolve = sharedDevice->upResolve;
        sampler = sharedDevice->sampler; rasterizer = sharedDevice->rasterizer;
        if (separateAdapters) {
            // One pending frame. D3D9 never reads this D3D11 target directly.
            auto& target = output.front();
            D3D11_TEXTURE2D_DESC desc{};
            desc.Width = w; desc.Height = h; desc.MipLevels = 1; desc.ArraySize = 1;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
            desc.BindFlags = D3D11_BIND_RENDER_TARGET;
            Check(device->CreateTexture2D(&desc, nullptr, &target.texture));
            Check(device->CreateRenderTargetView(target.texture.Get(), nullptr, &target.rtv));
            Check(crossAdapter.Initialize(device.Get(), device9, desc));
        } else {
            for (auto& target : output) {
                HANDLE shared = nullptr; // Legacy shared handles belong to the resource; never CloseHandle.
                Check(device9->CreateTexture(w, h, 1, D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8,
                    D3DPOOL_DEFAULT, &target.texture9, &shared));
                Check(target.texture9->GetSurfaceLevel(0, &target.surface));
                Check(device->OpenSharedResource(shared, IID_PPV_ARGS(&target.texture)));
                Check(device->CreateRenderTargetView(target.texture.Get(), nullptr, &target.rtv));
                Check(device9->CreateQuery(D3DQUERYTYPE_EVENT, &target.consumed));
            }
        }
        D3D11_BUFFER_DESC buffer{};
        buffer.ByteWidth = sizeof(Constants); buffer.Usage = D3D11_USAGE_DEFAULT;
        buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        Check(device->CreateBuffer(&buffer, nullptr, &constants));
        D3D11_QUERY_DESC query{ D3D11_QUERY_EVENT, 0 };
        Check(device->CreateQuery(&query, &completed));
        // Optional on older drivers: retain the query/retry path if unsupported.
        ComPtr<ID3D11Device5> fenceDevice;
        if (SUCCEEDED(fenceResult = device.As(&fenceDevice)) && SUCCEEDED(fenceResult = context.As(&fenceContext)) &&
            SUCCEEDED(fenceResult = fenceDevice->CreateFence(0, D3D11_FENCE_FLAG_NONE, IID_PPV_ARGS(&completionFence)))) {
            completionEvent.attach(CreateEventW(nullptr, FALSE, FALSE, nullptr));
            if (!completionEvent) fenceResult = HRESULT_FROM_WIN32(GetLastError());
        }
    }
    Target MakeTarget(UINT w, UINT h, DXGI_FORMAT format = DXGI_FORMAT_B8G8R8A8_UNORM) {
        Target t; t.width = w; t.height = h;
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = w; desc.Height = h; desc.MipLevels = 1; desc.ArraySize = 1;
        desc.Format = format; desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        Check(device->CreateTexture2D(&desc, nullptr, &t.texture));
        Check(device->CreateShaderResourceView(t.texture.Get(), nullptr, &t.srv));
        Check(device->CreateRenderTargetView(t.texture.Get(), nullptr, &t.rtv));
        ++targetAllocations;
        targetAllocatedBytes += static_cast<uint64_t>(w) * h * (format == DXGI_FORMAT_R16G16B16A16_FLOAT ? 8 : 4);
        return t;
    }
    ~GpuGlass() { ResetCapture(); }
    void ResetCapture() noexcept {
        windowBackdrop.reset();
        if (capturedFrame) { try { capturedFrame.Close(); } catch (...) { } capturedFrame = nullptr; }
        copiedBox = {};
        captureViews.clear();
        try { if (captureSession) captureSession.Close(); } catch (...) { }
        try { if (framePool) framePool.Close(); } catch (...) { }
        captureSession = nullptr; framePool = nullptr; captureItem = nullptr; captureDevice = nullptr;
        duplication.Reset(); desktopView.Reset(); desktopTexture.Reset(); monitor = nullptr;
        uploadedBackdrop = {}; uploadedPixelsHash = 0; hasUploadedPixelsHash = false; uploadMatchesSubmitted = false;
        uploadedPixelsWidth = uploadedPixelsHeight = 0; uploadedDirty = true;
        usingUploadedBackdrop = false;
        hasRenderedFrame = false;
        sourceDirty = true;
    }
    bool StartGraphicsCapture(HMONITOR wanted) {
        // Hybrid graphics can expose a monitor on the discrete adapter while
        // DuplicateOutput only works on the integrated adapter. WGC delivers a
        // texture on our presentation device without a CPU readback bridge.
        MONITORINFO info{ sizeof(info) };
        winrt::check_bool(GetMonitorInfoW(wanted, &info));
        vnotch::com::EnsureMta();
        if (!Capture::GraphicsCaptureSession::IsSupported()) throw DXGI_ERROR_UNSUPPORTED;
        ResetCapture();
        struct Rollback {
            GpuGlass* self;
            ~Rollback() noexcept { if (self) self->ResetCapture(); }
        } rollback{ this };
        auto interop = winrt::get_activation_factory<Capture::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        winrt::check_hresult(interop->CreateForMonitor(wanted,
            winrt::guid_of<Capture::IGraphicsCaptureItem>(), winrt::put_abi(captureItem)));
        ComPtr<IDXGIDevice> dxgiDevice; Check(device.As(&dxgiDevice));
        winrt::com_ptr<IInspectable> inspectable;
        Check(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), inspectable.put()));
        captureDevice = inspectable.as<DirectX::Direct3D11::IDirect3DDevice>();
        captureSize = captureItem.Size();
        framePool = Capture::Direct3D11CaptureFramePool::CreateFreeThreaded(
            captureDevice, DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized, 3, captureSize);
        captureSession = framePool.CreateCaptureSession(captureItem);
        ApplyCaptureInterval();
        captureSession.IsCursorCaptureEnabled(false);
        if (winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(
            L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IsBorderRequired")) {
            // Explicitly requested by the user: ask Windows once per process,
            // before StartCapture so the first session also starts borderless.
            static std::once_flag borderlessAccess;
            static HRESULT borderlessRequest = E_PENDING;
            std::call_once(borderlessAccess, [] {
                try {
                    auto status = Capture::GraphicsCaptureAccess::RequestAccessAsync(Capture::GraphicsCaptureAccessKind::Borderless).get();
                    borderlessRequest = status == winrt::Windows::Security::Authorization::AppCapabilityAccess::AppCapabilityAccessStatus::Allowed ? S_OK : E_ACCESSDENIED;
                }
                catch (...) { borderlessRequest = winrt::to_hresult(); }
            });
            captureSession.IsBorderRequired(false);
            borderlessResult = borderlessRequest;
        }
        captureSession.StartCapture();
        outputDesc.DesktopCoordinates = info.rcMonitor;
        outputDesc.Rotation = DXGI_MODE_ROTATION_IDENTITY; // WGC frames are already oriented.
        monitor = wanted; retryAt = 0;
        captureResult = S_FALSE;
        rollback.self = nullptr;
        return true;
    }
    void ApplyCaptureInterval() noexcept {
        // The worker enforces the user's FPS cap. WGC's default ~16 ms interval
        // can miss two out of three vblanks at 144 Hz; a full frame budget here
        // also drops alternate frames under phase jitter. Use the supported
        // 1 ms minimum (zero/sub-ms values fail on some Windows builds).
        try {
            if (captureSession && winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(
                L"Windows.Graphics.Capture.GraphicsCaptureSession", L"MinUpdateInterval"))
                captureSession.MinUpdateInterval(std::chrono::milliseconds(1));
        } catch (...) { }
    }
    void SetCaptureInterval(double ms) {
        const int64_t ticks = std::clamp(static_cast<int64_t>(std::llround(ms * 10000.0)), int64_t{10000}, int64_t{10000000});
        if (ticks == captureIntervalTicks) return;
        captureIntervalTicks = ticks;
        ApplyCaptureInterval();
    }
    bool EnsureOutput(const Frame& f) {
        RECT region{ f.x, f.y, f.x + f.width, f.y + f.height };
        HMONITOR wanted = MonitorFromRect(&region, MONITOR_DEFAULTTONEAREST);
        if (wanted == monitor && (duplication || framePool)) return true;
        ResetCapture();
        if (GetTickCount64() < retryAt) return false;
        retryAt = GetTickCount64() + 1000;
        for (UINT i = 0; ; ++i) {
            ComPtr<IDXGIOutput> out;
            if (adapter->EnumOutputs(i, &out) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_OUTPUT_DESC desc{};
            Check(out->GetDesc(&desc));
            if (desc.Monitor != wanted || !desc.AttachedToDesktop) continue;
            ComPtr<IDXGIOutput1> out1;
            Check(out.As(&out1));
            ComPtr<IDXGIOutput5> out5;
            HRESULT hr;
            if (SUCCEEDED(out.As(&out5))) {
                const DXGI_FORMAT formats[] = { DXGI_FORMAT_B8G8R8A8_UNORM };
                hr = out5->DuplicateOutput1(device.Get(), 0, ARRAYSIZE(formats), formats, &duplication);
            } else {
                hr = out1->DuplicateOutput(device.Get(), &duplication);
            }
            captureResult = hr;
            if (hr == DXGI_ERROR_UNSUPPORTED || hr == DXGI_ERROR_NOT_CURRENTLY_AVAILABLE)
                return StartGraphicsCapture(wanted);
            // Desktop/session transitions are recoverable. Device removal is not.
            if (hr == E_ACCESSDENIED)
                return false;
            Check(hr);
            outputDesc = desc; monitor = wanted; retryAt = 0;
            return true;
        }
        // WGC also handles a window moved to a monitor on another adapter.
        return StartGraphicsCapture(wanted);
    }
    // Monitor-local texels cropPs can read for this lens: bilinear taps around
    // the crop, clamped like the shader clamps to the monitor. Never empty.
    RECT NeededBox(const Frame& f, UINT textureWidth, UINT textureHeight) const noexcept {
        const LONG w = static_cast<LONG>(textureWidth), h = static_cast<LONG>(textureHeight);
        const LONG x = f.x - outputDesc.DesktopCoordinates.left, y = f.y - outputDesc.DesktopCoordinates.top;
        RECT r{};
        r.left = std::clamp<LONG>(x - 2, 0, w - 1);
        r.top = std::clamp<LONG>(y - 2, 0, h - 1);
        r.right = std::clamp<LONG>(x + f.width + 2, r.left + 1, w);
        r.bottom = std::clamp<LONG>(y + f.height + 2, r.top + 1, h);
        return r;
    }
    static RECT ExpandBox(RECT r, UINT textureWidth, UINT textureHeight) noexcept {
        constexpr LONG slack = 64;
        return { std::max<LONG>(0, r.left - slack), std::max<LONG>(0, r.top - slack),
            std::min<LONG>(static_cast<LONG>(textureWidth), r.right + slack),
            std::min<LONG>(static_cast<LONG>(textureHeight), r.bottom + slack) };
    }
    static bool BoxContains(const RECT& outer, const RECT& inner) noexcept {
        return outer.right > outer.left && outer.bottom > outer.top &&
            outer.left <= inner.left && outer.top <= inner.top &&
            outer.right >= inner.right && outer.bottom >= inner.bottom;
    }
    void CopyBoxFrom(ID3D11Texture2D* source, const RECT& r) {
        const D3D11_BOX box{ static_cast<UINT>(r.left), static_cast<UINT>(r.top), 0,
            static_cast<UINT>(r.right), static_cast<UINT>(r.bottom), 1 };
        context->CopySubresourceRegion(desktopTexture.Get(), 0, box.left, box.top, 0, source, 0, &box);
    }
    // No new WGC frame: if the lens moved outside the copied box, recopy the
    // new box from the held snapshot so the source stays exact.
    bool RecopyHeldFrame(const Frame& requested) {
        if (!desktopTexture) return false;
        if (!capturedFrame) return true;
        D3D11_TEXTURE2D_DESC desc{}; desktopTexture->GetDesc(&desc);
        const RECT needed = NeededBox(requested, desc.Width, desc.Height);
        if (BoxContains(copiedBox, needed)) return true;
        auto access = capturedFrame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        ComPtr<ID3D11Texture2D> held;
        Check(access->GetInterface(IID_PPV_ARGS(&held)));
        D3D11_TEXTURE2D_DESC heldDesc{}; held->GetDesc(&heldDesc);
        if (heldDesc.Width != desc.Width || heldDesc.Height != desc.Height || heldDesc.Format != desc.Format) return true;
        const RECT box = ExpandBox(needed, desc.Width, desc.Height);
        CopyBoxFrom(held.Get(), box);
        copiedBox = box;
        desktopChanged = true;
        return true;
    }
    bool AcquireDesktop(const Frame& requested) {
        desktopChanged = false;
        if (framePool) {
            auto frame = framePool.TryGetNextFrame();
            struct Close { Capture::Direct3D11CaptureFrame& f; ~Close() noexcept { try { if (f) f.Close(); } catch (...) { } } } close{ frame };
            if (!frame) {
                return RecopyHeldFrame(requested);
            }
            // Drain queued frames after UI/GPU contention so the current lens
            // always renders the newest completed desktop snapshot.
            for (int i = 0; i < 3; ++i) {
                auto newer = framePool.TryGetNextFrame();
                if (!newer) break;
                frame.Close();
                frame = newer;
            }
            const auto size = frame.ContentSize();
            if (size.Width <= 0 || size.Height <= 0) { frame.Close(); frame = nullptr; return RecopyHeldFrame(requested); }
            if (size.Width != captureSize.Width || size.Height != captureSize.Height) {
                frame.Close(); frame = nullptr;
                ResetCapture();
                return false;
            }
            auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            ComPtr<ID3D11Texture2D> acquired;
            Check(access->GetInterface(IID_PPV_ARGS(&acquired)));
            D3D11_TEXTURE2D_DESC acquiredDesc{}; acquired->GetDesc(&acquiredDesc);
            if (acquiredDesc.Width != static_cast<UINT>(size.Width) || acquiredDesc.Height != static_cast<UINT>(size.Height)) {
                // WGC can return an old-sized allocation during a display resize.
                // Keep the last good image instead of copying an invalid surface.
                frame.Close(); frame = nullptr; return RecopyHeldFrame(requested);
            }
            D3D11_TEXTURE2D_DESC currentDesc{};
            if (desktopTexture) desktopTexture->GetDesc(&currentDesc);
            if (!desktopTexture || currentDesc.Width != acquiredDesc.Width || currentDesc.Height != acquiredDesc.Height || currentDesc.Format != acquiredDesc.Format) {
                desktopView.Reset();
                desktopTexture.Reset();
                D3D11_TEXTURE2D_DESC desc = acquiredDesc;
                desc.Usage = D3D11_USAGE_DEFAULT; desc.CPUAccessFlags = 0; desc.MiscFlags = 0;
                desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                Check(device->CreateTexture2D(&desc, nullptr, &desktopTexture));
                Check(device->CreateShaderResourceView(desktopTexture.Get(), nullptr, &desktopView));
            }
            // cropPs only samples inside the lens crop. Copy that box (plus slack
            // for small lens motion) instead of the whole monitor every frame.
            const RECT box = ExpandBox(NeededBox(requested, acquiredDesc.Width, acquiredDesc.Height), acquiredDesc.Width, acquiredDesc.Height);
            CopyBoxFrom(acquired.Get(), box);
            // Hold the newest frame (pool of 3) so a moved lens can recopy its
            // new box from the same snapshot while the desktop stays static.
            if (capturedFrame) { capturedFrame.Close(); capturedFrame = nullptr; }
            capturedFrame = frame; frame = nullptr;
            copiedBox = box;
            ++captureFrames;
            desktopChanged = true; captureResult = S_OK;
            return true;
        }
        ComPtr<IDXGIResource> resource;
        DXGI_OUTDUPL_FRAME_INFO info{};
        HRESULT hr = duplication->AcquireNextFrame(0, &info, &resource);
        captureResult = hr;
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return desktopTexture != nullptr;
        if (hr == DXGI_ERROR_ACCESS_LOST) { ResetCapture(); return false; }
        Check(hr);
        // ReleaseFrame on every successful acquisition, including allocation failures.
        struct Release { IDXGIOutputDuplication* value; ~Release() { value->ReleaseFrame(); } } release{ duplication.Get() };
        ComPtr<ID3D11Texture2D> acquired;
        Check(resource.As(&acquired));
        if (!desktopTexture) {
            D3D11_TEXTURE2D_DESC desc{}; acquired->GetDesc(&desc);
            desc.Usage = D3D11_USAGE_DEFAULT; desc.CPUAccessFlags = 0; desc.MiscFlags = 0;
            desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            Check(device->CreateTexture2D(&desc, nullptr, &desktopTexture));
            Check(device->CreateShaderResourceView(desktopTexture.Get(), nullptr, &desktopView));
        }
        // Pointer-only desktop updates do not invalidate the backdrop.
        if (info.LastPresentTime.QuadPart != 0 || !hasRenderedFrame) {
            ++captureFrames;
            bool fullCopy = !hasRenderedFrame || info.TotalMetadataBufferSize == 0;
            UINT dirtyBytes = 0, moveBytes = 0;
            if (!fullCopy) {
                dirtyRects.resize(info.TotalMetadataBufferSize / sizeof(RECT) + 1);
                moveRects.resize(info.TotalMetadataBufferSize / sizeof(DXGI_OUTDUPL_MOVE_RECT) + 1);
                fullCopy = FAILED(duplication->GetFrameDirtyRects(static_cast<UINT>(dirtyRects.size() * sizeof(RECT)), dirtyRects.data(), &dirtyBytes)) ||
                    FAILED(duplication->GetFrameMoveRects(static_cast<UINT>(moveRects.size() * sizeof(DXGI_OUTDUPL_MOVE_RECT)), moveRects.data(), &moveBytes));
            }
            if (fullCopy) {
                context->CopyResource(desktopTexture.Get(), acquired.Get());
                desktopChanged = true;
            } else {
                RECT region{ requested.x - outputDesc.DesktopCoordinates.left, requested.y - outputDesc.DesktopCoordinates.top,
                    requested.x - outputDesc.DesktopCoordinates.left + requested.width,
                    requested.y - outputDesc.DesktopCoordinates.top + requested.height };
                auto copyRect = [&](RECT rect) {
                    D3D11_BOX box{ static_cast<UINT>(rect.left), static_cast<UINT>(rect.top), 0,
                        static_cast<UINT>(rect.right), static_cast<UINT>(rect.bottom), 1 };
                    if (rect.right <= rect.left || rect.bottom <= rect.top) return;
                    context->CopySubresourceRegion(desktopTexture.Get(), 0, box.left, box.top, 0, acquired.Get(), 0, &box);
                    RECT intersection{};
                    // Rotation changes the metadata coordinate system. Copy the
                    // changed regions but conservatively refresh the rotated lens.
                    desktopChanged |= outputDesc.Rotation != DXGI_MODE_ROTATION_IDENTITY || IntersectRect(&intersection, &region, &rect);
                };
                for (UINT i = 0; i < dirtyBytes / sizeof(RECT); ++i) copyRect(dirtyRects[i]);
                for (UINT i = 0; i < moveBytes / sizeof(DXGI_OUTDUPL_MOVE_RECT); ++i) copyRect(moveRects[i].DestinationRect);
            }
        }
        return true;
    }
    void Draw(TextureInput source, ID3D11RenderTargetView* dest, UINT w, UINT h,
              ID3D11PixelShader* shader, Constants data, TextureInput detail = {}, float x = 0, float y = 0) {
        std::lock_guard commandLock(sharedDevice->commands);
        auto region = [](float* out, TextureInput input) {
            out[0] = static_cast<float>(input.activeWidth) / input.width;
            out[1] = static_cast<float>(input.activeHeight) / input.height;
            out[2] = 0.5f / input.width; out[3] = 0.5f / input.height;
        };
        region(data.sourceRegion, source); region(data.detailRegion, detail);
        context->UpdateSubresource(constants.Get(), 0, nullptr, &data, 0, 0);
        ID3D11Buffer* cb = constants.Get(); context->PSSetConstantBuffers(0, 1, &cb);
        ID3D11SamplerState* smp = sampler.Get(); context->PSSetSamplers(0, 1, &smp);
        context->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
        context->OMSetDepthStencilState(nullptr, 0);
        context->GSSetShader(nullptr, nullptr, 0);
        context->HSSetShader(nullptr, nullptr, 0); context->DSSetShader(nullptr, nullptr, 0);
        context->OMSetRenderTargets(1, &dest, nullptr);
        D3D11_VIEWPORT vp{ x, y, static_cast<float>(w), static_cast<float>(h), 0, 1 };
        context->RSSetViewports(1, &vp); context->RSSetState(rasterizer.Get());
        context->IASetInputLayout(nullptr); context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex.Get(), nullptr, 0); context->PSSetShader(shader, nullptr, 0);
        ID3D11ShaderResourceView* inputs[] = { source.view, detail.view };
        context->PSSetShaderResources(0, 2, inputs); context->Draw(3, 0);
        ID3D11ShaderResourceView* empty[2]{}; context->PSSetShaderResources(0, 2, empty);
        context->OMSetRenderTargets(0, nullptr, nullptr);
    }
    HRESULT Render(const Frame& f) {
        std::lock_guard commandLock(sharedDevice->commands);
        struct DpiScope {
            DPI_AWARENESS_CONTEXT previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            ~DpiScope() { if (previous) SetThreadDpiAwarenessContext(previous); }
        } dpi;
        if (pending) return S_FALSE;
        if (!EnsureOutput(f) || !AcquireDesktop(f)) {
            const ULONGLONG now = GetTickCount64();
            if (!captureWaitStarted) captureWaitStarted = now;
            // Never leave WPF waiting indefinitely for its first complete frame.
            // The controller reports this error, switches renderer, and retries.
            if (now - captureWaitStarted >= 5000)
                throw FAILED(captureResult) && captureResult != DXGI_ERROR_WAIT_TIMEOUT
                    ? captureResult : HRESULT_FROM_WIN32(ERROR_TIMEOUT);
            return S_FALSE;
        }
        captureWaitStarted = 0;
        sourceDirty |= desktopChanged;
        if (workWidth && static_cast<uint64_t>(f.width) * f.height * 2 < static_cast<uint64_t>(workWidth) * workHeight) {
            if (!smallSince) smallSince = GetTickCount64();
            if (GetTickCount64() - smallSince >= 1000) {
                workWidth = workHeight = 0;
                pyramid.clear(); reconstruction.clear(); resolved = {};
                sourceDirty = true; smallSince = 0;
            }
        } else smallSince = 0;
        Frame normalized = f; normalized.refract &= 1;
        if (hasRenderedFrame && !sourceDirty && !(f.refract & 2) &&
            memcmp(&normalized, &lastFrame, sizeof(Frame)) == 0) return S_FALSE;
        bool rebuildSource = !hasRenderedFrame || sourceDirty || f.x != lastFrame.x || f.y != lastFrame.y ||
            f.width != lastFrame.width || f.height != lastFrame.height || f.blur != lastFrame.blur;
        HRESULT hr = RenderCaptured(f, rebuildSource);
        if (hr == S_OK) { lastFrame = normalized; hasRenderedFrame = true; sourceDirty = false; }
        return hr;
    }
    // S_OK submitted; S_FALSE unchanged/busy; E_PENDING requests the CPU path.
    HRESULT RenderWindows(const Frame& f) {
        std::unique_lock commandLock(sharedDevice->commands);
        if (pending) return S_FALSE;
        if (!usingUploadedBackdrop) { ResetCapture(); usingUploadedBackdrop = true; }
        if (!windowBackdrop) windowBackdrop = std::make_unique<WindowBackdrop>(sharedDevice);
        Target composed;
        bool changed = false;
        const HRESULT captured = windowBackdrop->Capture(f, device.Get(), context.Get(), windowFrameWake.get(), commandLock,
            [&](UINT w, UINT h) { return MakeTarget(std::min<UINT>(width, (w + 127) / 128 * 128), std::min<UINT>(height, (h + 63) / 64 * 64)); },
            [&](const auto& source, ID3D11RenderTargetView* destination, const RECT& hit, const RECT& area) {
                Constants data{};
                data.crop[0] = static_cast<float>(source.crop.left); data.crop[1] = static_cast<float>(source.crop.top);
                data.crop[2] = static_cast<float>(source.crop.right - source.crop.left);
                data.crop[3] = static_cast<float>(source.crop.bottom - source.crop.top);
                data.desktop[0] = static_cast<float>(source.width); data.desktop[1] = static_cast<float>(source.height);
                Draw({source.view.Get(), source.width, source.height, source.width, source.height}, destination,
                    hit.right - hit.left, hit.bottom - hit.top, sharedDevice->opaqueWindow.Get(), data, {},
                    static_cast<float>(hit.left - area.left), static_cast<float>(hit.top - area.top));
            }, composed, changed);
        if (captured != S_OK) return captured;
        // A busy shared output may defer this submission. Keep the source
        // dirty until the resolved texture actually contains the new pixels.
        sourceDirty |= desktopTexture.Get() != composed.texture.Get() || changed;
        desktopTexture = composed.texture; desktopView = composed.srv;
        usingUploadedBackdrop = true;
        outputDesc.DesktopCoordinates = {f.x, f.y, f.x + f.width, f.y + f.height};
        outputDesc.Rotation = DXGI_MODE_ROTATION_IDENTITY;
        Frame normalized = f; normalized.refract &= 1;
        if (hasRenderedFrame && !sourceDirty && !(f.refract & 2) && memcmp(&normalized, &lastFrame, sizeof(Frame)) == 0) return S_FALSE;
        HRESULT hr = RenderCaptured(f, sourceDirty || !hasRenderedFrame || f.blur != lastFrame.blur ||
            f.width != lastFrame.width || f.height != lastFrame.height);
        if (hr == S_OK) { lastFrame = normalized; hasRenderedFrame = true; sourceDirty = false; ++captureFrames; }
        return hr;
    }
    static uint64_t HashPixelRows(const uint8_t* pixels, int width, int height, UINT stride) noexcept {
        uint64_t h = 0x9E3779B97F4A7C15ull ^ (static_cast<uint64_t>(width) << 32) ^ static_cast<uint64_t>(height);
        const size_t rowWords64 = static_cast<size_t>(width) / 2;
        const bool tailPixel = (width & 1) != 0;
        for (int row = 0; row < height; ++row) {
            const uint8_t* line = pixels + static_cast<size_t>(row) * stride;
            const auto* words = reinterpret_cast<const uint64_t*>(line);
            for (size_t i = 0; i < rowWords64; ++i) {
                uint64_t v = words[i];
                h ^= v;
                h *= 0xBF58476D1CE4E5B9ull;
            }
            if (tailPixel) {
                uint32_t tail = 0;
                memcpy(&tail, line + rowWords64 * 8, 4);
                h ^= tail;
                h *= 0x94D049BB133111EBull;
            }
        }
        return h ^ (h >> 31);
    }

    HRESULT RenderPixels(const Frame& f, const uint8_t* pixels, UINT stride) {
        std::lock_guard commandLock(sharedDevice->commands);
        if (pending) return S_FALSE;
        if (!usingUploadedBackdrop) ResetCapture();
        usingUploadedBackdrop = true;
        if (f.width <= 0 || f.height <= 0 || !pixels) return E_INVALIDARG;
        const size_t rowBytes = static_cast<size_t>(f.width) * 4;
        if (stride < rowBytes) return E_INVALIDARG;
        const bool sizeChanged = static_cast<UINT>(f.width) != uploadedPixelsWidth ||
            static_cast<UINT>(f.height) != uploadedPixelsHeight ||
            !hasUploadedPixelsHash || !uploadedBackdrop.texture;
        // refract bit 4: the caller's full-frame hash says these pixels equal its last
        // submitted pixel frame. Trust it only while our texture still holds exactly
        // that frame (no upload since the last successful render); skip the rehash.
        const bool callerUnchanged = (f.refract & 4) != 0 && uploadMatchesSubmitted && !sizeChanged;
        const uint64_t hash = callerUnchanged ? uploadedPixelsHash : HashPixelRows(pixels, f.width, f.height, stride);
        bool pixelsDiffer = sizeChanged || hash != uploadedPixelsHash;
        if (static_cast<UINT>(f.width) > uploadedBackdrop.width || static_cast<UINT>(f.height) > uploadedBackdrop.height) {
            const UINT w = std::min(static_cast<UINT>(width), std::max(uploadedBackdrop.width, (static_cast<UINT>(f.width) + 127) / 128 * 128));
            const UINT h = std::min(static_cast<UINT>(height), std::max(uploadedBackdrop.height, (static_cast<UINT>(f.height) + 63) / 64 * 64));
            uploadedBackdrop = MakeTarget(w, h);
            pixelsDiffer = true;
        }
        if (pixelsDiffer) {
            const D3D11_BOX region{ 0, 0, 0, static_cast<UINT>(f.width), static_cast<UINT>(f.height), 1 };
            context->UpdateSubresource(uploadedBackdrop.texture.Get(), 0, &region, pixels, stride, 0);
            uploadedPixelsHash = hash;
            hasUploadedPixelsHash = true;
            uploadedPixelsWidth = static_cast<UINT>(f.width);
            uploadedPixelsHeight = static_cast<UINT>(f.height);
            uploadedDirty = true;
            uploadMatchesSubmitted = false;
            ++captureFrames;
        }
        uploadedDirty |= desktopTexture.Get() != uploadedBackdrop.texture.Get();
        desktopTexture = uploadedBackdrop.texture; desktopView = uploadedBackdrop.srv;
        outputDesc.DesktopCoordinates = { f.x, f.y, f.x + f.width, f.y + f.height };
        outputDesc.Rotation = DXGI_MODE_ROTATION_IDENTITY;
        captureResult = S_OK;
        Frame normalized = f; normalized.refract &= 1;
        if (hasRenderedFrame && !uploadedDirty && !(f.refract & 2) &&
            memcmp(&normalized, &lastFrame, sizeof(Frame)) == 0) return S_FALSE;
        const bool rebuildSource = !hasRenderedFrame || uploadedDirty || f.x != lastFrame.x || f.y != lastFrame.y ||
            f.width != lastFrame.width || f.height != lastFrame.height || f.blur != lastFrame.blur;
        const HRESULT hr = RenderCaptured(f, rebuildSource);
        if (hr == S_OK) {
            lastFrame = normalized;
            hasRenderedFrame = true;
            sourceDirty = false;
            uploadedDirty = false;
            uploadMatchesSubmitted = true;
            // A window query that began before the pixel handoff must not
            // replace this frame when its GPU result arrives later.
            if (windowBackdrop) windowBackdrop->Supersede();
        }
        return hr;
    }
    HRESULT RenderCaptured(const Frame& f, bool rebuildSource = true) {
        std::lock_guard commandLock(sharedDevice->commands);
        auto& target = output[nextOutput];
        if (target.awaitingConsumer) {
            // D3D11 must not recycle a shared source while D3D9 still copies it.
            BOOL consumed = FALSE;
            HRESULT hr = target.consumed->GetData(&consumed, sizeof(consumed), D3DGETDATA_FLUSH);
            Check(hr);
            if (hr == S_FALSE || !consumed) { ++consumerStalls; return S_FALSE; }
            target.awaitingConsumer = false;
        }
        const float effectiveBlur = f.blur < 1.0f ? f.blur * f.blur * (std::pow(1.0f / 28.0f, 0.55f) * 28.0f) : std::clamp(std::pow(f.blur / 28.0f, 0.55f) * 28.0f, 0.0f, 60.0f);
        int levels = effectiveBlur == 0.0f ? 0 : std::clamp(static_cast<int>(std::ceil(std::log2(std::max(1.0f, effectiveBlur)))), 1, 5);
        if (static_cast<UINT>(f.width) > workWidth || static_cast<UINT>(f.height) > workHeight) {
            // Grow in bounded buckets, retain the peak capacity across view
            // transitions, and render only the active subrectangle. Never shrink
            // and recreate the whole pyramid on every animated/idle size change.
            workWidth = std::min(static_cast<UINT>(width), std::max(workWidth, (static_cast<UINT>(f.width) + 127) / 128 * 128));
            workHeight = std::min(static_cast<UINT>(height), std::max(workHeight, (static_cast<UINT>(f.height) + 63) / 64 * 64));
            pyramid.clear(); reconstruction.clear(); resolved = {};
            rebuildSource = true;
        }
        pyramid.resize(std::max(pyramid.size(), static_cast<size_t>(levels + 1)));
        reconstruction.resize(std::max(reconstruction.size(), static_cast<size_t>(levels)));
        std::array<UINT, 6> activeW{}, activeH{};
        UINT w = workWidth, h = workHeight;
        for (int i = 0; i <= levels; ++i) {
            activeW[i] = i == 0 ? f.width : std::max(1u, (activeW[i-1] + 1) / 2);
            activeH[i] = i == 0 ? f.height : std::max(1u, (activeH[i-1] + 1) / 2);
            if (!pyramid[i].texture) pyramid[i] = MakeTarget(w, h, DXGI_FORMAT_R16G16B16A16_FLOAT);
            if (i > 0 && i < levels && !reconstruction[i].texture) reconstruction[i] = MakeTarget(w, h, DXGI_FORMAT_R16G16B16A16_FLOAT);
            w = std::max(1u, (w + 1) / 2); h = std::max(1u, (h + 1) / 2);
        }
        if (!resolved.texture) resolved = MakeTarget(workWidth, workHeight);
        Constants data{}; memcpy(data.lens, f.lens, sizeof(data.lens));
        data.crop[0] = static_cast<float>(f.x - outputDesc.DesktopCoordinates.left);
        data.crop[1] = static_cast<float>(f.y - outputDesc.DesktopCoordinates.top);
        data.crop[2] = static_cast<float>(f.width); data.crop[3] = static_cast<float>(f.height);
        data.desktop[0] = static_cast<float>(outputDesc.DesktopCoordinates.right - outputDesc.DesktopCoordinates.left);
        data.desktop[1] = static_cast<float>(outputDesc.DesktopCoordinates.bottom - outputDesc.DesktopCoordinates.top);
        data.desktop[2] = static_cast<float>(outputDesc.Rotation);
        if (rebuildSource) {
            D3D11_TEXTURE2D_DESC desktopDesc{}; desktopTexture->GetDesc(&desktopDesc);
            Draw({ desktopView.Get(), desktopDesc.Width, desktopDesc.Height,
                    usingUploadedBackdrop ? static_cast<UINT>(f.width) : desktopDesc.Width,
                    usingUploadedBackdrop ? static_cast<UINT>(f.height) : desktopDesc.Height },
                pyramid[0].rtv.Get(), f.width, f.height, crop.Get(), data);
            data.pass[2] = std::max(1.0f, effectiveBlur / 16.0f);
            const float upper = static_cast<float>(1 << levels);
            const float lower = levels == 1 ? 0.0f : upper * 0.5f;
            const float fraction = std::clamp((effectiveBlur - lower) / std::max(1.0f, upper - lower), 0.0f, 1.0f);
            const float blend = fraction * fraction * (3.0f - 2.0f * fraction);
            for (int i = 1; i <= levels; ++i) {
                data.pass[0] = 1.0f / activeW[i-1]; data.pass[1] = 1.0f / activeH[i-1];
                Draw(pyramid[i-1].Input(activeW[i-1], activeH[i-1]), pyramid[i].rtv.Get(), activeW[i], activeH[i], down.Get(), data);
            }
            for (int i = levels; i > 0; --i) {
                data.pass[0] = 1.0f / activeW[i]; data.pass[1] = 1.0f / activeH[i];
                data.pass[3] = i == levels ? blend : 1.0f;
                auto input = i == levels ? pyramid[i].Input(activeW[i], activeH[i]) : reconstruction[i].Input(activeW[i], activeH[i]);
                // The last up pass resolves to sRGB directly (upResolvePs): no
                // full-resolution FP16 reconstruction[0] write and re-read.
                const bool last = i == 1;
                Draw(input, last ? resolved.rtv.Get() : reconstruction[i-1].rtv.Get(), activeW[i-1], activeH[i-1],
                    last ? upResolve.Get() : up.Get(), data, pyramid[i-1].Input(activeW[i-1], activeH[i-1]));
            }
            if (!levels)
                Draw(pyramid[0].Input(f.width, f.height), resolved.rtv.Get(), f.width, f.height, resolve.Get(), data);
        }
        const float clear[4]{};
        if (!context1) context.As(&context1);
        if (!target.initialized || !context1) context->ClearRenderTargetView(target.rtv.Get(), clear);
        else {
            D3D11_RECT rect{ 0, 0, std::max({f.width, target.activeWidth, presentedWidth}),
                std::max({f.height, target.activeHeight, presentedHeight}) };
            context1->ClearView(target.rtv.Get(), clear, &rect, 1);
        }
        target.initialized = true;
        target.activeWidth = pendingWidth = f.width; target.activeHeight = pendingHeight = f.height;
        if (f.refract & 1) PrepareLensConstants(data.lens);
        Draw(resolved.Input(f.width, f.height), target.rtv.Get(), f.width, f.height, (f.refract & 1) ? refraction.Get() : copy.Get(), data);
        if (crossAdapter.readback) crossAdapter.QueueCopy(context.Get(), target.texture.Get(), PresentRegion());
        context->End(completed.Get());
        if (completionFence && completionEvent) Check(fenceContext->Signal(completionFence.Get(), ++fenceValue));
        context->Flush(); pending = true;
        Check(device->GetDeviceRemovedReason());
        return S_OK;
    }
    HRESULT WaitReady(DWORD timeoutMs) {
        if (!pending || !completionFence || !completionEvent) return S_FALSE;
        if (CompletionReady() == S_OK) return S_OK;
        // An earlier frame may have completed without consuming its event.
        // Never mistake that stale notification for completion of this frame.
        ResetEvent(completionEvent.get());
        Check(completionFence->SetEventOnCompletion(fenceValue, completionEvent.get()));
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs);
        for (;;) {
            if (CompletionReady() == S_OK) return S_OK;
            const auto remaining = std::chrono::duration_cast<std::chrono::microseconds>(deadline - std::chrono::steady_clock::now()).count();
            if (remaining <= 0) return S_FALSE;
            const DWORD result = WaitForSingleObject(completionEvent.get(), static_cast<DWORD>((remaining + 999) / 1000));
            if (result == WAIT_FAILED) return HRESULT_FROM_WIN32(GetLastError());
            Check(device->GetDeviceRemovedReason());
            if (result == WAIT_TIMEOUT) return CompletionReady();
            // Also tolerate an old notification delivered after ResetEvent.
        }
    }
    HRESULT CompletionReady() {
        if (!pending) return S_FALSE;
        // A completed fence is authoritative. EVENT query results can remain
        // unavailable in the driver's CPU cache for another compositor tick.
        // ID3D11Fence is free-threaded: the UI-thread present must not wait for
        // another pipeline (Spotlight/webcam) recording on the shared context.
        if (completionFence && completionEvent) {
            const uint64_t value = completionFence->GetCompletedValue();
            if (value == UINT64_MAX) { Check(device->GetDeviceRemovedReason()); return S_FALSE; }
            return value >= fenceValue ? S_OK : S_FALSE;
        }
        std::lock_guard commandLock(sharedDevice->commands);
        BOOL ready = FALSE;
        HRESULT hr = context->GetData(completed.Get(), &ready, sizeof(ready), D3D11_ASYNC_GETDATA_DONOTFLUSH);
        Check(hr);
        return hr == S_OK && ready ? S_OK : S_FALSE;
    }
    RECT PresentRegion() const noexcept {
        // Initialize the entire persistent WPF surface once. Include the old
        // extent on subsequent transfers to clear pixels when the glass shrinks.
        if (!presentedWidth || !presentedHeight) return {0, 0, width, height};
        return {0, 0, std::max(pendingWidth, presentedWidth), std::max(pendingHeight, presentedHeight)};
    }
    HRESULT PresentReady(IDirect3DSurface9* destination) {
        if (CompletionReady() != S_OK) return S_FALSE;
        const RECT region = PresentRegion();
        if (crossAdapter.readback) {
            std::lock_guard commands(sharedDevice->commands);
            HRESULT hr = crossAdapter.Present(context.Get(), presentationDevice.Get(), destination, region);
            if (hr == S_OK) { presentedWidth = pendingWidth; presentedHeight = pendingHeight; pending = false; }
            return hr;
        }
        auto& target = output[nextOutput];
        Check(presentationDevice->StretchRect(target.surface.Get(), &region, destination, &region, D3DTEXF_NONE));
        presentedWidth = pendingWidth; presentedHeight = pendingHeight;
        Check(target.consumed->Issue(D3DISSUE_END));
        // Submit the copy now, rather than first flushing it on source reuse.
        HRESULT consumed = target.consumed->GetData(nullptr, 0, D3DGETDATA_FLUSH);
        Check(consumed);
        target.awaitingConsumer = consumed != S_OK;
        nextOutput = (nextOutput + 1) % static_cast<UINT>(output.size()); pending = false;
        return S_OK;
    }
};
vnotch::ImmortalHandleRegistry<GpuGlass> pipelines;

// Readouts, presentation and reset all share this pipeline's mutable state.
// The adapter lock separately protects D3D11 commands across pipelines.
class LockedPipeline {
    decltype(pipelines)::Lease lease;
    std::unique_lock<std::mutex> lock;
public:
    explicit LockedPipeline(int64_t handle) : lease(pipelines.Acquire(handle)) {
        if (lease) lock = std::unique_lock(lease->stateMutex);
    }
    explicit operator bool() const noexcept { return static_cast<bool>(lease); }
    GpuGlass* operator->() const noexcept { return lease.get(); }
};
}

VN_EXPORT int32_t __stdcall vn_glass_gpu_create_on_adapter(IDirect3DDevice9Ex* device9, int32_t w, int32_t h,
    LUID renderLuid, int64_t* handle, wchar_t* adapterName, uint32_t nameCapacity) noexcept {
    if (!device9 || !handle || w <= 0 || h <= 0 || w > 8192 || h > 8192) return E_INVALIDARG;
    *handle = 0;
    try {
        auto pipeline = std::make_unique<GpuGlass>(); pipeline->Initialize(device9, w, h, renderLuid);
        if (adapterName && nameCapacity) {
            DXGI_ADAPTER_DESC1 desc{};
            Check(pipeline->adapter->GetDesc1(&desc));
            wcsncpy_s(adapterName, nameCapacity, desc.Description, _TRUNCATE);
        }
        *handle = pipelines.Add(std::move(pipeline)); return S_OK;
    } catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; }
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_create(IDirect3DDevice9Ex* device9, int32_t w, int32_t h, int64_t* handle) noexcept {
    return vn_glass_gpu_create_on_adapter(device9, w, h, {}, handle, nullptr, 0);
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_render(int64_t handle, const Frame* frame) noexcept {
    LockedPipeline pipeline(handle);
    if (!pipeline || !frame || frame->width <= 0 || frame->height <= 0 ||
        frame->width > pipeline->width || frame->height > pipeline->height ||
        !std::isfinite(frame->blur) || frame->blur < 0 || frame->blur > 60)
        return E_INVALIDARG;
    try { return pipeline->Render(*frame); }
    catch (const winrt::hresult_error& error) { return pipeline->captureResult = error.code(); }
    catch (HRESULT hr) { return pipeline->captureResult = hr; } catch (...) { return pipeline->captureResult = E_FAIL; }
}
VN_EXPORT uint32_t __stdcall vn_glass_gpu_wait_backdrop(int64_t handle, HANDLE interrupt, uint32_t timeoutMs) noexcept {
    auto pipeline = pipelines.Acquire(handle);
    if (!pipeline || !pipeline->windowFrameWake || !interrupt) return WAIT_FAILED;
    HANDLE events[]{interrupt, pipeline->windowFrameWake.get()};
    return WaitForMultipleObjects(ARRAYSIZE(events), events, FALSE, timeoutMs);
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_render_windows(int64_t handle, const Frame* frame) noexcept {
    LockedPipeline pipeline(handle);
    if (!pipeline || !frame || frame->width <= 0 || frame->height <= 0 ||
        frame->width > pipeline->width || frame->height > pipeline->height ||
        !std::isfinite(frame->blur) || frame->blur < 0 || frame->blur > 60) return E_INVALIDARG;
    try { return pipeline->RenderWindows(*frame); }
    catch (...) { return E_PENDING; } // Exact CPU fallback owns unsupported cases.
}
// The Magnifier backend supplies an exact crop with a private window-exclusion
// list. Upload that crop while keeping blur, refraction and presentation on GPU.
VN_EXPORT int32_t __stdcall vn_glass_gpu_render_pixels(int64_t handle, const Frame* frame,
    const uint8_t* pixels, int32_t length, int32_t stride) noexcept {
    LockedPipeline pipeline(handle);
    if (!pipeline || !frame || !pixels || frame->width <= 0 || frame->height <= 0 ||
        frame->width > pipeline->width || frame->height > pipeline->height ||
        !std::isfinite(frame->blur) || frame->blur < 0 || frame->blur > 60 ||
        stride < static_cast<int64_t>(frame->width) * 4 ||
        length < static_cast<int64_t>(frame->height - 1) * stride + static_cast<int64_t>(frame->width) * 4)
        return E_INVALIDARG;
    try { return pipeline->RenderPixels(*frame, pixels, static_cast<UINT>(stride)); }
    catch (HRESULT hr) { return pipeline->captureResult = hr; }
    catch (...) { return pipeline->captureResult = E_FAIL; }
}
VN_EXPORT void __stdcall vn_glass_gpu_destroy(int64_t handle) noexcept { pipelines.Remove(handle); }
VN_EXPORT int32_t __stdcall vn_glass_gpu_wait_ready(int64_t handle, uint32_t timeoutMs) noexcept {
    LockedPipeline pipeline(handle);
    if (!pipeline || timeoutMs > 8) return E_INVALIDARG;
    try { return pipeline->WaitReady(timeoutMs); } catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; }
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_present_ready(int64_t handle, IDirect3DSurface9* destination) noexcept {
    LockedPipeline pipeline(handle);
    if (!pipeline || !destination) return E_INVALIDARG;
    try { return pipeline->PresentReady(destination); } catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; }
}
VN_EXPORT void __stdcall vn_glass_gpu_reset(int64_t handle) noexcept {
    LockedPipeline pipeline(handle);
    if (pipeline) {
        std::lock_guard lock(pipeline->sharedDevice->commands);
        pipeline->pending = false; pipeline->ResetCapture();
        pipeline->pyramid.clear(); pipeline->reconstruction.clear(); pipeline->resolved = {};
        pipeline->workWidth = pipeline->workHeight = 0; pipeline->smallSince = 0;
        pipeline->captureWaitStarted = 0; pipeline->retryAt = 0; pipeline->captureResult = S_FALSE;
    }
}
VN_EXPORT uint32_t __stdcall vn_glass_gpu_find_adapter(IDirect3D9Ex* direct9, HWND hwnd) noexcept {
    if (!direct9) return 0;
    const HMONITOR monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    for (UINT i = 0; i < direct9->GetAdapterCount(); ++i)
        if (direct9->GetAdapterMonitor(i) == monitor) return i;
    return 0;
}
VN_EXPORT uint32_t __stdcall vn_glass_gpu_find_preferred_adapter(IDirect3D9Ex* direct9, HWND hwnd, LUID* renderLuid) noexcept {
    const UINT monitorAdapter = vn_glass_gpu_find_adapter(direct9, hwnd);
    if (!renderLuid) return monitorAdapter;
    *renderLuid = HighPerformanceGlassAdapter();
    if (!direct9 || SameLuid(*renderLuid, {})) return monitorAdapter;
    // DXGI and D3D9 ordinals are unrelated. Prefer a local shared surface
    // even if the monitor is connected to the other GPU.
    LUID luid{};
    if (SUCCEEDED(direct9->GetAdapterLUID(monitorAdapter, &luid)) && SameLuid(luid, *renderLuid)) return monitorAdapter;
    for (UINT j = 0; j < direct9->GetAdapterCount(); ++j)
        if (SUCCEEDED(direct9->GetAdapterLUID(j, &luid)) && SameLuid(luid, *renderLuid)) return j;
    return monitorAdapter;
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_capture_status(int64_t handle) noexcept {
    LockedPipeline pipeline(handle);
    return pipeline ? pipeline->captureResult : E_HANDLE;
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_capture_backend(int64_t handle) noexcept {
    LockedPipeline pipeline(handle);
    return !pipeline ? 0 : pipeline->framePool ? 2 : pipeline->duplication ? 1 : 0;
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_borderless_status(int64_t handle) noexcept {
    LockedPipeline pipeline(handle);
    return pipeline ? pipeline->borderlessResult : E_HANDLE;
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_fence_status(int64_t handle) noexcept {
    LockedPipeline pipeline(handle);
    return pipeline ? pipeline->fenceResult : E_HANDLE;
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_set_interval(int64_t handle, double intervalMs) noexcept {
    LockedPipeline pipeline(handle);
    if (!pipeline || !std::isfinite(intervalMs) || intervalMs <= 0) return E_INVALIDARG;
    try { pipeline->SetCaptureInterval(intervalMs); return S_OK; }
    catch (...) { return winrt::to_hresult(); }
}
VN_EXPORT uint64_t __stdcall vn_glass_gpu_take_capture_count(int64_t handle) noexcept {
    LockedPipeline pipeline(handle);
    if (!pipeline) return 0;
    return std::exchange(pipeline->captureFrames, uint64_t{0});
}
VN_EXPORT uint64_t __stdcall vn_glass_gpu_consumer_stalls(int64_t handle) noexcept {
    LockedPipeline pipeline(handle);
    return pipeline ? pipeline->consumerStalls : 0;
}
