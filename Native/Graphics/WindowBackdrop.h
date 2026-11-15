#pragma once
#include "GlassFrame.h"
#include "../Controllers/WindowBackdropCapture.h"
#include "../Controllers/BackdropLayers.h"

namespace vnotch::graphics {
// This path optimizes fully covered opaque window regions. Wallpaper, translucent
// overlays, unsupported capture and initial pool warmup use the existing exact
// CPU composition. No desktop screenshot can accidentally include this process.
class WindowBackdrop final {
    static void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
    using Cache = vnotch::controllers::backdrop::capture::WindowCaptureCache;
    using Layers = vnotch::controllers::backdrop::EnumState;
    struct State {
        Cache cache;
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        ComPtr<ID3D11Query> query;
        bool pending = false;
        HRESULT Poll(uint64_t& samples) noexcept {
            HRESULT hr = context->GetData(query.Get(), &samples, sizeof(samples), D3D11_ASYNC_GETDATA_DONOTFLUSH);
            if (FAILED(device->GetDeviceRemovedReason())) return DXGI_ERROR_DEVICE_REMOVED;
            return hr;
        }
    };
    std::unique_ptr<State> state;
    Target front, back;
    Layers frontLayers{}, pendingLayers{};
    uint64_t expectedSamples = 0;
    bool valid = false;
    ULONGLONG retryAt = 0, ineligibleSince = 0;
    static bool Same(const Layers& a, const Layers& b) noexcept {
        if (a.count != b.count || !EqualRect(&a.area, &b.area)) return false;
        for (int i = 0; i < a.count; ++i)
            if (a.layers[i].hwnd != b.layers[i].hwnd || a.layers[i].pid != b.layers[i].pid || a.layers[i].thread != b.layers[i].thread ||
                !EqualRect(&a.layers[i].window, &b.layers[i].window) ||
                !EqualRect(&a.layers[i].visible, &b.layers[i].visible)) return false;
        return true;
    }
public:
    ~WindowBackdrop() {
        if (!state || !state->pending) return;
        uint64_t samples = 0;
        if (state->Poll(samples) != S_FALSE) return;
        auto* pending = state.release();
        if (!TrySubmitThreadpoolCallback([](PTP_CALLBACK_INSTANCE, void* pointer) noexcept {
            std::unique_ptr<State> owner(static_cast<State*>(pointer));
            uint64_t samples = 0;
            while (owner->Poll(samples) == S_FALSE) Sleep(1);
        }, pending, nullptr)) {
            state.reset(pending);
            while (state->Poll(samples) == S_FALSE) Sleep(1);
        }
    }
    // The caller holds the adapter command lock. No waits or readbacks here.
    template<class MakeTarget, class DrawWindow>
    bool Capture(const Frame& frame, ID3D11Device* device, ID3D11DeviceContext* context,
        MakeTarget makeTarget, DrawWindow drawWindow, Target& result, bool& changed) {
        changed = false;
        if (GetTickCount64() < retryAt) return false;
        Layers layers{};
        layers.area = {frame.x, frame.y, frame.x + frame.width, frame.y + frame.height};
        layers.self_pid = GetCurrentProcessId(); layers.require_opaque = true;
        EnumWindows(vnotch::controllers::backdrop::CollectLayer, reinterpret_cast<LPARAM>(&layers));
        bool ready = true;
        if (state && state->pending) {
            uint64_t samples = 0;
            HRESULT hr = state->Poll(samples);
            if (hr == S_FALSE) ready = false;
            else {
                state->pending = false;
                if (FAILED(hr)) { valid = false; return false; }
                if (samples == expectedSamples) {
                    std::swap(front, back); frontLayers = pendingLayers; valid = true; changed = true;
                } else {
                    valid = false; retryAt = GetTickCount64() + 1000;
                    return false;
                }
            }
        }
        const bool eligible = !layers.unsupported && layers.coverage_initialized && !layers.coverage_overflow &&
            layers.uncovered_count == 0 && layers.count > 0 && layers.count <= 4;
        if (!eligible) {
            valid = false;
            if (state && ready) {
                state->cache.ExpireUnused();
                if (!ineligibleSince) ineligibleSince = GetTickCount64();
                if (GetTickCount64() - ineligibleSince >= 2000) { front = {}; back = {}; }
            }
            return false;
        }
        ineligibleSince = 0;
        if (ready) {
            if (!state) {
                auto next = std::make_unique<State>(); next->device = device; next->context = context;
                next->cache.BindDevice(device);
                D3D11_QUERY_DESC desc{D3D11_QUERY_OCCLUSION, 0};
                Check(device->CreateQuery(&desc, &next->query));
                state = std::move(next);
            }
            std::array<Cache::TextureRegion, 4> regions{};
            std::array<RECT, 4> hits{};
            bool available = true, fresh = false;
            try {
                // Idle eviction releases the capture cache's device. Rebind
                // before opening another window so its SRVs share our device.
                state->cache.BindDevice(device);
                for (int i = 0; i < layers.count; ++i) {
                    auto& layer = layers.layers[i];
                    IntersectRect(&hits[i], &layer.visible, &layers.area);
                    if (!state->cache.AcquireTexture(layer.hwnd, hits[i], layer.visible, layer.window, regions[i])) available = false;
                    fresh |= regions[i].fresh;
                }
            } catch (...) { available = false; valid = false; retryAt = GetTickCount64() + 1000; }
            if (available && (fresh || !valid || !Same(layers, frontLayers))) {
                if (back.width < static_cast<UINT>(frame.width) || back.height < static_cast<UINT>(frame.height))
                    back = makeTarget(frame.width, frame.height);
                const float clear[4]{}; context->ClearRenderTargetView(back.rtv.Get(), clear);
                expectedSamples = 0;
                context->Begin(state->query.Get());
                for (int i = layers.count - 1; i >= 0; --i) {
                    const auto& hit = hits[i];
                    expectedSamples += static_cast<uint64_t>(hit.right - hit.left) * (hit.bottom - hit.top);
                    drawWindow(regions[i], back.rtv.Get(), hit, layers.area);
                }
                context->End(state->query.Get()); context->Flush();
                pendingLayers = layers; state->pending = true;
            }
            // No partial/stale result after a resize, identity change or capture failure.
            if (!available) return false;
        }
        if (!valid || !Same(layers, frontLayers)) return false;
        result = front;
        return true;
    }
};

}
