// Local integration fixture: an animated, non-private desktop backdrop.
// Launch via the interactive desktop to exercise real capture rather than a mock texture.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
internal sealed class VisualCheck : Form
{
    private readonly Timer timer = new Timer { Interval = 33 };
    private Process child;
    private int frame;
    private readonly string root = AppDomain.CurrentDomain.BaseDirectory;
    [STAThread] private static void Main() { Application.EnableVisualStyles(); Application.Run(new VisualCheck()); }
    VisualCheck()
    {
        Text = "Liquid Glass — live desktop verification";
        DoubleBuffered = true;
        Size = new Size(760,460);
        StartPosition = FormStartPosition.Manual;
        Rectangle wa=Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right-780, wa.Top);
        timer.Tick += delegate { frame++; if(frame<=60) Invalidate(); if(child!=null && child.HasExited) Close(); if(frame>1800) Close(); };
        Shown += delegate
        {
            child=Process.Start(new ProcessStartInfo(Path.Combine(root,"GpuVramWidget.exe"),"--diagnostic") { WorkingDirectory=root, UseShellExecute=false });
            timer.Start();
        };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g=e.Graphics;
        int sceneFrame=Math.Min(frame,60); // Freeze after motion to verify idle render skipping.
        g.Clear(Color.FromArgb(24,36,64));
        for(int x=-100;x<Width+100;x+=64)
        {
            using(var b=new SolidBrush(((x/64)&1)==0?Color.FromArgb(30,174,220):Color.FromArgb(213,89,154)))
                g.FillRectangle(b,x+(sceneFrame*2)%64,0,32,Height);
        }
        using(var b=new SolidBrush(Color.FromArgb(250,198,56)))
            g.FillEllipse(b,Width-460+(float)Math.Sin(sceneFrame*.055)*130,25,180,180);
        g.DrawString("LIVE DESKTOP CAPTURE TEST", Font, Brushes.White,20,280);
        g.DrawString("Moving stripes and a yellow circle must remain live through the glass.",Font,Brushes.White,20,310);
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose(); if(child!=null) { if(!child.HasExited) child.CloseMainWindow(); child.Dispose(); }
        base.OnFormClosed(e);
    }
}
