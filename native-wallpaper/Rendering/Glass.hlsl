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
    float4 Tone;               // saturation, brightness, contrast, RGB separation
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
        : Background.Sample(LinearClamp, uv).rgb;
}

Vertex BackgroundVertex(uint id : SV_VertexID)
{
    Vertex v;
    v.position = float4(id == 1 ? 3 : -1, id == 2 ? -3 : 1, 0, 1);
    v.local = 0;
    v.index = 0;
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
    return v;
}

float SignedDistance(float2 p, float2 halfSize, float radius)
{
    float2 q = abs(p) - (halfSize - radius);
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
}

float3 SampleGlassBackground(float2 pixel, float radius)
{
    if (BlurPass.w == 0) return SampleBackground(pixel);

    float2 uv = pixel / View.xy;
    if (BlurPass.w == 1) return BlurTexture.Sample(LinearClamp, uv).rgb;

    // Read overlapping, prefiltered regions along a rotated square to soften the mip texel grid.
    float2 first = float2(.27, .13) * radius / View.xy;
    float2 second = float2(-.13, .27) * radius / View.xy;
    float level = BlurPass.z;
    float3 color = .25 * (BlurTexture.SampleLevel(LinearClamp, uv - first, level).rgb +
                          BlurTexture.SampleLevel(LinearClamp, uv + first, level).rgb +
                          BlurTexture.SampleLevel(LinearClamp, uv - second, level).rgb +
                          BlurTexture.SampleLevel(LinearClamp, uv + second, level).rgb);
    // Blend into the half-resolution prefilter at the smallest radii.
    return radius < 2 ? lerp(SampleBackground(pixel), color, radius * .5) : color;
}

float3 SampleGlass(float2 pixel, float2 displacement, float scale)
{
    float radius = Optics.z * scale;
    float3 color = SampleGlassBackground(pixel, radius);
    if (Tone.w > 0 && Appearance.w > 0)
    {
        float2 shift = displacement * 0.055;
        color.r = SampleGlassBackground(pixel - shift, radius).r;
        color.b = SampleGlassBackground(pixel + shift, radius).b;
    }
    return color;
}

float4 GlassPixel(Vertex v, bool overlay)
{
    float4 text = Text[v.index];
    float strength = lerp(1, lerp(Depth.x, 1, text.z), Depth.y) * Appearance.w;
    float2 halfSize = Cards[v.index].zw;
    float scale = View.y / 1080.0;
    // The rounded corner must stay outside the rectangle occupied by the glyphs.
    float radius = max(0, min(min(Optics.w * scale, Depth.z * 3.41421356), min(halfSize.x, halfSize.y) - 1));
    float2 q = abs(v.local) - (halfSize - radius);
    float distance = SignedDistance(v.local, halfSize, radius);
    float coverage = saturate(0.5 - distance);
    float shadow = 0;
    if (Shadow.a > 0)
    {
        float shadowEdge = SignedDistance(v.local - ShadowShape.yz * scale, halfSize, radius);
        shadow = saturate(1 - shadowEdge / max(1, ShadowShape.x * scale));
        shadow *= shadow * Shadow.a * strength;
    }
    if (coverage <= 0 && shadow <= 0) discard;
    float2 edge = max(q, 0);
    if (dot(edge, edge) < 0.0001)
        edge = q.x > q.y ? float2(1, 0) : float2(0, 1);
    float2 normal = normalize(edge) * sign(v.local);
    float rim = Optics.x > 0 ? saturate(1 + distance / max(1.0, Optics.x * strength * scale)) : 0;
    float press = saturate(Depth.w);
    float2 displacement = Optics.y * (1 + .25 * press) * strength * rim * rim * normal * scale;

    float shadowAlpha = saturate(shadow);
    if (overlay && coverage <= 0) return float4(Shadow.rgb * shadowAlpha, shadowAlpha);
    float3 original = lerp(SampleBackground(v.position.xy), Shadow.rgb, shadowAlpha);
    if (coverage <= 0) return float4(original, 1);
    float3 refracted = SampleGlass(v.position.xy - displacement, displacement, scale);
    float gray = dot(refracted, float3(.2126, .7152, .0722));
    float3 corrected = saturate((lerp(gray, refracted, Tone.x) - 0.5) * Tone.z + 0.5 + Tone.y);
    refracted = lerp(refracted, corrected, saturate(Appearance.w));
    float direction = dot(normal, float2(cos(HighlightShape.x), sin(HighlightShape.x)));
    float lighting = 0.18 + 0.82 * pow(saturate(direction), HighlightShape.y);
    if (HighlightShape.w == 1) lighting = 0.35 + 0.65 * pow(abs(direction), HighlightShape.y);
    if (HighlightShape.w == 2) lighting = 1;
    float shine = HighlightColor.a * strength * saturate(HighlightShape.z * scale + distance) * lighting;
    float3 glass = lerp(refracted, Tint.rgb, saturate(Tint.a * strength));
    glass = lerp(glass, Surface.rgb, saturate(Surface.a * strength));
    glass = lerp(glass, HighlightColor.rgb, saturate(shine));
    glass = lerp(glass, float3(1, 1, 1), press * (.035 + .075 * rim));

    float2 glyph = (v.local - displacement * ShadowShape.w) / text.xy + 0.5;
    float4 bounds = GlyphUv[v.index];
    float2 uv = bounds.xy + saturate(glyph) * bounds.zw;
    float mask = Lyrics.Sample(LinearClamp, uv) * text.z;
    if (any(glyph < 0) || any(glyph > 1))
        mask = 0;
    glass = lerp(glass, Appearance.rgb, mask);
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
