using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GpuVramWidget
{
    // The floating VRAM widget in the dot-matrix style (see DotUi):
    //   01 RTX 4080 ........................ [16 GB]
    //   ● status line
    //   (ring: used GB)   FREE | SERVER / CTX | MAX CTX / VRAM history sparkline
    //   PROCESSES                         +38 · 495 MB
    //   name ········· value  (one dot-bar row per GPU process, shared scale)
    //   [CLEAN] [▶ LAUNCH profile ▾] [•••]
    // The whole window is one card: glass mode draws only the foreground (the shader is the card),
    // classic mode paints the card into a layered window.
    internal sealed class WidgetForm : Form
    {
        private const int Pad = 22, Edge = 6, RowH = 22;
        private const int HeadY = 18, StatusY = 44, HeroY = 68, RingSize = 124;
        private const int ProcY = 206, RowsY = 226, ActionH = 30, ActionBottom = 18;

        private VramSample _sample;
        private long _sampleVersion;
        private int _procScroll;   // first visible process row (mouse wheel)
        private bool _expanded;
        private int _heightBeforeExpand;
        private bool _programmaticResize, _userResizedWhileExpanded;
        private bool _dragging, _moved;
        private Point _dragOffset;
        private string _downKey, _hover;
        private string _signature;
        private int _foregroundPaints;

        private Bitmap _surface, _ssSurface;
        private readonly List<KeyValuePair<Rectangle, string>> _hits = new List<KeyValuePair<Rectangle, string>>();
        private readonly Dictionary<int, ProcRow> _rowByPid = new Dictionary<int, ProcRow>();
        private readonly Dictionary<int, string> _appNames = new Dictionary<int, string>();
        private VramSample _appNameSample;

        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _glassTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _renderThrottle = new System.Windows.Forms.Timer { Interval = 33 };
        private GlassRenderer _glass;
        private DateTime _glassRetryAt;
        private int _glassFrames;
        private bool _closing;
        private string _glassStatus = "Glass: starting";

        private LauncherController _launcher;
        private LaunchPanel _panel;
        private int _collecting;
        private long _requestSeq;

        public WidgetForm()
        {
            Text = "GPU VRAM";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            MinimumSize = new Size(360, 330);
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Options.Width - 24, wa.Top + 24);
            ClientSize = new Size(Options.Width, Options.Height);
            _timer.Interval = Options.IntervalMs;
            _timer.Tick += delegate { RefreshAsync(); };
            _renderThrottle.Tick += delegate { _renderThrottle.Stop(); UpdateSurface(); };
            _glassTimer.Tick += RenderGlass;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;   // WS_EX_TOOLWINDOW: not in Alt+Tab
                cp.ExStyle |= Options.Glass ? 0x00200000 : 0x80000; // DirectComposition NOREDIRECTIONBITMAP, or classic layered bitmap
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { if (!Options.Glass) base.OnPaintBackground(e); }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Options.Glass) StartGlass();
            else if (Options.Diagnostic) { _glassTimer.Interval = 16; _glassTimer.Start(); }
            _launcher = new LauncherController(this, delegate { return _sample; });
            _launcher.Changed += delegate { if (!_closing) UpdateSurface(); if (_panel != null && !_panel.IsDisposed) _panel.Invalidate(); };
            _launcher.VramChanged += delegate { RefreshAsync(); };
            RefreshAsync();
            _timer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closing = true;
            _timer.Stop(); _glassTimer.Stop(); _renderThrottle.Stop();
            _timer.Dispose(); _glassTimer.Dispose(); _renderThrottle.Dispose();
            if (_glass != null) { _glass.Dispose(); _glass = null; }
            if (_launcher != null) _launcher.Dispose();
            if (_surface != null) _surface.Dispose();
            if (_ssSurface != null) _ssSurface.Dispose();
            base.OnFormClosed(e);
        }

        // ================= glass renderer lifecycle =================
        private void StartGlass()
        {
            try
            {
                if (_glass != null) { _glass.Dispose(); _glass = null; }
                _glass = new GlassRenderer(Handle);
                _signature = null;
                UpdateSurface();
                _glassTimer.Interval = _glass.Interval;
                _glassTimer.Start();
                _glassStatus = "Glass: waiting for desktop";
            }
            catch (Exception ex)
            {
                LogGlass(ex.Message);
                _glassStatus = "Glass failed to start (retrying)";
                _glassRetryAt = DateTime.Now.AddSeconds(2);
                _glassTimer.Start();
            }
        }

        private void RenderGlass(object sender, EventArgs e)
        {
            if (Options.Diagnostic && !Options.Glass)
            {
                _glassFrames++;
                if (_glassFrames == 60 && _surface != null)
                    _surface.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "classic-preview.png"));
                if (_glassFrames >= 120) Close();
                return;
            }
            if (_closing || DateTime.Now < _glassRetryAt) return;
            if (_glass == null) { StartGlass(); return; }
            try
            {
                _glass.Submit(_dragging && _moved, false);
                int result = _glass.Render();
                if (result == 2) { StartGlass(); return; } // Moved to another adapter/output.
                _glassTimer.Interval = _glass.Interval;
                _glassStatus = result == 0 ? "Glass: live desktop refraction" : "Glass: waiting for desktop";
                _glassFrames++;
                if (result == 1 && _glassFrames % 120 == 0) LogGlass(_glass.Status);
                if (Options.Diagnostic && (_glassFrames == 120 || _glassFrames == 240))
                {
                    string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "glass-preview-" + _glassFrames + ".bmp");
                    _glass.Save(path, false);
                    LogGlass("Frame " + _glassFrames + ", capture status " + result + ", saved " + path);
                    LogGlass(_glass.Counters());
                    LogGlass("foregroundPaints=" + _foregroundPaints + ", pollIntervalMs=" + _glassTimer.Interval);
                    if (_glassFrames == 240) _glass.Compare(AppDomain.CurrentDomain.BaseDirectory);
                }
                if (Options.Diagnostic && _glassFrames == 160)
                {
                    // Exercise the actual WinForms resize/upload path.
                    Location = new Point(Left - 140, Top + 40);
                    ClientSize = new Size(520, 420);
                    UpdateSurface();
                }
                if (Options.Diagnostic && _glassFrames >= 300) Close();
            }
            catch (Exception ex)
            {
                LogGlass(ex.Message);
                _glass.Dispose(); _glass = null;
                _glassRetryAt = DateTime.Now.AddSeconds(2);
                _glassStatus = "Desktop capture lost, retrying";
            }
        }

        private static void LogGlass(string message)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "glass.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine); }
            catch { }
        }

        private void WakeGlass()
        {
            if (Options.Glass && _glass != null) _glass.Submit(_dragging && _moved, true);
        }

        // ================= data =================
        private void RefreshAsync()
        {
            // In-flight guard: skip this tick while the previous collection runs.
            if (Interlocked.CompareExchange(ref _collecting, 1, 0) != 0) return;
            long seq = Interlocked.Increment(ref _requestSeq);
            Task.Factory.StartNew(delegate
            {
                VramSample s = null;
                try { s = GpuStats.Collect(); } catch { }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        Interlocked.Exchange(ref _collecting, 0);
                        if (_closing || IsDisposed) return;
                        if (s == null || seq != Interlocked.Read(ref _requestSeq)) return; // stale sample
                        _sample = s; _sampleVersion++;
                        UpdateSurface();
                    });
                }
                catch { Interlocked.Exchange(ref _collecting, 0); }
            });
        }

        // Helper processes are attributed to their app (steamwebhelper -> steam); cached per sample.
        private string AppNameOf(ProcRow row)
        {
            if (!ReferenceEquals(_appNameSample, _sample))
            {
                _appNames.Clear(); _appNameSample = _sample;
                if (_sample != null)
                {
                    var tree = ProcessTree.Snapshot();
                    foreach (var r in _sample.Rows) _appNames[r.Pid] = tree.AppNameFor(r.Pid) ?? r.Name;
                }
            }
            string app;
            return _appNames.TryGetValue(row.Pid, out app) ? app : row.Name;
        }

        private bool IsCleanupRow(ProcRow row) { return AppSettings.IsCleanup(row.Name) || AppSettings.IsCleanup(AppNameOf(row)); }

        private static bool IsServer(ProcRow row) { return string.Equals(row.Name, "llama-server", StringComparison.OrdinalIgnoreCase); }

        // Pinned (--always) processes and llama-server first, then by usage.
        private List<ProcRow> OrderedRows()
        {
            var ordered = new List<ProcRow>();
            if (_sample == null) return ordered;
            var pinned = new HashSet<string>(Options.Always, StringComparer.OrdinalIgnoreCase);
            ordered.AddRange(_sample.Rows);
            ordered.Sort(delegate (ProcRow a, ProcRow b)
            {
                bool pa = pinned.Contains(a.Name) || IsServer(a), pb = pinned.Contains(b.Name) || IsServer(b);
                return pa != pb ? (pa ? -1 : 1) : b.MB.CompareTo(a.MB);
            });
            return ordered;
        }

        private int RowCapacity(int height)
        {
            return Math.Max(1, (height - ActionBottom - ActionH - 12 - RowsY) / RowH);
        }

        // ================= rendering =================
        private void ThrottledRender() { if (!_renderThrottle.Enabled) _renderThrottle.Start(); }

        private string Signature(int w, int h)
        {
            var status = _launcher != null ? _launcher.Status() : null;
            var profile = _launcher != null ? _launcher.Current : null;
            var fit = _launcher != null && profile != null ? _launcher.Fit(profile) : null;
            return string.Join("|", new[] {
                w.ToString(), h.ToString(), AppSettings.Material.ToString(), Options.Glass.ToString(), _expanded.ToString(),
                _hover ?? "", _sampleVersion.ToString(), _procScroll.ToString(), status != null ? status.Text + status.Color.ToArgb() : "",
                profile != null ? profile.Path : "", fit != null ? fit.Valid.ToString() : "-",
                _launcher != null ? _launcher.Server.State.ToString() + _launcher.Server.Ctx + _launcher.Busy : "",
                string.Join(",", AppSettings.Cleanup), GpuStats.AdapterName ?? "" });
        }

        private void UpdateSurface()
        {
            WakeGlass();
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w < 1 || h < 1) return;
            string key = Signature(w, h);
            if (key == _signature) return;
            _signature = key;
            _foregroundPaints++;

            int ss = Math.Max(1, Options.Supersample);
            var surface = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                if (ss > 1)
                {
                    if (_ssSurface == null || _ssSurface.Width != w * ss || _ssSurface.Height != h * ss)
                    {
                        if (_ssSurface != null) _ssSurface.Dispose();
                        _ssSurface = new Bitmap(w * ss, h * ss, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    }
                    using (var gt = Graphics.FromImage(_ssSurface)) { gt.ScaleTransform(ss, ss); Draw(gt, w, h); }
                    using (var gs = Graphics.FromImage(surface))
                    {
                        gs.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        gs.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        gs.DrawImage(_ssSurface, new Rectangle(0, 0, w, h));
                    }
                }
                else using (var g = Graphics.FromImage(surface)) Draw(g, w, h);
            }
            catch { surface.Dispose(); throw; }

            if (_surface != null) _surface.Dispose();
            _surface = surface;
            if (Options.Glass) { if (_glass != null) _glass.Upload(_surface); return; }
            PushLayered(_surface, w, h);
        }

        private void PushLayered(Bitmap bitmap, int w, int h)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr oldObj = Native.SelectObject(memDc, hBmp);
            try
            {
                var size = new SIZE { CX = w, CY = h };
                var src = new POINT { X = 0, Y = 0 };
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                Native.UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, 2); // ULW_ALPHA
            }
            finally
            {
                Native.SelectObject(memDc, oldObj);
                Native.DeleteObject(hBmp);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void Hit(RectangleF r, string key) { _hits.Add(new KeyValuePair<Rectangle, string>(Rectangle.Round(r), key)); }

        private void Draw(Graphics g, int w, int h)
        {
            g.Clear(Color.Transparent);
            DotUi.Prepare(g);
            _hits.Clear(); _rowByPid.Clear();
            var p = DotUi.P;

            // Classic mode paints the card; glass mode leaves it to the shader (same 7 px inset as its shape).
            if (!Options.Glass)
            {
                var card = new RectangleF(7.5f, 7.5f, w - 15, h - 15);
                DotUi.Card(g, card, 255, (float)Options.CornerRadius);
                if (p.IsDark)
                    using (var clip = DotUi.Rounded(card, (float)Options.CornerRadius))
                    using (var glow = new GraphicsPath())
                    {
                        glow.AddEllipse(card.X - w * 0.45f, card.Y - h * 0.55f, w * 1.1f, h * 1.0f);
                        using (var brush = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(70, p.Ember), SurroundColors = new[] { Color.FromArgb(0, p.Ember) } })
                        {
                            g.SetClip(clip); g.FillPath(brush, glow); g.ResetClip();
                        }
                    }
            }

            double used = _sample != null ? _sample.UsedMB / 1024 : 0, total = _sample != null ? _sample.TotalMB / 1024 : 16;
            string adapter = string.IsNullOrEmpty(GpuStats.AdapterName) ? "GPU" : GpuStats.AdapterName;

            // 01 ADAPTER ............ [16 GB]
            DotUi.CardHead(g, Pad, HeadY, w - Pad * 2, "01", adapter, Math.Round(total) + " GB VRAM");

            // ● status line
            var status = _launcher != null ? _launcher.Status() : new StatusInfo { Text = "Reading sensors…", Color = p.Muted };
            var statusRect = new RectangleF(Pad - 4, StatusY - 3, w - Pad * 2 + 8, 22);
            if (status.Clickable && _hover == "status")
                using (var path = DotUi.Rounded(statusRect, 8)) using (var b = new SolidBrush(Color.FromArgb(p.IsDark ? 40 : 120, p.PillBg))) g.FillPath(b, path);
            DotUi.StatusDot(g, Pad + 4, StatusY + 8, status.Color);
            DotUi.Text(g, status.Text, DotUi.Mono(12, true), p.Ink, new RectangleF(Pad + 16, StatusY, w - Pad * 2 - 16, 16));
            if (status.Clickable) Hit(statusRect, "status");

            DrawHero(g, w, used, total);
            DrawProcesses(g, w, h);
            DrawActions(g, w, h);
        }

        // Ring = used VRAM. Warning tick = where the selected profile stops fitting; critical tick near full.
        private void DrawHero(Graphics g, int w, double used, double total)
        {
            var p = DotUi.P;
            var ring = new RectangleF(Pad - 6, HeroY, RingSize, RingSize);
            double warn = double.NaN;
            var profile = _launcher != null ? _launcher.Current : null;
            var limits = _launcher != null && profile != null ? _launcher.Limits(profile) : null;
            var budget = VramBudget.Measure(_sample);
            int baseline = profile != null ? LauncherController.HealthyCtx(profile, limits) : 0;
            // Amber tick: used VRAM beyond which the script's own ctx would no longer fit.
            if (limits != null && limits.Valid && !_launcher.ServerActive && baseline > 0)
                warn = (budget.TotalMB - AppSettings.HeadroomMiB - limits.NeedMiB(baseline)) / 1024;
            if (!double.IsNaN(warn) && warn < 0) warn = double.NaN;
            double crit = total * 0.97;
            DotUi.Ring(g, ring, used, 0, total, warn, crit, 3.6f, 48);

            string value = _sample == null ? "--" : used < 99.95 ? used.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(used).ToString(CultureInfo.InvariantCulture); // <= 4 characters
            var size = DotUi.MeasureDotText(value, 2.6f, 1f);
            float cx = ring.X + ring.Width / 2, cy = ring.Y + ring.Height / 2;
            DotUi.DotText(g, value, cx - size.Width / 2, cy - size.Height / 2 - 8, 2.6f, 1f, p.ForValue(used, warn, crit, p.Ink));
            DotUi.Text(g, "GB", DotUi.Mono(11), p.Muted, new RectangleF(cx - 30, cy + 9, 60, 14), StringAlignment.Center);
            DotUi.Text(g, "USED", DotUi.Mono(9.5f), p.Dim, new RectangleF(cx - 30, cy + 23, 60, 13), StringAlignment.Center);

            // Stat grid: caption over value.
            float x0 = ring.Right + 12, colW = (w - Pad - x0) / 2;
            var server = _launcher != null ? _launcher.Server : null;
            string serverText = server == null ? "--" : server.State == ServerState.Ready ? "READY" : server.State == ServerState.Loading ? "LOADING"
                : server.State == ServerState.Starting ? "STARTING" : "OFF";
            Color serverColor = server == null ? p.Muted : server.State == ServerState.Ready ? p.Accent
                : server.State == ServerState.Loading || server.State == ServerState.Starting ? p.Warn : p.Muted;
            bool live = server != null && server.State == ServerState.Ready && server.Ctx > 0;
            int ctx = live ? server.Ctx : baseline;
            int limit = limits != null && limits.Valid ? limits.HealthyAfter : 0;

            // Two stat rows, vertically centred on the ring.
            float row1 = HeroY + RingSize / 2f - 41, row2 = row1 + 46;
            Stat(g, x0, row1, colW, "Free", _sample == null ? "--" : ((budget.TotalMB - budget.UsedMB) / 1024).ToString("0.0", CultureInfo.InvariantCulture), "GB", p.Ink);
            Stat(g, x0 + colW, row1, colW, "Server", serverText, server != null && server.Port > 0 && server.State == ServerState.Ready ? ":" + server.Port : "", serverColor);
            // Left: the healthy ctx is the script's own (or the running server's). Right: the measured/estimated
            // limit for reference, red when the baseline goes past it.
            Stat(g, x0, row2, colW, live ? "Ctx" : "Healthy ctx", ctx > 0 ? LauncherController.Short(ctx) : "--", "", p.Ink);
            bool spills = limits != null && limits.Valid && !live && limits.RawNeedMiB(ctx) > budget.FreeAfter + AppSettings.HeadroomMiB;
            Color limitColor = spills ? p.Crit : limit > 0 && ctx > limit ? p.Warn : p.Ink;
            Stat(g, x0 + colW, row2, colW, limits != null && !limits.Calibrated ? "Limit (est.)" : "Limit",
                limits == null ? (profile == null ? "--" : "…") : limit > 0 ? "~" + LauncherController.Short(limit) : "--", "", limitColor);

        }

        private static void Stat(Graphics g, float x, float y, float width, string caption, string value, string unit, Color color)
        {
            DotUi.Caption(g, caption, new RectangleF(x, y, width - 6, 14));
            DotUi.ValueWithUnit(g, value, unit, x, y + 15, 20, false, color, 15);
        }

        private void DrawProcesses(Graphics g, int w, int h)
        {
            var p = DotUi.P;
            // The list is a window of `capacity` rows (the window height decides; expanding grows the window)
            // that the mouse wheel moves through.
            int capacity = RowCapacity(h);
            var all = OrderedRows();
            _procScroll = Math.Max(0, Math.Min(_procScroll, all.Count - capacity));
            var rows = all.GetRange(_procScroll, Math.Min(capacity, all.Count - _procScroll));
            double hiddenMB = 0;
            for (int i = capacity; i < all.Count; i++) hiddenMB += all[i].MB;
            var caption = new RectangleF(Pad, ProcY, w - Pad * 2, 16);
            DotUi.Caption(g, "Processes", caption);
            string arrow = _expanded ? "  ▴" : "  ▾";
            string right = _sample == null ? ""
                : all.Count <= capacity ? (_expanded ? "collapse" + arrow : all.Count + " total")
                : _procScroll == 0 && !_expanded ? "+" + (all.Count - capacity) + " · " + CleanupResult.FormatMB(hiddenMB) + " · scroll" + arrow
                : (_procScroll + 1) + "-" + (_procScroll + rows.Count) + " of " + all.Count + arrow;
            DotUi.Caption(g, right, caption, StringAlignment.Far, _hover == "processes" ? p.Ink : p.Muted);
            Hit(caption, "processes");

            if (_sample == null) { DotUi.Text(g, "Reading…", DotUi.Mono(12), p.Muted, new RectangleF(Pad, RowsY, w - Pad * 2, RowH)); return; }
            if (rows.Count == 0) { DotUi.Text(g, "No GPU processes", DotUi.Mono(12), p.Muted, new RectangleF(Pad, RowsY, w - Pad * 2, RowH)); return; }

            double peak = 0;
            foreach (var r in all) peak = Math.Max(peak, r.MB); // one scale for every row, so scrolling keeps bars comparable
            double max = DotUi.NiceMax(peak);
            float nameW = Math.Min(140, (w - Pad * 2) * 0.36f), valueW = 70;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                float y = RowsY + i * RowH;
                var rect = new RectangleF(Pad - 6, y, w - Pad * 2 + 12, RowH);
                if (_hover == "row:" + row.Pid)
                    using (var path = DotUi.Rounded(rect, 8)) using (var b = new SolidBrush(Color.FromArgb(p.IsDark ? 50 : 140, p.PillBg))) g.FillPath(b, path);
                bool server = IsServer(row), cleanup = IsCleanupRow(row);
                if (cleanup) using (var b = new SolidBrush(p.Accent)) g.FillEllipse(b, Pad - 1, y + RowH / 2f - 2.5f, 5, 5);
                DotUi.Text(g, row.Name, DotUi.Mono(12, server), row.MB < 1 ? p.Muted : p.Ink, new RectangleF(Pad + 8, y, nameW - 8, RowH));
                DotUi.Bar(g, new RectangleF(Pad + nameW + 6, y, w - Pad * 2 - nameW - valueW - 6, RowH), row.MB, 0, max,
                    double.NaN, double.NaN, double.NaN, 6, 3, server ? p.Accent : p.Ink);
                string value = row.MB >= 1024 ? (row.MB / 1024).ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(row.MB).ToString(CultureInfo.InvariantCulture);
                DotUi.ValueWithUnit(g, value, row.MB >= 1024 ? "GB" : "MB", w - Pad, y + 1, RowH, true, p.Ink, 13);
                _rowByPid[row.Pid] = row;
                Hit(rect, "row:" + row.Pid);
            }
        }

        // [CLEAN] [▶ LAUNCH profile ▾] [•••]
        private void DrawActions(Graphics g, int w, int h)
        {
            var p = DotUi.P;
            float y = h - ActionBottom - ActionH, cleanW = 92, moreW = 44, gap = 8;
            var clean = new RectangleF(Pad, y, cleanW, ActionH);
            var more = new RectangleF(w - Pad - moreW, y, moreW, ActionH);
            var launch = new RectangleF(clean.Right + gap, y, more.Left - gap - clean.Right - gap, ActionH);
            bool busy = _launcher != null && _launcher.Busy, running = _launcher != null && _launcher.ServerActive;

            DotUi.Pill(g, clean, "", false, _hover == "clean", false, !busy);
            DrawBolt(g, clean.X + 14, y + 9, busy ? p.Dim : p.Accent);
            DotUi.Text(g, "CLEAN", DotUi.Mono(12, true), busy ? p.Dim : p.Ink, new RectangleF(clean.X + 30, y, cleanW - 34, ActionH));
            Hit(clean, "clean");

            bool hotLaunch = _hover == "launch" || _hover == "profiles";
            DotUi.Pill(g, launch, "", !running, hotLaunch, false, !busy);
            Color ink = running ? p.Ink : p.Accent;
            if (running) using (var b = new SolidBrush(p.Crit)) g.FillRectangle(b, launch.X + 14, y + 10, 10, 10);
            else using (var b = new SolidBrush(ink)) g.FillPolygon(b, new[] { new PointF(launch.X + 14, y + 9), new PointF(launch.X + 14, y + 21), new PointF(launch.X + 24, y + 15) });
            var profile = _launcher != null ? _launcher.Current : null;
            string label = running ? "STOP SERVER" : profile == null ? "ADD PROFILE" : "LAUNCH " + profile.Name;
            float chevron = running ? 0 : 30;
            DotUi.Text(g, label, DotUi.Mono(12, true), busy ? p.Dim : ink, new RectangleF(launch.X + 32, y, launch.Width - 36 - chevron, ActionH));
            if (!running)
            {
                float sx = launch.Right - chevron;
                using (var b = new SolidBrush(Color.FromArgb(120, p.Accent)))
                    for (float dy = 8; dy <= ActionH - 8; dy += 4) g.FillEllipse(b, sx - 0.8f, y + dy - 0.8f, 1.6f, 1.6f);
                float cx = sx + chevron / 2, cy = y + ActionH / 2;
                using (var pen = new Pen(ink, 1.5f)) g.DrawLines(pen, new[] { new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2) });
                Hit(new RectangleF(sx, y, chevron, ActionH), "profiles");
                Hit(new RectangleF(launch.X, y, launch.Width - chevron, ActionH), "launch");
            }
            else Hit(launch, "launch");

            DotUi.Pill(g, more, "", false, _hover == "more");
            using (var b = new SolidBrush(p.Ink))
                for (int i = -1; i <= 1; i++) g.FillEllipse(b, more.X + moreW / 2 - 2 + i * 7, y + ActionH / 2 - 2, 4, 4);
            Hit(more, "more");
        }

        private static void DrawBolt(Graphics g, float x, float y, Color color)
        {
            using (var b = new SolidBrush(color))
                g.FillPolygon(b, new[] {
                    new PointF(x + 6.5f, y), new PointF(x + 0.5f, y + 7), new PointF(x + 4.5f, y + 7),
                    new PointF(x + 3.5f, y + 12), new PointF(x + 9.5f, y + 5), new PointF(x + 5.5f, y + 5), new PointF(x + 6.5f, y) });
        }

        // ================= interaction =================
        private string HitKey(Point point)
        {
            for (int i = _hits.Count - 1; i >= 0; i--) if (_hits[i].Key.Contains(point)) return _hits[i].Value;
            return null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_glass != null) _glass.PrepareDrag();
            _dragging = true; _moved = false;
            _dragOffset = e.Location;
            _downKey = HitKey(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                // Queued mouse events carry client coordinates from an older window position; use the live pointer.
                Point mouse = Cursor.Position, target = new Point(mouse.X - _dragOffset.X, mouse.Y - _dragOffset.Y);
                if (Math.Abs(target.X - Left) > 3 || Math.Abs(target.Y - Top) > 3)
                {
                    _moved = true;
                    if (_glass != null) _glass.PrepareDrag();
                    Location = target;
                }
            }
            else
            {
                string key = HitKey(e.Location);
                Cursor = key != null ? Cursors.Hand : Cursors.Default;
                if (key != _hover) { _hover = key; UpdateSurface(); }
            }
            WakeGlass();
        }

        // Wheel over the process list scrolls it (collapsed or expanded).
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Y < ProcY || e.Y > ClientSize.Height - ActionBottom - ActionH) return;
            int steps = Math.Max(1, Math.Abs(e.Delta) / 120) * (e.Delta > 0 ? -1 : 1);
            int previous = _procScroll;
            _procScroll = Math.Max(0, _procScroll + steps * 2);
            if (_procScroll != previous) UpdateSurface(); // DrawProcesses clamps the upper bound
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != null && !_dragging) { _hover = null; UpdateSurface(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            string key = HitKey(e.Location);
            if (e.Button == MouseButtons.Right)
            {
                ProcRow row = RowFor(key);
                if (row != null) ShowProcessMenu(row, e.Location); else ShowMainMenu(e.Location, false);
            }
            else if (e.Button == MouseButtons.Left && _dragging && !_moved && key != null && key == _downKey)
                Activate(key, e.Location);
            _dragging = false;
            WakeGlass();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) { _dragging = false; WakeGlass(); }
        }

        private ProcRow RowFor(string key)
        {
            int pid; ProcRow row;
            return key != null && key.StartsWith("row:") && int.TryParse(key.Substring(4), out pid) && _rowByPid.TryGetValue(pid, out row) ? row : null;
        }

        private void Activate(string key, Point at)
        {
            if (_launcher == null) return;
            ProcRow row = RowFor(key);
            if (row != null) { ShowProcessMenu(row, at); return; }
            switch (key)
            {
                case "clean":
                    if (_launcher.Busy) break;
                    if (AppSettings.Cleanup.Count == 0) ShowPanel(); else _launcher.Clean(null);
                    break;
                case "launch":
                    if (_launcher.Busy) break;
                    if (_launcher.ServerActive)
                    {
                        if (DotDialog.Confirm(this, "Stop llama-server", "The loaded model will be unloaded.", "Stop")) _launcher.Server.Stop();
                    }
                    else if (_launcher.Current == null) ShowPanel();
                    else _launcher.CleanAndLaunch(_launcher.Current);
                    break;
                case "profiles": ShowProfileMenu(); break;
                case "more": ShowMainMenu(Point.Empty, true); break;
                case "status": _launcher.StatusClicked(); break;
                case "processes": ToggleExpanded(); break;
            }
        }

        private void ToggleExpanded()
        {
            _expanded = !_expanded;
            _procScroll = 0;
            _programmaticResize = true;
            if (_expanded)
            {
                _heightBeforeExpand = ClientSize.Height;
                _userResizedWhileExpanded = false;
                int rows = _sample != null ? _sample.Rows.Count : 0;
                int need = RowsY + rows * RowH + 12 + ActionH + ActionBottom;
                int limit = Screen.FromControl(this).WorkingArea.Bottom - Top - 24;
                ClientSize = new Size(ClientSize.Width, Math.Max(_heightBeforeExpand, Math.Min(need, limit)));
            }
            else if (!_userResizedWhileExpanded && _heightBeforeExpand > 0)
                ClientSize = new Size(ClientSize.Width, _heightBeforeExpand);
            _programmaticResize = false;
            UpdateSurface();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            WakeGlass();
            if (_expanded && !_programmaticResize) _userResizedWhileExpanded = true;
            ThrottledRender();
        }

        // Edge resizing for the borderless window.
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, WM_EXITSIZEMOVE = 0x232;
            if (m.Msg == WM_EXITSIZEMOVE) { UpdateSurface(); return; }
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                long lp = m.LParam.ToInt64();
                var pos = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                bool l = pos.X < Edge, r = pos.X > ClientSize.Width - Edge, t = pos.Y < Edge, b = pos.Y > ClientSize.Height - Edge;
                if (b && r) m.Result = (IntPtr)17; else if (b && l) m.Result = (IntPtr)16;
                else if (t && r) m.Result = (IntPtr)14; else if (t && l) m.Result = (IntPtr)13;
                else if (r) m.Result = (IntPtr)11; else if (b) m.Result = (IntPtr)15;
                else if (l) m.Result = (IntPtr)10; else if (t) m.Result = (IntPtr)12;
                return;
            }
            base.WndProc(ref m);
        }

        // ================= menus =================
        private void ShowMainMenu(Point at, bool fromMoreButton)
        {
            var menu = DotChrome.Menu();
            int listed = AppSettings.Cleanup.Count, restore = AppSettings.RestoreQueue.Count;
            Add(menu.Items, "Launch console…", delegate { ShowPanel(); });
            Add(menu.Items, listed > 0 ? "Clean now (" + listed + " apps)" : "Clean now (list empty)", delegate { Activate("clean", Point.Empty); }, _launcher != null && !_launcher.Busy);
            Add(menu.Items, restore > 0 ? "Restore closed apps (" + restore + ")" : "Restore closed apps", delegate { _launcher.Restore(); }, restore > 0);
            Add(menu.Items, "Stop llama-server", delegate { _launcher.Server.Stop(); }, _launcher != null && _launcher.ServerActive);
            menu.Items.Add(new ToolStripSeparator());

            var material = new ToolStripMenuItem("Material");
            AddMaterial(material, "Dark smoked glass", GlassMaterial.Dark);
            AddMaterial(material, "Light glass", GlassMaterial.Light);
            AddMaterial(material, "Legacy glass", GlassMaterial.Legacy);
            material.DropDownItems.Add(new ToolStripSeparator());
            var solid = new ToolStripMenuItem(Options.Glass ? "Solid card (no shader, restarts)" : "Solid card (current)") { Checked = !Options.Glass, Enabled = Options.Glass };
            solid.Click += delegate { RestartMode(false); };
            material.DropDownItems.Add(solid);
            menu.Items.Add(material);

            if (Options.Glass)
            {
                var rendering = new ToolStripMenuItem("Rendering");
                rendering.DropDownItems.Add(new ToolStripMenuItem(_glassStatus) { Enabled = false });
                rendering.DropDownItems.Add(new ToolStripSeparator());
                Add(rendering.DropDownItems, "Clear refraction", delegate { Options.Refraction = 19; Options.Blur = 1.2f; Options.Tint = .035f; WakeGlass(); });
                Add(rendering.DropDownItems, "Soft refraction", delegate { Options.Refraction = 10; Options.Blur = 3; Options.Tint = .16f; WakeGlass(); });
                Toggle(rendering.DropDownItems, "Economy refresh (30 FPS when idle)", Options.Economy, delegate (bool v) { Options.Economy = v; }, Options.SampleHz == 0);
                Add(rendering.DropDownItems, Options.SampleHz > 0 ? "Sampling rate: " + Options.SampleHz + " Hz…" : "Sampling rate: auto…", delegate
                {
                    int hz = DotDialog.ChooseRate(this, Options.SampleHz);
                    if (hz >= 0) { Options.SampleHz = hz; WakeGlass(); }
                });
                Toggle(rendering.DropDownItems, "Boost refresh while dragging", Options.DragBoost, delegate (bool v) { Options.DragBoost = v; });
                Toggle(rendering.DropDownItems, "Sync position while dragging", Options.SyncDrag, delegate (bool v) { Options.SyncDrag = v; });
                Toggle(rendering.DropDownItems, "Light motion prediction", Options.PredictionMs > 0, delegate (bool v) { Options.PredictionMs = v ? 4 : 0; }, Options.SyncDrag);
                menu.Items.Add(rendering);

                var diagnostics = new ToolStripMenuItem("Diagnostics");
                Add(diagnostics.DropDownItems, "Reload glass shader", delegate { StartGlass(); });
                Add(diagnostics.DropDownItems, "Record 30 s latency trace", delegate
                {
                    if (_glass == null) return;
                    try
                    {
                        string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "glass-latency-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".csv");
                        _glass.Trace(path); LogGlass("Latency trace: " + path);
                        _launcher.Flash("Recording latency trace (30 s)");
                    }
                    catch (Exception ex) { LogGlass(ex.Message); }
                });
                Add(diagnostics.DropDownItems, "Save glass preview", delegate
                {
                    if (_glass == null) return;
                    try { _glass.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "glass-preview.bmp"), false); _launcher.Flash("Saved glass-preview.bmp"); }
                    catch (Exception ex) { LogGlass(ex.Message); }
                });
                menu.Items.Add(diagnostics);
            }
            menu.Items.Add(new ToolStripSeparator());
            Toggle(menu.Items, "Always on top", TopMost, delegate (bool v) { TopMost = v; });
            Add(menu.Items, "Exit", delegate { Close(); });
            menu.Closed += delegate { menu.BeginInvoke((Action)menu.Dispose); };
            if (fromMoreButton)
            {
                var r = HitRect("more");
                menu.Show(this, new Point(r.Right, r.Top), ToolStripDropDownDirection.AboveLeft);
            }
            else menu.Show(this, at);
        }

        private void AddMaterial(ToolStripMenuItem parent, string text, GlassMaterial material)
        {
            var item = new ToolStripMenuItem(text) { Checked = Options.Glass && AppSettings.Material == material };
            item.Click += delegate
            {
                AppSettings.Material = material; AppSettings.Save();
                if (!Options.Glass) { RestartMode(true); return; }
                MaterialChanged();
                if (_panel != null && !_panel.IsDisposed) _panel.ThemeChanged();
            };
            parent.DropDownItems.Add(item);
        }

        // Palette and shader material follow AppSettings.Material; redraw the foreground and re-submit options.
        internal void MaterialChanged() { _signature = null; WakeGlass(); UpdateSurface(); }

        private static void Add(ToolStripItemCollection items, string text, Action action, bool enabled = true)
        {
            var item = new ToolStripMenuItem(text) { Enabled = enabled };
            item.Click += delegate { action(); };
            items.Add(item);
        }

        private void Toggle(ToolStripItemCollection items, string text, bool value, Action<bool> set, bool enabled = true)
        {
            var item = new ToolStripMenuItem(text) { Checked = value, Enabled = enabled };
            item.Click += delegate { set(!value); WakeGlass(); };
            items.Add(item);
        }

        private Rectangle HitRect(string key)
        {
            foreach (var h in _hits) if (h.Value == key) return h.Key;
            return new Rectangle(ClientSize.Width - Pad, ClientSize.Height - ActionBottom - ActionH, 1, 1);
        }

        private void ShowProfileMenu()
        {
            _launcher.Rescan();
            var menu = DotChrome.Menu();
            var current = _launcher.Current;
            foreach (var profile in _launcher.Profiles)
            {
                var p = profile;
                var item = new ToolStripMenuItem(p.Name + (p.Ctx > 0 ? "    ctx " + LauncherController.Short(p.Ctx) : "")) { Checked = ReferenceEquals(p, current) };
                item.Click += delegate { _launcher.Current = p; };
                menu.Items.Add(item);
            }
            if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
            Add(menu.Items, _launcher.Profiles.Count > 0 ? "Launch console…" : "Add a profile folder…", delegate { ShowPanel(); });
            menu.Closed += delegate { menu.BeginInvoke((Action)menu.Dispose); };
            var r = HitRect("profiles");
            menu.Show(this, new Point(r.Right, r.Top), ToolStripDropDownDirection.AboveLeft);
        }

        private void ShowProcessMenu(ProcRow row, Point at)
        {
            if (_launcher == null) { ConfirmKill(row); return; }
            string app = AppNameOf(row), exe = null;
            foreach (var node in ProcessTree.Snapshot().All)
                if (string.Equals(node.Name, app, StringComparison.OrdinalIgnoreCase) && (exe = ProcessTree.ImagePath(node.Pid)) != null) break;
            if (exe == null) exe = ProcessTree.ImagePath(row.Pid);
            var menu = AppMenu.Build(app, exe, delegate (string message) { _launcher.Flash(message); });
            menu.Items.Insert(0, new ToolStripMenuItem(row.Name + " · " + CleanupResult.FormatMB(row.MB) + " · PID " + row.Pid) { Enabled = false });
            menu.Items.Insert(1, new ToolStripSeparator());
            menu.Items.Add(new ToolStripSeparator());
            Add(menu.Items, "End process " + row.Name + "…", delegate { ConfirmKill(row); });
            menu.Show(this, at);
        }

        internal void ShowPanel()
        {
            if (_launcher == null) return;
            if (_panel != null && !_panel.IsDisposed) { _panel.Activate(); return; }
            _panel = new LaunchPanel(_launcher, delegate { return _sample; });
            var wa = Screen.FromControl(this).WorkingArea;
            int x = Left - _panel.Width - 12;
            if (x < wa.Left) x = Math.Min(Right + 12, wa.Right - _panel.Width);
            _panel.Location = new Point(Math.Max(wa.Left, x), Math.Max(wa.Top, Math.Min(Top, wa.Bottom - _panel.Height)));
            _panel.TopMost = TopMost;
            _panel.Show(this);
        }

        private void ConfirmKill(ProcRow row)
        {
            if (row.Pid <= 4 || row.Pid == System.Diagnostics.Process.GetCurrentProcess().Id) return;
            if (!DotDialog.Confirm(this, "End process", "End " + row.Name + " (PID " + row.Pid + ", " + CleanupResult.FormatMB(row.MB) + ")? Unsaved work in it is lost.", "End process")) return;
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(row.Pid))
                {
                    // The PID may have been reused by another program since the sample.
                    string now = null;
                    try { now = p.ProcessName; } catch { }
                    if (!string.Equals(now, row.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        DotDialog.Info(this, "Cancelled", "PID " + row.Pid + " now belongs to " + (now ?? "another process") + ". Nothing was ended.");
                        return;
                    }
                    p.Kill();
                }
            }
            catch (Exception ex)
            {
                DotDialog.Info(this, "Could not end process", ex.Message + (ex is System.ComponentModel.Win32Exception ? " Try running the widget as administrator." : ""));
            }
        }

        // ================= mode restart =================
        private void RestartMode(bool glass)
        {
            string args = RestartArguments(glass, ClientSize.Width, ClientSize.Height);
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, args)
                { WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory, UseShellExecute = false };
                using (var process = System.Diagnostics.Process.Start(start))
                    if (process == null) throw new InvalidOperationException("The selected mode did not start.");
                Close(); // OnFormClosed releases desktop capture, D3D11 and render timers.
            }
            catch (Exception ex) { DotDialog.Info(this, "Could not switch mode", ex.Message); }
        }

        private static string RestartArguments(bool glass, int width, int height)
        {
            // Rebuilt from current settings: no stale --classic or growing duplicates after repeated switches.
            var args = new System.Text.StringBuilder(glass ? "--glass" : "--classic");
            args.AppendFormat(CultureInfo.InvariantCulture,
                " --top {0} --interval {1} --w {2} --h {3} --corner {4} --smooth {5} --ss {6} --fps {7} --refraction {8} --blur {9} --tint {10}",
                Options.Top, glass ? Options.IntervalMs : Math.Max(2000, Options.IntervalMs), width, height,
                Options.CornerRadius, Options.CornerSmooth, glass ? Options.Supersample : 1,
                Options.Fps, Options.Refraction, Options.Blur, Options.Tint);
            if (!string.IsNullOrEmpty(Options.FontPath)) args.Append(" --font ").Append(QuoteArgument(Options.FontPath));
            if (Options.Always.Count > 0) args.Append(" --always ").Append(QuoteArgument(string.Join(",", Options.Always)));
            if (Options.Economy) args.Append(" --economy");
            if (Options.SampleHz > 0) args.Append(" --sample-hz ").Append(Options.SampleHz);
            args.Append(Options.DragBoost ? " --drag-boost" : " --no-drag-boost");
            args.Append(Options.SyncDrag ? " --sync-drag" : " --no-sync-drag");
            args.Append(" --prediction-ms ").Append(Options.PredictionMs.ToString(CultureInfo.InvariantCulture));
            return args.ToString();
        }

        private static string QuoteArgument(string value)
        {
            var result = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }
    }
}
