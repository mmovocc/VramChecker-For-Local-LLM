using System;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GpuVramWidget
{
    internal enum ServerState { None, Starting, Loading, Ready, Stopped }

    // Watches any running llama-server (launched here or by hand): finds the process, reads its command line
    // once for port/model, then polls /health (503 = loading, 200 = ready) and /props for the real n_ctx.
    internal sealed class ServerMonitor : IDisposable
    {
        public ServerState State { get; private set; }
        public int Pid { get; private set; }
        public int Port { get; private set; }
        public int Ctx { get; private set; }
        public string Model { get; private set; }
        public DateTime Since { get; private set; }
        public double SharedMiB { get; private set; }   // system RAM the server's GPU allocations occupy
        // Healthy servers keep a few hundred MB here; past 1 GB the driver has spilled VRAM into system RAM.
        public bool Spilling { get { return SharedMiB > 1024; } }
        public event EventHandler Changed;

        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly System.Windows.Forms.Control _ui;
        private int _polling;
        private DateTime _expectUntil;   // a launch was requested; show "starting" until the process appears
        private LaunchProfile.LaunchHandle _launch;   // the console we opened for the latest launch
        private int _launchedPid;                      // llama-server process that belongs to _launch

        internal ServerMonitor(System.Windows.Forms.Control ui)
        {
            _ui = ui;
            _timer.Tick += delegate { Poll(); };
            _timer.Start();
            Poll();
        }

        internal void ExpectLaunch(int port, LaunchProfile.LaunchHandle launch)
        {
            _launch = launch; _launchedPid = 0;
            _expectUntil = DateTime.UtcNow.AddSeconds(20);
            Port = port; Pid = 0; Ctx = 0; Model = null;
            Set(ServerState.Starting);
        }

        // Kill the server, and the console window hosting it only if this widget opened that window
        // (a terminal the user started llama-server from by hand is left alone).
        // Stops the server. For a server this widget launched, the stop flag tells its PowerShell script to exit,
        // which closes the Windows Terminal tab (or window); a cmd fallback console is closed directly.
        // Consoles the user opened by hand are never touched.
        internal void Stop()
        {
            int pid = Pid;
            if (pid == 0) return;
            bool ours = _launch != null && pid == _launchedPid;
            if (ours && _launch.StopFlag != null)
                try { System.IO.File.WriteAllText(_launch.StopFlag, "stopped by GpuVramWidget"); } catch { }
            var node = ProcessTree.Snapshot().Get(pid);
            // Wait for exit so an immediate relaunch/fit sees the freed VRAM.
            try { using (var p = Process.GetProcessById(pid)) { p.Kill(); p.WaitForExit(3000); } } catch { }
            if (ours && _launch.HostPid != 0 && _launch.StopFlag == null && node != null && node.ParentPid == _launch.HostPid)
                try { using (var p = Process.GetProcessById(_launch.HostPid)) p.Kill(); } catch { }
        }

        private void Poll()
        {
            if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
            int knownPid = Pid, port = Port;
            Task.Factory.StartNew(delegate
            {
                int pid = 0, newPort = port, ctx = 0; string model = null; int health = -1; double shared = 0;
                try
                {
                    pid = FindServer(knownPid);
                    if (pid != 0 && pid != knownPid) ReadCommandLine(pid, ref newPort, ref ctx, ref model);
                    if (pid != 0) { health = Health(newPort, ref ctx); shared = GpuMemory.ProcessMiB(pid, "Shared Usage"); }
                }
                catch { }
                try
                {
                    _ui.BeginInvoke((Action)delegate
                    {
                        Interlocked.Exchange(ref _polling, 0);
                        SharedMiB = shared;
                        Apply(pid, knownPid, newPort, ctx, model, health);
                    });
                }
                catch { Interlocked.Exchange(ref _polling, 0); }
            });
        }

        private void Apply(int pid, int knownPid, int port, int ctx, string model, int health)
        {
            if (pid == 0)
            {
                if (State == ServerState.Starting && DateTime.UtcNow < _expectUntil) return;
                Pid = 0;
                Set(State == ServerState.None ? ServerState.None : ServerState.Stopped);
                return;
            }
            if (pid != knownPid)
            {
                // The first server seen after a launch request is the one in our console.
                if (State == ServerState.Starting && _launch != null && _launchedPid == 0) _launchedPid = pid;
                Pid = pid; Port = port; Model = model; Ctx = ctx; Since = DateTime.Now;
            }
            if (ctx > 0) Ctx = ctx;
            Set(health == 200 ? ServerState.Ready : ServerState.Loading);
        }

        private void Set(ServerState state)
        {
            bool changed = state != State;
            if (changed) { State = state; if (state != ServerState.Ready) Since = DateTime.Now; }
            var handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        internal void Dismiss() { if (State == ServerState.Stopped) Set(ServerState.None); }

        private static int FindServer(int preferred)
        {
            Process[] all = Process.GetProcessesByName("llama-server");
            int found = 0;
            foreach (var p in all)
            {
                // The calibration's hidden test server is not "the" server.
                if (p.Id != Calibrator.RunningPid && (p.Id == preferred || found == 0)) found = p.Id;
                p.Dispose();
            }
            return found;
        }

        private static void ReadCommandLine(int pid, ref int port, ref int ctx, ref string model)
        {
            using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
            using (var results = searcher.Get())
                foreach (ManagementObject mo in results)
                {
                    string cmd = mo["CommandLine"] as string;
                    if (cmd == null) continue;
                    var tokens = LaunchProfile.Tokenize(cmd);
                    port = 8080;
                    for (int i = 0; i < tokens.Count - 1; i++)
                    {
                        int n;
                        if (tokens[i] == "--port" && int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) port = n;
                        else if ((tokens[i] == "-c" || tokens[i] == "--ctx-size") && int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ctx = n;
                        else if (tokens[i] == "-m" || tokens[i] == "--model") model = System.IO.Path.GetFileNameWithoutExtension(tokens[i + 1]);
                    }
                }
        }

        // HTTP status of /health (-1 = not listening yet); on 200 also reads the effective n_ctx from /props.
        private static int Health(int port, ref int ctx)
        {
            int status = Get("http://127.0.0.1:" + port + "/health", null);
            if (status == 200)
            {
                string body = null;
                if (Get("http://127.0.0.1:" + port + "/props", s => body = s) == 200 && body != null)
                {
                    Match m = Regex.Match(body, "\"n_ctx\"\\s*:\\s*(\\d+)");
                    if (m.Success) ctx = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }
            return status;
        }

        private static int Get(string url, Action<string> body)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 800; request.ReadWriteTimeout = 800; request.Proxy = null;
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (body != null)
                        using (var reader = new System.IO.StreamReader(response.GetResponseStream())) body(reader.ReadToEnd());
                    return (int)response.StatusCode;
                }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                int code = r != null ? (int)r.StatusCode : -1;
                if (r != null) r.Close();
                return code;
            }
        }

        public void Dispose() { _timer.Stop(); _timer.Dispose(); }
    }
}
