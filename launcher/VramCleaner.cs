using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace GpuVramWidget
{
    internal sealed class CleanupResult
    {
        public int Closed, Forced;
        public readonly List<string> Failed = new List<string>();
        public double FreedMB;
        public string Summary()
        {
            if (Closed + Forced + Failed.Count == 0) return "Nothing on the clean list is running";
            string text = "Freed " + FormatMB(FreedMB) + " · closed " + (Closed + Forced) + " processes";
            if (Forced > 0) text += " (" + Forced + " forced)";
            if (Failed.Count > 0) text += " · " + Failed.Count + " could not be stopped";
            return text;
        }
        internal static string FormatMB(double mb) { return mb >= 1024 ? (mb / 1024).ToString("F1") + " GB" : Math.Round(mb).ToString("F0") + " MB"; }
    }

    // Graceful first (WM_CLOSE to the app's windows, so browsers save sessions), then Kill after the grace
    // period. Runs on a worker thread; progress strings are for the widget status row.
    internal static class VramCleaner
    {
        internal static CleanupResult Run(ICollection<string> names, int graceSeconds, Action<string> progress)
        {
            var result = new CleanupResult();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in names) if (!ProcessTree.IsProtected(n)) set.Add(n);
            if (set.Count == 0) return result;
            int self = Process.GetCurrentProcess().Id;

            double before, total;
            GpuStats.QueryTotals(out before, out total);

            var tree = ProcessTree.Snapshot();
            var targets = new List<ProcessTree.Node>();
            foreach (var node in tree.All) if (node.Pid != self && set.Contains(node.Name)) targets.Add(node);
            if (targets.Count == 0) return result;

            // Remember top-level instances (parent is a different program) for one-click restore.
            var restore = new List<string>();
            foreach (var node in targets)
            {
                var parent = tree.Get(node.ParentPid);
                if (parent != null && string.Equals(parent.Name, node.Name, StringComparison.OrdinalIgnoreCase)) continue;
                string path = ProcessTree.ImagePath(node.Pid);
                if (path != null && !restore.Exists(delegate (string r) { return string.Equals(r, path, StringComparison.OrdinalIgnoreCase); }))
                    restore.Add(path);
            }

            progress("Closing " + set.Count + " apps…");
            var pids = new HashSet<int>();
            foreach (var node in targets) pids.Add(node.Pid);
            PostCloseToWindows(pids);

            var deadline = DateTime.UtcNow.AddSeconds(graceSeconds);
            var alive = new List<int>(pids);
            while (DateTime.UtcNow < deadline)
            {
                alive.RemoveAll(delegate (int pid) { return !IsAlive(pid); });
                if (alive.Count == 0) break;
                progress("Waiting for " + alive.Count + " processes · force in " + Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds) + " s");
                Thread.Sleep(250);
            }
            alive.RemoveAll(delegate (int pid) { return !IsAlive(pid); });
            result.Closed = pids.Count - alive.Count;
            foreach (int pid in alive)
            {
                var node = tree.Get(pid);
                try { using (var p = Process.GetProcessById(pid)) { p.Kill(); p.WaitForExit(2000); } result.Forced++; }
                catch (ArgumentException) { result.Closed++; } // exited meanwhile
                catch { result.Failed.Add(node != null ? node.Name : pid.ToString()); }
            }

            // Freed allocations are returned asynchronously; wait until resident VRAM stops falling.
            progress("Waiting for VRAM to settle…");
            double last = before, now = before; int stable = 0;
            var settleUntil = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < settleUntil && stable < 3)
            {
                Thread.Sleep(300);
                GpuStats.QueryTotals(out now, out total);
                stable = Math.Abs(now - last) < 24 ? stable + 1 : 0;
                last = now;
            }
            result.FreedMB = Math.Max(0, before - now);

            lock (AppSettings.RestoreQueue)
            {
                foreach (string path in restore)
                    if (!AppSettings.RestoreQueue.Exists(delegate (string r) { return string.Equals(r, path, StringComparison.OrdinalIgnoreCase); }))
                        AppSettings.RestoreQueue.Add(path);
                AppSettings.Save();
            }
            return result;
        }

        // Relaunch apps closed by cleanup. Returns how many were started.
        internal static int Restore()
        {
            List<string> paths;
            lock (AppSettings.RestoreQueue) { paths = new List<string>(AppSettings.RestoreQueue); AppSettings.RestoreQueue.Clear(); AppSettings.Save(); }
            int started = 0;
            foreach (string path in paths)
            {
                try
                {
                    if (!System.IO.File.Exists(path)) continue;
                    var start = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(path) };
                    using (Process.Start(start)) { }
                    started++;
                }
                catch { }
            }
            return started;
        }

        private static bool IsAlive(int pid)
        {
            IntPtr h = OpenProcess(0x00100000 /* SYNCHRONIZE */, false, pid);
            if (h == IntPtr.Zero) return false;
            try { return WaitForSingleObject(h, 0) == 0x102 /* WAIT_TIMEOUT */; }
            finally { CloseHandle(h); }
        }

        // WM_CLOSE to every top-level window (visible or tray-hidden) owned by the targets.
        private static void PostCloseToWindows(HashSet<int> pids)
        {
            EnumWindows(delegate (IntPtr hwnd, IntPtr lParam)
            {
                uint pid; GetWindowThreadProcessId(hwnd, out pid);
                if (pids.Contains((int)pid) && GetWindow(hwnd, 4 /* GW_OWNER */) == IntPtr.Zero)
                    PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    }
}
