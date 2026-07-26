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

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Composites topmost layered "overlay" windows (game overlays such as
    /// Discord, Steam or GeForce Experience overlays) on top of a monitor
    /// capture. Desktop duplication based captures can miss these windows,
    /// so they are rendered explicitly with PrintWindow.
    /// </summary>
    public static class OverlayCapture
    {
        private const int GWL_EXSTYLE = -20;
        private const int DWMWA_CLOAKED = 13;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        private const long WS_EX_LAYERED = 0x00080000;
        private const long WS_EX_TRANSPARENT = 0x00000020;
        private const long WS_EX_TOPMOST = 0x00000008;
        private const long WS_EX_NOACTIVATE = 0x08000000;

        public static void CompositeOverlays(Bitmap bmp, Rectangle captureRect)
        {
            if (bmp == null || captureRect.Width <= 0 || captureRect.Height <= 0)
            {
                return;
            }

            List<(IntPtr Handle, Rectangle Rect)> overlays = EnumOverlayWindows(captureRect);

            if (overlays.Count == 0)
            {
                return;
            }

            try
            {
                using (Graphics gDest = Graphics.FromImage(bmp))
                {
                    foreach ((IntPtr handle, Rectangle rect) in overlays)
                    {
                        DrawOverlayWindow(gDest, handle, rect, captureRect);
                    }
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Overlay capture failed.");
            }
        }

        private static List<(IntPtr Handle, Rectangle Rect)> EnumOverlayWindows(Rectangle captureRect)
        {
            List<(IntPtr, Rectangle)> overlays = new List<(IntPtr, Rectangle)>();
            int currentProcessId;

            using (Process currentProcess = Process.GetCurrentProcess())
            {
                currentProcessId = currentProcess.Id;
            }

            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(hWnd))
                    {
                        return true;
                    }

                    long exStyle = GetWindowExStyle(hWnd);

                    // Overlay windows are layered, topmost and non-interactive.
                    bool isOverlay = (exStyle & WS_EX_LAYERED) != 0 && (exStyle & WS_EX_TOPMOST) != 0 &&
                        ((exStyle & WS_EX_TRANSPARENT) != 0 || (exStyle & WS_EX_NOACTIVATE) != 0);

                    if (!isOverlay)
                    {
                        return true;
                    }

                    // Skip our own windows (region selector, crosshair, etc.).
                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);

                    if (processId == currentProcessId)
                    {
                        return true;
                    }

                    // Skip cloaked (hidden by DWM) windows.
                    if (NativeMethods.DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    {
                        return true;
                    }

                    if (!NativeMethods.GetWindowRect(hWnd, out RECT windowRect))
                    {
                        return true;
                    }

                    Rectangle rect = windowRect;

                    if (rect.Width <= 0 || rect.Height <= 0 || !rect.IntersectsWith(captureRect))
                    {
                        return true;
                    }

                    overlays.Add((hWnd, rect));
                }
                catch
                {
                    // Ignore windows that disappear while enumerating.
                }

                return true;
            }, IntPtr.Zero);

            return overlays;
        }

        private static void DrawOverlayWindow(Graphics gDest, IntPtr handle, Rectangle windowRect, Rectangle captureRect)
        {
            try
            {
                using (Bitmap windowBmp = new Bitmap(windowRect.Width, windowRect.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics gWindow = Graphics.FromImage(windowBmp))
                    {
                        IntPtr hdc = gWindow.GetHdc();

                        try
                        {
                            if (!NativeMethods.PrintWindow(handle, hdc, PW_RENDERFULLCONTENT))
                            {
                                return;
                            }
                        }
                        finally
                        {
                            gWindow.ReleaseHdc(hdc);
                        }
                    }

                    gDest.DrawImage(windowBmp, windowRect.X - captureRect.X, windowRect.Y - captureRect.Y);
                }
            }
            catch
            {
                // Window closed between enumeration and rendering.
            }
        }

        private static long GetWindowExStyle(IntPtr hWnd)
        {
            if (IntPtr.Size == 8)
            {
                return NativeMethods.GetWindowLongPtr64(hWnd, GWL_EXSTYLE).ToInt64();
            }

            return NativeMethods.GetWindowLong32(hWnd, GWL_EXSTYLE).ToInt64();
        }
    }
}
