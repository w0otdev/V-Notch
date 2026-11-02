#include "../vnotch_native.h"
#include "../vnotch_cpu.h"
#include "../vnotch_simd_kernels.h"
#include <array>

VN_EXPORT int32_t __stdcall vn_smartcrop_preprocess_bgra32(
    const uint8_t* pixels, int32_t length, int32_t width, int32_t height, int32_t stride,
    float* tensor, int32_t capacity, float* out_scale) noexcept {
    constexpr int32_t side = 416, plane = side * side;
    if (!pixels || !tensor || !out_scale || width <= 0 || height <= 0 ||
        width > INT32_MAX / 4 || stride < width * 4 ||
        static_cast<int64_t>(stride) * height > length || capacity < 3 * plane)
        return VN_ERR_INVALID_ARG;
    const float scale = std::min(static_cast<float>(side) / width, static_cast<float>(side) / height);
    const int32_t w = std::clamp(static_cast<int32_t>(width * scale), 1, side);
    const int32_t h = std::clamp(static_cast<int32_t>(height * scale), 1, side);
    std::fill_n(tensor, 3 * plane, 114.0f);
    std::array<int32_t, side> x0{}, x1{};
    std::array<float, side> weights{};
    for (int32_t x = 0; x < w; ++x) {
        const double sx = std::clamp((x + 0.5) * width / w - 0.5, 0.0, static_cast<double>(width - 1));
        x0[x] = static_cast<int32_t>(sx); x1[x] = std::min(x0[x] + 1, width - 1);
        weights[x] = static_cast<float>(sx - x0[x]);
    }
    const bool avx = vnotch::cpu::SupportsX64V3();
    for (int32_t y = 0; y < h; ++y) {
        const double sy = std::clamp((y + 0.5) * height / h - 0.5, 0.0, static_cast<double>(height - 1));
        const int32_t y0 = static_cast<int32_t>(sy), y1 = std::min(y0 + 1, height - 1);
        const float wy = static_cast<float>(sy - y0);
        const auto* top = pixels + static_cast<size_t>(y0) * stride;
        const auto* bottom = pixels + static_cast<size_t>(y1) * stride;
        float* b = tensor + y * side; float* g = b + plane; float* r = g + plane;
        int32_t x = avx ? vnotch::kernels::avx2::SmartCropResizeRow(top, bottom,
            x0.data(), x1.data(), weights.data(), wy, w, b, g, r) : 0;
        for (; x < w; ++x) {
            for (int32_t c = 0; c < 3; ++c) {
                const float a = top[x0[x] * 4 + c], d = bottom[x0[x] * 4 + c];
                const float upper = a + (top[x1[x] * 4 + c] - a) * weights[x];
                const float lower = d + (bottom[x1[x] * 4 + c] - d) * weights[x];
                b[c * plane + x] = upper + (lower - upper) * wy;
            }
        }
    }
    *out_scale = scale;
    return VN_OK;
}
