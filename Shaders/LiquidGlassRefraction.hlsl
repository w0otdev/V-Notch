
sampler2D input : register(s0);

float srcW         : register(c0);
float srcH         : register(c1);
float notchW       : register(c2);  // notch width in source pixels
float notchH       : register(c3);  // notch height in source pixels
float offX         : register(c4);  // source pixel at the notch's left edge
float offY         : register(c5);  // source pixel at the notch's top edge
float bottomCornerR: register(c6);  // lower corner radius in source pixels
float zR           : register(c7);  // width of the optical rim in source pixels
float uRefr        : register(c8);
float uChroma      : register(c9);
float uDistort     : register(c10);
float bevelMode    : register(c11); // >= 0.5 broadens the lens profile
float satFactor    : register(c12); // 1 + saturation
float brightAdd    : register(c13); // brightness in 0..1 colour space
float topCornerR   : register(c14);  // upper corner radius in source pixels
float edgeBend     : register(c15);  // independent outer-rim bend multiplier

float smoother01(float x)
{
    x = saturate(x);
    return x * x * x * (x * (x * 6.0 - 15.0) + 10.0);
}

float lensProfile(float t)
{
    float s = smoother01(t);
    // Peak the optical slope near the outer edge, then settle quickly into a
    // stable centre. 256/27 normalises s*(1-s)^3 to one at s=1/4.
    float inv = 1.0 - s;
    return (256.0 / 27.0) * s * inv * inv * inv;
}

float roundedRectSdf(float px, float py, float bx, float by, float topR, float bottomR)
{
    float r = py < 0.0 ? topR : bottomR;
    float qx = abs(px) - bx + r;
    float qy = abs(py) - by + r;
    float2 outside = max(float2(qx, qy), 0.0);
    return length(outside) + min(max(qx, qy), 0.0) - r;
}

float hashGrid(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
}

float valueNoise(float2 p)
{
    float2 cell = floor(p);
    float2 f = frac(p);
    f = float2(smoother01(f.x), smoother01(f.y));

    float n00 = hashGrid(cell);
    float n10 = hashGrid(cell + float2(1.0, 0.0));
    float n01 = hashGrid(cell + float2(0.0, 1.0));
    float n11 = hashGrid(cell + float2(1.0, 1.0));
    return lerp(lerp(n00, n10, f.x), lerp(n01, n11, f.x), f.y);
}

float refractionAmplitude(float rimWidth, float refraction, float mode)
{
    float r = max(refraction, 0.0);
    float response = r / max(0.65 + 0.35 * r, 0.001);
    return rimWidth * 0.58 * response * (mode >= 0.5 ? 1.08 : 1.0);
}

float3 filteredSample(float2 sourcePixel, float filterMix)
{
    float2 uv = saturate(sourcePixel / float2(srcW, srcH));
    float3 center = tex2D(input, uv).rgb;
    if (filterMix <= 0.001)
        return center;

    // A compact cross footprint is only mixed in where the source mapping changes
    // by more than roughly one pixel per output pixel. It suppresses stair-stepping
    // on text and grid lines without softening the flat centre of the material.
    float radius = 0.60 + filterMix * 0.65;
    float2 dx = float2(radius / srcW, 0.0);
    float2 dy = float2(0.0, radius / srcH);
    float3 cross = center * 0.5;
    cross += tex2D(input, saturate(uv + dx)).rgb * 0.125;
    cross += tex2D(input, saturate(uv - dx)).rgb * 0.125;
    cross += tex2D(input, saturate(uv + dy)).rgb * 0.125;
    cross += tex2D(input, saturate(uv - dy)).rgb * 0.125;
    return lerp(center, cross, filterMix);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float halfX = notchW * 0.5;
    float halfY = notchH * 0.5;

    // The input contains an overscanned desktop region, while the effect output is
    // only the visible notch. Map output UV through notch pixels into that source
    // region so the side-cap lens can safely sample beyond both rounded edges.
    float npx = uv.x * srcW;
    float npy = uv.y * srcH;
    float lx = npx - (notchW - 1.0) * 0.5;
    float ly = npy - (notchH - 1.0) * 0.5;
    float2 basePixel = float2(npx + offX, npy + offY);

    float inside = -roundedRectSdf(lx, ly, halfX, halfY, topCornerR, bottomCornerR);
    float2 displacement = 0.0;
    float2 chromaOffset = 0.0;
    float mappingRate = 0.0;

    if (inside > 0.0)
    {
        const float normalStep = 0.75;
        float dR = -roundedRectSdf(lx + normalStep, ly, halfX, halfY, topCornerR, bottomCornerR);
        float dL = -roundedRectSdf(lx - normalStep, ly, halfX, halfY, topCornerR, bottomCornerR);
        float dD = -roundedRectSdf(lx, ly + normalStep, halfX, halfY, topCornerR, bottomCornerR);
        float dU = -roundedRectSdf(lx, ly - normalStep, halfX, halfY, topCornerR, bottomCornerR);
        float2 inwardNormal = float2(dR - dL, dD - dU) / (2.0 * normalStep);
        float normalLength = length(inwardNormal);
        inwardNormal = normalLength > 0.0001 ? inwardNormal / normalLength : 0.0;

        // Extreme mode: do not flatten values above the former 300% ceiling.
        float bend = max(edgeBend, 0.0);
        float sideAxis = pow(saturate(abs(inwardNormal.x)), 2.6);
        float directionalZR = zR * (1.0 + 0.5 * bend * sideAxis);
        float t = saturate(inside / max(directionalZR, 0.001));
        float profile = lensProfile(t);
        if (bevelMode >= 0.5)
            profile = sqrt(profile); // wider, still C1-flat at both ends
        else
        {
            // Broaden the readable shoulder of the fold without disturbing the
            // flat centre. This keeps normal mode visibly refractive rather than
            // looking like a thin highlight painted on the border.
            float shoulder = saturate((bend - 0.8) / 2.2) * 0.28;
            profile = lerp(profile, sqrt(max(profile, 0.0)), shoulder);
        }

        // Deliberately uncapped. Large Edge Bend values may pull samples beyond
        // the optical rim, producing the pronounced fold requested by the user.
        float amplitude =
            refractionAmplitude(zR, uRefr, bevelMode) * pow(bend, 1.5);
        float aspect = saturate((notchH / max(notchW, 1.0)) * 2.5);
        float verticalBalance = lerp(0.68, 1.0, aspect);
        displacement = -inwardNormal * float2(1.0, verticalBalance) * amplitude * profile;

        if (uDistort > 0.0001)
        {
            // Continuous low-frequency value noise reads as a subtle material
            // variation; the old per-pixel hash appeared as grain and jagged edges.
            float2 noisePoint = float2(lx, ly) * 0.045;
            float2 fluid = float2(
                valueNoise(noisePoint),
                valueNoise(noisePoint + float2(19.7, 43.1))) * 2.0 - 1.0;
            displacement += fluid * float2(1.0, verticalBalance) * (uDistort * 2.25 * profile);
        }

        float chromaStrength = min(
            uChroma * (1.25 + zR * 0.085) * pow(max(profile, 0.0), 0.72),
            8.0);
        chromaOffset = inwardNormal * float2(1.0, verticalBalance) * chromaStrength;

        // Estimate the local change in displacement over one source pixel. This
        // drives the adaptive footprint used only in the high-curvature band.
        float dt = 1.0 / max(directionalZR, 1.0);
        float p0 = lensProfile(saturate(t - dt));
        float p1 = lensProfile(saturate(t + dt));
        if (bevelMode >= 0.5)
        {
            p0 = sqrt(p0);
            p1 = sqrt(p1);
        }
        mappingRate = abs(amplitude * (p1 - p0) * 0.5);
    }

    // Invalid/stale geometry must degrade to a 1:1 backdrop rather than magnifying
    // a tiny source slice across the entire pill.
    float geometryValid = step(1.0, srcW) * step(1.0, srcH)
        * step(1.0, notchW) * step(1.0, notchH);
    float2 sourcePixel = basePixel + displacement * geometryValid;
    float filterMix = saturate((mappingRate - 0.28) * 0.9) * geometryValid;
    float3 col = filteredSample(sourcePixel, filterMix);

    chromaOffset *= geometryValid;
    if (abs(chromaOffset.x) + abs(chromaOffset.y) > 0.0001)
    {
        col.r = tex2D(input, saturate((sourcePixel + chromaOffset) / float2(srcW, srcH))).r;
        col.b = tex2D(input, saturate((sourcePixel - chromaOffset) / float2(srcW, srcH))).b;
    }

    float lum = dot(col, float3(0.299, 0.587, 0.114));
    col = saturate(lum + (col - lum) * satFactor + brightAdd);
    return float4(col, 1.0);
}
