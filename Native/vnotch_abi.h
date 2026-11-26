#pragma once
#include <cstdint>

#define VN_EXPORT extern "C" __declspec(dllexport)

// Status-returning exports only. HRESULT/value-returning exports keep their own contract.
constexpr int32_t VN_OK = 0;
constexpr int32_t VN_ERR_INVALID_ARG = -1;
constexpr int32_t VN_ERR_BUFFER_TOO_SMALL = -2;
constexpr int32_t VN_ERR_FAIL = -3;
constexpr int32_t VN_ERR_LIMIT_EXCEEDED = -4;

// v1 freezes existing signatures, packing, status values and handle ownership.
// Additive exports increment minor; incompatible changes require a new major.
#pragma pack(push, 1)
struct VnAbiInfo {
    uint32_t size;
    uint32_t major;
    uint32_t minor;
    uint32_t pointer_size;
    uint32_t registry_handle_version;
    uint32_t status_version;
};
#pragma pack(pop)
static_assert(sizeof(VnAbiInfo) == 24);
VN_EXPORT int32_t __stdcall vn_abi_query(uint32_t capacity, VnAbiInfo* info) noexcept;
