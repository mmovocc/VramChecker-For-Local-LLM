using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GpuVramWidget
{
    // A launch profile is one of the user's existing llama-server .bat/.cmd files. The file stays the
    // source of truth; it is parsed only to pre-flight memory and, when asked, relaunch with another -c.
    internal sealed class LaunchProfile
    {
        public string Path;          // .bat/.cmd file
        public string Name;          // file name without extension
        public string ServerExe;     // llama-server.exe
        public List<string> Args = new List<string>();
        public string Error;         // why the file cannot be launched/pre-flighted

        public string Model { get { return Value("-m", "--model"); } }
        public string Draft { get { return Value("-md", "--model-draft"); } }
        public string Mmproj { get { return Value("-mm", "--mmproj"); } }
        public int Port { get { return IntValue(8080, "--port"); } }
        public int Ctx { get { return IntValue(0, "-c", "--ctx-size"); } }
        public string Host { get { return Value("--host") ?? "127.0.0.1"; } }
        public override string ToString() { return Name; }

        internal static List<LaunchProfile> Scan()
        {
            var result = new List<LaunchProfile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in AppSettings.ProfileDirs)
            {
                if (!Directory.Exists(dir)) continue;
                var files = new List<string>();
                try { files.AddRange(Directory.GetFiles(dir, "*.bat")); files.AddRange(Directory.GetFiles(dir, "*.cmd")); }
                catch { continue; }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    if (!seen.Add(file)) continue;
                    var profile = Parse(file);
                    if (profile.ServerExe != null || profile.Error != null) result.Add(profile);
                }
            }
            return result;
        }

        internal static LaunchProfile Parse(string file)
        {
            var p = new LaunchProfile { Path = file, Name = System.IO.Path.GetFileNameWithoutExtension(file) };
            try
            {
                string dir = System.IO.Path.GetDirectoryName(file) + "\\";
                var logical = new StringBuilder();
                foreach (string raw in File.ReadAllLines(file, Encoding.Default))
                {
                    string line = raw.TrimEnd();
                    bool continued = line.EndsWith("^");
                    logical.Append(continued ? line.Substring(0, line.Length - 1) : line).Append(' ');
                    if (continued) continue;
                    string command = logical.ToString().Trim(); logical.Length = 0;
                    if (command.IndexOf("llama-server", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var tokens = Tokenize(command.Replace("%~dp0", dir));
                    int exe = tokens.FindIndex(delegate (string t) {
                        return System.IO.Path.GetFileName(t).StartsWith("llama-server", StringComparison.OrdinalIgnoreCase);
                    });
                    if (exe < 0) continue;
                    p.ServerExe = tokens[exe];
                    if (!p.ServerExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p.ServerExe += ".exe";
                    p.Args = tokens.GetRange(exe + 1, tokens.Count - exe - 1);
                    break;
                }
                if (p.ServerExe == null) return p;
                if (p.ServerExe.IndexOf('%') >= 0 || p.Args.Exists(delegate (string a) { return a.IndexOf('%') >= 0; }))
                    p.Error = "Script uses variables; no pre-flight (can still launch)";
                else if (!File.Exists(p.ServerExe)) p.Error = "Not found: " + p.ServerExe;
                else if (p.Model == null || !File.Exists(p.Model)) p.Error = "Model file not found";
            }
            catch (Exception ex) { p.Error = ex.Message; }
            return p;
        }

        public string Value(params string[] names)
        {
            for (int i = 0; i < Args.Count - 1; i++)
                foreach (string n in names) if (Args[i] == n) return Args[i + 1];
            return null;
        }

        private int IntValue(int fallback, params string[] names)
        {
            int n; string v = Value(names);
            return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        // Arguments with -c replaced (or added); everything else is passed through untouched.
        internal List<string> ArgsWithCtx(int ctx)
        {
            var args = new List<string>();
            for (int i = 0; i < Args.Count; i++)
            {
                if ((Args[i] == "-c" || Args[i] == "--ctx-size") && i + 1 < Args.Count) { i++; continue; }
                args.Add(Args[i]);
            }
            args.Add("--ctx-size"); args.Add(ctx.ToString(CultureInfo.InvariantCulture));
            return args;
        }

        internal sealed class LaunchHandle
        {
            public int HostPid;         // console process we started (cmd fallback; 0 when Windows Terminal hosts it)
            public string StopFlag;     // file that tells the launch script the widget stopped the server
            public bool InTerminal;     // opened as a Windows Terminal tab
        }

        // Runs llama-server in a new Windows Terminal tab running PowerShell (pwsh when installed, else Windows
        // PowerShell); falls back to a standalone PowerShell window when wt.exe cannot be started. The script
        // travels as -EncodedCommand: no script file is written and execution policy does not apply.
        // When the widget stops the server it first creates StopFlag, so the script exits 0 and the tab or window
        // closes; when the server exits on its own the console stays open with the exit code until Enter.
        // Scripts that could not be parsed (variables etc.) fall back to running the file itself with cmd.
        internal LaunchHandle Start(int ctxOverride)
        {
            string workDir = System.IO.Path.GetDirectoryName(Path);
            if (ServerExe == null || Error != null)
            {
                var bat = new System.Diagnostics.ProcessStartInfo(Path) { UseShellExecute = true, WorkingDirectory = workDir };
                using (var host = System.Diagnostics.Process.Start(bat)) return new LaunchHandle { HostPid = host != null ? host.Id : 0 };
            }
            var handle = new LaunchHandle { StopFlag = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpuvramwidget-" + Guid.NewGuid().ToString("N") + ".stop") };
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(PowerShellScript(ctxOverride, handle.StopFlag)));
            string shell = PowerShellExe(), shellArgs = "-NoLogo -NoProfile -EncodedCommand " + encoded;
            string title = "llama-server · " + Name + (ctxOverride > 0 && ctxOverride != Ctx ? " · ctx " + ctxOverride : "");
            string wt = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
            if (File.Exists(wt))
            {
                try
                {
                    var tab = new System.Diagnostics.ProcessStartInfo(wt, "new-tab --title " + WtQuote(title) + " -d " + WtQuote(workDir) + " " + WtQuote(shell) + " " + shellArgs)
                        { UseShellExecute = true, WorkingDirectory = workDir };
                    using (System.Diagnostics.Process.Start(tab)) { }
                    handle.InTerminal = true;
                    return handle;
                }
                catch (System.ComponentModel.Win32Exception) { } // alias registered but not resolvable: use a plain window
            }
            var window = new System.Diagnostics.ProcessStartInfo(shell, shellArgs) { UseShellExecute = true, WorkingDirectory = workDir };
            using (var host = System.Diagnostics.Process.Start(window)) handle.HostPid = host != null ? host.Id : 0;
            return handle;
        }

        internal string PowerShellScript(int ctxOverride, string stopFlag = null)
        {
            var args = ctxOverride > 0 && ctxOverride != Ctx ? ArgsWithCtx(ctxOverride) : Args;
            var script = new StringBuilder();
            script.AppendLine("$Host.UI.RawUI.WindowTitle = " + PsQuote("llama-server · " + Name + (ctxOverride > 0 ? " · ctx " + ctxOverride : "")));
            script.Append("$serverArgs = @(");
            for (int i = 0; i < args.Count; i++) script.Append(i > 0 ? ", " : "").Append(PsQuote(args[i]));
            script.AppendLine(")");
            script.AppendLine("Write-Host ('> ' + " + PsQuote(ServerExe) + " + ' ' + ($serverArgs -join ' ')) -ForegroundColor DarkGray");
            script.AppendLine("& " + PsQuote(ServerExe) + " @serverArgs");
            script.AppendLine("$code = $LASTEXITCODE");
            if (stopFlag != null)
            {
                script.AppendLine("$stopFlag = " + PsQuote(stopFlag));
                script.AppendLine("if (Test-Path -LiteralPath $stopFlag) { Remove-Item -LiteralPath $stopFlag -ErrorAction SilentlyContinue; exit 0 }");
            }
            script.AppendLine("Write-Host ''");
            script.AppendLine("Write-Host \"llama-server exited with code $code.\" -ForegroundColor Yellow");
            script.AppendLine("[void](Read-Host 'Press Enter to close')");
            return script.ToString();
        }

        // wt.exe argument: quoted, ';' escaped (it separates wt subcommands), no trailing backslash before the quote.
        private static string WtQuote(string value)
        {
            string v = value.Replace(";", "\\;");
            if (v.EndsWith("\\")) v += ".";
            return "\"" + v.Replace("\"", "\\\"") + "\"";
        }

        private static string PsQuote(string value) { return "'" + value.Replace("'", "''") + "'"; }

        private static string PowerShellExe()
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { string pwsh = System.IO.Path.Combine(dir.Trim(), "pwsh.exe"); if (dir.Trim().Length > 0 && File.Exists(pwsh)) return pwsh; }
                catch (ArgumentException) { }
            }
            string installed = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe");
            if (File.Exists(installed)) return installed;
            return System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        }

        // cmd-style split: whitespace separates, double quotes group and are removed.
        internal static List<string> Tokenize(string command)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool quoted = false, any = false;
            foreach (char c in command)
            {
                if (c == '"') { quoted = !quoted; any = true; continue; }
                if (!quoted && char.IsWhiteSpace(c))
                {
                    if (any) { tokens.Add(current.ToString()); current.Length = 0; any = false; }
                    continue;
                }
                current.Append(c); any = true;
            }
            if (any) tokens.Add(current.ToString());
            return tokens;
        }
    }
}
