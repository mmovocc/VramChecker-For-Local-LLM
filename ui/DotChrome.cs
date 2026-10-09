using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GpuVramWidget
{
    // Menus and dialogs in the dot-matrix style: mono type, palette surfaces, accent dot for checked items.
    internal static class DotChrome
    {
        internal static ContextMenuStrip Menu()
        {
            var menu = new ContextMenuStrip { Renderer = new MenuRenderer(), ShowImageMargin = true, Font = DotUi.Mono(12), Padding = new Padding(2, 6, 2, 6) };
            menu.Opening += delegate { Style(menu.Items); };
            return menu;
        }

        // Palette may change between openings (material switch), so styles are applied on open.
        private static void Style(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.ForeColor = DotUi.P.Ink;
                item.Font = DotUi.For(item.Text, DotUi.Mono(12));
                item.Padding = new Padding(4, 3, 8, 3);
                var mi = item as ToolStripMenuItem;
                if (mi != null && mi.HasDropDownItems)
                {
                    var drop = mi.DropDown as ToolStripDropDownMenu;
                    if (drop != null) { drop.Renderer = new MenuRenderer(); drop.ShowImageMargin = true; drop.Padding = new Padding(2, 6, 2, 6); }
                    Style(mi.DropDownItems);
                }
            }
        }

        private sealed class MenuRenderer : ToolStripRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var b = new SolidBrush(DotUi.P.CardTop)) e.Graphics.FillRectangle(b, e.AffectedBounds);
            }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                using (var pen = new Pen(DotUi.P.PillBorder)) e.Graphics.DrawRectangle(pen, r);
            }
            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                var g = e.Graphics; DotUi.Prepare(g);
                var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
                using (var path = DotUi.Rounded(r, 8))
                {
                    using (var b = new SolidBrush(DotUi.P.PillBg)) g.FillPath(b, path);
                    using (var pen = new Pen(DotUi.P.PillBorder)) g.DrawPath(pen, path);
                }
            }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = !e.Item.Enabled ? DotUi.P.Dim : e.Item.Selected ? DotUi.P.Accent : DotUi.P.Ink;
                e.TextFont = DotUi.For(e.Text, DotUi.Mono(12));
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                base.OnRenderItemText(e);
            }
            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var g = e.Graphics; DotUi.Prepare(g);
                float cx = e.ImageRectangle.X + e.ImageRectangle.Width / 2f, cy = e.ImageRectangle.Y + e.ImageRectangle.Height / 2f;
                using (var b = new SolidBrush(DotUi.P.Accent)) g.FillEllipse(b, cx - 3.5f, cy - 3.5f, 7, 7);
            }
            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                float y = e.Item.Height / 2f;
                using (var b = new SolidBrush(DotUi.P.Unlit))
                    for (float x = 12; x < e.Item.Width - 10; x += 6) e.Graphics.FillEllipse(b, x - 0.9f, y - 0.9f, 1.8f, 1.8f);
            }
            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                var g = e.Graphics; DotUi.Prepare(g);
                float cx = e.ArrowRectangle.X + e.ArrowRectangle.Width / 2f, cy = e.ArrowRectangle.Y + e.ArrowRectangle.Height / 2f;
                using (var pen = new Pen(DotUi.P.Muted, 1.4f)) g.DrawLines(pen, new[] { new PointF(cx - 2, cy - 4), new PointF(cx + 2, cy), new PointF(cx - 2, cy + 4) });
            }
        }

        // Windows 11 rounds the window and draws its 1 px border in the palette's pill-border colour, so the
        // window itself paints a plain rectangle (an inner rounded card would leave seams at the corners).
        // Returns false where DWM cannot do this (Windows 10): the caller then draws a square border.
        internal static bool ApplyWindowTheme(IntPtr hwnd)
        {
            try
            {
                int dark = DotUi.P.IsDark ? 1 : 0; DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
                int corner = 2;                                     // DWMWCP_ROUND
                int border = ColorRef(DotUi.P.PillBorder);          // DWMWA_BORDER_COLOR
                return DwmSetWindowAttribute(hwnd, 33, ref corner, 4) == 0 && DwmSetWindowAttribute(hwnd, 34, ref border, 4) == 0;
            }
            catch { return false; }
        }

        private static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }

    // Small owner-drawn modal: title, message, a row of pills. Replaces MessageBox and the old rate dialog.
    internal sealed class DotDialog : Form
    {
        private readonly string _title, _message;
        private readonly List<KeyValuePair<string, int>> _buttons = new List<KeyValuePair<string, int>>();
        private readonly List<RectangleF> _rects = new List<RectangleF>();
        private readonly int _primary;
        private int _hover = -1;
        private bool _dwmFrame;
        internal int Result = -1;

        private DotDialog(string title, string message, int primary, params KeyValuePair<string, int>[] buttons)
        {
            _title = title; _message = message; _primary = primary;
            _buttons.AddRange(buttons);
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; KeyPreview = true; DoubleBuffered = true;
            // Always above the widget: with "Always on top" the widget and its glass host sit in the topmost band.
            TopMost = true;
            BackColor = DotUi.P.CardBottom;
            using (var g = CreateGraphics())
            {
                float width = 420, buttonsWidth = 0;
                foreach (var b in _buttons) buttonsWidth += Math.Max(84, DotUi.Measure(g, b.Key, DotUi.Mono(12)) + 32) + 8;
                width = Math.Max(width, buttonsWidth + 44);
                var lines = Wrap(g, message, width - 48);
                ClientSize = new Size((int)width, 64 + lines.Count * 18 + 62);
            }
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { Result = -1; Close(); }
                if (e.KeyCode == Keys.Enter && _primary >= 0) { Result = _primary; Close(); }
            };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); _dwmFrame = DotChrome.ApplyWindowTheme(Handle); }

        protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); }

        internal static bool Confirm(IWin32Window owner, string title, string message, string ok)
        {
            using (var d = new DotDialog(title, message, 1, new KeyValuePair<string, int>("Cancel", 0), new KeyValuePair<string, int>(ok, 1)))
            {
                d.ShowDialog(owner);
                return d.Result == 1;
            }
        }

        internal static void Info(IWin32Window owner, string title, string message)
        {
            using (var d = new DotDialog(title, message, 0, new KeyValuePair<string, int>("OK", 0))) d.ShowDialog(owner);
        }

        // Fixed background sampling rate: returns Hz, 0 for automatic, -1 when cancelled.
        internal static int ChooseRate(IWin32Window owner, int current)
        {
            var options = new List<KeyValuePair<string, int>> { new KeyValuePair<string, int>("Auto", 0) };
            foreach (int hz in new[] { 5, 10, 15, 30, 60, 120 }) options.Add(new KeyValuePair<string, int>(hz + " Hz", hz));
            options.Add(new KeyValuePair<string, int>("Cancel", -1));
            using (var d = new DotDialog("Sampling rate",
                "Background capture and glass redraw share one clock. Auto adapts to activity; a fixed rate is for testing. Current: "
                + (current > 0 ? current + " Hz" : "Auto") + ".", current, options.ToArray()))
            {
                d.ShowDialog(owner);
                return d.Result;
            }
        }

        private static List<string> Wrap(Graphics g, string text, float width)
        {
            var lines = new List<string>();
            foreach (string para in (text ?? "").Split('\n'))
            {
                string line = "";
                foreach (string word in para.Split(' '))
                {
                    string next = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && DotUi.Measure(g, next, DotUi.Mono(12)) > width) { lines.Add(line); line = word; }
                    else line = next;
                }
                lines.Add(line);
            }
            return lines;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; DotUi.Prepare(g);
            var p = DotUi.P;
            using (var fill = new LinearGradientBrush(ClientRectangle, p.CardTop, p.CardBottom, LinearGradientMode.Vertical)) g.FillRectangle(fill, ClientRectangle);
            if (!_dwmFrame) using (var pen = new Pen(p.PillBorder)) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            DotUi.CardHead(g, 24, 20, ClientSize.Width - 48, "", _title, null);
            var lines = Wrap(g, _message, ClientSize.Width - 48);
            for (int i = 0; i < lines.Count; i++) DotUi.Text(g, lines[i], DotUi.Mono(12), p.Muted, new RectangleF(24, 50 + i * 18, ClientSize.Width - 48, 18));
            _rects.Clear();
            float x = ClientSize.Width - 22, y = ClientSize.Height - 22 - 30;
            for (int i = _buttons.Count - 1; i >= 0; i--)
            {
                float w = Math.Max(84, DotUi.Measure(g, _buttons[i].Key, DotUi.Mono(12)) + 32);
                x -= w;
                _rects.Insert(0, new RectangleF(x, y, w, 30));
                x -= 8;
            }
            for (int i = 0; i < _buttons.Count; i++)
                DotUi.Pill(g, _rects[i], _buttons[i].Key, _buttons[i].Value == _primary, _hover == i);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hover = _rects.FindIndex(delegate (RectangleF r) { return r.Contains(e.Location); });
            if (hover != _hover) { _hover = hover; Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int hit = _rects.FindIndex(delegate (RectangleF r) { return r.Contains(e.Location); });
            if (hit >= 0) { Result = _buttons[hit].Value; Close(); }
        }

        // Drag anywhere outside buttons.
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _rects.FindIndex(delegate (RectangleF r) { return r.Contains(e.Location); }) < 0)
            { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }
        }
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    }
}
