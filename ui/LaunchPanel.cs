using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GpuVramWidget
{
    // Launch console, fully owner-drawn in the dot-matrix style:
    //   VRAM CONSOLE (dot logo) / subtitle / ● status                                   [ESC]
    //   01 PROFILES (one row per script)          03 CLEAN LIST | GPU PREFS (tabs)
    //   02 PRE-FLIGHT (ring = need; amber past free-now, red past free-after-clean; plan bar)
    //   [switch] LAUNCH AT MAX CTX                       [RESTORE] [CLEAN ONLY] [CLEAN & LAUNCH]
    //   FREE THE VRAM, FEED THE MODEL_
    internal sealed class LaunchPanel : Form
    {
        private const int PageX = 28, Gap = 18, W = 912, H = 740, BodyY = 118;
        private const int ProfileRowH = 28, ProfileRows = 5, CleanRowH = 26;

        private sealed class CleanRow { public string Name, Exe; public double MB; public bool Running; public int Pref; }

        private readonly LauncherController _launcher;
        private readonly Func<VramSample> _sample;
        private readonly List<KeyValuePair<RectangleF, string>> _hits = new List<KeyValuePair<RectangleF, string>>();
        private readonly Timer _refresh = new Timer { Interval = 2000 };
        private List<CleanRow> _rows = new List<CleanRow>();
        private string _hover, _down;
        private int _profileScroll, _cleanScroll, _gpuScroll;
        private RectangleF _profileArea, _cleanArea, _gpuArea;
        private List<GpuPreference.Entry> _gpu = new List<GpuPreference.Entry>();
        private bool _gpuTab;   // right card shows GPU preferences instead of the clean list

        internal LaunchPanel(LauncherController launcher, Func<VramSample> sample)
        {
            _launcher = launcher; _sample = sample;
            Text = "VRAM Console";
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            KeyPreview = true; DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            ClientSize = new Size(W, H);
            BackColor = DotUi.P.Ground;
            _launcher.Changed += LauncherChanged;
            _refresh.Tick += delegate { RefreshRows(); Invalidate(); };
            _refresh.Start();
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
                if (e.KeyCode == Keys.Enter) Act("primary", Point.Empty);
            };
            RefreshRows();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); DotChrome.ApplyWindowTheme(Handle); }

        internal void ShowGpuTab() { _gpuTab = true; RefreshRows(); Invalidate(); }

        internal void ThemeChanged() { BackColor = DotUi.P.Ground; DotChrome.ApplyWindowTheme(Handle); Invalidate(); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _launcher.Changed -= LauncherChanged;
            _refresh.Stop(); _refresh.Dispose();
            base.OnFormClosed(e);
        }

        private void LauncherChanged(object sender, EventArgs e) { if (!IsDisposed) Invalidate(); }

        // ---------------- data
        private void RefreshRows()
        {
            try { _gpu = GpuPreference.Entries(); } catch { _gpu = new List<GpuPreference.Entry>(); }
            var sample = _sample();
            var tree = ProcessTree.Snapshot();
            var byApp = new Dictionary<string, CleanRow>(StringComparer.OrdinalIgnoreCase);
            if (sample != null)
                foreach (var row in sample.Rows)
                {
                    string app = tree.AppNameFor(row.Pid) ?? row.Name;
                    if (ProcessTree.IsProtected(app)) continue;
                    CleanRow r;
                    if (!byApp.TryGetValue(app, out r)) byApp[app] = r = new CleanRow { Name = app, Running = true };
                    r.MB += row.MB;
                }
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in tree.All) running.Add(node.Name);
            foreach (string name in AppSettings.Cleanup)
                if (!byApp.ContainsKey(name)) byApp[name] = new CleanRow { Name = name, Running = running.Contains(name) };
            foreach (var r in byApp.Values)
            {
                if (!r.Running) continue;
                foreach (var node in tree.All)
                    if (string.Equals(node.Name, r.Name, StringComparison.OrdinalIgnoreCase) && (r.Exe = ProcessTree.ImagePath(node.Pid)) != null) break;
                r.Pref = r.Exe != null ? GpuPreference.Get(r.Exe) : 0;
            }
            var rows = new List<CleanRow>(byApp.Values);
            // Listed apps first, then by usage, so the list reads as "what will be closed".
            rows.Sort(delegate (CleanRow a, CleanRow b)
            {
                bool la = AppSettings.IsCleanup(a.Name), lb = AppSettings.IsCleanup(b.Name);
                return la != lb ? (la ? -1 : 1) : b.MB.CompareTo(a.MB);
            });
            _rows = rows;
        }

        // ---------------- painting
        private void Hit(RectangleF r, string key) { _hits.Add(new KeyValuePair<RectangleF, string>(r, key)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; DotUi.Prepare(g);
            var p = DotUi.P;
            _hits.Clear();
            g.Clear(p.Ground);
            if (p.IsDark)
                using (var glow = new GraphicsPath())
                {
                    // One radial ember glow in the top-left corner.
                    glow.AddEllipse(-W * 0.55f, -H * 0.75f, W * 1.35f, H * 1.4f);
                    using (var brush = new PathGradientBrush(glow) { CenterPoint = new PointF(W * 0.12f, -H * 0.05f), CenterColor = p.Ember, SurroundColors = new[] { Color.FromArgb(0, p.Ground) } })
                        g.FillPath(brush, glow);
                }

            DrawHeader(g);
            float colW = (W - PageX * 2 - Gap) / 2f, leftX = PageX, rightX = PageX + colW + Gap;
            float profileH = 18 + 20 + 16 + ProfileRows * ProfileRowH + 12 + 30 + 22;
            DrawProfiles(g, new RectangleF(leftX, BodyY, colW, profileH));
            float fitY = BodyY + profileH + Gap, bottom = H - 96;
            DrawPreflight(g, new RectangleF(leftX, fitY, colW, bottom - fitY));
            DrawRightCard(g, new RectangleF(rightX, BodyY, colW, bottom - BodyY));
            DrawFooter(g);
        }

        private void DrawHeader(Graphics g)
        {
            var p = DotUi.P;
            DotUi.DotText(g, "VRAM CONSOLE", PageX, 26, 3.2f, 1.2f, p.Ink);
            DotUi.Text(g, "llama-server launcher · clean · fit · launch", DotUi.Mono(13), p.Muted, new RectangleF(PageX, 64, 520, 18));
            var status = _launcher.Status();
            var sr = new RectangleF(PageX - 6, 86, 560, 22);
            if (status.Clickable && _hover == "status")
                using (var path = DotUi.Rounded(sr, 8)) using (var b = new SolidBrush(p.PillBg)) g.FillPath(b, path);
            DotUi.StatusDot(g, PageX + 4, 97, status.Color);
            DotUi.Text(g, status.Text, DotUi.Mono(12, true), p.Ink, new RectangleF(PageX + 16, 88, 540, 18));
            if (status.Clickable) Hit(sr, "status");

            var close = new RectangleF(W - PageX - 64, 24, 64, 28);
            DotUi.Pill(g, close, "ESC", false, _hover == "close");
            Hit(close, "close");
            string materialText = AppSettings.Material == GlassMaterial.Dark ? "DARK GLASS" : AppSettings.Material == GlassMaterial.Light ? "LIGHT GLASS" : "LEGACY GLASS";
            float materialW = DotUi.Measure(g, materialText, DotUi.Mono(12)) + 36;
            var material = new RectangleF(close.X - 8 - materialW, 24, materialW, 28);
            DotUi.Pill(g, material, materialText, false, _hover == "material");
            Hit(material, "material");
            DotUi.Caption(g, "Settings · %APPDATA%\\GpuVramWidget", new RectangleF(W - PageX - 360, 64, 360, 16), StringAlignment.Far);
        }

        private void DrawProfiles(Graphics g, RectangleF card)
        {
            var p = DotUi.P;
            DotUi.Card(g, card);
            var profiles = _launcher.Profiles;
            DotUi.CardHead(g, card.X + 22, card.Y + 18, card.Width - 44, "01", "Profiles", profiles.Count + " × script");
            _profileArea = new RectangleF(card.X + 12, card.Y + 54, card.Width - 24, ProfileRows * ProfileRowH);
            var current = _launcher.Current;
            if (profiles.Count == 0)
                DotUi.Text(g, "No scripts yet. Add a folder with llama-server .bat files.", DotUi.Mono(12), p.Muted, new RectangleF(card.X + 22, card.Y + 58, card.Width - 44, 20));
            _profileScroll = Math.Max(0, Math.Min(_profileScroll, profiles.Count - ProfileRows));
            for (int i = 0; i < ProfileRows && i + _profileScroll < profiles.Count; i++)
            {
                var profile = profiles[i + _profileScroll];
                var r = new RectangleF(_profileArea.X, _profileArea.Y + i * ProfileRowH, _profileArea.Width, ProfileRowH - 2);
                bool selected = ReferenceEquals(profile, current), hot = _hover == "profile:" + profile.Path;
                if (selected || hot)
                    using (var path = DotUi.Rounded(r, 9))
                    {
                        using (var b = new SolidBrush(selected ? p.AccentBg : p.PillBg)) g.FillPath(b, path);
                        using (var pen = new Pen(selected ? p.Accent : p.PillBorder)) g.DrawPath(pen, path);
                    }
                float cy = r.Y + r.Height / 2;
                if (selected) using (var b = new SolidBrush(p.Accent)) g.FillEllipse(b, r.X + 10, cy - 3.5f, 7, 7);
                else using (var pen = new Pen(p.Unlit, 1.2f)) g.DrawEllipse(pen, r.X + 10, cy - 3.5f, 7, 7);
                string tags = (profile.Draft != null ? "DRAFT " : "") + (profile.Mmproj != null ? "VISION " : "") + (profile.Ctx > 0 ? "CTX " + LauncherController.Short(profile.Ctx) : "");
                float tagW = DotUi.Measure(g, tags, DotUi.Mono(10.5f)) + 4;
                DotUi.Text(g, profile.Name, DotUi.Mono(12, selected), profile.Error != null && profile.ServerExe == null ? p.Dim : selected ? p.Accent : p.Ink,
                    new RectangleF(r.X + 26, r.Y, r.Width - 40 - tagW, r.Height));
                DotUi.Caption(g, tags, new RectangleF(r.Right - 10 - tagW, r.Y, tagW, r.Height), StringAlignment.Far);
                Hit(r, "profile:" + profile.Path);
            }
            if (profiles.Count > ProfileRows)
                DotUi.Caption(g, (_profileScroll + 1) + "-" + Math.Min(profiles.Count, _profileScroll + ProfileRows) + " of " + profiles.Count + " · scroll",
                    new RectangleF(card.X + 22, _profileArea.Bottom + 2, card.Width - 44, 14), StringAlignment.Far, p.Dim);

            float by = card.Bottom - 22 - 30, bx = card.X + 22;
            foreach (var b in new[] { new[] { "Add folder", "addfolder" }, new[] { "Edit script", "edit" }, new[] { "Rescan", "rescan" } })
            {
                float bw = DotUi.Measure(g, b[0].ToUpperInvariant(), DotUi.Mono(12)) + 32;
                var r = new RectangleF(bx, by, bw, 30);
                DotUi.Pill(g, r, b[0].ToUpperInvariant(), false, _hover == b[1], false, b[1] != "edit" || current != null);
                Hit(r, b[1]);
                bx += bw + 8;
            }
        }

        // The script's ctx is the healthy baseline; the ring shows its VRAM need against what is free now
        // (amber) and after cleaning (red). Measured/estimated limits are shown beside it for reference only.
        private void DrawPreflight(Graphics g, RectangleF card)
        {
            var p = DotUi.P;
            DotUi.Card(g, card);
            var profile = _launcher.Current;
            var limits = profile != null ? _launcher.Limits(profile) : null;
            var budget = VramBudget.Measure(_sample());
            int ctx = profile != null ? LauncherController.HealthyCtx(profile, limits) : 0;
            string badge = profile == null ? null : limits == null ? "measuring" : !limits.Valid ? "unavailable"
                : (profile.Ctx > 0 ? "yours " : "limit ") + LauncherController.Short(ctx);
            DotUi.CardHead(g, card.X + 22, card.Y + 18, card.Width - 44, "02", "Pre-flight", badge);
            float top = card.Y + 52;
            if (profile == null) { DotUi.Text(g, "Select a profile.", DotUi.Mono(12), p.Muted, new RectangleF(card.X + 22, top, card.Width - 44, 20)); return; }
            if (limits == null) { DotUi.Text(g, "Reading model metadata with llama-fit-params…", DotUi.Mono(12), p.Muted, new RectangleF(card.X + 22, top, card.Width - 44, 20)); return; }
            if (!limits.Valid)
            {
                var fit = _launcher.Fit(profile);
                DotUi.Text(g, fit != null && fit.Error != null ? fit.Error : "Pre-flight unavailable", DotUi.Mono(12), p.Crit, new RectangleF(card.X + 22, top, card.Width - 44, 20));
                DotUi.Text(g, "The script can still be launched.", DotUi.Mono(12), p.Muted, new RectangleF(card.X + 22, top + 22, card.Width - 44, 20));
                return;
            }
            double need = limits.NeedMiB(ctx), totalGB = budget.TotalMB / 1024;
            double warn = budget.FreeNow / 1024, crit = budget.FreeAfter / 1024;

            var ring = new RectangleF(card.X + 12, top - 4, 140, 140);
            DotUi.Ring(g, ring, need / 1024, 0, totalGB, warn > 0 ? warn : double.NaN, crit > 0 ? crit : double.NaN, 4f, 50);
            string value = (need / 1024).ToString(need < 102400 ? "0.0" : "0", CultureInfo.InvariantCulture); // <= 4 characters
            var vs = DotUi.MeasureDotText(value, 2.8f, 1f);
            float cx = ring.X + ring.Width / 2, cy = ring.Y + ring.Height / 2;
            DotUi.DotText(g, value, cx - vs.Width / 2, cy - vs.Height / 2 - 9, 2.8f, 1f, p.ForValue(need / 1024, warn, crit, p.Ink));
            DotUi.Text(g, "GB", DotUi.Mono(11), p.Muted, new RectangleF(cx - 30, cy + 9, 60, 14), StringAlignment.Center);
            DotUi.Text(g, "NEED", DotUi.Mono(9.5f), p.Dim, new RectangleF(cx - 30, cy + 23, 60, 13), StringAlignment.Center);

            float x0 = ring.Right + 10, colW = (card.Right - 22 - x0) / 2;
            bool spills = limits.RawNeedMiB(ctx) > budget.FreeAfter + AppSettings.HeadroomMiB;
            bool overMemory = ctx > limits.MemoryAfter, overSpeed = ctx > limits.Speed;
            string est = limits.Calibrated ? "" : " (est.)";
            Stat(g, x0, top, colW, profile.Ctx > 0 ? "Healthy · script" : "Healthy · computed", ctx.ToString("N0"), "", spills ? p.Crit : overMemory || overSpeed ? p.Warn : p.Ink);
            Stat(g, x0 + colW, top, colW, "Limit" + est, "~" + LauncherController.Short(limits.HealthyAfter), "", p.Ink);
            Stat(g, x0, top + 42, colW, "Memory limit" + est, "~" + LauncherController.Short(limits.MemoryAfter), "", spills ? p.Crit : overMemory ? p.Warn : p.Ink);
            Stat(g, x0 + colW, top + 42, colW, (int)AppSettings.MinDecodeTps + " t/s limit" + est,
                limits.Speed < (1 << 30) ? "~" + LauncherController.Short(limits.Speed) : "--", "", overSpeed ? p.Warn : p.Ink);
            double full = limits.DecodeTpsFull(ctx);
            Stat(g, x0, top + 84, colW, "Decode full" + est, full > 0 ? full.ToString("0", CultureInfo.InvariantCulture) : "--", full > 0 ? "t/s" : "",
                full > 0 && full < AppSettings.MinDecodeTps ? p.Warn : p.Ink);
            Stat(g, x0 + colW, top + 84, colW, "Per 1K ctx" + est, limits.MiBPer1K.ToString("0.0", CultureInfo.InvariantCulture), "MB", p.Ink);

            // Calibrate + the decode threshold used for the speed limit.
            float by = ring.Bottom + 8, bx = card.X + 22;
            string calText = limits.Calibrated ? "RECALIBRATE" : "CALIBRATE";
            float cw = DotUi.Measure(g, calText, DotUi.Mono(12)) + 32;
            var calRect = new RectangleF(bx, by, cw, 28);
            DotUi.Pill(g, calRect, calText, !limits.Calibrated, _hover == "calibrate", false, !_launcher.Busy && !_launcher.ServerActive);
            if (!_launcher.Busy && !_launcher.ServerActive) Hit(calRect, "calibrate");
            DotUi.Caption(g, "Min decode", new RectangleF(calRect.Right + 14, by, 80, 28));
            float px = calRect.Right + 14 + 76;
            foreach (int tps in new[] { 25, 30, 35 })
            {
                var r = new RectangleF(px, by, 46, 28);
                DotUi.Pill(g, r, tps.ToString(), (int)AppSettings.MinDecodeTps == tps, _hover == "tps:" + tps);
                Hit(r, "tps:" + tps);
                px += 50;
            }
            DotUi.Caption(g, limits.Calibrated ? "Measured " + limits.Calibration.When.ToString("MM-dd HH:mm") + " · ctx " + LauncherController.Short(limits.Calibration.TestedCtx)
                : _launcher.ServerActive ? "Estimate (no test) · stop llama-server to calibrate" : "Estimate (no test): fit-params + model files · calibrate to measure",
                new RectangleF(card.X + 22, by + 32, card.Width - 44, 14), StringAlignment.Near, p.Dim);
        }

        private static void Stat(Graphics g, float x, float y, float width, string caption, string value, string unit, Color color)
        {
            DotUi.Caption(g, caption, new RectangleF(x, y, width - 6, 14));
            DotUi.ValueWithUnit(g, value, unit, x, y + 15, 20, false, color, 15);
        }

        private void DrawRightCard(Graphics g, RectangleF card)
        {
            var p = DotUi.P;
            DotUi.Card(g, card);
            int changed = 0;
            foreach (var e in _gpu) if (e.Changed) changed++;
            if (_gpuTab)
                DotUi.CardHead(g, card.X + 22, card.Y + 18, card.Width - 44, "03", "GPU preferences", _gpu.Count + " × app · " + changed + " changed");
            else
            {
                int listed = 0; double listedMB = 0;
                foreach (var r in _rows) if (AppSettings.IsCleanup(r.Name)) { listed++; if (r.MB > 0) listedMB += r.MB; }
                DotUi.CardHead(g, card.X + 22, card.Y + 18, card.Width - 44, "03", "Clean list", listed + " × app · " + CleanupResult.FormatMB(listedMB));
            }
            // Tabs.
            float tx = card.X + 22;
            foreach (var tab in new[] { new[] { "CLEAN LIST", "tab:clean" }, new[] { changed > 0 ? "GPU PREFS · " + changed + " CHANGED" : "GPU PREFS", "tab:gpu" } })
            {
                float tw = DotUi.Measure(g, tab[0], DotUi.Mono(12)) + 32;
                var r = new RectangleF(tx, card.Y + 50, tw, 28);
                DotUi.Pill(g, r, tab[0], (tab[1] == "tab:gpu") == _gpuTab, _hover == tab[1]);
                Hit(r, tab[1]);
                tx += tw + 8;
            }
            if (_gpuTab) DrawGpuPrefs(g, card, card.Y + 92);
            else DrawCleanBody(g, card, card.Y + 92);
        }

        private void DrawCleanBody(Graphics g, RectangleF card, float y)
        {
            var p = DotUi.P;
            // Grace pills.
            float x = card.X + 22;
            DotUi.Caption(g, "Grace", new RectangleF(x, y, 60, 26));
            x += 56;
            foreach (int s in new[] { 3, 5, 10, 20 })
            {
                var r = new RectangleF(x, y, 52, 26);
                DotUi.Pill(g, r, s + "S", AppSettings.GraceSeconds == s, _hover == "grace:" + s);
                Hit(r, "grace:" + s);
                x += 58;
            }
            DotUi.Caption(g, "then force", new RectangleF(x + 4, y, card.Right - 22 - x - 4, 26), StringAlignment.Near, p.Dim);
            DotUi.Text(g, "Click to toggle · right-click for GPU preference", DotUi.Mono(11), p.Muted, new RectangleF(card.X + 22, y + 32, card.Width - 44, 16));

            _cleanArea = new RectangleF(card.X + 12, y + 56, card.Width - 24, card.Bottom - 18 - (y + 56));
            int visible = (int)(_cleanArea.Height / CleanRowH);
            _cleanScroll = Math.Max(0, Math.Min(_cleanScroll, _rows.Count - visible));
            double peak = 0;
            foreach (var r in _rows) peak = Math.Max(peak, r.MB);
            double max = DotUi.NiceMax(Math.Max(peak, 1));
            if (_rows.Count == 0) DotUi.Text(g, "No GPU apps found", DotUi.Mono(12), p.Muted, new RectangleF(card.X + 22, _cleanArea.Y, card.Width - 44, 20));
            for (int i = 0; i < visible && i + _cleanScroll < _rows.Count; i++)
            {
                var row = _rows[i + _cleanScroll];
                var r = new RectangleF(_cleanArea.X, _cleanArea.Y + i * CleanRowH, _cleanArea.Width, CleanRowH - 2);
                bool on = AppSettings.IsCleanup(row.Name), hot = _hover == "clean:" + row.Name;
                if (hot) using (var path = DotUi.Rounded(r, 8)) using (var b = new SolidBrush(p.PillBg)) g.FillPath(b, path);
                float cy = r.Y + r.Height / 2;
                if (on) using (var b = new SolidBrush(p.Accent)) g.FillEllipse(b, r.X + 10, cy - 3.5f, 7, 7);
                else using (var pen = new Pen(p.Unlit, 1.2f)) g.DrawEllipse(pen, r.X + 10, cy - 3.5f, 7, 7);
                float nameW = 150, tagW = 44, valueW = 70;
                DotUi.Text(g, row.Name, DotUi.Mono(12, on), !row.Running ? p.Dim : on ? p.Accent : p.Ink, new RectangleF(r.X + 26, r.Y, nameW - 4, r.Height));
                DotUi.Bar(g, new RectangleF(r.X + 26 + nameW, r.Y, r.Width - 26 - nameW - valueW - tagW - 10, r.Height), Math.Max(0, row.MB), 0, max,
                    double.NaN, double.NaN, double.NaN, 6, 3, on ? p.Accent : p.Ink);
                if (row.Running && row.MB > 0)
                    DotUi.ValueWithUnit(g, row.MB >= 1024 ? (row.MB / 1024).ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(row.MB).ToString(CultureInfo.InvariantCulture),
                        row.MB >= 1024 ? "GB" : "MB", r.Right - tagW - 6, r.Y + 1, r.Height, true, p.Ink, 13);
                else DotUi.Caption(g, row.Running ? "0 MB" : "not running", new RectangleF(r.Right - tagW - 6 - valueW, r.Y, valueW, r.Height), StringAlignment.Far, p.Dim);
                DotUi.Caption(g, row.Pref == 1 ? "iGPU" : row.Pref == 2 ? "dGPU" : "", new RectangleF(r.Right - tagW, r.Y, tagW - 6, r.Height), StringAlignment.Far,
                    row.Pref == 1 ? p.Accent : p.Muted);
                Hit(r, "clean:" + row.Name);
            }
            if (_rows.Count > visible)
                DotUi.Caption(g, (_cleanScroll + 1) + "-" + Math.Min(_rows.Count, _cleanScroll + visible) + " of " + _rows.Count + " · scroll",
                    new RectangleF(card.X + 22, card.Bottom - 18, card.Width - 44, 14), StringAlignment.Far, p.Dim);
        }

        // Every app with a GPU preference now or at widget start. Three pills per row set it directly;
        // an amber dot marks rows that differ from the startup snapshot.
        private void DrawGpuPrefs(Graphics g, RectangleF card, float y)
        {
            var p = DotUi.P;
            int changed = 0;
            foreach (var e in _gpu) if (e.Changed) changed++;
            DotUi.Caption(g, "Snapshot at start " + GpuPreference.StartupTime.ToString("HH:mm") + " · " + (changed == 0 ? "unchanged" : changed + " changed"),
                new RectangleF(card.X + 22, y, 260, 26));
            string restoreText = "RESTORE STARTUP";
            float rw = DotUi.Measure(g, restoreText, DotUi.Mono(12)) + 32;
            var restore = new RectangleF(card.Right - 22 - rw, y, rw, 26);
            DotUi.Pill(g, restore, restoreText, changed > 0, _hover == "gpurestore", false, changed > 0);
            if (changed > 0) Hit(restore, "gpurestore");
            DotUi.Text(g, "Applies when the app restarts · right-click a row for more", DotUi.Mono(11), p.Muted, new RectangleF(card.X + 22, y + 32, card.Width - 44, 16));

            _gpuArea = new RectangleF(card.X + 12, y + 56, card.Width - 24, card.Bottom - 18 - (y + 56));
            int visible = (int)(_gpuArea.Height / CleanRowH);
            _gpuScroll = Math.Max(0, Math.Min(_gpuScroll, _gpu.Count - visible));
            if (_gpu.Count == 0)
                DotUi.Text(g, "No app has a GPU preference. Windows decides for all.", DotUi.Mono(12), p.Muted, new RectangleF(card.X + 22, _gpuArea.Y, card.Width - 44, 20));
            string[] labels = { "DEFAULT", "IGPU", "DGPU" };
            float[] widths = { 70, 52, 52 };
            for (int i = 0; i < visible && i + _gpuScroll < _gpu.Count; i++)
            {
                var e = _gpu[i + _gpuScroll];
                var r = new RectangleF(_gpuArea.X, _gpuArea.Y + i * CleanRowH, _gpuArea.Width, CleanRowH - 2);
                if (_hover == "gpurow:" + e.Key) using (var path = DotUi.Rounded(r, 8)) using (var b = new SolidBrush(p.PillBg)) g.FillPath(b, path);
                float cy = r.Y + r.Height / 2;
                if (e.Changed) using (var b = new SolidBrush(p.Warn)) g.FillEllipse(b, r.X + 10, cy - 3.5f, 7, 7);
                Hit(r, "gpurow:" + e.Key);

                float px = r.Right - 6;
                for (int k = 2; k >= 0; k--)
                {
                    px -= widths[k];
                    var pr = new RectangleF(px, r.Y + 2, widths[k], r.Height - 4);
                    string key = "gpuset:" + k + ":" + e.Key;
                    DotUi.Pill(g, pr, labels[k], e.Current == k, _hover == key);
                    Hit(pr, key);
                    px -= 4;
                }
                string was = e.Changed ? "was " + (e.AtStartup == 1 ? "iGPU" : e.AtStartup == 2 ? "dGPU" : "default") : e.Packaged ? "store app" : e.Missing ? "missing" : "";
                float wasW = 84;
                DotUi.Caption(g, was, new RectangleF(px - wasW, r.Y, wasW - 4, r.Height), StringAlignment.Far, e.Changed ? p.Warn : p.Dim);
                DotUi.Text(g, e.Display, DotUi.Mono(12, e.Changed), e.Missing ? p.Dim : p.Ink, new RectangleF(r.X + 26, r.Y, px - wasW - r.X - 30, r.Height));
            }
            if (_gpu.Count > visible)
                DotUi.Caption(g, (_gpuScroll + 1) + "-" + Math.Min(_gpu.Count, _gpuScroll + visible) + " of " + _gpu.Count + " · scroll",
                    new RectangleF(card.X + 22, card.Bottom - 18, card.Width - 44, 14), StringAlignment.Far, p.Dim);
        }

        private void DrawFooter(Graphics g)
        {
            var p = DotUi.P;
            float y = H - 80;
            var profile = _launcher.Current;
            // Launches always use the script's own ctx: the user's tuned healthy baseline.
            DotUi.Text(g, "LAUNCHES WITH THE SCRIPT'S CTX", DotUi.Mono(12, true), p.Ink, new RectangleF(PageX, y, 360, 15));
            DotUi.Caption(g, profile != null && profile.Ctx > 0 ? "Healthy baseline " + profile.Ctx.ToString("N0") + " · edit the script to change it" : "Script has no --ctx-size",
                new RectangleF(PageX, y + 15, 380, 15), StringAlignment.Near, p.Muted);

            bool busy = _launcher.Busy, running = _launcher.ServerActive;
            int restore = AppSettings.RestoreQueue.Count;
            string primary = profile == null ? "ADD FOLDER" : running ? "RESTART SERVER" : AppSettings.Cleanup.Count > 0 ? "CLEAN & LAUNCH" : "LAUNCH";
            var buttons = new[] {
                new[] { restore > 0 ? "RESTORE (" + restore + ")" : "RESTORE", "restore" },
                new[] { "CLEAN ONLY", "cleanonly" },
                new[] { primary, "primary" } };
            float x = W - PageX;
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                float bw = Math.Max(110, DotUi.Measure(g, buttons[i][0], DotUi.Mono(12)) + 36);
                x -= bw;
                var r = new RectangleF(x, y, bw, 32);
                bool enabled = !busy && (buttons[i][1] != "restore" || restore > 0);
                DotUi.Pill(g, r, buttons[i][0], buttons[i][1] == "primary" && enabled, _hover == buttons[i][1], false, enabled);
                if (enabled) Hit(r, buttons[i][1]);
                x -= 8;
            }
            DotUi.Text(g, "FREE THE VRAM, FEED THE MODEL_", DotUi.Mono(13, true), p.Ink, new RectangleF(PageX, H - 34, 400, 18));
            DotUi.Caption(g, "Pre-flight by llama-fit-params · budget from D3DKMT", new RectangleF(W - PageX - 420, H - 34, 420, 18), StringAlignment.Far, p.Dim);
        }

        // ---------------- input
        private string HitKey(Point pt)
        {
            for (int i = _hits.Count - 1; i >= 0; i--) if (_hits[i].Key.Contains(pt)) return _hits[i].Value;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string key = HitKey(e.Location);
            Cursor = key != null ? Cursors.Hand : Cursors.Default;
            if (key != _hover) { _hover = key; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != null) { _hover = null; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _down = HitKey(e.Location);
            // Empty space in the header drags the window.
            if (e.Button == MouseButtons.Left && _down == null && e.Y < BodyY) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            string key = HitKey(e.Location);
            if (key == null || key != _down) return;
            if (e.Button == MouseButtons.Right && key.StartsWith("clean:")) { ShowAppMenu(key.Substring(6), e.Location); return; }
            if (e.Button == MouseButtons.Right && key.StartsWith("gpurow:")) { ShowGpuMenu(key.Substring(7), e.Location); return; }
            if (e.Button == MouseButtons.Left) Act(key, e.Location);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int step = e.Delta > 0 ? -1 : 1;
            if (_profileArea.Contains(e.Location)) _profileScroll += step;
            else if (!_gpuTab && _cleanArea.Contains(e.Location)) _cleanScroll += step * 2;
            else if (_gpuTab && _gpuArea.Contains(e.Location)) _gpuScroll += step * 2;
            else return;
            Invalidate();
        }

        private void Act(string key, Point at)
        {
            var profile = _launcher.Current;
            if (key.StartsWith("profile:"))
            {
                foreach (var p in _launcher.Profiles) if (p.Path == key.Substring(8)) _launcher.Current = p;
                return;
            }
            if (key.StartsWith("clean:"))
            {
                string name = key.Substring(6);
                AppSettings.SetCleanup(name, !AppSettings.IsCleanup(name));
                RefreshRows(); Invalidate();
                return;
            }
            if (key.StartsWith("gpuset:"))
            {
                // gpuset:<0|1|2>:<registry value name>
                int pref = key[7] - '0';
                string name = key.Substring(9);
                try { GpuPreference.SetRaw(name, pref); _launcher.Flash(GpuPreference.DisplayName(name) + (pref == 1 ? " → integrated GPU" : pref == 2 ? " → discrete GPU" : " → Windows default") + " (on next start)"); }
                catch (Exception ex) { _launcher.Flash("Could not set GPU preference: " + ex.Message); }
                RefreshRows(); Invalidate();
                return;
            }
            if (key == "tab:clean" || key == "tab:gpu") { _gpuTab = key == "tab:gpu"; RefreshRows(); Invalidate(); return; }
            if (key == "gpurestore") { RestoreGpuPreferences(); return; }
            if (key.StartsWith("tps:")) { AppSettings.MinDecodeTps = int.Parse(key.Substring(4)); AppSettings.Save(); Invalidate(); return; }
            if (key.StartsWith("grace:")) { AppSettings.GraceSeconds = int.Parse(key.Substring(6)); AppSettings.Save(); Invalidate(); return; }
            switch (key)
            {
                case "close": Close(); break;
                case "status": _launcher.StatusClicked(); break;
                case "material":
                    AppSettings.Material = AppSettings.Material == GlassMaterial.Dark ? GlassMaterial.Light : AppSettings.Material == GlassMaterial.Light ? GlassMaterial.Legacy : GlassMaterial.Dark;
                    AppSettings.Save();
                    var owner = Owner as WidgetForm;
                    ThemeChanged();
                    if (owner != null) owner.MaterialChanged();
                    break;
                case "addfolder": AddFolder(); break;
                case "edit": if (profile != null) try { System.Diagnostics.Process.Start("notepad.exe", "\"" + profile.Path + "\""); } catch { } break;
                case "rescan": _launcher.Rescan(); Invalidate(); break;
                case "calibrate":
                    if (profile != null && DotDialog.Confirm(this, "Calibrate " + profile.Name,
                        "Runs this profile in a hidden llama-server on 127.0.0.1 at two ctx sizes, measuring VRAM, spill and decode speed as the context fills. About 4 minutes; the GPU is fully used meanwhile.", "Calibrate"))
                        _launcher.Calibrate(profile);
                    break;
                case "restore": _launcher.Restore(); break;
                case "cleanonly": _launcher.Clean(delegate { RefreshRows(); Invalidate(); }); break;
                case "primary":
                    if (profile == null) AddFolder();
                    else _launcher.CleanAndLaunch(profile);
                    break;
            }
        }

        private void RestoreGpuPreferences()
        {
            int changed = GpuPreference.ChangedSinceStartup();
            if (changed == 0) return;
            if (!DotDialog.Confirm(this, "Restore GPU preferences",
                "Put " + changed + " apps back to the GPU preference they had when the widget started (" + GpuPreference.StartupTime.ToString("HH:mm") + "). Each app picks it up on its next start.",
                "Restore")) return;
            try { _launcher.Flash("Restored " + GpuPreference.RestoreStartup() + " GPU preferences"); }
            catch (Exception ex) { _launcher.Flash("Restore failed: " + ex.Message); }
            RefreshRows(); Invalidate();
        }

        private void ShowGpuMenu(string key, Point at)
        {
            var entry = _gpu.Find(delegate (GpuPreference.Entry x) { return x.Key == key; });
            if (entry == null) return;
            var menu = DotChrome.Menu();
            menu.Items.Add(new ToolStripMenuItem(entry.Packaged ? key : entry.Key) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            var revert = new ToolStripMenuItem("Revert to startup (" + (entry.AtStartup == 1 ? "iGPU" : entry.AtStartup == 2 ? "dGPU" : "default") + ")") { Enabled = entry.Changed };
            revert.Click += delegate { GpuPreference.RevertToStartup(key); _launcher.Flash(entry.Display + " reverted"); RefreshRows(); Invalidate(); };
            menu.Items.Add(revert);
            var open = new ToolStripMenuItem("Open file location") { Enabled = !entry.Packaged && !entry.Missing };
            open.Click += delegate { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + key + "\""); } catch { } };
            menu.Items.Add(open);
            var settings = new ToolStripMenuItem("Open Windows graphics settings");
            settings.Click += delegate { try { System.Diagnostics.Process.Start("ms-settings:display-advancedgraphics"); } catch { } };
            menu.Items.Add(settings);
            menu.Closed += delegate { menu.BeginInvoke((Action)menu.Dispose); };
            menu.Show(this, at);
        }

        private void ShowAppMenu(string app, Point at)
        {
            var row = _rows.Find(delegate (CleanRow r) { return r.Name == app; });
            var menu = AppMenu.Build(app, row != null ? row.Exe : null, delegate (string message) { _launcher.Flash(message); RefreshRows(); Invalidate(); });
            menu.Show(this, at);
        }

        private void AddFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Folder with llama-server launch scripts (.bat / .cmd)" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (!AppSettings.ProfileDirs.Exists(delegate (string d) { return string.Equals(d, dialog.SelectedPath, StringComparison.OrdinalIgnoreCase); }))
                    AppSettings.ProfileDirs.Add(dialog.SelectedPath);
                AppSettings.Save();
                _launcher.Rescan();
                Invalidate();
            }
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    }
}
