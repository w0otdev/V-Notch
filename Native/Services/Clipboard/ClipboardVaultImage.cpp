#include "vnotch_common.h"
#include "vnotch_handle_registry.h"
#include <bcrypt.h>
#pragma comment(lib, "bcrypt.lib")

namespace {
struct PlainImage {
    uint8_t* bytes = nullptr;
    size_t length = 0;
    ~PlainImage() {
        if (bytes) { SecureZeroMemory(bytes, length); VirtualFree(bytes, 0, MEM_RELEASE); }
    }
};
vnotch::HandleRegistry<PlainImage> images;
struct Algorithm {
    BCRYPT_ALG_HANDLE value = nullptr;
    ~Algorithm() { if (value) BCryptCloseAlgorithmProvider(value, 0); }
};
struct Key {
    BCRYPT_KEY_HANDLE value = nullptr;
    ~Key() { if (value) BCryptDestroyKey(value); }
};
struct Mapping {
    HANDLE handle = nullptr;
    const uint8_t* view = nullptr;
    ~Mapping() { if (view) UnmapViewOfFile(view); if (handle) CloseHandle(handle); }
};
}

// The existing envelope is nonce[12], tag[16], ciphertext. Authenticate it completely
// before exposing any plaintext. The encrypted file is mapped, never copied to the GC heap.
VN_EXPORT int32_t vn_clipboard_vault_open_image(intptr_t file_handle, const uint8_t* key, int32_t key_length,
    int64_t* out_handle, uint8_t** out_bytes, int32_t* out_length) noexcept {
    if (!key || key_length != 32 || !out_handle || !out_bytes || !out_length) return VN_ERR_INVALID_ARG;
    *out_handle = 0; *out_bytes = nullptr; *out_length = 0;
    try {
        const auto file = reinterpret_cast<HANDLE>(file_handle);
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file, &size) || size.QuadPart < 28 || size.QuadPart > INT32_MAX) return VN_ERR_FAIL;
        Mapping mapping;
        mapping.handle = CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr);
        if (!mapping.handle) return VN_ERR_FAIL;
        mapping.view = static_cast<const uint8_t*>(MapViewOfFile(mapping.handle, FILE_MAP_READ, 0, 0, 0));
        if (!mapping.view) return VN_ERR_FAIL;
        auto image = std::make_unique<PlainImage>();
        image->length = static_cast<size_t>(size.QuadPart - 28);
        image->bytes = static_cast<uint8_t*>(VirtualAlloc(nullptr, std::max<size_t>(1, image->length), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
        if (!image->bytes) return VN_ERR_FAIL;
        Algorithm algorithm;
        Key cipher;
        if (BCryptOpenAlgorithmProvider(&algorithm.value, BCRYPT_AES_ALGORITHM, nullptr, 0) < 0 ||
            BCryptSetProperty(algorithm.value, BCRYPT_CHAINING_MODE, reinterpret_cast<PUCHAR>(const_cast<wchar_t*>(BCRYPT_CHAIN_MODE_GCM)), sizeof(BCRYPT_CHAIN_MODE_GCM), 0) < 0 ||
            BCryptGenerateSymmetricKey(algorithm.value, &cipher.value, nullptr, 0, const_cast<PUCHAR>(key), key_length, 0) < 0) return VN_ERR_FAIL;
        BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO auth;
        BCRYPT_INIT_AUTH_MODE_INFO(auth);
        auth.pbNonce = const_cast<PUCHAR>(mapping.view); auth.cbNonce = 12;
        auth.pbTag = const_cast<PUCHAR>(mapping.view + 12); auth.cbTag = 16;
        ULONG written = 0;
        if (BCryptDecrypt(cipher.value, const_cast<PUCHAR>(mapping.view + 28), static_cast<ULONG>(image->length), &auth,
            nullptr, 0, image->bytes, static_cast<ULONG>(image->length), &written, 0) < 0 || written != image->length) return VN_ERR_FAIL;
        const auto bytes = image->bytes;
        const auto length = static_cast<int32_t>(image->length);
        *out_handle = images.Add(std::move(image));
        *out_bytes = bytes; *out_length = length;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_clipboard_vault_close_image(int64_t handle) noexcept {
    return images.Remove(handle) ? VN_OK : VN_ERR_INVALID_ARG;
}
