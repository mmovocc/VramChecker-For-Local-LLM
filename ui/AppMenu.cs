using System;
using System.Windows.Forms;

namespace GpuVramWidget
{
    // Per-app actions shared by the widget's process rows and the launch console's clean list.
    internal static class AppMenu
    {
        internal static ContextMenuStrip Build(string app, string exePath, Action<string> done)
        {
            var menu = DotChrome.Menu();
            bool listed = AppSettings.IsCleanup(app), protectedApp = ProcessTree.IsProtected(app);
            var cleanup = new ToolStripMenuItem(listed ? "Remove " + app + " from clean list" : "Add " + app + " to clean list") { Enabled = !protectedApp };
            cleanup.Click += delegate { AppSettings.SetCleanup(app, !listed); done(listed ? app + " removed from clean list" : app + " will close before launch"); };
            menu.Items.Add(cleanup);

            int pref = exePath != null ? GpuPreference.Get(exePath) : 0;
            bool supported = GpuPreference.Supported(exePath);
            var igpu = new ToolStripMenuItem("Integrated GPU (frees dGPU VRAM)") { Checked = pref == 1, Enabled = supported };
            igpu.Click += delegate { SetPreference(exePath, pref == 1 ? 0 : 1, app, done); };
            var dgpu = new ToolStripMenuItem("Discrete GPU") { Checked = pref == 2, Enabled = supported };
            dgpu.Click += delegate { SetPreference(exePath, pref == 2 ? 0 : 2, app, done); };
            var gpu = new ToolStripMenuItem("GPU preference") { Enabled = supported && !protectedApp };
            gpu.DropDownItems.Add(igpu); gpu.DropDownItems.Add(dgpu);
            if (!supported && exePath != null) gpu.ToolTipText = "Store apps: set this in Windows Settings > Display > Graphics";
            menu.Items.Add(gpu);

            var open = new ToolStripMenuItem("Open file location") { Enabled = exePath != null };
            open.Click += delegate { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + exePath + "\""); } catch { } };
            menu.Items.Add(open);
            menu.Closed += delegate { menu.BeginInvoke((Action)menu.Dispose); };
            return menu;
        }

        private static void SetPreference(string exePath, int preference, string app, Action<string> done)
        {
            try
            {
                GpuPreference.Set(exePath, preference);
                done(preference == 1 ? app + " uses the integrated GPU from its next start"
                    : preference == 2 ? app + " uses the discrete GPU from its next start" : app + " back to the system default GPU");
            }
            catch (Exception ex) { done("Could not set GPU preference: " + ex.Message); }
        }
    }
}
