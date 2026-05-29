using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LocTray
{
    /// <summary>
    /// Lange horizontale tekstbalk die over de taskbar zit, met alle live stats
    /// als één regel: CPU | RAM | drives | R/W | Ping. Versleepbaar, positie wordt onthouden.
    /// </summary>
    public sealed class TaskbarBar : Form
    {
        private float _cpu, _ram, _read, _write;
        private long _ping = -1;
        private List<(string letter, double used, double total, int pct)> _drives = new();

        private static readonly Color BG         = Color.FromArgb(28, 28, 34);
        private static readonly Color BORDER     = Color.FromArgb(70, 75, 88);
        private static readonly Color SEP        = Color.FromArgb(90, 95, 110);
        private static readonly Color TEXT       = Color.FromArgb(232, 232, 238);
        private static readonly Color TEXT_DIM   = Color.FromArgb(160, 162, 170);
        private static readonly Color CPU_COLOR  = Color.FromArgb(96, 180, 255);
        private static readonly Color RAM_COLOR  = Color.FromArgb(190, 140, 255);
        private static readonly Color DISK_COLOR = Color.FromArgb(120, 200, 100);
        private static readonly Color NET_COLOR  = Color.FromArgb(255, 180, 80);
        private static readonly Color HOT        = Color.FromArgb(255, 90, 90);

        private bool _dragging;
        private Point _dragStart;
        private bool _userPositioned;

        private record Segment(string Label, string Value, Color LabelColor);

        public TaskbarBar()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = BG;
            Opacity = 0.96;
            StartPosition = FormStartPosition.Manual;
            Width = 600;
            Height = 32;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Cursor = Cursors.SizeAll;

            MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _dragging = true;
                    _dragStart = e.Location;
                }
            };
            MouseMove += (_, e) =>
            {
                if (_dragging)
                {
                    _userPositioned = true;
                    Location = new Point(Left + e.X - _dragStart.X, Top + e.Y - _dragStart.Y);
                }
            };
            MouseUp += (_, e) =>
            {
                if (_dragging)
                {
                    _dragging = false;
                    SavePosition();
                }
            };

            LoadPosition();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        // Niet de focus stelen van het actieve venster
        protected override bool ShowWithoutActivation => true;

        public void UpdateStats(float cpu, float ram, float read, float write, long pingMs,
                                List<(string, double, double, int)> drives)
        {
            _cpu = cpu;
            _ram = ram;
            _read = read;
            _write = write;
            _ping = pingMs;
            _drives = drives;
            ResizeToContent();
            if (Visible) Invalidate();
        }

        private List<Segment> BuildSegments()
        {
            var list = new List<Segment>
            {
                new("CPU", $"{(int)Math.Round(_cpu)}%", _cpu >= 85 ? HOT : CPU_COLOR),
                new("RAM", $"{(int)Math.Round(_ram)}%", _ram >= 85 ? HOT : RAM_COLOR)
            };
            foreach (var d in _drives)
                list.Add(new(d.letter, $"{d.used:0}/{d.total:0}GB ({d.pct}%)", DISK_COLOR));
            list.Add(new("R/W", $"{_read / 1048576:0.0} / {_write / 1048576:0.0} MB/s", TEXT_DIM));
            list.Add(new("Ping", _ping < 0 ? "—" : $"{_ping} ms", NET_COLOR));
            return list;
        }

        private void ResizeToContent()
        {
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            using var fLabel = new Font("Segoe UI", 9);
            using var fBold  = new Font("Segoe UI Semibold", 9.5f);

            int width = 14;
            var segments = BuildSegments();
            for (int i = 0; i < segments.Count; i++)
            {
                width += (int)Math.Ceiling(g.MeasureString(segments[i].Label, fLabel).Width) + 5;
                width += (int)Math.Ceiling(g.MeasureString(segments[i].Value, fBold).Width) + 8;
                if (i < segments.Count - 1) width += 13;
            }
            width += 14;

            if (Width != width) Width = width;
            if (Height != 32) Height = 32;
            if (!_userPositioned) DockOverTaskbarRight();
        }

        private void DockOverTaskbarRight()
        {
            var screen = Screen.PrimaryScreen;
            if (screen == null) return;
            var bounds = screen.Bounds;
            var work = screen.WorkingArea;
            const int trayEstimate = 320;

            if (work.Bottom < bounds.Bottom)            // taskbar onderaan
            {
                Height = bounds.Bottom - work.Bottom;
                Top = work.Bottom;
                Left = bounds.Right - trayEstimate - Width;
            }
            else if (work.Top > bounds.Top)             // taskbar bovenaan
            {
                Height = work.Top - bounds.Top;
                Top = bounds.Top;
                Left = bounds.Right - trayEstimate - Width;
            }
            else                                         // autohide / geen taskbar
            {
                Top = bounds.Bottom - Height;
                Left = bounds.Right - trayEstimate - Width;
            }
        }

        private void LoadPosition()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\LocTray");
                if (k?.GetValue("BarX") is int x && k.GetValue("BarY") is int y)
                {
                    var loc = new Point(x, y);
                    var vs = SystemInformation.VirtualScreen;
                    if (loc.X >= vs.Left - 100 && loc.X < vs.Right - 100 &&
                        loc.Y >= vs.Top  - 50  && loc.Y < vs.Bottom)
                    {
                        Location = loc;
                        _userPositioned = true;
                    }
                }
            }
            catch { }
        }

        private void SavePosition()
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(@"Software\LocTray");
                k.SetValue("BarX", Location.X, RegistryValueKind.DWord);
                k.SetValue("BarY", Location.Y, RegistryValueKind.DWord);
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using var border = new Pen(BORDER, 1);
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            using var fLabel = new Font("Segoe UI", 9);
            using var fBold  = new Font("Segoe UI Semibold", 9.5f);
            using var bMain  = new SolidBrush(TEXT);
            using var sepPen = new Pen(SEP, 1);

            var segments = BuildSegments();
            int x = 14;
            int yLabel = (Height - 14) / 2;

            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                using var labelBr = new SolidBrush(s.LabelColor);

                g.DrawString(s.Label, fLabel, labelBr, x, yLabel);
                x += (int)Math.Ceiling(g.MeasureString(s.Label, fLabel).Width) + 5;

                g.DrawString(s.Value, fBold, bMain, x, yLabel - 1);
                x += (int)Math.Ceiling(g.MeasureString(s.Value, fBold).Width) + 8;

                if (i < segments.Count - 1)
                {
                    g.DrawLine(sepPen, x, 7, x, Height - 7);
                    x += 13;
                }
            }
        }
    }

    /// <summary>
    /// Vergrote, schonere welkomstpopup met meer info.
    /// </summary>
    public sealed class WelcomePopup : Form
    {
        public WelcomePopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.FromArgb(28, 30, 36);
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(540, 400);
            TopMost = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            var btn = new Button
            {
                Text = "Begrepen",
                BackColor = Color.FromArgb(80, 170, 255),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 10),
                Size = new Size(160, 40),
                Location = new Point((Width - 160) / 2, Height - 70),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.Click += (_, _) => Close();
            Controls.Add(btn);
            AcceptButton = btn;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using var border = new Pen(Color.FromArgb(60, 64, 76), 1);
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            // Bovenste accent-strip
            using (var accent = new SolidBrush(Color.FromArgb(80, 170, 255)))
                g.FillRectangle(accent, 0, 0, Width, 4);

            using var titleFont = new Font("Segoe UI Semibold", 20);
            using var subFont   = new Font("Segoe UI", 11);
            using var bodyFont  = new Font("Segoe UI Semibold", 10);
            using var listFont  = new Font("Segoe UI", 10);
            using var dimFont   = new Font("Segoe UI", 9.5f, FontStyle.Italic);

            using var titleBr  = new SolidBrush(Color.White);
            using var subBr    = new SolidBrush(Color.FromArgb(150, 200, 255));
            using var bodyBr   = new SolidBrush(Color.FromArgb(232, 232, 238));
            using var listBr   = new SolidBrush(Color.FromArgb(210, 212, 220));
            using var dimBr    = new SolidBrush(Color.FromArgb(150, 152, 160));
            using var bulletBr = new SolidBrush(Color.FromArgb(80, 170, 255));

            int x = 36;
            int y = 38;

            g.DrawString("LocTray", titleFont, titleBr, x, y);
            y += 42;
            g.DrawString("Correctly installed!", subFont, subBr, x, y);
            y += 36;
            g.DrawString("Wat je nu hebt", bodyFont, bodyBr, x, y);
            y += 30;

            string[] bullets =
            {
                "Live stats-balk boven je taskbar",
                "CPU, RAM, schijven, R/W én ping",
                "Updates elke seconde",
                "Versleep met linkermuis naar gewenste plek",
                "Rechtsklik op de balk voor opties",
                "Auto-start staat al aan voor volgende keer"
            };
            foreach (var b in bullets)
            {
                g.FillEllipse(bulletBr, x + 2, y + 7, 6, 6);
                g.DrawString(b, listFont, listBr, x + 18, y);
                y += 26;
            }

            y += 6;
            g.DrawString("Tip: kies 'Afsluiten' in het rechtsklik-menu om te stoppen.",
                         dimFont, dimBr, x, y);
        }
    }
}
