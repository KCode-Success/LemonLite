// LiquidGlass.hlsl
// 移植自 LiquidGlassDemo D3D11 CompositeThumbPS（ps_3_0 WPF ShaderEffect 版本）。
// 完整对齐原版折射效果：
//   - 3D 胶囊法线 N（中心 0，边缘 1，与原版一致）
//   - innerLensMask（限制折射区域到 thumb 内部）
//   - outerRingMask + ringRefracted（外环强色散）
//   - innerAtten（端点衰减）
//   - shellColor 渐变 + shellShadow
//   - veilWeight 含 outerRingMask 项
//   - lowerShadow 立体感
// 透明度调整：shellColor/veil 改为中性 RGB（去除蓝偏导致的淡紫感），veil 起始值降到 0.20。
// 折射角度修正：backdrop 扩大到 3x 控件区域（控件位于中心 1/3），折射采样能拿到 thumb 外侧的连续背景。
//   BackdropSize: XY=3x 控件 DIP 尺寸(backdrop 尺寸), ZW=控件 DIP 尺寸(控件在 backdrop 中的偏移)
//   main 中 px = uv * BackdropSize.zw（控件坐标），sampleBackdrop 中 backdropUV = (samplePx + BackdropSize.zw) / BackdropSize.xy

sampler2D DesktopTex : register(s0);

float4 TrackColor     : register(c0);  // rgba 0..1, 已 lerp(off, on, fraction)
float4 TrackRect      : register(c1);  // xy=topLeft(px), zw=size(px)
float4 ThumbRect      : register(c2);  // xy=topLeft(px, 缩放前), zw=size(px, 缩放前)
float4 ScaleFracPress : register(c3);  // x=scaleX, y=scaleY, z=fraction, w=pressProgress
float4 ShadowParams   : register(c4);  // xy=offset(px), z=radius(px), w=alpha
float4 GlassParams    : register(c5);  // x=blurRadius, y=refractionAmount, z=innerShadowRadius, w=highlightAlpha(=press)
float4 BackdropSize   : register(c6);  // xy=3x 控件 px 尺寸(backdrop), zw=控件 px 尺寸(offset)

float sdCapsule(float2 p, float2 a, float2 b, float r)
{
    float2 pa = p - a;
    float2 ba = b - a;
    float h = saturate(dot(pa, ba) / dot(ba, ba));
    return length(pa - ba * h) - r;
}

// 在控件 px 位置 samplePx 处采样 backdrop（3x 区域，控件位于中心 1/3）
// samplePx 是控件坐标，转换到 backdrop UV = (samplePx + offset) / backdropSize
float3 sampleBackdrop(float2 samplePx)
{
    float2 safeSize = max(BackdropSize.xy, float2(1.0, 1.0));
    // 控件坐标 -> backdrop 坐标 -> UV
    float2 backdropUV = (samplePx + BackdropSize.zw) / safeSize;
    backdropUV = clamp(backdropUV, float2(0.0, 0.0), float2(1.0, 1.0));
    float3 desktop = tex2D(DesktopTex, backdropUV).rgb;

    // 在采样位置叠加 track 颜色（基于该位置的 track SDF，用控件坐标）
    float trackR = TrackRect.w * 0.5;
    float2 trackA = float2(TrackRect.x + trackR, TrackRect.y + trackR);
    float2 trackB = float2(TrackRect.x + TrackRect.z - trackR, TrackRect.y + trackR);
    float d = sdCapsule(samplePx, trackA, trackB, trackR);
    float aa = smoothstep(0.5, -0.5, d);
    float alpha = aa * TrackColor.a;
    return lerp(desktop, TrackColor.rgb, alpha);
}

// 简化 5-tap blur（中心 + 4 邻），模拟原版 Gaussian
float3 sampleBlur(float2 centerPx, float blurRadius)
{
    float3 c  = sampleBackdrop(centerPx);
    float3 x1 = (sampleBackdrop(centerPx + float2(blurRadius, 0))
               + sampleBackdrop(centerPx - float2(blurRadius, 0))) * 0.5;
    float3 y1 = (sampleBackdrop(centerPx + float2(0, blurRadius))
               + sampleBackdrop(centerPx - float2(0, blurRadius))) * 0.5;
    return (c + x1 + y1) / 3.0;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    // px 是控件坐标（0 = 控件左上角），UV [0,1] 映射到控件 DIP 尺寸
    float2 px = uv * BackdropSize.zw;

    // ---- Track 几何 ----
    float trackR = TrackRect.w * 0.5;
    float2 trackA = float2(TrackRect.x + trackR, TrackRect.y + trackR);
    float2 trackB = float2(TrackRect.x + TrackRect.z - trackR, TrackRect.y + trackR);
    float d_track = sdCapsule(px, trackA, trackB, trackR);
    float trackAa = smoothstep(0.5, -0.5, d_track);

    // ---- Thumb 几何（缩放后）----
    float2 thumbCenter = ThumbRect.xy + ThumbRect.zw * 0.5;
    float2 stretchedSize = max(ThumbRect.zw * ScaleFracPress.xy, float2(1.0, 1.0));
    float2 stretchedTopLeft = thumbCenter - stretchedSize * 0.5;
    float thumbR = max(stretchedSize.y * 0.5, 0.5);
    float2 thumbA = float2(thumbR, thumbR);
    float2 thumbB = float2(stretchedSize.x - thumbR, thumbR);
    float2 thumbLocalPx = px - stretchedTopLeft;
    float d_thumb = sdCapsule(thumbLocalPx, thumbA, thumbB, thumbR);
    float thumbAa = smoothstep(0.5, -0.5, d_thumb);

    // ---- Capsule 3D Normal（与原版完全一致）----
    float2 pa = thumbLocalPx - thumbA;
    float2 ba = thumbB - thumbA;
    float h_seg = saturate(dot(pa, ba) / max(dot(ba, ba), 0.0001));
    float2 closestPoint = thumbA + ba * h_seg;
    float2 distVec = thumbLocalPx - closestPoint;
    float d_center = length(distVec);
    float normalized_d = d_center / max(thumbR, 0.0001);

    // 2D 梯度方向（单位向量）
    float2 grad = (d_center > 0.001) ? (distVec / d_center) : float2(0.0, 0.0);
    // 扁平中心 + 陡峭边缘的 z 高度
    float z = sqrt(max(1.0 - pow(normalized_d, 1.5), 0.0));
    // 3D 法线（关键：N.xy 长度从中心 0 渐变到边缘 ~1，决定折射方向与强度）
    float3 N = normalize(float3(grad.x * normalized_d, grad.y * normalized_d, z));

    // normPos（用于 shellColor 渐变和 grayStroke）
    float2 normPos = (thumbLocalPx - stretchedSize * 0.5) / max(stretchedSize * 0.5, float2(0.0001, 0.0001));

    // ---- Shadow 几何 ----
    float2 shadowTopLeft = stretchedTopLeft + ShadowParams.xy - ShadowParams.z;
    float2 shadowSize = stretchedSize + ShadowParams.z * 2.0;
    float shadowR = thumbR + ShadowParams.z;
    float2 shadowLocalPx = px - shadowTopLeft;
    float2 shadowA = float2(shadowR, shadowR);
    float2 shadowB = float2(shadowSize.x - shadowR, shadowR);
    float d_shadow = sdCapsule(shadowLocalPx, shadowA, shadowB, shadowR);

    // ---- Thumb 玻璃颜色 ----
    float press = saturate(ScaleFracPress.w);
    float fraction = ScaleFracPress.z;
    float blurRadius = max(GlassParams.x, 0.0001);
    float refractionAmount = GlassParams.y;

    // ---- Masks（与原版一致）----
    float innerLensMask = 1.0 - smoothstep(0.54, 0.90, normalized_d);
    float rimMask       = smoothstep(0.64, 0.99, normalized_d);
    float outerRingMask = smoothstep(0.85, 1.00, normalized_d);
    float lensAmount    = smoothstep(0.18, 0.84, press);

    // ---- Inner lens 折射（与原版一致）----
    float refr_strength = (0.006 + (refractionAmount / max(stretchedSize.y, 1.0)) * 0.028) * (0.45 + press * 0.30);
    float disp_spread   = 0.008 + press * 0.015;
    // 端点衰减：纵向左右边缘（|N.x|≈1）的 inner lens 折射减弱到 0.25，
    // 避免滑到轨道极端时端点折射采样到 track 颜色填满整个端点。
    // 中间区域（|N.x|≈0）保持原强度，所以 refrScale 不变。
    float innerAtten    = lerp(1.0, 0.25, smoothstep(0.25, 0.70, abs(N.x)));
    float innerRefr     = refr_strength * innerAtten;
    float innerDisp     = disp_spread * innerAtten;

    // 原版: uv_R = base_uv + N.xy * (innerRefr * (1.0 - innerDisp)) * innerLensMask
    // 原版 refr_strength 是全屏 backbuffer UV 空间偏移，像素偏移 = innerRefr * BackdropTexSize.y(~1080) ≈ 8px
    // WPF 版 sampleBackdrop 接收控件像素坐标，refrScale 把 UV 偏移转换为控件像素偏移
    // refrScale = 860 匹配原版全屏 backbuffer 的视觉比例（中间区域折射强度）
    float refrScale = 860.0;
    float2 innerOffsetR = N.xy * (innerRefr * (1.0 - innerDisp)) * innerLensMask * refrScale;
    float2 innerOffsetG = N.xy *  innerRefr                       * innerLensMask * refrScale;
    float2 innerOffsetB = N.xy * (innerRefr * (1.0 + innerDisp)) * innerLensMask * refrScale;

    float3 refracted;
    refracted.r = sampleBackdrop(px + innerOffsetR).r;
    refracted.g = sampleBackdrop(px + innerOffsetG).g;
    refracted.b = sampleBackdrop(px + innerOffsetB).b;

    // blur（与原版一致：在 screenPx + N.xy * (BlurRadius * 0.35) 处取模糊）
    float3 blurred = sampleBlur(px + N.xy * (blurRadius * 0.35), blurRadius);
    // 减少 blur 混入：原版 0.10 + (1-press)*0.10，改为 0.05 + (1-press)*0.05，减少雾感
    refracted = lerp(refracted, blurred, 0.05 + (1.0 - press) * 0.05);

    // ---- Outer ring 折射（外环强色散，与原版一致）----
    float lrMask    = smoothstep(0.30, 0.85, abs(N.x));
    float ringAtten = lerp(1.0, 0.60, lrMask);
    float ringRefr  = refr_strength * ringAtten;
    float ring_disp = (0.02 + press * 0.03) * ringAtten;

    float2 ringOffsetR = N.xy * (ringRefr * (1.0 - ring_disp)) * outerRingMask * refrScale;
    float2 ringOffsetG = N.xy *  ringRefr                       * outerRingMask * refrScale;
    float2 ringOffsetB = N.xy * (ringRefr * (1.0 + ring_disp)) * outerRingMask * refrScale;

    float3 ringRefracted;
    ringRefracted.r = sampleBackdrop(px + ringOffsetR).r;
    ringRefracted.g = sampleBackdrop(px + ringOffsetG).g;
    ringRefracted.b = sampleBackdrop(px + ringOffsetB).b;
    ringRefracted = lerp(ringRefracted, blurred, 0.05);

    // ---- Lighting（shellColor 改为中性 RGB，去除原版蓝偏导致的淡紫感）----
    // 原版: lerp(float3(0.950, 0.955, 0.965), float3(0.994, 0.996, 0.999), ...)
    // 改为: lerp(float3(0.965, 0.965, 0.965), float3(0.995, 0.995, 0.995), ...) — 纯中性灰
    float3 shellColor = lerp(float3(0.965, 0.965, 0.965), float3(0.995, 0.995, 0.995),
                             saturate(0.62 - normPos.y * 0.54));
    float shellShadow = smoothstep(0.25, 1.0, normPos.y * 0.65 + normPos.x * 0.25 + 0.20);
    shellColor *= 1.0 - shellShadow * 0.10;

    // 静止白色 shell，按压折射镜头
    float3 glassColor = lerp(shellColor, refracted, lensAmount);
    // 外环：纯色散折射（按压时替代白色 shell）
    glassColor = lerp(glassColor, ringRefracted, outerRingMask * lensAmount);

    // 白色面纱：静止时也减弱（0.20→0.12），按压时快速归零，避免按动后残留白雾
    float shellVeil = lerp(0.12, 0.0, smoothstep(0.0, 0.4, press));
    float veilWeight = shellVeil * (0.55 + rimMask * 0.45) * (1.0 - outerRingMask * lensAmount);
    glassColor = lerp(glassColor, float3(0.985, 0.985, 0.985), veilWeight);

    // Fresnel rim
    float fresnel = pow(1.0 - saturate(N.z), 2.4);
    glassColor += fresnel * 0.028;

    // 边缘厚度阴影
    float rimShadow = rimMask * (0.05 + press * 0.05);
    glassColor *= 1.0 - rimShadow;

    // 关闭态时左上半边缘灰色描边（Apple liquid glass）
    float offState = 1.0 - saturate(fraction);
    float upperLeftHalf = saturate(smoothstep(0.3, -0.3, normPos.x + normPos.y));
    float grayStroke = rimMask * upperLeftHalf * press * offState * 0.14;
    glassColor = lerp(glassColor, float3(0.42, 0.42, 0.42), grayStroke);

    // 右下厚度阴影：按压时减弱（原版 0.06 + press*0.05，改为 0.04 + press*0.02），避免按动变暗
    float lowerShadow = exp(-(
        pow((normPos.x - 0.35) / 0.42, 2.0) +
        pow((normPos.y - 0.42) / 0.24, 2.0)
    ));
    glassColor *= 1.0 - lowerShadow * (0.04 + press * 0.02);

    // ---- 合成（z-order: shadow → track → thumb）----
    float4 outColor = float4(0, 0, 0, 0);

    // 1. Shadow
    float shadowAa = 1.0 - smoothstep(-ShadowParams.z, ShadowParams.z, d_shadow);
    float shadowAlpha = shadowAa * ShadowParams.w;
    outColor.rgb = float3(0, 0, 0);
    outColor.a = shadowAlpha;

    // 2. Track (alpha over shadow)
    float trackAlpha = trackAa * TrackColor.a;
    outColor.rgb = lerp(outColor.rgb, TrackColor.rgb, trackAlpha);
    outColor.a = outColor.a + trackAlpha * (1.0 - outColor.a);

    // 3. Thumb (alpha over track)
    float thumbAlpha = thumbAa;
    outColor.rgb = lerp(outColor.rgb, glassColor, thumbAlpha);
    outColor.a = outColor.a + thumbAlpha * (1.0 - outColor.a);

    return outColor;
}
