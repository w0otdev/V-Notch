#pragma once
#include "../vnotch_native.h"

namespace vnotch::services {
#pragma pack(push, 1)
struct VnDetection {
    float x1, y1, x2, y2, confidence;
    int32_t class_id;
};
#pragma pack(pop)
}

VN_EXPORT int32_t __stdcall vn_smartcrop_preprocess_bgra32(const uint8_t*, int32_t, int32_t, int32_t,
    int32_t, float*, int32_t, float*) noexcept;
