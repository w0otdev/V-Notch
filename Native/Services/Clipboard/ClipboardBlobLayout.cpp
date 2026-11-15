#include "Parsing/Utf16Text.h"
#include "Parsing/Utf16Json.h"

using namespace vnotch::parsing;

VN_EXPORT int32_t __stdcall vn_clipboard_blob_build_nonce_aad(
    const uint8_t* stream_id_ptr, int32_t stream_id_len,
    uint64_t chunk_index, int32_t plain_len, uint8_t is_final,
    uint8_t* out_nonce, int32_t nonce_cap,
    uint8_t* out_aad, int32_t aad_cap) noexcept
{
    if (stream_id_ptr == nullptr || stream_id_len < 12 ||
        out_nonce == nullptr || nonce_cap < 12 ||
        out_aad == nullptr || aad_cap < 33)
    {
        return VN_ERR_INVALID_ARG;
    }

    std::memcpy(out_nonce, stream_id_ptr, 12);
    uint64_t low = 0;
    std::memcpy(&low, out_nonce + 4, sizeof(uint64_t));
    low ^= chunk_index;
    std::memcpy(out_nonce + 4, &low, sizeof(uint64_t));

    static constexpr uint8_t kMagic[8] = {'V', 'N', 'P', 'B', 'L', 'O', 'B', '1'};
    std::memcpy(out_aad, kMagic, 8);
    std::memcpy(out_aad + 8, stream_id_ptr, 12);
    std::memcpy(out_aad + 20, &chunk_index, sizeof(uint64_t));
    std::memcpy(out_aad + 28, &plain_len, sizeof(int32_t));
    out_aad[32] = is_final != 0 ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_blob_compute_geometry(
    int64_t file_len,
    int64_t* out_total_chunks,
    int32_t* out_last_plain_len,
    int64_t* out_plain_length,
    int32_t* out_error_kind) noexcept
{
    if (out_total_chunks == nullptr || out_last_plain_len == nullptr ||
        out_plain_length == nullptr || out_error_kind == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    constexpr int64_t kHeaderSize = 20;
    constexpr int64_t kChunkSize = 64 * 1024;
    constexpr int64_t kTagSize = 16;
    constexpr int64_t kDiskChunkSize = kChunkSize + kTagSize;

    *out_total_chunks = 0;
    *out_last_plain_len = 0;
    *out_plain_length = 0;
    *out_error_kind = 0;

    if (file_len < kHeaderSize + kTagSize)
    {
        *out_error_kind = 1; // Truncated
        return VN_OK;
    }

    const int64_t body_len = file_len - kHeaderSize;
    if (body_len == kTagSize)
    {
        *out_total_chunks = 1;
        *out_last_plain_len = 0;
        *out_plain_length = 0;
        return VN_OK;
    }

    const int64_t rem = body_len % kDiskChunkSize;
    if (rem > 0 && rem <= kTagSize)
    {
        *out_error_kind = 2; // Corrupt length
        return VN_OK;
    }

    if (rem == 0)
    {
        *out_total_chunks = body_len / kDiskChunkSize;
        *out_last_plain_len = static_cast<int32_t>(kChunkSize);
    }
    else
    {
        *out_total_chunks = body_len / kDiskChunkSize + 1;
        *out_last_plain_len = static_cast<int32_t>(rem - kTagSize);
    }

    *out_plain_length = (*out_total_chunks - 1) * kChunkSize + *out_last_plain_len;
    return VN_OK;
}
