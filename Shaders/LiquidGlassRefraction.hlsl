sampler2D input : register(s0);
// Credits to OverShifted for the Liquid Glass Effect (https://github.com/OverShifted/LiquidGlass)
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
float u_glowWeight       : register(c15); // directional specular glow weight (0.30)
float u_glowBias         : register(c16); // glow bias (0.0)
float u_glowEdge0        : register(c17); // glow edge start (0.06)
float blurRadius         : register(c18); // single-pass backdrop Gaussian blur radius in pixels (0..28)

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

float luminance(float3 col)
{
    return dot(col, float3(0.299, 0.587, 0.114));
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

// Pseudo-random noise for physical glass grain (OverShifted rand formula)
float rand(float2 co)
{
    return frac(sin(dot(co, float2(12.9898, 78.233))) * 43758.5453);
}

// Fast directional Glass Glow Rim: exact analytical expansion of sin(atan2(y, x) - 0.5)
// Eliminates heavy atan2 and sin transcendental operations.
float directionalGlow(float2 p)
{
    float lenSq = dot(p, p);
    if (lenSq < 0.000001) return 0.0;
    return (p.y * 0.87758256 - p.x * 0.47942554) * rsqrt(lenSq);
}

// OverShifted Exponential Refraction Lens Equation: f(x) = 1.0 - b * (c * e)^(-d * x - a)
float f_refract(float x, float a, float b, float c, float d)
{
    float exponent = -d * x - a;
    float baseVal = max(c * M_E, 0.0001);
    return 1.0 - b * exp2(exponent * log2(baseVal));
}

// Smooth Continuous Island Distance Field: Returns (insideDistPixels, inwardNormal.x, inwardNormal.y)
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

    float r = py < 0.0 ? topRadius : bottomRadius;
    float qx = px - (halfSize.x - r);
    float qy = (py < 0.0 ? -py : py) - (halfSize.y - r);

    float sdf = 0.0;
    float2 d = float2(0.0, 0.0);

    if (qx > 0.0 && qy > 0.0)
    {
        // Corner quadrant: evaluate continuous superellipse metric Ln
        if (abs(nPower - 2.0) < 0.01)
        {
            float lenN = sqrt(qx * qx + qy * qy);
            sdf = lenN - r;
            d = float2(qx, qy * (py < 0.0 ? -1.0 : 1.0));
        }
        else if (abs(nPower - 3.0) < 0.01)
        {
            float qx_3 = qx * qx * qx;
            float qy_3 = qy * qy * qy;
            float lenN = pow(max(qx_3 + qy_3, 0.0001), 0.33333333);
            sdf = lenN - r;
            d = float2(qx * qx, qy * qy * (py < 0.0 ? -1.0 : 1.0));
        }
        else
        {
            float qx_n = pow(max(qx, 0.0001), nPower);
            float qy_n = pow(max(qy, 0.0001), nPower);
            float lenN = pow(qx_n + qy_n, 1.0 / nPower);
            sdf = lenN - r;
            d = float2(pow(max(qx, 0.0001), nPower - 1.0), pow(max(qy, 0.0001), nPower - 1.0) * (py < 0.0 ? -1.0 : 1.0));
        }
    }
    else if (qx > 0.0)
    {
        // Vertical side segment (between top & bottom corners)
        sdf = qx - r;
        d = float2(1.0, 0.0);
    }
    else if (qy > 0.0)
    {
        // Top / Bottom horizontal flat segment
        sdf = qy - r;
        d = float2(0.0, py < 0.0 ? -1.0 : 1.0);
    }
    else
    {
        // Inside central rect core: blend smoothly across corner miter to avoid 90-degree normal step when topRadius == 0
        float dX = qx - r;
        float dY = qy - r;
        sdf = max(dX, dY);
        float2 w = exp(float2(dX - sdf, dY - sdf) * 0.35);
        d = float2(w.x, w.y * (py < 0.0 ? -1.0 : 1.0));
    }

    float2 pSign = float2(localPos.x < 0.0 ? -1.0 : 1.0, 1.0);
    float2 outwardNormal = float2(0.0, 0.0);
    float dLenSq = dot(d, d);
    if (dLenSq > 0.00001)
    {
        outwardNormal = d * rsqrt(dLenSq) * pSign;
    }

    return float3(-sdf, -outwardNormal.x, -outwardNormal.y);
}

// Ultra-fast Bilinear Texture Sampling
float3 sampleSource(float2 sourcePixel, float2 rcpSourceSize)
{
    float2 uv = saturate(sourcePixel * rcpSourceSize);
    return tex2D(input, uv).rgb;
}

// Hardware-bilinear 9-tap Gaussian blur inside the single refraction pass.
// Eliminates a separate full-surface WPF BlurEffect render target pass.
float3 sampleBlurredSource(float2 sourcePixel, float2 rcpSourceSize, float radius)
{
    float3 center = sampleSource(sourcePixel, rcpSourceSize);
    if (radius < 0.5)
        return center;

    float2 d1 = float2(radius * 0.75, radius * 0.75);
    float2 d2 = float2(radius * 1.50, 0.0);

    float3 sum = center * 0.20;
    sum += sampleSource(sourcePixel + float2( d1.x,  d1.y), rcpSourceSize) * 0.12;
    sum += sampleSource(sourcePixel + float2(-d1.x,  d1.y), rcpSourceSize) * 0.12;
    sum += sampleSource(sourcePixel + float2( d1.x, -d1.y), rcpSourceSize) * 0.12;
    sum += sampleSource(sourcePixel + float2(-d1.x, -d1.y), rcpSourceSize) * 0.12;
    sum += sampleSource(sourcePixel + float2( d2.x,  0.0),  rcpSourceSize) * 0.08;
    sum += sampleSource(sourcePixel + float2(-d2.x,  0.0),  rcpSourceSize) * 0.08;
    sum += sampleSource(sourcePixel + float2( 0.0,   d2.x), rcpSourceSize) * 0.08;
    sum += sampleSource(sourcePixel + float2( 0.0,  -d2.x), rcpSourceSize) * 0.08;
    return sum;
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
    float2 halfSize = max(notchSize * 0.5, float2(0.5, 0.5));

    // Fast Bounding Box Pre-Cull:
    // Any pixel outside the notch boundary + 1.5px antialias padding has alpha = 0.
    // Early exit immediately bypasses all SDF, pow(), refraction, glow, and sampling logic!
    if (abs(localPos.x) > halfSize.x + 1.5 || abs(localPos.y) > halfSize.y + 1.5)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float2 basePixel = float2(npx + offX, npy + offY);
    float2 rcpSourceSize = 1.0 / sourceSize;

    // OverShifted parameters
    float nSquircle = max(powerFactor, 1.05);
    float bevelAmt = saturate(bevelMode);
    float paramA = max(u_a, 0.0);
    float paramB = max(u_b, 0.0);
    float paramC = max(u_c, 0.1);
    float paramD = max(u_d, 0.1) * lerp(1.0, 0.58, bevelAmt);
    float fPow   = max(u_fPower, 0.1);
    float bend   = max(edgeBend, 0.05);

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

    // Normalized coordinate relative to notch bounds [-1, 1]
    float2 pNorm = localPos / halfSize;

    // Optical Rim & Normalized depth [0, 1] from edge to center
    float notchRadius = min(halfSize.x, halfSize.y);
    float distNorm = saturate(insidePixels / max(notchRadius, 1.0));
    float oneMinusDist = 1.0 - distNorm;

    // -----------------------------------------------------------------
    // Pointer interaction & dynamic ripple (only evaluated when pressed > 0.001)
    // -----------------------------------------------------------------
    float flexPixels = 0.0;
    float interactionMask = 0.0;
    float interactionEnergy = 0.0;
    float2 pointerLocal = float2(0.0, 0.0);
    float active = saturate(pointerActive);
    float pressed = saturate(pressAmount) * active;

    if (pressed > 0.001)
    {
        float2 pointer01 = saturate(float2(pointerX, pointerY));
        pointerLocal = pointer01 * notchSize - halfSize;
        float2 pointerDelta = localPos - pointerLocal;

        float radiusPixels = max(notchH * 0.70, 1.0);
        float2 interactionScale = float2(max(notchW * 0.35, radiusPixels * 2.0), max(notchH * 0.75, radiusPixels * 1.5));
        float interactionDist = length(pointerDelta / interactionScale);
        interactionMask = smoothstep(1.0, 0.0, interactionDist) * active;
        interactionEnergy = interactionMask * lerp(0.5, 1.0, pressed);

        float2 radialFromPointer = safeNormalize(pointerDelta);

        // Dynamic wave / ripple flex
        float ripplePhase = pressed * 4.71238898; // 1.5 * PI
        float dimpleSlope = sin(saturate(interactionDist) * 6.2831853 - ripplePhase) * interactionMask;
        flexPixels = max(flexStrength, 0.0) * dimpleSlope * lerp(0.5, 3.5, pressed);

        // Dynamic flex on inward normal
        inwardNormal = safeNormalize(inwardNormal - radialFromPointer * (interactionMask * pressed * 0.06));
    }

    // -----------------------------------------------------------------
    // OverShifted Exponential Refraction Lens Formula + Deep Meniscus Dome
    // -----------------------------------------------------------------
    float fVal = f_refract(distNorm, paramA, paramB, paramC, paramD);
    float domePull = (oneMinusDist * oneMinusDist) * lerp(0.16, 0.36, bevelAmt) * saturate(paramB * 0.45);
    float refractBase = clamp(fVal - domePull, 0.02, 1.25);
    float refractFactor = (abs(fPow - 1.0) < 0.001) ? refractBase : pow(max(refractBase, 0.0001), fPow);

    // OverShifted displacement vector mapping background coordinates
    float2 samplePNorm = pNorm * refractFactor;
    float2 displacement = (samplePNorm - pNorm) * halfSize * bend;

    // Organic glass surface wave distortion (driven by u_glowEdge0)
    float distortAmt = clamp(u_glowEdge0, 0.0, 2.0);
    if (distortAmt > 0.001)
    {
        float2 wavePhase = localPos * 0.085;
        float2 wave = float2(
            sin(wavePhase.x + wavePhase.y * 0.7) + cos(wavePhase.y * 1.3 - wavePhase.x * 0.5) * 0.5,
            cos(wavePhase.y - wavePhase.x * 0.6) + sin(wavePhase.x * 1.2 + wavePhase.y * 0.4) * 0.5);
        float waveMask = lerp(0.35, 1.0, oneMinusDist);
        displacement += wave * (distortAmt * 14.0 * waveMask);
    }

    // Add pointer ripple displacement
    if (pressed > 0.001)
    {
        displacement += safeNormalize(localPos - pointerLocal) * flexPixels;
    }

    float2 sourcePixel = basePixel + displacement;

    // -----------------------------------------------------------------
    // Chromatic Dispersion (Aberration) - Fast 3-Tap RGB
    // -----------------------------------------------------------------
    float3 col = float3(0.0, 0.0, 0.0);
    float dispMag = length(displacement);
    float2 outwardNormal = -inwardNormal;
    float chromaEdge = pow(oneMinusDist, 0.75);
    float chromaAmount = max(u_chroma, 0.0) * (0.45 + 0.55 * chromaEdge) * (1.2 + saturate(dispMag * 0.12) * 3.5);

    float activeBlur = clamp(blurRadius, 0.0, 28.0);
    if (chromaAmount > 0.001)
    {
        float2 chromaDir = dispMag > 0.001 ? (displacement / dispMag) : outwardNormal;
        float2 chromaOffset = chromaDir * min(chromaAmount * 4.5, 14.0);
        float3 sampleR = sampleBlurredSource(sourcePixel + chromaOffset, rcpSourceSize, activeBlur);
        float3 sampleG = sampleBlurredSource(sourcePixel, rcpSourceSize, activeBlur);
        float3 sampleB = sampleBlurredSource(sourcePixel - chromaOffset, rcpSourceSize, activeBlur);
        col = float3(sampleR.r, sampleG.g, sampleB.b);
    }
    else
    {
        col = sampleBlurredSource(sourcePixel, rcpSourceSize, activeBlur);
    }

    // -----------------------------------------------------------------
    // Fresnel Inner Bloom & Specular Rim Glint (u_glowBias & u_glowWeight)
    // -----------------------------------------------------------------
    float fresnelStrength = clamp(u_glowBias, 0.0, 2.5);
    if (fresnelStrength > 0.001)
    {
        float fresnelWidth = lerp(22.0, 38.0, bevelAmt);
        float fresnelBand = 1.0 - smoother01(insidePixels / fresnelWidth);
        float topBias = saturate(-outwardNormal.y * 0.5 + 0.5);
        float fresnelCurve = fresnelBand * fresnelBand * (0.65 + 0.35 * topBias);
        float3 fresnelTint = lerp(float3(0.82, 0.90, 1.0), float3(1.0, 1.0, 1.0), topBias);
        col = lerp(col, saturate(col * 1.18 + fresnelTint * 0.30), saturate(fresnelCurve * fresnelStrength * 0.58));
    }

    float specStrength = clamp(u_glowWeight, 0.0, 2.5);
    if (specStrength > 0.001)
    {
        float dirGlow = saturate(directionalGlow(pNorm) * 0.5 + 0.5);
        float bevelWidth = lerp(3.2, 8.0, bevelAmt);
        float outerBevel = smoothstep(bevelWidth, 0.15, insidePixels);
        float innerSheen = (1.0 - smoother01(insidePixels / lerp(12.0, 26.0, bevelAmt))) * 0.38;
        float specRim = (outerBevel * (0.45 + 0.55 * dirGlow) + innerSheen * pow4(dirGlow)) * specStrength * 0.42;
        col += float3(specRim, specRim, specRim * 1.04);
    }

    // -----------------------------------------------------------------
    // Apple Liquid Glass Micro-Bevel Chamfer Hairline (outer 1.8px..3.5px)
    // -----------------------------------------------------------------
    float chamferWidth = lerp(1.8, 3.4, bevelAmt);
    if (insidePixels < chamferWidth)
    {
        float topLight = saturate(-outwardNormal.y * 0.65 + 0.35);
        float bevelHairline = smoothstep(chamferWidth, 0.2, insidePixels) * topLight * lerp(0.14, 0.24, bevelAmt);
        col += float3(bevelHairline, bevelHairline, bevelHairline);
    }

    // -----------------------------------------------------------------
    // Touch Light & Dynamic Moving Highlight
    // -----------------------------------------------------------------
    float touchLightStrength = max(highlightStrength, 0.0);
    if (touchLightStrength > 0.001)
    {
        // Optical rim factor: confined strictly to the outer perimeter rim (18px)
        // This ensures the central notch body remains crystal clear with ZERO lighting seams
        float rimWidth = max(bottomCornerR * 0.85, lerp(18.0, 28.0, bevelAmt));
        float rim = 1.0 - smoother01(insidePixels / rimWidth);

        if (rim > 0.0001 || pressed > 0.001)
        {
            // Animated/spring-tracked light source local coordinate
            float2 lightLocal = float2(lightX, lightY) * notchSize - halfSize;

            // Vector from current surface pixel toward light source
            float2 lightRay = safeNormalize(lightLocal - localPos);
            float lightFacing = saturate(dot(outwardNormal, lightRay));
            float lightFacing4 = pow4(lightFacing);

            float2 lightDistanceScale = max(notchSize * float2(0.72, 0.95), float2(1.0, 1.0));
            float lightDistance = length((localPos - lightLocal) / lightDistanceScale);
            float localLightFalloff = 1.0 - smoother01(lightDistance);

            // Specular highlight along the glass outer squircle rim (ambient when idle, focused when pressed)
            float specular = lightFacing4 * rim * touchLightStrength * lerp(0.45, 1.0, localLightFalloff) * lerp(0.55, 1.0, pressed);

            // Ambient touch light glow radiating from touch / cursor position during click / hold
            if (pressed > 0.001)
            {
                float touchGlow = interactionEnergy * (0.70 + 0.30 * rim) * touchLightStrength * 1.35;
                float3 ambientTouch = saturate(col * 0.70 + float3(0.55, 0.55, 0.55));
                col += touchGlow * (float3(0.08, 0.08, 0.10) + ambientTouch * 0.12);

                // Click / press reactive touch specular highlight
                float touchSpecular = interactionMask * pressed * 0.60 * touchLightStrength;
                float rimSpecular = lightFacing4 * rim * interactionMask * pressed * 0.80 * touchLightStrength;
                col += (touchSpecular + rimSpecular) * ambientTouch;
            }

            // Specular surface sheen spill along outer rim
            float3 ambientSpill = saturate(col * 1.08 + 0.04);
            col += specular * (float3(0.085, 0.088, 0.095) + ambientSpill * 0.075);
        }
    }

    // -----------------------------------------------------------------
    // Frosted Glass Physical Micro-Grain (u_noise)
    // -----------------------------------------------------------------
    float noiseStrength = clamp(u_noise, 0.0, 1.0);
    if (noiseStrength > 0.001)
    {
        float grain = rand(floor(localPos + halfSize)) - 0.5;
        col += grain * (noiseStrength * 0.24);
    }

    // -----------------------------------------------------------------
    // Saturation & Brightness adjustments
    // -----------------------------------------------------------------
    float origLum = luminance(col);
    col = float3(origLum, origLum, origLum) + (col - float3(origLum, origLum, origLum)) * max(satFactor, 0.0);
    col += brightAdd;

    return float4(saturate(col) * alpha, alpha);
}