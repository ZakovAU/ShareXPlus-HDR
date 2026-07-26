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

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// Converts captured scRGB half float pixels into the BT.2100 PQ encoding that HDR still
    /// image formats expect.
    /// </summary>
    public static class HdrPixelConverter
    {
        /// <summary>
        /// scRGB defines 1.0 as 80 nits, PQ is absolute and tops out at 10000 nits.
        /// </summary>
        private const float ScRgbToPqScale = 80f / 10000f;

        // SMPTE ST 2084 inverse EOTF constants.
        private const float PqM1 = 2610f / 16384f;
        private const float PqM2 = 2523f / 4096f * 128f;
        private const float PqC1 = 3424f / 4096f;
        private const float PqC2 = 2413f / 4096f * 32f;
        private const float PqC3 = 2392f / 4096f * 32f;

        // Linear Rec.709 to linear Rec.2020, D65 white point preserved (BT.2087).
        private const float R709To2020_RR = 0.6274039f, R709To2020_RG = 0.3292830f, R709To2020_RB = 0.0433131f;
        private const float R709To2020_GR = 0.0690973f, R709To2020_GG = 0.9195404f, R709To2020_GB = 0.0113623f;
        private const float R709To2020_BR = 0.0163914f, R709To2020_BG = 0.0880132f, R709To2020_BB = 0.8955953f;

        /// <summary>
        /// Produces a tightly packed rgb48le buffer (16 bit per channel, little endian, no alpha)
        /// holding BT.2020 primaries with the PQ transfer function, which is what ffmpeg's rawvideo
        /// demuxer wants as input for an HDR10 style encode.
        /// </summary>
        public static byte[] ToBt2020Pq48(HdrImageData hdr)
        {
            if (hdr == null)
            {
                throw new ArgumentNullException(nameof(hdr));
            }

            int width = hdr.Width;
            int height = hdr.Height;
            int destStride = width * 6;

            byte[] source = hdr.Pixels;
            byte[] dest = new byte[(long)destStride * height];

            Parallel.For(0, height, y =>
            {
                // Casting per row inside the lambda keeps the spans out of the closure (ref structs
                // cannot be captured) and removes the per byte assembly and bounds checks from the
                // inner loop. The buffer is tightly packed, so row y starts at y * width pixels.
                ReadOnlySpan<Half> srcRow = MemoryMarshal.Cast<byte, Half>(source.AsSpan()).Slice(y * width * 4, width * 4);
                Span<ushort> destRow = MemoryMarshal.Cast<byte, ushort>(dest.AsSpan()).Slice(y * width * 3, width * 3);

                int s = 0;
                int d = 0;

                for (int x = 0; x < width; x++)
                {
                    float r = (float)srcRow[s];
                    float g = (float)srcRow[s + 1];
                    float b = (float)srcRow[s + 2];
                    s += 4;

                    // scRGB is allowed to go negative to address colours outside Rec.709; most of
                    // those land inside Rec.2020, and whatever still does not gets clipped by the PQ curve.
                    float r2020 = (R709To2020_RR * r) + (R709To2020_RG * g) + (R709To2020_RB * b);
                    float g2020 = (R709To2020_GR * r) + (R709To2020_GG * g) + (R709To2020_GB * b);
                    float b2020 = (R709To2020_BR * r) + (R709To2020_BG * g) + (R709To2020_BB * b);

                    destRow[d] = LinearToPq(r2020);
                    destRow[d + 1] = LinearToPq(g2020);
                    destRow[d + 2] = LinearToPq(b2020);
                    d += 3;
                }
            });

            return dest;
        }

        /// <summary>
        /// Highest single channel value in the image, expressed in nits. This is the MaxCLL an
        /// encoder wants to advertise.
        /// </summary>
        public static float CalculateMaxCllNits(HdrImageData hdr)
        {
            if (hdr == null)
            {
                return 0;
            }

            byte[] source = hdr.Pixels;
            int width = hdr.Width;
            float[] rowMax = new float[hdr.Height];

            Parallel.For(0, hdr.Height, y =>
            {
                ReadOnlySpan<Half> row = MemoryMarshal.Cast<byte, Half>(source.AsSpan()).Slice(y * width * 4, width * 4);
                float max = 0;

                for (int i = 0; i < row.Length; i += 4)
                {
                    max = MathF.Max(max, (float)row[i]);
                    max = MathF.Max(max, (float)row[i + 1]);
                    max = MathF.Max(max, (float)row[i + 2]);
                }

                rowMax[y] = max;
            });

            float result = 0;

            foreach (float value in rowMax)
            {
                result = MathF.Max(result, value);
            }

            return result * 80f;
        }

        /// <summary>
        /// SMPTE ST 2084 inverse EOTF, taking linear scRGB in and returning a 16 bit PQ code value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort LinearToPq(float linear)
        {
            float normalized = linear * ScRgbToPqScale;

            if (normalized <= 0 || float.IsNaN(normalized))
            {
                return 0;
            }

            if (normalized >= 1f)
            {
                return ushort.MaxValue;
            }

            float ym1 = MathF.Pow(normalized, PqM1);
            float pq = MathF.Pow((PqC1 + (PqC2 * ym1)) / (1f + (PqC3 * ym1)), PqM2);
            float scaled = MathF.Round(pq * ushort.MaxValue);

            return scaled >= ushort.MaxValue ? ushort.MaxValue : (ushort)scaled;
        }
    }
}
