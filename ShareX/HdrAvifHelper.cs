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
using ShareX.MediaLib;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace ShareX
{
    /// <summary>
    /// Decides when a capture should leave ShareX as HDR AVIF and produces the encoded bytes.
    /// </summary>
    public static class HdrAvifHelper
    {
        /// <summary>
        /// True when the image still carries the HDR pixels from capture and the user wants HDR
        /// captures written as AVIF.
        /// </summary>
        public static bool ShouldUseAvif(Image img, TaskSettings taskSettings)
        {
            if (img == null || taskSettings == null || !taskSettings.ImageSettings.ImageAutoUseAVIFForHDR)
            {
                return false;
            }

            return HdrImageRegistry.HasHdrContent(img);
        }

        public static AvifEncoderOptions GetEncoderOptions(TaskSettings taskSettings)
        {
            return new AvifEncoderOptions
            {
                Quality = taskSettings.ImageSettings.ImageAVIFQuality,
                Speed = taskSettings.ImageSettings.ImageAVIFSpeed,
                UseChromaSubsampling = taskSettings.ImageSettings.ImageAVIFChromaSubsampling
            };
        }

        public static string GetFFmpegPath(TaskSettings taskSettings)
        {
            return taskSettings.CaptureSettings.FFmpegOptions.FFmpegPath;
        }

        /// <summary>
        /// Encodes the HDR pixels behind <paramref name="img"/> as AVIF. Returns null when there are
        /// no HDR pixels, ffmpeg cannot write AV1, or the encode fails, in which case the caller
        /// should carry on with the normal SDR format.
        /// </summary>
        public static byte[] EncodeHdr(Image img, TaskSettings taskSettings)
        {
            HdrImageData hdr = HdrImageRegistry.Get(img);

            if (hdr == null)
            {
                return null;
            }

            if (!hdr.IsSameSize(img))
            {
                DebugHelper.WriteLine($"HDR AVIF: payload is {hdr.Width}x{hdr.Height} but the image is {img.Width}x{img.Height}, skipping HDR output.");
                return null;
            }

            string ffmpegPath = GetFFmpegPath(taskSettings);

            if (!File.Exists(ffmpegPath))
            {
                DebugHelper.WriteLine("HDR AVIF: ffmpeg was not found, falling back to SDR output. " +
                    "Download it from the screen recorder options to get HDR screenshots.");
                return null;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();

            using (MemoryStream ms = AvifEncoder.EncodeHdr(ffmpegPath, hdr, GetEncoderOptions(taskSettings)))
            {
                if (ms == null)
                {
                    return null;
                }

                byte[] data = ms.ToArray();

                DebugHelper.WriteLine($"HDR AVIF: encoded {img.Width}x{img.Height} ({hdr.Metadata.MaxNits:0} nits peak) " +
                    $"into {data.Length / 1024} KiB in {stopwatch.ElapsedMilliseconds} ms.");

                return data;
            }
        }

        /// <summary>
        /// Encodes any image as AVIF, using the HDR pixels when they are available and the SDR
        /// bitmap otherwise. Used when the user explicitly selects AVIF as their image format.
        /// </summary>
        public static MemoryStream Encode(Image img, TaskSettings taskSettings)
        {
            byte[] hdrData = EncodeHdr(img, taskSettings);

            if (hdrData != null)
            {
                return new MemoryStream(hdrData);
            }

            string ffmpegPath = GetFFmpegPath(taskSettings);

            if (!File.Exists(ffmpegPath))
            {
                return null;
            }

            return AvifEncoder.EncodeSdr(ffmpegPath, img, GetEncoderOptions(taskSettings));
        }

        /// <summary>
        /// Frees the HDR pixels once the capture has been written out, rather than waiting for the
        /// bitmap itself to be collected. A 4K payload is 66 MB.
        /// </summary>
        public static void Release(Image img)
        {
            HdrImageRegistry.Detach(img);
        }
    }
}
