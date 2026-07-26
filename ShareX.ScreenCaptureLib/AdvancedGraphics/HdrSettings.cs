using System;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics;

public class HdrSettings
{
    public const float MinHdrBrightnessNits = 80;
    public const float MaxHdrBrightnessNits = 10000; // PQ hard limit
    public const float MinScale = 0;
    public const float MaxScale = 2000;

    private float hdrBrightnessNits = 203;

    /// <summary>
    /// Target white level (in nits) used when converting HDR content for an HDR display.
    /// 203 nits is the ITU reference white for HDR graphics; raise it toward your
    /// display's peak brightness when HDR output is used.
    /// </summary>
    public float HdrBrightnessNits
    {
        get => hdrBrightnessNits;
        set => hdrBrightnessNits = Math.Clamp(value, MinHdrBrightnessNits, MaxHdrBrightnessNits);
    }

    private float brightnessScale = 100;

    /// <summary>
    /// User brightness adjustment in percent (100 = no change).
    /// </summary>
    public float BrightnessScale
    {
        get => brightnessScale;
        set => brightnessScale = Math.Clamp(value, 1, MaxScale);
    }

    private float sdrWhiteScale = 100;

    /// <summary>
    /// SDR white level adjustment in percent (100 = use the Windows SDR white level as-is).
    /// </summary>
    public float SdrWhiteScale
    {
        get => sdrWhiteScale;
        set => sdrWhiteScale = Math.Clamp(value, MinScale, MaxScale);
    }

    public bool Use99ThPercentileMaxCll { get; set; } = true;
    public HdrMode HdrMode { get; set; } = HdrMode.Hdr16Bpc;
    public HdrToneMapType HdrToneMapType { get; set; } = HdrToneMapType.NormalizeToCll;

    /// <summary>
    /// Keep the untonemapped scRGB pixels alongside the SDR bitmap so the capture can be written
    /// out as HDR (AVIF). Costs width * height * 8 bytes per HDR capture.
    /// </summary>
    public bool KeepHdrPixels { get; set; } = true;

    /// <summary>
    /// Above this many nits the capture is treated as containing real HDR content rather than
    /// SDR content that happens to be on an HDR desktop. 80 nits is scRGB 1.0, i.e. SDR white.
    /// </summary>
    public float HdrContentThresholdNits { get; set; } = 100;

    public PerformanceMode PerformanceMode { get; set; } = PerformanceMode.Balanced;

    /// <summary>
    /// Keep the desktop duplication staging buffers alive between captures.
    /// </summary>
    public bool ReuseBuffers => PerformanceMode is PerformanceMode.Max;

    /// <summary>
    /// Skip the managed pixel copy and read directly from the mapped staging texture,
    /// and release cached duplications after each capture.
    /// </summary>
    public bool AvoidBuffering => PerformanceMode is PerformanceMode.SaveMemory or PerformanceMode.LowMemory;

    /// <summary>
    /// Keep D3D11 devices (and their shaders) alive between captures.
    /// </summary>
    public bool SaveDevices => PerformanceMode is PerformanceMode.Max or PerformanceMode.Balanced;
}

public enum PerformanceMode
{
    Max,
    Balanced,
    SaveMemory,
    LowMemory
}
