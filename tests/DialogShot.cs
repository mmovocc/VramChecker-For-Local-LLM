using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GpuVramWidget;

// Opens a DotDialog from an always-on-top owner (the widget's situation) and checks that the dialog is
// the window under its own centre point, i.e. visible and clickable; saves a screenshot of its corners.
// Usage: DialogShot.exe out.png [dark|light]
internal static class DialogShot
{
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        if (args.Length > 1) AppSettings.Material = args[1] == "light" ? GlassMaterial.Light : GlassMaterial.Dark;
        int exit = 1;
        var wa = Screen.PrimaryScreen.WorkingArea;
        var owner = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(wa.Left + 60, wa.Top + 60, 420, 420), BackColor = Color.SteelBlue };
        owner.Shown += delegate
        {
            var timer = new Timer { Interval = 900 };
            timer.Tick += delegate
            {
                timer.Stop();
                foreach (Form f in Application.OpenForms)
                {
                    if (!(f is DotDialog)) continue;
                    var b = f.Bounds;
                    IntPtr top = GetAncestor(WindowFromPoint(new Point(b.X + b.Width / 2, b.Y + 30)), 2 /* GA_ROOT */);
                    exit = top == f.Handle ? 0 : 2;
                    Console.WriteLine(exit == 0 ? "PASS: dialog is topmost and hit-testable" : "FAIL: another window covers the dialog");
                    b.Inflate(10, 10);
                    using (var bmp = new Bitmap(b.Width, b.Height))
                    {
                        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                        bmp.Save(args.Length > 0 ? args[0] : "dialog.png");
                    }
                    f.Close();
                }
            };
            timer.Start();
            DotDialog.Confirm(owner, "Stop llama-server", "The loaded model will be unloaded.", "Stop");
            owner.Close();
        };
        Application.Run(owner);
        return exit;
    }
}
