#pragma once

#include "../vnotch_com.h"
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/Windows.Security.Authorization.AppCapabilityAccess.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <algorithm>
#include <cstring>
#include <memory>
#include <mutex>
#include <chrono>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "windowsapp.lib")

namespace vnotch::controllers::backdrop::capture {
namespace wc = winrt::Windows::Graphics::Capture;
namespace wx = winrt::Windows::Graphics::DirectX;
using Microsoft::WRL::ComPtr;
// [DEBUG-glass-freshness] Removed after checking the real source-window bounds.
inline std::atomic<intptr_t> debug_target{0};
inline std::atomic<int> debug_info[28]{};

inline bool HasVisiblePixels(const D3D11_MAPPED_SUBRESOURCE& mapped, int width, int height) noexcept {
    for (int y = 0; y < height; ++y) {
        const auto* row = reinterpret_cast<const uint32_t*>(static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch);
        for (int x = 0; x < width; ++x) if ((row[x] & 0xFF000000u) != 0) return true;
    }
    return false;
}

// Capture individual underlying windows, so the notch remains visible to normal
// screenshots and is never fed back into its own backdrop. PrintWindow cannot
// supply fresh Chromium/DirectComposition frames at the display refresh rate.
class WindowCaptureCache final {
    static constexpr int FramePoolBuffers = 2;
    struct FrameNotification {
        HANDLE wake = nullptr; // process-lifetime event owned by the composite worker
        std::atomic<uint64_t> version{0};
    };
    std::shared_ptr<FrameNotification> notification = std::make_shared<FrameNotification>();
    struct Entry {
        HWND hwnd = nullptr;
        DWORD pid = 0, thread = 0;
        ULONGLONG used = 0;
        wc::GraphicsCaptureItem item{nullptr};
        wc::Direct3D11CaptureFramePool pool{nullptr};
        wc::GraphicsCaptureSession session{nullptr};
        winrt::Windows::Graphics::SizeInt32 size{};
        wc::Direct3D11CaptureFrame gpu_frame{nullptr};
        ComPtr<ID3D11Texture2D> gpu_texture;
        ComPtr<ID3D11ShaderResourceView> gpu_view;
        ComPtr<ID3D11Texture2D> staging;
        RECT copied{};
        std::vector<uint32_t> pixels;
        bool copied_frame = false;
        ULONGLONG recreate_cooldown_until = 0;

        ~Entry() noexcept {
            try { if (gpu_frame) gpu_frame.Close(); } catch (...) { }
            try { if (session) session.Close(); } catch (...) { }
            try { if (pool) pool.Close(); } catch (...) { }
        }
    };

    struct RejectedWindow {
        HWND hwnd;
        ULONGLONG until;
    };

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    wx::Direct3D11::IDirect3DDevice capture_device{nullptr};
    std::vector<std::unique_ptr<Entry>> entries;
    std::vector<RejectedWindow> rejected_windows;
    ULONGLONG retry_at = 0;

    static void RequestBorderlessAccessOnce() noexcept {
        static std::once_flag once;
        std::call_once(once, [] {
            try {
                if (!winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(
                    L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IsBorderRequired")) return;
                (void)wc::GraphicsCaptureAccess::RequestAccessAsync(wc::GraphicsCaptureAccessKind::Borderless).get();
            } catch (...) { }
        });
    }

    static void ConfigureSession(wc::GraphicsCaptureSession& session) noexcept {
        try { session.IsCursorCaptureEnabled(false); } catch (...) { }
        // Recent Windows builds default to a ~16 ms capture interval, which
        // becomes every third vblank on a 144 Hz display. Zero/sub-ms values
        // are rejected on some builds; 1 ms lets the consumer pace the frames.
        try {
            if (winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(
                L"Windows.Graphics.Capture.GraphicsCaptureSession", L"MinUpdateInterval"))
                session.MinUpdateInterval(std::chrono::milliseconds(1));
        } catch (...) { }
        try {
            if (winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(
                L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IsBorderRequired")) {
                RequestBorderlessAccessOnce();
                session.IsBorderRequired(false);
            }
        } catch (...) { }
    }

    void EnsureDevice(HMONITOR wanted = nullptr) {
        if (device && capture_device) return;
        vnotch::com::EnsureMta();
        if (!wc::GraphicsCaptureSession::IsSupported()) winrt::throw_hresult(E_NOTIMPL);
        const D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0};
        ComPtr<IDXGIAdapter1> selected;
        if (wanted) {
            ComPtr<IDXGIFactory1> factory;
            winrt::check_hresult(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
            for (UINT a = 0; !selected; ++a) {
                ComPtr<IDXGIAdapter1> candidate;
                if (factory->EnumAdapters1(a, &candidate) == DXGI_ERROR_NOT_FOUND) break;
                for (UINT o = 0; ; ++o) {
                    ComPtr<IDXGIOutput> output;
                    if (candidate->EnumOutputs(o, &output) == DXGI_ERROR_NOT_FOUND) break;
                    DXGI_OUTPUT_DESC desc{};
                    if (SUCCEEDED(output->GetDesc(&desc)) && desc.Monitor == wanted) { selected = candidate; break; }
                }
            }
        }
        winrt::check_hresult(D3D11CreateDevice(selected.Get(), selected ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, ARRAYSIZE(levels), D3D11_SDK_VERSION,
            &device, nullptr, &context));
        ComPtr<ID3D11Multithread> multithread;
        winrt::check_hresult(context.As(&multithread));
        multithread->SetMultithreadProtected(TRUE);
        ComPtr<IDXGIDevice> dxgi;
        winrt::check_hresult(device.As(&dxgi));
        winrt::com_ptr<IInspectable> inspectable;
        winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(), inspectable.put()));
        capture_device = inspectable.as<wx::Direct3D11::IDirect3DDevice>();
    }

    Entry& Get(HWND hwnd, DWORD pid, DWORD thread) {
        const ULONGLONG now = GetTickCount64();
        entries.erase(std::remove_if(entries.begin(), entries.end(), [&](const auto& entry) {
            DWORD current_pid = 0;
            const DWORD current_thread = GetWindowThreadProcessId(entry->hwnd, &current_pid);
            return now - entry->used > 2000 || current_pid != entry->pid || current_thread != entry->thread;
        }), entries.end());
        for (auto& entry : entries) if (entry->hwnd == hwnd) { entry->used = now; return *entry; }
        const bool debug = reinterpret_cast<intptr_t>(hwnd) == debug_target.load();
        if (debug) debug_info[12] = 1;
        EnsureDevice(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
        if (entries.size() >= 4) {
            auto oldest = std::min_element(entries.begin(), entries.end(), [](const auto& a, const auto& b) { return a->used < b->used; });
            entries.erase(oldest);
        }
        auto entry = std::make_unique<Entry>();
        entry->hwnd = hwnd; entry->pid = pid; entry->thread = thread; entry->used = now;
        if (debug) debug_info[12] = 2;
        const auto interop = winrt::get_activation_factory<wc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        winrt::check_hresult(interop->CreateForWindow(hwnd, winrt::guid_of<wc::IGraphicsCaptureItem>(), winrt::put_abi(entry->item)));
        entry->size = entry->item.Size();
        if (entry->size.Width <= 0 || entry->size.Height <= 0) winrt::throw_hresult(E_PENDING);
        if (debug) debug_info[12] = 3;
        entry->pool = wc::Direct3D11CaptureFramePool::CreateFreeThreaded(
            capture_device, wx::DirectXPixelFormat::B8G8R8A8UIntNormalized, FramePoolBuffers, entry->size);
        if (notification->wake) {
            entry->pool.FrameArrived([signal = notification](const auto&, const auto&) {
                signal->version.fetch_add(1, std::memory_order_release);
                SetEvent(signal->wake);
            });
        }
        if (debug) debug_info[12] = 4;
        entry->session = entry->pool.CreateCaptureSession(entry->item);
        ConfigureSession(entry->session);
        if (debug) debug_info[12] = 5;
        entry->session.StartCapture();
        if (debug) debug_info[12] = 6;
        entries.push_back(std::move(entry));
        return *entries.back();
    }

    static bool RectContains(const RECT& outer, const RECT& inner) noexcept {
        return inner.left >= outer.left && inner.top >= outer.top &&
               inner.right <= outer.right && inner.bottom <= outer.bottom;
    }

    bool Copy(Entry& entry, const RECT& hit, const RECT& visible, const RECT& window,
        uint32_t* destination, int stride, bool* out_fresh) {
        if (out_fresh) *out_fresh = false;
        const bool debug = reinterpret_cast<intptr_t>(entry.hwnd) == debug_target.load();
        if (debug) ++debug_info[0];
        auto newest = entry.pool.TryGetNextFrame();
        struct Close { wc::Direct3D11CaptureFrame& frame; ~Close() noexcept { try { if (frame) frame.Close(); } catch (...) { } } } close{newest};
        if (newest) {
            if (debug) ++debug_info[1];
            // Drain queued frames after a slow fallback/startup instead of replaying them.
            for (int i = 0; i < FramePoolBuffers; ++i) {
                auto next = entry.pool.TryGetNextFrame();
                if (!next) break;
                newest.Close(); newest = next;
            }
            const auto size = newest.ContentSize();
            if (size.Width != entry.size.Width || size.Height != entry.size.Height) {
                newest.Close(); newest = nullptr;
                entry.pixels.clear(); entry.copied_frame = false;
                entry.size = size;
                if (size.Width > 0 && size.Height > 0) {
                    entry.pool.Recreate(capture_device, wx::DirectXPixelFormat::B8G8R8A8UIntNormalized, FramePoolBuffers, size);
                }
                return false;
            }
        }

        const int vis_w = visible.right - visible.left, vis_h = visible.bottom - visible.top;
        const int win_w = window.right - window.left, win_h = window.bottom - window.top;
        if (debug) {
            debug_info[3] = entry.size.Width; debug_info[4] = entry.size.Height;
            debug_info[5] = vis_w; debug_info[6] = vis_h;
            debug_info[7] = win_w; debug_info[8] = win_h;
        }
        // WGC normally excludes the invisible resize border. Allow <= 2 px tolerance
        // for fractional-DPI border rounding; retain the published strip during resize.
        const auto near_dim = [](int a, int b) noexcept { return a >= b - 2 && a <= b + 2; };
        RECT bounds{};
        if (entry.size.Width == vis_w && entry.size.Height == vis_h) bounds = visible;
        else if (entry.size.Width == win_w && entry.size.Height == win_h) bounds = window;
        else if (near_dim(entry.size.Width, vis_w) && near_dim(entry.size.Height, vis_h)) bounds = visible;
        else if (near_dim(entry.size.Width, win_w) && near_dim(entry.size.Height, win_h)) bounds = window;
        else { if (debug) ++debug_info[2]; return false; }

        const int width = hit.right - hit.left, height = hit.bottom - hit.top;
        if (width <= 0 || height <= 0 || width > entry.size.Width || height > entry.size.Height) return false;
        const LONG crop_left = std::clamp<LONG>(hit.left - bounds.left, 0, static_cast<LONG>(entry.size.Width) - width);
        const LONG crop_top = std::clamp<LONG>(hit.top - bounds.top, 0, static_cast<LONG>(entry.size.Height) - height);
        const RECT crop{crop_left, crop_top, crop_left + width, crop_top + height};

        if (newest) {
            auto access = newest.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            ComPtr<ID3D11Texture2D> texture;
            winrt::check_hresult(access->GetInterface(IID_PPV_ARGS(&texture)));
            D3D11_TEXTURE2D_DESC source_desc{};
            texture->GetDesc(&source_desc);
            if (static_cast<UINT>(crop.right) > source_desc.Width || static_cast<UINT>(crop.bottom) > source_desc.Height) return false;

            D3D11_TEXTURE2D_DESC staging_desc{};
            if (entry.staging) entry.staging->GetDesc(&staging_desc);
            if (staging_desc.Width != static_cast<UINT>(width) || staging_desc.Height != static_cast<UINT>(height)) {
                entry.staging.Reset();
                staging_desc = {};
                staging_desc.Width = width; staging_desc.Height = height;
                staging_desc.MipLevels = staging_desc.ArraySize = staging_desc.SampleDesc.Count = 1;
                staging_desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                staging_desc.Usage = D3D11_USAGE_STAGING; staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                winrt::check_hresult(device->CreateTexture2D(&staging_desc, nullptr, &entry.staging));
            }
            // Copy only the intersecting crop. Keep the frame checked out until
            // Map completes the GPU copy, so the pool cannot recycle its surface
            // while CopySubresourceRegion is still queued on the GPU.
            const D3D11_BOX box{static_cast<UINT>(crop.left), static_cast<UINT>(crop.top), 0,
                static_cast<UINT>(crop.right), static_cast<UINT>(crop.bottom), 1};
            context->CopySubresourceRegion(entry.staging.Get(), 0, 0, 0, 0, texture.Get(), 0, &box);
            D3D11_MAPPED_SUBRESOURCE mapped{};
            winrt::check_hresult(context->Map(entry.staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
            struct Unmap { ID3D11DeviceContext* context; ID3D11Texture2D* texture; ~Unmap() { context->Unmap(texture, 0); } } unmap{context.Get(), entry.staging.Get()};
            // A cleared/empty WGC surface is not an opaque black image. Validate
            // alpha before replacing the cache, and keep genuine dark content.
            if (HasVisiblePixels(mapped, width, height)) {
                entry.pixels.resize(static_cast<size_t>(width) * height);
                for (int row = 0; row < height; ++row)
                    std::memcpy(entry.pixels.data() + static_cast<size_t>(row) * width,
                        static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(row) * mapped.RowPitch, static_cast<size_t>(width) * 4);
                if (debug) ++debug_info[9];
                entry.copied = crop;
                entry.copied_frame = true;
                if (out_fresh) *out_fresh = true;
            }
        }
        if (!entry.copied_frame || !RectContains(entry.copied, crop)) {
            // Static window uncovered a wider strip than previously cached:
            // request a fresh pool frame asynchronously and retain the published strip.
            const ULONGLONG now = GetTickCount64();
            if (now >= entry.recreate_cooldown_until && entry.size.Width > 0 && entry.size.Height > 0) {
                entry.recreate_cooldown_until = now + 250;
                try { entry.pool.Recreate(capture_device, wx::DirectXPixelFormat::B8G8R8A8UIntNormalized, FramePoolBuffers, entry.size); } catch (...) { }
            }
            return false;
        }

        const int cached_w = entry.copied.right - entry.copied.left;
        const int sub_x = crop.left - entry.copied.left;
        const int sub_y = crop.top - entry.copied.top;
        for (int row = 0; row < height; ++row)
            std::memcpy(destination + static_cast<size_t>(row) * stride,
                entry.pixels.data() + static_cast<size_t>(sub_y + row) * cached_w + sub_x, static_cast<size_t>(width) * 4);
        return true;
    }

public:
    // GPU clients serialize this cache with their device command lock and only
    // call again after their completion query. Thus a pool frame is never closed
    // while a composition command still samples it.
    void BindDevice(ID3D11Device* shared) {
        vnotch::com::EnsureMta();
        if (device.Get() == shared) return;
        entries.clear(); capture_device = nullptr;
        device = shared; device->GetImmediateContext(&context);
        ComPtr<IDXGIDevice> dxgi; winrt::check_hresult(device.As(&dxgi));
        winrt::com_ptr<IInspectable> inspectable;
        winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(), inspectable.put()));
        capture_device = inspectable.as<wx::Direct3D11::IDirect3DDevice>();
    }
    struct TextureRegion {
        ComPtr<ID3D11ShaderResourceView> view;
        RECT crop{};
        UINT width = 0, height = 0;
        bool fresh = false;
    };
    bool AcquireTexture(HWND hwnd, const RECT& hit, const RECT& visible, const RECT& window, TextureRegion& out) {
        DWORD pid = 0;
        const DWORD thread = GetWindowThreadProcessId(hwnd, &pid);
        if (!thread || pid == GetCurrentProcessId()) return false;
        auto& entry = Get(hwnd, pid, thread);
        auto newest = entry.pool.TryGetNextFrame();
        if (newest) {
            for (int i = 0; i < FramePoolBuffers; ++i) {
                auto next = entry.pool.TryGetNextFrame();
                if (!next) break;
                newest.Close(); newest = next;
            }
            if (entry.gpu_frame) entry.gpu_frame.Close();
            entry.gpu_frame = newest; entry.gpu_texture.Reset(); entry.gpu_view.Reset();
            const auto size = newest.ContentSize();
            if (size.Width != entry.size.Width || size.Height != entry.size.Height) {
                entry.gpu_frame.Close(); entry.gpu_frame = nullptr; entry.size = size;
                if (size.Width > 0 && size.Height > 0)
                    entry.pool.Recreate(capture_device, wx::DirectXPixelFormat::B8G8R8A8UIntNormalized, FramePoolBuffers, size);
                return false;
            }
            auto access = newest.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            winrt::check_hresult(access->GetInterface(IID_PPV_ARGS(&entry.gpu_texture)));
            winrt::check_hresult(device->CreateShaderResourceView(entry.gpu_texture.Get(), nullptr, &entry.gpu_view));
            out.fresh = true;
        }
        if (!entry.gpu_view) return false;
        RECT bounds{};
        if (entry.size.Width == visible.right - visible.left && entry.size.Height == visible.bottom - visible.top) bounds = visible;
        else if (entry.size.Width == window.right - window.left && entry.size.Height == window.bottom - window.top) bounds = window;
        else return false;
        out.crop = {hit.left - bounds.left, hit.top - bounds.top, hit.right - bounds.left, hit.bottom - bounds.top};
        D3D11_TEXTURE2D_DESC desc{}; entry.gpu_texture->GetDesc(&desc);
        if (out.crop.left < 0 || out.crop.top < 0 || out.crop.right > static_cast<LONG>(desc.Width) ||
            out.crop.bottom > static_cast<LONG>(desc.Height)) return false;
        out.view = entry.gpu_view; out.width = desc.Width; out.height = desc.Height;
        return true;
    }

    void SetFrameWakeEvent(HANDLE wake) noexcept { notification->wake = wake; }
    uint64_t FrameVersion() const noexcept { return notification->version.load(std::memory_order_acquire); }
    bool HasEntries() const noexcept { return !entries.empty(); }
    bool HasWindow(HWND hwnd) const noexcept {
        for (const auto& entry : entries) if (entry->hwnd == hwnd) return true;
        return false;
    }

    void ExpireUnused() noexcept {
        const ULONGLONG now = GetTickCount64();
        rejected_windows.erase(std::remove_if(rejected_windows.begin(), rejected_windows.end(), [&](const RejectedWindow& r) {
            return now >= r.until || !IsWindow(r.hwnd);
        }), rejected_windows.end());
        entries.erase(std::remove_if(entries.begin(), entries.end(), [&](const auto& entry) {
            return now - entry->used > 2000;
        }), entries.end());
        if (entries.empty()) {
            capture_device = nullptr;
            context.Reset(); device.Reset();
        }
    }

    bool CopyWindow(HWND hwnd, const RECT& hit, const RECT& visible, const RECT& window,
        uint32_t* destination, int stride, bool* out_fresh = nullptr) noexcept {
        if (out_fresh) *out_fresh = false;
        const ULONGLONG now = GetTickCount64();
        if (now < retry_at) return false;
        rejected_windows.erase(std::remove_if(rejected_windows.begin(), rejected_windows.end(), [&](const RejectedWindow& r) {
            return now >= r.until || !IsWindow(r.hwnd);
        }), rejected_windows.end());
        for (const auto& r : rejected_windows) {
            if (r.hwnd == hwnd) return false;
        }
        try {
            const auto debug_start = std::chrono::steady_clock::now();
            DWORD pid = 0;
            const DWORD thread = GetWindowThreadProcessId(hwnd, &pid);
            if (!thread || !pid) return false;
            const bool copied = Copy(Get(hwnd, pid, thread), hit, visible, window, destination, stride, out_fresh);
            const int micros = static_cast<int>(std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - debug_start).count());
            if (reinterpret_cast<intptr_t>(hwnd) == debug_target.load()) debug_info[13] += micros;
            debug_info[25] += micros; ++debug_info[24];
            return copied;
        } catch (...) {
            const winrt::hresult hr = winrt::to_hresult();
            if (reinterpret_cast<intptr_t>(hwnd) == debug_target.load()) { debug_info[10] = hr; ++debug_info[11]; }
            const ULONGLONG fail_now = GetTickCount64();
            const bool device_lost = !device || !capture_device || FAILED(device->GetDeviceRemovedReason());
            if (device_lost) {
                entries.clear();
                capture_device = nullptr;
                context.Reset(); device.Reset();
                retry_at = fail_now + 1000;
            } else {
                entries.erase(std::remove_if(entries.begin(), entries.end(), [&](const auto& entry) {
                    return entry->hwnd == hwnd;
                }), entries.end());
                if (rejected_windows.size() >= 32) rejected_windows.erase(rejected_windows.begin());
                const ULONGLONG backoff_ms = (hr == E_PENDING) ? 500 : 3000;
                rejected_windows.push_back({ hwnd, fail_now + backoff_ms });
            }
            return false;
        }
    }
};
}
