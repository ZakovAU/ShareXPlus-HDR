using System.Numerics;
using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders;
using ShareX.ScreenCaptureLib.AdvancedGraphics.GDI;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public static class ShaderConstantHelper // naming is hard
{
    public static void GetShaderConstants(MonitorInfo monitorInfo, HdrSettings settings, ImageInfo imageInfo, out VertexShaderConstants vertexShader,
        out PixelShaderConstants pixelShader)
    {
        // white level, isHDR, is10bpc, is16bpc
        vertexShader = new VertexShaderConstants
        {
            LuminanceScale = new Vector4(1.0f, 0.0f, 0.0f, 0.0f)
        };

        bool isHdr = false;
        uint bitsPerColor = 8;
        uint sdrWhiteLevel = 80;
        float maxFullFrameLuminance = 600;
        float maxLuminance = 600;
        float minLuminance = 0.0f;
        float maxContentLuminance = settings.Use99ThPercentileMaxCll ? imageInfo.P99Nits : imageInfo.MaxNits;

        pixelShader = new PixelShaderConstants()
        {
            DisplayMaxLuminance = maxLuminance / 80,
            HdrMaxLuminance = maxContentLuminance / 80,
            UserBrightnessScale = settings.BrightnessScale / 100,
            TonemapType = (uint)settings.HdrToneMapType,
        };

        monitorInfo.QueryMonitorData((colorInfoNullable, sdrInfoNullable, output6) =>
        {
            if (colorInfoNullable.HasValue)
            {
                var colorInfo = colorInfoNullable.Value;
                isHdr = (colorInfo.AdvancedColorStatus & AdvancedColorStatus.AdvancedColorEnabled) == AdvancedColorStatus.AdvancedColorEnabled;
                bitsPerColor = colorInfo.BitsPerColorChannel;
            }

            if (sdrInfoNullable.HasValue)
            {
                var sdrInfo = sdrInfoNullable.Value;
                sdrWhiteLevel = sdrInfo.SDRWhiteLevel;
            }

            if (output6 != null)
            {
                bitsPerColor = output6.Description1.BitsPerColor;
                maxFullFrameLuminance = output6.Description1.MaxFullFrameLuminance;
                maxLuminance = output6.Description1.MaxLuminance;
                minLuminance = output6.Description1.MinLuminance;
            }
        });

        pixelShader.DisplayMaxLuminance = maxLuminance / 80;
        pixelShader.TonemapType = (uint)settings.HdrToneMapType;

        // DisplayConfig reports SDRWhiteLevel as a multiplier of 80 nits * 1000
        // (i.e. 1000 == 80 nits), convert to scRGB units where 1.0 == 80 nits.
        pixelShader.SdrWhiteLevel = (float)(sdrWhiteLevel / 1000.0 * (settings.SdrWhiteScale / 100.0));

        // The render target is always an 8-bit B8G8R8A8_UNorm canvas that stores
        // sRGB-encoded SDR pixels (HDR output formats are not currently supported),
        // so the shader must take the 8 bpc path: lum.x = 1.0 (no extra scaling),
        // isHDR/is10bpc/is16bpc output flags all 0. The shader still receives the
        // HDR source texture and tonemaps it down to the SDR range first.
        vertexShader.LuminanceScale = new Vector4(1.0f, 0.0f, 0.0f, 0.0f);
    }
}