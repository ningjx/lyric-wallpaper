using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

// The prototype uses a small subset of the Windows SDK COM interfaces directly.
// Slot numbers include IUnknown and are grouped by the interface that owns them.
internal sealed unsafe class D3D11Renderer : IDisposable
{
    private enum Device { CreateBuffer = 3, CreateTexture2D = 5, CreateShaderResourceView = 7,
        CreateRenderTargetView = 9, CreateVertexShader = 12, CreatePixelShader = 15, CreateSamplerState = 23 }
    private enum Context { VSSetConstantBuffers = 7, PSSetShaderResources = 8, PSSetShader = 9,
        PSSetSamplers = 10, VSSetShader = 11, Draw = 13, Map = 14, Unmap = 15, PSSetConstantBuffers = 16, DrawInstanced = 21,
        IASetPrimitiveTopology = 24, OMSetRenderTargets = 33, RSSetViewports = 44, CopyResource = 47, UpdateSubresource = 48, Flush = 111 }
    private enum SwapChain { Present = 8, GetBuffer = 9, ResizeBuffers = 13 }
    private const uint Bgra8 = 87;
    private static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid DxgiFactory2Id = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    private nint device, context, chain, target, constants, sampler, background, lyrics, atlasTexture;
    private nint backgroundVs, backgroundPs, cardVs, cardPs, blurHorizontalPs, blurVerticalPs;
    private (nint Target, nint Input) blurHorizontal, blurVertical;
    private readonly record struct BlurKey(BackgroundLayout Layout, int Zoom, int X, int Y, int Radius);
    private BlurKey? cachedBlur;
    private int backgroundWidth, backgroundHeight;
    public const int SceneVectors = 45;
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

            var source = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Glass.hlsl"));
            backgroundVs = CreateShader(source, "BackgroundVertex", "vs_5_0", Device.CreateVertexShader);
            backgroundPs = CreateShader(source, "BackgroundPixel", "ps_5_0", Device.CreatePixelShader);
            cardVs = CreateShader(source, "CardVertex", "vs_5_0", Device.CreateVertexShader);
            cardPs = CreateShader(source, "CardPixel", "ps_5_0", Device.CreatePixelShader);
            blurHorizontalPs = CreateShader(source, "BlurHorizontal", "ps_5_0", Device.CreatePixelShader);
            blurVerticalPs = CreateShader(source, "BlurVertical", "ps_5_0", Device.CreatePixelShader);

            var constantDescription = new BufferDescription { ByteWidth = SceneVectors * 16, BindFlags = 4 };
            nint resource;
            Check(((delegate* unmanaged[Stdcall]<nint, BufferDescription*, nint, nint*, int>)Slot(device, (int)Device.CreateBuffer))
                (device, &constantDescription, 0, &resource), "create constant buffer");
            constants = resource;
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
                Format = Bgra8, SampleCount = 1, BindFlags = 8
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
        cachedBlur = null;
        nint* textures = stackalloc nint[] { background, lyrics };
        BindResources(Context.PSSetShaderResources, 2, textures);
    }

    public bool Draw(Span<Vector4> scene, uint cardCount, WallpaperSettings settings, string? capturePath)
    {
        float imageAspect = (float)backgroundWidth / backgroundHeight;
        float screenAspect = (float)Width / Height;
        float ratio = screenAspect / imageAspect;
        scene[0] = settings.Layout switch
        {
            BackgroundLayout.Contain => new(Width, Height, Math.Max(1, ratio), Math.Max(1, 1 / ratio)),
            BackgroundLayout.Stretch => new(Width, Height, 1, 1),
            BackgroundLayout.Tile => new(Width, Height, (float)OutputWidth / backgroundWidth, (float)OutputHeight / backgroundHeight),
            _ => new(Width, Height, Math.Min(1, ratio), Math.Min(1, 1 / ratio))
        };
        scene[34] = new(settings.BackgroundZoom / 100f, settings.BackgroundX / 100f, settings.BackgroundY / 100f,
            settings.Layout == BackgroundLayout.Tile ? 1 : 0);
        bool blurEnabled = settings.FullSceneBlur && settings.Blur > 0 && !settings.BehindGlass && settings.Glass > 0;
        bool sampleBlur = blurEnabled && cardCount != 0;
        int blurWidth = (Width + 1) / 2, blurHeight = (Height + 1) / 2;
        scene[44] = new(blurWidth, blurHeight, settings.Blur * Height / 1080f, sampleBlur ? 1 : 0);
        fixed (Vector4* data = scene)
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, void*, uint, uint, void>)Slot(context, (int)Context.UpdateSubresource))
                (context, constants, 0, 0, data, 0, 0);

        if (sampleBlur)
            BuildBlur(new BlurKey(settings.Layout, settings.BackgroundZoom, settings.BackgroundX, settings.BackgroundY,
                settings.Blur), blurWidth, blurHeight);
        else if (!blurEnabled)
            ReleaseBlur();

        // Flip-model Present unbinds the back buffer, so bind it again each frame.
        SetTarget(target);
        SetViewport(Width, Height);
        if (sampleBlur) SetBlurInput(blurVertical.Input);
        SetShaders(backgroundVs, backgroundPs);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Slot(context, (int)Context.Draw))(context, 3, 0);
        SetShaders(cardVs, cardPs);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, void>)Slot(context, (int)Context.DrawInstanced))
            (context, 6, cardCount, 0, 0);
        if (capturePath is not null) SaveFrame(capturePath);
        int result = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Slot(chain, (int)SwapChain.Present))(chain, 1, 0);
        Check(result, "present");
        return result != 0x087A0001; // DXGI_STATUS_OCCLUDED
    }

    private void BuildBlur(BlurKey desired, int width, int height)
    {
        if (cachedBlur == desired) return;
        if (blurHorizontal.Target == 0)
        {
            try
            {
                blurHorizontal = CreateBlurSurface(width, height);
                blurVertical = CreateBlurSurface(width, height);
            }
            catch { ReleaseBlur(); throw; }
        }

        SetViewport(width, height);
        // D3D11 forbids binding the same texture as an SRV and an RTV.
        SetBlurInput(0);
        SetTarget(blurHorizontal.Target);
        SetShaders(backgroundVs, blurHorizontalPs);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Slot(context, (int)Context.Draw))(context, 3, 0);
        SetTarget(blurVertical.Target);
        SetBlurInput(blurHorizontal.Input);
        SetShaders(backgroundVs, blurVerticalPs);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Slot(context, (int)Context.Draw))(context, 3, 0);
        cachedBlur = desired;
        BlurBuilds++;
    }

    private (nint Target, nint Input) CreateBlurSurface(int width, int height)
    {
        nint texture = CreateTexture(new TextureDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = Bgra8, SampleCount = 1, BindFlags = 0x20 | 8
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
        if (blurHorizontal.Target == 0) return;
        SetBlurInput(0);
        Marshal.Release(blurHorizontal.Target);
        Marshal.Release(blurHorizontal.Input);
        if (blurVertical.Target != 0)
        {
            Marshal.Release(blurVertical.Target);
            Marshal.Release(blurVertical.Input);
        }
        blurHorizontal = blurVertical = default;
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

    private void SetViewport(int width, int height)
    {
        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1 };
        ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)Slot(context, (int)Context.RSSetViewports))
            (context, 1, &viewport);
    }

    private void SetTarget(nint view) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Slot(context, (int)Context.OMSetRenderTargets))
            (context, view == 0 ? 0u : 1u, view == 0 ? null : &view, 0);

    private void SetBlurInput(nint input) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, (int)Context.PSSetShaderResources))
            (context, 2, 1, &input);

    private void BindScene()
    {
        ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(context, (int)Context.IASetPrimitiveTopology))(context, 4);
        nint buffer = constants;
        BindResources(Context.VSSetConstantBuffers, 1, &buffer);
        BindResources(Context.PSSetConstantBuffers, 1, &buffer);
        nint filter = sampler;
        BindResources(Context.PSSetSamplers, 1, &filter);
        nint* textures = stackalloc nint[] { background, lyrics };
        BindResources(Context.PSSetShaderResources, 2, textures);
    }

    private void BindResources(Context member, uint count, nint* resources) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, (int)member))(context, 0, count, resources);

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

    public void UpdateTextRow(int slot, Bitmap row)
    {
        var pixels = row.LockBits(new Rectangle(0, 0, row.Width, row.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var box = new Box { Top = (uint)(slot * row.Height), Right = (uint)row.Width,
                Bottom = (uint)((slot + 1) * row.Height), Back = 1 };
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, void*, uint, uint, void>)Slot(context, (int)Context.UpdateSubresource))
                (context, atlasTexture, 0, (nint)(&box), (void*)pixels.Scan0, (uint)pixels.Stride, 0);
        }
        finally { row.UnlockBits(pixels); }
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
        ReleaseBlur();
        foreach (nint resource in new[] { context, target, constants, sampler, lyrics, atlasTexture, background,
            cardPs, cardVs, backgroundPs, backgroundVs, blurHorizontalPs, blurVerticalPs, chain, device })
            if (resource != 0) Marshal.Release(resource);
        context = target = constants = sampler = lyrics = atlasTexture = background = cardPs = cardVs =
            backgroundPs = backgroundVs = blurHorizontalPs = blurVerticalPs = chain = device = 0;
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
