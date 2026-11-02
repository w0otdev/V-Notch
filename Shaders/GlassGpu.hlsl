// D3D11 capture crop, rotation correction and Dual-Kawase passes.
// The refraction body is appended from LiquidGlassRefraction.hlsl at build time.
Texture2D sourceTexture : register(t0);
Texture2D detailTexture : register(t1);
SamplerState linearSampler : register(s0);
cbuffer GlassConstants : register(b0)
{
    float4 lens[32];
    float4 cropRect;       // monitor-local physical pixels
    float4 desktopInfo;    // upright monitor width, height, DXGI rotation, unused
    float4 passInfo;       // inverse active input width/height, Kawase offset, blend
    float4 sourceRegion;   // active/capacity ratio, half texel in capacity space
    float4 detailRegion;
};
float4 sampleSource(float2 uv) { return sourceTexture.Sample(linearSampler, clamp(uv * sourceRegion.xy, sourceRegion.zw, sourceRegion.xy - sourceRegion.zw)); }
float4 sampleDetail(float2 uv) { return detailTexture.Sample(linearSampler, clamp(uv * detailRegion.xy, detailRegion.zw, detailRegion.xy - detailRegion.zw)); }
struct VertexOutput { float4 position : SV_Position; float2 uv : TEXCOORD0; };
float3 srgbToLinear(float3 c) { return lerp(c / 12.92, pow((c + 0.055) / 1.055, 2.4), step(0.04045, c)); }
float3 linearToSrgb(float3 c) { return lerp(c * 12.92, 1.055 * pow(max(c, 0), 1.0 / 2.4) - 0.055, step(0.0031308, c)); }
VertexOutput fullscreenVs(uint id : SV_VertexID)
{
    VertexOutput o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.position = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
float4 cropPs(VertexOutput i) : SV_Target
{
    // Clamp in upright desktop space before undoing the output rotation.
    float2 p = clamp(cropRect.xy + i.uv * cropRect.zw, 0.5, desktopInfo.xy - 0.5);
    float2 uv = p / desktopInfo.xy;
    if (desktopInfo.z == 2) uv = float2(uv.y, 1 - uv.x);
    else if (desktopInfo.z == 3) uv = 1 - uv;
    else if (desktopInfo.z == 4) uv = float2(1 - uv.y, uv.x);
    return float4(srgbToLinear(sampleSource(uv).rgb), 1);
}
float4 downPs(VertexOutput i) : SV_Target
{
    float2 d = passInfo.xy * passInfo.z;
    float4 s = sampleSource(i.uv) * 4;
    s += sampleSource(i.uv + float2(-d.x, -d.y));
    s += sampleSource(i.uv + float2( d.x, -d.y));
    s += sampleSource(i.uv + float2(-d.x,  d.y));
    s += sampleSource(i.uv + d);
    return s / 8;
}
float4 upPs(VertexOutput i) : SV_Target
{
    float2 d = passInfo.xy * passInfo.z;
    float4 s = sampleSource(i.uv + float2(-2*d.x, 0));
    s += sampleSource(i.uv + float2(2*d.x, 0));
    s += sampleSource(i.uv + float2(0, -2*d.y));
    s += sampleSource(i.uv + float2(0, 2*d.y));
    s += sampleSource(i.uv + float2(-d.x, -d.y)) * 2;
    s += sampleSource(i.uv + float2( d.x, -d.y)) * 2;
    s += sampleSource(i.uv + float2(-d.x,  d.y)) * 2;
    s += sampleSource(i.uv + d) * 2;
    // Blend the last octave with its finer source before reconstructing. This
    // keeps the blur continuous across powers of two instead of popping levels.
    return lerp(sampleDetail(i.uv), s / 12, passInfo.w);
}
float4 resolvePs(VertexOutput i) : SV_Target
{
    float3 c = linearToSrgb(sampleSource(i.uv).rgb);
    // Stable sub-LSB dither masks quantization in smooth, dark gradients.
    float dither = (frac(52.9829189 * frac(dot(i.position.xy + cropRect.xy, float2(0.06711056, 0.00583715)))) - 0.5) / 255;
    return float4(saturate(c + dither), 1);
}
float4 copyPs(VertexOutput i) : SV_Target { return sampleSource(i.uv); }
