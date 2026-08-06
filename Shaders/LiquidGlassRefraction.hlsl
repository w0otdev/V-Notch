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
float pointerX         : register(c16); // 0..1 trong notch
float pointerY         : register(c17); // 0..1 trong notch
float pointerActive    : register(c18); // hover/touch: 0..1
float pressAmount      : register(c19); // pressed: 0..1

// Có thể nằm ngoài khoảng 0..1 để mô phỏng nguồn sáng bên ngoài vật liệu.
float lightX           : register(c20);
float lightY           : register(c21);

float interactionRadius: register(c22); // theo chiều cao notch, khoảng 0.5..1.2
float highlightStrength: register(c23); // khoảng 0.6..1.4
float flexStrength     : register(c24); // displacement tính bằng pixel
float pointerVelX      : register(c25);
float pointerVelY      : register(c26);
float releasePulse     : register(c27);
float hoverLift        : register(c28);

float2 safeNormalize(float2 v)
{
    float lengthSquared = dot(v, v);

    if (lengthSquared < 0.000001)
        return float2(0.0, -1.0);

    return v * rsqrt(lengthSquared);
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

    if (geometryValid < 0.5)
        return tex2D(input, saturate(uv));

    float npx = uv.x * srcW;
    float npy = uv.y * srcH;
    
    float2 localPosition = float2(
        npx - notchW * 0.5,
        npy - notchH * 0.5);

    float2 basePixel = float2(npx + offX, npy + offY);
    float2 halfSize = max(notchSize * 0.5, float2(0.5, 0.5));

    float3 field = roundedRectField(
        localPosition,
        halfSize,
        topCornerR,
        bottomCornerR);

    float inside = field.x;
    float2 inwardNormal = field.yz;

    float alpha = saturate(inside - 0.5);
    if (alpha <= 0.0)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float broad = step(0.5, bevelMode);
    float bend = clamp(edgeBend, 0.0, 3.0);

    // -----------------------------------------------------------------
    // Pointer interaction
    // -----------------------------------------------------------------

    float active = saturate(pointerActive);
    float pressed = saturate(pressAmount) * active;

    float2 pointer01 = saturate(float2(pointerX, pointerY));

    float2 pointerLocal =
        pointer01 * notchSize -
        halfSize;

    float2 pointerDelta =
        localPosition -
        pointerLocal;

    float radiusPixels =
        max(
            notchH * max(interactionRadius, 0.05),
            1.0);

    // Lan toả khoảng 30% bề mặt Notch để không bị quá sáng trên view lớn
    float2 interactionScale =
        float2(
            max(notchW * 0.30, radiusPixels * 2.5), 
            max(notchH * 0.60, radiusPixels * 1.5));

    float interactionDistance =
        length(pointerDelta / interactionScale);

    // Dùng smoothstep để gradient sáng mượt hơn thay vì ngắt đột ngột
    float interactionMask = smoothstep(1.0, 0.0, interactionDistance);

    interactionMask *= active;

    float interactionEnergy =
        interactionMask *
        lerp(0.5, 1.0, pressed);

    float2 radialFromPointer =
        safeNormalize(pointerDelta);

    // Flex thay đổi optical normal một lượng rất nhỏ.
    // Không làm méo silhouette thật của element.
    float normalFlex =
        interactionMask *
        pressed *
        max(flexStrength, 0.0) *
        0.055;

    inwardNormal = safeNormalize(
        inwardNormal -
        radialFromPointer * normalFlex);

    // -----------------------------------------------------------------
    // Optical rim
    // -----------------------------------------------------------------

    float sideAxis = abs(inwardNormal.x);
    sideAxis *= sideAxis;
    sideAxis *= sideAxis;

    float rimWidth =
        max(zR, 1.0) *
        (1.0 + 0.22 * bend * sideAxis);

    // Flex làm vùng lens rộng nhẹ tại điểm nhấn.
    float flexRimExpansion =
        interactionMask *
        pressed *
        max(flexStrength, 0.0) *
        0.12;

    float effectiveRimWidth =
        rimWidth +
        flexRimExpansion;

    float rimT =
        saturate(
            inside /
            max(effectiveRimWidth, 0.001));

    float profile =
        lensProfile(rimT, broad);

    float rim =
        1.0 -
        smoother01(
            inside /
            max(effectiveRimWidth, 1.0));

    float refraction = max(uRefr, 0.0);

    float refractionResponse =
        refraction /
        (1.0 + refraction);

    float bendGain =
        min(
            bend * (0.72 + 0.28 * bend),
            1.80);

    float amplitude =
        effectiveRimWidth *
        0.42 *
        refractionResponse *
        bendGain *
        lerp(1.0, 1.08, broad);

    float aspect =
        saturate(
            notchH /
            max(notchW, 1.0) *
            2.5);

    float verticalBalance =
        lerp(0.80, 1.0, aspect);

    float2 axisBalance =
        float2(1.0, verticalBalance);

    float2 displacement =
        -inwardNormal *
        axisBalance *
        amplitude *
        profile;

    // -----------------------------------------------------------------
    // Liquid distortion
    // -----------------------------------------------------------------

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

    // -----------------------------------------------------------------
    // Local flex / dimple (water drop ripple)
    // -----------------------------------------------------------------

    // Khi pressed thay đổi, ripplePhase thay đổi giúp gợn sóng lan ra ngoài
    float ripplePhase = pressed * 3.14159 * 1.5;
    
    // Tạo sóng bằng hàm sin với phase thay đổi, kết hợp fade out theo khoảng cách (interactionMask)
    float dimpleSlope = sin(saturate(interactionDistance) * 3.14159 * 2.0 - ripplePhase) * interactionMask;

    float flexPixels =
        max(flexStrength, 0.0) *
        dimpleSlope *
        lerp(0.5, 4.0, pressed); // Tăng cường độ rõ rệt khi nhấn mạnh

    // Đẩy lưới ảnh ra xa khỏi điểm chạm giống như gợn sóng nước
    displacement +=
        radialFromPointer *
        flexPixels *
        (
            0.40 +
            profile * 0.60
        );

    float2 sourcePixel =
        basePixel +
        displacement;

    float3 center =
        sampleSource(
            sourcePixel,
            sourceSize);

    float3 col = center;
    float localDetail = 0.0;

    // -----------------------------------------------------------------
    // Scattering + chromatic dispersion
    // Tổng tối đa ba texture samples.
    // -----------------------------------------------------------------

    if (rim > 0.001)
    {
        float scatterPixels =
            lerp(0.30, 0.62, broad) *
            rim;

        float chromaPixels =
            min(
                max(uChroma, 0.0) *
                (
                    0.18 +
                    effectiveRimWidth * 0.018
                ),
                1.10) *
            profile;

        float tapPixels =
            scatterPixels +
            chromaPixels;

        float2 tapOffset =
            inwardNormal *
            axisBalance *
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

    // -----------------------------------------------------------------
    // Saturation + brightness
    // -----------------------------------------------------------------

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
        max(satFactor, 0.0);

    col += brightAdd;
    col = saturate(col);

    // -----------------------------------------------------------------
    // Adaptive material based on background
    // -----------------------------------------------------------------

    float centerLum = luminance(center);
    float lum = luminance(col);

    // Derivative của nội dung nền.
    // Không cần thêm texture sample.
    float2 contentGradient =
        float2(
            ddx(centerLum),
            ddy(centerLum));

    float contentGradientLength =
        length(contentGradient);

    float contentEdge =
        saturate(
            contentGradientLength *
            4.5);

    float2 contentDirection =
        safeNormalize(contentGradient);

    float separationNeed =
        saturate(
            localDetail * 2.40 +
            contentEdge * 0.70 +
            rim * 0.08);

    float adaptiveNeutral =
        1.0 -
        smoother01(
            (lum - 0.28) /
            0.44);

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

    // -----------------------------------------------------------------
    // Moving highlight
    // -----------------------------------------------------------------

    // lightX/lightY có thể nằm ngoài 0..1.
    float2 animatedLightLocal =
        float2(lightX, lightY) *
        notchSize -
        halfSize;

    // Khi hover, nguồn sáng tiến dần về pointer.
    float2 lightLocal =
        lerp(
            animatedLightLocal,
            pointerLocal,
            active * 0.82);

    float2 outwardNormal =
        -inwardNormal;

    // Vector từ nguồn sáng đến pixel.
    float2 lightRay =
        safeNormalize(
            localPosition -
            lightLocal);

    float lightFacing =
        saturate(
            dot(
                outwardNormal,
                lightRay));

    float oppositeFacing =
        saturate(
            -dot(
                outwardNormal,
                lightRay));

    float specular =
        pow4(lightFacing) *
        rim *
        max(highlightStrength, 0.0);

    float2 lightDistanceScale =
        max(
            notchSize * float2(0.72, 0.95),
            float2(1.0, 1.0));

    float lightDistance =
        length(
            (
                localPosition -
                lightLocal
            ) /
            lightDistanceScale);

    float localLightFalloff =
        1.0 -
        smoother01(lightDistance);

    specular *=
        lerp(
            0.48,
            1.0,
            localLightFalloff);

    // -----------------------------------------------------------------
    // Touch glow & Water-drop Ripple
    // -----------------------------------------------------------------

    float touchGlow =
        interactionEnergy *
        (
            0.65 +
            0.55 * profile
        ) *
        max(highlightStrength, 1.0) * 1.5; // Khuếch đại mạnh để sáng rõ ràng hơn

    // Glow lấy một phần màu của background để không bị trắng giả.
    float3 ambientTouch =
        saturate(
            center * 0.70 + // Tăng cường độ phản quang từ ảnh nền
            float3(0.55, 0.55, 0.55));

    col +=
        touchGlow *
        (
            float3(0.08, 0.08, 0.10) + // Thêm base color sáng hơn (hơi xanh nhạt/trắng)
            ambientTouch * 0.12
        );

    // Tính toán specular (vệt sáng)
    float touchSpecular =
        interactionMask *
        lerp(0.06, 0.60, pressed) * 
        max(highlightStrength, 0.0);

    float rimSpecular =
        pow4(lightFacing) *
        rim *
        interactionMask *
        lerp(0.15, 0.80, pressed) * 
        max(highlightStrength, 0.0);

    col +=
        (touchSpecular + rimSpecular) *
        ambientTouch;

    // -----------------------------------------------------------------
    // Shadow reacts to background content
    // -----------------------------------------------------------------

    float contentAlignment =
        dot(
            outwardNormal,
            contentDirection);

    // Background sáng dần theo hướng normal:
    // tăng dark rim để giữ separation.
    float contentDarkRim =
        saturate(contentAlignment) *
        contentEdge *
        rim;

    // Background tối dần theo hướng normal:
    // tăng light rim.
    float contentLightRim =
        saturate(-contentAlignment) *
        contentEdge *
        rim;

    col -=
        contentDarkRim *
        (
            0.026 +
            lum * 0.026
        );

    col +=
        contentLightRim *
        (
            0.016 +
            (1.0 - lum) * 0.018
        );

    float opticalShadow =
        oppositeFacing *
        oppositeFacing *
        oppositeFacing *
        rim;

    // Điểm chạm làm shadow mềm và mở ra như vật liệu đang flex.
    float shadowSuppression =
        1.0 -
        touchGlow * 0.58;

    float shadowStrength =
        (
            0.014 +
            lum * 0.014 +
            separationNeed * 0.032
        ) *
        shadowSuppression;

    col -=
        opticalShadow *
        shadowStrength;

    // -----------------------------------------------------------------
    // Adaptive silhouette
    // -----------------------------------------------------------------

    float hairline =
        1.0 -
        smoother01(
            inside / 1.15);

    float darkBackdropNeed =
        1.0 - lum;

    float brightBackdropNeed =
        lum;

    col +=
        hairline *
        darkBackdropNeed *
        0.042;

    col -=
        hairline *
        brightBackdropNeed *
        0.022;

    // Main moving specular.
    float3 ambientSpill =
        saturate(
            center * 1.08 +
            0.02);

    col +=
        specular *
        (
            float3(0.032, 0.032, 0.032) +
            ambientSpill * 0.032
        );

    return float4(
        saturate(col) * alpha,
        alpha);
}