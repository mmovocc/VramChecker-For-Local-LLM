using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Windows.Forms;

namespace LiquidGlass
{
    // Only Worker accesses native rendering state. UI submissions replace pending state.
    public sealed class GlassRenderer : IDisposable
    {
        public GlassOptions Options { get; private set; }
        public IntPtr CompositionWindowHandle { get { return _host.Handle; } }
        private IntPtr _native;
        private readonly IntPtr _hwnd;
        private readonly Control _owner;
        private readonly CompositionHost _host;
        private readonly object _gate = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _worker;
        private readonly Queue<Action> _commands = new Queue<Action>();
        private Bitmap _pendingOverlay;
        private Settings _settings;
        private bool _stopping, _interaction, _disposed;
        private Exception _failure;
        private int _result = 1;
        private volatile int _refreshHz = 60;
        private readonly int[] _placement = new int[5];
        private string _status = "等待渲染线程初始化";
        private struct Settings
        {
            internal float Radius, Refraction, Blur, Tint;
            internal int Fps, Economy, SampleHz, Flags;
            internal float PredictionMs;
            internal bool SameAs(Settings o)
            {
                return Radius == o.Radius && Refraction == o.Refraction && Blur == o.Blur && Tint == o.Tint &&
                    Fps == o.Fps && Economy == o.Economy && SampleHz == o.SampleHz && Flags == o.Flags &&
                    PredictionMs == o.PredictionMs;
            }
        }
        // Fixed screen-space host stays attached across mouse release.
        // It shares the UI thread, so HTTRANSPARENT can reach the original window.
        private sealed class CompositionHost : NativeWindow, IDisposable
        {
            private readonly Rectangle _bounds;
            private Rectangle? _region;
            [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l,int t,int r,int b);
            [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
            [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd,IntPtr region,bool redraw);
            [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr hwnd,IntPtr rect);
            internal CompositionHost(IntPtr owner)
            {
                Rectangle bounds = Screen.FromHandle(owner).Bounds;
                _bounds = bounds;
                CreateHandle(new CreateParams { Caption = "LiquidGlass composition host", Parent = owner,
                    Style = unchecked((int)0x80000000), ExStyle = 0x00200000 | 0x08000000 | 0x80 | 0x20,
                    X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height });
            }
            internal void Region(Rectangle? bounds)
            {
                if (_region == bounds) return;
                IntPtr region = IntPtr.Zero;
                if (bounds.HasValue) {
                    Rectangle r = bounds.Value; r.Offset(-_bounds.X,-_bounds.Y);
                    region = CreateRectRgn(r.Left,r.Top,r.Right,r.Bottom);
                    if (region == IntPtr.Zero) return;
                }
                if (SetWindowRgn(Handle,region,false) != 0) _region = bounds;
                else if (region != IntPtr.Zero) DeleteObject(region);
            }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x14) { m.Result = new IntPtr(1); return; }
                if (m.Msg == 0x0F) { ValidateRect(Handle,IntPtr.Zero); m.Result=IntPtr.Zero; return; }
                if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; } // HTTRANSPARENT
                if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
                base.WndProc(ref m);
            }
            public void Dispose() { DestroyHandle(); }
        }
        private sealed class FrameTimer : WaitHandle
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string name, uint flags, uint access);
            [DllImport("kernel32.dll")]
            private static extern bool SetWaitableTimer(Microsoft.Win32.SafeHandles.SafeWaitHandle timer, ref long due,
                int period, IntPtr callback, IntPtr argument, bool resume);
            internal FrameTimer()
            {
                IntPtr handle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x00100002); // synchronize + modify
                if (handle == IntPtr.Zero) handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x00100002);
                SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, true);
            }
            internal void Wait(AutoResetEvent wake, WaitHandle[] handles, double milliseconds)
            {
                long due = -Math.Max(1L, (long)(milliseconds * 10000));
                if (!SafeWaitHandle.IsInvalid && SetWaitableTimer(SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    WaitHandle.WaitAny(handles);
                else wake.WaitOne(Math.Max(1, (int)Math.Ceiling(milliseconds)));
            }
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("LiquidGlass.Native.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern IntPtr GlassCreate(IntPtr hwnd, IntPtr host, string shader);
        [DllImport("LiquidGlass.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GlassDestroy(IntPtr renderer);
        [DllImport("LiquidGlass.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GlassLastError();
        [DllImport("LiquidGlass.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GlassOverlay(IntPtr renderer, IntPtr bits, int width, int height, int stride);
        [DllImport("LiquidGlass.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GlassRender(IntPtr renderer, float radius, float refraction, float blur, float tint, int maxFps, int economy, int sampleHz, int flags, float predictionMs);
        [DllImport("LiquidGlass.Native.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int GlassTrace(IntPtr renderer, string path);
        public void Trace(string path) { Invoke(delegate { Check(GlassTrace(_native, path)); }); }
        [DllImport("LiquidGlass.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GlassRefreshRate(IntPtr renderer);
        [DllImport("LiquidGlass.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GlassPlacement(IntPtr renderer, [Out] int[] values);
        [DllImport("LiquidGlass.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GlassInterval(IntPtr renderer);
        [DllImport("LiquidGlass.Native.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int GlassCompare(IntPtr renderer, string reference, string optimized);
        // This is just the UI status/diagnostic poll, not the render clock.
        public int Interval { get { return 33; } }
        public void Compare(string directory) { Invoke(delegate { Check(GlassCompare(_native, Path.Combine(directory,"reference.bmp"),Path.Combine(directory,"optimized.bmp"))); }); }
        [DllImport("LiquidGlass.Native.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int GlassSave(IntPtr renderer, string path, int source);
        [DllImport("LiquidGlass.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GlassCounters(IntPtr renderer, [Out] ulong[] values);
        public string Counters()
        {
            var v = new ulong[6]; Invoke(delegate { GlassCounters(_native, v); });
            return string.Format("draws={0}, skipped={1}, cropCopies={2}, copiedPixels={3}, cropBytes={4}, geometryBuilds={5}", v[0], v[1], v[2], v[3], v[4],v[5]);
        }

        public GlassRenderer(IntPtr hwnd, GlassOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            options.Validate();
            Options = options;
            if (!SetWindowDisplayAffinity(hwnd, 0x11))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法排除浮窗自身捕获");
            _hwnd = hwnd;
            _owner = Control.FromHandle(hwnd);
            _host = new CompositionHost(hwnd);
            if (!SetWindowDisplayAffinity(_host.Handle, 0x11))
            {
                int error = Marshal.GetLastWin32Error(); _host.Dispose();
                throw new System.ComponentModel.Win32Exception(error, "无法排除玻璃承载窗口捕获");
            }
            Submit(false, false);
            _worker = new Thread(Worker) { IsBackground = true, Name = "LiquidGlass render" };
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
        }
        public void Upload(Bitmap image)
        {
            UploadOwned(image.Clone(new Rectangle(0, 0, image.Width, image.Height), PixelFormat.Format32bppPArgb));
        }
        // Takes ownership of a 32bppPArgb bitmap; avoids a second full copy per foreground update.
        internal void UploadOwned(Bitmap image)
        {
            lock (_gate)
            {
                if (_stopping) { image.Dispose(); return; }
                if (_pendingOverlay != null) _pendingOverlay.Dispose();
                _pendingOverlay = image;
                _wake.Set();
            }
        }
        private void UploadNative(Bitmap image)
        {
            var bits = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { Check(GlassOverlay(_native, bits.Scan0, image.Width, image.Height, bits.Stride)); }
            finally { image.UnlockBits(bits); }
        }
        public int Render()
        {
            lock (_gate)
            {
                if (_failure != null) throw new InvalidOperationException("玻璃渲染失败：" + _failure.Message, _failure);
                return _result;
            }
        }
        public void PrepareDrag() { _host.Region(null); }
        public void Submit(bool dragging, bool interaction)
        {
            Options.Validate();
            Rectangle? region = null;
            lock (_gate) {
                if (!dragging && Options.SyncDrag && _placement[4] != 0) {
                    Rectangle final = new Rectangle(_placement[0],_placement[1],_placement[2],_placement[3]);
                    if (_owner != null && _owner.RectangleToScreen(_owner.ClientRectangle) == final) region = final;
                }
            }
            _host.Region(region);
            int hz = Options.SampleHz;
            if (dragging && Options.DragBoost) hz = Math.Max(Math.Max(60, _refreshHz), Math.Max(hz, Options.Fps));
            var settings = new Settings { Radius = Options.CornerRadius, Refraction = Options.Refraction,
                Blur = Options.Blur, Tint = Options.Tint, Fps = Options.Fps,
                Economy = Options.Economy ? 1 : 0, SampleHz = hz,
                Flags = (dragging ? 1 : 0) | (Options.SyncDrag ? 2 : 0) | (dragging && Options.DragBoost ? 4 : 0) | (Options.Material << 3),
                PredictionMs = Options.PredictionMs };
            lock (_gate)
            {
                if (_stopping) return;
                bool changed = !_settings.SameAs(settings);
                _settings = settings;
                // Fixed-rate mode must not acquire extra frames on mouse events.
                _interaction |= interaction && (hz == 0 || (dragging && Options.DragBoost));
                if (changed || _interaction) _wake.Set();
            }
        }
        private void Worker()
        {
            var clock = Stopwatch.StartNew();
            double deadline = 0, lastStart = -1000;
            int previousHz = -1;
            int previousFlags = 0;
            int[] placement = new int[5];
            FrameTimer frameTimer = null;
            try
            {
                frameTimer = new FrameTimer();
                WaitHandle[] waits = { _wake, frameTimer };
                _native = GlassCreate(_hwnd, _host.Handle, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LiquidGlass.hlsl"));
                if (_native == IntPtr.Zero) throw new InvalidOperationException(Marshal.PtrToStringUni(GlassLastError()));
                _refreshHz = Math.Max(1, GlassRefreshRate(_native));
                while (true)
                {
                    Settings s; bool interaction; Action command = null;
                    lock (_gate)
                    {
                        if (_stopping) break;
                        s = _settings; interaction = _interaction; _interaction = false;
                        if (_commands.Count > 0) command = _commands.Dequeue();
                    }
                    if (command != null) { command(); continue; }
                    double now = clock.Elapsed.TotalMilliseconds;
                    int hz = s.SampleHz > 0 ? s.SampleHz : s.Fps;
                    double period = 1000.0 / Math.Max(1, hz);
                    if (hz != previousHz) { deadline = Math.Max(now, lastStart + period); previousHz = hz; }
                    // Finish a boosted drag immediately, then resume the user's low
                    // rate. Otherwise a 1 Hz setting could leave the drag host up for a second.
                    if ((previousFlags & 4) != 0 && (s.Flags & 4) == 0) deadline = now;
                    previousFlags = s.Flags;
                    if (interaction) deadline = Math.Min(deadline, Math.Max(now, lastStart + period));
                    if (now < deadline) { frameTimer.Wait(_wake, waits, deadline - now); continue; }
                    Bitmap overlay;
                    lock (_gate) { overlay = _pendingOverlay; _pendingOverlay = null; }
                    if (overlay != null) { using (overlay) UploadNative(overlay); }
                    lastStart = clock.Elapsed.TotalMilliseconds;
                    int result = GlassRender(_native, s.Radius, s.Refraction, s.Blur, s.Tint, s.Fps, s.Economy, s.SampleHz, s.Flags, s.PredictionMs);
                    Check(result);
                    GlassPlacement(_native,placement);
                    lock (_gate) { Array.Copy(placement,_placement,5); _result = result; if(result!=0) _status = Marshal.PtrToStringUni(GlassLastError()); }
                    // The UI recreates the device after a monitor/adapter transition.
                    if (result == 2) { _wake.WaitOne(33); continue; }
                    double interval = s.SampleHz > 0 ? period : Math.Max(period, GlassInterval(_native));
                    // Count presentation time inside the frame period, never add it on top.
                    deadline = lastStart + interval;
                    // During boosted dragging the compositor provides the clock. Avoid
                    // a second full-period wait drifting across the next display deadline.
                    // A 2 ms floor prevents spinning if no surface has been shown yet.
                    if ((s.Flags & 4) != 0) deadline = lastStart + Math.Min(period, 2.0);
                    if (deadline < clock.Elapsed.TotalMilliseconds) deadline = clock.Elapsed.TotalMilliseconds;
                }
            }
            catch (Exception ex) { lock (_gate) _failure = ex; }
            finally
            {
                if (frameTimer != null) frameTimer.Dispose();
                if (_native != IntPtr.Zero) { GlassDestroy(_native); _native = IntPtr.Zero; }
                lock (_gate)
                {
                    _stopping = true;
                    if (_pendingOverlay != null) { _pendingOverlay.Dispose(); _pendingOverlay = null; }
                    // Pending diagnostic calls observe shutdown instead of waiting forever.
                    while (_commands.Count > 0) _commands.Dequeue()();
                }
            }
        }
        private void Invoke(Action action)
        {
            Exception error = null;
            using (var done = new ManualResetEvent(false))
            {
                lock (_gate)
                {
                    if (_stopping) throw new InvalidOperationException("渲染线程已停止", _failure);
                    _commands.Enqueue(delegate
                    {
                        try { if (_native == IntPtr.Zero) throw new InvalidOperationException("渲染线程已停止", _failure); action(); }
                        catch (Exception ex) { error = ex; }
                        finally { done.Set(); }
                    });
                    _wake.Set();
                }
                done.WaitOne();
            }
            if (error != null) throw error;
        }
        public string Status { get { lock (_gate) return _status; } }
        public void Save(string path, bool desktop) { Invoke(delegate { Check(GlassSave(_native, path, desktop ? 1 : 0)); }); }
        private static void Check(int result)
        {
            if (result < 0) throw new InvalidOperationException(Marshal.PtrToStringUni(GlassLastError()));
        }
        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; _stopping = true; _wake.Set(); }
            _worker.Join();
            _host.Dispose();
            // All producers are UI-owned and have stopped before Dispose returns.
            _wake.Dispose();
        }
    }
}
