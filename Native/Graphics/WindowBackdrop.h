#pragma once
#include "GlassFrame.h"
#include "GlassDevice.h"
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
        std::shared_ptr<GlassDevice> adapter;
        Cache cache;
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        ComPtr<ID3D11DeviceContext3> context3;
        winrt::handle validationDone;
        ComPtr<ID3D11Query> query;
        bool pending = false;
        HRESULT Poll(uint64_t& samples) noexcept {
            HRESULT hr = context->GetData(query.Get(), &samples, sizeof(samples), D3D11_ASYNC_GETDATA_DONOTFLUSH);
            if (FAILED(device->GetDeviceRemovedReason())) return DXGI_ERROR_DEVICE_REMOVED;
            return hr;
        }
    };
    std::shared_ptr<GlassDevice> adapter;
    std::unique_ptr<State> state;
    Target front, back;
    Layers frontLayers{}, pendingLayers{};
    uint64_t expectedSamples = 0;
    int64_t afterPixelUpload = 0;
    bool valid = false, pendingCurrent = false;
    ULONGLONG retryAt = 0, ineligibleSince = 0;
    HRESULT CompleteValidation(bool& changed) {
        uint64_t samples = 0;
        HRESULT hr = state->Poll(samples);
        if (hr == S_FALSE) return hr;
        state->pending = false;
        if (FAILED(hr)) { valid = false; return E_PENDING; }
        if (!pendingCurrent) return S_OK;
        if (samples != expectedSamples) {
            valid = false; retryAt = GetTickCount64() + 1000;
            return E_PENDING;
        }
        std::swap(front, back);
        frontLayers = pendingLayers;
        valid = changed = true;
        return S_OK;
    }
    static bool Same(const Layers& a, const Layers& b) noexcept {
        if (a.count != b.count || !EqualRect(&a.area, &b.area)) return false;
        for (int i = 0; i < a.count; ++i)
            if (a.layers[i].hwnd != b.layers[i].hwnd || a.layers[i].pid != b.layers[i].pid || a.layers[i].thread != b.layers[i].thread ||
                !EqualRect(&a.layers[i].window, &b.layers[i].window) ||
                !EqualRect(&a.layers[i].visible, &b.layers[i].visible)) return false;
        return true;
    }
public:
    explicit WindowBackdrop(std::shared_ptr<GlassDevice> shared) : adapter(std::move(shared)) {}
    void Supersede() noexcept {
        valid = false;
        pendingCurrent = false;
        // WGC SystemRelativeTime uses QPC in 100 ns units. A frame can be
        // dequeued after the fallback upload yet still have been captured before it.
        LARGE_INTEGER now{}, frequency{};
        QueryPerformanceCounter(&now);
        QueryPerformanceFrequency(&frequency);
        constexpr int64_t units = 10'000'000;
        afterPixelUpload = (now.QuadPart / frequency.QuadPart) * units +
            (now.QuadPart % frequency.QuadPart) * units / frequency.QuadPart;
    }
    ~WindowBackdrop() {
        if (!state || !state->pending) return;
        {
            std::lock_guard lock(adapter->commands);
            uint64_t samples = 0;
            if (state->Poll(samples) != S_FALSE) return;
            state->context->Flush();
        }
        auto* pending = state.release();
        if (!TrySubmitThreadpoolCallback([](PTP_CALLBACK_INSTANCE, void* pointer) noexcept {
            std::unique_ptr<State> owner(static_cast<State*>(pointer));
            const auto deadline = GetTickCount64() + 500;
            uint64_t samples = 0;
            do {
                {
                    std::lock_guard lock(owner->adapter->commands);
                    if (owner->Poll(samples) != S_FALSE) break;
                }
                Sleep(1);
            } while (GetTickCount64() < deadline);
        }, pending, nullptr)) delete pending;
    }
    // The pipeline state lock keeps the backdrop alive while other pipelines use the adapter.
    template<class MakeTarget, class DrawWindow>
    HRESULT Capture(const Frame& frame, ID3D11Device* device, ID3D11DeviceContext* context, HANDLE frameWake,
        std::unique_lock<std::recursive_mutex>& commands, MakeTarget makeTarget, DrawWindow drawWindow, Target& result, bool& changed) {
        changed = false;
        if (GetTickCount64() < retryAt) return E_PENDING;
        Layers layers{};
        layers.area = {frame.x, frame.y, frame.x + frame.width, frame.y + frame.height};
        layers.self_pid = GetCurrentProcessId(); layers.require_opaque = true;
        EnumWindows(vnotch::controllers::backdrop::CollectLayer, reinterpret_cast<LPARAM>(&layers));
        const bool eligible = !layers.unsupported && layers.coverage_initialized && !layers.coverage_overflow &&
            layers.uncovered_count == 0 && layers.count > 0 && layers.count <= 4;
        if (!eligible || !Same(layers, frontLayers)) valid = false;
        const bool geometryMoved = state && state->pending && !Same(layers, pendingLayers);
        if (!eligible || geometryMoved) pendingCurrent = false;
        bool ready = true;
        if (state && state->pending) {
            HRESULT hr = CompleteValidation(changed);
            if (FAILED(hr)) return hr;
            ready = hr == S_OK;
        }
        if (!eligible) {
            valid = false;
            if (state && ready) {
                state->cache.ExpireUnused();
                if (!ineligibleSince) ineligibleSince = GetTickCount64();
                if (GetTickCount64() - ineligibleSince >= 2000) { front = {}; back = {}; }
            }
            return E_PENDING;
        }
        ineligibleSince = 0;
        if (ready) {
            if (!state) {
                auto next = std::make_unique<State>(); next->adapter = adapter; next->device = device; next->context = context;
                next->cache.BindDevice(device);
                next->cache.SetFrameWakeEvent(frameWake);
                if (SUCCEEDED(next->context.As(&next->context3)))
                    next->validationDone = winrt::handle{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
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
                    if (!state->cache.AcquireTexture(layer.hwnd, hits[i], layer.visible, layer.window, regions[i], afterPixelUpload)) available = false;
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
                context->End(state->query.Get());
                pendingLayers = layers; state->pending = pendingCurrent = true;
                if (valid && fresh && state->validationDone) {
                    // A live source should not stay one capture interval behind
                    // just because coverage validation finished between polls.
                    state->context3->Flush1(D3D11_CONTEXT_TYPE_ALL, state->validationDone.get());
                    commands.unlock();
                    WaitForSingleObject(state->validationDone.get(), 1);
                    commands.lock();
                    HRESULT hr = CompleteValidation(changed);
                    if (FAILED(hr)) return hr;
                } else context->Flush();
            }
            // No partial/stale result after a resize, identity change or capture failure.
            if (!available) { valid = false; return E_PENDING; }
        }
        // Keep the current presentation while its replacement is being validated.
        // A moving crop may still use the pixel compositor for the new position.
        if (!valid) return pendingCurrent && !geometryMoved ? S_FALSE : E_PENDING;
        result = front;
        return S_OK;
    }
};

}
