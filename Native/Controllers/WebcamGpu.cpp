#include "vnotch_native.h"
#include "vnotch_handle_registry.h"
#include <d3d9.h>
#include <d3d11_4.h>
#include <wrl/client.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.directx.direct3d11.interop.h>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "windowsapp.lib")
namespace vnotch::camera {
using Microsoft::WRL::ComPtr;
inline void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
struct FrameLease {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Query> ready;
    winrt::Windows::Foundation::IClosable owner{nullptr};
    bool queued = false;
    HRESULT Poll() noexcept {
        BOOL done = FALSE;
        const HRESULT hr = context->GetData(ready.Get(), &done, sizeof(done), D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (FAILED(hr)) return hr;
        const HRESULT removed = device->GetDeviceRemovedReason();
        if (FAILED(removed)) return removed;
        return hr == S_OK && done ? S_OK : S_FALSE;
    }
    void Drain() noexcept {
        if (queued) while (Poll() == S_FALSE) Sleep(1);
    }
    ~FrameLease() noexcept {
        // A successful submit transfers Close ownership from C# to this lease.
        if (queued && owner) { try { owner.Close(); } catch (...) {} }
    }
};
// Called only after unpublishing the handle. Never make the WPF dispatcher wait
// for an outstanding camera command; keep its frame alive on a cleanup worker.
inline void Retire(std::unique_ptr<FrameLease> frame) noexcept {
    if (!frame || !frame->queued || frame->Poll() != S_FALSE) return;
    auto* pending = frame.release();
    if (!TrySubmitThreadpoolCallback([](PTP_CALLBACK_INSTANCE, void* value) noexcept {
        std::unique_ptr<FrameLease> lease(static_cast<FrameLease*>(value)); lease->Drain();
    }, pending, nullptr)) {
        frame.reset(pending); frame->Drain();
    }
}
class SurfaceCopy final {
    ComPtr<IDirect3DDevice9Ex> presentation;
    ComPtr<IDirect3DTexture9> target9;
    ComPtr<IDirect3DSurface9> surface9;
    ComPtr<IDirect3DQuery9> consumed;
    ComPtr<ID3D11Device> sourceDevice;
    ComPtr<ID3D11Texture2D> target11;
    std::unique_ptr<FrameLease> frame;
    int width, height;
    bool consuming = false;
public:
    SurfaceCopy(IDirect3DDevice9Ex* device, int w, int h) : presentation(device), width(w), height(h) {
        Check(device->CreateQuery(D3DQUERYTYPE_EVENT, &consumed));
    }
    ~SurfaceCopy() { Retire(std::move(frame)); }
    // S_OK: owns owner.Close(). S_FALSE: busy, caller closes/drops. Failure:
    // unsupported device/format, caller retains ownership and uses CPU fallback.
    HRESULT Submit(IUnknown* input, IUnknown* owner) {
        if (frame) return S_FALSE;
        if (consuming) {
            const HRESULT hr = consumed->GetData(nullptr, 0, D3DGETDATA_FLUSH);
            Check(hr); if (hr != S_OK) return S_FALSE;
            consuming = false;
        }
        ComPtr<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess> access;
        Check(input->QueryInterface(IID_PPV_ARGS(&access)));
        ComPtr<ID3D11Texture2D> source;
        Check(access->GetInterface(IID_PPV_ARGS(&source)));
        D3D11_TEXTURE2D_DESC desc{}; source->GetDesc(&desc);
        if (desc.Width != static_cast<UINT>(width) || desc.Height != static_cast<UINT>(height) ||
            desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM || desc.ArraySize != 1 || desc.MipLevels != 1 || desc.SampleDesc.Count != 1)
            return DXGI_ERROR_UNSUPPORTED;
        ComPtr<ID3D11Device> device; source->GetDevice(&device);
        if (device != sourceDevice) {
            target11.Reset(); surface9.Reset(); target9.Reset(); sourceDevice.Reset();
            HANDLE shared = nullptr; // Legacy resource handle, not CloseHandle-owned.
            Check(presentation->CreateTexture(width, height, 1, D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8,
                D3DPOOL_DEFAULT, &target9, &shared));
            Check(target9->GetSurfaceLevel(0, &surface9));
            // Cross-adapter sharing is deliberately rejected by OpenSharedResource.
            Check(device->OpenSharedResource(shared, IID_PPV_ARGS(&target11)));
            sourceDevice = device;
        }
        auto next = std::make_unique<FrameLease>();
        next->device = device; device->GetImmediateContext(&next->context);
        Check(owner->QueryInterface(winrt::guid_of<winrt::Windows::Foundation::IClosable>(), winrt::put_abi(next->owner)));
        const D3D11_QUERY_DESC query{D3D11_QUERY_EVENT, 0};
        Check(device->CreateQuery(&query, &next->ready));
        ComPtr<ID3D11Multithread> multithread; Check(next->context.As(&multithread));
        multithread->SetMultithreadProtected(TRUE);
        multithread->Enter();
        next->context->CopyResource(target11.Get(), source.Get());
        next->context->End(next->ready.Get());
        next->context->Flush();
        multithread->Leave();
        next->queued = true;
        frame = std::move(next); // No throwing operation after queuing the copy.
        return S_OK;
    }
    HRESULT Present(IDirect3DSurface9* destination) {
        if (!frame) return S_FALSE;
        const HRESULT hr = frame->Poll();
        if (hr == S_FALSE) return hr;
        frame.reset(); // Source is no longer read, even if the device was removed.
        Check(hr);
        Check(presentation->StretchRect(surface9.Get(), nullptr, destination, nullptr, D3DTEXF_NONE));
        Check(consumed->Issue(D3DISSUE_END));
        consuming = true;
        Check(consumed->GetData(nullptr, 0, D3DGETDATA_FLUSH));
        return S_OK;
    }
};
inline vnotch::HandleRegistry<SurfaceCopy> copies;
}
VN_EXPORT HRESULT vn_webcam_gpu_create(IDirect3DDevice9Ex* device, int w, int h, int64_t* handle) noexcept {
    if (!device || !handle || w <= 0 || h <= 0 || w > 8192 || h > 8192) return E_INVALIDARG;
    *handle = 0;
    try { *handle = vnotch::camera::copies.Add(std::make_unique<vnotch::camera::SurfaceCopy>(device, w, h)); return S_OK; }
    catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; }
}
VN_EXPORT HRESULT vn_webcam_gpu_submit(int64_t handle, IUnknown* surface, IUnknown* owner) noexcept {
    auto copy = vnotch::camera::copies.Acquire(handle);
    if (!copy || !surface || !owner) return E_INVALIDARG;
    try { return copy->Submit(surface, owner); }
    catch (HRESULT hr) { return hr; } catch (...) { return winrt::to_hresult(); }
}
VN_EXPORT HRESULT vn_webcam_gpu_present(int64_t handle, IDirect3DSurface9* destination) noexcept {
    auto copy = vnotch::camera::copies.Acquire(handle);
    if (!copy || !destination) return E_INVALIDARG;
    try { return copy->Present(destination); }
    catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; }
}
VN_EXPORT void vn_webcam_gpu_destroy(int64_t handle) noexcept { vnotch::camera::copies.Remove(handle); }
