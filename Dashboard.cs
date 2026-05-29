using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LocTray
{
    // One labelled stat segment: "CPU" + "45%" in a colour.
    public record Seg(string Label, string Value, Color Color);

    // ═══════════════════════════════════════════════════════════════════════════
    //  TaskbarBar — hybrid display:
    //   • A row of EMPTY (transparent) Shell_NotifyIcon tray icons reserves space
    //     and is promoted out of the "^" overflow, so the slot sits next to the
    //     clock and stays left-aligned exactly like normal tray icons.
    //   • A top-most overlay window (StatOverlay) is painted directly over those
    //     reserved slots, drawing the full readable "CPU 45%  RAM 62% …" text.
    //  Together: perfect tray placement + legible labelled text.
    // ═══════════════════════════════════════════════════════════════════════════
    public sealed class TaskbarBar : IDisposable
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        // Per-stat colours
        static readonly Color C_CPU  = Color.FromArgb( 96, 180, 255);
        static readonly Color C_RAM  = Color.FromArgb(190, 140, 255);
        static readonly Color C_DISK = Color.FromArgb(120, 200, 100);
        static readonly Color C_PING = Color.FromArgb(255, 180,  80);
        static readonly Color C_HOT  = Color.FromArgb(255,  85,  85);

        private readonly List<NotifyIcon> _icons = new();
        private readonly StatOverlay _overlay = new();
        private Icon? _blank;
        private ContextMenuStrip? _menu;
        private bool _visible;
        private int  _promoteTicks;
        private bool _explorerRestarted;

        float _cpu, _ram;
        long  _ping = -1;
        List<(string letter, double used, double total, int pct)> _drives = new();

        // ── Public API (unchanged signature) ─────────────────────────────────
        public ContextMenuStrip? ContextMenuStrip
        {
            get => _menu;
            set
            {
                _menu = value;
                _overlay.ContextMenuStrip = value;
                foreach (var n in _icons) n.ContextMenuStrip = value;
            }
        }

        public TaskbarBar()
        {
            _blank = MakeBlankIcon();
        }

        public void Show()
        {
            _visible = true;
            foreach (var n in _icons) n.Visible = true;
            _overlay.Show();
            _promoteTicks = 12;
            PromoteOutOfOverflow();
        }

        public void Hide()
        {
            _visible = false;
            foreach (var n in _icons) n.Visible = false;
            _overlay.Hide();
        }

        public void UpdateStats(float cpu, float ram, float read, float write, long pingMs,
                                List<(string, double, double, int)> drives)
        {
            _cpu = cpu; _ram = ram; _ping = pingMs; _drives = drives;

            var segs = BuildSegs();

            // Paint the text overlay; it returns how wide the text is in pixels.
            int width = _overlay.UpdateSegments(segs);

            // Reserve that width with empty tray icons. A promoted Win11 tray slot
            // is ~38px wide (DPI-scaled); match the reserved width to the text so
            // there are no leftover blank slots.
            int slot  = Math.Max(28, (int)(38 * _overlay.DeviceDpi / 96.0));
            int count = Math.Clamp((int)Math.Round((double)width / slot) + 1, 1, 16);
            EnsureIcons(count);

            if (_promoteTicks > 0) { _promoteTicks--; PromoteOutOfOverflow(); }
        }

        public void Dispose()
        {
            foreach (var n in _icons) { n.Visible = false; n.Dispose(); }
            _icons.Clear();
            _blank?.Dispose(); _blank = null;
            _overlay.Dispose();
        }

        // ── Build labelled segments ──────────────────────────────────────────
        private List<Seg> BuildSegs()
        {
            var list = new List<Seg>
            {
                new("CPU", $"{(int)Math.Round(_cpu)}%", _cpu >= 85 ? C_HOT : C_CPU),
                new("RAM", $"{(int)Math.Round(_ram)}%", _ram >= 85 ? C_HOT : C_RAM),
            };

            foreach (var d in _drives)
                list.Add(new($"{d.letter.TrimEnd('\\', ':')}:", $"{d.pct}%",
                             d.pct >= 90 ? C_HOT : C_DISK));

            list.Add(new("PING", _ping < 0 ? "—" : $"{_ping}ms", C_PING));
            return list;
        }

        // ── Keep the empty-icon row at the requested count ───────────────────
        private void EnsureIcons(int count)
        {
            while (_icons.Count < count)
            {
                var n = new NotifyIcon
                {
                    ContextMenuStrip = _menu,
                    Icon             = _blank,
                    Visible          = _visible,
                };
                _icons.Add(n);
            }
            while (_icons.Count > count)
            {
                var last = _icons[^1];
                last.Visible = false; last.Dispose();
                _icons.RemoveAt(_icons.Count - 1);
            }
        }

        private static Icon MakeBlankIcon()
        {
            using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Transparent);
            IntPtr h = bmp.GetHicon();
            try   { return (Icon)Icon.FromHandle(h).Clone(); }
            finally { DestroyIcon(h); }
        }

        // ── Promote our (empty) tray icons out of the Win11 overflow flyout ───
        private void PromoteOutOfOverflow()
        {
            try
            {
                string exe = Environment.ProcessPath ?? Application.ExecutablePath;

                using var root = Registry.CurrentUser.OpenSubKey(
                    @"Control Panel\NotifyIconSettings", writable: true);
                if (root == null) return;

                bool changed = false;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var sub = root.OpenSubKey(name, writable: true);
                    if (sub?.GetValue("ExecutablePath") is not string path) continue;
                    if (!string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;

                    if (sub.GetValue("IsPromoted") is not int p || p != 1)
                    {
                        sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                        changed = true;
                    }
                }

                if (changed && _visible)
                    foreach (var n in _icons) { n.Visible = false; n.Visible = true; }

                if (!_explorerRestarted && !PromotionBootstrapped())
                {
                    _explorerRestarted = true;
                    MarkPromotionBootstrapped();
                    RestartExplorer();
                }
            }
            catch { }
        }

        private static bool PromotionBootstrapped()
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\LocTray");
            return k?.GetValue("PromotionBootstrapped") is int v && v == 1;
        }

        private static void MarkPromotionBootstrapped()
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\LocTray");
            k?.SetValue("PromotionBootstrapped", 1, RegistryValueKind.DWord);
        }

        private static void RestartExplorer()
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
                {
                    try { p.Kill(); } catch { }
                }
                try { System.Diagnostics.Process.Start("explorer.exe"); } catch { }
            }
            catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  StatOverlay — borderless top-most window painted over the reserved tray
    //  slots, showing the readable labelled stats.
    // ═══════════════════════════════════════════════════════════════════════════
    internal sealed class StatOverlay : Form
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindow(string? cls, string? title);
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string? cls, string? title);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        static readonly IntPtr HWND_TOPMOST = new(-1);
        const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000;
        const int WM_SETTINGCHANGE = 0x001A, WM_DISPLAYCHANGE = 0x007E;

        static readonly Color C_LABEL = Color.FromArgb(210, 213, 222);
        const int PAD_X = 8, GAP = 12;

        private readonly Font _fLabel = new("Segoe UI", 9f, FontStyle.Regular);
        private readonly Font _fValue = new("Segoe UI Semibold", 9f, FontStyle.Bold);
        private List<Seg> _segs = new();
        private int _width;

        // Horizontal nudge: promoted icons sit just right of the "^" chevron,
        // so start the text a little right of the notification area's left edge.
        const int X_OFFSET = 28;

        public StatOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar   = false;
            TopMost         = true;
            StartPosition   = FormStartPosition.Manual;
            DoubleBuffered  = true;
            BackColor       = Color.FromArgb(1, 1, 1);
            TransparencyKey = Color.FromArgb(1, 1, 1);
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_SETTINGCHANGE || m.Msg == WM_DISPLAYCHANGE) ApplyPos();
        }

        // Measure the rendered text width in pixels (used to size the icon row),
        // then dock the overlay over the notification area.
        public int UpdateSegments(List<Seg> segs)
        {
            _segs = segs;
            using (var g = CreateGraphics()) _width = MeasureWidth(g);
            ApplyPos();
            Invalidate();
            return _width;
        }

        // Reserve a fixed column width per value so the layout never shifts when
        // a number grows/shrinks (e.g. "9%" → "29%").
        private float ValueWidth(Graphics g, Seg s)
        {
            string tmpl = s.Value.EndsWith("%")  ? "100%"
                        : s.Value.EndsWith("ms") ? "999ms"
                        : s.Value;
            return g.MeasureString(tmpl, _fValue).Width;
        }

        private int MeasureWidth(Graphics g)
        {
            float w = PAD_X;
            for (int i = 0; i < _segs.Count; i++)
            {
                w += g.MeasureString(_segs[i].Label, _fLabel).Width + 4f;
                w += ValueWidth(g, _segs[i]);
                if (i < _segs.Count - 1) w += GAP;
            }
            return (int)Math.Ceiling(w) + PAD_X;
        }

        // Dock over the left edge of the notification area (where our promoted
        // icons sit), extending right, vertically centred on the taskbar.
        private void ApplyPos()
        {
            if (!IsHandleCreated || _width <= 0) return;

            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero || !GetWindowRect(tray, out var tb)) return;

            int leftEdge = tb.Right;
            IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero && GetWindowRect(notify, out var nb))
                leftEdge = nb.Left;

            int barH = tb.Bottom - tb.Top;
            int h = Math.Min(barH, 40);
            int x = leftEdge + X_OFFSET;
            int y = tb.Top + (barH - h) / 2;

            if (Size.Width != _width || Size.Height != h) Size = new Size(_width, h);
            SetWindowPos(Handle, HWND_TOPMOST, x, y, _width, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(TransparencyKey);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using var lBr = new SolidBrush(C_LABEL);
            float x = PAD_X, midY = Height / 2f;

            for (int i = 0; i < _segs.Count; i++)
            {
                var s = _segs[i];

                var lSz = g.MeasureString(s.Label, _fLabel);
                g.DrawString(s.Label, _fLabel, lBr, x, midY - lSz.Height / 2f);
                x += lSz.Width + 4f;

                using var vBr = new SolidBrush(s.Color);
                var vSz = g.MeasureString(s.Value, _fValue);
                g.DrawString(s.Value, _fValue, vBr, x, midY - vSz.Height / 2f);
                x += ValueWidth(g, s) + GAP;   // fixed column, left-aligned
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _fLabel.Dispose(); _fValue.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WelcomePopup — shown once on first install
    // ═══════════════════════════════════════════════════════════════════════════
    public sealed class WelcomePopup : Form
    {
        public WelcomePopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            BackColor       = Color.FromArgb(22, 24, 30);
            ShowInTaskbar   = false;
            StartPosition   = FormStartPosition.CenterScreen;
            Size            = new Size(480, 300);
            TopMost         = true;
            DoubleBuffered  = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);

            var btn = new Button
            {
                Text      = "Got it",
                BackColor = Color.FromArgb(72, 158, 255),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI Semibold", 10),
                Size      = new Size(120, 34),
                Location  = new Point((480 - 120) / 2, 300 - 52),
                Cursor    = Cursors.Hand,
                TabStop   = false
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.Click += (_, _) => Close();
            Controls.Add(btn);
            AcceptButton = btn;
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using var border = new Pen(Color.FromArgb(50, 53, 65), 1);
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            using (var acc = new SolidBrush(Color.FromArgb(72, 158, 255)))
                g.FillRectangle(acc, 0, 0, Width, 4);

            using var fT = new Font("Segoe UI Semibold", 19);
            using var fS = new Font("Segoe UI", 10.5f);
            using var fL = new Font("Segoe UI", 9.5f);
            using var fD = new Font("Segoe UI", 8.5f, FontStyle.Italic);

            using var wBr = new SolidBrush(Color.White);
            using var sBr = new SolidBrush(Color.FromArgb(130, 190, 255));
            using var lBr = new SolidBrush(Color.FromArgb(200, 202, 215));
            using var dBr = new SolidBrush(Color.FromArgb(120, 122, 135));

            int x = 36, y = 28;
            g.DrawString("LocTray", fT, wBr, x, y);                  y += 36;
            g.DrawString("Successfully installed!", fS, sBr, x, y);  y += 32;

            string[] items =
            [
                "→  Live labelled stats, docked next to the clock",
                "→  CPU   RAM   C: (% used)   Ping",
                "→  Full readable text — no hover needed",
                "→  Right-click the bar for options or to exit",
                "→  Starts automatically with Windows",
            ];
            foreach (var item in items) { g.DrawString(item, fL, lBr, x, y); y += 24; }

            y += 4;
            g.DrawString("Tip: right-click the stats area to exit LocTray.", fD, dBr, x, y);
        }
    }
}
