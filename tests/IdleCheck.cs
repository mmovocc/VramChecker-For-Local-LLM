using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiquidGlass;

internal sealed class IdleCheck : Form
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    private GlassRenderer renderer;
    private readonly Timer timer=new Timer { Interval=100 };
    private readonly Form backdrop=new Form { FormBorderStyle=FormBorderStyle.None, ShowInTaskbar=false, BackColor=Color.SlateGray };
    private int tick; private string result=""; private ulong[] before;
    protected override CreateParams CreateParams { get { var cp=base.CreateParams; cp.ExStyle|=0x00200000; return cp; } }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    private ulong[] Counts()
    {
        string[] parts=renderer.Counters().Split(','); var values=new ulong[parts.Length];
        for(int i=0;i<parts.Length;i++) values[i]=ulong.Parse(parts[i].Split('=')[1]); return values;
    }
    private void Upload()
    {
        using(var b=new Bitmap(380,206)) { using(var g=Graphics.FromImage(b)) g.DrawString("Fixed 30 Hz check",SystemFonts.DefaultFont,Brushes.Black,20,20); renderer.Upload(b); }
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try {
            renderer=new GlassRenderer(Handle,new GlassOptions { SampleHz=30 }); Upload(); timer.Start();
        } catch(Exception ex) { result=ex.ToString(); Environment.ExitCode=1; Close(); }
    }
    private IdleCheck()
    {
        backdrop.Bounds=Screen.PrimaryScreen.Bounds; backdrop.Show();
        FormBorderStyle=FormBorderStyle.None; ClientSize=new Size(380,206); StartPosition=FormStartPosition.Manual;
        Location=new Point(backdrop.Left+300,backdrop.Top+200); timer.Tick+=delegate {
            try {
                tick++; renderer.Submit(false,false); if(renderer.Render()!=0 && tick>10) throw new Exception("No captured desktop");
                if(tick==10) before=Counts();
                if(tick==30) { var after=Counts(); result="Static 2 seconds: draws="+(after[0]-before[0])+", skips="+(after[1]-before[1])+", copies="+(after[2]-before[2])+Environment.NewLine; before=after; }
                if(tick>=31 && tick<=40) backdrop.BackColor= tick%2==0 ? Color.SlateGray : Color.CadetBlue;
                if(tick==43) { var after=Counts(); ulong draws=after[0]-before[0]; if(draws<5) throw new Exception("Changed backdrop did not redraw"); result+="Changing background: draws="+draws+Environment.NewLine; before=after; Upload(); }
                if(tick==46) { if(Counts()[0]<=before[0]) throw new Exception("Foreground upload did not redraw"); result+="PASS: fixed cadence, changed desktop and foreground updates."; Close(); }
            } catch(Exception ex) { result+=ex; Environment.ExitCode=1; Close(); }
        };
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose(); if(renderer!=null) renderer.Dispose(); backdrop.Dispose();
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"idle-result.txt"),result); base.OnFormClosed(e);
    }
    [STAThread] static void Main() { SetProcessDPIAware(); Application.Run(new IdleCheck()); }
}
