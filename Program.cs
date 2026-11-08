using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetSpeedTray
{
    public enum SpeedUnits
    {
        Mbit,
        MByte
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (SpeedTrayApp app = new SpeedTrayApp())
            {
                app.Run();
            }
        }

        public static string FormatSpeed(double bytesPerSec, SpeedUnits units)
        {
            if (units == SpeedUnits.Mbit)
            {
                double mbits = bytesPerSec * 8.0 / 1000000.0;
                if (mbits >= 1000.0)
                    return FormatNum(mbits / 1000.0) + " Гбит/с";
                return FormatNum(mbits) + " Мбит/с";
            }

            double mb = bytesPerSec / 1000000.0;
            if (mb >= 1000.0)
                return FormatNum(mb / 1000.0) + " ГБ/с";
            return FormatNum(mb) + " МБ/с";
        }

        public static string Compact(double bytesPerSec, SpeedUnits units)
        {
            if (units == SpeedUnits.Mbit)
                return FormatNum(bytesPerSec * 8.0 / 1000000.0);
            return FormatNum(bytesPerSec / 1000000.0);
        }

        private static string FormatNum(double value)
        {
            if (value >= 10.0)
                return ((long)Math.Round(value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    public class AppSettings
    {
        public int IntervalMs = 1000;
        public SpeedUnits Units = SpeedUnits.Mbit;
        public bool ColoredIcon = false;
        public bool FullscreenSleep = true;

        private static string FilePath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NetSpeedTray");
            return Path.Combine(dir, "settings.ini");
        }

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            try
            {
                string p = FilePath();
                if (!File.Exists(p)) return s;

                foreach (string line in File.ReadAllLines(p))
                {
                    string[] kv = line.Split(new char[] { '=' }, 2);
                    if (kv.Length != 2) continue;
                    string k = kv[0].Trim();
                    string v = kv[1].Trim();

                    if (k == "interval")
                    {
                        int x;
                        if (int.TryParse(v, out x) && x >= 100 && x <= 60000) s.IntervalMs = x;
                    }
                    else if (k == "units")
                    {
                        s.Units = (v == "mbyte") ? SpeedUnits.MByte : SpeedUnits.Mbit;
                    }
                    else if (k == "color")
                    {
                        s.ColoredIcon = (v == "colored");
                    }
                    else if (k == "fullscreen")
                    {
                        s.FullscreenSleep = (v != "0");
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                string p = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p,
                    "interval=" + IntervalMs + "\r\n" +
                    "units=" + (Units == SpeedUnits.MByte ? "mbyte" : "mbit") + "\r\n" +
                    "color=" + (ColoredIcon ? "colored" : "white") + "\r\n" +
                    "fullscreen=" + (FullscreenSleep ? "1" : "0") + "\r\n");
            }
            catch { }
        }
    }

    public class SpeedTrayApp : IDisposable
    {
        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private Timer _timer;
        private Stopwatch _sw;
        private Icon _currentIcon;
        private AppSettings _settings;

        private long _prevDown;
        private long _prevUp;
        private bool _hasPrev;
        private bool _hasRendered;
        private string _lastDown;
        private string _lastUp;

        private NetworkInterface[] _nics;
        private int _nicRefreshCounter;

        private bool _sleeping;
        private StringBuilder _sbClass = new StringBuilder(256);

        private Font _iconFont;
        private StringFormat _iconFormat;

        private const string RegRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RegValueName = "NetSpeedTray";

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;

        public void Run()
        {
            _settings = AppSettings.Load();

            BuildTray();
            BuildMenu();

            _timer = new Timer();
            _timer.Interval = _settings.IntervalMs;
            _timer.Tick += OnTick;
            _timer.Start();

            _sw = Stopwatch.StartNew();

            UpdateSpeed();

            Application.Run();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_settings.FullscreenSleep)
            {
                if (IsFullscreenAppActive())
                {
                    _sleeping = true;
                    return;
                }
                if (_sleeping)
                {
                    // проснулись: сбрасываем базу, чтобы не было ложного скачка
                    _sleeping = false;
                    _hasPrev = false;
                    _sw.Restart();
                }
            }

            UpdateSpeed();
        }

        private void UpdateSpeed()
        {
            long down, up;
            GetCounters(out down, out up);
            double sec = Math.Max(_sw.Elapsed.TotalSeconds, 0.05);
            _sw.Restart();

            if (!_hasPrev)
            {
                _prevDown = down;
                _prevUp = up;
                _hasPrev = true;
                if (_hasRendered) return; // после пробуждения оставляем последние цифры
                UpdateDisplay(0.0, 0.0, false);
                return;
            }

            double downBps = (down - _prevDown) / sec;
            double upBps = (up - _prevUp) / sec;
            if (downBps < 0) downBps = 0;
            if (upBps < 0) upBps = 0;

            _prevDown = down;
            _prevUp = up;

            UpdateDisplay(downBps, upBps, true);
        }

        private void GetCounters(out long down, out long up)
        {
            down = 0;
            up = 0;

            if (_nics == null || _nicRefreshCounter++ >= 60)
            {
                _nicRefreshCounter = 0;
                try
                {
                    _nics = NetworkInterface.GetAllNetworkInterfaces();
                }
                catch
                {
                    _nics = new NetworkInterface[0];
                }
            }

            foreach (NetworkInterface nic in _nics)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                try
                {
                    IPv4InterfaceStatistics st = nic.GetIPv4Statistics();
                    down += st.BytesReceived;
                    up += st.BytesSent;
                }
                catch { }
            }
        }

        private void UpdateDisplay(double downBps, double upBps, bool ready)
        {
            string d = ready ? Program.Compact(downBps, _settings.Units) : "…";
            string u = ready ? Program.Compact(upBps, _settings.Units) : "…";

            // цифры не изменились — не тратим ресурсы на отрисовку
            if (d == _lastDown && u == _lastUp) return;

            _lastDown = d;
            _lastUp = u;
            _hasRendered = true;

            Icon icon = RenderIcon(d, u);
            Icon old = _currentIcon;
            _tray.Icon = icon;
            _currentIcon = icon;
            if (old != null) old.Dispose();

            if (ready)
            {
                _tray.Text = "↓ " + Program.FormatSpeed(downBps, _settings.Units) +
                             "    ↑ " + Program.FormatSpeed(upBps, _settings.Units);
            }
        }

        private Icon RenderIcon(string downText, string upText)
        {
            int s = SystemInformation.SmallIconSize.Width;
            if (s < 16) s = 16;
            const int ss = 4;
            int S = s * ss;

            if (_iconFont == null)
            {
                float fontSize = s * 0.46f * ss;
                _iconFont = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                _iconFormat = new StringFormat();
                _iconFormat.Alignment = StringAlignment.Center;
                _iconFormat.LineAlignment = StringAlignment.Center;
            }

            Color downColor = _settings.ColoredIcon ? Color.FromArgb(46, 204, 113) : Color.White;
            Color upColor = _settings.ColoredIcon ? Color.FromArgb(243, 156, 18) : Color.White;

            Icon result;
            using (Bitmap hi = new Bitmap(S, S))
            {
                using (Graphics g = Graphics.FromImage(hi))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.Clear(Color.Transparent);

                    float lineH = S / 2f;
                    DrawText(g, downText, new RectangleF(0, 0, S, lineH), downColor, ss);
                    DrawText(g, upText, new RectangleF(0, lineH, S, lineH), upColor, ss);
                }

                using (Bitmap fin = new Bitmap(s, s))
                {
                    using (Graphics g2 = Graphics.FromImage(fin))
                    {
                        g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g2.DrawImage(hi, new Rectangle(0, 0, s, s),
                                     new Rectangle(0, 0, S, S), GraphicsUnit.Pixel);
                    }

                    IntPtr h = fin.GetHicon();
                    try
                    {
                        using (Icon tmp = Icon.FromHandle(h))
                            result = (Icon)tmp.Clone();
                    }
                    finally
                    {
                        Native.DestroyIcon(h);
                    }
                }
            }
            return result;
        }

        private void DrawText(Graphics g, string text, RectangleF rect, Color color, int ss)
        {
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddString(text, _iconFont.FontFamily, (int)_iconFont.Style, _iconFont.Size, rect, _iconFormat);
                using (Pen pen = new Pen(Color.Black, 1.0f * ss))
                {
                    pen.LineJoin = LineJoin.Round;
                    g.DrawPath(pen, gp);
                }
                using (SolidBrush b = new SolidBrush(color))
                    g.FillPath(b, gp);
            }
        }

        private bool IsFullscreenAppActive()
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            _sbClass.Clear();
            Native.GetClassName(hwnd, _sbClass, 256);
            string cls = _sbClass.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return false;

            // окна с заголовком (обычные/развёрнутые) не считаем полноэкранными
            if ((Native.GetWindowLong(hwnd, GWL_STYLE) & WS_CAPTION) != 0) return false;

            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return false;

            IntPtr mon = Native.MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            if (!Native.GetMonitorInfo(mon, ref mi)) return false;

            const int tol = 8;
            return r.Left <= mi.rcMonitor.Left + tol &&
                   r.Top <= mi.rcMonitor.Top + tol &&
                   r.Right >= mi.rcMonitor.Right - tol &&
                   r.Bottom >= mi.rcMonitor.Bottom - tol;
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Text = "NetSpeedTray";
            _tray.Visible = true;
        }

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip();

            ToolStripMenuItem miSettings = new ToolStripMenuItem("Настройки...");
            miSettings.Click += delegate { ShowSettings(); };

            ToolStripMenuItem miAutoStart = new ToolStripMenuItem("Автозапуск при входе в Windows");
            miAutoStart.Click += delegate { ToggleAutoStart(); };

            ToolStripMenuItem miExit = new ToolStripMenuItem("Выход");
            miExit.Click += delegate { Exit(); };

            _menu.Opening += delegate
            {
                miAutoStart.Checked = IsAutoStartEnabled();
            };

            _menu.Items.Add(miSettings);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miAutoStart);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miExit);

            _tray.ContextMenuStrip = _menu;
        }

        private void ShowSettings()
        {
            using (SettingsForm f = new SettingsForm(_settings))
            {
                if (f.ShowDialog() == DialogResult.OK)
                {
                    _settings = f.ResultSettings;
                    _settings.Save();

                    _timer.Interval = _settings.IntervalMs;
                    _lastDown = null;
                    _lastUp = null;
                    UpdateSpeed();
                }
            }
        }

        private bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegRunKey, false))
                {
                    if (key == null) return false;
                    object val = key.GetValue(RegValueName);
                    return val != null &&
                        string.Equals(val.ToString(), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private void ToggleAutoStart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegRunKey, true))
                {
                    if (key == null) return;
                    if (IsAutoStartEnabled())
                        key.DeleteValue(RegValueName, false);
                    else
                        key.SetValue(RegValueName, Application.ExecutablePath);
                }
            }
            catch { }
        }

        private void Exit()
        {
            try
            {
                _timer.Stop();
                _tray.Visible = false;
            }
            finally
            {
                Application.Exit();
            }
        }

        public void Dispose()
        {
            if (_timer != null) _timer.Dispose();
            if (_menu != null) _menu.Dispose();
            if (_currentIcon != null) _currentIcon.Dispose();
            if (_iconFont != null) _iconFont.Dispose();
            if (_iconFormat != null) _iconFormat.Dispose();
            if (_tray != null) _tray.Dispose();
        }
    }

    public class SettingsForm : Form
    {
        private ComboBox _cbInterval;
        private RadioButton _rbMbit;
        private RadioButton _rbMbyte;
        private RadioButton _rbWhite;
        private RadioButton _rbColored;
        private CheckBox _chkFullscreen;

        private static readonly int[] IntervalValues = new int[] { 500, 1000, 2000, 3000, 5000 };

        public AppSettings ResultSettings { get; private set; }

        public SettingsForm(AppSettings current)
        {
            ResultSettings = current;

            Text = "Настройки NetSpeedTray";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(6F, 13F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(352, 270);

            Label lblInterval = new Label();
            lblInterval.AutoSize = true;
            lblInterval.Text = "Интервал обновления:";
            lblInterval.Location = new Point(12, 15);
            Controls.Add(lblInterval);

            _cbInterval = new ComboBox();
            _cbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbInterval.Location = new Point(175, 12);
            _cbInterval.Width = 90;
            _cbInterval.Items.AddRange(new object[] { "0.5 с", "1 с", "2 с", "3 с", "5 с" });
            int sel = 1;
            for (int i = 0; i < IntervalValues.Length; i++)
            {
                if (IntervalValues[i] == current.IntervalMs) sel = i;
            }
            _cbInterval.SelectedIndex = sel;
            Controls.Add(_cbInterval);

            GroupBox gUnits = new GroupBox();
            gUnits.Text = "Единицы измерения";
            gUnits.Location = new Point(12, 44);
            gUnits.Size = new Size(328, 66);
            Controls.Add(gUnits);

            _rbMbit = new RadioButton();
            _rbMbit.AutoSize = true;
            _rbMbit.Text = "Мбит/с (как у провайдера и в speedtest)";
            _rbMbit.Location = new Point(12, 20);
            _rbMbit.Checked = (current.Units == SpeedUnits.Mbit);
            gUnits.Controls.Add(_rbMbit);

            _rbMbyte = new RadioButton();
            _rbMbyte.AutoSize = true;
            _rbMbyte.Text = "МБ/с (как в торрентах и браузере)";
            _rbMbyte.Location = new Point(12, 42);
            _rbMbyte.Checked = (current.Units == SpeedUnits.MByte);
            gUnits.Controls.Add(_rbMbyte);

            GroupBox gColor = new GroupBox();
            gColor.Text = "Цифры на иконке";
            gColor.Location = new Point(12, 116);
            gColor.Size = new Size(328, 66);
            Controls.Add(gColor);

            _rbWhite = new RadioButton();
            _rbWhite.AutoSize = true;
            _rbWhite.Text = "Белые";
            _rbWhite.Location = new Point(12, 20);
            _rbWhite.Checked = !current.ColoredIcon;
            gColor.Controls.Add(_rbWhite);

            _rbColored = new RadioButton();
            _rbColored.AutoSize = true;
            _rbColored.Text = "Зелёные (загрузка) / оранжевые (отдача)";
            _rbColored.Location = new Point(12, 42);
            _rbColored.Checked = current.ColoredIcon;
            gColor.Controls.Add(_rbColored);

            _chkFullscreen = new CheckBox();
            _chkFullscreen.AutoSize = true;
            _chkFullscreen.Text = "Засыпать при полноэкранных приложениях (меньше нагрузка)";
            _chkFullscreen.Location = new Point(12, 190);
            _chkFullscreen.Checked = current.FullscreenSleep;
            Controls.Add(_chkFullscreen);

            Button btnOk = new Button();
            btnOk.Text = "Сохранить";
            btnOk.Location = new Point(178, 230);
            btnOk.Size = new Size(80, 26);
            btnOk.Click += delegate { ApplyAndClose(); };
            Controls.Add(btnOk);
            AcceptButton = btnOk;

            Button btnCancel = new Button();
            btnCancel.Text = "Отмена";
            btnCancel.Location = new Point(264, 230);
            btnCancel.Size = new Size(80, 26);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);
            CancelButton = btnCancel;
        }

        private void ApplyAndClose()
        {
            AppSettings s = new AppSettings();
            s.IntervalMs = IntervalValues[_cbInterval.SelectedIndex];
            s.Units = _rbMbyte.Checked ? SpeedUnits.MByte : SpeedUnits.Mbit;
            s.ColoredIcon = _rbColored.Checked;
            s.FullscreenSleep = _chkFullscreen.Checked;
            ResultSettings = s;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        internal static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        internal static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        internal static int GetWindowLong(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return (int)(long)GetWindowLongPtr64(hWnd, nIndex);
            return GetWindowLong32(hWnd, nIndex);
        }

        [DllImport("user32.dll")]
        internal static extern bool DestroyIcon(IntPtr handle);
    }
}
