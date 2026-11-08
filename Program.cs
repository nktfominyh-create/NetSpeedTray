using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetSpeedTray
{
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

        public static string FormatSpeed(double bytesPerSec)
        {
            double mbits = bytesPerSec * 8.0 / 1000000.0;
            if (mbits >= 1000.0)
                return FormatNum(mbits / 1000.0) + " Гбит/с";
            return FormatNum(mbits) + " Мбит/с";
        }

        public static string Compact(double bytesPerSec)
        {
            double mbits = bytesPerSec * 8.0 / 1000000.0;
            return FormatNum(mbits);
        }

        private static string FormatNum(double value)
        {
            if (value >= 10.0)
                return ((long)Math.Round(value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    public class SpeedTrayApp : IDisposable
    {
        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private Timer _timer;
        private Stopwatch _sw;
        private Icon _currentIcon;

        private long _prevDown;
        private long _prevUp;
        private bool _hasPrev;

        private const string RegRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RegValueName = "NetSpeedTray";

        private static readonly Color DownColor = Color.White;
        private static readonly Color UpColor = Color.White;

        public void Run()
        {
            BuildTray();
            BuildMenu();

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += OnTick;

            _sw = Stopwatch.StartNew();
            _timer.Start();

            UpdateSpeed();

            Application.Run();
        }

        private void OnTick(object sender, EventArgs e)
        {
            UpdateSpeed();
        }

        private void UpdateSpeed()
        {
            long down, up;
            GetCounters(out down, out up);
            double sec = Math.Max(_sw.Elapsed.TotalSeconds, 0.05);
            _sw.Restart();

            bool ready = _hasPrev;
            double downBps = 0.0, upBps = 0.0;
            if (ready)
            {
                downBps = (down - _prevDown) / sec;
                upBps = (up - _prevUp) / sec;
                if (downBps < 0) downBps = 0;
                if (upBps < 0) upBps = 0;
            }

            _prevDown = down;
            _prevUp = up;
            _hasPrev = true;

            Icon icon = RenderIcon(downBps, upBps, ready);
            Icon old = _currentIcon;
            _tray.Icon = icon;
            _currentIcon = icon;
            if (old != null) old.Dispose();

            _tray.Text = "↓ " + Program.FormatSpeed(downBps) + "    ↑ " + Program.FormatSpeed(upBps);
        }

        private void GetCounters(out long down, out long up)
        {
            down = 0;
            up = 0;

            NetworkInterface[] nics;
            try
            {
                nics = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch
            {
                return;
            }

            foreach (NetworkInterface nic in nics)
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

        private Icon RenderIcon(double downBps, double upBps, bool ready)
        {
            int s = SystemInformation.SmallIconSize.Width;
            if (s < 16) s = 16;
            const int ss = 4;
            int S = s * ss;

            string down = ready ? Program.Compact(downBps) : "…";
            string up = ready ? Program.Compact(upBps) : "…";

            Icon result;
            using (Bitmap hi = new Bitmap(S, S))
            {
                using (Graphics g = Graphics.FromImage(hi))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.Clear(Color.Transparent);

                    float fontSize = s * 0.46f * ss;
                    float lineH = S / 2f;

                    using (Font f = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        DrawText(g, down, f, sf, new RectangleF(0, 0, S, lineH), DownColor, ss);
                        DrawText(g, up, f, sf, new RectangleF(0, lineH, S, lineH), UpColor, ss);
                    }
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
                        DestroyIcon(h);
                    }
                }
            }
            return result;
        }

        private void DrawText(Graphics g, string text, Font f, StringFormat sf, RectangleF rect, Color color, int ss)
        {
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddString(text, f.FontFamily, (int)f.Style, f.Size, rect, sf);
                using (Pen pen = new Pen(Color.Black, 1.0f * ss))
                {
                    pen.LineJoin = LineJoin.Round;
                    g.DrawPath(pen, gp);
                }
                using (SolidBrush b = new SolidBrush(color))
                    g.FillPath(b, gp);
            }
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

            ToolStripMenuItem miAutoStart = new ToolStripMenuItem("Автозапуск при входе в Windows");
            miAutoStart.Click += delegate { ToggleAutoStart(); };

            ToolStripMenuItem miExit = new ToolStripMenuItem("Выход");
            miExit.Click += delegate { Exit(); };

            _menu.Opening += delegate
            {
                miAutoStart.Checked = IsAutoStartEnabled();
            };

            _menu.Items.Add(miAutoStart);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miExit);

            _tray.ContextMenuStrip = _menu;
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

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public void Dispose()
        {
            if (_timer != null) _timer.Dispose();
            if (_menu != null) _menu.Dispose();
            if (_currentIcon != null) _currentIcon.Dispose();
            if (_tray != null) _tray.Dispose();
        }
    }
}
