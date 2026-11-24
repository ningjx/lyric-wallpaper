using System.Numerics;

namespace LyricWallpaper.Rendering;

internal static class GaussianBlurKernel
{
    private const int MaxRadius = 16;
    public const int VectorCount = 1 + MaxRadius / 2;

    public static Size TargetSize(int width, int height, float radiusPixels, out int downsampleGrid)
    {
        // A wide blur needs fewer intermediate pixels. Keep the kernel bounded at high resolutions and DPRs.
        int divisor = 2;
        while (radiusPixels > divisor * MaxRadius) divisor *= 2;
        downsampleGrid = Math.Clamp(divisor / 2, 2, 16);
        return new((width + divisor - 1) / divisor, (height + divisor - 1) / divisor);
    }

    public static void Write(Span<Vector4> constants, float radius, int downsampleGrid)
    {
        radius = Math.Clamp(radius, 0, MaxRadius);
        int extent = (int)MathF.Ceiling(radius);
        float sigma = Math.Max(.35f, radius / 2);
        Span<float> weights = stackalloc float[MaxRadius + 2];
        weights.Clear();
        float total = 1;
        for (int i = 1; i <= extent; i++)
        {
            // Ease in the outer tap as the visual radius crosses a texel boundary.
            float coverage = Math.Min(1, radius + 1 - i);
            weights[i] = coverage * MathF.Exp(-.5f * i * i / (sigma * sigma));
            total += 2 * weights[i];
        }

        constants.Clear();
        int pairs = (extent + 1) / 2;
        constants[0] = new(1 / total, pairs, downsampleGrid, 0);
        for (int pair = 0; pair < pairs; pair++)
        {
            int offset = 2 * pair + 1;
            float weight = weights[offset] + weights[offset + 1];
            // A fractional bilinear lookup combines two adjacent weighted texels.
            if (weight > 0) constants[pair + 1] = new(offset + weights[offset + 1] / weight, weight / total, 0, 0);
        }
    }
}
