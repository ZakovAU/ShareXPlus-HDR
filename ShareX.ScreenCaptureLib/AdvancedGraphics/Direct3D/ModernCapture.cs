using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using ShareX.HelpersLib;
using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders;
using ShareX.ScreenCaptureLib.AdvancedGraphics.GDI;
using SharpGen.Runtime;
using Veldrid;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.DXGI.Debug;
using Vortice.Mathematics;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public class ModernCapture : IDisposable, DisposableCache
{
#if DEBUG
    private IDXGIDebug1 debug;
#endif
    private DeviceCache deviceCache;
    private IDXGIFactory1 idxgiFactory1;
    private HdrSettings Settings;

    private InputElementDescription[] shaderInputElements =
    [
        new("POSITION", 0, Format.R32G32_Float, 0),
        new("TEXCOORD", 0, Format.R32G32_Float, 0)
    ];

    private byte[] vxShader;
    private byte[] psShader;
    private Blob inputSignatureBlob;

    public ModernCapture(HdrSettings settings)
    {
#if DEBUG
        DXGI.DXGIGetDebugInterface1(out debug).CheckError();
#endif

        Settings = settings;
        deviceCache = new DeviceCache(InitializeDevice);
        idxgiFactory1 = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        InitializeShaders();
        if (settings.SaveDevices)
        {
            deviceCache.Init(idxgiFactory1);
        }
    }

    private void ReInit()
    {
        Dispose();
#if DEBUG
        DXGI.DXGIGetDebugInterface1(out debug).CheckError();
#endif
        deviceCache = new DeviceCache(InitializeDevice);
        idxgiFactory1 = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (Settings.SaveDevices)
        {
            deviceCache.Init(idxgiFactory1);
        }
    }

    private void PrintDebug()
    {
#if DEBUG
        debug.ReportLiveObjects(DXGI.DebugAll,ReportLiveObjectFlags.Summary);
        // TODO: how to do this correctly?
        var idxgiInfoQueue = debug.QueryInterface<IDXGIInfoQueue>();
        var infoQueueMessage = idxgiInfoQueue.GetMessage(DXGI.DebugAll, 0);
        Console.WriteLine(infoQueueMessage.Description);
#endif
    }

    private readonly Dictionary<IntPtr /*hmon*/, DuplicationState> _duplications = new();
    private readonly Lock _lock = new(); // makes first-time creation threadsafe

    private sealed class DuplicationState(IDXGIOutputDuplication dup, ID3D11Texture2D staging, bool isHdr, ID3D11Device device) : IDisposable, DisposableCache
    {
        public IDXGIOutputDuplication Dup { get; } = dup;
        public ID3D11Texture2D Staging { get; set; } = staging;
        public bool IsHdr { get; } = isHdr;

        public ID3D11Device Device = device;

        public void ReleaseFrame(bool includeBuffer)
        {
            Dup?.ReleaseFrame();
            if (includeBuffer)
            {
                Staging?.Dispose();
                Staging = null;
            }
        }

        public void Dispose()
        {
            Dup?.Dispose();
            Staging?.Dispose();
        }

        public void ReleaseCachedValues(HdrSettings settings)
        {
            ReleaseFrame(!settings.ReuseBuffers);
        }
    }

    private DeviceCache GetCache()
    {
        // deviceCache.Dispose();
        // deviceCache = new DeviceCache(InitializeDevice);
        // deviceCache.Init(idxgiFactory1);
        return deviceCache;
    }

    private DuplicationState GetOrCreateDup(IntPtr hmon, bool forceRecreate = false)
    {
        lock (_lock)
        {
            if (_duplications.Count > MonitorEnumerationHelper.GetMonitorsCount())
            {
                foreach (var duplicationsValue in _duplications.Values)
                {
                    duplicationsValue.Dispose();
                }

                _duplications.Clear();
            }

            if (_duplications.TryGetValue(hmon, out var state))
            {
                if (!forceRecreate)
                {
                    // The staging buffer is torn down after every capture unless ReuseBuffers is on,
                    // so recreate it only when it is actually gone. That keeps repeat calls within a
                    // single capture free.
                    state.Staging ??= CreateStagingBuffer(state.Device, state.Dup.Description);
                    return state;
                }

                state.Dup.Dispose();
                state.Staging?.Dispose();
            }

            // your helper:
            var screen = GetCache().GetOutputForScreen(idxgiFactory1, hmon);

            // Ask for native format first, SDR fallback second
            var fmts = new[] { Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm };

            IDXGIOutputDuplication dup;
            try
            {
                using IDXGIOutput5 output5 = screen.Output.QueryInterface<IDXGIOutput5>();
                dup = output5.DuplicateOutput1(screen.Device, fmts);
            }
            catch (SharpGenException)
            {
                // DuplicateOutput1 fails with DXGI_ERROR_UNSUPPORTED on some
                // drivers/sessions; the older DuplicateOutput hands us the
                // desktop in its native format (R16G16B16A16_Float on HDR desktops).
                using IDXGIOutput1 output1 = screen.Output.QueryInterface<IDXGIOutput1>();
                dup = output1.DuplicateOutput(screen.Device);
            }

            var desc = dup.Description;
            bool isHdr = desc.ModeDescription.Format == Format.R16G16B16A16_Float;

            state = new DuplicationState(dup, CreateStagingBuffer(screen.Device, desc), isHdr, screen.Device);
            _duplications[hmon] = state;
            return state;
        }
    }

    private ID3D11Texture2D CreateStagingBuffer(ID3D11Device device, OutduplDescription desc)
    {
        var texDesc = new Texture2DDescription
        {
            Width = desc.ModeDescription.Width,
            Height = desc.ModeDescription.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.ModeDescription.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read | CpuAccessFlags.Write
        };
        return device.CreateTexture2D(texDesc);
    }


    /// Temporary struct to carry each region’s state
    private class RegionTempState
    {
        public ModernCaptureMonitorDescription Region;
        public DeviceAccess DeviceAccess;
        public ID3D11Device Device;
        public ID3D11DeviceContext Context;
        public Rectangle SrcRect;
        public bool IsHdr;
    }

    public Bitmap CaptureAndProcess(HdrSettings hdrSettings, ModernCaptureItemDescription item)
    {
        // TODO: support multi-gpu setups
        item.Regions = CursorFilter.FilterByCursorGpu(deviceCache, idxgiFactory1, item.Regions);
        Settings = hdrSettings;
        List<DisposableCache> disposableCaches = [];
        try
        {
            bool forceCpuTonemap = false;

            // (A) First pass: discover if all Regions live on the *same* ID3D11Device, and gather per-region state:
            ID3D11Device commonDevice = null;
            ID3D11DeviceContext commonCtx = null;
            bool hasCommonDevice = true;
            var perRegionState = new List<RegionTempState>();
            ID3D11Device firstDevice = null;

            foreach (var r in item.Regions)
            {
                // 2) Grab the D3D11Device + Context for this monitor from your cache:
                var screenAccess = GetCache().GetOutputForScreen(idxgiFactory1, r.MonitorInfo.Hmon);
                ID3D11Device device = screenAccess.Device;
                ID3D11DeviceContext ctx = screenAccess.Context.Device.ImmediateContext;

                // 3) If this is the first region, capture its device as "common"; else check equality:
                if (commonDevice == null)
                {
                    commonDevice = device;
                    commonCtx = ctx;
                }
                else if (!ReferenceEquals(commonDevice, device))
                {
                    hasCommonDevice = false;
                    break;
                }

                // 4) Compute this region’s SrcRect (pixel‐coords inside the monitor texture):
                var srcRect = new Rectangle(
                    r.DestGdiRect.X - r.MonitorInfo.MonitorArea.X,
                    r.DestGdiRect.Y - r.MonitorInfo.MonitorArea.Y,
                    r.DestGdiRect.Width,
                    r.DestGdiRect.Height
                );

                perRegionState.Add(new RegionTempState
                {
                    Region = r,
                    Device = device,
                    DeviceAccess = screenAccess.Context,
                    Context = ctx,
                    SrcRect = srcRect,
                    IsHdr = GetOrCreateDup(r.MonitorInfo.Hmon).IsHdr,
                });
            }

            if (!hasCommonDevice)
            {
                throw new Exception("💀 We currently don't support screenshots across multiple GPUs");
            }
#if DEBUG
            var loaded = RenderDoc.Load(out var lib);
            if (loaded && lib != null) lib.StartFrameCapture();
#endif

            // (B) If GPU composition is allowed, create one big GPU canvas now:
            ID3D11Texture2D canvasGpu = null;
            ID3D11DeviceContext canvasContext = null;
            int W = item.CanvasRect.Width;
            int H = item.CanvasRect.Height;

            canvasGpu = Direct3DUtils.CreateCanvasTexture((uint)W, (uint)H, commonDevice);
            canvasContext = commonCtx;

            // (C) When any monitor in this capture is running HDR, build a second canvas that keeps
            // the pixels as they came off the desktop so we can write real HDR output later.
            ID3D11Texture2D hdrCanvasGpu = null;
            byte[] hdrPixels = null;
            List<Rectangle> hdrDestRects = null;
            List<ImageInfo> hdrRegionInfos = null;

            if (Settings.KeepHdrPixels && perRegionState.Exists(s => s.IsHdr))
            {
                // The managed buffer is left until we know the capture actually contains HDR
                // content, because at 4K it is 66 MB of large object heap for nothing.
                hdrCanvasGpu = Direct3DUtils.CreateHdrCanvasTexture((uint)W, (uint)H, commonDevice);
                hdrDestRects = [];
                hdrRegionInfos = [];
            }

            // (D) Now actually do one pass per region:
            foreach (var state in perRegionState)
            {
                var r = state.Region;
                var device = state.Device;
                var ctx = state.Context;
                var srcRect = state.SrcRect;

                // 1) AcquireNextFrame:
                var dupState = GetOrCreateDup(state.Region.MonitorInfo.Hmon);
                IDXGIResource resourcee = null;
                OutduplFrameInfo outduplFrameInfo = default;
                bool acquired = false;

                // Bounded retry loop. The old version spun until a frame with a fresh present
                // arrived, which never happens on an idle desktop (infinite WaitTimeout loop)
                // and leaked the undisposable resource of every stale frame it threw away.
                const int maxAcquireAttempts = 100; // 100 x 10ms timeout = ~1s worst case

                for (int attempt = 0; attempt < maxAcquireAttempts && !acquired; attempt++)
                {
                    dupState.Dup.ReleaseFrame();
                    resourcee?.Dispose();
                    resourcee = null;

                    Result acquireNextFrame = dupState.Dup.AcquireNextFrame(10, out outduplFrameInfo, out resourcee);

                    if (acquireNextFrame.Failure)
                    {
                        resourcee?.Dispose();
                        resourcee = null;

                        if (acquireNextFrame.ApiCode != "WaitTimeout")
                        {
                            // AccessLost / InvalidCall: the duplication is dead, recreate it.
                            dupState.Dup.ReleaseFrame();
                            dupState = GetOrCreateDup(state.Region.MonitorInfo.Hmon, true);
                        }

                        continue;
                    }

                    // A frame with LastPresentTime == 0 still contains the current desktop image
                    // (that is what the first acquire after (re)creating the duplication returns),
                    // so after a few tries accept it instead of waiting forever for a new present.
                    acquired = outduplFrameInfo.LastPresentTime != 0 || attempt >= 5;
                }

                if (!acquired || resourcee == null)
                {
                    // The desktop never presented a frame; bail out so the caller can fall back
                    // to GDI capture instead of hanging or returning a black bitmap.
                    throw new ApplicationException("Desktop duplication produced no frame.");
                }

                using var resource = resourcee;
                using var frameTex = resource.QueryInterface<ID3D11Texture2D>();

                // 2) Copy GPU→staging (float or unorm, depending on format):
                ctx.CopyResource(dupState.Staging, frameTex);

                ID3D11Texture2D ldrSource = dupState.Staging;


                //   destBox is where to place it in the big canvas
                var destBox = new Box
                {
                    Left = r.DestGdiRect.X - item.CanvasRect.Left,
                    Top = r.DestGdiRect.Y - item.CanvasRect.Top,
                    Front = 0,
                    Back = 1,
                    Right = (r.DestGdiRect.X - item.CanvasRect.Left) + r.DestGdiRect.Width,
                    Bottom = ( r.DestGdiRect.Y - item.CanvasRect.Top) + r.DestGdiRect.Height
                };

                //   srcBox is the sub‐rectangle inside ldrSource
                var srcBox = new Box
                {
                    Left = srcRect.X,
                    Top = srcRect.Y,
                    Front = 0,
                    Back = 1,
                    Right = srcRect.Right,
                    Bottom = srcRect.Bottom
                };

                if (dupState.IsHdr)
                {
                    if (!forceCpuTonemap)
                    {
                        // GPU path: convert HDR staging → B8G8R8A8_UNorm GPU texture
                        ldrSource = Tonemapping.TonemapOnGpu(Settings, state.Region, state.DeviceAccess, dupState.Staging, frameTex, canvasGpu, destBox, srcBox,
                            out ImageInfo regionInfo);
                        hdrRegionInfos?.Add(regionInfo);
                    }
                    else
                    {
                        // CPU path: convert HDR staging → B8G8R8A8_UNorm STAGING
                        ldrSource = Tonemapping.TonemapOnCpu(Settings, state.Region, state.DeviceAccess, frameTex);
                    }

                    if (hdrCanvasGpu != null)
                    {
                        // Same format on both sides, so the untonemapped pixels move straight across.
                        canvasContext.CopySubresourceRegion(hdrCanvasGpu, 0, (uint)destBox.Left, (uint)destBox.Top, 0, frameTex, 0, srcBox);
                        hdrDestRects.Add(new Rectangle(destBox.Left, destBox.Top, r.DestGdiRect.Width, r.DestGdiRect.Height));
                    }
                }
                else
                {
                    canvasContext.CopySubresourceRegion(
                        canvasGpu, // destination (big canvas)
                        0, // dest mip
                        (uint)destBox.Left, // dest X offset in canvas
                        (uint)destBox.Top, // dest Y offset in canvas
                        0, // dest Z
                        ldrSource, // source texture (either GPU‐tonemapped or staging if it was already unorm)
                        0, // source mip
                        srcBox
                    );

                    if (hdrCanvasGpu != null)
                    {
                        // An SDR monitor sharing the capture with an HDR one. Its 8 bit sRGB pixels
                        // cannot be copied into a float canvas by the GPU, so lift them into scRGB here.
                        hdrPixels ??= new byte[(long)W * H * HdrImageData.BytesPerPixel];
                        ConvertSdrRegionToScRgb(canvasContext, dupState.Staging, srcBox, hdrPixels, W,
                            destBox.Left, destBox.Top);
                    }
                }
                dupState.ReleaseFrame(!Settings.ReuseBuffers);
            } // end per‐region loop

            // 1) Copy GPU canvas → staging
            using var stagingCanvas = Direct3DUtils.CreateStagingFor(canvasGpu);
            canvasContext.CopyResource(stagingCanvas, canvasGpu);

            // 2) Map once, then build a Bitmap from that pointer
            var descSt = stagingCanvas.Description;
            var mapped = canvasContext.Map(stagingCanvas, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            Bitmap finalBitmap = BitmapUtils.BuildBitmapFromMappedPointer(
                mapped.DataPointer,
                (int)mapped.RowPitch,
                (int)descSt.Width,
                (int)descSt.Height
            );
            canvasContext.Unmap(stagingCanvas, 0);

            if (hdrCanvasGpu != null)
            {
                if (hdrRegionInfos.Exists(i => i.Hdr))
                {
                    hdrPixels ??= new byte[(long)W * H * HdrImageData.BytesPerPixel];
                    ReadBackHdrCanvas(canvasContext, hdrCanvasGpu, hdrDestRects, hdrPixels, W);
                    AttachHdrPayload(finalBitmap, hdrPixels, W, H, hdrRegionInfos);
                }
                else
                {
                    // HDR desktop, but nothing in frame is brighter than SDR white. Keeping the
                    // pixels would just cost memory and force an AVIF encode for no benefit.
                    Console.WriteLine("CaptureAndProcess(): no HDR content in frame, keeping SDR output only.");
                }
            }

            hdrCanvasGpu?.Dispose();
            canvasGpu.Dispose();
            stagingCanvas.Dispose();
#if DEBUG
            if (loaded && lib != null) lib.EndFrameCapture();
#endif
            return finalBitmap;
        }
        catch (Exception e)
        {
            // somethingn went wrong, so lets scram
            foreach (var disposableCache in disposableCaches)
            {
                disposableCache.ReleaseCachedValues(Settings);
            }

            ReInit();

            throw new ApplicationException("HDR screenshot failed", e);
        }
        finally
        {
            foreach (var disposableCache in disposableCaches)
            {
                disposableCache.ReleaseCachedValues(Settings);
            }
            this.ReleaseCachedValues(Settings);
        }
    }

    /// <summary>
    /// Pulls the half float canvas back into managed memory, but only the rectangles an HDR monitor
    /// actually wrote, so any SDR regions already converted in place survive.
    /// </summary>
    private static void ReadBackHdrCanvas(ID3D11DeviceContext ctx, ID3D11Texture2D hdrCanvasGpu, List<Rectangle> rects, byte[] dest, int canvasWidth)
    {
        using var hdrStaging = Direct3DUtils.CreateStagingFor(hdrCanvasGpu);
        ctx.CopyResource(hdrStaging, hdrCanvasGpu);

        var mapped = ctx.Map(hdrStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

        try
        {
            int destStride = canvasWidth * HdrImageData.BytesPerPixel;

            unsafe
            {
                byte* src = (byte*)mapped.DataPointer;

                fixed (byte* destBase = dest)
                {
                    foreach (Rectangle rect in rects)
                    {
                        int rowBytes = rect.Width * HdrImageData.BytesPerPixel;

                        for (int y = 0; y < rect.Height; y++)
                        {
                            Buffer.MemoryCopy(
                                src + ((rect.Y + y) * (long)mapped.RowPitch) + (rect.X * HdrImageData.BytesPerPixel),
                                destBase + ((rect.Y + y) * (long)destStride) + (rect.X * HdrImageData.BytesPerPixel),
                                rowBytes,
                                rowBytes);
                        }
                    }
                }
            }
        }
        finally
        {
            ctx.Unmap(hdrStaging, 0);
        }
    }

    /// <summary>
    /// Lifts an SDR monitor's 8 bit sRGB pixels into the scRGB half float canvas, placing SDR white
    /// at the configured HDR reference white so the region does not look washed out next to real
    /// HDR content.
    /// </summary>
    private void ConvertSdrRegionToScRgb(ID3D11DeviceContext ctx, ID3D11Texture2D sdrStaging, Box srcBox, byte[] dest, int canvasWidth,
        int destX, int destY)
    {
        float whiteScale = Math.Max(1f, Settings.HdrBrightnessNits) / 80f;
        ushort[] lut = BuildSrgbToScRgbLut(whiteScale);
        const ushort halfOne = 0x3C00;

        var mapped = ctx.Map(sdrStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

        try
        {
            int width = srcBox.Right - srcBox.Left;
            int height = srcBox.Bottom - srcBox.Top;
            int destStride = canvasWidth * HdrImageData.BytesPerPixel;

            unsafe
            {
                byte* srcBase = (byte*)mapped.DataPointer;

                for (int y = 0; y < height; y++)
                {
                    // Desktop duplication hands out B8G8R8A8_UNorm.
                    byte* srcRow = srcBase + ((srcBox.Top + y) * (long)mapped.RowPitch) + (srcBox.Left * 4);
                    int destOffset = ((destY + y) * destStride) + (destX * HdrImageData.BytesPerPixel);

                    for (int x = 0; x < width; x++)
                    {
                        WriteHalf(dest, destOffset, lut[srcRow[2]]);
                        WriteHalf(dest, destOffset + 2, lut[srcRow[1]]);
                        WriteHalf(dest, destOffset + 4, lut[srcRow[0]]);
                        WriteHalf(dest, destOffset + 6, halfOne);

                        srcRow += 4;
                        destOffset += HdrImageData.BytesPerPixel;
                    }
                }
            }
        }
        finally
        {
            ctx.Unmap(sdrStaging, 0);
        }
    }

    private static ushort[] BuildSrgbToScRgbLut(float whiteScale)
    {
        var lut = new ushort[256];

        for (int i = 0; i < lut.Length; i++)
        {
            float encoded = i / 255f;
            float linear = encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
            lut[i] = BitConverter.HalfToUInt16Bits((Half)(linear * whiteScale));
        }

        return lut;
    }

    private static void WriteHalf(byte[] buffer, int offset, ushort bits)
    {
        buffer[offset] = (byte)bits;
        buffer[offset + 1] = (byte)(bits >> 8);
    }

    private void AttachHdrPayload(Bitmap bitmap, byte[] pixels, int width, int height, List<ImageInfo> regionInfos)
    {
        if (bitmap == null || pixels == null)
        {
            return;
        }

        var metadata = new HdrImageMetadata
        {
            IsHdrDisplay = true,
            MinNits = float.MaxValue
        };

        float avgTotal = 0;

        foreach (ImageInfo info in regionInfos)
        {
            metadata.MaxNits = Math.Max(metadata.MaxNits, info.MaxNits);
            metadata.MinNits = Math.Min(metadata.MinNits, info.MinNits);
            metadata.P99Nits = Math.Max(metadata.P99Nits, info.P99Nits);
            metadata.MaxCllNits = Math.Max(metadata.MaxCllNits, info.MaxCLL * 80f);
            metadata.HasHdrContent |= info.Hdr;
            avgTotal += info.AvgNits;
        }

        metadata.AvgNits = avgTotal / regionInfos.Count;

        if (metadata.MinNits == float.MaxValue)
        {
            metadata.MinNits = 0;
        }

        HdrImageRegistry.Attach(bitmap, new HdrImageData(width, height, pixels, metadata));
    }

    private void InitializeDevice(DeviceAccess deviceAccess)
    {
        var device = deviceAccess.Device;
        deviceAccess.pxShader = device.CreatePixelShader(psShader);
        deviceAccess.vxShader = device.CreateVertexShader(vxShader);

        deviceAccess.inputLayout = device.CreateInputLayout(shaderInputElements, inputSignatureBlob);

        var samplerDesc = new SamplerDescription
        {
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxLOD = float.MaxValue,
            BorderColor = new Color4(0, 0, 0, 0),
            Filter = Filter.MinMagMipLinear
        };

        deviceAccess.samplerState = device.CreateSamplerState(samplerDesc);
    }

    private void InitializeShaders()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using (var vxShaderStream = assembly.GetManifestResourceStream($"{ShaderConstants.ResourcePrefix}.PostProcessingQuad.cso"))
        {
            vxShader = new byte[vxShaderStream.Length];
            vxShaderStream.ReadExactly(vxShader);
            inputSignatureBlob = Vortice.D3DCompiler.Compiler.GetInputSignatureBlob(vxShader);
        }

        using (var psShaderStream = assembly.GetManifestResourceStream($"{ShaderConstants.ResourcePrefix}.PostProcessingColor.cso"))
        {
            psShader = new byte[psShaderStream.Length];
            psShaderStream.ReadExactly(psShader);
        }
    }

    public void Dispose()
    {
        foreach (var duplicationsValue in _duplications.Values)
        {
            duplicationsValue.Dispose();
        }
        _duplications.Clear();
        deviceCache?.Dispose();
        deviceCache = null;
        idxgiFactory1?.Dispose();
        idxgiFactory1 = null;
#if DEBUG
        debug?.Dispose();
        debug = null;
#endif
    }

    public void ReleaseCachedValues(HdrSettings settings)
    {
        if (!settings.AvoidBuffering) return;
        foreach (var duplicationsValue in _duplications.Values)
        {
            duplicationsValue.Dispose();
        }
        _duplications.Clear();
        deviceCache?.ReleaseCachedValues(settings);
    }
}