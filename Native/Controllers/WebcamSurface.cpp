#include "vnotch_native.h"
#include <memorybuffer.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Imaging.h>

extern "C" int32_t vn_webcam_convert_rgb_in_place(uint8_t*, int32_t, uint8_t, int32_t) noexcept;
extern "C" int32_t vn_webcam_convert_gray8_to_bgra8(const uint8_t*, int32_t, uint8_t*) noexcept;

VN_EXPORT HRESULT vn_webcam_write_surface(IUnknown* source, uint8_t* target, int pitch, int width, int height) noexcept
{
    using namespace winrt::Windows::Graphics::Imaging;
    if (!source || !target || width <= 0 || height <= 0 || width > INT_MAX / 4 || pitch < width * 4) return E_INVALIDARG;
    try {
        SoftwareBitmap bitmap{nullptr};
        winrt::check_hresult(source->QueryInterface(winrt::guid_of<ISoftwareBitmap>(), winrt::put_abi(bitmap)));
        if (bitmap.PixelWidth() != width || bitmap.PixelHeight() != height) return E_INVALIDARG;
        auto format = bitmap.BitmapPixelFormat();
        if (format != BitmapPixelFormat::Bgra8 && format != BitmapPixelFormat::Rgba8 && format != BitmapPixelFormat::Gray8) {
            bitmap = SoftwareBitmap::Convert(bitmap, BitmapPixelFormat::Bgra8, BitmapAlphaMode::Premultiplied);
            format = BitmapPixelFormat::Bgra8;
        }
        auto alpha = static_cast<int>(bitmap.BitmapAlphaMode());
        auto buffer = bitmap.LockBuffer(BitmapBufferAccessMode::Read);
        auto plane = buffer.GetPlaneDescription(0);
        auto reference = buffer.CreateReference();
        auto access = reference.as<::Windows::Foundation::IMemoryBufferByteAccess>();
        uint8_t* pixels{};
        uint32_t capacity{};
        winrt::check_hresult(access->GetBuffer(&pixels, &capacity));
        int row_bytes = width * (format == BitmapPixelFormat::Gray8 ? 1 : 4);
        int64_t last = plane.StartIndex + int64_t(height - 1) * plane.Stride;
        if (!pixels || plane.Width < width || plane.Height < height || plane.StartIndex < 0 || last < 0 ||
            std::abs(int64_t(plane.Stride)) < row_bytes ||
            std::max<int64_t>(last, plane.StartIndex) + row_bytes > capacity) return E_INVALIDARG;
        for (int y = 0; y < height; ++y) {
            const auto src = pixels + plane.StartIndex + int64_t(y) * plane.Stride;
            auto dst = target + int64_t(y) * pitch;
            if (format == BitmapPixelFormat::Gray8) vn_webcam_convert_gray8_to_bgra8(src, width, dst);
            else {
                memcpy(dst, src, row_bytes);
                if (format == BitmapPixelFormat::Rgba8 || alpha != 0)
                    vn_webcam_convert_rgb_in_place(dst, row_bytes, format == BitmapPixelFormat::Rgba8, alpha);
            }
        }
        return S_OK;
    } catch (...) { return winrt::to_hresult(); }
}
