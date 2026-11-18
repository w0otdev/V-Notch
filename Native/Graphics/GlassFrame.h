#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <d3d11.h>
#include <wrl/client.h>
namespace vnotch::graphics {
using Microsoft::WRL::ComPtr;
struct Frame {
    int32_t x, y, width, height;
    float blur;
    int32_t refract;
    float lens[32][4];
};
struct Constants {
    float lens[32][4]{};
    float crop[4]{};
    float desktop[4]{};
    float pass[4]{};
    float sourceRegion[4]{}, detailRegion[4]{};
};
// Factor the original exponential rim law into draw constants. Lens travel
// still depends on the actual distance to the edge at each pixel.
inline void PrepareLensConstants(float lens[][4]) noexcept {
    lens[0][1] = 1.f / std::max(lens[0][0], 1.f);
    lens[1][1] = 1.f / std::max(lens[1][0], 1.f);
    const float half = 0.5f * std::min(lens[2][0], lens[3][0]);
    const float rim = std::min(half, lens[3][1] > 0.f ? lens[3][1] : 24.f);
    const float bevel = std::clamp(lens[31][0], 0.f, 1.f);
    const float b = std::max(lens[10][0], 0.f);
    const float base = std::max(lens[11][0], 0.1f) * 2.71828183f;
    const float rate = std::max(lens[12][0], 0.1f) * (1.f - 0.42f * bevel);
    lens[6][1] = rim;
    lens[6][2] = 1.f / std::max(rim, 1.f);
    lens[10][1] = b * std::pow(base, -std::max(lens[9][0], 0.f));
    lens[12][1] = rate * std::log2(base);
    lens[12][2] = 1.38629436f * lens[12][1]; // derivative of exp2(-depth² * decay)
    lens[15][1] = (0.16f + 0.20f * bevel) * std::min(b * 0.45f, 1.f);
    lens[30][1] = rim * 1.5f * std::max(lens[30][0], 0.f);
    const float specular = std::clamp(lens[15][0], 0.f, 2.5f);
    lens[16][1] = 0.72f * std::clamp(lens[16][0], 0.f, 2.5f) + 0.42f * specular;
    lens[16][2] = std::sqrt(lens[16][1]);
    lens[15][2] = 0.14f + 0.35f * specular;
    lens[26][1] = std::max(lens[6][0] * 0.85f, 18.f + 10.f * bevel);
    lens[26][2] = 1.f / lens[26][1];
    lens[26][3] = std::max({lens[6][0], lens[7][0], rim, lens[26][1]});
    lens[31][1] = 1.8f + 1.6f * bevel;
    lens[31][2] = 1.f / (0.2f - lens[31][1]);
    // Pointer and light coordinate transforms are also constant over the draw.
    const float w = std::max(lens[2][0], 1.f), h = std::max(lens[3][0], 1.f);
    lens[2][2] = -2.f / w;
    lens[3][2] = -2.f / h;
    if (lens[24][0] * lens[25][0] > 0.001f) {
        const float radius = std::max(h * 0.70f, 1.f);
        lens[22][1] = (std::clamp(lens[22][0], 0.f, 1.f) - 0.5f) * w;
        lens[23][1] = (std::clamp(lens[23][0], 0.f, 1.f) - 0.5f) * h;
        lens[22][2] = 1.f / std::max(w * 0.35f, radius * 2.f);
        lens[23][2] = 1.f / std::max(h * 0.75f, radius * 1.5f);
        const float press = std::clamp(lens[25][0], 0.f, 1.f) * std::clamp(lens[24][0], 0.f, 1.f);
        lens[25][1] = press * 4.71238898f;
        lens[25][2] = std::max(lens[27][0], 0.f) * (0.5f + 3.f * press);
        lens[25][3] = 0.5f + 0.5f * press;
    }
    lens[28][1] = (lens[28][0] - 0.5f) * w;
    lens[29][1] = (lens[29][0] - 0.5f) * h;
    lens[28][2] = 1.f / std::max(w * 0.72f, 1.f);
    lens[29][2] = 1.f / std::max(h * 0.95f, 1.f);
}
struct TextureInput {
    ID3D11ShaderResourceView* view = nullptr;
    UINT width = 1, height = 1, activeWidth = 1, activeHeight = 1;
};
struct Target {
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11ShaderResourceView> srv;
    ComPtr<ID3D11RenderTargetView> rtv;
    UINT width = 0, height = 0;
    TextureInput Input(UINT w, UINT h) const { return { srv.Get(), width, height, w, h }; }
};
static_assert(sizeof(Frame) == 536);
static_assert(sizeof(Constants) == 592);
}
