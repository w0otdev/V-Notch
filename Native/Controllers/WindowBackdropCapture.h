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
public:
    // Borrowed until the next cache operation. Stride includes the padding
    // retained for moving or expanding glass.
    struct PixelRegion {
        const uint32_t* pixels = nullptr;
        int stride = 0;
        bool fresh = false;
    };
private:
    static constexpr int FramePoolBuffers = 2;
    struct FrameNotification {
        winrt::handle wake;
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
        int64_t copied_at = 0;
        bool saw_initial_crop = false, has_initial_color = false, showing_initial_color = false;
        uint32_t initial_color = 0;
        ULONGLONG confirm_initial_color_at = 0;
        ULONGLONG recreate_cooldown_until = 0;
        bool awaiting_repaint = false;
        ULONGLONG first_resized_at = 0;
        int64_t first_resized_sample = 0;

        void ResizePool(const wx::Direct3D11::IDirect3DDevice& device, winrt::Windows::Graphics::SizeInt32 next) {
            size = next;
            // Keep draining the new pool while the window repaints its client.
            // A static source can still publish its retained frame after the grace period.
            awaiting_repaint = true;
            first_resized_at = 0;
            recreate_cooldown_until = GetTickCount64() + 100;
            if (size.Width > 0 && size.Height > 0)
                pool.Recreate(device, wx::DirectXPixelFormat::B8G8R8A8UIntNormalized, FramePoolBuffers, size);
        }

        bool RepaintReady(int64_t sample) noexcept {
            if (!awaiting_repaint) return true;
            const auto now = GetTickCount64();
            if (!first_resized_at) {
                first_resized_at = now;
                first_resized_sample = sample;
            }
            if (sample == first_resized_sample && now - first_resized_at < 100) return false;
            awaiting_repaint = false;
            return true;
        }

        void ObserveCrop() noexcept {
            if (!saw_initial_crop) {
                saw_initial_crop = true;
                initial_color = pixels.front();
                has_initial_color = showing_initial_color =
                    std::all_of(pixels.begin(), pixels.end(), [&](uint32_t p) { return p == initial_color; });
            } else if (has_initial_color) {
                // Composited child windows can briefly expose their original
                // backing color during a surface swap, even after WGC reports
                // the right size. Confirm a return to that exact flat crop.
                const bool flat = pixels[pixels.size() / 2] == initial_color &&
                    std::all_of(pixels.begin(), pixels.end(), [&](uint32_t p) { return p == initial_color; });
                if (flat && !showing_initial_color) confirm_initial_color_at = GetTickCount64() + 40;
                if (!flat) confirm_initial_color_at = 0;
                showing_initial_color = flat;
            }
        }

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
                wc::GraphicsCaptureAccess::RequestAccessAsync(wc::GraphicsCaptureAccessKind::Borderless)
                    .Completed([](auto const& access, auto status) noexcept {
                        if (status == winrt::Windows::Foundation::AsyncStatus::Completed) {
                            try { (void)access.GetResults(); } catch (...) {}
                        }
                    });
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
                SetEvent(signal->wake.get());
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

    bool ReadRegion(Entry& entry, const RECT& hit, const RECT& visible, const RECT& window, PixelRegion& out) {
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
                entry.ResizePool(capture_device, size);
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

            // Cache a padded crop. While the notch resizes, `hit` grows every
            // frame; a static window (e.g. MyDockFinder's top bar) sends no new
            // WGC frame, so an exact-crop cache could not serve the wider strip
            // and the layer dropped out until the next frame. Same pixels, 1:1.
            constexpr LONG kCachePad = 192;
            const RECT cache{
                std::max<LONG>(0, crop.left - kCachePad), std::max<LONG>(0, crop.top - kCachePad),
                std::min<LONG>(static_cast<LONG>(source_desc.Width), crop.right + kCachePad),
                std::min<LONG>(static_cast<LONG>(source_desc.Height), crop.bottom + kCachePad)};
            const int cache_w = cache.right - cache.left, cache_h = cache.bottom - cache.top;

            D3D11_TEXTURE2D_DESC staging_desc{};
            if (entry.staging) entry.staging->GetDesc(&staging_desc);
            // Grow-only staging: a resizing hit must not reallocate every frame.
            if (staging_desc.Width < static_cast<UINT>(cache_w) || staging_desc.Height < static_cast<UINT>(cache_h)) {
                entry.staging.Reset();
                const UINT grow_w = std::max<UINT>(staging_desc.Width, static_cast<UINT>(cache_w));
                const UINT grow_h = std::max<UINT>(staging_desc.Height, static_cast<UINT>(cache_h));
                staging_desc = {};
                staging_desc.Width = grow_w; staging_desc.Height = grow_h;
                staging_desc.MipLevels = staging_desc.ArraySize = staging_desc.SampleDesc.Count = 1;
                staging_desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                staging_desc.Usage = D3D11_USAGE_STAGING; staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                winrt::check_hresult(device->CreateTexture2D(&staging_desc, nullptr, &entry.staging));
            }
            // Copy only the padded crop. Keep the frame checked out until
            // Map completes the GPU copy, so the pool cannot recycle its surface
            // while CopySubresourceRegion is still queued on the GPU.
            const D3D11_BOX box{static_cast<UINT>(cache.left), static_cast<UINT>(cache.top), 0,
                static_cast<UINT>(cache.right), static_cast<UINT>(cache.bottom), 1};
            context->CopySubresourceRegion(entry.staging.Get(), 0, 0, 0, 0, texture.Get(), 0, &box);
            D3D11_MAPPED_SUBRESOURCE mapped{};
            winrt::check_hresult(context->Map(entry.staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
            struct Unmap { ID3D11DeviceContext* context; ID3D11Texture2D* texture; ~Unmap() { context->Unmap(texture, 0); } } unmap{context.Get(), entry.staging.Get()};
            // A cleared/empty WGC surface is not an opaque black image. Validate
            // alpha before replacing the cache, and keep genuine dark content.
            // Judge the requested crop only, as before; padding may be empty.
            D3D11_MAPPED_SUBRESOURCE crop_view = mapped;
            crop_view.pData = static_cast<uint8_t*>(mapped.pData) +
                static_cast<size_t>(crop.top - cache.top) * mapped.RowPitch + static_cast<size_t>(crop.left - cache.left) * 4;
            if (HasVisiblePixels(crop_view, width, height)) {
                entry.pixels.resize(static_cast<size_t>(cache_w) * cache_h);
                for (int row = 0; row < cache_h; ++row)
                    std::memcpy(entry.pixels.data() + static_cast<size_t>(row) * cache_w,
                        static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(row) * mapped.RowPitch, static_cast<size_t>(cache_w) * 4);
                if (debug) ++debug_info[9];
                entry.copied = cache;
                entry.copied_frame = true;
                entry.copied_at = newest.SystemRelativeTime().count();
                entry.ObserveCrop();
                out.fresh = true;
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
        out.pixels = entry.pixels.data() + static_cast<size_t>(sub_y) * cached_w + sub_x;
        out.stride = cached_w;
        if (!entry.RepaintReady(entry.copied_at) || GetTickCount64() < entry.confirm_initial_color_at) return false;
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
    bool AcquireTexture(HWND hwnd, const RECT& hit, const RECT& visible, const RECT& window, TextureRegion& out,
                        int64_t afterPixelUpload) {
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
                entry.gpu_frame.Close(); entry.gpu_frame = nullptr;
                entry.ResizePool(capture_device, size);
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
        if (afterPixelUpload || entry.awaiting_repaint) {
            const int64_t capturedAt = entry.gpu_frame.SystemRelativeTime().count();
            if (capturedAt <= afterPixelUpload || !entry.RepaintReady(capturedAt)) return false;
        }
        out.view = entry.gpu_view; out.width = desc.Width; out.height = desc.Height;
        return true;
    }

    // Bind before opening the first pool. A late FrameArrived can outlive
    // the renderer, so its shared notification owns a duplicate event handle.
    void SetFrameWakeEvent(HANDLE wake) noexcept {
        HANDLE copy = nullptr;
        if (wake && DuplicateHandle(GetCurrentProcess(), wake, GetCurrentProcess(), &copy,
                                    EVENT_MODIFY_STATE, FALSE, 0))
            notification->wake = winrt::handle{copy};
    }
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

    bool ReadWindow(HWND hwnd, const RECT& hit, const RECT& visible, const RECT& window, PixelRegion& out) noexcept {
        out = {};
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
            const bool copied = ReadRegion(Get(hwnd, pid, thread), hit, visible, window, out);
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
