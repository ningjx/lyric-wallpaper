using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

// The prototype uses a small subset of the Windows SDK COM interfaces directly.
// Slot numbers include IUnknown and are grouped by the interface that owns them.
using LyricWallpaper.Scene;

namespace LyricWallpaper.Rendering;

internal sealed unsafe class D3D11Renderer : IDisposable
{
    private enum Device { CreateBuffer = 3, CreateTexture2D = 5, CreateShaderResourceView = 7,
        CreateRenderTargetView = 9, CreateVertexShader = 12, CreatePixelShader = 15, CreateBlendState = 20, CreateSamplerState = 23 }
    private enum Context { VSSetConstantBuffers = 7, PSSetShaderResources = 8, PSSetShader = 9,
        PSSetSamplers = 10, VSSetShader = 11, Draw = 13, Map = 14, Unmap = 15, PSSetConstantBuffers = 16, DrawInstanced = 21,
        VSSetShaderResources = 25, VSSetSamplers = 26,
        IASetPrimitiveTopology = 24, OMSetRenderTargets = 33, OMSetBlendState = 35, RSSetViewports = 44,
        CopyResource = 47, UpdateSubresource = 48, GenerateMips = 54, Flush = 111 }
    private enum SwapChain { Present = 8, GetBuffer = 9, ResizeBuffers = 13 }
    private const uint Bgra8 = 87, R8Unorm = 61;
    private const int PreviewWidth = 384, PreviewHeight = 216;
    private static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid DxgiFactory2Id = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    private nint device, context, chain, target, constants, blurConstants, sampler, background, lyrics, atlasTexture;
    private nint backgroundVs, backgroundPs, cardVs, cardPs, blurDownsamplePs, blurHorizontalPs, blurVerticalPs;
    private nint widgetVs, widgetPs, widgetBlend;
    private nint previewBackgroundPs, previewWidgetPs, editorBackdropPs, previewTexture, previewTarget, previewStaging;
    private (nint Target, nint Input) blurHorizontal, blurResult;
    private (Size Size, bool Mipmapped) blurSurface;
    private readonly record struct BlurKey(BackgroundView View, int GaussianRadius);
    private BlurKey? cachedBlur;
    private int backgroundWidth, backgroundHeight;
    private Bitmap backgroundBitmap;
    private WallpaperLightMap? lightMap;
    private (BackgroundView View, int Width, int Height)? lightKey;
    private WallpaperLight[] wallpaperLights = [];
    private const int WallpaperLightsOffset = 65;
    public const int SceneVectors = WallpaperLightsOffset + WallpaperLightMap.MaxSources * 2;
    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int BlurBuilds { get; private set; }
    public string Adapter { get; }
    public string Presentation { get; }

    public D3D11Renderer(nint window, int width, int height, Bitmap backgroundBitmap, Size atlas, bool layered,
        double renderScale = 1)
    {
        OutputWidth = width;
        OutputHeight = height;
        (Width, Height) = ScaledSize(renderScale);
        backgroundWidth = backgroundBitmap.Width;
        backgroundHeight = backgroundBitmap.Height;
        this.backgroundBitmap = backgroundBitmap;
        try
        {
            var description = new SwapChainDescription
            {
                Width = (uint)Width, Height = (uint)Height, Format = Bgra8, SampleCount = 1,
                Usage = 0x20, BufferCount = layered ? 1u : 2u, Window = window, Windowed = 1
            };
            if (layered)
            {
                // Raised Explorer desktops use an opaque layered child; DXGI blt presents support this path.
                Check(D3D11CreateDeviceAndSwapChain(0, 1, 0, 0x20, null, 0, 7,
                    &description, out chain, out device, out _, out context), "create layered D3D11 swap chain");
                Presentation = "DXGI blt";
            }
            else
            {
                Check(D3D11CreateDevice(0, 1, 0, 0x20, null, 0, 7,
                    out device, out _, out context), "create D3D11 hardware device");
                chain = CreateFlipChain(window, Width, Height);
                Presentation = "DXGI flip discard";
            }

            Adapter = ReadAdapter();
            target = CreateBackBufferTarget();

            var source = ReadShaderSource();
            backgroundVs = CreateShader(source, "BackgroundVertex", "vs_5_0", Device.CreateVertexShader);
            backgroundPs = CreateShader(source, "BackgroundPixel", "ps_5_0", Device.CreatePixelShader);
            cardVs = CreateShader(source, "CardVertex", "vs_5_0", Device.CreateVertexShader);
            cardPs = CreateShader(source, "CardPixel", "ps_5_0", Device.CreatePixelShader);
            widgetVs = CreateShader(source, "WidgetVertex", "vs_5_0", Device.CreateVertexShader);
            widgetPs = CreateShader(source, "WidgetPixel", "ps_5_0", Device.CreatePixelShader);
            blurDownsamplePs = CreateShader(source, "BlurDownsample", "ps_5_0", Device.CreatePixelShader);
            blurHorizontalPs = CreateShader(source, "BlurHorizontal", "ps_5_0", Device.CreatePixelShader);
            blurVerticalPs = CreateShader(source, "BlurVertical", "ps_5_0", Device.CreatePixelShader);

            constants = CreateConstantBuffer(SceneVectors);
            blurConstants = CreateConstantBuffer(GaussianBlurKernel.VectorCount);
            nint resource;
            var blend = new BlendDescription();
            blend.Targets[0] = new RenderTargetBlend
            {
                Enabled = 1, Source = 2, Destination = 6, Operation = 1,
                SourceAlpha = 2, DestinationAlpha = 6, AlphaOperation = 1, WriteMask = 15
            };
            Check(((delegate* unmanaged[Stdcall]<nint, BlendDescription*, nint*, int>)Slot(device, (int)Device.CreateBlendState))
                (device, &blend, &resource), "create widget blend state");
            widgetBlend = resource;
            var sampling = new SamplerDescription
            {
                Filter = 0x15, AddressU = 3, AddressV = 3, AddressW = 3, MaxAnisotropy = 1,
                Comparison = 1, MaxLod = float.MaxValue
            };
            Check(((delegate* unmanaged[Stdcall]<nint, SamplerDescription*, nint*, int>)Slot(device, (int)Device.CreateSamplerState))
                (device, &sampling, &resource), "create sampler");
            sampler = resource;
            background = CreateTexture(backgroundBitmap);
            var atlasDescription = new TextureDescription
            {
                Width = (uint)atlas.Width, Height = (uint)atlas.Height, MipLevels = 1, ArraySize = 1,
                Format = R8Unorm, SampleCount = 1, BindFlags = 8
            };
            atlasTexture = CreateTexture(atlasDescription, null);
            lyrics = CreateTextureView(atlasTexture);
            BindScene();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static byte[] ReadShaderSource()
    {
        using var shader = typeof(D3D11Renderer).Assembly.GetManifestResourceStream("Glass.hlsl")
            ?? throw new InvalidOperationException("找不到内置玻璃着色器。");
        var source = new byte[shader.Length];
        shader.ReadExactly(source);
        return source;
    }

    private nint CreateConstantBuffer(int vectors)
    {
        var description = new BufferDescription { ByteWidth = (uint)(vectors * 16), BindFlags = 4 };
        nint buffer;
        Check(((delegate* unmanaged[Stdcall]<nint, BufferDescription*, nint, nint*, int>)Slot(device, (int)Device.CreateBuffer))
            (device, &description, 0, &buffer), "create constant buffer");
        return buffer;
    }

    private (int Width, int Height) ScaledSize(double scale)
    {
        scale = Math.Min(scale, 16384d / Math.Max(OutputWidth, OutputHeight));
        return (Math.Max(1, (int)Math.Round(OutputWidth * scale)), Math.Max(1, (int)Math.Round(OutputHeight * scale)));
    }

    private nint CreateBackBufferTarget()
    {
        nint buffer;
        var textureId = Texture2DId;
        Check(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(chain, (int)SwapChain.GetBuffer))
            (chain, 0, &textureId, &buffer), "get back buffer");
        try { return CreateRenderTarget(buffer); }
        finally { Marshal.Release(buffer); }
    }

    public void ChangeRenderScale(double scale)
    {
        var size = ScaledSize(scale);
        if (size == (Width, Height)) return;
        SetTarget(0);
        Marshal.Release(target);
        target = 0;
        ((delegate* unmanaged[Stdcall]<nint, void>)Slot(context, (int)Context.Flush))(context);
        int result = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, uint, int>)Slot(chain, (int)SwapChain.ResizeBuffers))
            (chain, 0, (uint)size.Width, (uint)size.Height, 0, 0);
        // If ResizeBuffers fails, the previous buffers are still usable.
        target = CreateBackBufferTarget();
        Check(result, "resize DPR back buffer");
        (Width, Height) = size;
        ReleaseBlur();
    }

    public void ChangeBackground(Bitmap bitmap)
    {
        nint next = CreateTexture(bitmap);
        Marshal.Release(background);
        background = next;
        backgroundWidth = bitmap.Width;
        backgroundHeight = bitmap.Height;
        backgroundBitmap = bitmap;
        lightMap = null;
        lightKey = null;
        cachedBlur = null;
        nint* textures = stackalloc nint[] { background, lyrics };
        BindResources(Context.PSSetShaderResources, 2, textures);
        BindResources(Context.VSSetShaderResources, 1, textures);
    }

    public bool Draw(SceneFrame frame, string? capturePath)
    {
        Span<Vector4> scene = stackalloc Vector4[SceneVectors];
        bool sampleBlur = PrepareScene(scene, frame.Style, frame.Lyrics, frame.Widgets.Count > 0);

        // Flip-model Present unbinds the back buffer, so bind it again each frame.
        SetTarget(target);
        SetViewport(Width, Height);
        if (sampleBlur) SetBlurInput(blurResult.Input);
        DrawBackground(backgroundPs);
        if (frame.Lyrics.Count != 0) DrawCards(cardVs, cardPs, (uint)frame.Lyrics.Count);
        if (frame.Widgets.Count > 0)
        {
            var lights = FindWallpaperLights(scene, frame.Style);
            SetBlend(widgetBlend);
            foreach (var card in frame.Widgets) DrawWidget(scene, card, frame.Style.Glass.Strength, widgetPs, lights);
            SetBlend(0);
        }
        if (capturePath is not null) SaveFrame(capturePath);
        int result = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Slot(chain, (int)SwapChain.Present))(chain, 1, 0);
        Check(result, "present");
        return result != 0x087A0001; // DXGI_STATUS_OCCLUDED
    }

    public WidgetPreviewFrame DrawWidgetPreview(string type, SceneStyle style, GlassCard card)
    {
        EnsurePreviewResources();
        // Sample a crop around the real card geometry and leave space for its shadow.
        float step = Math.Max(2 * card.Half.X / (.78f * PreviewWidth), 2 * card.Half.Y / (.70f * PreviewHeight));
        var origin = card.Center - new Vector2(PreviewWidth, PreviewHeight) * (step / 2);
        Span<Vector4> scene = stackalloc Vector4[SceneVectors];
        scene[61] = new(origin, step, step);
        bool sampleBlur = PrepareScene(scene, style, [], hasWidgets: true);

        SetTarget(previewTarget);
        SetViewport(PreviewWidth, PreviewHeight);
        if (sampleBlur) SetBlurInput(blurResult.Input);
        DrawBackground(previewBackgroundPs);
        SetViewport(Width / step, Height / step, -origin.X / step, -origin.Y / step);
        SetBlend(widgetBlend);
        DrawWidget(scene, card, style.Glass.Strength, previewWidgetPs, FindWallpaperLights(scene, style));
        SetBlend(0);
        SetTarget(0);
        return new(type, PreviewWidth, PreviewHeight, ReadPreviewPixels());
    }

    public WidgetEditorBackdrop DrawEditorBackdrop(BackgroundView view)
    {
        EnsurePreviewResources();
        Span<Vector4> scene = stackalloc Vector4[SceneVectors];
        PackBackground(scene, view);
        scene[61] = new(0, 0, Width / (float)PreviewWidth, Height / (float)PreviewHeight);
        WriteConstants(scene);

        SetTarget(previewTarget);
        SetViewport(PreviewWidth, PreviewHeight);
        DrawBackground(editorBackdropPs);
        SetTarget(0);
        return new(PreviewWidth, PreviewHeight, ReadPreviewPixels());
    }

    private void PackBackground(Span<Vector4> scene, BackgroundView view)
    {
        float ratio = (Width / (float)Height) / (backgroundWidth / (float)backgroundHeight);
        scene[0] = view.Layout switch
        {
            BackgroundLayout.Contain => new(Width, Height, Math.Max(1, ratio), Math.Max(1, 1 / ratio)),
            BackgroundLayout.Stretch => new(Width, Height, 1, 1),
            BackgroundLayout.Tile => new(Width, Height, (float)OutputWidth / backgroundWidth, (float)OutputHeight / backgroundHeight),
            _ => new(Width, Height, Math.Min(1, ratio), Math.Min(1, 1 / ratio))
        };
        scene[34] = new(view.Zoom / 100f, view.X / 100f, view.Y / 100f, view.Layout == BackgroundLayout.Tile ? 1 : 0);
    }

    private bool PrepareScene(Span<Vector4> scene, SceneStyle style, IReadOnlyList<GlassCard> lyricsCards, bool hasWidgets)
    {
        var view = style.Background;
        var material = style.Glass;
        var lyricsStyle = style.Lyrics;
        PackBackground(scene, view);
        scene[33] = PackColor(lyricsStyle.TextColor, material.Strength);
        scene[35] = new(material.RefractionHeight, material.RefractionAmount, material.Blur, material.Radius);
        scene[36] = new(material.Tone.Saturation, material.Tone.Brightness, material.Tone.Contrast, (int)material.Tone.Dispersion);
        scene[37] = PackColor(material.Highlight.Color);
        scene[38] = new(material.Highlight.Angle, material.Highlight.Falloff, material.Highlight.Width, (int)material.Highlight.Style);
        scene[39] = PackColor(material.Tint);
        scene[40] = PackColor(material.Surface);
        scene[41] = PackColor(material.Shadow.Color);
        scene[42] = new(material.Shadow.Radius, material.Shadow.X, material.Shadow.Y, lyricsStyle.BehindGlass ? 1 : 0);
        scene[43] = new(lyricsStyle.FarGlass, lyricsStyle.Depth ? 1 : 0, lyricsCards.Count == 0 ? 0 : lyricsCards[0].Padding, 0);
        scene[62] = new(material.OpticalDepth ? 1 : 0, material.ContinuousCorners ? 1 : 0, 0, 0);
        scene[63] = new(material.InnerShadow.Alpha, material.InnerShadow.Radius, material.InnerShadow.X, material.InnerShadow.Y);
        scene[64] = new(material.ProgressiveBlur ? 1 : 0, material.HueTint ? 1 : 0, lyricsStyle.AdaptiveContrast ? 1 : 0, 0);
        scene[WallpaperLightsOffset..].Clear();
        for (int i = 0; i < lyricsCards.Count; i++) PackCard(scene, i, lyricsCards[i]);
        bool blurEnabled = material.Blur > 0 && material.Strength > 0 && (!lyricsStyle.BehindGlass || material.ProgressiveBlur);
        bool sampleBlur = blurEnabled && (lyricsCards.Count != 0 || hasWidgets);
        float blurRadius = material.Blur * Height / 1080f;
        int downsampleGrid = 2;
        Size blurSize = Size.Empty;
        float mipLevel = 0;
        if (sampleBlur)
        {
            if (material.FullSceneBlur)
                blurSize = GaussianBlurKernel.TargetSize(Width, Height, blurRadius, out downsampleGrid);
            else
            {
                blurSize = new((Width + 1) / 2, (Height + 1) / 2);
                // The mip footprint and the four neighboring samples together set the blur width.
                mipLevel = MathF.Log2(Math.Max(1, .85f * blurRadius * blurSize.Height / Height));
            }
        }
        scene[44] = new(blurSize.Width, blurSize.Height, mipLevel, sampleBlur ? (material.FullSceneBlur ? 1 : 2) : 0);
        WriteConstants(scene);

        if (sampleBlur)
        {
            // The ordinary blur levels depend on the background transform. Moving the radius slider only selects a level.
            var key = new BlurKey(view, material.FullSceneBlur ? material.Blur : 0);
            BuildBlur(key, blurSize, blurRadius * blurSize.Height / Height, downsampleGrid);
        }
        else if (!blurEnabled)
            ReleaseBlur();

        return sampleBlur;
    }

    private void DrawBackground(nint pixelShader)
    {
        SetShaders(backgroundVs, pixelShader);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Slot(context, (int)Context.Draw))(context, 3, 0);
    }

    private ReadOnlySpan<WallpaperLight> FindWallpaperLights(ReadOnlySpan<Vector4> scene, SceneStyle style)
    {
        if (!style.Glass.Highlight.FollowWallpaper || style.Glass.Highlight.Color.Alpha <= 0 || style.Glass.Strength <= 0)
            return [];
        var key = (style.Background, Width, Height);
        if (key != lightKey)
        {
            lightMap ??= new WallpaperLightMap(backgroundBitmap);
            var view = scene[0];
            var transform = scene[34];
            wallpaperLights = lightMap.Find(new(view.X, view.Y), new(view.Z, view.W), transform.X,
                new(transform.Y, transform.Z), transform.W > 0);
            lightKey = key;
        }
        return wallpaperLights;
    }

    private void DrawWidget(Span<Vector4> scene, GlassCard card, float strength, nint pixelShader, ReadOnlySpan<WallpaperLight> lights)
    {
        PackCard(scene, 0, card);
        scene[33] = PackColor(card.Color, strength);
        scene[43] = new(1, 0, card.Padding, card.Press);
        scene[64].W = card.AutoInvertText ? 1 : 0;
        if (!lights.IsEmpty)
        {
            for (int index = 0; index < lights.Length; index++)
            {
                var light = lights[index];
                var (direction, focus, weight) = light.At(card.Center, Height);
                scene[WallpaperLightsOffset + index] = new(direction, focus, weight);
                scene[WallpaperLightsOffset + WallpaperLightMap.MaxSources + index] = new(light.Color, 0);
            }
            scene[38].W = 3;
        }
        WriteConstants(scene);
        if (card.AutoInvertText && scene[44].W > 0) SetWidgetBlurInput(blurResult.Input);
        DrawCards(widgetVs, pixelShader, 1);
        if (card.AutoInvertText && scene[44].W > 0) SetWidgetBlurInput(0);
    }

    private void EnsurePreviewResources()
    {
        if (previewTarget != 0) return;
        try
        {
            var shader = ReadShaderSource();
            previewBackgroundPs = CreateShader(shader, "PreviewBackgroundPixel", "ps_5_0", Device.CreatePixelShader);
            previewWidgetPs = CreateShader(shader, "PreviewWidgetPixel", "ps_5_0", Device.CreatePixelShader);
            editorBackdropPs = CreateShader(shader, "EditorBackdropPixel", "ps_5_0", Device.CreatePixelShader);
            var description = new TextureDescription
            {
                Width = PreviewWidth, Height = PreviewHeight, MipLevels = 1, ArraySize = 1,
                Format = Bgra8, SampleCount = 1, BindFlags = 0x20
            };
            previewTexture = CreateTexture(description, null);
            previewTarget = CreateRenderTarget(previewTexture);
            description.BindFlags = 0;
            description.Usage = 3;
            description.CpuAccess = 0x20000;
            previewStaging = CreateTexture(description, null);
        }
        catch
        {
            ReleasePreviewResources();
            throw;
        }
    }

    private byte[] ReadPreviewPixels()
    {
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(context, (int)Context.CopyResource))
            (context, previewStaging, previewTexture);
        Subresource mapped;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Subresource*, int>)Slot(context, (int)Context.Map))
            (context, previewStaging, 0, 1, 0, &mapped), "read editor preview");
        try
        {
            const int stride = PreviewWidth * 4;
            var pixels = new byte[stride * PreviewHeight];
            for (int row = 0; row < PreviewHeight; row++)
                Marshal.Copy(mapped.Memory + row * (int)mapped.RowPitch, pixels, row * stride, stride);
            return pixels;
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(context, (int)Context.Unmap))(context, previewStaging, 0);
        }
    }

    private void ReleasePreviewResources()
    {
        foreach (nint resource in new[] { previewBackgroundPs, previewWidgetPs, editorBackdropPs, previewTarget, previewTexture, previewStaging })
            if (resource != 0) Marshal.Release(resource);
        previewBackgroundPs = previewWidgetPs = editorBackdropPs = previewTarget = previewTexture = previewStaging = 0;
    }

    private static void PackCard(Span<Vector4> constants, int index, GlassCard card)
    {
        constants[1 + index] = new(card.Center.X, card.Center.Y, card.Half.X, card.Half.Y);
        constants[17 + index] = new(card.Ink, card.Opacity, 0);
        constants[45 + index] = card.Uv;
    }

    private static Vector4 PackColor(MaterialColor value) => PackColor(value.Argb, value.Alpha);

    private static Vector4 PackColor(int argb, float alpha)
    {
        var color = Color.FromArgb(argb);
        return new(color.R / 255f, color.G / 255f, color.B / 255f, alpha);
    }

    private void WriteConstants(Span<Vector4> scene) => WriteBuffer(constants, scene);

    private void WriteBuffer(nint buffer, ReadOnlySpan<Vector4> values)
    {
        fixed (Vector4* data = values)
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, void*, uint, uint, void>)Slot(context, (int)Context.UpdateSubresource))
                (context, buffer, 0, 0, data, 0, 0);
    }

    private void DrawCards(nint vertexShader, nint pixelShader, uint count)
    {
        SetShaders(vertexShader, pixelShader);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, void>)Slot(context, (int)Context.DrawInstanced))
            (context, 6, count, 0, 0);
    }

    private void SetBlend(nint state) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)Slot(context, (int)Context.OMSetBlendState))
            (context, state, 0, uint.MaxValue);

    private void BuildBlur(BlurKey desired, Size size, float radius, int downsampleGrid)
    {
        if (cachedBlur == desired) return;
        bool mipmapped = desired.GaussianRadius == 0;
        if (blurSurface != (size, mipmapped)) ReleaseBlur();
        if (blurResult.Target == 0)
        {
            try
            {
                blurResult = CreateBlurSurface(size, mipmapped);
                if (!mipmapped) blurHorizontal = CreateBlurSurface(size, mipmapped: false);
                blurSurface = (size, mipmapped);
            }
            catch { ReleaseBlur(); throw; }
        }

        Span<Vector4> kernel = stackalloc Vector4[GaussianBlurKernel.VectorCount];
        GaussianBlurKernel.Write(kernel, mipmapped ? 0 : radius, downsampleGrid);
        WriteBuffer(blurConstants, kernel);
        SetViewport(size.Width, size.Height);

        SetBlurInput(0);
        SetTarget(blurResult.Target);
        DrawBackground(blurDownsamplePs);
        if (mipmapped)
        {
            // Mip generation reads from the same resource, so unbind its render target first.
            SetTarget(0);
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)Slot(context, (int)Context.GenerateMips))(context, blurResult.Input);
        }
        else
        {
            // Reuse the prefilter target for the vertical result after the horizontal pass reads it.
            SetTarget(blurHorizontal.Target);
            SetBlurInput(blurResult.Input);
            DrawBackground(blurHorizontalPs);
            SetBlurInput(0);
            SetTarget(blurResult.Target);
            SetBlurInput(blurHorizontal.Input);
            DrawBackground(blurVerticalPs);
        }
        cachedBlur = desired;
        BlurBuilds++;
    }

    private (nint Target, nint Input) CreateBlurSurface(Size size, bool mipmapped)
    {
        nint texture = CreateTexture(new TextureDescription
        {
            Width = (uint)size.Width, Height = (uint)size.Height, MipLevels = mipmapped ? 0u : 1u, ArraySize = 1,
            Format = Bgra8, SampleCount = 1, BindFlags = 0x20 | 8, Misc = mipmapped ? 1u : 0u
        }, null);
        try
        {
            nint view = CreateRenderTarget(texture);
            try { return (view, CreateTextureView(texture)); }
            catch { Marshal.Release(view); throw; }
        }
        finally { Marshal.Release(texture); }
    }

    private void ReleaseBlur()
    {
        if (blurResult.Target == 0) return;
        SetBlurInput(0);
        Marshal.Release(blurResult.Target);
        Marshal.Release(blurResult.Input);
        if (blurHorizontal.Target != 0)
        {
            Marshal.Release(blurHorizontal.Target);
            Marshal.Release(blurHorizontal.Input);
        }
        blurHorizontal = blurResult = default;
        blurSurface = default;
        cachedBlur = null;
    }

    private void SaveFrame(string path)
    {
        var textureId = Texture2DId;
        nint buffer;
        Check(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(chain, (int)SwapChain.GetBuffer))
            (chain, 0, &textureId, &buffer), "get frame for capture");
        nint staging = 0;
        try
        {
            var description = new TextureDescription
            {
                Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1,
                Format = Bgra8, SampleCount = 1, Usage = 3, CpuAccess = 0x20000
            };
            Check(((delegate* unmanaged[Stdcall]<nint, TextureDescription*, nint, nint*, int>)Slot(device, (int)Device.CreateTexture2D))
                (device, &description, 0, &staging), "create capture staging texture");
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(context, (int)Context.CopyResource))(context, staging, buffer);
            Subresource mapped;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Subresource*, int>)Slot(context, (int)Context.Map))
                (context, staging, 0, 1, 0, &mapped), "map captured frame");
            try
            {
                using var frame = new Bitmap(Width, Height, (int)mapped.RowPitch, PixelFormat.Format32bppArgb, mapped.Memory);
                frame.Save(path, ImageFormat.Png);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(context, (int)Context.Unmap))(context, staging, 0);
            }
        }
        finally
        {
            if (staging != 0) Marshal.Release(staging);
            Marshal.Release(buffer);
        }
    }

    private nint CreateFlipChain(nint window, int width, int height)
    {
        Guid id = DxgiFactory2Id;
        Check(CreateDXGIFactory1(&id, out nint factory), "create DXGI factory");
        try
        {
            var description = new FlipDescription
            {
                Width = (uint)width, Height = (uint)height, Format = Bgra8,
                SampleCount = 1, Usage = 0x20, BufferCount = 2, SwapEffect = 4
            };
            nint result;
            // IDXGIFactory2::CreateSwapChainForHwnd
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, FlipDescription*, nint, nint, nint*, int>)Slot(factory, 15))
                (factory, device, window, &description, 0, 0, &result), "create HWND flip swap chain");
            return result;
        }
        finally { Marshal.Release(factory); }
    }

    private string ReadAdapter()
    {
        Guid id = DxgiDeviceId;
        Check(Marshal.QueryInterface(device, in id, out nint dxgi), "get DXGI device");
        try
        {
            nint adapter;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dxgi, 7))(dxgi, &adapter), "get DXGI adapter");
            try
            {
                AdapterDescription description;
                Check(((delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)Slot(adapter, 8))
                    (adapter, &description), "read DXGI adapter");
                return new string(description.Name);
            }
            finally { Marshal.Release(adapter); }
        }
        finally { Marshal.Release(dxgi); }
    }

    private void SetViewport(float width, float height, float x = 0, float y = 0)
    {
        var viewport = new Viewport { X = x, Y = y, Width = width, Height = height, MaxDepth = 1 };
        ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)Slot(context, (int)Context.RSSetViewports))
            (context, 1, &viewport);
    }

    private void SetTarget(nint view) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Slot(context, (int)Context.OMSetRenderTargets))
            (context, view == 0 ? 0u : 1u, view == 0 ? null : &view, 0);

    private void SetBlurInput(nint input) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, (int)Context.PSSetShaderResources))
            (context, 2, 1, &input);

    private void SetWidgetBlurInput(nint input) =>
        BindResources(Context.VSSetShaderResources, 1, &input, startSlot: 2);

    private void BindScene()
    {
        ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(context, (int)Context.IASetPrimitiveTopology))(context, 4);
        nint buffer = constants;
        BindResources(Context.VSSetConstantBuffers, 1, &buffer);
        BindResources(Context.PSSetConstantBuffers, 1, &buffer);
        nint kernel = blurConstants;
        BindResources(Context.PSSetConstantBuffers, 1, &kernel, startSlot: 1);
        nint filter = sampler;
        BindResources(Context.PSSetSamplers, 1, &filter);
        BindResources(Context.VSSetSamplers, 1, &filter);
        nint* textures = stackalloc nint[] { background, lyrics };
        BindResources(Context.PSSetShaderResources, 2, textures);
        BindResources(Context.VSSetShaderResources, 1, textures);
    }

    private void BindResources(Context member, uint count, nint* resources, uint startSlot = 0) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, (int)member))(context, startSlot, count, resources);

    private void SetShaders(nint vertex, nint pixel)
    {
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)Slot(context, (int)Context.VSSetShader))(context, vertex, 0, 0);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)Slot(context, (int)Context.PSSetShader))(context, pixel, 0, 0);
    }

    private nint CreateTexture(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var description = new TextureDescription
            {
                Width = (uint)bitmap.Width, Height = (uint)bitmap.Height, MipLevels = 1, ArraySize = 1,
                Format = Bgra8, SampleCount = 1, Usage = 1, BindFlags = 8
            };
            var pixels = new Subresource { Memory = data.Scan0, RowPitch = (uint)data.Stride };
            nint texture = CreateTexture(description, &pixels);
            try { return CreateTextureView(texture); }
            finally { Marshal.Release(texture); }
        }
        finally { bitmap.UnlockBits(data); }
    }

    private nint CreateTexture(TextureDescription description, Subresource* pixels)
    {
        nint texture;
        Check(((delegate* unmanaged[Stdcall]<nint, TextureDescription*, Subresource*, nint*, int>)Slot(device, (int)Device.CreateTexture2D))
            (device, &description, pixels, &texture), "create texture");
        return texture;
    }

    private nint CreateTextureView(nint texture)
    {
        nint view;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(device, (int)Device.CreateShaderResourceView))
            (device, texture, 0, &view), "create texture view");
        return view;
    }

    private nint CreateRenderTarget(nint texture)
    {
        nint view;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(device, (int)Device.CreateRenderTargetView))
            (device, texture, 0, &view), "create render target");
        return view;
    }

    public void UpdateTextRow(int slot, Size size, ReadOnlySpan<byte> mask)
    {
        fixed (byte* pixels = mask)
        {
            var box = new Box { Top = (uint)(slot * size.Height), Right = (uint)size.Width,
                Bottom = (uint)((slot + 1) * size.Height), Back = 1 };
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, void*, uint, uint, void>)Slot(context, (int)Context.UpdateSubresource))
                (context, atlasTexture, 0, (nint)(&box), pixels, (uint)size.Width, 0);
        }
    }

    private nint CreateShader(byte[] source, string entry, string profile, Device member)
    {
        fixed (byte* data = source)
        {
            int hr = D3DCompile(data, (nuint)source.Length, "Glass.hlsl", 0, 0, entry, profile, 1u << 15, 0,
                out nint code, out nint errors);
            try
            {
                if (hr < 0 && errors != 0)
                    throw new InvalidOperationException(Marshal.PtrToStringAnsi(BlobData(errors)));
                Check(hr, $"compile {entry}");
                nint shader;
                nuint size = ((delegate* unmanaged[Stdcall]<nint, nuint>)Slot(code, 4))(code);
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)Slot(device, (int)member))
                    (device, BlobData(code), size, 0, &shader), $"create {entry}");
                return shader;
            }
            finally
            {
                if (code != 0) Marshal.Release(code);
                if (errors != 0) Marshal.Release(errors);
            }
        }
    }

    public void Dispose()
    {
        ReleasePreviewResources();
        ReleaseBlur();
        foreach (nint resource in new[] { context, target, constants, blurConstants, sampler, lyrics, atlasTexture, background,
            cardPs, cardVs, backgroundPs, backgroundVs, blurDownsamplePs, blurHorizontalPs, blurVerticalPs, widgetVs, widgetPs, widgetBlend, chain, device })
            if (resource != 0) Marshal.Release(resource);
        context = target = constants = blurConstants = sampler = lyrics = atlasTexture = background = cardPs = cardVs =
            backgroundPs = backgroundVs = blurDownsamplePs = blurHorizontalPs = blurVerticalPs = widgetVs = widgetPs = widgetBlend = chain = device = 0;
    }

    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];
    private static nint BlobData(nint blob) => ((delegate* unmanaged[Stdcall]<nint, nint>)Slot(blob, 3))(blob);
    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new COMException($"{operation} failed (0x{hr:X8})", hr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SwapChainDescription
    {
        public uint Width, Height, RefreshNumerator, RefreshDenominator, Format, Scanline, Scaling;
        public uint SampleCount, SampleQuality, Usage, BufferCount;
        public nint Window;
        public int Windowed;
        public uint SwapEffect, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FlipDescription
    {
        public uint Width, Height, Format;
        public int Stereo;
        public uint SampleCount, SampleQuality, Usage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription
    {
        public fixed char Name[128];
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long Luid;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDescription { public uint ByteWidth, Usage, BindFlags, CpuAccess, Misc, StructureStride; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BlendDescription
    {
        public int AlphaToCoverage, Independent;
        public RenderTargetBlends Targets;
    }
    [System.Runtime.CompilerServices.InlineArray(8)]
    private struct RenderTargetBlends { private RenderTargetBlend first; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RenderTargetBlend
    {
        public int Enabled;
        public uint Source, Destination, Operation, SourceAlpha, DestinationAlpha, AlphaOperation;
        public byte WriteMask;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, Misc;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Subresource { public nint Memory; public uint RowPitch, SlicePitch; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SamplerDescription
    {
        public uint Filter, AddressU, AddressV, AddressW;
        public float MipLodBias;
        public uint MaxAnisotropy, Comparison;
        public fixed float Border[4];
        public float MinLod, MaxLod;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Viewport { public float X, Y, Width, Height, MinDepth, MaxDepth; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, uint driver, nint software, uint flags,
        uint* features, uint featureCount, uint sdkVersion, out nint device, out uint feature, out nint context);
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDeviceAndSwapChain(nint adapter, uint driver, nint software, uint flags,
        uint* features, uint featureCount, uint sdkVersion, SwapChainDescription* description, out nint chain,
        out nint device, out uint feature, out nint context);
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(Guid* iid, out nint factory);
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte* source, nuint size, string name, nint defines, nint include,
        string entry, string profile, uint flags, uint effectFlags, out nint code, out nint errors);
}
