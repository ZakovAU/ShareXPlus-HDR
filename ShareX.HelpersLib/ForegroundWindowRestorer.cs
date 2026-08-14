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
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// Remembers which window had foreground focus before a capture overlay was shown and
    /// restores focus to it afterwards. Fullscreen games (exclusive and borderless) get
    /// minimized or deactivated when a capture overlay takes foreground activation away from
    /// them; restoring focus gives it back so using region capture does not effectively
    /// alt-tab the user out of their game.
    /// </summary>
    public class ForegroundWindowRestorer
    {
        public IntPtr Handle { get; private set; }

        public void Capture()
        {
            if (Handle == IntPtr.Zero)
            {
                IntPtr handle = NativeMethods.GetForegroundWindow();

                if (handle != IntPtr.Zero && NativeMethods.IsWindow(handle))
                {
                    Handle = handle;
                }
            }
        }

        public void Restore()
        {
            if (Handle != IntPtr.Zero)
            {
                try
                {
                    if (NativeMethods.IsWindow(Handle) && NativeMethods.IsWindowVisible(Handle) && NativeMethods.GetForegroundWindow() != Handle)
                    {
                        if (NativeMethods.IsIconic(Handle))
                        {
                            NativeMethods.ShowWindow(Handle, (int)WindowShowStyle.Restore);
                        }

                        NativeMethods.SetForegroundWindow(Handle);
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }

                Handle = IntPtr.Zero;
            }
        }

        /// <summary>
        /// True when the current foreground window belongs to another process and covers an
        /// entire monitor, i.e. a fullscreen or borderless game or other fullscreen
        /// application. Capture overlays should not take foreground activation away from
        /// such windows because many games minimize themselves on focus loss.
        /// </summary>
        public static bool IsForegroundWindowFullscreenApp()
        {
            try
            {
                IntPtr foregroundHandle = NativeMethods.GetForegroundWindow();

                if (foregroundHandle == IntPtr.Zero || !NativeMethods.IsWindow(foregroundHandle) || !NativeMethods.IsWindowVisible(foregroundHandle))
                {
                    return false;
                }

                NativeMethods.GetWindowThreadProcessId(foregroundHandle, out uint processId);

                if (processId == Process.GetCurrentProcess().Id)
                {
                    return false;
                }

                using (Process process = Process.GetProcessById((int)processId))
                {
                    // Desktop, taskbar and file explorer are owned by explorer.exe. The
                    // desktop window covers the whole screen, so it must be excluded.
                    if (process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                if (!NativeMethods.GetWindowRect(foregroundHandle, out RECT rect))
                {
                    return false;
                }

                Rectangle windowRect = rect;

                if (windowRect.Width <= 0 || windowRect.Height <= 0)
                {
                    return false;
                }

                foreach (Screen screen in Screen.AllScreens)
                {
                    if (windowRect.Contains(screen.Bounds))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
