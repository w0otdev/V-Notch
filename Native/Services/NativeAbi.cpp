#include "../vnotch_abi.h"

VN_EXPORT int32_t vn_abi_query(uint32_t capacity, VnAbiInfo* info) noexcept {
    if (!info) return VN_ERR_INVALID_ARG;
    if (capacity < sizeof(VnAbiInfo)) return VN_ERR_BUFFER_TOO_SMALL;
    *info = {sizeof(VnAbiInfo), 2, 0, sizeof(void*), 1, 1};
    return VN_OK;
}
