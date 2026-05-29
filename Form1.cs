using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private readonly ToolStripMenuItem _startupItem;
        private readonly TaskbarBar _bar;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly PerformanceCounter _cpuPc = new("Processor", "% Processor Time", "_Total");
        private readonly PerformanceCounter _readPc;
        private readonly PerformanceCounter _writePc;
        private readonly Ping _pinger = new();

        private float _cpu, _ram, _read, _write;
        private long _ping = -1;
        private int _tickCount;
        private bool _pingInFlight;

        public Form1()
        {
            // Hoofdvenster blijft onzichtbaar
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;

            bool firstRun = InitAutostart();

            // Contextmenu: zit op de TaskbarBar (rechtsklik)
            var menu = new ContextMenuStrip();
            _startupItem = new ToolStripMenuItem("Start met Windows")
            {
                Checked = IsAutostartEnabled(),
                CheckOnClick = true
            };
            _startupItem.CheckedChanged += (_, _) => SetAutostart(_startupItem.Checked);
            menu.Items.Add(_startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Afsluiten", null, (_, _) => Quit());

            _bar = new TaskbarBar { ContextMenuStrip = menu };

            // Disk counter: pak primaire schijf (index 0), fallback _Total
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

            _cpuPc.NextValue();
            _readPc.NextValue();
            _writePc.NextValue();

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Tick();

            _bar.Show();

            if (firstRun)
                new WelcomePopup().Show();
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

        private void Quit()
        {
            _bar.Hide();
            Application.Exit();
        }

        // ---- Autostart ----
        private static bool InitAutostart()
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
            return firstRun;
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

        // ---- Main tick (1s) ----
        private void Tick()
        {
            _cpu = _cpuPc.NextValue();
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref m);
            _ram = m.dwMemoryLoad;
            _read  = _readPc.NextValue();
            _write = _writePc.NextValue();

            var drives = GatherDrives();

            // Ping elke 5s
            if (++_tickCount % 5 == 1 && !_pingInFlight)
                _ = DoPingAsync();

            _bar.UpdateStats(_cpu, _ram, _read, _write, _ping, drives);
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
            _cpuPc.Dispose();
            _readPc.Dispose();
            _writePc.Dispose();
            _pinger.Dispose();
            base.OnFormClosed(e);
        }
    }
}
