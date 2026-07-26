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
using ShareX.Properties;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ShareX
{
    public class HotkeyManager
    {
        public List<HotkeySettings> Hotkeys { get; private set; }
        public bool IgnoreHotkeys { get; set; }

        public delegate void HotkeyTriggerEventHandler(HotkeySettings hotkeySetting);
        public delegate void HotkeysToggledEventHandler(bool hotkeysEnabled);

        public HotkeyTriggerEventHandler HotkeyTrigger;
        public HotkeysToggledEventHandler HotkeysToggledTrigger;

        private const int HookRepeatLimit = 1000;

        private readonly HotkeyInputManager inputManager;
        private readonly SynchronizationContext uiContext;

        // Hotkeys are matched primarily through a low-level keyboard hook
        // (WH_KEYBOARD_LL) running on the dedicated hotkey thread, the same
        // approach tools like ProcessFlipper use. Games such as Star Citizen
        // swallow RegisterHotKey hotkeys even though registration succeeds,
        // while the low-level hook still sees every keystroke. RegisterHotKey
        // is kept as a secondary path and deduplicated via a shared repeat
        // limit timer.
        private readonly Stopwatch hookRepeatTimer = Stopwatch.StartNew();

        public HotkeyManager()
        {
            uiContext = SynchronizationContext.Current;

            inputManager = new HotkeyInputManager();
            inputManager.HotkeyPress += InputManager_HotkeyPress;
            inputManager.KeyDown += InputManager_KeyDown;

            Application.ApplicationExit += Application_ApplicationExit;
        }

        private void Application_ApplicationExit(object sender, System.EventArgs e)
        {
            inputManager.Dispose();
        }

        private void InputManager_HotkeyPress(ushort id, Keys key, Modifiers modifier)
        {
            if (!IgnoreHotkeys && (!Program.Settings.DisableHotkeysOnFullscreen || !CaptureHelpers.IsActiveWindowFullscreen()))
            {
                HotkeySettings hotkeySetting = Hotkeys?.Find(x => x.HotkeyInfo.ID == id);

                if (hotkeySetting != null && CheckRepeatLimit())
                {
                    TriggerOnUIThread(hotkeySetting);
                }
            }
        }

        private void InputManager_KeyDown(object sender, KeyEventArgs e)
        {
            if (IgnoreHotkeys || Hotkeys == null || (Program.Settings.DisableHotkeysOnFullscreen && CaptureHelpers.IsActiveWindowFullscreen()))
            {
                return;
            }

            // Read the physical modifier state; Control.ModifierKeys is
            // message-queue relative and unreliable while a game owns input.
            bool control = IsAsyncKeyDown(Keys.ControlKey);
            bool shift = IsAsyncKeyDown(Keys.ShiftKey);
            bool alt = IsAsyncKeyDown(Keys.Menu);
            bool win = IsAsyncKeyDown(Keys.LWin) || IsAsyncKeyDown(Keys.RWin);

            HotkeySettings match = null;

            foreach (HotkeySettings hotkeySetting in Hotkeys)
            {
                HotkeyInfo info = hotkeySetting.HotkeyInfo;

                // Only match hotkeys that are currently active (i.e. were
                // attempted for registration; toggled-off hotkeys are
                // NotConfigured).
                if (info.Status != HotkeyStatus.Registered && info.Status != HotkeyStatus.Failed)
                {
                    continue;
                }

                if (info.KeyCode == e.KeyCode && info.Control == control && info.Shift == shift && info.Alt == alt && info.Win == win)
                {
                    match = hotkeySetting;
                    break;
                }
            }

            if (match != null && CheckRepeatLimit())
            {
                // Swallow the keystroke so the focused game does not also
                // react to the hotkey.
                e.SuppressKeyPress = true;
                DebugHelper.WriteLine("Hotkey triggered via keyboard hook. " + match);
                TriggerOnUIThread(match);
            }
        }

        private static bool IsAsyncKeyDown(Keys key)
        {
            return (NativeMethods.GetAsyncKeyState((int)key) & 0x8000) != 0;
        }

        private bool CheckRepeatLimit()
        {
            if (hookRepeatTimer.ElapsedMilliseconds >= HookRepeatLimit)
            {
                hookRepeatTimer.Restart();
                return true;
            }

            return false;
        }

        private void TriggerOnUIThread(HotkeySettings hotkeySetting)
        {
            if (uiContext != null)
            {
                uiContext.Post(_ => OnHotkeyTrigger(hotkeySetting), null);
            }
            else
            {
                OnHotkeyTrigger(hotkeySetting);
            }
        }

        public void UpdateHotkeys(List<HotkeySettings> hotkeys, bool showFailedHotkeys)
        {
            if (Hotkeys != null)
            {
                UnregisterAllHotkeys();
            }

            Hotkeys = hotkeys;

            RegisterAllHotkeys();

            if (showFailedHotkeys)
            {
                ShowFailedHotkeys();
            }
        }

        protected void OnHotkeyTrigger(HotkeySettings hotkeySetting)
        {
            HotkeyTrigger?.Invoke(hotkeySetting);
        }

        public void RegisterHotkey(HotkeySettings hotkeySetting)
        {
            if (!Program.Settings.DisableHotkeys || hotkeySetting.TaskSettings.Job == HotkeyType.DisableHotkeys)
            {
                UnregisterHotkey(hotkeySetting, false);

                if (hotkeySetting.HotkeyInfo.Status != HotkeyStatus.Registered && hotkeySetting.HotkeyInfo.IsValidHotkey)
                {
                    inputManager.RegisterHotkey(hotkeySetting.HotkeyInfo);

                    if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.Registered)
                    {
                        DebugHelper.WriteLine("Hotkey registered: " + hotkeySetting);
                    }
                    else if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.Failed)
                    {
                        DebugHelper.WriteLine("Hotkey register failed: " + hotkeySetting);
                    }
                }
                else
                {
                    hotkeySetting.HotkeyInfo.Status = HotkeyStatus.NotConfigured;
                }
            }

            if (!Hotkeys.Contains(hotkeySetting))
            {
                Hotkeys.Add(hotkeySetting);
            }
        }

        public void RegisterAllHotkeys()
        {
            foreach (HotkeySettings hotkeySetting in Hotkeys.ToArray())
            {
                RegisterHotkey(hotkeySetting);
            }
        }

        public void RegisterFailedHotkeys()
        {
            foreach (HotkeySettings hotkeySetting in Hotkeys.Where(x => x.HotkeyInfo.Status == HotkeyStatus.Failed))
            {
                RegisterHotkey(hotkeySetting);
            }
        }

        public void UnregisterHotkey(HotkeySettings hotkeySetting, bool removeFromList = true)
        {
            if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.Registered)
            {
                inputManager.UnregisterHotkey(hotkeySetting.HotkeyInfo);

                if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.NotConfigured)
                {
                    DebugHelper.WriteLine("Hotkey unregistered: " + hotkeySetting);
                }
                else if (hotkeySetting.HotkeyInfo.Status == HotkeyStatus.Failed)
                {
                    DebugHelper.WriteLine("Hotkey unregister failed: " + hotkeySetting);
                }
            }

            if (removeFromList)
            {
                Hotkeys.Remove(hotkeySetting);
            }
        }

        public void UnregisterAllHotkeys(bool removeFromList = true, bool temporary = false)
        {
            if (Hotkeys != null)
            {
                foreach (HotkeySettings hotkeySetting in Hotkeys.ToArray())
                {
                    if (!temporary || hotkeySetting.TaskSettings.Job != HotkeyType.DisableHotkeys)
                    {
                        UnregisterHotkey(hotkeySetting, removeFromList);
                    }
                }
            }
        }

        public void ToggleHotkeys(bool hotkeysDisabled)
        {
            if (!hotkeysDisabled)
            {
                RegisterAllHotkeys();
            }
            else
            {
                UnregisterAllHotkeys(false, true);
            }

            HotkeysToggledTrigger?.Invoke(hotkeysDisabled);
        }

        public void ShowFailedHotkeys()
        {
            List<HotkeySettings> failedHotkeysList = Hotkeys.Where(x => x.HotkeyInfo.Status == HotkeyStatus.Failed).ToList();

            if (failedHotkeysList.Count > 0)
            {
                string failedHotkeys = string.Join("\r\n", failedHotkeysList.Select(x => $"[{x.HotkeyInfo}] {x.TaskSettings}"));
                string hotkeyText = failedHotkeysList.Count > 1 ? Resources.HotkeyManager_ShowFailedHotkeys_hotkeys : Resources.HotkeyManager_ShowFailedHotkeys_hotkey;
                string text = string.Format(Resources.HotkeyManager_ShowFailedHotkeys_Unable_to_register_hotkey, hotkeyText, failedHotkeys);

                MessageBox.Show(text, "ShareX - " + Resources.HotkeyManager_ShowFailedHotkeys_Hotkey_registration_failed, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public void ResetHotkeys()
        {
            UnregisterAllHotkeys();
            Hotkeys.AddRange(GetDefaultHotkeyList());
            RegisterAllHotkeys();

            if (Program.Settings.DisableHotkeys)
            {
                TaskHelpers.ToggleHotkeys();
            }
        }

        public static List<HotkeySettings> GetDefaultHotkeyList()
        {
            return new List<HotkeySettings>
            {
                new HotkeySettings(HotkeyType.RectangleRegion, Keys.Control | Keys.PrintScreen),
                new HotkeySettings(HotkeyType.PrintScreen, Keys.PrintScreen),
                new HotkeySettings(HotkeyType.ActiveWindow, Keys.Alt | Keys.PrintScreen),
                new HotkeySettings(HotkeyType.ScreenRecorder, Keys.Shift | Keys.PrintScreen),
                new HotkeySettings(HotkeyType.ScreenRecorderGIF, Keys.Control | Keys.Shift | Keys.PrintScreen)
            };
        }
    }
}
