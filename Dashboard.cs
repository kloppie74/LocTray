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
    //  LiveDashboard — full-window "Task Manager-style" view. Opened from the
    //  popup's expand arrow. Real resizable Form with a dark Win11 title bar,
    //  live sparklines for CPU/RAM, per-core bars, network bandwidth and a
    //  thermal-zone reading when the motherboard exposes one.
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

        private readonly NetworkInterface? _netIface;
        private long _prevSent, _prevRecv;
        private double _bpsSent, _bpsRecv;

        private float _thermalC = -1;
        private int _procCount;

        private LiveDashboard(TaskbarBar bar)
        {
            _bar  = bar;
            _info = SystemInfo.Gather();
            _ext  = ExtendedStats.Gather();

            Text            = "LocTray — Live Performance";
            BackColor       = BG_TOP;
            ForeColor       = TEXT_HI;
            ShowInTaskbar   = true;
            StartPosition   = FormStartPosition.CenterScreen;
            ClientSize      = new Size(1000, 720);
            MinimumSize     = new Size(880, 660);
            DoubleBuffered  = true;
            KeyPreview      = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);

            // Per-core CPU counters — PerformanceCounter at 500ms is fine here.
            try
            {
                int threads = Environment.ProcessorCount;
                _coreCounters = new PerformanceCounter[threads];
                for (int i = 0; i < threads; i++)
                    _coreCounters[i] = new PerformanceCounter("Processor", "% Processor Time", i.ToString());
                foreach (var pc in _coreCounters) pc.NextValue();
                _coreValues = new float[threads];
            }
            catch { _coreCounters = null; _coreValues = null; }

            // Pick the fastest live network adapter (for ↓↑ throughput).
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
            {
                for (int i = 0; i < _coreCounters.Length; i++)
                {
                    try { _coreValues[i] = _coreCounters[i].NextValue(); }
                    catch { }
                }
            }

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

            const int PAD = 24;
            int W = ClientSize.Width;
            int leftW  = (int)((W - PAD * 3) * 0.6) + 4;
            int rightX = PAD + leftW + PAD;
            int rightW = W - rightX - PAD;

            // ── Header ────────────────────────────────────────────────────
            int y = PAD;
            using (var fT  = new Font("Segoe UI Semibold", 18f))
            using (var fSt = new Font("Segoe UI", 9.5f))
            {
                using var hi = new SolidBrush(TEXT_HI);
                using var md = new SolidBrush(TEXT_MD);
                g.DrawString("Live Performance", fT, hi, PAD, y);

                var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
                string upStr = up.TotalDays >= 1
                    ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m"
                    : $"{up.Hours}h {up.Minutes}m {up.Seconds}s";
                string sub = $"{_ext.MachineName}   ·   {_ext.OsName}   ·   Uptime {upStr}";
                g.DrawString(sub, fSt, md, PAD, y + 34);
            }
            y += 74;

            int yL = y, yR = y;

            // ── Left column: CPU + Memory ─────────────────────────────────
            yL = DrawCpuCard(g, PAD, yL, leftW);
            yL += 14;
            DrawMemCard(g, PAD, yL, leftW);

            // ── Right column: Storage, GPU, Network ───────────────────────
            yR = DrawStorageCard(g, rightX, yR, rightW);
            yR += 14;
            yR = DrawGpuCard(g, rightX, yR, rightW);
            yR += 14;
            DrawNetCard(g, rightX, yR, rightW);
        }

        private int DrawCpuCard(Graphics g, int x, int y, int w)
        {
            const int h = 340;
            DrawCardBg(g, x, y, w, h);

            using var fL   = new Font("Segoe UI Semibold", 9f);
            using var fBig = new Font("Segoe UI Semibold", 30f);
            using var fH   = new Font("Segoe UI Semibold", 11.5f);
            using var fS   = new Font("Segoe UI", 9.5f);
            using var fT   = new Font("Segoe UI Semibold", 8.5f);

            using (var br = new SolidBrush(A_CPU))
                g.DrawString("CPU", fL, br, x + 20, y + 16);
            using (var br = new LinearGradientBrush(
                new Rectangle(x + 20, y + 34, 28, 2), A_CPU, B_CPU, 0f))
                g.FillRectangle(br, x + 20, y + 34, 28, 2);

            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(_info.CpuName, fH, br, x + 20, y + 46);

            string specs = $"{_info.CpuGhz:0.00} GHz  ·  {_info.CpuCores}C / {_info.CpuThreads}T";
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
            DrawSparkline(g, x + 20, y + 100, w - 40, 96, _cpuHistory, _historyIdx, A_CPU);

            // Per-core bars
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString("PER-CORE", fT, br, x + 20, y + 208);
            if (_coreValues != null && _coreValues.Length > 0)
                DrawCoreBars(g, x + 20, y + 228, w - 40, 60, _coreValues, A_CPU, B_CPU);

            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString($"Processes  ·  {_procCount}", fS, br, x + 20, y + h - 28);

            return y + h;
        }

        private int DrawMemCard(Graphics g, int x, int y, int w)
        {
            const int h = 232;
            DrawCardBg(g, x, y, w, h);

            using var fL   = new Font("Segoe UI Semibold", 9f);
            using var fBig = new Font("Segoe UI Semibold", 30f);
            using var fH   = new Font("Segoe UI Semibold", 11.5f);
            using var fS   = new Font("Segoe UI", 9.5f);

            using (var br = new SolidBrush(A_MEM))
                g.DrawString("MEMORY", fL, br, x + 20, y + 16);
            using (var br = new LinearGradientBrush(
                new Rectangle(x + 20, y + 34, 28, 2), A_MEM, B_MEM, 0f))
                g.FillRectangle(br, x + 20, y + 34, 28, 2);

            string memHead = $"{_info.RamTotalGB:0.#} GB {_info.RamType}";
            if (_info.RamSpeedMhz > 0) memHead += $" @ {_info.RamSpeedMhz} MHz";
            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(memHead, fH, br, x + 20, y + 46);

            var (_, ram, _) = _bar.GetLive();
            double usedGB = _info.RamTotalGB * ram / 100.0;
            string memSub = _info.RamMfr.Length > 0
                ? $"{_info.RamMfr}  ·  {usedGB:0.#} / {_info.RamTotalGB:0.#} GB"
                : $"{usedGB:0.#} / {_info.RamTotalGB:0.#} GB";
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString(memSub, fS, br, x + 20, y + 68);

            string pctStr = $"{ram:0.0}%";
            var pctSz = g.MeasureString(pctStr, fBig);
            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(pctStr, fBig, br, x + w - pctSz.Width - 20, y + 12);

            DrawSparkline(g, x + 20, y + 100, w - 40, 116, _ramHistory, _historyIdx, A_MEM);

            return y + h;
        }

        private int DrawStorageCard(Graphics g, int x, int y, int w)
        {
            var drives = _bar.GetDrives();
            int rows = Math.Max(1, drives.Count);
            int h = 60 + rows * 50;
            DrawCardBg(g, x, y, w, h);

            using var fL = new Font("Segoe UI Semibold", 9f);
            using var fH = new Font("Segoe UI Semibold", 11f);
            using var fS = new Font("Segoe UI", 9f);

            using (var br = new SolidBrush(A_DSK))
                g.DrawString("STORAGE", fL, br, x + 20, y + 16);
            using (var br = new SolidBrush(A_DSK))
                g.FillRectangle(br, x + 20, y + 34, 28, 2);

            if (drives.Count == 0)
            {
                using var br = new SolidBrush(TEXT_LO);
                g.DrawString("No fixed drives detected", fS, br, x + 20, y + 48);
                return y + h;
            }

            int rowY = y + 50;
            foreach (var d in drives)
            {
                using (var br = new SolidBrush(TEXT_HI))
                    g.DrawString($"{d.letter}\\", fH, br, x + 20, rowY);
                string info = $"{d.used:0.#} / {d.total:0.#} GB";
                using (var br = new SolidBrush(TEXT_MD))
                    g.DrawString(info, fS, br, x + 68, rowY + 2);

                string pctStr = $"{d.pct}%";
                var pctSz = g.MeasureString(pctStr, fH);
                using (var br = new SolidBrush(TEXT_HI))
                    g.DrawString(pctStr, fH, br, x + w - pctSz.Width - 20, rowY);

                DrawProgressBar(g, x + 20, rowY + 24, w - 40, 6, d.pct, A_DSK, B_DSK);
                rowY += 50;
            }

            return y + h;
        }

        private int DrawGpuCard(Graphics g, int x, int y, int w)
        {
            const int h = 100;
            DrawCardBg(g, x, y, w, h);

            using var fL = new Font("Segoe UI Semibold", 9f);
            using var fH = new Font("Segoe UI Semibold", 11.5f);
            using var fS = new Font("Segoe UI", 9.5f);

            using (var br = new SolidBrush(A_GPU))
                g.DrawString("GPU", fL, br, x + 20, y + 16);
            using (var br = new SolidBrush(A_GPU))
                g.FillRectangle(br, x + 20, y + 34, 28, 2);

            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(_info.GpuName, fH, br, x + 20, y + 46);
            string sub = _info.GpuMemoryGB > 0 ? $"{_info.GpuMemoryGB:0.#} GB VRAM" : "Graphics adapter";
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString(sub, fS, br, x + 20, y + 68);

            return y + h;
        }

        private int DrawNetCard(Graphics g, int x, int y, int w)
        {
            const int h = 156;
            DrawCardBg(g, x, y, w, h);

            using var fL    = new Font("Segoe UI Semibold", 9f);
            using var fH    = new Font("Segoe UI Semibold", 11.5f);
            using var fS    = new Font("Segoe UI", 9.5f);
            using var fSpd  = new Font("Segoe UI Semibold", 13f);

            using (var br = new SolidBrush(A_NET))
                g.DrawString("NETWORK", fL, br, x + 20, y + 16);
            using (var br = new SolidBrush(A_NET))
                g.FillRectangle(br, x + 20, y + 34, 28, 2);

            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(_ext.NetAdapterName, fH, br, x + 20, y + 46);
            string sub = _ext.LocalIp == "—" ? "Ping to 8.8.8.8" : $"{_ext.LocalIp}  ·  Ping to 8.8.8.8";
            using (var br = new SolidBrush(TEXT_MD))
                g.DrawString(sub, fS, br, x + 20, y + 68);

            // Throughput
            string down = "↓ " + FmtBps(_bpsRecv);
            string up   = "↑ " + FmtBps(_bpsSent);
            using (var br = new SolidBrush(TEXT_HI))
            {
                g.DrawString(down, fSpd, br, x + 20, y + 96);
                g.DrawString(up,   fSpd, br, x + 20, y + 122);
            }

            // Ping right side
            var (_, _, ping) = _bar.GetLive();
            string p = ping < 0 ? "—" : $"{ping} ms";
            var pSz = g.MeasureString(p, fSpd);
            using (var br = new SolidBrush(TEXT_HI))
                g.DrawString(p, fSpd, br, x + w - pSz.Width - 20, y + 108);

            return y + h;
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
                foreach (var pc in _coreCounters)
                    try { pc.Dispose(); } catch { }
            base.OnFormClosed(e);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ExtendedStats — extras that only the full LiveDashboard shows.
    // ═══════════════════════════════════════════════════════════════════════════
    internal sealed record ExtendedStats(
        string OsName,
        string MachineName,
        string LocalIp,
        string NetAdapterName)
    {
        public static ExtendedStats Gather()
        {
            string os = Environment.OSVersion.Version switch
            {
                { Major: 10, Build: >= 22000 } => "Windows 11",
                { Major: 10 }                  => "Windows 10",
                _                              => $"Windows {Environment.OSVersion.Version}"
            };

            string machine = Environment.MachineName;
            string ip = "—", adapter = "—";

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
                    var addr = ni.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (addr != null) ip = addr.Address.ToString();
                }
            }
            catch { }

            return new ExtendedStats(os, machine, ip, adapter);
        }
    }
}
