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
        return float3(r - qx, localPos.x < 0.0 ? 1.0 : -1.0, 0.0);
    }
    else if (qy > 0.0)
    {
        // Top / Bottom horizontal flat segment
        return float3(r - qy, 0.0, py < 0.0 ? 1.0 : -1.0);
    }
    else
    {
        // Inside central rect core: blend smoothly across corner miter to avoid 90-degree normal step when topRadius == 0
        float dX = qx - r;
        float dY = qy - r;
        sdf = max(dX, dY);
        // exp(0) == 1 for the larger axis, so only the smaller one needs exp():
        // identical weights, one transcendental fewer on most interior pixels.
        float e = exp(-abs(dX - dY) * 0.35);
        float2 w = dX >= dY ? float2(1.0, e) : float2(e, 1.0);
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
#ifdef GLASS_RIM
    // The native sampler already clamps to the active subrectangle.
    return tex2D(input, sourcePixel * rcpSourceSize).rgb;
#else
    float2 uv = saturate(sourcePixel * rcpSourceSize);
    return tex2D(input, uv).rgb;
#endif
}

// Hardware-bilinear 9-tap Gaussian blur inside the single refraction pass.
// Eliminates a separate full-surface WPF BlurEffect render target pass.
float3 sampleBlurredSource(float2 sourcePixel, float2 rcpSourceSize, float radius)
{
    float3 center = sampleSource(sourcePixel, rcpSourceSize);
#ifdef GLASS_PREFILTERED_BACKDROP
    return center;
#else
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
#endif
}

// Integrate about the original ray. For a compressed pixel the two halves
// also measure the colour gradient along the direction of dispersion.
float3 sampleTransmission(float2 pixel, float2 axis, float spread, float chroma, float2 rcpSize, float blur)
{
#ifndef GLASS_PREFILTERED_BACKDROP
    // WPF may already take nine taps per ray for its blur. Keep its PS 3.0
    // sampler budget at two rays, including when the bevel is compressed.
    bool filtered = spread > 0.125 && blur < 0.5;
    float outerOffset = filtered ? spread : 0.0;
    float innerOffset = filtered ? spread : chroma;
    float3 col = sampleBlurredSource(pixel - axis * outerOffset, rcpSize, blur);
    if (innerOffset > 0.01)
    {
        float3 inner = sampleBlurredSource(pixel + axis * innerOffset, rcpSize, blur);
        float3 weight = float3(-0.45, 0.0, 1.0);
        if (filtered) weight = 0.5 + 0.5 * weight * saturate(chroma / innerOffset);
        col += (inner - col) * weight;
    }
    return saturate(col);
#else
    if (spread <= 0.125 || blur >= 0.5)
    {
        float3 principal = sampleBlurredSource(pixel, rcpSize, blur);
        if (chroma > 0.01)
        {
            float3 blue = sampleBlurredSource(pixel + axis * chroma, rcpSize, blur);
            principal.rb += (blue.rb - principal.rb) * float2(-0.45, 1.0);
        }
        return saturate(principal);
    }
    float2 offset = axis * spread;
    float split = 0.5 * saturate((spread - 0.5) * 2.0);
    float2 inner = offset * (1.0 - split);
    float3 minus = sampleSource(pixel - inner, rcpSize);
    float3 plus = sampleSource(pixel + inner, rcpSize);
    if (split > 0.0)
    {
        float2 outer = offset * (1.0 + split);
        minus = 0.5 * (minus + sampleSource(pixel - outer, rcpSize));
        plus = 0.5 * (plus + sampleSource(pixel + outer, rcpSize));
    }
    float3 col = (minus + plus) * 0.5;
    if (chroma > spread)
    {
        float3 blue = sampleSource(pixel + axis * chroma, rcpSize);
        col.rb += (blue.rb - col.rb) * float2(-0.45, 1.0);
    }
    else
        col.rb += (plus.rb - minus.rb) * (0.5 * chroma / spread) * float2(-0.45, 1.0);
    return saturate(col);
#endif
}

// Plain backdrop for the animation bleed band, with the same tone adjustments
// as the glass body so the band reads as glass, not as a flat fill.
float3 bleedBackdrop(float2 basePixel, float2 rcpSourceSize)
{
    float3 c = sampleSource(basePixel, rcpSourceSize);
    float lum = luminance(c);
    c = float3(lum, lum, lum) + (c - float3(lum, lum, lum)) * max(satFactor, 0.0);
    return saturate(c + brightAdd);
}

float3 finishGlass(float3 col, float2 pixel)
{
    float grainStrength = saturate(u_noise);
    if (grainStrength > 0.001)
        col += (rand(floor(pixel)) - 0.5) * (grainStrength * 0.24);
    float lum = luminance(col);
    return saturate(lum + (col - lum) * max(satFactor, 0.0) + brightAdd);
}

float4 shadeGlass(float2 rasterPixel, float2 rcpSourceSize)
{
    float2 notchSize = float2(notchW, notchH);

    // GPU path only: while the notch animates, the lens is drawn `bleed` pixels
    // in from the frame origin and the surrounding pixels carry the plain
    // backdrop at full alpha. WPF shifts the frame back and its live clip cuts
    // the shape, so late frames never reveal the dark shadow layer behind them.
    // bleed == 0 (always in the WPF effect) keeps the original output exactly.
#ifdef GLASS_BLEED
    float bleed = max(GLASS_BLEED, 0.0);
#else
    float bleed = 0.0;
#endif
    // GPU path only: optical rim width in pixels (from the ZRadius setting, as the
    // CPU renderer uses). The WPF effect uses a fixed 24 px cap.
#ifdef GLASS_RIM
    float rimCap = max(GLASS_RIM, 0.0);
#else
    float rimCap = 0.0;
#endif
    float npx = rasterPixel.x - bleed;
    float npy = rasterPixel.y - bleed;

    float2 localPos = float2(npx - notchW * 0.5, npy - notchH * 0.5);
    float2 halfSize = notchSize * 0.5;

    // Fast Bounding Box Pre-Cull:
    // Any pixel outside the notch boundary + 1.5px antialias padding has alpha = 0.
    // Early exit immediately bypasses all SDF, pow(), refraction, glow, and sampling logic!
    if (abs(localPos.x) > halfSize.x + 1.5 + bleed || abs(localPos.y) > halfSize.y + 1.5 + bleed)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float2 basePixel = float2(npx + offX, npy + offY);

    // OverShifted parameters
    float nSquircle = max(powerFactor, 1.05);
    float bevelAmt = saturate(bevelMode);
    float paramA = max(u_a, 0.0);
    float paramB = max(u_b, 0.0);
    float paramC = max(u_c, 0.1);
    float paramD = max(u_d, 0.1) * lerp(1.0, 0.58, bevelAmt);
    float fPow   = max(u_fPower, 0.1);
    float bend   = max(edgeBend, 0.0);

    // Compute SDF and normal
    float2 fieldPos = localPos;
    float2 fieldHalf = halfSize;
    if (rimCap > 0.0 && topCornerR < 0.5)
    {
        // Square top corners: the notch hangs from the screen edge, so the top is
        // not a glass edge. Push it out of the field so no lens runs along it.
        float lift = rimCap * 4.0 + 2.0;
        fieldPos.y += lift * 0.5;
        fieldHalf.y += lift * 0.5;
    }
#ifdef GLASS_PREFILTERED_BACKDROP
    float rimRadius = lens[6].y;
    float invRim = lens[6].z;
    float edgeLoss = lens[10].y;
    float decay = lens[12].y;
    float dome = lens[15].y;
    float lever = lens[30].y;
#else
    float notchRadius = min(halfSize.x, halfSize.y);
    float rimRadius = min(notchRadius, rimCap > 0.0 ? rimCap : 24.0);
    float invRim = 1.0 / max(rimRadius, 1.0);
    float logBase = log2(paramC * M_E);
    float edgeLoss = paramB * exp2(-paramA * logBase);
    float decay = paramD * logBase;
    float dome = lerp(0.16, 0.36, bevelAmt) * saturate(paramB * 0.45);
    float lever = rimRadius * 1.5 * bend;
#endif
#ifdef GLASS_PREFILTERED_BACKDROP
    float invLightRim = lens[26].z;
    float reflectScale = lens[16].y;
    float reflectPeak = lens[16].z;
    float glintScale = lens[15].z;
    float chamferWidth = lens[31].y;
    float invChamferRange = lens[31].z;
    float inset = lens[26].w;
    float2 clearance = fieldHalf - abs(fieldPos);
    // Beyond every corner and optical band there is just the backdrop and its
    // finish. Native capture already supplies a filtered texture here.
    if (min(clearance.x, clearance.y) >= inset && pressAmount * pointerActive <= 0.001)
        return float4(finishGlass(sampleSource(basePixel, rcpSourceSize), localPos + halfSize), 1.0);
#else
    float invLightRim = 1.0 / max(bottomCornerR * 0.85, lerp(18.0, 28.0, bevelAmt));
    float specStrength = clamp(u_glowWeight, 0.0, 2.5);
    float reflectScale = clamp(u_glowBias, 0.0, 2.5) * 0.72 + specStrength * 0.42;
    float reflectPeak = sqrt(reflectScale);
    float glintScale = 0.14 + 0.35 * specStrength;
    float chamferWidth = lerp(1.8, 3.4, bevelAmt);
    float invChamferRange = 1.0 / (0.2 - chamferWidth);
#endif
    float3 field = notchDistanceField(fieldPos, fieldHalf, topCornerR, bottomCornerR, nSquircle);
    float insidePixels = field.x;
    float2 inwardNormal = field.yz;

    // Smooth anti-aliased silhouette alpha
    float alpha = saturate(insidePixels + 0.5);
#ifdef GLASS_PREFILTERED_BACKDROP
    if (alpha <= 0.0)
    {
        if (bleed > 0.0)
            return float4(bleedBackdrop(basePixel, rcpSourceSize), 1.0);
        return float4(0.0, 0.0, 0.0, 0.0);
    }
#endif

    // Optical Rim & Normalized depth [0, 1] from edge to center
    float distNorm = saturate(insidePixels * invRim);
    float oneMinusDist = 1.0 - distNorm;

    // -----------------------------------------------------------------
    // Pointer interaction & dynamic ripple (only evaluated when pressed > 0.001)
    // -----------------------------------------------------------------
    float2 pointerDisplacement = 0.0;
    float interactionMask = 0.0;
    float interactionEnergy = 0.0;
    float active = saturate(pointerActive);
    float pressed = saturate(pressAmount) * active;

    if (pressed > 0.001)
    {
#ifdef GLASS_PREFILTERED_BACKDROP
        float2 pointerDelta = localPos - float2(lens[22].y, lens[23].y);
        float interactionDist = length(pointerDelta * float2(lens[22].z, lens[23].z));
        float ripplePhase = lens[25].y;
        float flex = lens[25].z;
        float energyGain = lens[25].w;
#else
        float2 pointer01 = saturate(float2(pointerX, pointerY));
        float2 pointerLocal = pointer01 * notchSize - halfSize;
        float2 pointerDelta = localPos - pointerLocal;

        float radiusPixels = max(notchH * 0.70, 1.0);
        float2 interactionScale = float2(max(notchW * 0.35, radiusPixels * 2.0), max(notchH * 0.75, radiusPixels * 1.5));
        float interactionDist = length(pointerDelta / interactionScale);
        float ripplePhase = pressed * 4.71238898;
        float flex = max(flexStrength, 0.0) * lerp(0.5, 3.5, pressed);
        float energyGain = lerp(0.5, 1.0, pressed);
#endif
        interactionMask = smoothstep(1.0, 0.0, interactionDist) * active;
        interactionEnergy = interactionMask * energyGain;

        float2 radialFromPointer = safeNormalize(pointerDelta);

        // Dynamic wave / ripple flex
        float dimpleSlope = sin(saturate(interactionDist) * 6.2831853 - ripplePhase) * interactionMask;
        pointerDisplacement = radialFromPointer * (flex * dimpleSlope);

        // Dynamic flex on inward normal
        inwardNormal = safeNormalize(inwardNormal - radialFromPointer * (interactionMask * pressed * 0.06));
    }

    // -----------------------------------------------------------------
    // OverShifted Exponential Refraction Lens Formula + Deep Meniscus Dome
    // -----------------------------------------------------------------
    float grazing = oneMinusDist * oneMinusDist;
    // Scale lens-local coordinates towards the centre, as in the reference
    // shader. This also bends lines running across a flat side of the tray.
#ifdef GLASS_PREFILTERED_BACKDROP
    float2 bendAxis = localPos * float2(lens[2].z, lens[3].z);
#else
    float2 bendAxis = -localPos / halfSize;
#endif
    float2 displacement = 0.0;
#ifdef GLASS_PREFILTERED_BACKDROP
    float radialStep = 1.0;
#endif
    if (oneMinusDist > 0.0)
    {
        float loss = edgeLoss * exp2(-distNorm * distNorm * decay);
        float ray = 1.0 - loss - grazing * dome;
        float bounded = clamp(ray, 0.02, 1.25);
        float factor = bounded;
        if (abs(fPow - 1.0) >= 0.001)
            factor = pow(bounded, fPow);
        displacement = bendAxis * ((1.0 - factor) * lever * oneMinusDist);
#ifdef GLASS_PREFILTERED_BACKDROP
        // Differentiate the same ray to size the filter around its original
        // centre. The clamp has zero slope on either plateau.
        float slope = loss * (lens[12].z * distNorm) + 2.0 * oneMinusDist * dome;
        if (abs(fPow - 1.0) >= 0.001) slope *= fPow * factor / bounded;
        if (ray != bounded) slope = 0.0;
        radialStep = 1.0 - lever * invRim * (1.0 - factor + oneMinusDist * slope);
#endif

        float distortAmt = clamp(u_glowEdge0, 0.0, 2.0);
        if (distortAmt > 0.001)
        {
            float2 wave = sin(localPos.yx * 0.085 + localPos.xy * 0.045);
            displacement += wave * (distortAmt * 5.0 * grazing);
        }
    }

    float2 sourcePixel = basePixel + displacement + pointerDisplacement;

    // -----------------------------------------------------------------
    // Keep green on the principal ray. The more strongly bent blue ray
    // also gives a local gradient for the smaller red shift.
    // -----------------------------------------------------------------
    float2 outwardNormal = -inwardNormal;
    float chromaAmount = min(max(u_chroma, 0.0) * 2.0, 3.0) * grazing;
    float activeBlur = clamp(blurRadius, 0.0, 28.0);
#ifdef GLASS_PREFILTERED_BACKDROP
    float footprintSq = radialStep * radialStep;
#else
    // Measure the bent ray in actual WPF screen pixels before clipping the
    // silhouette, so all four lanes contribute a valid derivative at the lip.
    float2 footprint = float2(dot(ddx(sourcePixel), inwardNormal), dot(ddy(sourcePixel), inwardNormal));
    float footprintSq = dot(footprint, footprint);
    if (alpha <= 0.0) return 0.0;
#endif
    float spread = 0.25 * sqrt(max(footprintSq - 1.0, 0.0));
    float3 col = sampleTransmission(sourcePixel, bendAxis, spread, chromaAmount, rcpSourceSize, activeBlur);

    // -----------------------------------------------------------------
    // Screen-space reflection. Mirror the backdrop across the local tangent
    // and mix it with transmission as the surface turns away from the viewer.
    // -----------------------------------------------------------------
    float chamfer = smoothstep(0.0, 1.0, (insidePixels - chamferWidth) * invChamferRange);
    float reflectivity = grazing * reflectScale;
    if (reflectivity > 0.0)
    {
        // At the lip the curved surface scans two backdrop texels in the
        // normal direction per output pixel, while retaining tangential
        // detail. A nearby lamp therefore reads as a narrow arc.
        float mirrorTravel = insidePixels * (2.0 + oneMinusDist) + rimRadius * 0.65;
        float2 reflectedPixel = basePixel + outwardNormal * mirrorTravel;
        float3 reflected = sampleSource(reflectedPixel, rcpSourceSize);
        // Work per channel in display space. A warm light can brighten the
        // red return without coupling its dispersed ray into green.
        float3 contrast = saturate(reflected - col);
        // Concentrate the bright return on the rounded lip; its shoulder
        // still carries a softer image of the same source.
        float brightReturn = oneMinusDist * reflectPeak * (0.65 + 1.35 * chamfer);
        float3 weight = saturate(lerp(reflectivity, brightReturn, contrast));
        col = lerp(col, reflected, weight);
    }

    // -----------------------------------------------------------------
    // Apple Liquid Glass Micro-Bevel Chamfer Hairline (outer 1.8px..3.5px)
    // -----------------------------------------------------------------
    if (insidePixels < chamferWidth)
    {
        float facing = saturate(dot(outwardNormal, float2(-0.48, -0.88)) * 0.5 + 0.5);
        float glint = chamfer * facing * glintScale;
        col = lerp(col, float3(0.93, 0.96, 1.0), saturate(glint));
    }

    // -----------------------------------------------------------------
    // Touch Light & Dynamic Moving Highlight
    // -----------------------------------------------------------------
    float touchLightStrength = max(highlightStrength, 0.0);
    if (touchLightStrength > 0.001)
    {
        // Optical rim factor: confined strictly to the outer perimeter rim (18px)
        // This ensures the central notch body remains crystal clear with ZERO lighting seams
        float rim = 1.0 - smoother01(insidePixels * invLightRim);

        if (rim > 0.0001 || pressed > 0.001)
        {
            // Animated/spring-tracked light source local coordinate
#ifdef GLASS_PREFILTERED_BACKDROP
            float2 lightRay = float2(lens[28].y, lens[29].y) - localPos;
            float2 scaledLightRay = lightRay * float2(lens[28].z, lens[29].z);
#else
            float2 lightLocal = float2(lightX, lightY) * notchSize - halfSize;
            float2 lightRay = lightLocal - localPos;
            float2 lightDistanceScale = max(notchSize * float2(0.72, 0.95), float2(1.0, 1.0));
            float2 scaledLightRay = lightRay / lightDistanceScale;
#endif
            float facing = max(dot(outwardNormal, lightRay), 0.0);
            float facingSq = saturate(facing * facing / max(dot(lightRay, lightRay), 0.000001));
            float lightFacing4 = facingSq * facingSq;

            float localLightFalloff = 1.0 - smoother01(length(scaledLightRay));

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

    col = finishGlass(col, localPos + halfSize);
    if (bleed > 0.0)
        return float4(lerp(bleedBackdrop(basePixel, rcpSourceSize), col, alpha), 1.0);
    return float4(col * alpha, alpha);
}

#ifndef GLASS_PREFILTERED_BACKDROP
float4 main(float2 uv : TEXCOORD) : COLOR
{
    if (min(min(srcW, srcH), min(notchW, notchH)) < 1.0)
        return tex2D(input, saturate(uv));
    float2 sourceSize = float2(srcW, srcH);
    return shadeGlass(uv * sourceSize, 1.0 / sourceSize);
}
#endif
