using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LocTray
{
    public partial class Form1 : Form
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME { public uint Low, High; public ulong V => ((ulong)High << 32) | Low; }

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegPath = @"Software\LocTray";
        private const string AppName    = "LocTray";
        private readonly ToolStripMenuItem _startupItem;
        private readonly TaskbarBar _bar;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Ping _pinger = new();

        private float _cpu, _ram;
        private long  _ping = -1;
        private int   _tickCount;
        private bool  _pingInFlight;
        private ulong _prevIdle, _prevKernel, _prevUser;
        private List<(string letter, double used, double total, int pct)> _drives = new();

        public Form1()
        {
            ShowInTaskbar   = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState     = FormWindowState.Minimized;
            Opacity         = 0;

            InitAutostart();

            var menu = new ContextMenuStrip();
            _startupItem = new ToolStripMenuItem("Start met Windows")
            {
                Checked      = IsAutostartEnabled(),
                CheckOnClick = true
            };
            _startupItem.CheckedChanged += (_, _) => SetAutostart(_startupItem.Checked);
            menu.Items.Add(_startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Afsluiten", null, (_, _) => Quit());

            _bar = new TaskbarBar { ContextMenuStrip = menu };

            // Prime CPU sampler (first delta is zero anyway, but cache initial counters).
            ReadCpu();
            _drives = GatherDrives();

            _timer = new System.Windows.Forms.Timer { Interval = 500 };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Tick();

            _bar.Show();

            new WelcomePopup().Show();
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

        private void Quit()
        {
            _bar.Hide();
            Application.Exit();
        }

        // ---- Autostart ----
        private static void InitAutostart()
        {
            using var appKey = Registry.CurrentUser.CreateSubKey(AppRegPath);
            bool firstRun = appKey.GetValue("Initialized") == null;
            if (firstRun)
            {
                SetAutostart(true);
                appKey.SetValue("Initialized", 1);
            }
            else if (IsAutostartEnabled())
            {
                SetAutostart(true);
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
                k.SetValue(AppName, $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"");
            else
                k.DeleteValue(AppName, throwOnMissingValue: false);
        }

        // ---- Main tick (50 ms) ----
        private void Tick()
        {
            _tickCount++;

            _cpu = ReadCpu();

            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref m);
            _ram = m.dwMemoryLoad;

            // Drives refresh every 2s.
            if (_tickCount % 4 == 0) _drives = GatherDrives();

            // Ping every 5s.
            if (_tickCount % 10 == 1 && !_pingInFlight) _ = DoPingAsync();

            _bar.UpdateStats(_cpu, _ram, _ping, _drives);
        }

        // Smooth CPU% via GetSystemTimes deltas — works cleanly at any tick rate
        // (unlike PerformanceCounter which gets jittery below ~250 ms intervals).
        private float ReadCpu()
        {
            if (!GetSystemTimes(out var i, out var k, out var u)) return _cpu;
            ulong iv = i.V, kv = k.V, uv = u.V;
            if (_prevKernel == 0)
            {
                _prevIdle = iv; _prevKernel = kv; _prevUser = uv;
                return 0f;
            }
            ulong total = (kv - _prevKernel) + (uv - _prevUser);
            ulong idle  = iv - _prevIdle;
            _prevIdle = iv; _prevKernel = kv; _prevUser = uv;
            if (total == 0) return _cpu;
            double pct = (1.0 - (double)idle / total) * 100.0;
            return (float)Math.Clamp(pct, 0d, 100d);
        }

        private async Task DoPingAsync()
        {
            _pingInFlight = true;
            try
            {
                var reply = await _pinger.SendPingAsync("8.8.8.8", 1000);
                _ping = reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
            }
            catch
            {
                _ping = -1;
            }
            finally
            {
                _pingInFlight = false;
            }
        }

        private static List<(string letter, double used, double total, int pct)> GatherDrives()
        {
            var list = new List<(string, double, double, int)>();
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                try
                {
                    double tot  = d.TotalSize / 1073741824.0;
                    double used = tot - d.TotalFreeSpace / 1073741824.0;
                    int pct = (int)Math.Round(used / tot * 100);
                    list.Add((d.Name.TrimEnd('\\'), used, tot, pct));
                }
                catch { }
            }
            return list;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            _bar?.Dispose();
            _pinger.Dispose();
            base.OnFormClosed(e);
        }
    }
}
