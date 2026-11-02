#include "vnotch_com.h"
#include <wincodec.h>
#include <wrl/client.h>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "windowscodecs.lib")

namespace {
using Microsoft::WRL::ComPtr;
constexpr UINT PreviewEdge = 420;
struct PreviewInfo { int32_t width, height; double dpi_x, dpi_y; };
void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
}

VN_EXPORT int32_t __stdcall vn_clipboard_image_preview(const wchar_t* path, int32_t path_length,
    const uint8_t* encoded, int32_t encoded_length, uint8_t* pixels, int32_t capacity, PreviewInfo* info) noexcept {
    if (!info) return E_INVALIDARG;
    *info = {};
    const bool from_file = path && path_length > 0 && !encoded && encoded_length == 0;
    const bool from_memory = !path && path_length == 0 && encoded && encoded_length > 0;
    if ((!from_file && !from_memory) || path_length > 32767 || !pixels || capacity <= 0) return E_INVALIDARG;
    try {
        vnotch::com::ComScope com;
        ComPtr<IWICImagingFactory> factory;
        Check(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)));
        ComPtr<IWICStream> stream;
        ComPtr<IWICBitmapDecoder> decoder;
        if (from_file) {
            std::wstring filename(path, path_length);
            if (filename.find(L'\0') != std::wstring::npos) return E_INVALIDARG;
            Check(factory->CreateDecoderFromFilename(filename.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder));
        } else {
            Check(factory->CreateStream(&stream));
            // The caller pins the encoded bytes until every WIC object is released.
            Check(stream->InitializeFromMemory(const_cast<BYTE*>(encoded), static_cast<DWORD>(encoded_length)));
            Check(factory->CreateDecoderFromStream(stream.Get(), nullptr, WICDecodeMetadataCacheOnDemand, &decoder));
        }
        ComPtr<IWICBitmapFrameDecode> frame;
        Check(decoder->GetFrame(0, &frame));
        UINT width = 0, height = 0;
        Check(frame->GetSize(&width, &height));
        // Bound hostile image dimensions before requesting any pixel work.
        if (!width || !height || width > 65535 || height > 65535 || static_cast<uint64_t>(width) * height > 256ULL * 1024 * 1024)
            return WINCODEC_ERR_IMAGESIZEOUTOFRANGE;
        UINT out_width, out_height;
        if (width >= height) {
            out_width = std::min(width, PreviewEdge);
            out_height = std::max(1u, static_cast<UINT>(static_cast<uint64_t>(height) * out_width / width));
        } else {
            out_height = std::min(height, PreviewEdge);
            out_width = std::max(1u, static_cast<UINT>(static_cast<uint64_t>(width) * out_height / height));
        }
        const UINT stride = out_width * 4, bytes = stride * out_height;
        if (static_cast<UINT>(capacity) < bytes) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        double dpi_x = 96, dpi_y = 96;
        Check(frame->GetResolution(&dpi_x, &dpi_y));
        if (!std::isfinite(dpi_x) || dpi_x <= 0) dpi_x = 96;
        if (!std::isfinite(dpi_y) || dpi_y <= 0) dpi_y = 96;
        ComPtr<IWICBitmapSource> source = frame;
        ComPtr<IWICBitmapScaler> scaler;
        if (out_width != width || out_height != height) {
            Check(factory->CreateBitmapScaler(&scaler));
            Check(scaler->Initialize(source.Get(), out_width, out_height, WICBitmapInterpolationModeFant));
            source = scaler;
        }
        ComPtr<IWICFormatConverter> converter;
        Check(factory->CreateFormatConverter(&converter));
        // Match IgnoreColorProfile: convert channels/alpha without applying ICC metadata.
        Check(converter->Initialize(source.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone,
            nullptr, 0, WICBitmapPaletteTypeCustom));
        Check(converter->CopyPixels(nullptr, stride, bytes, pixels));
        *info = { static_cast<int32_t>(out_width), static_cast<int32_t>(out_height), dpi_x, dpi_y };
        return S_OK;
    } catch (HRESULT hr) { return hr; }
      catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
      catch (...) { return E_FAIL; }
}
