sampler2D input : register(s0);

// Geometry & Dimensions
float srcW               : register(c0);
float srcH               : register(c1);
float notchW             : register(c2);
float notchH             : register(c3);
float offX               : register(c4);
float offY               : register(c5);
float bottomCornerR      : register(c6);
float topCornerR         : register(c7);

// OverShifted LiquidGlass Core Parameters
float powerFactor        : register(c8);  // u_powerFactor (squircle power, e.g. 3.0)
float u_a                : register(c9);  // exponential offset a (0.7)
float u_b                : register(c10); // exponential amplitude b (2.3)
float u_c                : register(c11); // base scale c (5.2)
float u_d                : register(c12); // exponential rate d (6.9)
float u_fPower           : register(c13); // refraction power (1.0..3.0)
float u_noise            : register(c14); // film noise grain (0.05..0.15)
float u_glowWeight       : register(c15); // directional specular glow weight (0.25)
float u_glowBias         : register(c16); // glow bias (0.0)
float u_glowEdge0        : register(c17); // glow edge start (0.15)
float u_glowEdge1        : register(c18); // glow edge end (0.0)

// Material & Optical Appearance
float u_chroma           : register(c19); // chromatic aberration amount
float satFactor          : register(c20); // saturation multiplier (1.0 = normal)
float brightAdd          : register(c21); // brightness offset (0.0 = normal)

// Pointer & Lighting Interaction
float pointerX           : register(c22); // pointer X in notch (0..1)
float pointerY           : register(c23); // pointer Y in notch (0..1)
float pointerActive      : register(c24); // hover/touch active: 0..1
float pressAmount        : register(c25); // pressed state: 0..1
float highlightStrength  : register(c26); // interactive highlight multiplier
float flexStrength       : register(c27); // touch displacement flex in pixels
float lightX             : register(c28); // light source X
float lightY             : register(c29); // light source Y
float edgeBend           : register(c30); // edge refraction intensity multiplier
float bevelMode          : register(c31); // 0 = standard continuous lens, 1 = broad bevel

static const float M_E = 2.718281828459045;
static const float M_PI = 3.141592653589793;

float2 safeNormalize(float2 v)
{
    float lenSq = dot(v, v);
    if (lenSq < 0.000001)
        return float2(0.0, -1.0);
    return v * rsqrt(lenSq);
}

float pow4(float x)
{
    float x2 = x * x;
    return x2 * x2;
}

float smoother01(float x)
{
    x = saturate(x);
    return x * x * x * (x * (x * 6.0 - 15.0) + 10.0);
}

float luminance(float3 col)
{
    return dot(col, float3(0.299, 0.587, 0.114));
}

// Pseudo-random noise for physical glass grain
float rand(float2 co)
{
    return frac(sin(dot(co, float2(12.9898, 78.233))) * 43758.5453);
}

// OverShifted Superellipse (Squircle) Signed Distance Field
float sdSuperellipse(float2 p, float n, float r)
{
    float2 p_abs = abs(p);
    float numerator = pow(max(p_abs.x, 0.00001), n) + pow(max(p_abs.y, 0.00001), n) - pow(r, n);
    float den_x = pow(max(p_abs.x, 0.00001), 2.0 * n - 2.0);
    float den_y = pow(max(p_abs.y, 0.00001), 2.0 * n - 2.0);
    float denominator = n * sqrt(den_x + den_y) + 0.00001;
    return numerator / denominator;
}

// OverShifted Exponential Refraction Lens Equation: f(x) = 1.0 - b * (c * e)^(-d * x - a)
float f_refract(float x, float a, float b, float c, float d)
{
    float exponent = -d * x - a;
    float baseVal = max(c * M_E, 0.0001);
    return 1.0 - b * pow(baseVal, exponent);
}

// Directional Glass Glow
float directionalGlow(float2 p)
{
    return sin(atan2(p.y, p.x) - 0.5);
}

// Unified Notch Field: Returns (insideDistPixels, inwardNormal.x, inwardNormal.y)
float3 notchDistanceField(
    float2 localPos,
    float2 halfSize,
    float topRadius,
    float bottomRadius,
    float nPower)
{
    float px = abs(localPos.x);
    float py = localPos.y;

    float maxRadius = min(halfSize.x, halfSize.y);
    topRadius = clamp(topRadius, 0.0, maxRadius);
    bottomRadius = clamp(bottomRadius, 0.0, maxRadius);

    float centerYTop = -halfSize.y + topRadius;
    float centerYBottom = halfSize.y - bottomRadius;

    float sdf = 0.0;
    float2 d = float2(0.0, 0.0);

    if (py < centerYTop)
    {
        float2 q = float2(px - (halfSize.x - topRadius), py - centerYTop);
        if (q.x > 0.0)
        {
            // Top rounded quadrant: evaluate squircle curvature
            float2 qNorm = q / max(topRadius, 0.001);
            sdf = (sdSuperellipse(qNorm, nPower, 1.0)) * topRadius;
            d = q;
        }
        else
        {
            sdf = -halfSize.y - py;
            d = float2(0.0, -1.0);
        }
    }
    else if (py > centerYBottom)
    {
        float2 q = float2(px - (halfSize.x - bottomRadius), py - centerYBottom);
        if (q.x > 0.0)
        {
            // Bottom rounded quadrant: evaluate squircle curvature
            float2 qNorm = q / max(bottomRadius, 0.001);
            sdf = (sdSuperellipse(qNorm, nPower, 1.0)) * bottomRadius;
            d = q;
        }
        else
        {
            sdf = py - halfSize.y;
            d = float2(0.0, 1.0);
        }
    }
    else
    {
        sdf = px - halfSize.x;
        d = float2(1.0, 0.0);
    }

    if (sdf < 0.0)
    {
        float dSide = px - halfSize.x;
        float dTop = -halfSize.y - py;
        float dBottom = py - halfSize.y;
        float maxD = max(dSide, max(dTop, dBottom));

        if (maxD > sdf)
        {
            sdf = maxD;
            if (sdf == dSide) d = float2(1.0, 0.0);
            else if (sdf == dTop) d = float2(0.0, -1.0);
            else if (sdf == dBottom) d = float2(0.0, 1.0);
        }
    }

    float2 pSign = float2(localPos.x < 0.0 ? -1.0 : 1.0, 1.0);
    float2 outwardNormal = float2(0.0, 0.0);
    if (dot(d, d) > 0.00001)
    {
        outwardNormal = normalize(d) * pSign;
    }

    return float3(-sdf, -outwardNormal.x, -outwardNormal.y);
}

float3 sampleSource(float2 sourcePixel, float2 sourceSize)
{
    float2 sampleUv = saturate(sourcePixel / sourceSize);
    return tex2D(input, sampleUv).rgb;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 sourceSize = max(float2(srcW, srcH), float2(1.0, 1.0));
    float2 notchSize = max(float2(notchW, notchH), float2(1.0, 1.0));

    float geometryValid = step(1.0, srcW) * step(1.0, srcH) * step(1.0, notchW) * step(1.0, notchH);
    if (geometryValid < 0.5)
        return tex2D(input, saturate(uv));

    float npx = uv.x * srcW;
    float npy = uv.y * srcH;

    float2 localPos = float2(npx - notchW * 0.5, npy - notchH * 0.5);
    float2 basePixel = float2(npx + offX, npy + offY);
    float2 halfSize = max(notchSize * 0.5, float2(0.5, 0.5));

    // Clamp and sanitize OverShifted parameters
    float nSquircle = max(powerFactor, 1.05);
    float paramA = max(u_a, 0.0);
    float paramB = max(u_b, 0.0);
    float paramC = max(u_c, 0.1);
    float paramD = max(u_d, 0.1);
    float fPow   = max(u_fPower, 0.1);
    float bend   = max(edgeBend, 0.1);

    // Compute SDF and normal
    float3 field = notchDistanceField(localPos, halfSize, topCornerR, bottomCornerR, nSquircle);
    float insidePixels = field.x;
    float2 inwardNormal = field.yz;

    // Smooth anti-aliased silhouette alpha
    float alpha = saturate(insidePixels + 0.5);
    if (alpha <= 0.0)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    // Normalized coordinate relative to notch bounds
    float2 pNorm = localPos / halfSize;

    // Optical Rim & Normalized depth
    float notchRadius = min(halfSize.x, halfSize.y);
    float distNorm = saturate(insidePixels / max(notchRadius, 1.0));

    // -----------------------------------------------------------------
    // Pointer interaction & dynamic ripple
    // -----------------------------------------------------------------
    float active = saturate(pointerActive);
    float pressed = saturate(pressAmount) * active;
    float2 pointer01 = saturate(float2(pointerX, pointerY));
    float2 pointerLocal = pointer01 * notchSize - halfSize;
    float2 pointerDelta = localPos - pointerLocal;

    float radiusPixels = max(notchH * 0.70, 1.0);
    float2 interactionScale = float2(max(notchW * 0.35, radiusPixels * 2.0), max(notchH * 0.75, radiusPixels * 1.5));
    float interactionDist = length(pointerDelta / interactionScale);
    float interactionMask = smoothstep(1.0, 0.0, interactionDist) * active;

    float2 radialFromPointer = safeNormalize(pointerDelta);

    // Dynamic wave / ripple flex
    float ripplePhase = pressed * M_PI * 1.5;
    float dimpleSlope = sin(saturate(interactionDist) * M_PI * 2.0 - ripplePhase) * interactionMask;
    float flexPixels = max(flexStrength, 0.0) * dimpleSlope * lerp(0.5, 3.5, pressed);

    // Dynamic flex on inward normal
    inwardNormal = safeNormalize(inwardNormal - radialFromPointer * (interactionMask * pressed * 0.06));

    // -----------------------------------------------------------------
    // OverShifted Exponential Refraction Lens Formula
    // -----------------------------------------------------------------
    float fVal = f_refract(distNorm, paramA, paramB, paramC, paramD);
    float refractFactor = pow(max(fVal, 0.0001), fPow);

    // Displacement vector mapping background coordinates
    float2 samplePNorm = pNorm * refractFactor;
    float2 displacement = (samplePNorm - pNorm) * halfSize * bend;

    // Add pointer ripple displacement
    displacement += radialFromPointer * flexPixels;

    float2 sourcePixel = basePixel + displacement;

    // -----------------------------------------------------------------
    // Chromatic Dispersion
    // -----------------------------------------------------------------
    float3 col = float3(0.0, 0.0, 0.0);
    float chromaAmount = max(u_chroma, 0.0) * (1.0 - distNorm) * 2.0;

    if (chromaAmount > 0.001)
    {
        float2 chromaOffset = safeNormalize(displacement) * chromaAmount * 1.5;
        float3 sampleR = sampleSource(sourcePixel + chromaOffset, sourceSize);
        float3 sampleG = sampleSource(sourcePixel, sourceSize);
        float3 sampleB = sampleSource(sourcePixel - chromaOffset, sourceSize);
        col = float3(sampleR.r, sampleG.g, sampleB.b);
    }
    else
    {
        col = sampleSource(sourcePixel, sourceSize);
    }

    // -----------------------------------------------------------------
    // Film Noise / Grain
    // -----------------------------------------------------------------
    float grain = (rand(npx * 0.05 + npy * 100.0) - 0.5) * max(u_noise, 0.0);
    col += float3(grain, grain, grain);

    // -----------------------------------------------------------------
    // OverShifted Directional Glass Glow (Multiplicative Optical Rim)
    // -----------------------------------------------------------------
    float glowAngle = directionalGlow(pNorm);
    float glowMask = smoothstep(u_glowEdge0, u_glowEdge1, distNorm);
    float glowMul = glowAngle * u_glowWeight * glowMask + 1.0 + u_glowBias;
    col *= max(glowMul, 0.0);

    // -----------------------------------------------------------------
    // Apple Liquid Glass Micro-Bevel Hairline (Subtle 1px Polish Reflection)
    // -----------------------------------------------------------------
    float2 outwardNormal = -inwardNormal;
    // Ambient overhead soft top-lighting
    float topLight = saturate(-outwardNormal.y * 0.65 + 0.35);
    // 1-pixel crisp outer glass chamfer reflection
    float bevelHairline = smoothstep(1.6, 0.2, insidePixels) * topLight * 0.18;
    col += float3(bevelHairline, bevelHairline, bevelHairline);

    // -----------------------------------------------------------------
    // Interactive Touch Ripple Glow (Organic Refraction Energy)
    // -----------------------------------------------------------------
    if (active > 0.01)
    {
        float touchGlow = interactionMask * lerp(0.1, 0.4, pressed) * (1.0 - distNorm * 0.6);
        col += touchGlow * float3(0.04, 0.06, 0.09);
    }

    // -----------------------------------------------------------------
    // Material Tinting & Legibility Contrast
    // -----------------------------------------------------------------
    float3 darkTint = float3(0.03, 0.04, 0.06);
    col = lerp(col, darkTint, 0.25 * (1.0 - smoother01(1.0 - distNorm) * 0.35));

    // -----------------------------------------------------------------
    // Saturation & Brightness adjustments
    // -----------------------------------------------------------------
    float origLum = luminance(col);
    col = float3(origLum, origLum, origLum) + (col - float3(origLum, origLum, origLum)) * max(satFactor, 0.0);
    col += brightAdd;

    return float4(saturate(col) * alpha, alpha);
}
