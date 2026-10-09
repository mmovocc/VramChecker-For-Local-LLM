using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;
using System.Windows.Forms;
using GpuVramWidget;

// End-to-end launch/stop through Windows Terminal with a FAKE llama-server (a process that only sleeps):
// start -> ServerMonitor finds it -> Stop() writes the stop flag and kills it -> the tab's PowerShell exits.
// Usage: WtLaunchCheck.exe <folder containing fake llama-server.exe, model.gguf and fake.bat>
internal static class WtLaunchCheck
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (Process.GetProcessesByName("llama-server").Length > 0) { Console.WriteLine("SKIP: a llama-server is already running"); return 2; }
        var profile = LaunchProfile.Parse(Path.Combine(args[0], "fake.bat"));
        if (profile.Error != null) { Console.WriteLine("FAIL: " + profile.Error); return 1; }
        int exit = 1;
        var host = new Form { ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-2000, -2000) };
        host.Shown += delegate
        {
            var monitor = new ServerMonitor(host);
            var launch = profile.Start(0);
            Console.WriteLine("launched: inTerminal=" + launch.InTerminal + " hostPid=" + launch.HostPid);
            monitor.ExpectLaunch(profile.Port, launch);
            var started = DateTime.Now;
            var timer = new System.Windows.Forms.Timer { Interval = 500 };
            int shellPid = 0;
            timer.Tick += delegate
            {
                if (shellPid == 0 && monitor.Pid != 0)
                {
                    var node = ProcessTree.Snapshot().Get(monitor.Pid);
                    shellPid = node != null ? node.ParentPid : 0;
                    string parentName = node != null && ProcessTree.Snapshot().Get(node.ParentPid) != null ? ProcessTree.Snapshot().Get(node.ParentPid).Name : "?";
                    Console.WriteLine("server pid " + monitor.Pid + " under " + parentName + " (" + shellPid + "); state " + monitor.State);
                    Thread.Sleep(1500);
                    monitor.Stop();
                    Thread.Sleep(2500);
                    bool shellGone;
                    try { using (var p = Process.GetProcessById(shellPid)) shellGone = p.HasExited; } catch (ArgumentException) { shellGone = true; }
                    bool flagGone = !File.Exists(launch.StopFlag ?? "");
                    Console.WriteLine((shellGone ? "PASS" : "FAIL") + ": console exited after Stop (tab closes)" + (flagGone ? "; stop flag cleaned up" : "; stop flag left behind"));
                    exit = shellGone && flagGone ? 0 : 1;
                    timer.Stop(); monitor.Dispose(); host.Close();
                }
                else if ((DateTime.Now - started).TotalSeconds > 25) { Console.WriteLine("FAIL: server never detected"); timer.Stop(); host.Close(); }
            };
            timer.Start();
        };
        Application.Run(host);
        return exit;
    }
}
