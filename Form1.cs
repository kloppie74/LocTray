using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LocTray
{
    public partial class Form1 : Form
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegPath = @"Software\LocTray";
        private const string AppName    = "LocTray";

        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _startupItem;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly PerformanceCounter _cpuPc = new("Processor", "% Processor Time", "_Total");
        private readonly PerformanceCounter _readPc;
        private readonly PerformanceCounter _writePc;
        private bool _showCpu = true;
        private float _cpu, _ram, _read, _write;

        public Form1()
        {
            // Geen hoofdvenster: form blijft onzichtbaar
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;

            InitAutostart();

            var menu = new ContextMenuStrip();
            _startupItem = new ToolStripMenuItem("Start met Windows")
            {
                Checked = IsAutostartEnabled(),
                CheckOnClick = true
            };
            _startupItem.CheckedChanged += (_, _) => SetAutostart(_startupItem.Checked);
            menu.Items.Add(_startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Afsluiten", null, (_, _) => { _tray.Visible = false; Application.Exit(); });

            _tray = new NotifyIcon
            {
                Visible = true,
                ContextMenuStrip = menu,
                Icon = SystemIcons.Application,
                Text = "LocTray"
            };

            // Pak de primaire fysieke schijf (index 0), anders _Total
            string instance = "_Total";
            try
            {
                instance = new PerformanceCounterCategory("PhysicalDisk")
                    .GetInstanceNames()
                    .FirstOrDefault(n => n.StartsWith("0 ")) ?? "_Total";
            }
            catch { }

            _readPc  = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec",  instance);
            _writePc = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instance);

            // Eerste call van een PerformanceCounter geeft 0 terug
            _cpuPc.NextValue();
            _readPc.NextValue();
            _writePc.NextValue();

            _timer = new System.Windows.Forms.Timer { Interval = 3000 };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Tick();
        }

        // Verberg het venster permanent
        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

        // ---- Autostart (HKCU\...\Run) ----
        private static void InitAutostart()
        {
            // Eerste run: autostart standaard aanzetten. Daarna gebruikersvoorkeur respecteren.
            using var appKey = Registry.CurrentUser.CreateSubKey(AppRegPath);
            if (appKey.GetValue("Initialized") == null)
            {
                SetAutostart(true);
                appKey.SetValue("Initialized", 1);
            }
            else if (IsAutostartEnabled())
            {
                SetAutostart(true); // ververs pad (exe kan verplaatst zijn)
            }
        }

        private static bool IsAutostartEnabled()
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return k?.GetValue(AppName) != null;
        }

        private static void SetAutostart(bool enabled)
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (k == null) return;
            if (enabled)
                k.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
            else
                k.DeleteValue(AppName, throwOnMissingValue: false);
        }

        private void Tick()
        {
            _cpu = _cpuPc.NextValue();

            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref m);
            _ram = m.dwMemoryLoad;

            _read  = _readPc.NextValue();
            _write = _writePc.NextValue();

            int v = (int)Math.Round(_showCpu ? _cpu : _ram);
            DrawIcon(v, _showCpu ? "C" : "R");
            UpdateTooltip();
            _showCpu = !_showCpu;
        }

        private void DrawIcon(int value, string label)
        {
            const int size = 32; // 32x32 voor scherpe weergave (DPI-aware)
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(BgColor(value));

                string txt = value.ToString();
                int fs = value >= 100 ? 16 : 22;
                using var f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel);
                var sz = g.MeasureString(txt, f);
                g.DrawString(txt, f, Brushes.White,
                    (size - sz.Width) / 2f,
                    (size - sz.Height) / 2f - 2);

                // C of R indicator rechtsonder
                using var lf = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel);
                var lsz = g.MeasureString(label, lf);
                g.DrawString(label, lf, Brushes.White, size - lsz.Width - 1, size - lsz.Height + 1);
            }

            IntPtr h = bmp.GetHicon();
            try
            {
                using var tmp = Icon.FromHandle(h);
                var ico = (Icon)tmp.Clone();
                var old = _tray.Icon;
                _tray.Icon = ico;
                old?.Dispose();
            }
            finally
            {
                DestroyIcon(h); // voorkom GDI handle leak
            }
        }

        private static Color BgColor(int v) =>
            v < 50 ? Color.FromArgb(46, 125, 50)  :   // groen
            v < 80 ? Color.FromArgb(230, 145, 0)  :   // oranje
                     Color.FromArgb(198, 40, 40);    // rood

        private void UpdateTooltip()
        {
            var sb = new StringBuilder(127);
            sb.Append("CPU ").Append((int)Math.Round(_cpu)).Append("%  RAM ").Append((int)Math.Round(_ram)).Append('%');

            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                double tot  = d.TotalSize       / 1073741824.0;
                double used = tot - d.TotalFreeSpace / 1073741824.0;
                int pct = (int)Math.Round(used / tot * 100);
                string line = $"\n{d.Name.TrimEnd('\\')} {used:0}GB / {tot:0}GB ({pct}%)";
                if (sb.Length + line.Length > 100) break; // ruimte voor R/W-regel
                sb.Append(line);
            }

            sb.Append('\n').Append("R/W ").Append((_read / 1048576).ToString("0.0"))
              .Append(" / ").Append((_write / 1048576).ToString("0.0")).Append(" MB/s");

            string t = sb.ToString();
            _tray.Text = t.Length > 127 ? t.Substring(0, 127) : t;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            _tray.Visible = false;
            _tray.Icon?.Dispose();
            _tray.Dispose();
            _cpuPc.Dispose();
            _readPc.Dispose();
            _writePc.Dispose();
            base.OnFormClosed(e);
        }
    }
}
