#pragma once
#include <d3d9.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <cstring>

namespace vnotch::graphics {
// A render-only GPU can be visible to DXGI but absent from D3D9Ex. In that
// case only the finished glass rectangle crosses adapters, after refraction.
struct CrossAdapterGlassOutput {
    Microsoft::WRL::ComPtr<ID3D11Texture2D> readback;
    Microsoft::WRL::ComPtr<IDirect3DSurface9> upload;

    HRESULT Initialize(ID3D11Device* renderer, IDirect3DDevice9Ex* presenter,
                       const D3D11_TEXTURE2D_DESC& output) {
        auto desc = output;
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        HRESULT hr = renderer->CreateTexture2D(&desc, nullptr, &readback);
        if (FAILED(hr)) return hr;
        return presenter->CreateOffscreenPlainSurface(desc.Width, desc.Height, D3DFMT_A8R8G8B8,
            D3DPOOL_SYSTEMMEM, &upload, nullptr);
    }

    void QueueCopy(ID3D11DeviceContext* context, ID3D11Texture2D* output, const RECT& region) {
        const D3D11_BOX box{0, 0, 0, static_cast<UINT>(region.right), static_cast<UINT>(region.bottom), 1};
        context->CopySubresourceRegion(readback.Get(), 0, 0, 0, 0, output, 0, &box);
    }

    HRESULT Present(ID3D11DeviceContext* context, IDirect3DDevice9Ex* presenter,
                    IDirect3DSurface9* destination, const RECT& region) {
        D3D11_MAPPED_SUBRESOURCE mapped{};
        HRESULT hr = context->Map(readback.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
        if (hr == DXGI_ERROR_WAS_STILL_DRAWING) return S_FALSE;
        if (FAILED(hr)) return hr;
        D3DLOCKED_RECT locked{};
        hr = upload->LockRect(&locked, &region, 0);
        if (SUCCEEDED(hr)) {
            auto* src = static_cast<const BYTE*>(mapped.pData);
            auto* dst = static_cast<BYTE*>(locked.pBits);
            for (LONG y = 0; y < region.bottom; ++y) {
                std::memcpy(dst, src, static_cast<size_t>(region.right) * 4);
                src += mapped.RowPitch;
                dst += locked.Pitch;
            }
            hr = upload->UnlockRect();
        }
        context->Unmap(readback.Get(), 0);
        if (FAILED(hr)) return hr;
        const POINT origin{};
        return presenter->UpdateSurface(upload.Get(), &region, destination, &origin);
    }
};
}
