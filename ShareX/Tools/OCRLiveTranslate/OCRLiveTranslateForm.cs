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
using ShareX.ScreenCaptureLib;
using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShareX
{
    public class OCRLiveTranslateForm : Form
    {
        private static OCRLiveTranslateForm instance;

        public static bool IsRunning => instance != null && !instance.IsDisposed;
        public static bool IsLocked => IsRunning && instance.Locked;

        public OCRLiveTranslateOptions Options { get; private set; }
        public bool Locked { get; private set; }
        public bool Paused { get; private set; }

        private const int ChromeHeight = 32;
        private const int ToolbarHeight = 30;
        private const int ResizeBorder = 8;

        private TaskSettings taskSettings;
        private Rectangle monitorRegion;
        private IntPtr gameHandle;
        private bool preventActivation;
        private bool busy;
        private bool loaded;
        private string lastOcrText = "";

        private Panel pnlChrome, pnlToolbar;
        private Label lblTitle, lblOriginal;
        private TextBox txtTranslation;
        private Button btnLock, btnPause, btnReselect, btnCopy, btnClose;
        private ComboBox cbOCRLanguage, cbTargetLanguage;
        private Timer updateTimer;

        protected override bool ShowWithoutActivation => preventActivation;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams createParams = base.CreateParams;
                createParams.ExStyle |= (int)(WindowStyles.WS_EX_TOOLWINDOW | WindowStyles.WS_EX_TOPMOST);
                return createParams;
            }
        }

        private OCRLiveTranslateForm(Rectangle region, IntPtr gameHandle, TaskSettings taskSettings)
        {
            this.taskSettings = taskSettings;
            this.gameHandle = gameHandle;
            monitorRegion = region;
            Options = taskSettings.ToolsSettingsReference.OCRLiveTranslateOptions ?? new OCRLiveTranslateOptions();

            if (string.IsNullOrEmpty(Options.OCRLanguage))
            {
                Options.OCRLanguage = taskSettings.CaptureSettingsReference.OCROptions.Language;
            }

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            MinimumSize = new Size(420, 140);
            KeyPreview = true;
            Text = Resources.OCRLiveTranslate_Title;
            Icon = ShareXResources.Icon;

            Size overlaySize = Options.OverlaySize;
            if (overlaySize.Width < MinimumSize.Width || overlaySize.Height < MinimumSize.Height)
            {
                overlaySize = new Size(440, 200);
            }
            Size = overlaySize;

            if (!Options.OverlayLocation.IsEmpty && CaptureHelpers.GetScreenBounds().Contains(Options.OverlayLocation))
            {
                Location = Options.OverlayLocation;
            }
            else
            {
                Rectangle workingArea = CaptureHelpers.GetActiveScreenWorkingArea();
                Location = new Point(workingArea.Right - Width - 20, workingArea.Bottom - Height - 60);
            }

            Opacity = Math.Max(40, Math.Min(100, Options.OverlayOpacity)) / 100f;

            BuildUI();
            ShareXResources.ApplyTheme(this, true);
            ApplyOverlayColors();

            updateTimer = new Timer();
            updateTimer.Interval = Math.Max(250, Options.UpdateInterval);
            updateTimer.Tick += async (sender, e) => await TickAsync();

            loaded = true;
        }

        public static void Toggle(TaskSettings taskSettings = null)
        {
            if (taskSettings == null)
            {
                taskSettings = TaskSettings.GetDefaultTaskSettings();
            }

            if (IsLocked)
            {
                instance.Unlock();
                return;
            }

            if (IsRunning)
            {
                instance.Close();
                return;
            }

            Start(taskSettings);
        }

        public static void Start(TaskSettings taskSettings = null)
        {
            if (IsRunning || Program.Settings == null || !Program.Settings.ExperimentalOCRLiveTranslate)
            {
                return;
            }

            if (taskSettings == null)
            {
                taskSettings = TaskSettings.GetDefaultTaskSettings();
            }

            try
            {
                OCRHelper.ThrowIfNotSupported();
            }
            catch (Exception e)
            {
                e.ShowError(false);
                return;
            }

            if (!RegionCaptureTasks.GetRectangleRegion(out Rectangle rect, taskSettings.CaptureSettings.SurfaceOptions) || rect.IsEmpty)
            {
                return;
            }

            IntPtr gameHandle = NativeMethods.GetForegroundWindow();

            instance = new OCRLiveTranslateForm(rect, gameHandle, taskSettings);
            instance.Show();
            instance.updateTimer.Start();
        }

        public static void Stop()
        {
            if (IsRunning)
            {
                instance.Close();
            }
        }

        private void BuildUI()
        {
            pnlChrome = new Panel
            {
                Dock = DockStyle.Top,
                Height = ChromeHeight,
                Padding = new Padding(8, 4, 4, 4)
            };

            lblTitle = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = Resources.OCRLiveTranslate_Title,
                TextAlign = ContentAlignment.MiddleLeft,
                Cursor = Cursors.SizeAll
            };
            lblTitle.MouseDown += Chrome_MouseDown;

            btnClose = CreateChromeButton(Resources.cross, Resources.OCRLiveTranslate_Close);
            btnClose.Click += (sender, e) => Close();

            btnLock = CreateChromeButton(Resources.pin, Resources.OCRLiveTranslate_Lock);
            btnLock.Click += (sender, e) => Lock();

            btnPause = CreateChromeButton(Resources.clock, Resources.OCRLiveTranslate_Pause);
            btnPause.Click += (sender, e) => TogglePause();

            btnReselect = CreateChromeButton(Resources.layer_shape, Resources.OCRLiveTranslate_ReselectRegion);
            btnReselect.Click += async (sender, e) => await ReselectRegionAsync();

            btnCopy = CreateChromeButton(Resources.clipboard, Resources.OCRLiveTranslate_Copy);
            btnCopy.Click += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(txtTranslation.Text))
                {
                    ClipboardHelpers.CopyText(txtTranslation.Text);
                }
            };

            pnlChrome.Controls.Add(lblTitle);
            pnlChrome.Controls.Add(btnCopy);
            pnlChrome.Controls.Add(btnReselect);
            pnlChrome.Controls.Add(btnPause);
            pnlChrome.Controls.Add(btnLock);
            pnlChrome.Controls.Add(btnClose);
            btnClose.BringToFront();
            btnLock.BringToFront();
            btnPause.BringToFront();
            btnReselect.BringToFront();
            btnCopy.BringToFront();

            pnlToolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = ToolbarHeight,
                Padding = new Padding(6, 3, 6, 3)
            };

            Label lblOCR = new Label
            {
                AutoSize = true,
                Text = Resources.OCRLiveTranslate_Source,
                Location = new Point(6, 7)
            };

            cbOCRLanguage = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(40, 3),
                Width = 150
            };

            Label lblTarget = new Label
            {
                AutoSize = true,
                Text = Resources.OCRLiveTranslate_Target,
                Location = new Point(198, 7)
            };

            cbTargetLanguage = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(280, 3),
                Width = 140
            };

            pnlToolbar.Controls.Add(lblOCR);
            pnlToolbar.Controls.Add(cbOCRLanguage);
            pnlToolbar.Controls.Add(lblTarget);
            pnlToolbar.Controls.Add(cbTargetLanguage);

            txtTranslation = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                BorderStyle = System.Windows.Forms.BorderStyle.None,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 12f),
                Text = Resources.OCRLiveTranslate_Waiting
            };

            lblOriginal = new Label
            {
                Dock = DockStyle.Bottom,
                Height = Options.ShowOriginalText ? 36 : 0,
                Visible = Options.ShowOriginalText,
                Padding = new Padding(8, 2, 8, 4),
                AutoEllipsis = true
            };

            Controls.Add(txtTranslation);
            Controls.Add(lblOriginal);
            Controls.Add(pnlToolbar);
            Controls.Add(pnlChrome);

            PopulateLanguages();
        }

        private Button CreateChromeButton(Image image, string tooltip)
        {
            Button button = new Button
            {
                Dock = DockStyle.Right,
                Width = 28,
                FlatStyle = FlatStyle.Flat,
                Image = image,
                TabStop = false
            };
            button.FlatAppearance.BorderSize = 0;
            tt.SetToolTip(button, tooltip);
            return button;
        }

        private readonly ToolTip tt = new ToolTip();

        private void PopulateLanguages()
        {
            try
            {
                OCRLanguage[] ocrLanguages = OCRHelper.AvailableLanguages.OrderBy(x => x.DisplayName).ToArray();
                cbOCRLanguage.Items.AddRange(ocrLanguages);

                int ocrIndex = Array.FindIndex(ocrLanguages, x => x.LanguageTag.Equals(Options.OCRLanguage, StringComparison.OrdinalIgnoreCase));
                cbOCRLanguage.SelectedIndex = ocrIndex >= 0 ? ocrIndex : 0;

                if (ocrIndex < 0 && ocrLanguages.Length > 0)
                {
                    Options.OCRLanguage = ocrLanguages[0].LanguageTag;
                }
            }
            catch
            {
                cbOCRLanguage.Enabled = false;
            }

            cbTargetLanguage.Items.AddRange(TranslationHelper.TargetLanguages);
            int targetIndex = Array.FindIndex(TranslationHelper.TargetLanguages,
                x => x.LanguageTag.Equals(Options.TargetLanguage, StringComparison.OrdinalIgnoreCase));
            cbTargetLanguage.SelectedIndex = targetIndex >= 0 ? targetIndex : 0;

            if (targetIndex < 0)
            {
                Options.TargetLanguage = TranslationHelper.TargetLanguages[0].LanguageTag;
            }

            cbOCRLanguage.SelectedIndexChanged += (sender, e) =>
            {
                if (loaded && cbOCRLanguage.SelectedItem is OCRLanguage language)
                {
                    Options.OCRLanguage = language.LanguageTag;
                    lastOcrText = "";
                }
            };

            cbTargetLanguage.SelectedIndexChanged += (sender, e) =>
            {
                if (loaded && cbTargetLanguage.SelectedItem is OCRLanguage language)
                {
                    Options.TargetLanguage = language.LanguageTag;
                    lastOcrText = "";
                }
            };
        }

        private void ApplyOverlayColors()
        {
            Color back = ShareXResources.Theme.BackgroundColor;
            Color text = ShareXResources.Theme.TextColor;
            Color border = ShareXResources.Theme.BorderColor;

            BackColor = back;
            ForeColor = text;
            pnlChrome.BackColor = Color.FromArgb(255, ControlPaint.Dark(back, 0.08f));
            pnlToolbar.BackColor = back;
            txtTranslation.BackColor = back;
            txtTranslation.ForeColor = text;
            lblOriginal.ForeColor = ControlPaint.Dark(text, 0.25f);
            lblOriginal.BackColor = back;
            lblTitle.ForeColor = text;

            foreach (Button button in new[] { btnLock, btnPause, btnReselect, btnCopy, btnClose })
            {
                button.BackColor = pnlChrome.BackColor;
                button.FlatAppearance.MouseOverBackColor = border;
            }
        }

        private static Color ControlPaint_Dark(Color color, float amount)
        {
            return Color.FromArgb(color.A,
                (int)(color.R * (1 - amount)),
                (int)(color.G * (1 - amount)),
                (int)(color.B * (1 - amount)));
        }

        private async Task TickAsync()
        {
            if (busy || Paused || IsDisposed || monitorRegion.IsEmpty)
            {
                return;
            }

            busy = true;

            try
            {
                bool hideOverlay = Bounds.IntersectsWith(monitorRegion);
                float previousOpacity = (float)Opacity;

                if (hideOverlay)
                {
                    Visible = false;
                    await Task.Delay(20);
                }

                Bitmap bmp = null;

                try
                {
                    Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);
                    screenshot.CaptureCursor = false;
                    screenshot.CaptureOverlays = false;
                    bmp = screenshot.CaptureRectangle(monitorRegion);
                }
                finally
                {
                    if (hideOverlay && !IsDisposed)
                    {
                        Visible = true;
                        Opacity = previousOpacity;
                    }
                }

                if (bmp == null)
                {
                    return;
                }

                string ocrText;

                using (bmp)
                {
                    ocrText = await OCRHelper.OCR(bmp, Options.OCRLanguage, Options.ScaleFactor, false);
                }

                ocrText = NormalizeOcrText(ocrText);

                if (string.IsNullOrEmpty(ocrText) || IsSimilarText(ocrText, lastOcrText))
                {
                    return;
                }

                lastOcrText = ocrText;

                TranslationResult translation = await TranslationHelper.TranslateAsync(ocrText, Options.TargetLanguage);

                if (!IsDisposed)
                {
                    txtTranslation.Text = string.IsNullOrEmpty(translation.TranslatedText) ? ocrText : translation.TranslatedText;
                    lblOriginal.Text = ocrText;
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);

                if (!IsDisposed && string.IsNullOrEmpty(txtTranslation.Text))
                {
                    txtTranslation.Text = e.Message;
                }
            }
            finally
            {
                busy = false;
            }
        }

        private static string NormalizeOcrText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "";
            }

            return string.Join(" ", text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static bool IsSimilarText(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                return true;
            }

            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }

            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            int max = Math.Max(a.Length, b.Length);
            if (max < 6)
            {
                return false;
            }

            return Levenshtein(a, b) <= Math.Max(1, max / 20);
        }

        private static int Levenshtein(string a, string b)
        {
            int n = a.Length;
            int m = b.Length;
            int[] prev = new int[m + 1];
            int[] curr = new int[m + 1];

            for (int j = 0; j <= m; j++)
            {
                prev[j] = j;
            }

            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;

                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }

                int[] swap = prev;
                prev = curr;
                curr = swap;
            }

            return prev[m];
        }

        public void Lock()
        {
            if (Locked)
            {
                return;
            }

            Locked = true;
            preventActivation = true;
            pnlChrome.Visible = false;
            pnlToolbar.Visible = false;
            lblOriginal.Visible = false;
            SetClickThrough(true);
            NativeMethods.SetWindowPos(Handle, (IntPtr)NativeConstants.HWND_TOPMOST, 0, 0, 0, 0,
                SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE);
            RestoreGameFocus();
        }

        public void Unlock()
        {
            if (!Locked)
            {
                return;
            }

            Locked = false;
            preventActivation = false;
            SetClickThrough(false);
            pnlChrome.Visible = true;
            pnlToolbar.Visible = true;
            lblOriginal.Visible = Options.ShowOriginalText;
            this.ForceActivate();
        }

        private void TogglePause()
        {
            Paused = !Paused;
            btnPause.Image = Paused ? Resources.control : Resources.clock;
            tt.SetToolTip(btnPause, Paused ? Resources.OCRLiveTranslate_Resume : Resources.OCRLiveTranslate_Pause);
        }

        private async Task ReselectRegionAsync()
        {
            updateTimer.Stop();
            Visible = false;
            await Task.Delay(200);

            if (RegionCaptureTasks.GetRectangleRegion(out Rectangle rect, taskSettings.CaptureSettings.SurfaceOptions) && !rect.IsEmpty)
            {
                monitorRegion = rect;
                lastOcrText = "";
                gameHandle = NativeMethods.GetForegroundWindow();
            }

            if (!IsDisposed)
            {
                Visible = true;
                updateTimer.Start();
            }
        }

        private void SetClickThrough(bool enabled)
        {
            long exStyle = (long)NativeMethods.GetWindowLong(Handle, NativeConstants.GWL_EXSTYLE);

            if (enabled)
            {
                exStyle |= (long)(WindowStyles.WS_EX_TRANSPARENT | WindowStyles.WS_EX_NOACTIVATE);
            }
            else
            {
                exStyle &= ~((long)(WindowStyles.WS_EX_TRANSPARENT | WindowStyles.WS_EX_NOACTIVATE));
            }

            NativeMethods.SetWindowLong(Handle, NativeConstants.GWL_EXSTYLE, (IntPtr)exStyle);
        }

        private void RestoreGameFocus()
        {
            if (gameHandle != IntPtr.Zero && NativeMethods.IsWindow(gameHandle))
            {
                if (NativeMethods.IsIconic(gameHandle))
                {
                    NativeMethods.ShowWindow(gameHandle, (int)WindowShowStyle.Restore);
                }

                NativeMethods.SetForegroundWindow(gameHandle);
            }
        }

        private void Chrome_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, (uint)WindowsMessages.NCLBUTTONDOWN, (IntPtr)WindowHitTestRegions.HTCAPTION, IntPtr.Zero);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (!Locked && m.Msg == (int)WindowsMessages.NCHITTEST)
            {
                base.WndProc(ref m);

                if (m.Result.ToInt32() == (int)WindowHitTestRegions.HTCLIENT)
                {
                    int lp = unchecked((int)(long)m.LParam);
                    Point screenPoint = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                    Point pos = PointToClient(screenPoint);
                    bool left = pos.X <= ResizeBorder;
                    bool right = pos.X >= ClientSize.Width - ResizeBorder;
                    bool top = pos.Y <= ResizeBorder;
                    bool bottom = pos.Y >= ClientSize.Height - ResizeBorder;

                    if (top && left) m.Result = (IntPtr)WindowHitTestRegions.HTTOPLEFT;
                    else if (top && right) m.Result = (IntPtr)WindowHitTestRegions.HTTOPRIGHT;
                    else if (bottom && left) m.Result = (IntPtr)WindowHitTestRegions.HTBOTTOMLEFT;
                    else if (bottom && right) m.Result = (IntPtr)WindowHitTestRegions.HTBOTTOMRIGHT;
                    else if (left) m.Result = (IntPtr)WindowHitTestRegions.HTLEFT;
                    else if (right) m.Result = (IntPtr)WindowHitTestRegions.HTRIGHT;
                    else if (top) m.Result = (IntPtr)WindowHitTestRegions.HTTOP;
                    else if (bottom) m.Result = (IntPtr)WindowHitTestRegions.HTBOTTOM;
                    else if (pos.Y < ChromeHeight && !IsOverChromeButton(pos))
                    {
                        m.Result = (IntPtr)WindowHitTestRegions.HTCAPTION;
                    }
                }

                return;
            }

            base.WndProc(ref m);
        }

        private bool IsOverChromeButton(Point pos)
        {
            Point screen = PointToScreen(pos);

            foreach (Button button in new[] { btnLock, btnPause, btnReselect, btnCopy, btnClose })
            {
                if (button.Visible && button.RectangleToScreen(button.ClientRectangle).Contains(screen))
                {
                    return true;
                }
            }

            return false;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && !Locked)
            {
                Close();
                e.Handled = true;
            }

            base.OnKeyDown(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            updateTimer?.Stop();
            Options.OverlayLocation = Location;
            Options.OverlaySize = Size;
            RestoreGameFocus();
            instance = null;
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                updateTimer?.Dispose();
                tt?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
