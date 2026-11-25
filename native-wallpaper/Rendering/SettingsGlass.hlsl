// Native HLSL adaptation of the liquid-glass-webgl control optics (Apache-2.0).
// The base control layer is copied before the lens pass so moving glass never samples itself.
// All textures and output use premultiplied alpha for composition over the system backdrop.
Texture2D<float4> Source : register(t0);
SamplerState LinearClamp : register(s0);

cbuffer Settings : register(b0)
{
    float4 View;       // pixels, pixels, device scale, unused
    float4 Bounds;     // center and unscaled half extents in pixels
    float4 Clip;       // scrolled viewport in pixels
    float4 Surface;    // straight RGBA
    float4 Optics;     // corner radius, lens height, lens amount, local blur radius
    float4 Shape;      // XY scale, highlight, seven-path dispersion
    float4 Shadow;     // inner radius, inner alpha, outer radius, outer alpha
    float4 Sampling;   // content scale XY, combined backdrop type, unused
    float4 Track;      // scaled track center and half-size in pixels
    float4 TrackColor;
};

float4 Fullscreen(uint id : SV_VertexID) : SV_Position
{
    return float4(id == 1 ? 3 : -1, id == 2 ? -3 : 1, 0, 1);
}

float4 ShapeVertex(uint id : SV_VertexID) : SV_Position
{
    static const float2 corners[6] = {
        float2(-1,-1), float2(1,-1), float2(-1,1), float2(-1,1), float2(1,-1), float2(1,1)
    };
    float2 p = Bounds.xy + corners[id] * (Bounds.zw * Shape.xy + Shadow.z * 3 + 2);
    return float4(p / View.xy * float2(2,-2) + float2(-1,1), 0, 1);
}

float Distance(float2 local, float2 halfSize, float radius)
{
    float2 q = abs(local) - halfSize + radius;
    return min(max(q.x, q.y), 0) + length(max(q, 0)) - radius;
}

float2 Gradient(float2 local, float2 halfSize, float radius)
{
    float2 q = abs(local) - halfSize + radius;
    float2 outer = max(q, 0);
    if (dot(outer, outer) > 1e-8) return normalize(outer) * sign(local);
    return (q.x > q.y ? float2(1, 0) : float2(0, 1)) * sign(local);
}

float4 Paint(float4 p : SV_Position) : SV_Target
{
    if (any(p.xy < Clip.xy) || any(p.xy >= Clip.zw)) discard;
    float2 local = (p.xy - Bounds.xy) / Shape.xy;
    float alpha = Surface.a * saturate(.5 - Distance(local, Bounds.zw, Optics.x));
    return float4(Surface.rgb * alpha, alpha);
}

float4 Overlay(float4 p : SV_Position) : SV_Target
{
    return Source.SampleLevel(LinearClamp, p.xy / View.xy, 0);
}

float4 AlphaOver(float4 foreground, float4 background)
{
    return foreground + background * (1 - foreground.a);
}

float4 SampleDisc(float2 pixel, float blur)
{
    float2 uv = pixel / View.xy;
    float4 center = Source.SampleLevel(LinearClamp, uv, 0);
    if (blur < .5) return center;
    float2 r = blur / View.xy;
    const float d = .70710678;
    return center * .25 +
        (Source.SampleLevel(LinearClamp, uv + float2(r.x, 0), 0) +
         Source.SampleLevel(LinearClamp, uv - float2(r.x, 0), 0) +
         Source.SampleLevel(LinearClamp, uv + float2(0, r.y), 0) +
         Source.SampleLevel(LinearClamp, uv - float2(0, r.y), 0)) * .12 +
        (Source.SampleLevel(LinearClamp, uv + d * r, 0) +
         Source.SampleLevel(LinearClamp, uv - d * r, 0) +
         Source.SampleLevel(LinearClamp, uv + d * r * float2(-1, 1), 0) +
         Source.SampleLevel(LinearClamp, uv + d * r * float2(1, -1), 0)) * .0675;
}

float4 SampleGlass(float2 pixel, float blur)
{
    if (Sampling.z == 1)
    {
        float edge = Distance(pixel - Track.xy, Track.zw, min(Track.z, Track.w));
        float alpha = (1 - smoothstep(-max(.5, blur), max(.5, blur), edge)) * TrackColor.a;
        return float4(TrackColor.rgb * alpha, alpha);
    }
    if (Sampling.z == 2) pixel = Bounds.xy + (pixel - Bounds.xy) * Sampling.xy;
    return SampleDisc(pixel, blur);
}

float4 Spectral(float2 pixel, float2 spread)
{
    float4 red    = SampleGlass(pixel + spread, Optics.w);
    float4 orange = SampleGlass(pixel + spread * (2.0 / 3.0), Optics.w);
    float4 yellow = SampleGlass(pixel + spread / 3.0, Optics.w);
    float4 green  = SampleGlass(pixel, Optics.w);
    float4 cyan   = SampleGlass(pixel - spread / 3.0, Optics.w);
    float4 blue   = SampleGlass(pixel - spread * (2.0 / 3.0), Optics.w);
    float4 purple = SampleGlass(pixel - spread, Optics.w);
    float2 r = (2 * (red.ra + orange.ra + yellow.ra) + purple.ra) / 7.0;
    float2 g = (orange.ga + 2 * (yellow.ga + green.ga + cyan.ga)) / 7.0;
    float2 b = (cyan.ba + blue.ba + purple.ba) / 3.0;
    return float4(r.x, g.x, b.x, max(r.y, max(g.y, b.y)));
}

float4 LensShadow(float4 p : SV_Position) : SV_Target
{
    if (any(p.xy < Clip.xy) || any(p.xy >= Clip.zw) || Shadow.z <= 0 || Shadow.w <= 0) discard;
    float2 local = (p.xy - Bounds.xy) / Shape.xy;
    float dist = Distance(local, Bounds.zw, Optics.x);
    float coverage = saturate(.5 - dist * min(Shape.x, Shape.y));
    float edge = max(.5, Distance(local - float2(0, Shadow.z / 6), Bounds.zw, Optics.x));
    float outer = exp(-.5 * pow(edge / max(.5, Shadow.z), 2)) * Shadow.w * (1 - coverage);
    return float4(0, 0, 0, outer);
}

float4 Lens(float4 p : SV_Position) : SV_Target
{
    if (any(p.xy < Clip.xy) || any(p.xy >= Clip.zw)) discard;
    float2 local = (p.xy - Bounds.xy) / Shape.xy;
    float dist = Distance(local, Bounds.zw, Optics.x);
    float coverage = saturate(.5 - dist * min(Shape.x, Shape.y));
    if (coverage == 0) discard;

    float2 normal = Gradient(local, Bounds.zw, min(Optics.x * 1.5, min(Bounds.z, Bounds.w)));
    float4 glass;
    if (Sampling.z == 3)
    {
        float2 cell = floor((local + Bounds.zw) / (5 * View.z));
        float parity = frac((cell.x + cell.y) * .5) * 2;
        float3 checker = lerp(float3(252, 253, 254), float3(220, 226, 233), parity) / 255;
        glass = float4(checker, 1);
    }
    else
    {
        float rim = saturate(1 + min(dist, 0) / max(.001, Optics.y));
        // AGSL circleMap, evaluated in the original lens profile before the layer is stretched.
        float bend = 1 - sqrt(saturate(1 - rim * rim));
        float2 offset = normal * bend * Optics.z * Shape.xy;
        float2 samplePixel = p.xy + offset;
        float2 spread = offset * (local.x * local.y / max(1, Bounds.z * Bounds.w));
        glass = Shape.w > 0 && dot(spread, spread) > .0025
            ? Spectral(samplePixel, spread) : SampleGlass(samplePixel, Optics.w);
    }
    // The tab labels and slider track are already in the source under the moving lens.
    glass = AlphaOver(float4(Surface.rgb * Surface.a, Surface.a), glass);
    if (Shadow.x > 0 && Shadow.y > 0)
    {
        float shifted = Distance(local - float2(0, Shadow.x), Bounds.zw, Optics.x);
        float shade = saturate(smoothstep(-Shadow.x, Shadow.x, shifted) - smoothstep(-Shadow.x, Shadow.x, dist));
        glass = AlphaOver(float4(0, 0, 0, shade * Shadow.y), glass);
    }
    float highlight = Shape.z * (1 - smoothstep(0, .85 * View.z, abs(dist)));
    highlight *= .4 + .6 * abs(dot(normal, float2(.7071, -.7071)));
    glass = AlphaOver(highlight.xxxx, glass);
    return lerp(Source.SampleLevel(LinearClamp, p.xy / View.xy, 0), glass, coverage);
}
