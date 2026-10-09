using System;
using System.Drawing;
using System.Windows.Forms;
using GpuVramWidget;

// Opens the launch panel against live data, waits for the llama-fit-params pre-flight, saves a
// screenshot, closes. Read-only: no cleanup or launch is triggered. Usage: PanelShot.exe out.png
internal static class PanelShot
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { System.IO.File.WriteAllText((args.Length > 0 ? args[0] : "panel.png") + ".err.txt", e.ExceptionObject.ToString()); };
        var host = new Form { ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        host.Shown += delegate
        {
            // Read-only use of the saved settings; nothing below calls AppSettings.Save().
            AppSettings.Load();
            // Optional in-memory overrides for previews: PanelShot.exe out.png [firefox,msedge|-] [dark|light|legacy]
            if (args.Length > 1 && args[1] != "-") foreach (string n in args[1].Split(',')) AppSettings.Cleanup.Add(n);
            if (args.Length > 2) AppSettings.Material = args[2] == "light" ? GlassMaterial.Light : args[2] == "legacy" ? GlassMaterial.Legacy : GlassMaterial.Dark;
            // Snapshot to a temp file so the widget's own startup record is not overwritten.
            GpuPreference.SnapshotFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "panelshot-gpu.txt");
            GpuPreference.TakeStartupSnapshot();
            var sample = GpuStats.Collect();
            var launcher = new LauncherController(host, delegate { return sample; });
            var panel = new LaunchPanel(launcher, delegate { return sample; });
            var wa = Screen.PrimaryScreen.WorkingArea;
            panel.Location = new Point(wa.Left + 40, wa.Top + 40);
            panel.TopMost = true;
            panel.Show(host);
            if (Array.IndexOf(args, "gpu") >= 0) panel.ShowGpuTab(); // read-only view of the real preferences
            // Optional 4th argument "menu": also open a per-app context menu and save "<out>.menu.png".
            ContextMenuStrip menu = null;
            if (args.Length > 3 && args[3] == "menu")
            {
                var menuTimer = new Timer { Interval = 5500 };
                menuTimer.Tick += delegate
                {
                    menuTimer.Stop();
                    menu = AppMenu.Build("firefox", null, delegate { });
                    menu.Items.Insert(0, new ToolStripMenuItem("firefox · 114 MB · PID 1234") { Enabled = false });
                    menu.Items.Insert(1, new ToolStripSeparator());
                    menu.Show(panel, new Point(560, 240));
                };
                menuTimer.Start();
            }
            var timer = new Timer { Interval = 8000 };
            timer.Tick += delegate
            {
                timer.Stop();
                var bounds = panel.Bounds;
                using (var bmp = new Bitmap(bounds.Width, bounds.Height))
                {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                    bmp.Save(args.Length > 0 ? args[0] : "panel.png");
                }
                if (menu != null && menu.Visible)
                {
                    var mb = menu.Bounds;
                    using (var bmp = new Bitmap(mb.Width, mb.Height))
                    {
                        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(mb.Location, Point.Empty, mb.Size);
                        bmp.Save((args.Length > 0 ? args[0] : "panel.png") + ".menu.png");
                    }
                    menu.Close();
                }
                launcher.Dispose(); host.Close();
            };
            timer.Start();
        };
        Application.Run(host);
    }
}


