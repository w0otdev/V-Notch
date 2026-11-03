#include "vnotch_common.h"
#include "vnotch_handle_registry.h"
#include "vnotch_com.h"
#include <bcrypt.h>
#include <wincodec.h>
#include <shlwapi.h>
#include <memory>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "shlwapi.lib")

namespace {
struct Hash {
    BCRYPT_HASH_HANDLE handle{};
    SRWLOCK lock = SRWLOCK_INIT;
    ~Hash() { if (handle) BCryptDestroyHash(handle); }
    bool create() noexcept { return BCryptCreateHash(BCRYPT_SHA256_ALG_HANDLE, &handle, nullptr, 0, nullptr, 0, BCRYPT_HASH_REUSABLE_FLAG) >= 0; }
    bool append(const uint8_t* bytes, ULONG length) noexcept { return !length || BCryptHashData(handle, const_cast<PUCHAR>(bytes), length, 0) >= 0; }
    bool finish(uint8_t* bytes) noexcept { return BCryptFinishHash(handle, bytes, 32, 0) >= 0; }
};
vnotch::HandleRegistry<Hash> hashes;
bool dimensions(Hash& hash, int32_t width, int32_t height) noexcept {
    const int32_t dims[]{width, height};
    return hash.append(reinterpret_cast<const uint8_t*>(dims), sizeof(dims));
}
}

VN_EXPORT int32_t __stdcall vn_clipboard_hash_create(int64_t* result) noexcept {
    if (!result) return VN_ERR_INVALID_ARG;
    *result = 0;
    try {
        auto hash = std::make_unique<Hash>();
        if (!hash->create()) return VN_ERR_FAIL;
        *result = hashes.Add(std::move(hash));
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t __stdcall vn_clipboard_hash_close(int64_t handle) noexcept {
    return hashes.Remove(handle) ? VN_OK : VN_ERR_INVALID_ARG;
}
VN_EXPORT int32_t __stdcall vn_clipboard_hash_append(int64_t handle, const uint8_t* bytes, int32_t length) noexcept {
    if (length < 0 || (length && !bytes)) return VN_ERR_INVALID_ARG;
    auto hash = hashes.Acquire(handle);
    if (!hash) return VN_ERR_INVALID_ARG;
    vnotch::com::Guard guard(&hash->lock);
    return hash->append(bytes, static_cast<ULONG>(length)) ? VN_OK : VN_ERR_FAIL;
}
VN_EXPORT int32_t __stdcall vn_clipboard_hash_append_utf16(int64_t handle, const char16_t* value, int32_t length, uint8_t framed) noexcept {
    if (length < 0 || (length && !value)) return VN_ERR_INVALID_ARG;
    try {
        auto hash = hashes.Acquire(handle);
        if (!hash) return VN_ERR_INVALID_ARG;
        vnotch::com::Guard guard(&hash->lock);
        const auto text = reinterpret_cast<const wchar_t*>(value);
        const int bytes = length ? WideCharToMultiByte(CP_UTF8, 0, text, length, nullptr, 0, nullptr, nullptr) : 0;
        if (length && bytes <= 0) return VN_ERR_FAIL;
        if (framed && !hash->append(reinterpret_cast<const uint8_t*>(&bytes), sizeof(bytes))) return VN_ERR_FAIL;
        if (!bytes) return VN_OK;
        uint8_t stack[512];
        std::vector<uint8_t> storage;
        uint8_t* buffer = stack;
        if (bytes > sizeof(stack)) { storage.resize(bytes); buffer = storage.data(); }
        struct Clear { uint8_t* bytes; size_t length; ~Clear() { SecureZeroMemory(bytes, length); } } clear{buffer, static_cast<size_t>(bytes)};
        if (WideCharToMultiByte(CP_UTF8, 0, text, length, reinterpret_cast<char*>(buffer), bytes, nullptr, nullptr) != bytes) return VN_ERR_FAIL;
        return hash->append(buffer, bytes) ? VN_OK : VN_ERR_FAIL;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t __stdcall vn_clipboard_hash_finish(int64_t handle, uint8_t* output, int32_t capacity) noexcept {
    if (!output || capacity < 32) return VN_ERR_INVALID_ARG;
    auto hash = hashes.Acquire(handle);
    if (!hash) return VN_ERR_INVALID_ARG;
    vnotch::com::Guard guard(&hash->lock);
    return hash->finish(output) ? VN_OK : VN_ERR_FAIL;
}
VN_EXPORT int32_t __stdcall vn_clipboard_hash_bytes(const uint8_t* input, int32_t length, uint8_t* output, int32_t capacity) noexcept {
    if (length < 0 || (length && !input) || !output || capacity < 32) return VN_ERR_INVALID_ARG;
    return BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, const_cast<PUCHAR>(input), length, output, 32) >= 0 ? VN_OK : VN_ERR_FAIL;
}
VN_EXPORT int32_t __stdcall vn_clipboard_hash_pixels(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride, uint8_t* output, int32_t capacity) noexcept {
    if (!pixels || width <= 0 || height <= 0 || width > INT32_MAX / 4 || stride < width * 4 || !output || capacity < 32 ||
        static_cast<uint64_t>(stride) * height > SIZE_MAX) return VN_ERR_INVALID_ARG;
    Hash hash;
    if (!hash.create() || !dimensions(hash, width, height)) return VN_ERR_FAIL;
    for (int32_t y = 0; y < height; ++y)
        if (!hash.append(pixels + static_cast<size_t>(y) * stride, static_cast<ULONG>(width * 4))) return VN_ERR_FAIL;
    return hash.finish(output) ? VN_OK : VN_ERR_FAIL;
}
VN_EXPORT int32_t __stdcall vn_clipboard_hash_image(const uint8_t* input, int32_t length, const char16_t* path, int32_t path_length,
    uint8_t* output, int32_t capacity) noexcept {
    if (length < 0 || path_length < 0 || (length && !input) || (path_length && !path) || !output || capacity < 32 ||
        (!length && !path_length)) return VN_ERR_INVALID_ARG;
    try {
        vnotch::com::EnsureMta();
        vnotch::ComPtr<IWICImagingFactory> factory;
        vnotch::ComPtr<IWICBitmapDecoder> decoder;
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(factory.Put())))) return VN_ERR_FAIL;
        vnotch::ComPtr<IStream> stream;
        HRESULT hr;
        if (path_length) {
            const std::wstring filename(reinterpret_cast<const wchar_t*>(path), path_length);
            hr = factory->CreateDecoderFromFilename(filename.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, decoder.Put());
        } else {
            *stream.Put() = SHCreateMemStream(input, length);
            if (!stream) return VN_ERR_FAIL;
            hr = factory->CreateDecoderFromStream(stream.ptr, nullptr, WICDecodeMetadataCacheOnDemand, decoder.Put());
        }
        vnotch::ComPtr<IWICBitmapFrameDecode> frame;
        vnotch::ComPtr<IWICFormatConverter> converter;
        if (FAILED(hr) || FAILED(decoder->GetFrame(0, frame.Put())) || FAILED(factory->CreateFormatConverter(converter.Put())) ||
            FAILED(converter->Initialize(frame.ptr, GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom))) return VN_ERR_FAIL;
        UINT width = 0, height = 0;
        if (FAILED(converter->GetSize(&width, &height)) || !width || !height || width > INT32_MAX / 64 || height > INT32_MAX) return VN_ERR_FAIL;
        Hash hash;
        if (!hash.create() || !dimensions(hash, static_cast<int32_t>(width), static_cast<int32_t>(height))) return VN_ERR_FAIL;
        const UINT stride = width * 4;
        const UINT rows = std::min<UINT>(height, 16);
        std::vector<uint8_t> pixels(static_cast<size_t>(stride) * rows);
        struct Clear { std::vector<uint8_t>& bytes; ~Clear() { SecureZeroMemory(bytes.data(), bytes.size()); } } clear{pixels};
        for (UINT y = 0; y < height; y += rows) {
            const UINT count = std::min(rows, height - y);
            WICRect rect{0, static_cast<INT>(y), static_cast<INT>(width), static_cast<INT>(count)};
            if (FAILED(converter->CopyPixels(&rect, stride, stride * count, pixels.data())) || !hash.append(pixels.data(), stride * count)) return VN_ERR_FAIL;
        }
        return hash.finish(output) ? VN_OK : VN_ERR_FAIL;
    } catch (...) { return VN_ERR_FAIL; }
}
