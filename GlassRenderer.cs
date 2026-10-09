using System;
using System.Drawing;
namespace GpuVramWidget
{
    // Widget preferences stay here; the reusable renderer has no VRAM dependency.
    internal sealed class GlassRenderer : IDisposable
    {
        private readonly LiquidGlass.GlassOptions _options = new LiquidGlass.GlassOptions();
        private readonly LiquidGlass.GlassRenderer _renderer;
        internal IntPtr CompositionWindowHandle { get { return _renderer.CompositionWindowHandle; } }
        internal GlassRenderer(IntPtr hwnd) { ReadOptions(); _renderer = new LiquidGlass.GlassRenderer(hwnd, _options); }
        private void ReadOptions()
        {
            _options.CornerRadius=(float)Options.CornerRadius; _options.Refraction=Options.Refraction;
            _options.Blur=Options.Blur; _options.Tint=Options.Tint; _options.Fps=Options.Fps;
            _options.SampleHz=Options.SampleHz; _options.Economy=Options.Economy;
            _options.DragBoost=Options.DragBoost; _options.SyncDrag=Options.SyncDrag; _options.PredictionMs=Options.PredictionMs;
            _options.Material=(int)AppSettings.Material;
        }
        internal void Submit(bool dragging,bool interaction) { ReadOptions(); _renderer.Submit(dragging,interaction); }
        internal void PrepareDrag() { _renderer.PrepareDrag(); }
        internal void Upload(Bitmap image) { _renderer.Upload(image); }
        internal int Render() { return _renderer.Render(); }
        internal int Interval { get { return _renderer.Interval; } }
        internal string Status { get { return _renderer.Status; } }
        internal string Counters() { return _renderer.Counters(); }
        internal void Trace(string path) { _renderer.Trace(path); }
        internal void Save(string path,bool desktop) { _renderer.Save(path,desktop); }
        internal void Compare(string directory) { _renderer.Compare(directory); }
        public void Dispose() { _renderer.Dispose(); }
    }
}
