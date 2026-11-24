using System.Numerics;
using System.Runtime.InteropServices;

namespace LyricWallpaper.Rendering;

internal readonly record struct SettingsPrimitive(
    Vector4 Bounds, Vector4 Clip, Vector4 Surface, Vector4 Optics, Vector4 Shape, Vector4 Shadow,
    Vector4 Sampling = default, Vector4 Track = default, Vector4 TrackColor = default);

internal sealed unsafe partial class D3D11Renderer
{
    // Settings have their own immediate context, independent of the wallpaper render thread.
    private D3D11Renderer()
    {
        backgroundBitmap = null!;
        Adapter = Presentation = "";
        try
        {
            Check(D3D11CreateDevice(0, 1, 0, 0x20, null, 0, 7, out device, out _, out context), "create settings D3D11 device");
            constants = CreateConstantBuffer(10);
            CreateSharedStates();
            BindScene();
        }
        catch { Dispose(); throw; }
    }

    internal sealed class SettingsSurface : IDisposable
    {
        private readonly D3D11Renderer gpu = new();
        private nint fullscreen, shapeVertex, paint, lens, shadow, overlay, text;
        private nint baseTexture, baseTarget, baseView, output, outputTarget, presentation, staging, surface9, device9;
        private int width, height;
        public nint Surface9 => surface9;

        public SettingsSurface(nint window)
        {
            try
            {
                byte[] shader = ReadShaderSource("SettingsGlass.hlsl");
                fullscreen = gpu.CreateShader(shader, "Fullscreen", "vs_5_0", Device.CreateVertexShader);
                shapeVertex = gpu.CreateShader(shader, "ShapeVertex", "vs_5_0", Device.CreateVertexShader);
                paint = gpu.CreateShader(shader, "Paint", "ps_5_0", Device.CreatePixelShader);
                lens = gpu.CreateShader(shader, "Lens", "ps_5_0", Device.CreatePixelShader);
                shadow = gpu.CreateShader(shader, "LensShadow", "ps_5_0", Device.CreatePixelShader);
                overlay = gpu.CreateShader(shader, "Overlay", "ps_5_0", Device.CreatePixelShader);
                try { device9 = CreateInteropDevice(window); }
                catch (COMException) { /* Keep D3D11 rendering available to software WPF composition. */ }
            }
            catch { Dispose(); throw; }
        }

        public void SetText(int w, int h, ReadOnlySpan<byte> pixels)
        {
            nint texture;
            fixed (byte* data = pixels)
            {
                var initial = new Subresource { Memory = (nint)data, RowPitch = (uint)(4 * w) };
                var desc = Description(w, h);
                desc.BindFlags = 8;
                desc.Usage = 1;
                texture = gpu.CreateTexture(desc, &initial);
            }
            try
            {
                nint next = gpu.CreateTextureView(texture);
                Release(ref text);
                text = next;
            }
            finally { Marshal.Release(texture); }
        }

        public void Resize(int w, int h)
        {
            if (output != 0 && w == width && h == height) return;
            ReleaseBuffers();
            (width, height) = (w, h);
            var desc = Description(w, h);
            try
            {
                baseTexture = gpu.CreateTexture(desc, null);
                baseTarget = gpu.CreateRenderTarget(baseTexture);
                baseView = gpu.CreateTextureView(baseTexture);
                output = gpu.CreateTexture(desc, null);
                outputTarget = gpu.CreateRenderTarget(output);
                if (device9 != 0)
                {
                    try
                    {
                        desc.Misc = 2;
                        presentation = gpu.CreateTexture(desc, null);
                        surface9 = OpenInteropSurface(presentation, w, h);
                    }
                    catch (COMException)
                    {
                        Release(ref presentation);
                        Release(ref device9);
                    }
                }
            }
            catch { ReleaseBuffers(); throw; }
        }

        public void Draw(float dpi, IReadOnlyList<SettingsPrimitive> docks,
            IReadOnlyList<SettingsPrimitive> fills, IReadOnlyList<SettingsPrimitive> controls)
        {
            Span<Vector4> data = stackalloc Vector4[10];
            data[0] = new(width, height, dpi, 0);
            gpu.SetViewport(width, height);
            // DWM supplies the desktop acrylic. The GPU target carries only premultiplied control layers.
            Vector4 clear = Vector4.Zero;
            ((delegate* unmanaged[Stdcall]<nint, nint, Vector4*, void>)Slot(gpu.context, (int)Context.ClearRenderTarget))
                (gpu.context, baseTarget, &clear);
            gpu.SetBlend(gpu.widgetBlend);
            DrawShapes(baseTarget, 0, shadow, docks, data);
            DrawShapes(baseTarget, 0, lens, docks, data);
            DrawShapes(baseTarget, 0, paint, fills, data);
            if (text != 0) Full(baseTarget, text, overlay, data);
            Copy(output, baseTexture);
            DrawShapes(outputTarget, 0, shadow, controls, data);
            // Replace the sampled app pixels inside the lens, including their alpha.
            // Blending them over the original track would leave an unwarped duplicate.
            gpu.SetBlend(0);
            DrawShapes(outputTarget, baseView, lens, controls, data);
            gpu.SetTarget(0);
            Bind(0);
        }

        public void PresentShared()
        {
            Copy(presentation, output);
            ((delegate* unmanaged[Stdcall]<nint, void>)Slot(gpu.context, (int)Context.Flush))(gpu.context);
        }

        public void ReadPixels(byte[] pixels)
        {
            if (staging == 0)
            {
                var desc = Description(width, height);
                desc.BindFlags = 0;
                desc.Usage = 3;
                desc.CpuAccess = 0x20000;
                staging = gpu.CreateTexture(desc, null);
            }
            Copy(staging, output);
            Subresource mapped;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Subresource*, int>)Slot(gpu.context, (int)Context.Map))
                (gpu.context, staging, 0, 1, 0, &mapped), "read settings surface");
            try
            {
                for (int row = 0; row < height; row++)
                    Marshal.Copy(mapped.Memory + row * (int)mapped.RowPitch, pixels, row * width * 4, width * 4);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(gpu.context, (int)Context.Unmap))(gpu.context, staging, 0);
            }
        }

        private void DrawShapes(nint target, nint source, nint shader, IReadOnlyList<SettingsPrimitive> shapes, Span<Vector4> data)
        {
            if (shapes.Count == 0) return;
            Bind(0);
            gpu.SetTarget(target);
            Bind(source);
            gpu.SetShaders(shapeVertex, shader);
            foreach (var element in shapes)
            {
                data[1] = element.Bounds;
                data[2] = element.Clip;
                data[3] = element.Surface;
                data[4] = element.Optics;
                data[5] = element.Shape;
                data[6] = element.Shadow;
                data[7] = element.Sampling;
                data[8] = element.Track;
                data[9] = element.TrackColor;
                gpu.WriteConstants(data);
                DrawVertices(6);
            }
        }

        private void Full(nint target, nint source, nint shader, Span<Vector4> data)
        {
            Bind(0);
            gpu.SetTarget(target);
            Bind(source);
            gpu.SetShaders(fullscreen, shader);
            gpu.WriteConstants(data);
            DrawVertices(3);
        }

        private void Copy(nint destination, nint source)
        {
            gpu.SetTarget(0);
            Bind(0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(gpu.context, (int)Context.CopyResource))(gpu.context, destination, source);
        }

        private void DrawVertices(uint count) =>
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Slot(gpu.context, (int)Context.Draw))(gpu.context, count, 0);

        private void Bind(nint view) => gpu.BindResources(Context.PSSetShaderResources, 1, &view);

        private static TextureDescription Description(int w, int h) => new()
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Bgra8, SampleCount = 1, BindFlags = 0x28
        };

        private nint CreateInteropDevice(nint window)
        {
            Check(Direct3DCreate9Ex(32, out nint d3d), "create D3D9Ex image bridge");
            try
            {
                Guid dxgiId = DxgiDeviceId;
                Check(Marshal.QueryInterface(gpu.device, in dxgiId, out nint dxgi), "get settings DXGI device");
                AdapterDescription description;
                try
                {
                    nint adapter;
                    Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dxgi, 7))(dxgi, &adapter), "get settings adapter");
                    try
                    {
                        Check(((delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)Slot(adapter, 8))(adapter, &description),
                            "get settings adapter LUID");
                    }
                    finally { Marshal.Release(adapter); }
                }
                finally { Marshal.Release(dxgi); }
                uint total = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(d3d, 4))(d3d);
                for (uint i = 0; i < total; i++)
                {
                    long luid;
                    if (((delegate* unmanaged[Stdcall]<nint, uint, long*, int>)Slot(d3d, 21))(d3d, i, &luid) != 0 ||
                        luid != description.Luid) continue;
                    var config = new D3D9Presentation { Window = window, Windowed = 1, SwapEffect = 1 };
                    nint result;
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, uint, D3D9Presentation*, nint, nint*, int>)Slot(d3d, 20))
                        (d3d, i, 1, window, 0x26, &config, 0, &result), "open matching D3D9Ex adapter");
                    return result;
                }
                return 0;
            }
            finally { Marshal.Release(d3d); }
        }

        private nint OpenInteropSurface(nint texture, int w, int h)
        {
            var resourceId = new Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
            Check(Marshal.QueryInterface(texture, in resourceId, out nint resource), "get DXGI shared resource");
            nint handle;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(resource, 8))(resource, &handle), "get texture share handle");
            }
            finally { Marshal.Release(resource); }
            nint shared;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, uint, uint, nint*, nint*, int>)Slot(device9, 23))
                (device9, (uint)w, (uint)h, 1, 1, 21, 0, &shared, &handle), "open shared settings texture in D3D9Ex");
            try
            {
                nint surface;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(shared, 18))(shared, 0, &surface), "get settings image surface");
                return surface;
            }
            finally { Marshal.Release(shared); }
        }

        private void ReleaseBuffers()
        {
            gpu.SetTarget(0);
            Bind(0);
            Release(ref baseTexture); Release(ref baseTarget); Release(ref baseView);
            Release(ref output); Release(ref outputTarget); Release(ref surface9); Release(ref presentation); Release(ref staging);
            Release(ref text);
        }

        public void Dispose()
        {
            if (gpu.context == 0) return;
            ReleaseBuffers();
            Release(ref fullscreen); Release(ref shapeVertex); Release(ref paint);
            Release(ref lens); Release(ref shadow); Release(ref overlay); Release(ref device9);
            gpu.Dispose();
        }

        private static void Release(ref nint pointer)
        {
            if (pointer == 0) return;
            Marshal.Release(pointer);
            pointer = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D9Presentation
        {
            public uint Width, Height, Format, Count, MultiSample, MultiSampleQuality, SwapEffect;
            public nint Window;
            public int Windowed, EnableDepth;
            public uint DepthFormat, Flags, RefreshRate, Interval;
        }

        [DllImport("d3d9.dll")]
        private static extern int Direct3DCreate9Ex(uint version, out nint instance);
    }
}
