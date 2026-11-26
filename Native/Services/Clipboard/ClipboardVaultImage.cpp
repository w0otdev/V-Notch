#include "vnotch_common.h"
#include "vnotch_handle_registry.h"
#include <bcrypt.h>
#include <vector>
#pragma comment(lib, "bcrypt.lib")

namespace {
struct PlainImage {
    uint8_t* bytes = nullptr;
    size_t length = 0;
    ~PlainImage() {
        if (bytes) { SecureZeroMemory(bytes, length); VirtualFree(bytes, 0, MEM_RELEASE); }
    }
};
vnotch::ImmortalHandleRegistry<PlainImage> images;
struct Algorithm {
    BCRYPT_ALG_HANDLE value = nullptr;
    ~Algorithm() { if (value) BCryptCloseAlgorithmProvider(value, 0); }
};
struct Key {
    BCRYPT_KEY_HANDLE value = nullptr;
    ~Key() { if (value) BCryptDestroyKey(value); }
};
}

// The existing envelope is nonce[12], tag[16], ciphertext. Authenticate it completely
// before exposing any plaintext. ReadFile reports storage errors before decryption.
VN_EXPORT int32_t vn_clipboard_vault_load_image(intptr_t file_handle, const uint8_t* key, int32_t key_length,
    int64_t* out_handle, int32_t* out_length) noexcept {
    if (!key || key_length != 32 || !out_handle || !out_length) return VN_ERR_INVALID_ARG;
    *out_handle = 0; *out_length = 0;
    try {
        const auto file = reinterpret_cast<HANDLE>(file_handle);
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file, &size) || size.QuadPart < 28 || size.QuadPart > INT32_MAX) return VN_ERR_FAIL;
        std::vector<uint8_t> encrypted(static_cast<size_t>(size.QuadPart));
        DWORD received = 0;
        if (!ReadFile(file, encrypted.data(), static_cast<DWORD>(encrypted.size()), &received, nullptr) ||
            received != encrypted.size()) return VN_ERR_FAIL;
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
        auth.pbNonce = encrypted.data(); auth.cbNonce = 12;
        auth.pbTag = encrypted.data() + 12; auth.cbTag = 16;
        ULONG written = 0;
        if (BCryptDecrypt(cipher.value, encrypted.data() + 28, static_cast<ULONG>(image->length), &auth,
            nullptr, 0, image->bytes, static_cast<ULONG>(image->length), &written, 0) < 0 || written != image->length) return VN_ERR_FAIL;
        const auto length = static_cast<int32_t>(image->length);
        *out_handle = images.Add(std::move(image));
        *out_length = length;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_clipboard_vault_read_image(int64_t handle, int32_t offset, uint8_t* output, int32_t count) noexcept {
    auto image = images.Acquire(handle);
    if (!image || offset < 0 || count < 0 || (count && !output) ||
        static_cast<uint64_t>(offset) + count > image->length) return VN_ERR_INVALID_ARG;
    if (count) std::memcpy(output, image->bytes + offset, count);
    return VN_OK;
}

VN_EXPORT int32_t vn_clipboard_vault_close_image(int64_t handle) noexcept {
    return images.Remove(handle) ? VN_OK : VN_ERR_INVALID_ARG;
}
