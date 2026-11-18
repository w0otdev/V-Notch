#pragma once
#include "GlassShaders.g.h"
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <algorithm>
#include <memory>
#include <mutex>
#include <vector>

namespace vnotch::graphics {
// Immutable device objects are shared per adapter. Frame targets, queries and
// capture leases stay with their presenter. Weak entries do not keep idle GPUs alive.
struct GlassDevice final {
    template<class T> using ComPtr = Microsoft::WRL::ComPtr<T>;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> crop, down, up, upResolve, resolve, copy, refraction, opaqueWindow;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11RasterizerState> rasterizer;
    // D3D11Multithread protects individual calls, not a whole draw sequence.
    std::recursive_mutex commands;

    static void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
    template<size_t N>
    void Pixel(const unsigned char (&bytes)[N], ComPtr<ID3D11PixelShader>& shader) {
        Check(device->CreatePixelShader(bytes, N, nullptr, &shader));
    }
    explicit GlassDevice(IDXGIAdapter1* adapter) {
        constexpr D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0};
        Check(D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &device, nullptr, &context));
        ComPtr<ID3D11Multithread> multithread;
        Check(context.As(&multithread));
        multithread->SetMultithreadProtected(TRUE);
        using namespace shaders;
        Check(device->CreateVertexShader(fullscreenVs, sizeof(fullscreenVs), nullptr, &vertex));
        Pixel(cropPs, crop); Pixel(downPs, down); Pixel(upPs, up); Pixel(upResolvePs, upResolve);
        Pixel(resolvePs, resolve); Pixel(copyPs, copy); Pixel(refractPs, refraction);
        Pixel(opaqueWindowPs, opaqueWindow);
        D3D11_SAMPLER_DESC sample{};
        sample.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sample.AddressU = sample.AddressV = sample.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sample.MaxLOD = D3D11_FLOAT32_MAX;
        Check(device->CreateSamplerState(&sample, &sampler));
        D3D11_RASTERIZER_DESC raster{};
        raster.FillMode = D3D11_FILL_SOLID; raster.CullMode = D3D11_CULL_NONE; raster.DepthClipEnable = TRUE;
        Check(device->CreateRasterizerState(&raster, &rasterizer));
    }
    static std::shared_ptr<GlassDevice> Acquire(IDXGIAdapter1* adapter) {
        struct Cached { LUID luid; std::weak_ptr<GlassDevice> owner; };
        static std::mutex gate;
        static std::vector<Cached> cache;
        DXGI_ADAPTER_DESC1 desc{}; Check(adapter->GetDesc1(&desc));
        std::lock_guard lock(gate);
        std::erase_if(cache, [](const auto& entry) { return entry.owner.expired(); });
        for (auto& entry : cache) {
            if (entry.luid.HighPart != desc.AdapterLuid.HighPart || entry.luid.LowPart != desc.AdapterLuid.LowPart) continue;
            if (auto owner = entry.owner.lock(); owner && SUCCEEDED(owner->device->GetDeviceRemovedReason())) return owner;
        }
        auto owner = std::make_shared<GlassDevice>(adapter);
        cache.push_back({desc.AdapterLuid, owner});
        return owner;
    }
};
}
