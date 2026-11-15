#pragma once
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
