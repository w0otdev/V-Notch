#pragma once

#include "../vnotch_com.h"
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi.h>
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
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "windowsapp.lib")

namespace vnotch::controllers::backdrop::capture {
namespace wc = winrt::Windows::Graphics::Capture;
namespace wx = winrt::Windows::Graphics::DirectX;
using Microsoft::WRL::ComPtr;

// Capture individual underlying windows, so the notch remains visible to normal
// screenshots and is never fed back into its own backdrop. PrintWindow cannot
// supply fresh Chromium/DirectComposition frames at the display refresh rate.
class WindowCaptureCache final {
    struct Entry {
        HWND hwnd = nullptr;
        DWORD pid = 0, thread = 0;
        ULONGLONG used = 0;
        wc::GraphicsCaptureItem item{nullptr};
        wc::Direct3D11CaptureFramePool pool{nullptr};
        wc::GraphicsCaptureSession session{nullptr};
        wc::Direct3D11CaptureFrame frame{nullptr};
        winrt::Windows::Graphics::SizeInt32 size{};
        ComPtr<ID3D11Texture2D> texture, staging;
        RECT copied{};
        std::vector<uint32_t> pixels;
        bool copied_frame = false;

        ~Entry() noexcept {
            try { if (frame) frame.Close(); } catch (...) { }
            try { if (session) session.Close(); } catch (...) { }
            try { if (pool) pool.Close(); } catch (...) { }
        }
    };

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    wx::Direct3D11::IDirect3DDevice capture_device{nullptr};
    std::vector<std::unique_ptr<Entry>> entries;
    ULONGLONG retry_at = 0;

    static bool BorderlessAllowed() {
        static std::once_flag once;
        static bool allowed = false;
        std::call_once(once, [] {
            try {
                if (!winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(
                    L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IsBorderRequired")) return;
                const auto status = wc::GraphicsCaptureAccess::RequestAccessAsync(wc::GraphicsCaptureAccessKind::Borderless).get();
                allowed = status == winrt::Windows::Security::Authorization::AppCapabilityAccess::AppCapabilityAccessStatus::Allowed;
            } catch (...) { }
        });
        return allowed;
    }

    void EnsureDevice() {
        if (device && capture_device) return;
        vnotch::com::EnsureMta();
        if (!BorderlessAllowed() || !wc::GraphicsCaptureSession::IsSupported()) throw E_ACCESSDENIED;
        const D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0};
        winrt::check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
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
        EnsureDevice();
        if (entries.size() >= 4) {
            auto oldest = std::min_element(entries.begin(), entries.end(), [](const auto& a, const auto& b) { return a->used < b->used; });
            entries.erase(oldest);
        }
        auto entry = std::make_unique<Entry>();
        entry->hwnd = hwnd; entry->pid = pid; entry->thread = thread; entry->used = now;
        const auto interop = winrt::get_activation_factory<wc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        winrt::check_hresult(interop->CreateForWindow(hwnd, winrt::guid_of<wc::IGraphicsCaptureItem>(), winrt::put_abi(entry->item)));
        entry->size = entry->item.Size();
        entry->pool = wc::Direct3D11CaptureFramePool::CreateFreeThreaded(
            capture_device, wx::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, entry->size);
        entry->session = entry->pool.CreateCaptureSession(entry->item);
        entry->session.IsCursorCaptureEnabled(false);
        entry->session.IsBorderRequired(false);
        if (auto pacing = entry->session.try_as<wc::IGraphicsCaptureSession5>())
            pacing.MinUpdateInterval(winrt::Windows::Foundation::TimeSpan{0});
        entry->session.StartCapture();
        entries.push_back(std::move(entry));
        return *entries.back();
    }

    bool Copy(Entry& entry, const RECT& hit, const RECT& visible, const RECT& window,
        uint32_t* destination, int stride) {
        auto newest = entry.pool.TryGetNextFrame();
        if (newest) {
            // Drain queued frames after a slow fallback/startup instead of replaying them.
            for (int i = 0; i < 2; ++i) {
                auto next = entry.pool.TryGetNextFrame();
                if (!next) break;
                newest.Close(); newest = next;
            }
            const auto size = newest.ContentSize();
            if (size.Width != entry.size.Width || size.Height != entry.size.Height) {
                newest.Close();
                if (entry.frame) entry.frame.Close();
                entry.frame = nullptr; entry.texture.Reset(); entry.pixels.clear();
                entry.size = size;
                entry.pool.Recreate(capture_device, wx::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
                return false;
            }
            auto access = newest.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            ComPtr<ID3D11Texture2D> texture;
            winrt::check_hresult(access->GetInterface(IID_PPV_ARGS(&texture)));
            D3D11_TEXTURE2D_DESC desc{}, previous{};
            texture->GetDesc(&desc);
            if (entry.texture) entry.texture->GetDesc(&previous);
            if (previous.Width != desc.Width || previous.Height != desc.Height) {
                entry.texture.Reset();
                desc.Usage = D3D11_USAGE_DEFAULT; desc.CPUAccessFlags = 0;
                desc.BindFlags = 0; desc.MiscFlags = 0;
                winrt::check_hresult(device->CreateTexture2D(&desc, nullptr, &entry.texture));
            }
            // Keep our own snapshot so the two-buffer capture pool does not
            // retain one leased frame between consumer ticks and lose updates.
            context->CopyResource(entry.texture.Get(), texture.Get());
            if (entry.frame) entry.frame.Close();
            entry.frame = newest; entry.copied_frame = false;
        }
        if (!entry.texture) return false;
        // WGC normally excludes the invisible resize border. Accept only an
        // exact physical-size mapping; a resize in flight uses PrintWindow.
        RECT bounds{};
        if (entry.size.Width == visible.right - visible.left && entry.size.Height == visible.bottom - visible.top) bounds = visible;
        else if (entry.size.Width == window.right - window.left && entry.size.Height == window.bottom - window.top) bounds = window;
        else return false;
        const RECT crop{hit.left - bounds.left, hit.top - bounds.top, hit.right - bounds.left, hit.bottom - bounds.top};
        const int width = crop.right - crop.left, height = crop.bottom - crop.top;
        D3D11_TEXTURE2D_DESC source_desc{}; entry.texture->GetDesc(&source_desc);
        if (crop.left < 0 || crop.top < 0 || crop.right > static_cast<int>(source_desc.Width) || crop.bottom > static_cast<int>(source_desc.Height)) return false;
        if (!entry.copied_frame || !EqualRect(&crop, &entry.copied)) {
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
            const D3D11_BOX box{static_cast<UINT>(crop.left), static_cast<UINT>(crop.top), 0,
                static_cast<UINT>(crop.right), static_cast<UINT>(crop.bottom), 1};
            context->CopySubresourceRegion(entry.staging.Get(), 0, 0, 0, 0, entry.texture.Get(), 0, &box);
            entry.pixels.resize(static_cast<size_t>(width) * height);
            D3D11_MAPPED_SUBRESOURCE mapped{};
            winrt::check_hresult(context->Map(entry.staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
            for (int row = 0; row < height; ++row)
                std::memcpy(entry.pixels.data() + static_cast<size_t>(row) * width,
                    static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(row) * mapped.RowPitch, static_cast<size_t>(width) * 4);
            context->Unmap(entry.staging.Get(), 0);
            if (entry.frame) { entry.frame.Close(); entry.frame = nullptr; }
            entry.copied = crop; entry.copied_frame = true;
        }
        for (int row = 0; row < height; ++row)
            std::memcpy(destination + static_cast<size_t>(row) * stride,
                entry.pixels.data() + static_cast<size_t>(row) * width, static_cast<size_t>(width) * 4);
        return true;
    }

public:
    bool HasEntries() const noexcept { return !entries.empty(); }

    void ExpireUnused() noexcept {
        const ULONGLONG now = GetTickCount64();
        entries.erase(std::remove_if(entries.begin(), entries.end(), [&](const auto& entry) {
            return now - entry->used > 2000;
        }), entries.end());
        if (entries.empty()) {
            capture_device = nullptr;
            context.Reset(); device.Reset();
        }
    }

    bool CopyWindow(HWND hwnd, const RECT& hit, const RECT& visible, const RECT& window,
        uint32_t* destination, int stride) noexcept {
        if (GetTickCount64() < retry_at) return false;
        try {
            DWORD pid = 0;
            const DWORD thread = GetWindowThreadProcessId(hwnd, &pid);
            if (!thread || !pid) return false;
            return Copy(Get(hwnd, pid, thread), hit, visible, window, destination, stride);
        } catch (...) {
            entries.clear();
            capture_device = nullptr;
            context.Reset(); device.Reset();
            retry_at = GetTickCount64() + 3000;
            return false;
        }
    }
};
}
