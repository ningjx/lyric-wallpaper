Texture2D<float4> Background : register(t0);
Texture2D<float> Lyrics : register(t1);
Texture2D<float4> BlurTexture : register(t2);
SamplerState LinearClamp : register(s0);

cbuffer Scene : register(b0)
{
    float4 View;               // target size, source UV scale (cover)
    float4 Cards[16];          // center XY, half extents
    float4 Text[16];           // visible text size XY, opacity
    float4 Appearance;         // lyric color and glass intensity
    float4 BackgroundTransform; // zoom, XY offset, tile
    float4 Optics;             // height, displacement, blur, corner radius
    float4 Tone;               // saturation, brightness, contrast, dispersion mode
    float4 HighlightColor;
    float4 HighlightShape;     // angle, falloff, edge width, mode
    float4 Tint;
    float4 Surface;
    float4 Shadow;
    float4 ShadowShape;        // radius, XY offset, lyrics behind glass
    float4 Depth;              // far glass opacity, depth enabled, card padding, clock press
    float4 BlurPass;           // downsampled target dimensions, mip level, mode (off, Gaussian, mip)
    float4 GlyphUv[16];        // atlas origin and extent of the visible glyphs
    float4 PreviewWindow;      // scene origin and scene pixels per preview pixel
    float4 LensShape;
    float4 InnerShadow;
    float4 MaterialOptions;
    float4 WallpaperLightDirection[4];
    float4 WallpaperLightColor[4];
};

cbuffer BlurKernel : register(b1)
{
    float4 BlurParameters;     // center weight, pair count, downsample grid size
    float4 BlurPairs[8];        // offset in texels, normalized weight of two adjacent taps
};

struct Vertex
{
    float4 position : SV_Position;
    float2 local : TEXCOORD0;
    nointerpolation uint index : TEXCOORD1;
    nointerpolation float3 ink : TEXCOORD2;
};

float2 ScreenToUv(float2 p)
{
    return ((p / View.xy - 0.5 - BackgroundTransform.yz) / BackgroundTransform.x) * View.zw + 0.5;
}

float3 SampleBackground(float2 pixel)
{
    float2 uv = ScreenToUv(pixel);
    if (BackgroundTransform.w > 0) uv = frac(uv);
    return any(uv < 0) || any(uv > 1)
        ? float3(0.035, 0.055, 0.075)
        : Background.SampleLevel(LinearClamp, uv, 0).rgb;
}

Vertex BackgroundVertex(uint id : SV_VertexID)
{
    Vertex v;
    v.position = float4(id == 1 ? 3 : -1, id == 2 ? -3 : 1, 0, 1);
    v.local = 0;
    v.index = 0;
    v.ink = 0;
    return v;
}

float4 BackgroundPixel(Vertex v) : SV_Target
{
    return float4(SampleBackground(v.position.xy), 1);
}

float4 PreviewBackgroundPixel(Vertex v) : SV_Target
{
    return float4(SampleBackground(PreviewWindow.xy + v.position.xy * PreviewWindow.zw), 1);
}

float4 EditorBackdropPixel(Vertex v) : SV_Target
{
    static const float weights[5] = { 1, 4, 6, 4, 1 };
    float2 pixel = v.position.xy * PreviewWindow.zw;
    float spacing = View.y * .011;
    float3 color = 0;
    [unroll] for (int row = 0; row < 5; row++)
    {
        [unroll] for (int column = 0; column < 5; column++)
        {
            float2 samplePixel = clamp(pixel + float2(column - 2, row - 2) * spacing, 0.5, View.xy - 0.5);
            color += SampleBackground(samplePixel) * weights[column] * weights[row];
        }
    }
    color /= 256;
    float luminance = dot(color, float3(.2126, .7152, .0722));
    return float4(lerp(lerp(luminance, color, .68), float3(.98, .985, 1), .79), 1);
}

float4 BlurDownsample(Vertex v) : SV_Target
{
    int grid = (int)BlurParameters.z;
    float2 footprint = View.xy / BlurPass.xy;
    float2 spacing = footprint / grid;
    float2 first = (v.position.xy - 0.5) * footprint + 0.5 * spacing;
    float3 color = 0;
    [loop] for (int y = 0; y < grid; ++y)
    {
        [loop] for (int x = 0; x < grid; ++x)
            color += SampleBackground(first + float2(x, y) * spacing);
    }
    return float4(color / (grid * grid), 1);
}

float4 BlurAxis(Vertex v, bool horizontal) : SV_Target
{
    float2 uv = v.position.xy / BlurPass.xy;
    float2 stepUv = horizontal ? float2(1 / BlurPass.x, 0) : float2(0, 1 / BlurPass.y);
    float3 color = BlurTexture.SampleLevel(LinearClamp, uv, 0).rgb * BlurParameters.x;
    [loop] for (int pair = 0; pair < (int)BlurParameters.y; ++pair)
    {
        float2 offset = stepUv * BlurPairs[pair].x;
        color += (BlurTexture.SampleLevel(LinearClamp, uv - offset, 0).rgb +
                  BlurTexture.SampleLevel(LinearClamp, uv + offset, 0).rgb) * BlurPairs[pair].y;
    }
    return float4(color, 1);
}

float4 BlurHorizontal(Vertex v) : SV_Target { return BlurAxis(v, true); }
float4 BlurVertical(Vertex v) : SV_Target { return BlurAxis(v, false); }

Vertex CardVertex(uint id : SV_VertexID, uint instance : SV_InstanceID)
{
    static const float2 corners[6] = {
        float2(-1, -1), float2(1, -1), float2(-1, 1),
        float2(-1, 1), float2(1, -1), float2(1, 1)
    };
    Vertex v;
    float2 extra = Shadow.a > 0 ? (ShadowShape.x + abs(ShadowShape.yz)) * View.y / 1080.0 : 0;
    v.local = corners[id] * (Cards[instance].zw + extra);
    float2 pixel = Cards[instance].xy + v.local;
    v.position = float4(pixel / View.xy * float2(2, -2) + float2(-1, 1), 0, 1);
    v.index = instance;
    v.ink = Appearance.rgb;
    return v;
}

float ShapeDistance(float2 p, float2 halfSize, float radius, bool continuous, out float2 normal)
{
    float extent = continuous ? min(radius * 1.4, min(halfSize.x, halfSize.y)) : radius;
    float2 q = abs(p) - (halfSize - extent);
    if (min(q.x, q.y) <= 0)
    {
        normal = sign(p) * (q.x > q.y ? float2(1, 0) : float2(0, 1));
        return max(q.x, q.y) - extent;
    }
    if (!continuous || extent <= 0)
    {
        float distance = length(q);
        normal = sign(p) * q / max(distance, 1e-5);
        return distance - extent;
    }

    float axis = max(q.x, q.y);
    float2 corner = q / axis;
    float2 square = corner * corner;
    float norm = pow(dot(square, corner), 1.0 / 3.0);
    float gradientLength = length(square);
    normal = sign(p) * square / gradientLength;
    return (axis * norm - extent) * norm * norm / gradientLength;
}

float2 LensOffset(float2 local, float2 normal, float rim, float amount)
{
    if (rim <= 0 || amount == 0) return 0;
    if (LensShape.x > 0)
    {
        float2 radial = local * rsqrt(max(dot(local, local), 1e-8));
        float2 direction = normal + radial;
        normal = direction * rsqrt(max(dot(direction, direction), 1e-8));
    }
    return normal * amount;
}

float LensBend(float rim) { return 1 - sqrt(saturate(1 - rim * rim)); }

float3 ApplyRimHighlight(float3 glass, float distance, float2 normal, float scale, float strength)
{
    float opacity = HighlightColor.a * strength;
    float width = HighlightShape.z * scale;
    float softness = max(0.5, width * 0.5);
    if (opacity <= 0 || distance <= -width - softness) return glass;

    float band = 1 - smoothstep(max(0.0, width - softness), width + softness, -distance);
    if (HighlightShape.w == 3)
    {
        float3 light = 0;
        [unroll] for (int source = 0; source < 4; source++)
        {
            float4 probe = WallpaperLightDirection[source];
            float facing = pow(saturate(dot(normal, probe.xy)), HighlightShape.y);
            float intensity = lerp(.25, .08 + .92 * facing, probe.z);
            light += WallpaperLightColor[source].rgb * intensity * probe.w;
        }
        return saturate(glass + light * saturate(opacity * band));
    }
    float direction = dot(normal, float2(cos(HighlightShape.x), sin(HighlightShape.x)));
    float intensity = HighlightShape.w == 2 ? 1 : pow(saturate(abs(direction)), HighlightShape.y);
    float alpha = saturate(opacity * band * intensity);
    if (HighlightShape.w == 1)
        return lerp(glass, HighlightColor.rgb * step(0, direction), alpha * 0.38);
    return saturate(glass + HighlightColor.rgb * alpha);
}

float InnerShadowMask(float2 local, float2 halfSize, float radius, bool continuous, float distance, float scale)
{
    float softness = max(0.5, InnerShadow.y * scale * 1.5);
    float2 offset = InnerShadow.zw * scale;
    if (InnerShadow.x <= 0 || !any(offset) || distance < -softness - length(offset)) return 0;

    float2 unused;
    float shifted = ShapeDistance(local - offset, halfSize, radius, continuous, unused);
    return saturate(smoothstep(-softness, softness, shifted) - smoothstep(-softness, softness, distance));
}

float3 SampleGlassBackground(float2 pixel, float radius, float blurWeight)
{
    if (blurWeight <= 0) return SampleBackground(pixel);

    float2 uv = pixel / View.xy;
    if (BlurPass.w == 1)
    {
        float3 blurred = BlurTexture.SampleLevel(LinearClamp, uv, 0).rgb;
        return blurWeight >= 1 ? blurred : lerp(SampleBackground(pixel), blurred, blurWeight);
    }

    radius *= blurWeight;
    // Read overlapping, prefiltered regions along a rotated square to soften the mip texel grid.
    float2 first = float2(.27, .13) * radius / View.xy;
    float2 second = float2(-.13, .27) * radius / View.xy;
    float level = blurWeight >= 1 ? BlurPass.z : max(0, BlurPass.z + log2(blurWeight));
    float3 color = .25 * (BlurTexture.SampleLevel(LinearClamp, uv - first, level).rgb +
                          BlurTexture.SampleLevel(LinearClamp, uv + first, level).rgb +
                          BlurTexture.SampleLevel(LinearClamp, uv - second, level).rgb +
                          BlurTexture.SampleLevel(LinearClamp, uv + second, level).rgb);
    // Blend into the half-resolution prefilter at the smallest radii.
    return radius < 2 ? lerp(SampleBackground(pixel), color, radius * .5) : color;
}

float3 SampleGlass(float2 pixel, float2 offset, float rim, float radius, float blurWeight)
{
    float3 center = SampleGlassBackground(pixel, radius, blurWeight);
    [branch] if (Tone.w == 0) return center;

    // Dispersion follows the rim independently of the bend, including the straight edges.
    float2 spread = offset * smoothstep(0, 1, rim) * (Tone.w == 2 ? .42 : .22);
    [branch] if (dot(spread, spread) <= .0025) return center;
    [branch] if (Tone.w == 2)
    {
        float  red    = SampleGlassBackground(pixel - spread, radius, blurWeight).r;
        float2 orange = SampleGlassBackground(pixel - spread * (2.0 / 3.0), radius, blurWeight).rg;
        float2 yellow = SampleGlassBackground(pixel - spread / 3.0, radius, blurWeight).rg;
        float2 cyan   = SampleGlassBackground(pixel + spread / 3.0, radius, blurWeight).gb;
        float  blue   = SampleGlassBackground(pixel + spread * (2.0 / 3.0), radius, blurWeight).b;
        float2 purple = SampleGlassBackground(pixel + spread, radius, blurWeight).rb;
        return float3((2 * (red + orange.r + yellow.r) + purple.x) / 7.0,
                      (orange.g + 2 * (yellow.g + center.g + cyan.x)) / 7.0,
                      (cyan.y + blue + purple.y) / 3.0);
    }
    center.r = SampleGlassBackground(pixel - spread, radius, blurWeight).r;
    center.b = SampleGlassBackground(pixel + spread, radius, blurWeight).b;
    return center;
}

float3 ApplyTint(float3 color, float strength)
{
    float alpha = saturate(Tint.a * strength);
    if (alpha <= 0) return color;
    if (MaterialOptions.y <= 0) return lerp(color, Tint.rgb, alpha);

    float tintMin = min(Tint.r, min(Tint.g, Tint.b));
    float tintRange = max(Tint.r, max(Tint.g, Tint.b)) - tintMin;
    if (tintRange > 1e-5)
    {
        float low = min(color.r, min(color.g, color.b));
        float range = max(color.r, max(color.g, color.b)) - low;
        float3 hue = (Tint.rgb - tintMin) / tintRange;
        color = lerp(color, low + hue * range, alpha);
    }
    return lerp(color, Tint.rgb, 0.75 * alpha);
}

float RelativeLuminance(float3 color)
{
    float3 linearRgb = color * (color * (color * .305306011 + .682171111) + .012522878);
    return dot(linearRgb, float3(.2126, .7152, .0722));
}

float GlyphMask(float2 coord, float4 bounds)
{
    if (any(coord < 0) || any(coord > 1)) return 0;
    return Lyrics.SampleLevel(LinearClamp, bounds.xy + coord * bounds.zw, 0);
}

float3 CompositeText(float3 glass, float2 glyph, float4 bounds, float3 text, float3 inkColor, float scale)
{
    float2 stroke = max(0.8, 1.5 * scale) / text.xy;
    if (any(glyph < -stroke) || any(glyph > 1 + stroke)) return glass;

    float mask = GlyphMask(glyph, bounds);
    [branch] if (MaterialOptions.z > 0 && mask < .95 && text.z > 0)
    {
        float ink = RelativeLuminance(inkColor);
        float backdrop = RelativeLuminance(glass);
        float contrast = (max(ink, backdrop) + .05) / (min(ink, backdrop) + .05);
        float need = 1 - smoothstep(1.4, 4.5, contrast);
        [branch] if (need > 0)
        {
            float horizontal = max(GlyphMask(glyph - float2(stroke.x, 0), bounds),
                                   GlyphMask(glyph + float2(stroke.x, 0), bounds));
            float vertical = max(GlyphMask(glyph - float2(0, stroke.y), bounds),
                                 GlyphMask(glyph + float2(0, stroke.y), bounds));
            float outline = max(horizontal, vertical) * (1 - smoothstep(.55, .95, mask));
            float keyline = 1 - step(.179, ink);
            glass = lerp(glass, keyline, outline * need * text.z * .85);
        }
    }
    return lerp(glass, inkColor, mask * text.z);
}

float CardRadius(float2 halfSize, float scale)
{
    return max(0, min(min(Optics.w * scale, Depth.z * 3.41421356), min(halfSize.x, halfSize.y) - 1));
}

float GlassBlurWeight(float distance, float lensHeight, float halfHeight, float scale)
{
    if (BlurPass.w == 0) return 0;
    if (MaterialOptions.x <= 0) return 1;
    float edgeWidth = min(max(1, lensHeight * .6), halfHeight * .3);
    float centerWidth = max(edgeWidth + .5, min(halfHeight * .8, edgeWidth + 14 * scale));
    return smoothstep(edgeWidth, centerWidth, -distance);
}

float3 GlassSurface(float3 refracted, float strength, float press, float rim)
{
    float gray = dot(refracted, float3(.2126, .7152, .0722));
    float3 corrected = saturate((lerp(gray, refracted, Tone.x) - 0.5) * Tone.z + 0.5 + Tone.y);
    refracted = lerp(refracted, corrected, saturate(Appearance.w));
    float3 glass = ApplyTint(refracted, strength);
    glass = lerp(glass, Surface.rgb, saturate(Surface.a * strength));
    return lerp(glass, float3(1, 1, 1), press * (.035 + .075 * rim));
}

Vertex WidgetVertex(uint id : SV_VertexID, uint instance : SV_InstanceID)
{
    Vertex v = CardVertex(id, instance);
    [branch] if (MaterialOptions.w > 0)
    {
        float4 card = Cards[instance];
        float2 stepPx = Text[instance].xy * (1.0 / 3.0);
        float scale = View.y / 1080.0;
        float radius = CardRadius(card.zw, scale);
        float lensHeight = Optics.x * Appearance.w * scale;
        float luminance = 0;
        [loop] for (int row = -1; row <= 1; row++)
        {
            [loop] for (int column = -1; column <= 1; column++)
            {
                float2 local = float2(column, row) * stepPx;
                float2 normal;
                float distance = ShapeDistance(local, card.zw, radius, LensShape.y > 0, normal);
                float rim = lensHeight > 0 ? saturate(1 + distance / max(lensHeight, 1e-5)) : 0;
                float press = saturate(Depth.w);
                float2 shift = LensOffset(local, normal, rim, Optics.y * (1 + .25 * press) * Appearance.w * scale) * LensBend(rim);
                float blurWeight = GlassBlurWeight(distance, lensHeight, card.w, scale);
                float3 backdrop = SampleGlassBackground(card.xy + local + shift, Optics.z * scale, blurWeight);
                luminance += RelativeLuminance(GlassSurface(backdrop, Appearance.w, press, rim));
            }
        }
        v.ink = 1 - step(.179, luminance / 9.0);
    }
    return v;
}

float4 GlassPixel(Vertex v, bool overlay)
{
    float4 text = Text[v.index];
    float strength = lerp(1, lerp(Depth.x, 1, text.z), Depth.y) * Appearance.w;
    float2 halfSize = Cards[v.index].zw;
    float scale = View.y / 1080.0;
    // The rounded corner must stay outside the rectangle occupied by the glyphs.
    float radius = CardRadius(halfSize, scale);
    bool continuous = LensShape.y > 0;
    float2 normal;
    float distance = ShapeDistance(v.local, halfSize, radius, continuous, normal);
    float coverage = saturate(0.5 - distance);
    float shadow = 0;
    if (Shadow.a > 0)
    {
        float2 unused;
        float shadowEdge = ShapeDistance(v.local - ShadowShape.yz * scale, halfSize, radius, continuous, unused);
        shadow = saturate(1 - shadowEdge / max(1, ShadowShape.x * scale));
        shadow *= shadow * Shadow.a * strength;
    }
    if (coverage <= 0 && shadow <= 0) discard;
    float shadowAlpha = saturate(shadow);
    if (overlay && coverage <= 0) return float4(Shadow.rgb * shadowAlpha, shadowAlpha);
    float3 original = lerp(SampleBackground(v.position.xy), Shadow.rgb, shadowAlpha);
    if (coverage <= 0) return float4(original, 1);
    float lensHeight = Optics.x * strength * scale;
    float rim = lensHeight > 0 ? saturate(1 + distance / max(lensHeight, 1e-5)) : 0;
    float press = saturate(Depth.w);
    float2 offset = LensOffset(v.local, normal, rim, Optics.y * (1 + .25 * press) * strength * scale);
    float2 displacement = offset * LensBend(rim);
    float blurWeight = GlassBlurWeight(distance, lensHeight, halfSize.y, scale);
    float3 refracted = SampleGlass(v.position.xy + displacement, offset, rim, Optics.z * scale, blurWeight);
    float3 glass = GlassSurface(refracted, strength, press, rim);

    float2 glyph = (v.local + displacement * ShadowShape.w) / text.xy + 0.5;
    float4 bounds = GlyphUv[v.index];
    if (ShadowShape.w > 0) glass = CompositeText(glass, glyph, bounds, text.xyz, v.ink, scale);
    glass *= 1 - saturate(InnerShadow.x * strength * InnerShadowMask(v.local, halfSize, radius, continuous, distance, scale));
    glass = ApplyRimHighlight(glass, distance, normal, scale, strength);
    if (ShadowShape.w == 0) glass = CompositeText(glass, glyph, bounds, text.xyz, v.ink, scale);
    if (overlay)
    {
        float visibleShadow = shadowAlpha * (1 - coverage);
        return float4(glass * coverage + Shadow.rgb * visibleShadow, coverage + visibleShadow);
    }
    return float4(lerp(original, glass, coverage), 1);
}

float4 CardPixel(Vertex v) : SV_Target { return GlassPixel(v, false); }
float4 WidgetPixel(Vertex v) : SV_Target { return GlassPixel(v, true); }

float4 PreviewWidgetPixel(Vertex v) : SV_Target
{
    v.position.xy = PreviewWindow.xy + v.position.xy * PreviewWindow.zw;
    return GlassPixel(v, true);
}
