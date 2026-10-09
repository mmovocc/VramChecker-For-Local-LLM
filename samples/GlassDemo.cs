using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiquidGlass;

internal sealed class Demo : GlassForm
{
    private int _clicks;
    private readonly Rectangle _button = new Rectangle(24,130,190,44);
    internal Demo()
    {
        Text="LiquidGlass framework demo"; ClientSize=new Size(420,240);
        StartPosition=FormStartPosition.CenterScreen;
        var menu=new ContextMenuStrip();
        menu.Items.Add("关闭",null,delegate { Close(); }); ContextMenuStrip=menu;
        GlassFailed+=delegate { Text="Glass error: "+LastGlassError.Message; };
    }
    protected override bool IsGlassDragArea(Point point) { return !_button.Contains(point); }
    protected override void DrawGlassForeground(Graphics g)
    {
        g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using(var title=new Font("Segoe UI",19,FontStyle.Bold))
        using(var body=new Font("Microsoft YaHei UI",10)) {
            g.DrawString("LiquidGlass",title,Brushes.Black,24,24);
            g.DrawString("拖动空白区域 · 右键关闭",body,Brushes.Black,24,76);
            using(var fill=new SolidBrush(Color.FromArgb(75,255,255,255))) g.FillRectangle(fill,_button);
            g.DrawString("点击测试："+_clicks,body,Brushes.Black,38,142);
        }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if(e.Button==MouseButtons.Left && _button.Contains(e.Location)) { _clicks++; InvalidateGlass(); }
    }
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [STAThread] private static void Main()
    {
        SetProcessDPIAware(); Application.EnableVisualStyles(); Application.Run(new Demo());
    }
}
