using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace GpuVramWidget
{
    // One Toolhelp snapshot gives every pid -> parent/name mapping without opening processes.
    internal sealed class ProcessTree
    {
        internal sealed class Node { public int Pid, ParentPid; public string Name; }

        private readonly Dictionary<int, Node> _nodes = new Dictionary<int, Node>();

        // Shell, session and driver processes: never closed, never offered for the cleanup list.
        private static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
            "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "RuntimeBroker", "ShellExperienceHost",
            "StartMenuExperienceHost", "SearchHost", "SearchApp", "TextInputHost", "ShellHost", "LockApp",
            "ApplicationFrameHost", "SystemSettings", "audiodg", "conhost", "WindowsTerminal", "OpenConsole",
            "dllhost", "taskhostw", "spoolsv", "MsMpEng", "SecurityHealthSystray", "nvcontainer",
            "NVDisplay.Container", "atieclxx", "atiesrxx", "amdfendrsr", "cmd", "powershell", "pwsh",
            "llama-server", "GpuVramWidget"
        };

        internal static bool IsProtected(string name) { return string.IsNullOrEmpty(name) || Protected.Contains(name); }

        internal static ProcessTree Snapshot()
        {
            var tree = new ProcessTree();
            IntPtr snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return tree;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                for (bool ok = Process32First(snap, ref entry); ok; ok = Process32Next(snap, ref entry))
                {
                    string exe = entry.szExeFile ?? "";
                    if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = exe.Substring(0, exe.Length - 4);
                    tree._nodes[(int)entry.th32ProcessID] = new Node { Pid = (int)entry.th32ProcessID, ParentPid = (int)entry.th32ParentProcessID, Name = exe };
                }
            }
            finally { CloseHandle(snap); }
            return tree;
        }

        internal IEnumerable<Node> All { get { return _nodes.Values; } }

        internal Node Get(int pid) { Node n; return _nodes.TryGetValue(pid, out n) ? n : null; }

        // Embedded browser runtimes always belong to the app hosting them.
        private static readonly HashSet<string> Embedded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "msedgewebview2", "CefSharp.BrowserSubprocess", "QtWebEngineProcess"
        };
        private readonly Dictionary<int, string> _vendors = new Dictionary<int, string>();

        // The user-facing app that owns a helper: steamwebhelper -> steam, WeChatAppEx -> Weixin.
        // A parent only counts as the owner when both executables come from the same vendor folder, so apps
        // merely *launched* by another (a browser opened from Everything, a game from Steam) stay separate.
        internal string AppNameFor(int pid)
        {
            Node node = Get(pid);
            if (node == null) return null;
            string app = node.Name;
            for (int depth = 0; depth < 8; depth++)
            {
                Node parent = Get(node.ParentPid);
                if (parent == null || parent.Pid == node.Pid || IsProtected(parent.Name)) break;
                bool owned = string.Equals(parent.Name, node.Name, StringComparison.OrdinalIgnoreCase) || Embedded.Contains(node.Name);
                if (!owned)
                {
                    string child = Vendor(node.Pid);
                    owned = child != null && child == Vendor(parent.Pid);
                }
                if (!owned) break;
                app = parent.Name; node = parent;
            }
            return app;
        }

        // C:\Program Files (x86)\Steam\bin\x.exe -> "steam"; ...\AppData\Roaming\Tencent\... -> "tencent".
        // Game libraries are never grouped with their store client.
        private string Vendor(int pid)
        {
            string vendor;
            if (_vendors.TryGetValue(pid, out vendor)) return vendor;
            string path = ImagePath(pid);
            if (path != null && path.IndexOf(@"\steamapps\", StringComparison.OrdinalIgnoreCase) < 0)
            {
                var parts = new List<string>(path.ToLowerInvariant().Split('\\'));
                int i = 1, users = parts.IndexOf("users");
                if (users >= 0 && users + 3 < parts.Count && parts[users + 2] == "appdata")
                {
                    i = users + 4; // AppData\Roaming|Local\<vendor>, skipping Local\Programs
                    if (i < parts.Count && parts[i] == "programs") i++;
                }
                else if (parts.Count > 2 && (parts[1].StartsWith("program files") || parts[1] == "programdata")) i = 2;
                if (i < parts.Count - 1) vendor = parts[i];
            }
            _vendors[pid] = vendor;
            return vendor;
        }

        internal static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    }
}
