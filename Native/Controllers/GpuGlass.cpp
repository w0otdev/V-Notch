#include "vnotch_native.h"
#include "vnotch_handle_registry.h"
#include "GlassShaders.g.h"
#include <d3d9.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi1_5.h>
#include <d3dcompiler.h>
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
#pragma comment(lib, "d3dcompiler.lib")
#pragma comment(lib, "windowsapp.lib")

namespace {
using Microsoft::WRL::ComPtr;
namespace Capture = winrt::Windows::Graphics::Capture;
namespace DirectX = winrt::Windows::Graphics::DirectX;
void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
struct Frame {
    int32_t x, y, width, height;
    float blur;
    int32_t refract;
    float lens[32][4];
};
struct Constants {
    float lens[32][4]{};
    float crop[4]{};
    float desktop[4]{};
    float pass[4]{};
    float sourceRegion[4]{}, detailRegion[4]{};
};
struct TextureInput {
    ID3D11ShaderResourceView* view = nullptr;
    UINT width = 1, height = 1, activeWidth = 1, activeHeight = 1;
};
struct Target {
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11ShaderResourceView> srv;
    ComPtr<ID3D11RenderTargetView> rtv;
    UINT width = 0, height = 0;
    TextureInput Input(UINT w, UINT h) const { return { srv.Get(), width, height, w, h }; }
};
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
    std::vector<std::pair<ComPtr<ID3D11Texture2D>, ComPtr<ID3D11ShaderResourceView>>> captureViews;
    winrt::Windows::Graphics::SizeInt32 captureSize{};
    ULONGLONG captureWaitStarted = 0;
    ComPtr<ID3D11Texture2D> desktopTexture;
    ComPtr<ID3D11ShaderResourceView> desktopView;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> crop, down, up, resolve, copy, refraction;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11RasterizerState> rasterizer;
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11Query> completed;
    ComPtr<ID3D11DeviceContext4> fenceContext;
    ComPtr<ID3D11Fence> completionFence;
    winrt::handle completionEvent;
    uint64_t fenceValue = 0;
    HRESULT fenceResult = E_NOINTERFACE;
    std::array<SharedTarget, 2> output;
    std::vector<Target> pyramid;
    std::vector<Target> reconstruction;
    Target resolved;
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

    ComPtr<ID3DBlob> Compile(const char* entry, const char* profile) {
        ComPtr<ID3DBlob> blob, errors;
        HRESULT hr = D3DCompile(kGlassGpuShader, sizeof(kGlassGpuShader) - 1, "GlassGpu", nullptr,
            nullptr, entry, profile, D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_ENABLE_STRICTNESS,
            0, &blob, &errors);
        if (FAILED(hr) && errors) OutputDebugStringA(static_cast<const char*>(errors->GetBufferPointer()));
        Check(hr);
        return blob;
    }
    void PixelShader(const char* entry, ComPtr<ID3D11PixelShader>& shader) {
        auto blob = Compile(entry, "ps_4_0");
        Check(device->CreatePixelShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &shader));
    }
    void Initialize(IDirect3DDevice9Ex* device9, int w, int h) {
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
        ComPtr<IDXGIFactory1> factory;
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
        for (UINT i = 0; ; ++i) {
            ComPtr<IDXGIAdapter1> candidate;
            if (factory->EnumAdapters1(i, &candidate) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_ADAPTER_DESC1 desc{};
            Check(candidate->GetDesc1(&desc));
            if (desc.AdapterLuid.HighPart == luid.HighPart && desc.AdapterLuid.LowPart == luid.LowPart) {
                adapter = candidate; break;
            }
        }
        if (!adapter) throw DXGI_ERROR_NOT_FOUND;
        const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
        Check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &device, nullptr, &context));
        // WGC can access this device from its own worker while we run the shaders.
        ComPtr<ID3D11Multithread> multithread;
        Check(context.As(&multithread));
        multithread->SetMultithreadProtected(TRUE);
        for (auto& target : output) {
            HANDLE shared = nullptr; // Legacy shared handles belong to the resource; never CloseHandle.
            Check(device9->CreateTexture(w, h, 1, D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8,
                D3DPOOL_DEFAULT, &target.texture9, &shared));
            Check(target.texture9->GetSurfaceLevel(0, &target.surface));
            Check(device->OpenSharedResource(shared, IID_PPV_ARGS(&target.texture)));
            Check(device->CreateRenderTargetView(target.texture.Get(), nullptr, &target.rtv));
            Check(device9->CreateQuery(D3DQUERYTYPE_EVENT, &target.consumed));
        }
        auto blob = Compile("fullscreenVs", "vs_4_0");
        Check(device->CreateVertexShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &vertex));
        PixelShader("cropPs", crop); PixelShader("downPs", down); PixelShader("upPs", up);
        PixelShader("resolvePs", resolve);
        PixelShader("copyPs", copy); PixelShader("refractPs", refraction);
        D3D11_SAMPLER_DESC sample{};
        sample.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sample.AddressU = sample.AddressV = sample.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sample.MaxLOD = D3D11_FLOAT32_MAX;
        Check(device->CreateSamplerState(&sample, &sampler));
        D3D11_RASTERIZER_DESC raster{};
        raster.FillMode = D3D11_FILL_SOLID; raster.CullMode = D3D11_CULL_NONE; raster.DepthClipEnable = TRUE;
        Check(device->CreateRasterizerState(&raster, &rasterizer));
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
        if (capturedFrame) { try { capturedFrame.Close(); } catch (...) { } capturedFrame = nullptr; }
        captureViews.clear();
        try { if (captureSession) captureSession.Close(); } catch (...) { }
        try { if (framePool) framePool.Close(); } catch (...) { }
        captureSession = nullptr; framePool = nullptr; captureItem = nullptr; captureDevice = nullptr;
        duplication.Reset(); desktopView.Reset(); desktopTexture.Reset(); monitor = nullptr;
        hasRenderedFrame = false;
        sourceDirty = true;
    }
    bool StartGraphicsCapture(HMONITOR wanted) {
        // Hybrid graphics can expose a monitor on the discrete adapter while
        // DuplicateOutput only works on the integrated adapter. WGC delivers a
        // texture on our presentation device without a CPU readback bridge.
        vnotch::com::EnsureMta();
        if (!Capture::GraphicsCaptureSession::IsSupported()) throw DXGI_ERROR_UNSUPPORTED;
        auto interop = winrt::get_activation_factory<Capture::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        winrt::check_hresult(interop->CreateForMonitor(wanted,
            winrt::guid_of<Capture::IGraphicsCaptureItem>(), winrt::put_abi(captureItem)));
        ComPtr<IDXGIDevice> dxgiDevice; Check(device.As(&dxgiDevice));
        winrt::com_ptr<IInspectable> inspectable;
        Check(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), inspectable.put()));
        captureDevice = inspectable.as<DirectX::Direct3D11::IDirect3DDevice>();
        captureSize = captureItem.Size();
        framePool = Capture::Direct3D11CaptureFramePool::CreateFreeThreaded(
            captureDevice, DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, captureSize);
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
        MONITORINFO info{ sizeof(info) };
        if (!GetMonitorInfoW(wanted, &info)) throw HRESULT_FROM_WIN32(GetLastError());
        outputDesc.DesktopCoordinates = info.rcMonitor;
        outputDesc.Rotation = DXGI_MODE_ROTATION_IDENTITY; // WGC frames are already oriented.
        monitor = wanted; retryAt = 0;
        captureResult = S_FALSE;
        return true;
    }
    void ApplyCaptureInterval() {
        if (captureSession) {
            if (auto pacing = captureSession.try_as<Capture::IGraphicsCaptureSession5>())
                pacing.MinUpdateInterval(winrt::Windows::Foundation::TimeSpan{ captureIntervalTicks });
        }
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
    bool AcquireDesktop(const Frame& requested) {
        desktopChanged = false;
        if (framePool) {
            auto frame = framePool.TryGetNextFrame();
            if (!frame) {
                return desktopTexture != nullptr;
            }
            // The two-buffer pool can contain a delayed frame after UI/GPU
            // contention. Consume the newest complete frame for the current lens.
            if (auto newer = framePool.TryGetNextFrame()) { frame.Close(); frame = newer; }
            const auto size = frame.ContentSize();
            if (size.Width <= 0 || size.Height <= 0) { frame.Close(); return desktopTexture != nullptr; }
            if (size.Width != captureSize.Width || size.Height != captureSize.Height) {
                frame.Close();
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
                frame.Close(); return desktopTexture != nullptr;
            }
            if (acquiredDesc.BindFlags & D3D11_BIND_SHADER_RESOURCE) {
                // Keep the WGC frame leased until all draws reading it complete.
                // Reuse SRVs for the bounded frame-pool textures; no desktop copy.
                if (capturedFrame) capturedFrame.Close();
                capturedFrame = frame;
                desktopTexture = acquired;
                auto found = std::find_if(captureViews.begin(), captureViews.end(),
                    [&](const auto& entry) { return entry.first.Get() == acquired.Get(); });
                if (found == captureViews.end()) {
                    ComPtr<ID3D11ShaderResourceView> view;
                    Check(device->CreateShaderResourceView(acquired.Get(), nullptr, &view));
                    if (captureViews.size() >= 4) captureViews.clear();
                    captureViews.emplace_back(acquired, view);
                    desktopView = view;
                } else desktopView = found->second;
                ++captureFrames; desktopChanged = true; captureResult = S_OK;
                return true;
            }
            if (!desktopTexture) {
                D3D11_TEXTURE2D_DESC desc = acquiredDesc;
                desc.Usage = D3D11_USAGE_DEFAULT; desc.CPUAccessFlags = 0; desc.MiscFlags = 0;
                desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                Check(device->CreateTexture2D(&desc, nullptr, &desktopTexture));
                Check(device->CreateShaderResourceView(desktopTexture.Get(), nullptr, &desktopView));
            }
            context->CopyResource(desktopTexture.Get(), acquired.Get());
            frame.Close();
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
              ID3D11PixelShader* shader, Constants data, TextureInput detail = {}) {
        auto region = [](float* out, TextureInput input) {
            out[0] = static_cast<float>(input.activeWidth) / input.width;
            out[1] = static_cast<float>(input.activeHeight) / input.height;
            out[2] = 0.5f / input.width; out[3] = 0.5f / input.height;
        };
        region(data.sourceRegion, source); region(data.detailRegion, detail);
        context->UpdateSubresource(constants.Get(), 0, nullptr, &data, 0, 0);
        ID3D11Buffer* cb = constants.Get(); context->PSSetConstantBuffers(0, 1, &cb);
        ID3D11SamplerState* smp = sampler.Get(); context->PSSetSamplers(0, 1, &smp);
        context->OMSetRenderTargets(1, &dest, nullptr);
        D3D11_VIEWPORT vp{ 0, 0, static_cast<float>(w), static_cast<float>(h), 0, 1 };
        context->RSSetViewports(1, &vp); context->RSSetState(rasterizer.Get());
        context->IASetInputLayout(nullptr); context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex.Get(), nullptr, 0); context->PSSetShader(shader, nullptr, 0);
        ID3D11ShaderResourceView* inputs[] = { source.view, detail.view };
        context->PSSetShaderResources(0, 2, inputs); context->Draw(3, 0);
        ID3D11ShaderResourceView* empty[2]{}; context->PSSetShaderResources(0, 2, empty);
        context->OMSetRenderTargets(0, nullptr, nullptr);
    }
    HRESULT Render(const Frame& f) {
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
        if (workWidth && static_cast<uint64_t>(f.width) * f.height * 4 < static_cast<uint64_t>(workWidth) * workHeight) {
            if (!smallSince) smallSince = GetTickCount64();
            if (GetTickCount64() - smallSince >= 5000) {
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
    HRESULT RenderCaptured(const Frame& f, bool rebuildSource = true) {
        auto& target = output[nextOutput];
        if (target.awaitingConsumer) {
            // D3D11 must not recycle a shared source while D3D9 still copies it.
            BOOL consumed = FALSE;
            HRESULT hr = target.consumed->GetData(&consumed, sizeof(consumed), D3DGETDATA_FLUSH);
            Check(hr);
            if (hr == S_FALSE || !consumed) { ++consumerStalls; return S_FALSE; }
            target.awaitingConsumer = false;
        }
        int levels = f.blur == 0 ? 0 : std::clamp(static_cast<int>(std::ceil(std::log2(std::max(1.0f, f.blur)))), 1, 5);
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
            if (i < levels && !reconstruction[i].texture) reconstruction[i] = MakeTarget(w, h, DXGI_FORMAT_R16G16B16A16_FLOAT);
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
            Draw({ desktopView.Get(), desktopDesc.Width, desktopDesc.Height, desktopDesc.Width, desktopDesc.Height },
                pyramid[0].rtv.Get(), f.width, f.height, crop.Get(), data);
            data.pass[2] = std::max(1.0f, static_cast<float>(f.blur) / 32.0f);
            const float upper = static_cast<float>(1 << levels);
            const float lower = levels == 1 ? 0.0f : upper * 0.5f;
            const float fraction = std::clamp((f.blur - lower) / std::max(1.0f, upper - lower), 0.0f, 1.0f);
            const float blend = fraction * fraction * (3.0f - 2.0f * fraction);
            for (int i = 1; i <= levels; ++i) {
                data.pass[0] = 1.0f / activeW[i-1]; data.pass[1] = 1.0f / activeH[i-1];
                Draw(pyramid[i-1].Input(activeW[i-1], activeH[i-1]), pyramid[i].rtv.Get(), activeW[i], activeH[i], down.Get(), data);
            }
            for (int i = levels; i > 0; --i) {
                data.pass[0] = 1.0f / activeW[i]; data.pass[1] = 1.0f / activeH[i];
                data.pass[3] = i == levels ? blend : 1.0f;
                auto input = i == levels ? pyramid[i].Input(activeW[i], activeH[i]) : reconstruction[i].Input(activeW[i], activeH[i]);
                Draw(input, reconstruction[i-1].rtv.Get(), activeW[i-1], activeH[i-1],
                    up.Get(), data, pyramid[i-1].Input(activeW[i-1], activeH[i-1]));
            }
            Draw(levels ? reconstruction[0].Input(f.width, f.height) : pyramid[0].Input(f.width, f.height), resolved.rtv.Get(),
                f.width, f.height, resolve.Get(), data);
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
        Draw(resolved.Input(f.width, f.height), target.rtv.Get(), f.width, f.height, (f.refract & 1) ? refraction.Get() : copy.Get(), data);
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
        if (completionFence && completionEvent) {
            const uint64_t value = completionFence->GetCompletedValue();
            if (value == UINT64_MAX) { Check(device->GetDeviceRemovedReason()); return S_FALSE; }
            return value >= fenceValue ? S_OK : S_FALSE;
        }
        BOOL ready = FALSE;
        HRESULT hr = context->GetData(completed.Get(), &ready, sizeof(ready), D3D11_ASYNC_GETDATA_DONOTFLUSH);
        Check(hr);
        return hr == S_OK && ready ? S_OK : S_FALSE;
    }
    HRESULT TakeReady(IDirect3DSurface9** surface) {
        if (CompletionReady() != S_OK) return S_FALSE;
        Check(output[nextOutput].surface.CopyTo(surface));
        nextOutput ^= 1; pending = false;
        return S_OK;
    }
    HRESULT PresentReady(IDirect3DSurface9* destination) {
        if (CompletionReady() != S_OK) return S_FALSE;
        auto& target = output[nextOutput];
        // WPF keeps one back buffer throughout the session. Both this transfer
        // and WPF composition stay on the GPU; no SetBackBuffer churn per frame.
        RECT region{ 0, 0, std::max(pendingWidth, presentedWidth), std::max(pendingHeight, presentedHeight) };
        // The persistent D3D9 surface is uninitialized on its first use. Transfer
        // the fully cleared source once before switching to bounded updates.
        if (!presentedWidth || !presentedHeight) { region.right = width; region.bottom = height; }
        Check(presentationDevice->StretchRect(target.surface.Get(), &region, destination, &region, D3DTEXF_NONE));
        presentedWidth = pendingWidth; presentedHeight = pendingHeight;
        Check(target.consumed->Issue(D3DISSUE_END));
        // Submit the copy now, rather than first flushing it on source reuse.
        HRESULT consumed = target.consumed->GetData(nullptr, 0, D3DGETDATA_FLUSH);
        Check(consumed);
        target.awaitingConsumer = consumed != S_OK;
        nextOutput ^= 1; pending = false;
        return S_OK;
    }
};
vnotch::HandleRegistry<GpuGlass> pipelines;
}

VN_EXPORT int32_t __stdcall vn_glass_gpu_create(IDirect3DDevice9Ex* device9, int32_t w, int32_t h, int64_t* handle) noexcept {
    if (!device9 || !handle || w <= 0 || h <= 0 || w > 8192 || h > 8192) return E_INVALIDARG;
    *handle = 0;
    try {
        auto pipeline = std::make_unique<GpuGlass>(); pipeline->Initialize(device9, w, h);
        *handle = pipelines.Add(std::move(pipeline)); return S_OK;
    } catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; }
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_render(int64_t handle, const Frame* frame) noexcept {
    auto pipeline = pipelines.Acquire(handle);
    if (!pipeline || !frame || frame->width <= 0 || frame->height <= 0 ||
        frame->width > pipeline->width || frame->height > pipeline->height ||
        !std::isfinite(frame->blur) || frame->blur < 0 || frame->blur > 60)
        return E_INVALIDARG;
    try { return pipeline->Render(*frame); }
    catch (const winrt::hresult_error& error) { return pipeline->captureResult = error.code(); }
    catch (HRESULT hr) { return pipeline->captureResult = hr; } catch (...) { return pipeline->captureResult = E_FAIL; }
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_take_ready(int64_t handle, IDirect3DSurface9** surface) noexcept {
    if (!surface) return E_INVALIDARG;
    *surface = nullptr; auto pipeline = pipelines.Acquire(handle);
    if (!pipeline) return E_INVALIDARG;
    try { return pipeline->TakeReady(surface); } catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; }
}
VN_EXPORT void __stdcall vn_glass_gpu_destroy(int64_t handle) noexcept { pipelines.Remove(handle); }
VN_EXPORT int32_t __stdcall vn_glass_gpu_wait_ready(int64_t handle, uint32_t timeoutMs) noexcept {
    auto pipeline = pipelines.Acquire(handle);
    if (!pipeline || timeoutMs > 8) return E_INVALIDARG;
    try { return pipeline->WaitReady(timeoutMs); } catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; }
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_present_ready(int64_t handle, IDirect3DSurface9* destination) noexcept {
    auto pipeline = pipelines.Acquire(handle);
    if (!pipeline || !destination) return E_INVALIDARG;
    try { return pipeline->PresentReady(destination); } catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; }
}
VN_EXPORT void __stdcall vn_glass_gpu_reset(int64_t handle) noexcept {
    auto pipeline = pipelines.Acquire(handle);
    if (pipeline) {
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
VN_EXPORT int32_t __stdcall vn_glass_gpu_capture_status(int64_t handle) noexcept {
    auto pipeline = pipelines.Acquire(handle);
    return pipeline ? pipeline->captureResult : E_HANDLE;
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_capture_backend(int64_t handle) noexcept {
    auto pipeline = pipelines.Acquire(handle);
    return !pipeline ? 0 : pipeline->framePool ? 2 : pipeline->duplication ? 1 : 0;
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_borderless_status(int64_t handle) noexcept {
    auto pipeline = pipelines.Acquire(handle);
    return pipeline ? pipeline->borderlessResult : E_HANDLE;
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_fence_status(int64_t handle) noexcept {
    auto pipeline = pipelines.Acquire(handle);
    return pipeline ? pipeline->fenceResult : E_HANDLE;
}
VN_EXPORT int32_t __stdcall vn_glass_gpu_set_interval(int64_t handle, double intervalMs) noexcept {
    auto pipeline = pipelines.Acquire(handle);
    if (!pipeline || !std::isfinite(intervalMs) || intervalMs <= 0) return E_INVALIDARG;
    try { pipeline->SetCaptureInterval(intervalMs); return S_OK; }
    catch (...) { return winrt::to_hresult(); }
}
VN_EXPORT uint64_t __stdcall vn_glass_gpu_take_capture_count(int64_t handle) noexcept {
    auto pipeline = pipelines.Acquire(handle);
    if (!pipeline) return 0;
    return std::exchange(pipeline->captureFrames, uint64_t{0});
}
VN_EXPORT uint64_t __stdcall vn_glass_gpu_consumer_stalls(int64_t handle) noexcept {
    auto pipeline = pipelines.Acquire(handle);
    return pipeline ? pipeline->consumerStalls : 0;
}
