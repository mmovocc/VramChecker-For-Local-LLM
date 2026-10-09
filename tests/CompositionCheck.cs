using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GpuVramWidget;

internal sealed class CompositionCheck : Form
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr hwnd,out uint value);
    [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr hwnd,IntPtr region);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l,int t,int r,int b);
    [DllImport("gdi32.dll")] static extern int GetRgnBox(IntPtr region,out Rect box);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [StructLayout(LayoutKind.Sequential)] struct Rect { internal int Left,Top,Right,Bottom; }
    readonly Timer timer = new Timer { Interval=30 };
    GlassRenderer renderer;
    int tick;
    readonly string directory=AppDomain.CurrentDomain.BaseDirectory;
    string failure;
    protected override CreateParams CreateParams { get { var cp=base.CreateParams; cp.ExStyle|=0x00200000; return cp; } }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    CompositionCheck()
    {
        FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; ClientSize=new Size(380,206);
        StartPosition=FormStartPosition.Manual;
        Location=new Point(Screen.PrimaryScreen.Bounds.Left+100,Screen.PrimaryScreen.Bounds.Top+100);
        timer.Tick+=Step;
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try {
            Options.Glass=true; Options.SampleHz=15; Options.DragBoost=true; Options.SyncDrag=true;
            renderer=new GlassRenderer(Handle);
            using(var bitmap=new Bitmap(380,206)) {
                using(var g=Graphics.FromImage(bitmap)) g.DrawString("Composition integration check",SystemFonts.DefaultFont,Brushes.Black,20,30);
                renderer.Upload(bitmap);
            }
            renderer.Trace(Path.Combine(directory,"composition-latency.csv"));
            timer.Start();
        } catch(Exception ex) { failure=ex.ToString(); Close(); }
    }
    void Step(object sender,EventArgs e)
    {
        try {
            tick++;
            bool burst=tick>=110 && tick<230;
            bool drag=(tick>=20 && tick<=85) || (burst && (tick-110)%10<6);
            Options.SyncDrag=tick<=70 || burst;
            Options.PredictionMs=tick>=45 ? 4 : 0;
            if(drag) { renderer.PrepareDrag(); Location=new Point(Screen.PrimaryScreen.Bounds.Left+100+(burst ? (tick%6)*100 : (tick-20)*5),Screen.PrimaryScreen.Bounds.Top+100); }
            if(tick==55) {
                ClientSize=new Size(520,280);
                using(var bitmap=new Bitmap(520,280)) {
                    using(var g=Graphics.FromImage(bitmap)) g.DrawString("Resized synchronized surface",SystemFonts.DefaultFont,Brushes.Black,20,30);
                    renderer.Upload(bitmap);
                }
            }
            renderer.Submit(drag,true); renderer.Render();
            IntPtr host=renderer.CompositionWindowHandle;
            if(tick==40) {
                if(!IsWindowVisible(host)) throw new Exception("Synchronized host was not shown");
                uint affinity;
                if(!GetWindowDisplayAffinity(host,out affinity) || affinity!=0x11) throw new Exception("Host capture exclusion missing");
                renderer.Save(Path.Combine(directory,"composition-preview.bmp"),false);
            }
            if(tick==95 && IsWindowVisible(host)) throw new Exception("Synchronized host remained visible after release");
            if(burst && (tick-110)%10==9) {
                if(!IsWindowVisible(host)) throw new Exception("Persistent host hidden on rapid release");
                IntPtr region=CreateRectRgn(0,0,0,0);
                try {
                    Rect box;
                    if(GetWindowRgn(host,region)==0 || GetRgnBox(region,out box)==0 ||
                        box.Right-box.Left!=ClientSize.Width || box.Bottom-box.Top!=ClientSize.Height)
                        throw new Exception("Persistent host region not constrained after release");
                } finally { DeleteObject(region); }
                renderer.Save(Path.Combine(directory,"handoff-"+tick+".bmp"),false);
            }
            if(tick==65) renderer.Save(Path.Combine(directory,"composition-resized.bmp"),false);
            if(tick>=235) Close();
        } catch(Exception ex) { failure=ex.ToString(); Close(); }
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop(); timer.Dispose();
        if(renderer!=null) renderer.Dispose();
        File.WriteAllText(Path.Combine(directory,"composition-test-result.txt"),failure ?? "PASS: real D3D11 surface update, synchronized host show/hide, capture exclusion, mode transition, prediction and trace shutdown.");
        if(failure!=null) Environment.ExitCode=1;
        base.OnFormClosed(e);
    }
    [STAThread] static void Main() { SetProcessDPIAware(); Application.Run(new CompositionCheck()); }
}
