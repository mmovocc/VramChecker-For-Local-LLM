// Dot-matrix UI kit for WinForms/GDI+.
// Ported from "dot-matrix-dashboard-ui" by karminski (https://github.com/karminski/karminski-design-skills),
// licensed CC BY-NC-SA 4.0. Geometry (dot sizes, pitches, ring arc, 5x7 glyphs, sparkline step) follows the
// kit's wpf/Controls/*.cs OnRender bodies; the light palette is an addition for the light glass material.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace GpuVramWidget
{
    internal enum GlassMaterial { Legacy = 0, Dark = 1, Light = 2 }

    internal sealed class Palette
    {
        public Color Ink, Muted, Dim, Unlit, Faint, Accent, AccentBg, Warn, Crit, Ok;
        public Color CardTop, CardBottom, CardBorder, PillBg, PillBorder, PillHover, PillPressed, Hairline, Ground, Ember;
        public bool IsDark;

        internal static readonly Palette Dark = new Palette
        {
            IsDark = true,
            Ink = Hex(0xEDEAE4), Muted = Hex(0x8C8781), Dim = Hex(0x5A5651), Unlit = Hex(0x373431), Faint = Hex(0x2A2725),
            Accent = Hex(0xFF5A36), AccentBg = Hex(0x2B1A13), Warn = Hex(0xFFB547), Crit = Hex(0xFF3B3B), Ok = Hex(0x5BD38A),
            CardTop = Hex(0x1F1E1D), CardBottom = Hex(0x151414), CardBorder = Hex(0x2B2927),
            PillBg = Hex(0x1A1918), PillBorder = Hex(0x3A3735), PillHover = Hex(0x7A746E), PillPressed = Hex(0x262423),
            Hairline = Hex(0x262422), Ground = Hex(0x141313), Ember = Hex(0x7C4428)
        };

        // Same roles on warm paper: ink and dots dark, one accent, status hues deepened for contrast.
        internal static readonly Palette Light = new Palette
        {
            IsDark = false,
            Ink = Hex(0x1C1B1A), Muted = Hex(0x6E6A65), Dim = Hex(0x9C9690), Unlit = Hex(0xCFCAC4), Faint = Hex(0xE0DCD6),
            Accent = Hex(0xF04A26), AccentBg = Hex(0xFDE7DF), Warn = Hex(0xD98A10), Crit = Hex(0xE02A2A), Ok = Hex(0x2E9E5E),
            CardTop = Hex(0xFBFAF8), CardBottom = Hex(0xF1EFEC), CardBorder = Hex(0xE0DCD6),
            PillBg = Hex(0xF5F3F0), PillBorder = Hex(0xD5D0CA), PillHover = Hex(0x9C9690), PillPressed = Hex(0xEAE7E3),
            Hairline = Hex(0xE6E2DD), Ground = Hex(0xEFEDEA), Ember = Hex(0xF3C7AE)
        };

        private static Color Hex(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        internal Color ForValue(double value, double warning, double critical, Color normal)
        {
            if (!double.IsNaN(critical) && value >= critical) return Crit;
            if (!double.IsNaN(warning) && value >= warning) return Warn;
            return normal;
        }

        internal Color Level(double value, double warning, double critical, double floor)
        {
            if (!double.IsNaN(floor) && value <= floor) return Crit;
            return ForValue(value, warning, critical, Ink);
        }
    }

    internal static class DotUi
    {
        internal static Palette P { get { return AppSettings.Material == GlassMaterial.Dark ? Palette.Dark : Palette.Light; } }

        // ---------- fonts: monospace everywhere, CJK falls back to YaHei (GDI+ has no automatic fallback)
        private static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();
        private static string _mono, _monoBold;

        internal static Font Mono(float px, bool bold = false)
        {
            if (_mono == null)
            {
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var families = new InstalledFontCollection()) foreach (var f in families.Families) installed.Add(f.Name);
                _mono = installed.Contains("Cascadia Mono") ? "Cascadia Mono" : "Consolas";
                _monoBold = installed.Contains("Cascadia Mono SemiBold") ? "Cascadia Mono SemiBold" : _mono;
            }
            if (Program.UiFont != null) // --font override (private collection: must be built from the family object)
            {
                string key = "ui|" + px + "|" + bold;
                Font custom;
                if (!Fonts.TryGetValue(key, out custom))
                    Fonts[key] = custom = new Font(Program.UiFont, px, bold && Program.UiFont.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                return custom;
            }
            return Cached(bold ? _monoBold : _mono, px, bold && _monoBold == _mono);
        }

        internal static Font For(string text, Font mono)
        {
            foreach (char c in text ?? "") if (c >= 0x2E80) return Cached("Microsoft YaHei UI", mono.Size, mono.Bold);
            return mono;
        }

        private static Font Cached(string family, float px, bool bold)
        {
            string key = family + "|" + px + "|" + bold;
            Font font;
            if (!Fonts.TryGetValue(key, out font))
            {
                font = new Font(family, px, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                Fonts[key] = font;
            }
            return font;
        }

        private static readonly StringFormat Near = Format(StringAlignment.Near), Far = Format(StringAlignment.Far), Center = Format(StringAlignment.Center);
        private static StringFormat Format(StringAlignment alignment)
        {
            var f = (StringFormat)StringFormat.GenericTypographic.Clone();
            f.Alignment = alignment; f.LineAlignment = StringAlignment.Center;
            f.Trimming = StringTrimming.EllipsisCharacter;
            f.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            return f;
        }

        internal static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.Half;
        }

        // ---------- text
        internal static void Text(Graphics g, string text, Font font, Color color, RectangleF r, StringAlignment align = StringAlignment.Near)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (var b = new SolidBrush(color))
                g.DrawString(text, For(text, font), b, r, align == StringAlignment.Far ? Far : align == StringAlignment.Center ? Center : Near);
        }

        internal static float Measure(Graphics g, string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return g.MeasureString(text, For(text, font), 10000, Near).Width;
        }

        // 10.5 px, upper-case, muted.
        internal static void Caption(Graphics g, string text, RectangleF r, StringAlignment align = StringAlignment.Near, Color? color = null)
        {
            Text(g, (text ?? "").ToUpperInvariant(), Mono(10.5f), color ?? P.Muted, r, align);
        }

        // Value at 15 px ink with its unit at 10 px muted, right- or left-aligned on the baseline row.
        internal static float ValueWithUnit(Graphics g, string value, string unit, float x, float y, float h, bool alignRight, Color? color = null, float valuePx = 15)
        {
            Font vf = Mono(valuePx, true), uf = Mono(10);
            float vw = Measure(g, value, vf), uw = string.IsNullOrEmpty(unit) ? 0 : Measure(g, unit, uf) + 4;
            float left = alignRight ? x - vw - uw : x;
            Text(g, value, vf, color ?? P.Ink, new RectangleF(left, y, vw + 2, h));
            if (uw > 0) Text(g, unit, uf, P.Muted, new RectangleF(left + vw + 4, y + 1.5f, uw + 2, h));
            return vw + uw;
        }

        // ---------- shapes
        internal static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }

        // Card: radius 18, vertical gradient, 1 px border. alpha < 255 lets glass show through.
        internal static void Card(Graphics g, RectangleF r, int alpha = 255, float radius = 18)
        {
            using (var path = Rounded(r, radius))
            {
                using (var fill = new LinearGradientBrush(new PointF(0, r.Top - 1), new PointF(0, r.Bottom + 1),
                    Color.FromArgb(alpha, P.CardTop), Color.FromArgb(alpha, P.CardBottom)))
                    g.FillPath(fill, path);
                using (var pen = new Pen(P.CardBorder)) g.DrawPath(pen, path);
            }
        }

        // Card header: muted number, TITLE in caps, optional badge pill on the right. Returns the header height.
        internal static float CardHead(Graphics g, float x, float y, float width, string number, string title, string badge)
        {
            Font f = Mono(12), fb = Mono(12, true);
            float nw = string.IsNullOrEmpty(number) ? 0 : Measure(g, number, f) + 10;
            if (nw > 0) Text(g, number, f, P.Muted, new RectangleF(x, y, nw, 20));
            float bw = 0;
            if (!string.IsNullOrEmpty(badge))
            {
                string b = badge.ToUpperInvariant();
                bw = Measure(g, b, Mono(10.5f)) + 18;
                var br = new RectangleF(x + width - bw, y + 1, bw, 18);
                using (var path = Rounded(br, 9)) using (var pen = new Pen(P.PillBorder)) g.DrawPath(pen, path);
                Text(g, b, Mono(10.5f), P.Muted, br, StringAlignment.Center);
            }
            Text(g, (title ?? "").ToUpperInvariant(), fb, P.Ink, new RectangleF(x + nw, y, width - nw - bw - 8, 20));
            return 20;
        }

        // Pill button. active = accent border + accent text on accent-bg.
        internal static void Pill(Graphics g, RectangleF r, string text, bool active, bool hover, bool pressed = false, bool enabled = true)
        {
            using (var path = Rounded(r, 14))
            {
                Color fill = active ? P.AccentBg : pressed ? P.PillPressed : P.PillBg;
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                Color border = active ? P.Accent : hover && enabled ? P.PillHover : P.PillBorder;
                using (var pen = new Pen(border)) g.DrawPath(pen, path);
            }
            Color ink = !enabled ? P.Dim : active ? P.Accent : P.Ink;
            Text(g, text, Mono(12), ink, new RectangleF(r.X + 8, r.Y, r.Width - 16, r.Height), StringAlignment.Center);
        }

        // Toggle switch 46 x 24: three dim dots in the track, knob left (off, muted) or right (on, accent).
        internal static void Switch(Graphics g, RectangleF r, bool on, bool hover)
        {
            var track = new RectangleF(r.X, r.Y + (r.Height - 24) / 2, 46, 24);
            using (var path = Rounded(track, 12))
            {
                using (var b = new SolidBrush(Color.FromArgb(hover ? 217 : 255, on ? P.AccentBg : P.PillBg))) g.FillPath(b, path);
                using (var pen = new Pen(on ? P.Accent : P.PillBorder)) g.DrawPath(pen, path);
            }
            using (var dim = new SolidBrush(P.Dim))
                for (int i = -1; i <= 1; i++) g.FillEllipse(dim, track.X + 23 - 1.2f + i * 6.4f, track.Y + 12 - 1.2f, 2.4f, 2.4f);
            float kx = on ? track.Right - 4 - 16 : track.X + 4;
            using (var knob = new SolidBrush(on ? P.Accent : P.Muted)) g.FillEllipse(knob, kx, track.Y + 4, 16, 16);
        }

        // Status dot with a soft glow (box-shadow 0 0 8px currentColor).
        internal static void StatusDot(Graphics g, float cx, float cy, Color color)
        {
            for (int i = 3; i >= 1; i--)
                using (var glow = new SolidBrush(Color.FromArgb(22 * i, color))) g.FillEllipse(glow, cx - 3.5f - i * 2, cy - 3.5f - i * 2, 7 + i * 4, 7 + i * 4);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, cx - 3.5f, cy - 3.5f, 7, 7);
        }

        // ---------- dot renderers (ported from DotBar / DotRing / DotSparkline / DotMatrixText)

        internal static double Fraction(double value, double min, double max)
        {
            double span = max - min;
            if (span <= 0 || double.IsNaN(value)) return 0;
            return Math.Max(0, Math.Min(1, (value - min) / span));
        }

        private static void Dot(Graphics g, Color c, float cx, float cy, float r)
        {
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
        }

        // Bar: dot 3.4 / pitch 7 (compact: 3 / 6). Lit dots past a threshold take its colour; ticks above/below.
        internal static void Bar(Graphics g, RectangleF r, double value, double min, double max,
            double warning = double.NaN, double critical = double.NaN, double floor = double.NaN,
            float pitch = 7, float dotSize = 3.4f, Color? fill = null)
        {
            if (r.Width <= dotSize) return;
            Color normal = fill ?? P.Ink;
            pitch = Math.Max(dotSize + 1, pitch);
            int count = Math.Max(2, (int)Math.Floor((r.Width - dotSize) / pitch) + 1);
            double span = max - min;
            int lit = (int)Math.Round(Fraction(value, min, max) * count);
            if (lit == 0 && value > min) lit = 1;
            float radius = dotSize / 2, cy = r.Y + r.Height / 2;
            for (int i = 0; i < count; i++)
            {
                float cx = r.X + radius + i * pitch;
                if (i < lit) Dot(g, P.ForValue(min + (i + 1d) / count * span, warning, critical, normal), cx, cy, radius);
                else Dot(g, P.Unlit, cx, cy, radius * 0.62f);
            }
            float track = (count - 1) * pitch;
            Marker(g, floor, P.Crit, r.X, track, radius, cy, min, max);
            Marker(g, warning, P.Warn, r.X, track, radius, cy, min, max);
            Marker(g, critical, P.Crit, r.X, track, radius, cy, min, max);
        }

        private static void Marker(Graphics g, double value, Color c, float x0, float track, float radius, float cy, double min, double max)
        {
            if (double.IsNaN(value) || value < min || value > max) return;
            float x = x0 + radius + (float)Fraction(value, min, max) * track;
            using (var b = new SolidBrush(c))
            {
                g.FillRectangle(b, x - 0.9f, cy - radius - 5, 1.8f, 3.2f);
                g.FillRectangle(b, x - 0.9f, cy + radius + 1.8f, 1.8f, 3.2f);
            }
        }

        // Ring: 300° arc from 120°, 56 dots, outer faint ring of 90 dots with threshold dots.
        internal static void Ring(Graphics g, RectangleF r, double value, double min, double max,
            double warning = double.NaN, double critical = double.NaN, float dotSize = 4.4f, int dotCount = 56, Color? fill = null)
        {
            float size = Math.Min(r.Width, r.Height);
            if (size <= 0) return;
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, outer = size / 2 - 3, radius = outer - 10;
            for (int i = 0; i < 90; i++) { PointF p = At(cx, cy, outer, i * 4); Dot(g, P.Faint, p.X, p.Y, 0.9f); }
            int count = Math.Max(8, dotCount);
            double span = max - min;
            int lit = (int)Math.Round(Fraction(value, min, max) * count);
            if (lit == 0 && value > min) lit = 1;
            float dot = dotSize / 2;
            for (int i = 0; i < count; i++)
            {
                PointF p = At(cx, cy, radius, 120 + 300.0 * i / (count - 1));
                if (i < lit) Dot(g, P.ForValue(min + (i + 1d) / count * span, warning, critical, fill ?? P.Ink), p.X, p.Y, dot);
                else Dot(g, P.Unlit, p.X, p.Y, dot * 0.55f);
            }
            RingMarker(g, cx, cy, outer, warning, P.Warn, min, max);
            RingMarker(g, cx, cy, outer, critical, P.Crit, min, max);
        }

        private static void RingMarker(Graphics g, float cx, float cy, float radius, double value, Color c, double min, double max)
        {
            if (double.IsNaN(value) || value < min || value > max) return;
            PointF p = At(cx, cy, radius, 120 + 300 * Fraction(value, min, max));
            Dot(g, c, p.X, p.Y, 2.4f);
        }

        private static PointF At(float cx, float cy, float radius, double degrees)
        {
            double rad = degrees * Math.PI / 180;
            return new PointF(cx + radius * (float)Math.Cos(rad), cy + radius * (float)Math.Sin(rad));
        }

        // Sparkline: faint baseline dots every 6 px, dotted path (step 4.5), accent ring on the last point.
        internal static void Sparkline(Graphics g, RectangleF r, IList<double> values, double min = double.NaN, double max = double.NaN)
        {
            if (r.Width <= 16 || r.Height <= 12) return;
            for (float x = 2; x < r.Width - 2; x += 6) Dot(g, P.Faint, r.X + x, r.Bottom - 1.5f, 0.9f);
            if (values == null || values.Count == 0) return;
            double lo = min, hi = max;
            if (double.IsNaN(lo)) { lo = double.MaxValue; foreach (double v in values) lo = Math.Min(lo, v); }
            if (double.IsNaN(hi)) { hi = double.MinValue; foreach (double v in values) hi = Math.Max(hi, v); }
            if (hi - lo < 1e-6) hi = lo + 1;
            float top = r.Y + 8, bottom = r.Bottom - 6, left = r.X + 3, right = r.Right - 10;
            var pts = new PointF[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                float x = values.Count == 1 ? right : left + (right - left) * i / (values.Count - 1);
                pts[i] = new PointF(x, bottom - (float)Fraction(values[i], lo, hi) * (bottom - top));
            }
            const float step = 4.5f;
            float carry = 0;
            using (var ink = new SolidBrush(P.Ink))
                for (int i = 1; i < pts.Length; i++)
                {
                    PointF a = pts[i - 1], b = pts[i];
                    float length = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)), pos = carry;
                    while (pos <= length)
                    {
                        float t = length == 0 ? 0 : pos / length;
                        g.FillEllipse(ink, a.X + (b.X - a.X) * t - 1.15f, a.Y + (b.Y - a.Y) * t - 1.15f, 2.3f, 2.3f);
                        pos += step;
                    }
                    carry = pos - length;
                }
            PointF last = pts[pts.Length - 1];
            using (var pen = new Pen(P.Accent, 1.2f)) g.DrawEllipse(pen, last.X - 6, last.Y - 6, 12, 12);
            Dot(g, P.Accent, last.X, last.Y, 2.8f);
        }

        // ---------- 5x7 dot-matrix text
        private static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]> {
            {'0', new[] { "01110", "10001", "10011", "10101", "11001", "10001", "01110" }},
            {'1', new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" }},
            {'2', new[] { "01110", "10001", "00001", "00010", "00100", "01000", "11111" }},
            {'3', new[] { "11111", "00010", "00100", "00010", "00001", "10001", "01110" }},
            {'4', new[] { "00010", "00110", "01010", "10010", "11111", "00010", "00010" }},
            {'5', new[] { "11111", "10000", "11110", "00001", "00001", "10001", "01110" }},
            {'6', new[] { "00110", "01000", "10000", "11110", "10001", "10001", "01110" }},
            {'7', new[] { "11111", "00001", "00010", "00100", "01000", "01000", "01000" }},
            {'8', new[] { "01110", "10001", "10001", "01110", "10001", "10001", "01110" }},
            {'9', new[] { "01110", "10001", "10001", "01111", "00001", "00010", "01100" }},
            {'A', new[] { "01110", "10001", "10001", "11111", "10001", "10001", "10001" }},
            {'B', new[] { "11110", "10001", "10001", "11110", "10001", "10001", "11110" }},
            {'C', new[] { "01110", "10001", "10000", "10000", "10000", "10001", "01110" }},
            {'D', new[] { "11100", "10010", "10001", "10001", "10001", "10010", "11100" }},
            {'E', new[] { "11111", "10000", "10000", "11110", "10000", "10000", "11111" }},
            {'F', new[] { "11111", "10000", "10000", "11110", "10000", "10000", "10000" }},
            {'G', new[] { "01110", "10001", "10000", "10111", "10001", "10001", "01111" }},
            {'H', new[] { "10001", "10001", "10001", "11111", "10001", "10001", "10001" }},
            {'I', new[] { "01110", "00100", "00100", "00100", "00100", "00100", "01110" }},
            {'J', new[] { "00111", "00010", "00010", "00010", "00010", "10010", "01100" }},
            {'K', new[] { "10001", "10010", "10100", "11000", "10100", "10010", "10001" }},
            {'L', new[] { "10000", "10000", "10000", "10000", "10000", "10000", "11111" }},
            {'M', new[] { "10001", "11011", "10101", "10101", "10001", "10001", "10001" }},
            {'N', new[] { "10001", "10001", "11001", "10101", "10011", "10001", "10001" }},
            {'O', new[] { "01110", "10001", "10001", "10001", "10001", "10001", "01110" }},
            {'P', new[] { "11110", "10001", "10001", "11110", "10000", "10000", "10000" }},
            {'Q', new[] { "01110", "10001", "10001", "10001", "10101", "10010", "01101" }},
            {'R', new[] { "11110", "10001", "10001", "11110", "10100", "10010", "10001" }},
            {'S', new[] { "01111", "10000", "10000", "01110", "00001", "00001", "11110" }},
            {'T', new[] { "11111", "00100", "00100", "00100", "00100", "00100", "00100" }},
            {'U', new[] { "10001", "10001", "10001", "10001", "10001", "10001", "01110" }},
            {'V', new[] { "10001", "10001", "10001", "10001", "10001", "01010", "00100" }},
            {'W', new[] { "10001", "10001", "10001", "10101", "10101", "10101", "01010" }},
            {'X', new[] { "10001", "10001", "01010", "00100", "01010", "10001", "10001" }},
            {'Y', new[] { "10001", "10001", "10001", "01010", "00100", "00100", "00100" }},
            {'Z', new[] { "11111", "00001", "00010", "00100", "01000", "10000", "11111" }},
            {'%', new[] { "11000", "11001", "00010", "00100", "01000", "10011", "00011" }},
            {'+', new[] { "00000", "00100", "00100", "11111", "00100", "00100", "00000" }},
            {'/', new[] { "00000", "00001", "00010", "00100", "01000", "10000", "00000" }},
            {'_', new[] { "00000", "00000", "00000", "00000", "00000", "00000", "11111" }},
            {'-', new[] { "000", "000", "000", "111", "000", "000", "000" }},
            {'°', new[] { "010", "101", "010", "000", "000", "000", "000" }},
            {'.', new[] { "0", "0", "0", "0", "0", "0", "1" }},
            {':', new[] { "0", "1", "0", "0", "0", "1", "0" }},
            {' ', new[] { "000", "000", "000", "000", "000", "000", "000" }},
        };

        private static string[] Glyph(char c)
        {
            string[] glyph;
            return Glyphs.TryGetValue(char.ToUpperInvariant(c), out glyph) ? glyph : Glyphs[' '];
        }

        internal static SizeF MeasureDotText(string text, float dotSize, float gap)
        {
            float pitch = dotSize + gap;
            int columns = 0;
            foreach (char c in text ?? "") columns += Glyph(c)[0].Length + 1;
            if (columns > 0) columns--;
            return new SizeF(columns == 0 ? 0 : columns * pitch - gap, 7 * pitch - gap);
        }

        internal static void DotText(Graphics g, string text, float x, float y, float dotSize, float gap, Color color)
        {
            float pitch = dotSize + gap, radius = dotSize / 2;
            using (var b = new SolidBrush(color))
                foreach (char c in text ?? "")
                {
                    string[] glyph = Glyph(c);
                    for (int row = 0; row < 7; row++)
                        for (int col = 0; col < glyph[row].Length; col++)
                            if (glyph[row][col] == '1') g.FillEllipse(b, x + col * pitch, y + row * pitch, dotSize, dotSize);
                    x += (glyph[0].Length + 1) * pitch;
                }
        }

        // Scale rule for unbounded values: a nice step at or above peak x 1.15 (MiB steps for VRAM rows).
        internal static double NiceMax(double peak)
        {
            double[] steps = { 64, 128, 256, 512, 1024, 2048, 3072, 4096, 6144, 8192, 12288, 16384, 24576, 32768 };
            double target = peak * 1.15;
            foreach (double s in steps) if (s >= target) return s;
            return Math.Ceiling(target / 8192) * 8192;
        }
    }
}
