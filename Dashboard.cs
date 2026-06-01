using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
            _overlay.LeftClicked += OpenStatsDashboard;
        }

        // Snapshots used by the detailed stats dashboard.
        public (float cpu, float ram, long ping) GetLive() => (_cpu, _ram, _ping);
        public IReadOnlyList<(string letter, double used, double total, int pct)> GetDrives() => _drives;

        private void OpenStatsDashboard() => SystemDashboard.Open(this);

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

        public void UpdateStats(float cpu, float ram, long pingMs,
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
            int count = Math.Clamp((int)Math.Round((double)width / slot) + 2, 1, 16);
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
                n.MouseClick += (_, e) =>
                {
                    if (e.Button == MouseButtons.Left) OpenStatsDashboard();
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
        const uint SWP_NOACTIVATE = 0x0010, SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;
        const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
        const int WM_SETTINGCHANGE = 0x001A, WM_DISPLAYCHANGE = 0x007E;

        static readonly Color C_LABEL = Color.FromArgb(180, 185, 200);
        const int PAD_X = 12, GAP = 14;

        private readonly Font _fLabel = new("Segoe UI", 9f, FontStyle.Regular);
        private readonly Font _fValue = new("Segoe UI Semibold", 9f, FontStyle.Bold);
        private List<Seg> _segs = new();
        private int _width;
        private int _lastX = int.MinValue, _lastY = int.MinValue, _lastW = -1, _lastH = -1;

        public event Action? LeftClicked;

        // Horizontal nudge: promoted icons sit just right of the "^" chevron,
        // so start the text a little right of the notification area's left edge.
        const int X_OFFSET = 29;

        // Modern-luxe gradient palette — bright top, dim bottom for a raised
        // "floating panel" feel above the taskbar.
        static readonly Color BG_TOP = Color.FromArgb(50, 54, 70);
        static readonly Color BG_BOT = Color.FromArgb(30, 32, 44);

        public StatOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar   = false;
            TopMost         = true;
            StartPosition   = FormStartPosition.Manual;
            DoubleBuffered  = true;
            BackColor       = BG_TOP;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_SETTINGCHANGE || m.Msg == WM_DISPLAYCHANGE) ApplyPos();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left) LeftClicked?.Invoke();
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

        // Per-segment dot indicator (radius+gap) baked into the width budget.
        const float DOT = 5f, DOT_GAP = 6f;

        private int MeasureWidth(Graphics g)
        {
            float w = PAD_X;
            for (int i = 0; i < _segs.Count; i++)
            {
                w += DOT + DOT_GAP;
                w += g.MeasureString(_segs[i].Label, _fLabel).Width + 4f;
                w += ValueWidth(g, _segs[i]);
                if (i < _segs.Count - 1) w += GAP;
            }
            return (int)Math.Ceiling(w) + PAD_X;
        }

        // Apply a rounded-pill clipping region — makes the overlay float over
        // the taskbar instead of sitting as a flat rectangle.
        private void ApplyShape()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
            int r = Math.Min(10, Height / 2);
            using var path = new GraphicsPath();
            path.AddArc(0, 0, r, r, 180, 90);
            path.AddArc(Width - r, 0, r, r, 270, 90);
            path.AddArc(Width - r, Height - r, r, r, 0, 90);
            path.AddArc(0, Height - r, r, r, 90, 90);
            path.CloseFigure();
            var prev = Region;
            Region = new Region(path);
            prev?.Dispose();
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

            bool first      = _lastW == -1;
            bool posChanged = x != _lastX || y != _lastY || _width != _lastW || h != _lastH;

            if (posChanged)
            {
                bool sizeChanged = Size.Width != _width || Size.Height != h;
                _lastX = x; _lastY = y; _lastW = _width; _lastH = h;
                if (sizeChanged)
                {
                    Size = new Size(_width, h);
                    ApplyShape();
                }
                uint flags = SWP_NOACTIVATE | (first ? SWP_SHOWWINDOW : 0);
                SetWindowPos(Handle, HWND_TOPMOST, x, y, _width, h, flags);
            }
            else
            {
                // Re-assert HWND_TOPMOST without moving/resizing — protects against
                // being covered when another app takes focus. NOMOVE+NOSIZE means
                // no paint cycle is triggered, so no flicker.
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                             SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var bg = new LinearGradientBrush(ClientRectangle, BG_TOP, BG_BOT, 90f))
                g.FillRectangle(bg, ClientRectangle);

            // Subtle 1px inner highlight at the top — raised-panel cue.
            using (var hi = new SolidBrush(Color.FromArgb(34, 255, 255, 255)))
                g.FillRectangle(hi, 0, 0, Width, 1);

            using var lBr = new SolidBrush(C_LABEL);
            float x = PAD_X, midY = Height / 2f;

            for (int i = 0; i < _segs.Count; i++)
            {
                var s = _segs[i];

                // Colored dot anchor — matches the per-segment accent.
                using (var dot = new SolidBrush(s.Color))
                    g.FillEllipse(dot, x, midY - DOT / 2f, DOT, DOT);
                x += DOT + DOT_GAP;

                var lSz = g.MeasureString(s.Label, _fLabel);
                g.DrawString(s.Label, _fLabel, lBr, x, midY - lSz.Height / 2f);
                x += lSz.Width + 4f;

                using var vBr = new SolidBrush(s.Color);
                var vSz = g.MeasureString(s.Value, _fValue);
                g.DrawString(s.Value, _fValue, vBr, x, midY - vSz.Height / 2f);
                x += ValueWidth(g, s) + GAP;
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
                "→  Click the bar for detailed system info",
                "→  Right-click the bar for options or to exit",
                "→  Starts automatically with Windows",
            ];
            foreach (var item in items) { g.DrawString(item, fL, lBr, x, y); y += 24; }

            y += 4;
            g.DrawString("Tip: right-click the stats area to exit LocTray.", fD, dBr, x, y);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SystemDashboard — modern-luxe info panel. Borderless rounded card with a
    //  soft vertical gradient, accent-coloured section cards, gradient progress
    //  bars, draggable anywhere. Opens by clicking the tray bar.
    // ═══════════════════════════════════════════════════════════════════════════
    public sealed class SystemDashboard : Form
    {
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

        // Palette
        static readonly Color BG_TOP    = Color.FromArgb(26, 29, 39);
        static readonly Color BG_BOT    = Color.FromArgb(17, 20, 28);
        static readonly Color CARD_HI   = Color.FromArgb(38, 42, 56);
        static readonly Color CARD_BG   = Color.FromArgb(30, 34, 46);
        static readonly Color CARD_BD   = Color.FromArgb(50, 54, 70);
        static readonly Color BAR_TRACK = Color.FromArgb(40, 44, 60);
        static readonly Color TEXT_HI   = Color.FromArgb(244, 246, 250);
        static readonly Color TEXT_MD   = Color.FromArgb(169, 175, 192);
        static readonly Color TEXT_LO   = Color.FromArgb(110, 116, 132);

        static readonly Color A_CPU = Color.FromArgb( 91, 180, 255), B_CPU = Color.FromArgb(135, 202, 255);
        static readonly Color A_MEM = Color.FromArgb(190, 140, 255), B_MEM = Color.FromArgb(214, 178, 255);
        static readonly Color A_DSK = Color.FromArgb(120, 200, 100), B_DSK = Color.FromArgb(160, 224, 144);
        static readonly Color A_GPU = Color.FromArgb(255, 159,  78);
        static readonly Color A_NET = Color.FromArgb( 78, 210, 200);

        private static SystemDashboard? _open;

        public static void Open(TaskbarBar bar)
        {
            if (_open != null && !_open.IsDisposed)
            {
                _open.BringToFront();
                _open.Activate();
                return;
            }
            _open = new SystemDashboard(bar);
            _open.FormClosed += (_, _) => _open = null;
            _open.Show();
        }

        private readonly TaskbarBar _bar;
        private readonly SystemInfo _info;
        private readonly System.Windows.Forms.Timer _refresh;

        private SystemDashboard(TaskbarBar bar)
        {
            _bar  = bar;
            _info = SystemInfo.Gather();

            int driveCount = Math.Max(1, _bar.GetDrives().Count);
            int storageH = 56 + driveCount * 36;
            const int W = 480;
            int H = 28 + 70 + 116 + 14 + 116 + 14 + storageH + 14 + 90 + 14 + 80 + 24;

            FormBorderStyle = FormBorderStyle.None;
            BackColor       = BG_TOP;
            ShowInTaskbar   = false;
            StartPosition   = FormStartPosition.Manual;
            Size            = new Size(W, H);
            TopMost         = true;
            DoubleBuffered  = true;
            KeyPreview      = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);

            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            Location = new Point(Math.Max(8, wa.Right - Size.Width - 16),
                                 Math.Max(8, wa.Bottom - Size.Height - 16));

            var btnClose = new CloseGlyph(BG_TOP);
            btnClose.Click += (_, _) => Close();
            btnClose.Location = new Point(Width - btnClose.Width - 18, 18);
            Controls.Add(btnClose);

            var btnExpand = new ExpandGlyph(BG_TOP);
            btnExpand.Click += (_, _) =>
            {
                LiveDashboard.Open(_bar);
                Close();
            };
            btnExpand.Location = new Point(btnClose.Left - btnExpand.Width - 6, 18);
            Controls.Add(btnExpand);

            _refresh = new System.Windows.Forms.Timer { Interval = 200 };
            _refresh.Tick += (_, _) => Invalidate();
            _refresh.Start();
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // Rounded outer corners via clipping region.
            int r = 14;
            var path = new GraphicsPath();
            path.AddArc(0, 0, r, r, 180, 90);
            path.AddArc(Width - r, 0, r, r, 270, 90);
            path.AddArc(Width - r, Height - r, r, r, 0, 90);
            path.AddArc(0, Height - r, r, r, 90, 90);
            path.CloseFigure();
            Region = new Region(path);

            // Win11 native rounded corner hint (harmless on Win10).
            try
            {
                int pref = DWMWCP_ROUND;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { }
        }

        // Drag from anywhere — frameless windows lose the title bar so we
        // forward the click to the OS as if the caption was hit.
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
        }

        protected override bool ProcessCmdKey(ref Message m, Keys k)
        {
            if (k == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref m, k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            using (var bg = new LinearGradientBrush(ClientRectangle, BG_TOP, BG_BOT, 90f))
                g.FillRectangle(bg, ClientRectangle);

            using (var acc = new LinearGradientBrush(
                new Rectangle(0, 0, Width, 2), A_CPU, A_MEM, 0f))
                g.FillRectangle(acc, 0, 0, Width, 2);

            var (cpu, ram, ping) = _bar.GetLive();
            var drives = _bar.GetDrives();

            const int pad = 24;
            int x = pad, cardW = Width - 2 * pad, y = 28;

            using (var fT  = new Font("Segoe UI Semibold", 17f))
            using (var fSt = new Font("Segoe UI", 9.5f))
            {
                using var hi = new SolidBrush(TEXT_HI);
                using var md = new SolidBrush(TEXT_MD);
                g.DrawString("LocTray", fT, hi, x, y);
                g.DrawString("System Information", fSt, md, x, y + 30);
            }
            y += 70;

            y = DrawStatCard(g, x, y, cardW, "CPU",
                _info.CpuName,
                $"{_info.CpuGhz:0.00} GHz   ·   {_info.CpuCores} cores   ·   {_info.CpuThreads} threads",
                cpu, A_CPU, B_CPU);
            y += 14;

            string memHead = $"{_info.RamTotalGB:0.#} GB {_info.RamType}";
            if (_info.RamSpeedMhz > 0) memHead += $" @ {_info.RamSpeedMhz} MHz";
            double usedGB = _info.RamTotalGB * ram / 100.0;
            string memSub = _info.RamMfr.Length > 0
                ? $"{_info.RamMfr}   ·   {usedGB:0.#} / {_info.RamTotalGB:0.#} GB"
                : $"{usedGB:0.#} / {_info.RamTotalGB:0.#} GB";
            y = DrawStatCard(g, x, y, cardW, "MEMORY", memHead, memSub, ram, A_MEM, B_MEM);
            y += 14;

            y = DrawStorageCard(g, x, y, cardW, drives);
            y += 14;

            string gpuSub = _info.GpuMemoryGB > 0 ? $"{_info.GpuMemoryGB:0.#} GB VRAM" : "Graphics adapter";
            y = DrawStaticCard(g, x, y, cardW, "GPU", _info.GpuName, gpuSub, A_GPU);
            y += 14;

            DrawNetworkCard(g, x, y, cardW, ping);
        }

        private static int DrawStatCard(Graphics g, int x, int y, int w, string label,
                                        string headline, string sub, float pct, Color a, Color b)
        {
            const int h = 116;
            DrawCardBg(g, x, y, w, h);

            using var fL = new Font("Segoe UI Semibold", 8.5f);
            using var fV = new Font("Segoe UI Semibold", 22f);
            using var fH = new Font("Segoe UI Semibold", 11f);
            using var fS = new Font("Segoe UI", 9.5f);

            using (var br = new SolidBrush(a))
                g.DrawString(label, fL, br, x + 20, y + 16);

            using (var br = new LinearGradientBrush(
                new Rectangle(x + 20, y + 32, 24, 2), a, b, 0f))
                g.FillRectangle(br, x + 20, y + 32, 24, 2);

            string pctStr = $"{(int)Math.Round(pct)}%";
            var pctSz = g.MeasureString(pctStr, fV);
            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(pctStr, fV, br, x + w - pctSz.Width - 18, y + 6);

            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(headline, fH, br, x + 20, y + 46);
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString(sub, fS, br, x + 20, y + 68);

            DrawProgressBar(g, x + 20, y + h - 22, w - 40, 6, pct, a, b);

            return y + h;
        }

        private static int DrawStaticCard(Graphics g, int x, int y, int w, string label,
                                          string headline, string sub, Color a)
        {
            const int h = 90;
            DrawCardBg(g, x, y, w, h);

            using var fL = new Font("Segoe UI Semibold", 8.5f);
            using var fH = new Font("Segoe UI Semibold", 11f);
            using var fS = new Font("Segoe UI", 9.5f);

            using (var br = new SolidBrush(a))
                g.DrawString(label, fL, br, x + 20, y + 16);
            using (var br = new SolidBrush(a))
                g.FillRectangle(br, x + 20, y + 32, 24, 2);

            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(headline, fH, br, x + 20, y + 42);
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString(sub, fS, br, x + 20, y + 62);

            return y + h;
        }

        private static int DrawStorageCard(Graphics g, int x, int y, int w,
            IReadOnlyList<(string letter, double used, double total, int pct)> drives)
        {
            int rows = Math.Max(1, drives.Count);
            int h = 56 + rows * 36;
            DrawCardBg(g, x, y, w, h);

            using var fL = new Font("Segoe UI Semibold", 8.5f);
            using var fH = new Font("Segoe UI Semibold", 10.5f);
            using var fS = new Font("Segoe UI", 9f);

            using (var br = new SolidBrush(A_DSK))
                g.DrawString("STORAGE", fL, br, x + 20, y + 16);
            using (var br = new SolidBrush(A_DSK))
                g.FillRectangle(br, x + 20, y + 32, 24, 2);

            if (drives.Count == 0)
            {
                using var br = new SolidBrush(TEXT_LO);
                g.DrawString("No fixed drives detected", fS, br, x + 20, y + 44);
                return y + h;
            }

            int rowY = y + 44;
            foreach (var d in drives)
            {
                using (var br = new SolidBrush(TEXT_HI))
                    g.DrawString($"{d.letter}\\", fH, br, x + 20, rowY);

                string info = $"{d.used:0.#} / {d.total:0.#} GB";
                using (var br = new SolidBrush(TEXT_MD))
                    g.DrawString(info, fS, br, x + 64, rowY + 2);

                string pctStr = $"{d.pct}%";
                var pctSz = g.MeasureString(pctStr, fH);
                using (var br = new SolidBrush(TEXT_HI))
                    g.DrawString(pctStr, fH, br, x + w - pctSz.Width - 20, rowY);

                DrawProgressBar(g, x + 20, rowY + 22, w - 40, 4, d.pct, A_DSK, B_DSK);
                rowY += 36;
            }

            return y + h;
        }

        private static int DrawNetworkCard(Graphics g, int x, int y, int w, long ping)
        {
            const int h = 80;
            DrawCardBg(g, x, y, w, h);

            using var fL = new Font("Segoe UI Semibold", 8.5f);
            using var fV = new Font("Segoe UI Semibold", 18f);
            using var fS = new Font("Segoe UI", 9.5f);

            using (var br = new SolidBrush(A_NET))
                g.DrawString("NETWORK", fL, br, x + 20, y + 16);
            using (var br = new SolidBrush(A_NET))
                g.FillRectangle(br, x + 20, y + 32, 24, 2);

            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString("Ping to 8.8.8.8", fS, br, x + 20, y + 52);

            string val = ping < 0 ? "—" : $"{ping} ms";
            var sz = g.MeasureString(val, fV);
            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(val, fV, br, x + w - sz.Width - 20, y + 38);

            return y + h;
        }

        private static void DrawCardBg(Graphics g, int x, int y, int w, int h)
        {
            const int r = 10;
            using var path = new GraphicsPath();
            path.AddArc(x, y, r, r, 180, 90);
            path.AddArc(x + w - r, y, r, r, 270, 90);
            path.AddArc(x + w - r, y + h - r, r, r, 0, 90);
            path.AddArc(x, y + h - r, r, r, 90, 90);
            path.CloseFigure();

            using (var bg = new LinearGradientBrush(
                new Rectangle(x, y, w, Math.Max(h, 1)), CARD_HI, CARD_BG, 90f))
                g.FillPath(bg, path);
            using (var bd = new Pen(CARD_BD, 1))
                g.DrawPath(bd, path);
        }

        private static void DrawProgressBar(Graphics g, int x, int y, int w, int h,
                                            float pct, Color a, Color b)
        {
            using (var trackPath = new GraphicsPath())
            {
                trackPath.AddArc(x, y, h, h, 90, 180);
                trackPath.AddArc(x + w - h, y, h, h, 270, 180);
                trackPath.CloseFigure();
                using var trackBr = new SolidBrush(BAR_TRACK);
                g.FillPath(trackBr, trackPath);
            }

            pct = Math.Clamp(pct, 0f, 100f);
            if (pct < 0.5f) return;

            int fillW = Math.Max(h, (int)Math.Round(w * pct / 100.0));
            using var fillPath = new GraphicsPath();
            fillPath.AddArc(x, y, h, h, 90, 180);
            fillPath.AddArc(x + fillW - h, y, h, h, 270, 180);
            fillPath.CloseFigure();
            using var fillBr = new LinearGradientBrush(
                new Rectangle(x, y, fillW, h), a, b, 0f);
            g.FillPath(fillBr, fillPath);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refresh.Stop();
            _refresh.Dispose();
            base.OnFormClosed(e);
        }

        // Custom X glyph in the corner — matches the form bg, fades in a soft
        // hover halo without any button chrome.
        private sealed class CloseGlyph : Control
        {
            private bool _hover;

            public CloseGlyph(Color bg)
            {
                Size = new Size(28, 28);
                Cursor = Cursors.Hand;
                DoubleBuffered = true;
                BackColor = bg;
                TabStop = false;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                if (_hover)
                {
                    using var bg = new SolidBrush(Color.FromArgb(48, 255, 255, 255));
                    g.FillEllipse(bg, 0, 0, Width - 1, Height - 1);
                }

                int p = 9;
                var c = _hover ? Color.White : Color.FromArgb(169, 175, 192);
                using var pen = new Pen(c, 1.6f);
                g.DrawLine(pen, p, p, Width - p - 1, Height - p - 1);
                g.DrawLine(pen, Width - p - 1, p, p, Height - p - 1);
            }
        }

        // Diagonal "expand-to-full-window" arrow ↗ — opens the LiveDashboard.
        private sealed class ExpandGlyph : Control
        {
            private bool _hover;

            public ExpandGlyph(Color bg)
            {
                Size = new Size(28, 28);
                Cursor = Cursors.Hand;
                DoubleBuffered = true;
                BackColor = bg;
                TabStop = false;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                if (_hover)
                {
                    using var bg = new SolidBrush(Color.FromArgb(48, 255, 255, 255));
                    g.FillEllipse(bg, 0, 0, Width - 1, Height - 1);
                }

                int p = 9;
                int left = p, right = Width - p - 1, top = p, bot = Height - p - 1;
                var c = _hover ? Color.White : Color.FromArgb(169, 175, 192);
                using var pen = new Pen(c, 1.6f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

                // Diagonal shaft from bottom-left to top-right
                g.DrawLine(pen, left, bot, right, top);
                // Arrowhead at top-right (two short ticks)
                int ah = 6;
                g.DrawLine(pen, right, top, right - ah, top);
                g.DrawLine(pen, right, top, right, top + ah);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SystemInfo — static hardware snapshot pulled from WMI.
    // ═══════════════════════════════════════════════════════════════════════════
    internal sealed record SystemInfo(
        string CpuName,
        double CpuGhz,
        int    CpuCores,
        int    CpuThreads,
        double RamTotalGB,
        int    RamSpeedMhz,
        string RamType,
        string RamMfr,
        string GpuName,
        double GpuMemoryGB)
    {
        public static SystemInfo Gather()
        {
            string cpuName = "Unknown CPU";
            double cpuGhz  = 0;
            int    cores   = 0, threads = 0;
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT Name, MaxClockSpeed, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                foreach (ManagementObject mo in s.Get())
                {
                    cpuName = (mo["Name"]?.ToString() ?? cpuName).Trim();
                    cpuGhz  = U32(mo["MaxClockSpeed"]) / 1000.0;
                    cores   = (int)U32(mo["NumberOfCores"]);
                    threads = (int)U32(mo["NumberOfLogicalProcessors"]);
                    break;
                }
            }
            catch { }

            double ramGB   = 0;
            int    ramSpd  = 0;
            string ramType = "RAM";
            string ramMfr  = "";
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT Capacity, Speed, MemoryType, SMBIOSMemoryType, Manufacturer FROM Win32_PhysicalMemory");
                foreach (ManagementObject mo in s.Get())
                {
                    ramGB += U64(mo["Capacity"]) / 1073741824.0;
                    int sp = (int)U32(mo["Speed"]);
                    if (sp > ramSpd) ramSpd = sp;

                    string t = MemType((int)U32(mo["SMBIOSMemoryType"]),
                                       (int)U32(mo["MemoryType"]));
                    if (t != "RAM") ramType = t;

                    if (ramMfr.Length == 0)
                    {
                        string mfr = (mo["Manufacturer"]?.ToString() ?? "").Trim();
                        if (mfr.Length > 0 &&
                            !mfr.StartsWith("00", StringComparison.Ordinal) &&
                            !mfr.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                            ramMfr = mfr;
                    }
                }
            }
            catch { }

            string gpu  = "Unknown GPU";
            double gpuGB = 0;
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM FROM Win32_VideoController");
                foreach (ManagementObject mo in s.Get())
                {
                    string name = (mo["Name"]?.ToString() ?? "").Trim();
                    if (name.Length == 0) continue;
                    gpu   = name;
                    gpuGB = U32(mo["AdapterRAM"]) / 1073741824.0;
                    break;
                }
            }
            catch { }

            return new SystemInfo(cpuName, cpuGhz, cores, threads,
                                  ramGB, ramSpd, ramType, ramMfr,
                                  gpu, gpuGB);
        }

        private static uint  U32(object? v) { try { return v == null ? 0u  : Convert.ToUInt32(v); } catch { return 0u;  } }
        private static ulong U64(object? v) { try { return v == null ? 0UL : Convert.ToUInt64(v); } catch { return 0UL; } }

        private static string MemType(int smbios, int legacy)
        {
            string Map(int v) => v switch
            {
                34 => "DDR5",
                26 => "DDR4",
                24 => "DDR3",
                22 or 21 => "DDR2",
                20 => "DDR",
                _  => "RAM"
            };
            string fromSmbios = Map(smbios);
            return fromSmbios != "RAM" ? fromSmbios : Map(legacy);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  LiveDashboard — full-window dashboard opened from the popup's expand
    //  arrow. Three rows of cards: CPU+MEM hero with sparklines & per-core
    //  bars, full-width storage with per-drive read/write, then GPU/Network/
    //  System (+ Battery on laptops). Status pill in the header.
    // ═══════════════════════════════════════════════════════════════════════════
    public sealed class LiveDashboard : Form
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        // Palette — same family as the popup.
        static readonly Color BG_TOP    = Color.FromArgb(26, 29, 39);
        static readonly Color BG_BOT    = Color.FromArgb(17, 20, 28);
        static readonly Color CARD_HI   = Color.FromArgb(38, 42, 56);
        static readonly Color CARD_BG   = Color.FromArgb(30, 34, 46);
        static readonly Color CARD_BD   = Color.FromArgb(50, 54, 70);
        static readonly Color BAR_TRACK = Color.FromArgb(40, 44, 60);
        static readonly Color TEXT_HI   = Color.FromArgb(244, 246, 250);
        static readonly Color TEXT_MD   = Color.FromArgb(169, 175, 192);
        static readonly Color TEXT_LO   = Color.FromArgb(110, 116, 132);

        static readonly Color A_CPU = Color.FromArgb( 91, 180, 255), B_CPU = Color.FromArgb(135, 202, 255);
        static readonly Color A_MEM = Color.FromArgb(190, 140, 255), B_MEM = Color.FromArgb(214, 178, 255);
        static readonly Color A_DSK = Color.FromArgb(120, 200, 100), B_DSK = Color.FromArgb(160, 224, 144);
        static readonly Color A_GPU = Color.FromArgb(255, 159,  78);
        static readonly Color A_NET = Color.FromArgb( 78, 210, 200);
        static readonly Color A_SYS = Color.FromArgb(180, 185, 200);
        static readonly Color A_BAT = Color.FromArgb(120, 200, 100);

        // Status pill colors (green / amber / red)
        static readonly Color S_GOOD = Color.FromArgb(120, 200, 100);
        static readonly Color S_WARN = Color.FromArgb(255, 180,  80);
        static readonly Color S_BAD  = Color.FromArgb(255, 110, 130);

        private static LiveDashboard? _open;

        public static void Open(TaskbarBar bar)
        {
            if (_open != null && !_open.IsDisposed)
            {
                if (_open.WindowState == FormWindowState.Minimized)
                    _open.WindowState = FormWindowState.Normal;
                _open.BringToFront();
                _open.Activate();
                return;
            }
            _open = new LiveDashboard(bar);
            _open.FormClosed += (_, _) => _open = null;
            _open.Show();
        }

        private readonly TaskbarBar _bar;
        private readonly SystemInfo _info;
        private readonly ExtendedStats _ext;
        private readonly System.Windows.Forms.Timer _refresh;

        private readonly float[] _cpuHistory = new float[120];
        private readonly float[] _ramHistory = new float[120];
        private int _historyIdx;

        private readonly PerformanceCounter[]? _coreCounters;
        private readonly float[]? _coreValues;
        private readonly PerformanceCounter? _threadsCounter;

        private readonly NetworkInterface? _netIface;
        private long _prevSent, _prevRecv;
        private double _bpsSent, _bpsRecv;

        private readonly Dictionary<string, PerformanceCounter>? _diskReadCnt;
        private readonly Dictionary<string, PerformanceCounter>? _diskWriteCnt;
        private readonly Dictionary<string, (double r, double w)> _diskRates = new();

        private float _thermalC = -1f;
        private int _procCount, _threadCount;
        private readonly bool _hasBattery;

        private LiveDashboard(TaskbarBar bar)
        {
            _bar  = bar;
            _info = SystemInfo.Gather();
            _ext  = ExtendedStats.Gather();
            _hasBattery = !SystemInformation.PowerStatus.BatteryChargeStatus
                .HasFlag(BatteryChargeStatus.NoSystemBattery);

            Text            = "LocTray — Live Performance";
            BackColor       = BG_TOP;
            ForeColor       = TEXT_HI;
            ShowInTaskbar   = true;
            StartPosition   = FormStartPosition.CenterScreen;
            DoubleBuffered  = true;
            KeyPreview      = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);

            // Size adapts so the storage card always fits its drives.
            int driveCount = Math.Max(1, _bar.GetDrives().Count);
            int storageH = 56 + driveCount * 72;
            int totalH = 24 + 90 + 14 + 300 + 14 + storageH + 14 + 140 + 24;
            ClientSize  = new Size(1100, totalH);
            MinimumSize = new Size(960, Math.Min(totalH, 760));

            // Per-core CPU counters
            try
            {
                int t = Environment.ProcessorCount;
                _coreCounters = new PerformanceCounter[t];
                for (int i = 0; i < t; i++)
                    _coreCounters[i] = new PerformanceCounter("Processor", "% Processor Time", i.ToString());
                foreach (var pc in _coreCounters) pc.NextValue();
                _coreValues = new float[t];
            }
            catch { _coreCounters = null; _coreValues = null; }

            // System-wide thread count
            try
            {
                _threadsCounter = new PerformanceCounter("System", "Threads", "");
                _threadsCounter.NextValue();
            }
            catch { _threadsCounter = null; }

            // Fastest live network adapter for ↓↑ throughput
            try
            {
                _netIface = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .OrderByDescending(n => n.Speed)
                    .FirstOrDefault();
                if (_netIface != null)
                {
                    var s = _netIface.GetIPv4Statistics();
                    _prevSent = s.BytesSent;
                    _prevRecv = s.BytesReceived;
                }
            }
            catch { _netIface = null; }

            // Per-drive read/write counters (PhysicalDisk → matched by letter)
            try
            {
                _diskReadCnt  = new Dictionary<string, PerformanceCounter>();
                _diskWriteCnt = new Dictionary<string, PerformanceCounter>();
                var cat = new PerformanceCounterCategory("PhysicalDisk");
                var instances = cat.GetInstanceNames();
                foreach (var d in _bar.GetDrives())
                {
                    var inst = instances.FirstOrDefault(n => n.Contains(d.letter + ":"));
                    if (inst == null) continue;
                    var r = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec",  inst);
                    var w = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", inst);
                    try { r.NextValue(); w.NextValue(); } catch { }
                    _diskReadCnt[d.letter]  = r;
                    _diskWriteCnt[d.letter] = w;
                }
            }
            catch { _diskReadCnt = null; _diskWriteCnt = null; }

            _refresh = new System.Windows.Forms.Timer { Interval = 500 };
            _refresh.Tick += (_, _) => TickRefresh();
            _refresh.Start();
            TickRefresh();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Dark title bar on Win11 (no-op on older Windows).
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
            catch { }
        }

        protected override bool ProcessCmdKey(ref Message m, Keys k)
        {
            if (k == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref m, k);
        }

        private void TickRefresh()
        {
            var (cpu, ram, _) = _bar.GetLive();
            _cpuHistory[_historyIdx % _cpuHistory.Length] = cpu;
            _ramHistory[_historyIdx % _ramHistory.Length] = ram;
            _historyIdx++;

            if (_coreCounters != null && _coreValues != null)
                for (int i = 0; i < _coreCounters.Length; i++)
                    try { _coreValues[i] = _coreCounters[i].NextValue(); } catch { }

            if (_threadsCounter != null)
                try { _threadCount = (int)_threadsCounter.NextValue(); } catch { }

            if (_netIface != null)
            {
                try
                {
                    var s = _netIface.GetIPv4Statistics();
                    long ds = Math.Max(0, s.BytesSent     - _prevSent);
                    long dr = Math.Max(0, s.BytesReceived - _prevRecv);
                    _prevSent = s.BytesSent;
                    _prevRecv = s.BytesReceived;
                    // 500ms interval → multiply by 2 for per-second.
                    _bpsSent = ds * 2.0;
                    _bpsRecv = dr * 2.0;
                }
                catch { }
            }

            if (_diskReadCnt != null && _diskWriteCnt != null)
            {
                foreach (var d in _bar.GetDrives())
                {
                    try
                    {
                        double r = _diskReadCnt.TryGetValue(d.letter, out var rc)  ? rc.NextValue() : 0;
                        double w = _diskWriteCnt.TryGetValue(d.letter, out var wc) ? wc.NextValue() : 0;
                        _diskRates[d.letter] = (r, w);
                    }
                    catch { }
                }
            }

            try { _procCount = Process.GetProcesses().Length; } catch { }

            // Thermal probe is heavy — only re-read every ~5s.
            if (_historyIdx == 1 || _historyIdx % 10 == 0)
                _thermalC = ReadThermalC();

            Invalidate();
        }

        // System thermal zone via WMI. Many OEM systems don't expose this
        // (returns nothing) — accurate CPU temps need vendor APIs or a kernel
        // driver, which we intentionally avoid to keep the binary AV-clean.
        private static float ReadThermalC()
        {
            try
            {
                using var s = new ManagementObjectSearcher(@"root\WMI",
                    "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                foreach (ManagementObject mo in s.Get())
                {
                    object? v = mo["CurrentTemperature"];
                    if (v == null) continue;
                    uint raw = Convert.ToUInt32(v);
                    float c = (raw / 10f) - 273.15f;
                    if (c is > 10f and < 150f) return c;
                }
            }
            catch { }
            return -1f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            using (var bg = new LinearGradientBrush(ClientRectangle, BG_TOP, BG_BOT, 90f))
                g.FillRectangle(bg, ClientRectangle);

            using (var acc = new LinearGradientBrush(
                new Rectangle(0, 0, ClientSize.Width, 2), A_CPU, A_MEM, 0f))
                g.FillRectangle(acc, 0, 0, ClientSize.Width, 2);

            const int PAD = 24, GAP = 14;
            int rowW = ClientSize.Width - PAD * 2;

            // Header (90h)
            DrawHeader(g, PAD, PAD, rowW);
            int y = PAD + 90 + GAP;

            // Row 1 — CPU + MEM (50/50)
            int colW = (rowW - GAP) / 2;
            DrawCpuCard(g, PAD, y, colW, 300);
            DrawMemCard(g, PAD + colW + GAP, y, colW, 300);
            y += 300 + GAP;

            // Row 2 — Storage (full width)
            int drivesC = Math.Max(1, _bar.GetDrives().Count);
            int storageH = 56 + drivesC * 72;
            DrawStorageCard(g, PAD, y, rowW, storageH);
            y += storageH + GAP;

            // Row 3 — GPU + Network + System (+ Battery on laptops)
            int cards = _hasBattery ? 4 : 3;
            int cardW = (rowW - GAP * (cards - 1)) / cards;
            int rx = PAD;
            DrawGpuCard(g, rx, y, cardW, 140);    rx += cardW + GAP;
            DrawNetCard(g, rx, y, cardW, 140);    rx += cardW + GAP;
            DrawSystemCard(g, rx, y, cardW, 140); rx += cardW + GAP;
            if (_hasBattery) DrawBatteryCard(g, rx, y, cardW, 140);
        }

        // ──── Header ─────────────────────────────────────────────────────
        private void DrawHeader(Graphics g, int x, int y, int w)
        {
            var (cpu, ram, _) = _bar.GetLive();
            var (statusText, statusColor) = GetStatus(cpu, ram);

            using var fT  = new Font("Segoe UI Semibold", 20f);
            using var fSt = new Font("Segoe UI", 9.5f);
            using var hi  = new SolidBrush(TEXT_HI);
            using var md  = new SolidBrush(TEXT_MD);

            g.DrawString("Live Performance", fT, hi, x, y);

            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            string upStr = up.TotalDays >= 1
                ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m"
                : $"{up.Hours}h {up.Minutes}m {up.Seconds}s";
            string sub = $"{_ext.MachineName}   ·   {_ext.OsName}   ·   Up {upStr}   ·   {_ext.UserName}";
            g.DrawString(sub, fSt, md, x, y + 40);

            // Status pill, right-aligned with title baseline
            using var pillF = new Font("Segoe UI Semibold", 9.5f);
            var pillSz = g.MeasureString(statusText, pillF);
            int pillW = (int)pillSz.Width + 38;
            int pillH = 28;
            DrawStatusPill(g, x + w - pillW, y + 6, pillW, pillH, statusText, statusColor);
        }

        // ──── CPU Card ───────────────────────────────────────────────────
        private void DrawCpuCard(Graphics g, int x, int y, int w, int h)
        {
            DrawCardBg(g, x, y, w, h);

            using var fL   = new Font("Segoe UI Semibold", 9f);
            using var fBig = new Font("Segoe UI Semibold", 28f);
            using var fH   = new Font("Segoe UI Semibold", 11.5f);
            using var fS   = new Font("Segoe UI", 9.5f);
            using var fSm  = new Font("Segoe UI Semibold", 8.5f);
            using var fmt  = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

            using (var br = new SolidBrush(A_CPU)) g.DrawString("CPU", fL, br, x + 20, y + 16);
            using (var br = new LinearGradientBrush(
                new Rectangle(x + 20, y + 34, 28, 2), A_CPU, B_CPU, 0f))
                g.FillRectangle(br, x + 20, y + 34, 28, 2);

            int nameMaxW = w - 40 - 130;
            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(_info.CpuName, fH, br, new RectangleF(x + 20, y + 46, nameMaxW, 20), fmt);

            string specs = $"{_info.CpuGhz:0.00} GHz  ·  {_info.CpuCores}C/{_info.CpuThreads}T  ·  {_ext.CpuArch}";
            if (_thermalC > 0) specs += $"  ·  {_thermalC:0} °C";
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString(specs, fS, br, x + 20, y + 68);

            // Big live %
            var (cpu, _, _) = _bar.GetLive();
            string pctStr = $"{cpu:0.0}%";
            var pctSz = g.MeasureString(pctStr, fBig);
            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(pctStr, fBig, br, x + w - pctSz.Width - 20, y + 12);

            // Sparkline
            DrawSparkline(g, x + 20, y + 100, w - 40, 86, _cpuHistory, _historyIdx, A_CPU);

            // Min / Avg / Max
            var (mn, av, mx) = HistoryStats(_cpuHistory, _historyIdx);
            DrawHistoryRow(g, x + 20, y + 196, mn, av, mx, A_CPU);

            // Per-core
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString("PER-CORE", fSm, br, x + 20, y + 220);
            if (_coreValues != null && _coreValues.Length > 0)
                DrawCoreBars(g, x + 20, y + 238, w - 40, 40, _coreValues, A_CPU, B_CPU);

            // Footer
            string cache = "";
            if (_ext.CpuL2KB > 0) cache += $"L2 {FmtKB(_ext.CpuL2KB)}";
            if (_ext.CpuL3KB > 0) cache += (cache.Length > 0 ? "  ·  " : "") + $"L3 {FmtKB(_ext.CpuL3KB)}";
            string footer = $"Processes {_procCount}  ·  Threads {_threadCount}";
            if (cache.Length > 0) footer += $"  ·  {cache}";
            using (var br = new SolidBrush(TEXT_LO))
                g.DrawString(footer, fSm, br, new RectangleF(x + 20, y + h - 26, w - 40, 18), fmt);
        }

        // ──── Memory Card ────────────────────────────────────────────────
        private void DrawMemCard(Graphics g, int x, int y, int w, int h)
        {
            DrawCardBg(g, x, y, w, h);

            using var fL   = new Font("Segoe UI Semibold", 9f);
            using var fBig = new Font("Segoe UI Semibold", 28f);
            using var fH   = new Font("Segoe UI Semibold", 11.5f);
            using var fS   = new Font("Segoe UI", 9.5f);
            using var fSm  = new Font("Segoe UI Semibold", 8.5f);
            using var fmt  = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

            using (var br = new SolidBrush(A_MEM)) g.DrawString("MEMORY", fL, br, x + 20, y + 16);
            using (var br = new LinearGradientBrush(
                new Rectangle(x + 20, y + 34, 28, 2), A_MEM, B_MEM, 0f))
                g.FillRectangle(br, x + 20, y + 34, 28, 2);

            string memHead = $"{_info.RamTotalGB:0.#} GB {_info.RamType}";
            if (_info.RamSpeedMhz > 0) memHead += $" @ {_info.RamSpeedMhz} MHz";
            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(memHead, fH, br, new RectangleF(x + 20, y + 46, w - 40 - 130, 20), fmt);

            string memSub = _info.RamMfr.Length > 0
                ? $"{_info.RamMfr}  ·  {_ext.MemoryModules} module{(_ext.MemoryModules == 1 ? "" : "s")}"
                : $"{_ext.MemoryModules} module{(_ext.MemoryModules == 1 ? "" : "s")}";
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString(memSub, fS, br, x + 20, y + 68);

            var (_, ram, _) = _bar.GetLive();
            string pctStr = $"{ram:0.0}%";
            var pctSz = g.MeasureString(pctStr, fBig);
            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(pctStr, fBig, br, x + w - pctSz.Width - 20, y + 12);

            DrawSparkline(g, x + 20, y + 100, w - 40, 86, _ramHistory, _historyIdx, A_MEM);

            var (mn, av, mx) = HistoryStats(_ramHistory, _historyIdx);
            DrawHistoryRow(g, x + 20, y + 196, mn, av, mx, A_MEM);

            double usedGB = _info.RamTotalGB * ram / 100.0;
            double freeGB = Math.Max(0, _info.RamTotalGB - usedGB);
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString($"Used {usedGB:0.#} GB  ·  Free {freeGB:0.#} GB", fS, br, x + 20, y + 224);

            string pfLine = _ext.PageFileTotalMB > 0
                ? $"Page file  {_ext.PageFileUsedMB / 1024.0:0.#} / {_ext.PageFileTotalMB / 1024.0:0.#} GB"
                : "Page file  —";
            using (var br = new SolidBrush(TEXT_LO))
                g.DrawString(pfLine, fSm, br, x + 20, y + h - 26);
        }

        // ──── Storage Card ───────────────────────────────────────────────
        private void DrawStorageCard(Graphics g, int x, int y, int w, int h)
        {
            DrawCardBg(g, x, y, w, h);

            using var fL = new Font("Segoe UI Semibold", 9f);
            using var fH = new Font("Segoe UI Semibold", 11.5f);
            using var fS = new Font("Segoe UI", 9f);
            using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

            using (var br = new SolidBrush(A_DSK)) g.DrawString("STORAGE", fL, br, x + 20, y + 16);
            using (var br = new SolidBrush(A_DSK))
                g.FillRectangle(br, x + 20, y + 34, 28, 2);

            var drives = _bar.GetDrives();
            if (drives.Count == 0)
            {
                using var br = new SolidBrush(TEXT_LO);
                g.DrawString("No fixed drives detected", fS, br, x + 20, y + 50);
                return;
            }

            int rowY = y + 50;
            foreach (var d in drives)
            {
                using (var br = new SolidBrush(TEXT_HI))
                    g.DrawString($"{d.letter}\\", fH, br, x + 20, rowY);

                string model = _ext.DriveModels.TryGetValue(d.letter, out var m) ? m : "Local disk";
                int modelW = w - 64 - 80 - 20;
                using (var br = new SolidBrush(TEXT_MD))
                    g.DrawString(model, fS, br, new RectangleF(x + 64, rowY + 2, modelW, 20), fmt);

                string pctStr = $"{d.pct}%";
                var pctSz = g.MeasureString(pctStr, fH);
                using (var br = new SolidBrush(TEXT_HI))
                    g.DrawString(pctStr, fH, br, x + w - pctSz.Width - 20, rowY);

                using (var br = new SolidBrush(TEXT_MD))
                    g.DrawString($"{d.used:0.#} / {d.total:0.#} GB", fS, br, x + 20, rowY + 24);

                if (_diskRates.TryGetValue(d.letter, out var rw))
                {
                    string rwStr = $"↓ {FmtBps(rw.r)}    ↑ {FmtBps(rw.w)}";
                    var rwSz = g.MeasureString(rwStr, fS);
                    using var br = new SolidBrush(TEXT_MD);
                    g.DrawString(rwStr, fS, br, x + w - rwSz.Width - 20, rowY + 24);
                }

                DrawProgressBar(g, x + 20, rowY + 50, w - 40, 6, d.pct, A_DSK, B_DSK);
                rowY += 72;
            }
        }

        // ──── GPU Card ───────────────────────────────────────────────────
        private void DrawGpuCard(Graphics g, int x, int y, int w, int h)
        {
            DrawCardBg(g, x, y, w, h);

            using var fL = new Font("Segoe UI Semibold", 9f);
            using var fH = new Font("Segoe UI Semibold", 11f);
            using var fS = new Font("Segoe UI", 9.5f);
            using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

            using (var br = new SolidBrush(A_GPU)) g.DrawString("GPU", fL, br, x + 20, y + 16);
            using (var br = new SolidBrush(A_GPU))
                g.FillRectangle(br, x + 20, y + 34, 28, 2);

            int textW = w - 40;
            int ty = y + 44;
            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(_info.GpuName, fH, br, new RectangleF(x + 20, ty, textW, 18), fmt);
            ty += 20;
            string vram = _info.GpuMemoryGB > 0 ? $"{_info.GpuMemoryGB:0.#} GB VRAM" : "Graphics adapter";
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString(vram, fS, br, x + 20, ty);
            ty += 18;
            if (_ext.GpuDriver != "—")
            {
                using var br = new SolidBrush(TEXT_MD);
                g.DrawString($"Driver {_ext.GpuDriver}", fS, br, new RectangleF(x + 20, ty, textW, 18), fmt);
                ty += 18;
            }
            if (_ext.GpuResolution != "—")
            {
                using var br = new SolidBrush(TEXT_MD);
                g.DrawString(_ext.GpuResolution, fS, br, x + 20, ty);
            }
        }

        // ──── Network Card ───────────────────────────────────────────────
        private void DrawNetCard(Graphics g, int x, int y, int w, int h)
        {
            DrawCardBg(g, x, y, w, h);

            using var fL = new Font("Segoe UI Semibold", 9f);
            using var fH = new Font("Segoe UI Semibold", 11f);
            using var fS = new Font("Segoe UI", 9.5f);
            using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

            using (var br = new SolidBrush(A_NET)) g.DrawString("NETWORK", fL, br, x + 20, y + 16);
            using (var br = new SolidBrush(A_NET))
                g.FillRectangle(br, x + 20, y + 34, 28, 2);

            int textW = w - 40;
            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(_ext.NetAdapterName, fH, br, new RectangleF(x + 20, y + 44, textW, 18), fmt);

            string linkLine = _ext.LocalIp;
            if (_ext.NetLinkSpeedBps > 0) linkLine += $"  ·  {FmtBits(_ext.NetLinkSpeedBps)}";
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString(linkLine, fS, br, new RectangleF(x + 20, y + 64, textW, 18), fmt);

            using (var br = new SolidBrush(TEXT_HI))
            {
                g.DrawString($"↓ {FmtBps(_bpsRecv)}", fS, br, x + 20, y + 86);
                g.DrawString($"↑ {FmtBps(_bpsSent)}", fS, br, x + 20, y + 106);
            }

            var (_, _, ping) = _bar.GetLive();
            string p = ping < 0 ? "Ping —" : $"Ping {ping} ms";
            var pSz = g.MeasureString(p, fS);
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString(p, fS, br, x + w - pSz.Width - 20, y + 106);
        }

        // ──── System Card ────────────────────────────────────────────────
        private void DrawSystemCard(Graphics g, int x, int y, int w, int h)
        {
            DrawCardBg(g, x, y, w, h);

            using var fL = new Font("Segoe UI Semibold", 9f);
            using var fH = new Font("Segoe UI Semibold", 11f);
            using var fS = new Font("Segoe UI", 9.5f);
            using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

            using (var br = new SolidBrush(A_SYS)) g.DrawString("SYSTEM", fL, br, x + 20, y + 16);
            using (var br = new SolidBrush(A_SYS))
                g.FillRectangle(br, x + 20, y + 34, 28, 2);

            int textW = w - 40;
            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(_ext.SystemModel, fH, br, new RectangleF(x + 20, y + 44, textW, 18), fmt);
            using (var br = new SolidBrush(TEXT_MD))
            {
                g.DrawString(_ext.BiosInfo, fS, br, new RectangleF(x + 20, y + 64, textW, 18), fmt);
                g.DrawString($"User  {_ext.UserName}", fS, br, new RectangleF(x + 20, y + 86, textW, 18), fmt);
                g.DrawString($"Boot  {_ext.BootTime:d MMM HH:mm}", fS, br, x + 20, y + 106);
            }
        }

        // ──── Battery Card ───────────────────────────────────────────────
        private void DrawBatteryCard(Graphics g, int x, int y, int w, int h)
        {
            DrawCardBg(g, x, y, w, h);

            var ps = SystemInformation.PowerStatus;
            float frac = Math.Clamp(ps.BatteryLifePercent, 0f, 1f);

            Color accent = A_BAT;
            if (frac < 0.20f)      accent = S_BAD;
            else if (frac < 0.50f) accent = S_WARN;

            using var fL  = new Font("Segoe UI Semibold", 9f);
            using var fH  = new Font("Segoe UI Semibold", 18f);
            using var fS  = new Font("Segoe UI", 9.5f);

            using (var br = new SolidBrush(accent)) g.DrawString("BATTERY", fL, br, x + 20, y + 16);
            using (var br = new SolidBrush(accent))
                g.FillRectangle(br, x + 20, y + 34, 28, 2);

            int pctInt = (int)Math.Round(frac * 100);
            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString($"{pctInt}%", fH, br, x + 20, y + 44);

            string status;
            if (ps.PowerLineStatus == PowerLineStatus.Online)
                status = ps.BatteryChargeStatus.HasFlag(BatteryChargeStatus.High) ? "Plugged in  ·  Full" : "Charging";
            else
                status = "On battery";

            int remaining = ps.BatteryLifeRemaining;
            if (remaining > 0 && ps.PowerLineStatus != PowerLineStatus.Online)
            {
                var t = TimeSpan.FromSeconds(remaining);
                string time = t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";
                status += $"  ·  {time} left";
            }
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString(status, fS, br, x + 20, y + 80);

            DrawProgressBar(g, x + 20, y + 108, w - 40, 6, pctInt, accent, Color.FromArgb(180, accent));
        }

        // ──── Status pill ────────────────────────────────────────────────
        private static void DrawStatusPill(Graphics g, int x, int y, int w, int h,
                                           string text, Color accent)
        {
            using var path = new GraphicsPath();
            path.AddArc(x, y, h, h, 90, 180);
            path.AddArc(x + w - h, y, h, h, 270, 180);
            path.CloseFigure();

            using (var bg = new SolidBrush(Color.FromArgb(38, accent)))
                g.FillPath(bg, path);
            using (var bd = new Pen(Color.FromArgb(90, accent), 1))
                g.DrawPath(bd, path);

            using (var dot = new SolidBrush(accent))
                g.FillEllipse(dot, x + 12, y + h / 2 - 3, 6, 6);

            using var f = new Font("Segoe UI Semibold", 9.5f);
            using var br = new SolidBrush(accent);
            g.DrawString(text, f, br, x + 26, y + 5);
        }

        // ──── History row (MIN / AVG / MAX) ──────────────────────────────
        private static void DrawHistoryRow(Graphics g, int x, int y,
                                           float min, float avg, float max, Color accent)
        {
            using var fL = new Font("Segoe UI Semibold", 8.5f);
            using var fV = new Font("Segoe UI Semibold", 10f);
            using var brAcc = new SolidBrush(accent);
            using var brHi  = new SolidBrush(TEXT_HI);
            using var brSep = new SolidBrush(TEXT_LO);

            int cx = x;
            void Draw(string lbl, float v, bool last)
            {
                var lSz = g.MeasureString(lbl, fL);
                g.DrawString(lbl, fL, brAcc, cx, y + 2);
                cx += (int)lSz.Width + 4;
                string vStr = $"{v:0.0}%";
                var vSz = g.MeasureString(vStr, fV);
                g.DrawString(vStr, fV, brHi, cx, y);
                cx += (int)vSz.Width;
                if (!last)
                {
                    g.DrawString("  ·  ", fV, brSep, cx, y);
                    cx += 20;
                }
            }
            Draw("MIN", min, false);
            Draw("AVG", avg, false);
            Draw("MAX", max, true);
        }

        private static (float min, float avg, float max) HistoryStats(float[] h, int curIdx)
        {
            int count = Math.Min(curIdx, h.Length);
            if (count == 0) return (0, 0, 0);
            float min = float.MaxValue, max = 0, sum = 0;
            int start = curIdx >= h.Length ? curIdx % h.Length : 0;
            for (int i = 0; i < count; i++)
            {
                int idx = curIdx >= h.Length ? (start + i) % h.Length : i;
                float v = h[idx];
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            return (min, sum / count, max);
        }

        private static (string text, Color color) GetStatus(float cpu, float ram)
        {
            if (cpu > 90 || ram > 90) return ("High load",          S_BAD);
            if (cpu > 70 || ram > 80) return ("Busy",               S_WARN);
            return                          ("All systems normal", S_GOOD);
        }

        private static string FmtBits(long bps)
        {
            if (bps < 1_000_000)     return $"{bps / 1000.0:0} Kbit";
            if (bps < 1_000_000_000) return $"{bps / 1_000_000.0:0} Mbit";
            return $"{bps / 1_000_000_000.0:0.0} Gbit";
        }

        private static string FmtKB(int kb)
        {
            if (kb >= 1024) return $"{kb / 1024.0:0.#} MB";
            return $"{kb} KB";
        }

        private static string FmtBps(double bps)
        {
            if (bps < 1024)            return $"{bps:0} B/s";
            if (bps < 1024 * 1024)     return $"{bps / 1024:0.0} KB/s";
            if (bps < 1024L*1024*1024) return $"{bps / (1024.0 * 1024):0.00} MB/s";
            return $"{bps / (1024.0 * 1024 * 1024):0.00} GB/s";
        }

        private static void DrawCardBg(Graphics g, int x, int y, int w, int h)
        {
            const int r = 10;
            using var path = new GraphicsPath();
            path.AddArc(x, y, r, r, 180, 90);
            path.AddArc(x + w - r, y, r, r, 270, 90);
            path.AddArc(x + w - r, y + h - r, r, r, 0, 90);
            path.AddArc(x, y + h - r, r, r, 90, 90);
            path.CloseFigure();
            using (var bg = new LinearGradientBrush(
                new Rectangle(x, y, w, Math.Max(h, 1)), CARD_HI, CARD_BG, 90f))
                g.FillPath(bg, path);
            using (var bd = new Pen(CARD_BD, 1))
                g.DrawPath(bd, path);
        }

        private static void DrawProgressBar(Graphics g, int x, int y, int w, int h,
                                            float pct, Color a, Color b)
        {
            using (var trackPath = new GraphicsPath())
            {
                trackPath.AddArc(x, y, h, h, 90, 180);
                trackPath.AddArc(x + w - h, y, h, h, 270, 180);
                trackPath.CloseFigure();
                using var trackBr = new SolidBrush(BAR_TRACK);
                g.FillPath(trackBr, trackPath);
            }

            pct = Math.Clamp(pct, 0f, 100f);
            if (pct < 0.5f) return;

            int fillW = Math.Max(h, (int)Math.Round(w * pct / 100.0));
            using var fillPath = new GraphicsPath();
            fillPath.AddArc(x, y, h, h, 90, 180);
            fillPath.AddArc(x + fillW - h, y, h, h, 270, 180);
            fillPath.CloseFigure();
            using var fillBr = new LinearGradientBrush(
                new Rectangle(x, y, fillW, h), a, b, 0f);
            g.FillPath(fillBr, fillPath);
        }

        private static void DrawSparkline(Graphics g, int x, int y, int w, int h,
                                          float[] data, int curIdx, Color a)
        {
            // Track + grid
            using (var path = new GraphicsPath())
            {
                int r = 8;
                path.AddArc(x, y, r, r, 180, 90);
                path.AddArc(x + w - r, y, r, r, 270, 90);
                path.AddArc(x + w - r, y + h - r, r, r, 0, 90);
                path.AddArc(x, y + h - r, r, r, 90, 90);
                path.CloseFigure();
                using var tr = new SolidBrush(Color.FromArgb(35, 38, 52));
                g.FillPath(tr, path);

                using var grid = new Pen(Color.FromArgb(48, 52, 68), 1);
                for (int i = 1; i < 4; i++)
                    g.DrawLine(grid, x + 6, y + h * i / 4, x + w - 6, y + h * i / 4);
            }

            int n = data.Length;
            if (n < 2 || curIdx < 2) return;

            // Oldest sample first → newest at the right edge.
            int start = curIdx >= n ? curIdx % n : 0;
            int count = Math.Min(curIdx, n);
            if (count < 2) return;

            var points = new PointF[count];
            for (int i = 0; i < count; i++)
            {
                int idx = curIdx >= n ? (start + i) % n : i;
                float val = Math.Clamp(data[idx], 0, 100);
                points[i] = new PointF(
                    x + (float)i / (count - 1) * w,
                    y + h - (val / 100f * h) + 0.5f);
            }

            // Filled area under the curve.
            var fillPts = new PointF[count + 2];
            fillPts[0] = new PointF(points[0].X, y + h);
            points.CopyTo(fillPts, 1);
            fillPts[^1] = new PointF(points[^1].X, y + h);
            using (var fill = new LinearGradientBrush(
                new Rectangle(x, y, w, h),
                Color.FromArgb(90, a),
                Color.FromArgb(10, a), 90f))
                g.FillPolygon(fill, fillPts);

            // Curve.
            using (var pen = new Pen(a, 2f) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, points);
        }

        private static void DrawCoreBars(Graphics g, int x, int y, int w, int h,
                                         float[] cores, Color a, Color b)
        {
            int n = cores.Length;
            if (n == 0) return;
            int gap = 4;
            int barW = Math.Max(6, (w - (n - 1) * gap) / n);

            for (int i = 0; i < n; i++)
            {
                int bx = x + i * (barW + gap);
                float pct = Math.Clamp(cores[i], 0, 100);
                int fillH = (int)Math.Round(h * pct / 100.0);

                using (var bg = new SolidBrush(BAR_TRACK))
                    g.FillRectangle(bg, bx, y, barW, h);

                if (fillH > 0)
                {
                    using var fill = new LinearGradientBrush(
                        new Rectangle(bx, y + h - fillH, barW, Math.Max(fillH, 1)), a, b, 90f);
                    g.FillRectangle(fill, bx, y + h - fillH, barW, fillH);
                }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refresh.Stop();
            _refresh.Dispose();
            if (_coreCounters != null)
                foreach (var pc in _coreCounters) try { pc.Dispose(); } catch { }
            try { _threadsCounter?.Dispose(); } catch { }
            if (_diskReadCnt != null)
                foreach (var pc in _diskReadCnt.Values) try { pc.Dispose(); } catch { }
            if (_diskWriteCnt != null)
                foreach (var pc in _diskWriteCnt.Values) try { pc.Dispose(); } catch { }
            base.OnFormClosed(e);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ExtendedStats — extras that only the full LiveDashboard shows. Heavy
    //  WMI queries gathered once when the dashboard opens.
    // ═══════════════════════════════════════════════════════════════════════════
    internal sealed record ExtendedStats(
        string OsName,
        string MachineName,
        string LocalIp,
        string NetAdapterName,
        long   NetLinkSpeedBps,
        string BiosInfo,
        string SystemModel,
        DateTime BootTime,
        string UserName,
        int    CpuL2KB,
        int    CpuL3KB,
        string CpuArch,
        int    MemoryModules,
        int    PageFileUsedMB,
        int    PageFileTotalMB,
        string GpuDriver,
        string GpuResolution,
        Dictionary<string, string> DriveModels)
    {
        public static ExtendedStats Gather()
        {
            string os = Environment.OSVersion.Version switch
            {
                { Major: 10, Build: >= 22000 } => "Windows 11",
                { Major: 10 }                  => "Windows 10",
                _                              => $"Windows {Environment.OSVersion.Version}"
            };
            string machine  = Environment.MachineName;
            string user     = Environment.UserName;
            DateTime boot   = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

            string ip = "—", adapter = "—";
            long linkSpeed = 0;
            try
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .OrderByDescending(n => n.Speed)
                    .FirstOrDefault();
                if (ni != null)
                {
                    adapter = ni.Name;
                    linkSpeed = ni.Speed;
                    var addr = ni.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (addr != null) ip = addr.Address.ToString();
                }
            }
            catch { }

            string biosInfo = "—";
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT Manufacturer, SMBIOSBIOSVersion FROM Win32_BIOS");
                foreach (ManagementObject mo in s.Get())
                {
                    string mfr = (mo["Manufacturer"]?.ToString() ?? "").Trim();
                    string ver = (mo["SMBIOSBIOSVersion"]?.ToString() ?? "").Trim();
                    if (mfr.Length > 0 || ver.Length > 0)
                        biosInfo = $"{mfr} {ver}".Trim();
                    break;
                }
            }
            catch { }

            string sysModel = "—";
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                foreach (ManagementObject mo in s.Get())
                {
                    string mfr   = (mo["Manufacturer"]?.ToString() ?? "").Trim();
                    string model = (mo["Model"]?.ToString() ?? "").Trim();
                    sysModel = $"{mfr} {model}".Trim();
                    if (sysModel.Length == 0) sysModel = "—";
                    break;
                }
            }
            catch { }

            int l2KB = 0, l3KB = 0;
            string cpuArch = "—";
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT L2CacheSize, L3CacheSize, Architecture FROM Win32_Processor");
                foreach (ManagementObject mo in s.Get())
                {
                    l2KB = (int)U32(mo["L2CacheSize"]);
                    l3KB = (int)U32(mo["L3CacheSize"]);
                    int arch = (int)U32(mo["Architecture"]);
                    cpuArch = arch switch
                    {
                        0  => "x86",
                        5  => "ARM",
                        9  => "x64",
                        12 => "ARM64",
                        _  => "Unknown"
                    };
                    break;
                }
            }
            catch { }

            int modules = 0;
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory");
                foreach (ManagementObject mo in s.Get()) modules++;
            }
            catch { }

            int pfUsed = 0, pfTotal = 0;
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT CurrentUsage, AllocatedBaseSize FROM Win32_PageFileUsage");
                foreach (ManagementObject mo in s.Get())
                {
                    pfUsed  += (int)U32(mo["CurrentUsage"]);
                    pfTotal += (int)U32(mo["AllocatedBaseSize"]);
                }
            }
            catch { }

            string gpuDriver = "—", gpuRes = "—";
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController");
                foreach (ManagementObject mo in s.Get())
                {
                    string drv = (mo["DriverVersion"]?.ToString() ?? "").Trim();
                    uint hres = U32(mo["CurrentHorizontalResolution"]);
                    uint vres = U32(mo["CurrentVerticalResolution"]);
                    uint rate = U32(mo["CurrentRefreshRate"]);
                    if (hres > 0 && vres > 0)
                    {
                        if (drv.Length > 0) gpuDriver = drv;
                        gpuRes = rate > 0 ? $"{hres}×{vres} @ {rate} Hz" : $"{hres}×{vres}";
                        break;
                    }
                }
            }
            catch { }

            var driveModels = new Dictionary<string, string>();
            try
            {
                using var ld = new ManagementObjectSearcher(
                    "SELECT DeviceID FROM Win32_LogicalDisk WHERE DriveType=3");
                foreach (ManagementObject lo in ld.Get())
                {
                    string letter = (lo["DeviceID"]?.ToString() ?? "").TrimEnd(':');
                    if (letter.Length == 0) continue;
                    try
                    {
                        using var lp = new ManagementObjectSearcher(
                            $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{letter}:'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
                        foreach (ManagementObject pa in lp.Get())
                        {
                            string partId = (pa["DeviceID"]?.ToString() ?? "");
                            using var pd = new ManagementObjectSearcher(
                                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partId}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                            foreach (ManagementObject dr in pd.Get())
                            {
                                string model = (dr["Model"]?.ToString() ?? "").Trim();
                                if (model.Length > 0) driveModels[letter] = model;
                                break;
                            }
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return new ExtendedStats(os, machine, ip, adapter, linkSpeed,
                                     biosInfo, sysModel, boot, user,
                                     l2KB, l3KB, cpuArch,
                                     modules, pfUsed, pfTotal,
                                     gpuDriver, gpuRes, driveModels);
        }

        private static uint U32(object? v) { try { return v == null ? 0u : Convert.ToUInt32(v); } catch { return 0u; } }
    }
}
