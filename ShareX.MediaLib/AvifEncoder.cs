#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2025 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ShareX.MediaLib
{
    /// <summary>
    /// Writes AVIF stills through ffmpeg, which ShareX already ships for screen recording.
    /// HDR captures are fed in as BT.2100 PQ rgb48le and tagged so viewers light them up correctly;
    /// SDR images take a plain sRGB path.
    /// </summary>
    public static class AvifEncoder
    {
        /// <summary>
        /// AV1 encoders we know how to drive, best first. libaom is the reference AVIF encoder and
        /// is the only one guaranteed to handle 4:4:4 and still-picture mode.
        /// </summary>
        private static readonly string[] preferredEncoders = { "libaom-av1", "libsvtav1", "librav1e" };

        private static readonly Dictionary<string, string> encoderCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object encoderCacheLock = new object();

        /// <summary>
        /// Name of the AV1 encoder this ffmpeg build offers, or null when it cannot write AVIF.
        /// The answer is cached per ffmpeg path.
        /// </summary>
        public static string GetAvailableEncoder(string ffmpegPath)
        {
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                return null;
            }

            lock (encoderCacheLock)
            {
                if (encoderCache.TryGetValue(ffmpegPath, out string cached))
                {
                    return cached;
                }
            }

            string encoder = null;

            try
            {
                string output = RunAndCapture(ffmpegPath, "-hide_banner -loglevel error -encoders", 15000);

                if (output != null)
                {
                    foreach (string candidate in preferredEncoders)
                    {
                        if (output.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                        {
                            encoder = candidate;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Could not query ffmpeg for AV1 encoders.");
            }

            lock (encoderCacheLock)
            {
                encoderCache[ffmpegPath] = encoder;
            }

            return encoder;
        }

        public static bool IsAvailable(string ffmpegPath)
        {
            return GetAvailableEncoder(ffmpegPath) != null;
        }

        /// <summary>
        /// Drops the cached encoder probe, for when the user points ShareX at a different ffmpeg.
        /// </summary>
        public static void ResetEncoderCache()
        {
            lock (encoderCacheLock)
            {
                encoderCache.Clear();
            }
        }

        /// <summary>
        /// Encodes the HDR pixels as a 10 bit BT.2100 PQ AVIF. Returns null when ffmpeg is missing
        /// or the encode fails; the caller is expected to fall back to an SDR format.
        /// </summary>
        public static MemoryStream EncodeHdr(string ffmpegPath, HdrImageData hdr, AvifEncoderOptions options)
        {
            if (hdr == null)
            {
                return null;
            }

            options ??= new AvifEncoderOptions();

            string encoder = GetAvailableEncoder(ffmpegPath);

            if (encoder == null)
            {
                DebugHelper.WriteLine("AVIF: no AV1 encoder available in ffmpeg, cannot write HDR output.");
                return null;
            }

            byte[] pq;

            try
            {
                pq = HdrPixelConverter.ToBt2020Pq48(hdr);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "AVIF: converting HDR pixels to PQ failed.");
                return null;
            }

            float maxCll = hdr.Metadata.MaxCllNits > 0 ? hdr.Metadata.MaxCllNits : HdrPixelConverter.CalculateMaxCllNits(hdr);
            float maxFall = hdr.Metadata.AvgNits;

            StringBuilder args = new StringBuilder();
            args.Append("-hide_banner -loglevel error -y ");
            args.Append(CultureInfo.InvariantCulture, $"-f rawvideo -pix_fmt rgb48le -video_size {hdr.Width}x{hdr.Height} -framerate 1 -i pipe:0 ");
            args.Append("-frames:v 1 -map_metadata -1 ");

            // Two things happen in this filter chain. swscale has to be told to use the BT.2020
            // matrix for the RGB to YUV step, otherwise it silently converts with BT.709
            // coefficients and we would be tagging a lie. setparams then stamps the frame itself:
            // the -color_* output options alone leave the nclx colour box at "unspecified", and an
            // untagged HDR file is displayed as SDR.
            args.Append("-vf \"scale=out_color_matrix=bt2020:out_range=tv," +
                "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=tv\" ");

            AppendEncoderArgs(args, encoder, options, GetHdrPixelFormat(encoder, options), maxCll, maxFall);

            args.Append("-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -color_range tv ");

            return RunEncode(ffmpegPath, args.ToString(), pq, "HDR");
        }

        /// <summary>
        /// Encodes a regular SDR bitmap as an sRGB AVIF, used when the user picks AVIF as their
        /// image format but the capture has no HDR pixels behind it.
        /// </summary>
        public static MemoryStream EncodeSdr(string ffmpegPath, Image image, AvifEncoderOptions options)
        {
            if (image == null)
            {
                return null;
            }

            options ??= new AvifEncoderOptions();

            string encoder = GetAvailableEncoder(ffmpegPath);

            if (encoder == null)
            {
                DebugHelper.WriteLine("AVIF: no AV1 encoder available in ffmpeg.");
                return null;
            }

            string inputPath = Path.Combine(Path.GetTempPath(), "ShareX-avif-" + Path.GetRandomFileName() + ".png");

            try
            {
                using (Bitmap flattened = ImageHelpers.FillBackground(image, Color.White))
                {
                    flattened.Save(inputPath, ImageFormat.Png);
                }

                StringBuilder args = new StringBuilder();
                args.Append("-hide_banner -loglevel error -y ");
                args.Append(CultureInfo.InvariantCulture, $"-i \"{inputPath}\" ");
                args.Append("-frames:v 1 -map_metadata -1 ");
                args.Append("-vf \"setparams=color_primaries=bt709:color_trc=iec61966-2-1:colorspace=bt709:range=tv\" ");

                AppendEncoderArgs(args, encoder, options, GetSdrPixelFormat(encoder, options), 0, 0);

                args.Append("-color_primaries bt709 -color_trc iec61966-2-1 -colorspace bt709 -color_range tv ");

                return RunEncode(ffmpegPath, args.ToString(), null, "SDR");
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "AVIF: SDR encode failed.");
                return null;
            }
            finally
            {
                FileHelpers.DeleteFile(inputPath);
            }
        }

        private static void AppendEncoderArgs(StringBuilder args, string encoder, AvifEncoderOptions options, string pixelFormat,
            float maxCllNits, float maxFallNits)
        {
            args.Append(CultureInfo.InvariantCulture, $"-c:v {encoder} -pix_fmt {pixelFormat} ");

            int quality = Math.Clamp(options.Quality, 0, 63);
            int speed = Math.Clamp(options.Speed, 0, 10);

            switch (encoder)
            {
                case "libaom-av1":
                    args.Append(CultureInfo.InvariantCulture, $"-still-picture 1 -crf {quality} -b:v 0 -cpu-used {speed} ");
                    break;
                case "libsvtav1":
                    args.Append(CultureInfo.InvariantCulture, $"-crf {quality} -preset {Math.Clamp(speed, 0, 13)} ");

                    if (maxCllNits > 0)
                    {
                        // SVT-AV1 is the only one of the three that lets us stamp content light level.
                        args.Append(CultureInfo.InvariantCulture,
                            $"-svtav1-params \"content-light={(int)maxCllNits},{(int)Math.Max(1, maxFallNits)}\" ");
                    }
                    break;
                case "librav1e":
                    args.Append(CultureInfo.InvariantCulture, $"-qp {quality * 4} -speed {speed} ");
                    break;
            }
        }

        private static string GetHdrPixelFormat(string encoder, AvifEncoderOptions options)
        {
            // libsvtav1 and librav1e only reliably take 4:2:0 through ffmpeg; libaom handles 4:4:4,
            // which keeps text edges clean and is what a screenshot tool wants.
            if (options.UseChromaSubsampling || encoder != "libaom-av1")
            {
                return "yuv420p10le";
            }

            return "yuv444p10le";
        }

        private static string GetSdrPixelFormat(string encoder, AvifEncoderOptions options)
        {
            if (options.UseChromaSubsampling || encoder != "libaom-av1")
            {
                return "yuv420p";
            }

            return "yuv444p";
        }

        private static MemoryStream RunEncode(string ffmpegPath, string args, byte[] stdinData, string label)
        {
            string outputPath = Path.Combine(Path.GetTempPath(), "ShareX-avif-" + Path.GetRandomFileName() + ".avif");

            try
            {
                args += $"\"{outputPath}\"";

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    WorkingDirectory = Path.GetDirectoryName(ffmpegPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = stdinData != null,
                    RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8
                };

                DebugHelper.WriteLine($"AVIF ({label}): \"{psi.FileName}\" {psi.Arguments}");

                StringBuilder stderr = new StringBuilder();

                using (Process process = new Process())
                {
                    process.StartInfo = psi;
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            lock (stderr)
                            {
                                stderr.AppendLine(e.Data);
                            }
                        }
                    };

                    process.Start();
                    process.BeginErrorReadLine();

                    if (stdinData != null)
                    {
                        // A 4K frame is ~50 MB, far more than the pipe buffer, so this has to run
                        // while ffmpeg drains the other end or both sides deadlock.
                        Exception writeError = null;

                        Thread writer = new Thread(() =>
                        {
                            try
                            {
                                using (Stream stdin = process.StandardInput.BaseStream)
                                {
                                    stdin.Write(stdinData, 0, stdinData.Length);
                                    stdin.Flush();
                                }
                            }
                            catch (Exception e)
                            {
                                // ffmpeg closing the pipe early (it only wants one frame) is normal.
                                writeError = e;
                            }
                        })
                        {
                            IsBackground = true,
                            Name = "ShareX AVIF stdin"
                        };

                        writer.Start();
                        process.WaitForExit();
                        writer.Join(5000);

                        if (writeError != null && process.ExitCode != 0)
                        {
                            DebugHelper.WriteException(writeError, "AVIF: writing frame to ffmpeg failed.");
                        }
                    }
                    else
                    {
                        process.WaitForExit();
                    }

                    if (process.ExitCode != 0)
                    {
                        DebugHelper.WriteLine($"AVIF ({label}): ffmpeg exited with {process.ExitCode}.{Environment.NewLine}{stderr}");
                        return null;
                    }
                }

                if (!File.Exists(outputPath))
                {
                    DebugHelper.WriteLine($"AVIF ({label}): ffmpeg produced no output file.{Environment.NewLine}{stderr}");
                    return null;
                }

                MemoryStream ms = new MemoryStream(File.ReadAllBytes(outputPath));
                ms.Position = 0;
                return ms;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, $"AVIF ({label}): encode failed.");
                return null;
            }
            finally
            {
                FileHelpers.DeleteFile(outputPath);
            }
        }

        private static string RunAndCapture(string path, string args, int timeout)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(path),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (Process process = new Process())
            {
                process.StartInfo = psi;
                process.Start();

                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

                if (!process.WaitForExit(timeout))
                {
                    process.Kill();
                    return null;
                }

                return output;
            }
        }
    }

    public class AvifEncoderOptions
    {
        /// <summary>
        /// Constant quality value passed to the encoder, 0 (lossless-ish) to 63 (worst).
        /// </summary>
        public int Quality { get; set; } = 20;

        /// <summary>
        /// Encoder speed preset. Higher is faster and larger; 6 keeps a 4K still under a couple of seconds.
        /// </summary>
        public int Speed { get; set; } = 6;

        /// <summary>
        /// Force 4:2:0 instead of 4:4:4. Smaller files, but blurs coloured text.
        /// </summary>
        public bool UseChromaSubsampling { get; set; } = false;
    }
}
