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
using System.Threading;
using System.Windows.Forms;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// Runs hotkey registration (RegisterHotKey / WM_HOTKEY) and a low-level
    /// keyboard hook on a dedicated STA thread with its own message loop.
    /// This decouples hotkey handling from the main UI thread so hotkeys keep
    /// working while a fullscreen game (or an anti-hotkey overlay) is pumping
    /// or starving the foreground message loop.
    /// </summary>
    public class HotkeyInputManager : IDisposable
    {
        public event HotkeyForm.HotkeyEventHandler HotkeyPress;
        public event KeyEventHandler KeyDown, KeyUp;

        private readonly Thread thread;
        private readonly ManualResetEventSlim initialized = new ManualResetEventSlim(false);
        private HotkeyForm hotkeyForm;
        private KeyboardHook keyboardHook;

        public HotkeyInputManager()
        {
            thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "ShareXHotkeyThread"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            initialized.Wait();
        }

        private void ThreadMain()
        {
            try
            {
                hotkeyForm = new HotkeyForm();
                hotkeyForm.HotkeyPress += (id, key, modifier) => HotkeyPress?.Invoke(id, key, modifier);
                _ = hotkeyForm.Handle; // force handle creation on this thread

                keyboardHook = new KeyboardHook();
                keyboardHook.KeyDown += (sender, e) => KeyDown?.Invoke(sender, e);
                keyboardHook.KeyUp += (sender, e) => KeyUp?.Invoke(sender, e);
            }
            finally
            {
                initialized.Set();
            }

            Application.Run();
        }

        public void RegisterHotkey(HotkeyInfo hotkeyInfo)
        {
            InvokeOnInputThread(() => hotkeyForm.RegisterHotkey(hotkeyInfo));
        }

        public bool UnregisterHotkey(HotkeyInfo hotkeyInfo)
        {
            bool result = false;
            InvokeOnInputThread(() => result = hotkeyForm.UnregisterHotkey(hotkeyInfo));
            return result;
        }

        private void InvokeOnInputThread(Action action)
        {
            if (hotkeyForm == null || hotkeyForm.IsDisposed || !hotkeyForm.IsHandleCreated)
            {
                return;
            }

            try
            {
                hotkeyForm.Invoke(action);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        public void Dispose()
        {
            try
            {
                if (hotkeyForm != null && !hotkeyForm.IsDisposed && hotkeyForm.IsHandleCreated)
                {
                    hotkeyForm.Invoke((Action)(() =>
                    {
                        keyboardHook?.Dispose();
                        hotkeyForm.Dispose();
                        Application.ExitThread();
                    }));
                }
            }
            catch
            {
                // Application is shutting down; the background thread will end with the process.
            }

            initialized.Dispose();
        }
    }
}
