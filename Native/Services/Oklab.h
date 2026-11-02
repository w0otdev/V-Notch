#pragma once
#include <array>
#include <cmath>
#include <cstdint>

namespace vnotch::color {
struct Oklab { float l, a, b; };
inline const std::array<float, 256>& LinearSrgb() noexcept {
    static const auto table = [] {
        std::array<float, 256> values{};
        for (size_t i = 0; i < values.size(); ++i) {
            const float v = static_cast<float>(i) / 255;
            values[i] = v <= 0.04045f ? v / 12.92f : std::pow((v + 0.055f) / 1.055f, 2.4f);
        }
        return values;
    }();
    return table;
}
inline Oklab FromBgra(const uint8_t* pixel) noexcept {
    const auto& linear = LinearSrgb();
    const float r = linear[pixel[2]], g = linear[pixel[1]], b = linear[pixel[0]];
    const float l = std::cbrt(0.4122214708f*r + 0.5363325363f*g + 0.0514459929f*b);
    const float m = std::cbrt(0.2119034982f*r + 0.6806995451f*g + 0.1073969566f*b);
    const float s = std::cbrt(0.0883024619f*r + 0.2817188376f*g + 0.6299787005f*b);
    return {0.2104542553f*l + 0.7936177850f*m - 0.0040720468f*s,
        1.9779984951f*l - 2.4285922050f*m + 0.4505937099f*s,
        0.0259040371f*l + 0.7827717662f*m - 0.8086757660f*s};
}
}
