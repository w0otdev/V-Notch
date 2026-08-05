sampler2D input : register(s0);

float srcW          : register(c0);
float srcH          : register(c1);
float notchW        : register(c2);
float notchH        : register(c3);
float offX          : register(c4);
float offY          : register(c5);
float bottomCornerR : register(c6);
float zR            : register(c7);
float uRefr         : register(c8);
float uChroma       : register(c9);
float uDistort      : register(c10);
float bevelMode     : register(c11);
float satFactor     : register(c12);
float brightAdd     : register(c13);
float topCornerR    : register(c14);
float edgeBend      : register(c15);


float smoother01(float x)
{
    x = saturate(x);
    return x * x * x * (x * (x * 6.0 - 15.0) + 10.0);
}


float luminance(float3 col)
{
    return dot(col, float3(0.299, 0.587, 0.114));
}


// Edge-concentrated lens profile.
//
// t = 0 at the physical edge.
// t = 1 at the inner end of the optical rim.
//
// The profile rises smoothly after the silhouette and settles to zero
// before reaching the flat centre.
float lensProfile(float t, float broad)
{
    t = saturate(t);

    float rise = smoother01(t * 4.25);
    float inv = 1.0 - t;

    float narrowFall = inv * inv * inv;
    float broadFall = inv * inv;

    float profile = rise * lerp(narrowFall, broadFall, broad);

    return saturate(profile * 1.22);
}


// Returns:
//
// x  = signed distance toward the inside
// yz = unit inward normal
//
// This replaces five rounded-rectangle SDF evaluations with one.
float3 roundedRectField(
    float2 p,
    float2 halfSize,
    float topRadius,
    float bottomRadius)
{
    float radius = p.y < 0.0 ? topRadius : bottomRadius;

    radius = clamp(
        radius,
        0.0,
        min(halfSize.x, halfSize.y));

    float2 q = abs(p) - (halfSize - radius);
    float2 outside = max(q, 0.0);

    float outsideLength = length(outside);

    float sdf =
        outsideLength +
        min(max(q.x, q.y), 0.0) -
        radius;

    float2 pSign = float2(
        p.x < 0.0 ? -1.0 : 1.0,
        p.y < 0.0 ? -1.0 : 1.0);

    float2 outwardNormal;

    if (outsideLength > 0.0001)
    {
        outwardNormal = outside / outsideLength;
        outwardNormal *= pSign;
    }
    else if (q.x > q.y)
    {
        outwardNormal = float2(pSign.x, 0.0);
    }
    else
    {
        outwardNormal = float2(0.0, pSign.y);
    }

    float2 inwardNormal = -outwardNormal;

    return float3(
        -sdf,
        inwardNormal.x,
        inwardNormal.y);
}


float3 sampleSource(float2 sourcePixel, float2 sourceSize)
{
    float2 sampleUv = saturate(sourcePixel / sourceSize);
    return tex2D(input, sampleUv).rgb;
}


float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 sourceSize = max(
        float2(srcW, srcH),
        float2(1.0, 1.0));

    float2 notchSize = max(
        float2(notchW, notchH),
        float2(1.0, 1.0));

    float geometryValid =
        step(1.0, srcW) *
        step(1.0, srcH) *
        step(1.0, notchW) *
        step(1.0, notchH);

    // Invalid geometry becomes a true 1:1 backdrop.
    // This also avoids division by zero and NaN propagation.
    if (geometryValid < 0.5)
    {
        return tex2D(input, saturate(uv));
    }

    // Output UV belongs to the visible notch, not the overscan texture.
    // UV already points to pixel centres, so uv * notchSize produces
    // coordinates such as 0.5, 1.5, 2.5...
    float2 notchPixel = uv * notchSize;

    float2 halfSize = max(
        notchSize * 0.5,
        float2(0.5, 0.5));

    float2 localPosition = notchPixel - halfSize;

    float2 basePixel =
        float2(offX, offY) +
        notchPixel;

    float3 field = roundedRectField(
        localPosition,
        halfSize,
        topCornerR,
        bottomCornerR);

    float inside = field.x;
    float2 inwardNormal = field.yz;

    // Preserve the existing opaque-output behaviour outside the shape.
    // The WPF element should normally provide the actual rounded clip.
    if (inside <= 0.0)
    {
        return float4(
            sampleSource(basePixel, sourceSize),
            1.0);
    }

    float broad = step(0.5, bevelMode);

    // Keep the exposed slider useful, but prevent mapping inversion.
    float bend = clamp(edgeBend, 0.0, 3.0);

    float sideAxis = abs(inwardNormal.x);
    sideAxis *= sideAxis;
    sideAxis *= sideAxis;

    // Capsule side caps can have a slightly wider optical zone,
    // but no longer grow without bound.
    float rimWidth =
        max(zR, 1.0) *
        (1.0 + 0.22 * bend * sideAxis);

    float rimT = saturate(
        inside / max(rimWidth, 0.001));

    float profile = lensProfile(rimT, broad);

    float rim =
        1.0 -
        smoother01(
            inside / max(rimWidth, 1.0));

    float refraction = max(uRefr, 0.0);
    float refractionResponse =
        refraction / (1.0 + refraction);

    // edgeBend = 0 disables refraction.
    // edgeBend = 1 is the natural Apple-like range.
    float bendGain =
        min(
            bend * (0.72 + 0.28 * bend),
            1.80);

    float amplitude =
        rimWidth *
        0.42 *
        refractionResponse *
        bendGain *
        lerp(1.0, 1.08, broad);

    float aspect =
        saturate(
            (notchH / max(notchW, 1.0)) *
            2.5);

    // Keep the lens close to isotropic.
    // The previous 0.68 value visibly weakened top/bottom refraction.
    float verticalBalance =
        lerp(0.80, 1.0, aspect);

    float2 displacement =
        -inwardNormal *
        float2(1.0, verticalBalance) *
        amplitude *
        profile;

    // Smooth deterministic liquid asymmetry.
    //
    // No hashes, no random grain and no sin().
    // The centre and silhouette remain stable.
    if (uDistort > 0.0001)
    {
        float2 normalizedPosition =
            localPosition /
            max(halfSize, float2(1.0, 1.0));

        float nx2 =
            normalizedPosition.x *
            normalizedPosition.x;

        float ny2 =
            normalizedPosition.y *
            normalizedPosition.y;

        float flow =
            normalizedPosition.x *
            normalizedPosition.y *
            (1.0 - nx2) *
            (1.0 - ny2);

        float2 tangent =
            float2(
                -inwardNormal.y,
                inwardNormal.x);

        displacement +=
            tangent *
            max(uDistort, 0.0) *
            1.25 *
            flow *
            profile;
    }

    float2 sourcePixel =
        basePixel +
        displacement;

    float3 center =
        sampleSource(
            sourcePixel,
            sourceSize);

    float3 col = center;
    float localDetail = 0.0;

    // A maximum of two additional samples.
    //
    // They provide:
    // - soft radial scattering
    // - subtle spectral dispersion
    // - an estimate of background detail for adaptive separation
    if (rim > 0.001)
    {
        float scatterPixels =
            lerp(0.30, 0.62, broad) *
            rim;

        // Apple-like dispersion should generally remain below one pixel.
        float chromaPixels =
            min(
                max(uChroma, 0.0) *
                (0.18 + rimWidth * 0.018),
                1.10) *
            profile;

        float tapPixels =
            scatterPixels +
            chromaPixels;

        float2 tapOffset =
            inwardNormal *
            float2(1.0, verticalBalance) *
            tapPixels;

        float3 positiveSample =
            sampleSource(
                sourcePixel + tapOffset,
                sourceSize);

        float3 negativeSample =
            sampleSource(
                sourcePixel - tapOffset,
                sourceSize);

        float3 softSample =
            center * 0.50 +
            positiveSample * 0.25 +
            negativeSample * 0.25;

        float scatterMix =
            lerp(0.45, 0.62, broad) *
            rim;

        col = lerp(
            center,
            softSample,
            scatterMix);

        float chromaMix =
            saturate(
                max(uChroma, 0.0) *
                0.28) *
            profile;

        col.r = lerp(
            col.r,
            positiveSample.r,
            chromaMix);

        col.b = lerp(
            col.b,
            negativeSample.b,
            chromaMix);

        localDetail = abs(
            luminance(positiveSample) -
            luminance(negativeSample));
    }

    // Existing user colour controls.
    float originalLum = luminance(col);

    col =
        float3(
            originalLum,
            originalLum,
            originalLum) +
        (
            col -
            float3(
                originalLum,
                originalLum,
                originalLum)
        ) *
        max(satFactor, 0.0) +
        brightAdd;

    col = saturate(col);

    float lum = luminance(col);

    // Approximate Apple's adaptive Regular material:
    //
    // - dark content receives a slight light veil
    // - bright content receives a slight dark veil
    // - detailed backgrounds receive stronger separation
    float separationNeed =
        saturate(
            localDetail * 2.40 +
            rim * 0.08);

    float adaptiveNeutral =
        1.0 -
        smoother01(
            (lum - 0.28) / 0.44);

    float veilStrength =
        (
            0.014 +
            separationNeed * 0.038
        ) *
        lerp(0.95, 1.12, broad);

    col = lerp(
        col,
        float3(
            adaptiveNeutral,
            adaptiveNeutral,
            adaptiveNeutral),
        veilStrength);

    // Fixed environment light from the upper-left.
    //
    // This can later be replaced by a dynamic c16 light vector.
    float2 outwardNormal = -inwardNormal;

    float2 lightDirection =
        normalize(float2(-0.46, -0.89));

    float lightFacing =
        dot(
            outwardNormal,
            lightDirection);

    float highlightFacing =
        saturate(lightFacing);

    float shadowFacing =
        saturate(-lightFacing);

    float specular =
        highlightFacing *
        highlightFacing;

    specular *= specular;
    specular *= rim;

    float opticalShadow =
        shadowFacing *
        shadowFacing *
        shadowFacing *
        rim;

    // Thin adaptive silhouette.
    float hairline =
        1.0 -
        smoother01(
            inside / 1.15);

    float darkBackdropNeed =
        1.0 - lum;

    float brightBackdropNeed =
        lum;

    // Bright edge over dark content.
    col +=
        hairline *
        darkBackdropNeed *
        0.045;

    // Dark edge over bright content.
    col -=
        hairline *
        brightBackdropNeed *
        0.024;

    // Let nearby background colour subtly spill into the highlight.
    float3 ambientSpill =
        saturate(
            center * 1.08 +
            0.02);

    col +=
        specular *
        (
            float3(0.038, 0.038, 0.038) +
            ambientSpill * 0.025
        );

    col -=
        opticalShadow *
        (
            0.018 +
            separationNeed * 0.032
        );

    return float4(
        saturate(col),
        1.0);
}