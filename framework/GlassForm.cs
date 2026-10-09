using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace LiquidGlass
{
    // Owner-drawn foreground: native child controls are not composited into this surface.
    public class GlassForm : Form
    {
        public GlassOptions Glass { get; private set; }
        public event EventHandler GlassFailed;
        public Exception LastGlassError { get; private set; }
        private GlassRenderer _renderer;
        private readonly Timer _poll = new Timer { Interval = 33 };
        private bool _dragging, _foregroundDirty = true, _started;
        private Point _dragOffset;
        private DateTime _retryAt;
        public GlassForm()
        {
            Glass = new GlassOptions();
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(400,240);
            _poll.Tick += Poll;
        }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x00200000; return cp; }
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
        // Override to draw text, icons and interactive regions in client coordinates.
        protected virtual void DrawGlassForeground(Graphics graphics) { }
        // Override to exclude buttons or other interactive regions from window dragging.
        protected virtual bool IsGlassDragArea(Point point) { return true; }
        public void InvalidateGlass() { _foregroundDirty = true; }
        public void ApplyGlassOptions()
        {
            Glass.Validate();
            if (_renderer != null) _renderer.Submit(_dragging,true);
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e); _started = true; _poll.Start(); Poll(this,EventArgs.Empty);
        }
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!_started) return;
            if (!Visible) { _poll.Stop(); ReleaseRenderer(); }
            else { _foregroundDirty=true; _poll.Start(); }
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); _foregroundDirty=true; }
        private void Poll(object sender,EventArgs e)
        {
            if (!Visible || !IsHandleCreated || DateTime.UtcNow < _retryAt) return;
            try {
                if (_renderer == null) { _renderer = new GlassRenderer(Handle,Glass); _foregroundDirty=true; }
                if (_foregroundDirty && ClientSize.Width>0 && ClientSize.Height>0) {
                    var image = new Bitmap(ClientSize.Width,ClientSize.Height,PixelFormat.Format32bppPArgb);
                    try { using (var graphics = Graphics.FromImage(image)) DrawGlassForeground(graphics); }
                    catch { image.Dispose(); throw; }
                    _renderer.UploadOwned(image);
                    _foregroundDirty=false;
                }
                _renderer.Submit(_dragging,false);
                if (_renderer.Render()==2) { ReleaseRenderer(); _foregroundDirty=true; }
            } catch (Exception ex) {
                LastGlassError=ex; ReleaseRenderer(); _retryAt=DateTime.UtcNow.AddSeconds(2);
                var handler=GlassFailed; if(handler!=null) handler(this,EventArgs.Empty);
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if(e.Button==MouseButtons.Left && IsGlassDragArea(e.Location)) {
                _dragging=true; _dragOffset=e.Location; Capture=true;
                if(_renderer!=null) _renderer.PrepareDrag();
            }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if(_dragging) {
                if(_renderer!=null) _renderer.PrepareDrag();
                Point cursor=Cursor.Position; Location=new Point(cursor.X-_dragOffset.X,cursor.Y-_dragOffset.Y);
                if(_renderer!=null) _renderer.Submit(true,true);
            }
        }
        private void EndDrag()
        {
            _dragging=false;
            if(_renderer!=null) _renderer.Submit(false,true);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if(e.Button==MouseButtons.Left) { EndDrag(); Capture=false; }
            base.OnMouseUp(e);
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e); if(!Capture) EndDrag();
        }
        private void ReleaseRenderer() { if(_renderer!=null) { _renderer.Dispose(); _renderer=null; } }
        protected override void OnHandleDestroyed(EventArgs e) { ReleaseRenderer(); base.OnHandleDestroyed(e); }
        protected override void Dispose(bool disposing)
        {
            if(disposing) { _poll.Stop(); _poll.Dispose(); ReleaseRenderer(); }
            base.Dispose(disposing);
        }
    }
}
