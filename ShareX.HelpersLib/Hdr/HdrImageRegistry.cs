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

using System.Drawing;
using System.Runtime.CompilerServices;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// Side channel that keeps the HDR pixels of a capture reachable from the SDR
    /// <see cref="Bitmap"/> the pipeline passes around. <see cref="Bitmap"/> is sealed and every
    /// stage of ShareX is typed against it, so the HDR payload rides along in a weak table instead
    /// of a subclass. Entries disappear with the bitmap they belong to.
    /// </summary>
    public static class HdrImageRegistry
    {
        private static readonly ConditionalWeakTable<Image, HdrImageData> table = new ConditionalWeakTable<Image, HdrImageData>();

        public static void Attach(Image image, HdrImageData data)
        {
            if (image == null || data == null)
            {
                return;
            }

            table.AddOrUpdate(image, data);
        }

        public static HdrImageData Get(Image image)
        {
            if (image != null && table.TryGetValue(image, out HdrImageData data))
            {
                return data;
            }

            return null;
        }

        /// <summary>
        /// True when the image carries HDR pixels that are worth writing out as HDR.
        /// </summary>
        public static bool HasHdrContent(Image image)
        {
            HdrImageData data = Get(image);
            return data != null && data.Metadata.HasHdrContent;
        }

        public static void Detach(Image image)
        {
            if (image != null)
            {
                table.Remove(image);
            }
        }

        /// <summary>
        /// Carries the payload over to an image that holds the same pixels, e.g. a clone.
        /// </summary>
        public static void Propagate(Image source, Image destination)
        {
            HdrImageData data = Get(source);

            if (data != null && data.IsSameSize(destination))
            {
                Attach(destination, data);
            }
        }

        /// <summary>
        /// Carries the payload over to an image cropped out of <paramref name="source"/>.
        /// </summary>
        public static void Propagate(Image source, Image destination, Rectangle sourceRect)
        {
            HdrImageData data = Get(source);

            if (data == null || destination == null)
            {
                return;
            }

            if (sourceRect.Width != destination.Width || sourceRect.Height != destination.Height)
            {
                return;
            }

            HdrImageData cropped = data.Crop(sourceRect);

            if (cropped != null)
            {
                Attach(destination, cropped);
            }
        }
    }
}
