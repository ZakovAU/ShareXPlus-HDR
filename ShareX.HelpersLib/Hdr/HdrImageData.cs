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
using System.Drawing;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// The high dynamic range pixels behind a capture, kept next to the tonemapped SDR bitmap
    /// that travels through the rest of the pipeline.
    /// Pixels are scRGB (linear light, Rec.709 primaries, 1.0 == 80 nits) stored as tightly
    /// packed RGBA half floats, which is exactly what desktop duplication hands us on an HDR desktop.
    /// </summary>
    public sealed class HdrImageData
    {
        public const int BytesPerPixel = 8;

        public int Width { get; }
        public int Height { get; }

        /// <summary>
        /// Tightly packed RGBA half float pixels, <see cref="Stride"/> bytes per row.
        /// </summary>
        public byte[] Pixels { get; }

        public HdrImageMetadata Metadata { get; }

        public int Stride => Width * BytesPerPixel;

        public HdrImageData(int width, int height, byte[] pixels, HdrImageMetadata metadata)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "HDR image dimensions must be positive.");
            }

            if (pixels == null)
            {
                throw new ArgumentNullException(nameof(pixels));
            }

            long required = (long)width * height * BytesPerPixel;

            if (pixels.LongLength < required)
            {
                throw new ArgumentException($"HDR pixel buffer is {pixels.LongLength} bytes, expected at least {required}.", nameof(pixels));
            }

            Width = width;
            Height = height;
            Pixels = pixels;
            Metadata = metadata ?? new HdrImageMetadata();
        }

        public bool IsSameSize(Image image)
        {
            return image != null && image.Width == Width && image.Height == Height;
        }

        /// <summary>
        /// Returns the sub-rectangle as a new buffer, or null when the rectangle is not fully inside the image.
        /// </summary>
        public HdrImageData Crop(Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0 || !new Rectangle(0, 0, Width, Height).Contains(rect))
            {
                return null;
            }

            if (rect.X == 0 && rect.Y == 0 && rect.Width == Width && rect.Height == Height)
            {
                return this;
            }

            int destStride = rect.Width * BytesPerPixel;
            byte[] cropped = new byte[(long)destStride * rect.Height];

            for (int y = 0; y < rect.Height; y++)
            {
                Buffer.BlockCopy(Pixels, ((rect.Y + y) * Stride) + (rect.X * BytesPerPixel), cropped, y * destStride, destStride);
            }

            return new HdrImageData(rect.Width, rect.Height, cropped, Metadata);
        }
    }

    public sealed class HdrImageMetadata
    {
        /// <summary>
        /// Brightest luminance found in the capture, in nits.
        /// </summary>
        public float MaxNits { get; set; }

        public float MinNits { get; set; }
        public float AvgNits { get; set; }

        /// <summary>
        /// 99.94th percentile luminance in nits, which ignores the few blown out pixels most captures have.
        /// </summary>
        public float P99Nits { get; set; }

        /// <summary>
        /// Maximum content light level in nits (brightest single channel).
        /// </summary>
        public float MaxCllNits { get; set; }

        /// <summary>
        /// The desktop was being duplicated in a floating point format, i.e. HDR was on.
        /// </summary>
        public bool IsHdrDisplay { get; set; }

        /// <summary>
        /// Pixels actually exceed the SDR range, so there is something to preserve by writing HDR output.
        /// </summary>
        public bool HasHdrContent { get; set; }
    }
}
