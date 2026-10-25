#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services::data_protection
{
        static int32_t protect_bytes(const uint8_t* plain_ptr,
    int32_t plain_len,
    uint8_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
        {
    if (out_len == nullptr || plain_len < 0 || (plain_len > 0 && plain_ptr == nullptr))
    {
        return VN_ERR_INVALID_ARG;
    }
    uint8_t dummy = 0;
    DATA_BLOB in_blob{};
    in_blob.cbData = static_cast<DWORD>(plain_len);
    in_blob.pbData = const_cast<BYTE*>(plain_len > 0 ? plain_ptr : &dummy);

    DATA_BLOB out_blob{};
    if (!CryptProtectData(&in_blob, nullptr, nullptr, nullptr, nullptr, 0, &out_blob))
    {
        *out_len = 0;
        return VN_ERR_FAIL;
    }

    struct BlobGuard {
        DATA_BLOB* blob;
        ~BlobGuard() noexcept {
            if (blob->pbData != nullptr) {
                LocalFree(blob->pbData);
            }
        }
    } guard{&out_blob};

    int32_t required = static_cast<int32_t>(out_blob.cbData);
    *out_len = required;
    if (out_buf == nullptr || out_cap < required)
    {
        return VN_ERR_BUFFER_TOO_SMALL;
    }
    if (required > 0 && out_blob.pbData != nullptr)
    {
        std::memcpy(out_buf, out_blob.pbData, static_cast<size_t>(required));
    }
    return VN_OK;
}

        static int32_t unprotect_bytes(const uint8_t* enc_ptr,
    int32_t enc_len,
    uint8_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
        {
    if (out_len == nullptr || enc_len <= 0 || enc_ptr == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    DATA_BLOB in_blob{};
    in_blob.cbData = static_cast<DWORD>(enc_len);
    in_blob.pbData = const_cast<BYTE*>(enc_ptr);

    DATA_BLOB out_blob{};
    if (!CryptUnprotectData(&in_blob, nullptr, nullptr, nullptr, nullptr, 0, &out_blob))
    {
        *out_len = 0;
        return VN_ERR_FAIL;
    }

    struct SecureBlobGuard {
        DATA_BLOB* blob;
        ~SecureBlobGuard() noexcept {
            if (blob->pbData != nullptr) {
                SecureZeroMemory(blob->pbData, blob->cbData);
                LocalFree(blob->pbData);
            }
        }
    } guard{&out_blob};

    int32_t required = static_cast<int32_t>(out_blob.cbData);
    *out_len = required;
    if (out_buf == nullptr || out_cap < required)
    {
        return VN_ERR_BUFFER_TOO_SMALL;
    }
    if (required > 0 && out_blob.pbData != nullptr)
    {
        std::memcpy(out_buf, out_blob.pbData, static_cast<size_t>(required));
    }
    return VN_OK;
}
} // namespace vnotch::services::data_protection

VN_EXPORT int32_t vn_dpapi_protect_bytes(const uint8_t* plain_ptr,
    int32_t plain_len,
    uint8_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    return vnotch::services::data_protection::protect_bytes(plain_ptr, plain_len, out_buf, out_cap, out_len);
}

VN_EXPORT int32_t vn_dpapi_unprotect_bytes(const uint8_t* enc_ptr,
    int32_t enc_len,
    uint8_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    return vnotch::services::data_protection::unprotect_bytes(enc_ptr, enc_len, out_buf, out_cap, out_len);
}

