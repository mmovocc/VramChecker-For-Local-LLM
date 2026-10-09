using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiquidGlass;

internal sealed class FrameworkCheck : GlassForm
{
    private int paints;
    protected override void DrawGlassForeground(Graphics g) { paints++; g.DrawString("Reusable glass",SystemFonts.DefaultFont,Brushes.Black,20,20); }
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [STAThread] static void Main()
    {
        SetProcessDPIAware();
        string error=null; int tick=0;
        var context=new ApplicationContext(); var form=new FrameworkCheck();
        form.GlassFailed+=delegate { error=form.LastGlassError.ToString(); };
        var timer=new Timer { Interval=50 };
        timer.Tick+=delegate {
            try {
                tick++;
                if(tick==12) form.ClientSize=new Size(480,260);
                if(tick==20) { form.Glass.SampleHz=15; form.ApplyGlassOptions(); }
                if(tick==30) form.Hide();
                if(tick==40) form.Show();
                if(tick==55) { form.InvalidateGlass(); }
                if(tick==70) {
                    if(form.paints<3) throw new Exception("Foreground lifecycle not exercised");
                    if(error!=null) throw new Exception(error);
                    timer.Stop(); form.Dispose(); context.ExitThread();
                }
            } catch(Exception ex) { error=ex.ToString(); timer.Stop(); form.Dispose(); context.ExitThread(); }
        };
        form.Show(); timer.Start(); Application.Run(context); timer.Dispose(); context.Dispose();
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"framework-result.txt"),error ?? "PASS: independent GlassForm creation, foreground invalidation, resize, settings, hide/show recreation and disposal.");
        if(error!=null) Environment.ExitCode=1;
    }
}
