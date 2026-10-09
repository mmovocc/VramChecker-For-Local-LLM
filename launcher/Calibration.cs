using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace GpuVramWidget
{
    // Measured behaviour of one launch profile on this machine:
    //   VRAM(ctx)            = MemInterceptMiB + MemMiBPerToken * ctx        (dedicated VRAM of llama-server)
    //   decode ms/token(fill) = DecodeMs0 + DecodeMsPerToken * filled tokens (with the profile's own draft model)
    // Unlike llama-fit-params, this includes the draft model, its context state and real buffers, and it says
    // how fast generation stays as the context fills up.
    internal sealed class Calibration
    {
        public string Profile, Stamp;
        public DateTime When;
        public double MemInterceptMiB, MemMiBPerToken, DecodeMs0, DecodeMsPerToken, PrefillTps;
        public int TestedCtx;

        public double NeedMiB(int ctx) { return MemInterceptMiB + MemMiBPerToken * ctx; }
        public double DecodeTpsAt(int filled) { return 1000.0 / (DecodeMs0 + DecodeMsPerToken * filled); }

        // Largest context that, once completely filled, still decodes at minTps.
        public int SpeedLimit(double minTps)
        {
            if (DecodeMsPerToken <= 0) return int.MaxValue;
            double ms = 1000.0 / minTps;
            return ms <= DecodeMs0 ? 0 : (int)Math.Min(int.MaxValue, (ms - DecodeMs0) / DecodeMsPerToken);
        }
    }

    // %APPDATA%\GpuVramWidget\calibration.ini — one line per profile. A calibration is valid only while the model,
    // draft, projector and llama-server binaries are unchanged (size + write time stamp).
    internal static class CalibrationStore
    {
        private static readonly string FilePath = Path.Combine(AppSettings.Directory, "calibration.ini");
        private static Dictionary<string, Calibration> _all;

        internal static Calibration Get(LaunchProfile profile)
        {
            if (profile == null) return null;
            Load();
            Calibration c;
            return _all.TryGetValue(profile.Path, out c) && c.Stamp == Stamp(profile) ? c : null;
        }

        internal static void Put(Calibration c)
        {
            Load();
            _all[c.Profile] = c;
            try
            {
                Directory.CreateDirectory(AppSettings.Directory);
                var sb = new StringBuilder("# profile|stamp|utc|interceptMiB|MiBPerToken|decodeMs0|decodeMsPerToken|prefillTps|testedCtx\r\n");
                foreach (var x in _all.Values)
                    sb.AppendLine(string.Join("|", new[] { x.Profile, x.Stamp, x.When.ToUniversalTime().ToString("o"),
                        F(x.MemInterceptMiB), F(x.MemMiBPerToken), F(x.DecodeMs0), F(x.DecodeMsPerToken), F(x.PrefillTps), x.TestedCtx.ToString(CultureInfo.InvariantCulture) }));
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static void Load()
        {
            if (_all != null) return;
            _all = new Dictionary<string, Calibration>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    if (line.StartsWith("#")) continue;
                    string[] p = line.Split('|');
                    if (p.Length < 9) continue;
                    _all[p[0]] = new Calibration { Profile = p[0], Stamp = p[1], When = DateTime.Parse(p[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime(),
                        MemInterceptMiB = D(p[3]), MemMiBPerToken = D(p[4]), DecodeMs0 = D(p[5]), DecodeMsPerToken = D(p[6]), PrefillTps = D(p[7]),
                        TestedCtx = int.Parse(p[8], CultureInfo.InvariantCulture) };
                }
            }
            catch { }
        }

        internal static string Stamp(LaunchProfile profile)
        {
            var sb = new StringBuilder();
            foreach (string f in new[] { profile.ServerExe, profile.Model, profile.Draft, profile.Mmproj })
                if (f != null && File.Exists(f)) { var i = new FileInfo(f); sb.Append(i.Length).Append('@').Append(i.LastWriteTimeUtc.Ticks).Append(';'); }
            sb.Append(string.Join(" ", profile.Args.ToArray()).GetHashCode().ToString("x"));
            return sb.ToString();
        }

        private static string F(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        private static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
    }

    // Runs the profile in a hidden llama-server on 127.0.0.1 (free port) and measures memory and decode speed.
    // Two server starts: a small ctx and a large one; on the large one, decode speed at three fill depths.
    // Requires that no other llama-server is running (the caller checks). Nothing is written except the result.
    internal static class Calibrator
    {
        internal const int SmallCtx = 16384, MaxTestCtx = 131072;
        internal static int RunningPid;   // the hidden server, so the monitor can ignore it

        internal static Calibration Run(LaunchProfile profile, FitResult fit, double budgetMiB, Action<string> progress)
        {
            // Large test ctx: the estimate's memory limit with 1.5 GB of extra safety, capped to keep it to minutes.
            int large = fit != null && fit.Valid ? fit.MaxCtxFor(budgetMiB - 1536) : 65536;
            large = Math.Max(SmallCtx * 2, Math.Min(MaxTestCtx, FitResult.RoundCtx(large)));

            progress("Calibrating · memory at ctx " + LauncherController.Short(SmallCtx) + "…");
            double smallMiB, smallShared; int unused;
            using (var server = new TestServer(profile, SmallCtx))
            {
                server.WaitReady();
                Probe(server, 512, out unused);   // allocate compute buffers
                smallMiB = server.DedicatedMiB(); smallShared = server.SharedMiB();
            }

            var depths = new List<int>(); var ms = new List<double>();
            double largeMiB = 0, prefill = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                progress("Calibrating · memory and speed at ctx " + LauncherController.Short(large) + "…");
                using (var server = new TestServer(profile, large))
                {
                    server.WaitReady();
                    depths.Clear(); ms.Clear();
                    foreach (double f in new[] { 0.02, 0.45, 0.85 })
                    {
                        int depth = Math.Max(1024, (int)(large * f));
                        progress("Calibrating · decode with " + LauncherController.Short(depth) + " tokens of context…");
                        int prompt;
                        var t = Probe(server, depth, out prompt);
                        depths.Add(prompt); ms.Add(t.Item1);
                        if (t.Item2 > 0) prefill = t.Item2;
                    }
                    largeMiB = server.DedicatedMiB();
                    // Dedicated VRAM caps out when the driver spills; a spilled point would understate the slope.
                    if (server.SharedMiB() - smallShared < 400) break;
                }
                large = FitResult.RoundCtx(large * 0.8);
                if (attempt == 2) throw new InvalidOperationException("Still spilling at ctx " + large + "; free more VRAM and retry");
            }

            double slope = (largeMiB - smallMiB) / (large - SmallCtx);
            double ms0, msPerToken;
            LinearFit(depths, ms, out ms0, out msPerToken);
            return new Calibration
            {
                Profile = profile.Path, Stamp = CalibrationStore.Stamp(profile), When = DateTime.Now,
                MemMiBPerToken = slope, MemInterceptMiB = smallMiB - slope * SmallCtx,
                DecodeMs0 = ms0, DecodeMsPerToken = Math.Max(0, msPerToken), PrefillTps = prefill, TestedCtx = large
            };
        }

        // Fills `depth` tokens of context, then asks for an ordinary coding answer through the model's own chat
        // template, so the draft model sees natural output (a word-list continuation made speculative decoding
        // look ~25% faster than real use). Generates 160 tokens. Returns (decode ms/token, prefill t/s).
        private static Tuple<double, double> Probe(TestServer server, int depth, out int promptTokens)
        {
            string content = "Background notes:\n" + Filler.Text(server, depth)
                + "\n\nIgnore the notes above. Write a Python class implementing an LRU cache with type hints, docstrings and unit tests.";
            string json = "{\"messages\":[{\"role\":\"user\",\"content\":" + Json(content) + "}],\"max_tokens\":160,\"ignore_eos\":true,\"cache_prompt\":false,\"temperature\":0}";
            string body = server.Post("/v1/chat/completions", json, 900000);
            promptTokens = (int)Num(body, "prompt_n");
            double perToken = Num(body, "predicted_per_token_ms"), prefill = Num(body, "prompt_per_second");
            if (perToken <= 0) throw new InvalidOperationException("No tokens were generated during calibration");
            return Tuple.Create(perToken, prefill);
        }

        private static void LinearFit(List<int> x, List<double> y, out double a, out double b)
        {
            double n = x.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < x.Count; i++) { sx += x[i]; sy += y[i]; sxx += (double)x[i] * x[i]; sxy += x[i] * y[i]; }
            double d = n * sxx - sx * sx;
            b = d == 0 ? 0 : (n * sxy - sx * sy) / d;
            a = (sy - b * sx) / n;
        }

        internal static double Num(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9.eE+]+)");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        internal static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // Deterministic varied English filler (not one repeated block, which would make drafting unrealistically good).
        private static class Filler
        {
            private static readonly string[] Words = ("the of and to in is was for on that with as by at from it be this are or an have not which but had they were all one their there "
                + "been has more if will would when who what so can no about out up into than them some could time only other new these two may then do first any my now such like our over "
                + "man me even most made after also did many before must through back years where much your way well down should because each just those people how too little state good very make "
                + "world still own see men work long get here between both life being under never day same another know while last might us great old year off come since against go came right used take "
                + "three states himself few house use during without again place around however home small found thought went say part once general high upon school every don does got united left number course war "
                + "until always away something fact though water less public put think almost hand enough far took head yet government system better set told nothing night end why called didn eyes find going look asked "
                + "later knew point next program city business give group toward young days let room president side social given present several order national possible rather second face per among form important often things looked early white case john become large big need four within felt along children saw best church ever least power development light thing seemed family interest want members mind country area others although turned done open god service certain kind problem began different door thus help sense means whole matter perhaps itself york times law human line above name example action company hands local show whether five history gave today either act feet across taken past quite anything seen having death week experience body word half really field am car words already themselves information tell together college shall money period held keep sure probably free seems political real behind cannot miss question air office making brought whose special heard major problems ago became federal moment study available known result street economic boy position reason change south board individual job areas society west close turn love community true court force full seem am front tax").Split(' ');

            internal static string Text(TestServer server, int tokens)
            {
                // Token density measured once per server with the model's own tokenizer.
                if (server.CharsPerToken <= 0)
                {
                    string sample = Build(4000, 7);
                    string r = server.Post("/tokenize", "{\"content\":" + Json(sample) + "}", 60000);
                    int count = Regex.Matches(r, "\\d+").Count;
                    server.CharsPerToken = count > 0 ? sample.Length / (double)count : 4;
                }
                return Build((int)(tokens * server.CharsPerToken * 0.97), tokens);
            }

            private static string Build(int chars, int seed)
            {
                var rnd = new Random(seed);
                var sb = new StringBuilder(chars + 64);
                int n = 0;
                while (sb.Length < chars)
                {
                    string w = Words[rnd.Next(Words.Length)];
                    sb.Append(n % 14 == 0 ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w);
                    sb.Append(++n % 14 == 0 ? ". " : " ");
                    if (n % 120 == 0) sb.Append('\n');
                }
                return sb.ToString();
            }
        }
    }

    // A hidden llama-server for calibration: same arguments as the profile, but ctx overridden and bound to
    // 127.0.0.1 on a free port. Disposing kills it and waits for exit.
    internal sealed class TestServer : IDisposable
    {
        private readonly Process _process;
        private readonly int _port;
        internal double CharsPerToken;

        internal TestServer(LaunchProfile profile, int ctx)
        {
            _port = FreePort();
            var args = new List<string>();
            var source = profile.ArgsWithCtx(ctx);
            for (int i = 0; i < source.Count; i++)
            {
                if ((source[i] == "--port" || source[i] == "--host") && i + 1 < source.Count) { i++; continue; }
                args.Add(source[i]);
            }
            args.Add("--host"); args.Add("127.0.0.1"); args.Add("--port"); args.Add(_port.ToString(CultureInfo.InvariantCulture));
            var sb = new StringBuilder();
            foreach (string a in args) sb.Append(a.IndexOfAny(new[] { ' ', '"' }) >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a).Append(' ');
            var start = new ProcessStartInfo(profile.ServerExe, sb.ToString())
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(profile.Path)
            };
            _process = Process.Start(start);
            _process.OutputDataReceived += delegate { }; _process.ErrorDataReceived += delegate { };
            _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
            Calibrator.RunningPid = _process.Id;
        }

        internal void WaitReady()
        {
            var until = DateTime.UtcNow.AddMinutes(4);
            while (DateTime.UtcNow < until)
            {
                if (_process.HasExited) throw new InvalidOperationException("llama-server exited during calibration (code " + _process.ExitCode + ")");
                try { var r = (HttpWebRequest)WebRequest.Create(Url("/health")); r.Timeout = 1500; r.Proxy = null; using (var resp = (HttpWebResponse)r.GetResponse()) if ((int)resp.StatusCode == 200) return; }
                catch (WebException) { }
                Thread.Sleep(700);
            }
            throw new TimeoutException("llama-server did not become ready within 4 minutes");
        }

        internal string Post(string path, string json, int timeoutMs)
        {
            var r = (HttpWebRequest)WebRequest.Create(Url(path));
            r.Method = "POST"; r.ContentType = "application/json; charset=utf-8"; r.Proxy = null;
            r.Timeout = timeoutMs; r.ReadWriteTimeout = timeoutMs;
            byte[] data = Encoding.UTF8.GetBytes(json);
            using (var s = r.GetRequestStream()) s.Write(data, 0, data.Length);
            using (var resp = r.GetResponse()) using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return reader.ReadToEnd();
        }

        internal double DedicatedMiB() { return GpuMemory.ProcessMiB(_process.Id, "Dedicated Usage"); }
        internal double SharedMiB() { return GpuMemory.ProcessMiB(_process.Id, "Shared Usage"); }

        private string Url(string path) { return "http://127.0.0.1:" + _port + path; }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port;
        }

        public void Dispose()
        {
            try { if (!_process.HasExited) { _process.Kill(); _process.WaitForExit(10000); } } catch { }
            Calibrator.RunningPid = 0;
            _process.Dispose();
            Thread.Sleep(1500); // let the driver release the allocations before the next measurement
        }
    }

    // Per-process GPU memory from the "GPU Process Memory" performance counters (the numbers Task Manager shows).
    // "Shared Usage" growing well past its normal few hundred MB means CUDA spilled into system RAM.
    internal static class GpuMemory
    {
        internal static double ProcessMiB(int pid, string counter)
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Process Memory");
                string prefix = "pid_" + pid + "_";
                double total = 0;
                foreach (string instance in category.GetInstanceNames())
                    if (instance.StartsWith(prefix, StringComparison.Ordinal))
                        using (var pc = new PerformanceCounter("GPU Process Memory", counter, instance, true)) total += pc.RawValue;
                return total / 1048576.0;
            }
            catch { return 0; }
        }
    }
}
