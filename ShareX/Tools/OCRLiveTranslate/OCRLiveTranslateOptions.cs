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
using System.Globalization;

namespace ShareX
{
    public class OCRLiveTranslateOptions
    {
        public string OCRLanguage { get; set; } = "en";
        public string TargetLanguage { get; set; } = NormalizeLanguage(CultureInfo.CurrentUICulture.Name);
        public float ScaleFactor { get; set; } = 2f;
        public int UpdateInterval { get; set; } = 1000;
        public bool ShowOriginalText { get; set; } = true;
        public int OverlayOpacity { get; set; } = 92;
        public Point OverlayLocation { get; set; } = Point.Empty;
        public Size OverlaySize { get; set; } = new Size(440, 200);

        public static string NormalizeLanguage(string cultureName)
        {
            if (string.IsNullOrEmpty(cultureName))
            {
                return "en";
            }

            if (cultureName.StartsWith("zh-Hant", System.StringComparison.OrdinalIgnoreCase) ||
                cultureName.StartsWith("zh-TW", System.StringComparison.OrdinalIgnoreCase) ||
                cultureName.StartsWith("zh-HK", System.StringComparison.OrdinalIgnoreCase))
            {
                return "zh-TW";
            }

            if (cultureName.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase))
            {
                return "zh-CN";
            }

            int dash = cultureName.IndexOf('-');
            return dash > 0 ? cultureName.Substring(0, dash) : cultureName;
        }
    }
}
