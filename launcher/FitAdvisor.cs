using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GpuVramWidget
{
    // Memory cost of a profile, measured by llama.cpp itself: need(ctx) = Fixed + Extra + ctx / TokensPerMiB.
    // The budget it is compared against comes from the widget's own D3DKMT numbers, never from the tool:
    // under WDDM, CUDA's "free" ignores other processes (a running llama-server included).
    internal sealed class FitResult
    {
        public string Error;
        public double FixedMiB;       // weights + compute buffers of the main model
        public double TokensPerMiB;   // ctx per MiB of KV/recurrent state
        public int CtxTrain;          // model's trained context; never recommend beyond it
        public long ExtraMiB;         // draft/mmproj budget fit-params cannot see
        public bool ExtraEstimated;
        public double ModelGiB;

        public bool Valid { get { return Error == null && TokensPerMiB > 0 && FixedMiB > 0; } }

        public double NeedMiB(int ctx) { return FixedMiB + ExtraMiB + ctx / TokensPerMiB; }

        public int MaxCtxFor(double budgetMiB)
        {
            if (!Valid) return 0;
            double ctx = (budgetMiB - FixedMiB - ExtraMiB) * TokensPerMiB;
            if (CtxTrain > 0) ctx = Math.Min(ctx, CtxTrain);
            return RoundCtx(ctx);
        }

        // 1024-aligned, matching how ctx sizes are usually chosen (65536, 110592, ...).
        public static int RoundCtx(double ctx) { return Math.Max(0, (int)(ctx / 1024) * 1024); }
    }

    internal static class FitAdvisor
    {
        // Arguments llama-fit-params understands that change memory use; value = takes an argument.
        private static readonly Dictionary<string, bool> Passthrough = new Dictionary<string, bool> {
            {"-m",true},{"--model",true},{"-ngl",true},{"--n-gpu-layers",true},{"--gpu-layers",true},
            {"-ctk",true},{"--cache-type-k",true},{"-ctv",true},{"--cache-type-v",true},
            {"-np",true},{"--parallel",true},{"-b",true},{"--batch-size",true},{"-ub",true},{"--ubatch-size",true},
            {"-ot",true},{"--override-tensor",true},{"-ts",true},{"--tensor-split",true},{"-sm",true},{"--split-mode",true},
            {"-mg",true},{"--main-gpu",true},{"-ncmoe",true},{"--n-cpu-moe",true},{"-dev",true},{"--device",true},
            {"--swa-full",false},{"-nkvo",false},{"--no-kv-offload",false},{"-kvu",false},{"--kv-unified",false},
            {"-cmoe",false},{"--cpu-moe",false}
        };
        private const int Probe = 2048;

        internal static FitResult Analyze(LaunchProfile profile)
        {
            var result = new FitResult();
            if (profile.Error != null) { result.Error = profile.Error; return result; }
            string tool = Path.Combine(Path.GetDirectoryName(profile.ServerExe), "llama-fit-params.exe");
            if (!File.Exists(tool)) { result.Error = "This llama.cpp build has no llama-fit-params.exe"; return result; }

            try { result.ModelGiB = new FileInfo(profile.Model).Length / 1073741824.0; } catch { }
            // Draft (DFlash/speculative) and vision projector are loaded by llama-server only. Budget their
            // weights plus working buffers from file size (mmproj checked against a live server: within ~100 MiB).
            foreach (string extra in new[] { profile.Draft, profile.Mmproj })
            {
                if (extra == null || !File.Exists(extra)) continue;
                long mib = new FileInfo(extra).Length / 1048576;
                result.ExtraMiB += mib + Math.Max(256, mib / 4);
                result.ExtraEstimated = true;
            }

            var args = MemoryArgs(profile);
            // The fitted ctx is linear in the requested margin: two probes give the slope, and the tool's own
            // free figure is used only to recover the fixed cost (free = fixed + margin + ctx / slope).
            string log;
            long margin = 0;
            for (int attempt = 0; attempt < 4; attempt++, margin += 4096)
            {
                int high = Run(tool, args, margin, true, out log);
                double free = ParseLog(log, result);
                if (high < 0) { result.Error = "llama-fit-params failed"; return result; }
                if (high == 0) { result.Error = "The weights alone do not fit (try a lower -ngl)"; return result; }
                if (result.CtxTrain > 0 && high >= result.CtxTrain - 256) continue; // capped: probe with more margin
                int low = Run(tool, args, margin + Probe, false, out log);
                if (low <= 0 || low >= high) break;
                result.TokensPerMiB = (high - low) / (double)Probe;
                result.FixedMiB = free - margin - high / result.TokensPerMiB;
                return result;
            }
            result.Error = "Could not measure KV cost (unexpected llama-fit-params output)";
            return result;
        }

        private static List<string> MemoryArgs(LaunchProfile profile)
        {
            var args = new List<string>();
            for (int i = 0; i < profile.Args.Count; i++)
            {
                bool hasValue;
                string a = profile.Args[i];
                if (!Passthrough.TryGetValue(a, out hasValue)) continue;
                args.Add(a);
                if (hasValue && i + 1 < profile.Args.Count) args.Add(profile.Args[++i]);
            }
            // Optional value: "-fa on|off|auto"
            int fa = profile.Args.FindIndex(delegate (string a) { return a == "-fa" || a == "--flash-attn"; });
            if (fa >= 0)
            {
                args.Add("-fa");
                if (fa + 1 < profile.Args.Count && Regex.IsMatch(profile.Args[fa + 1], "^(on|off|auto)$")) args.Add(profile.Args[fa + 1]);
            }
            return args;
        }

        // Returns the device free MiB as seen by llama.cpp (0 if absent) and fills n_ctx_train.
        private static double ParseLog(string log, FitResult result)
        {
            Match m = Regex.Match(log, @"n_ctx_train\s*=\s*(\d+)");
            if (m.Success) result.CtxTrain = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            m = Regex.Match(log, @"-\s*(\d+)\s*MiB free");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        // Returns the fitted -c, 0 when the minimum ctx does not fit, -1 on tool failure.
        private static int Run(string tool, List<string> args, long marginMiB, bool verbose, out string log)
        {
            var sb = new StringBuilder();
            foreach (string a in args) sb.Append(Quote(a)).Append(' ');
            sb.Append("--fit-target ").Append(marginMiB.ToString(CultureInfo.InvariantCulture));
            if (verbose) sb.Append(" -v"); // device free memory and n_ctx_train are logged only verbosely
            var start = new ProcessStartInfo(tool, sb.ToString())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(tool),
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(start))
            {
                var stderr = new StringBuilder();
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                string stdout = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } log = ""; return -1; }
                p.WaitForExit();
                lock (stderr) log = stderr.ToString();
                Match m = Regex.Match(stdout, @"-c\s+(\d+)");
                if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                // A configuration that cannot fit even the minimum ctx exits 1 silently; real failures
                // (bad argument, unreadable model) print an error line.
                return Regex.IsMatch(log, "(?i)(invalid argument|error:|failed to load|unable to)") ? -1 : 0;
            }
        }

        private static string Quote(string a)
        {
            return a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 && a.Length > 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
        }
    }

    // Real VRAM budget from D3DKMT. A running llama-server counts as reclaimable: launching restarts it.
    internal static class VramBudget
    {
        internal sealed class Snapshot
        {
            public double TotalMB, UsedMB, CleanupMB, ServerMB;
            public int CleanupApps;
            public double FreeNow { get { return TotalMB - UsedMB - AppSettings.HeadroomMiB; } }
            public double FreeAfter { get { return FreeNow + CleanupMB + ServerMB; } }
        }

        internal static Snapshot Measure(VramSample sample)
        {
            var s = new Snapshot();
            if (sample == null) return s;
            s.TotalMB = sample.TotalMB; s.UsedMB = sample.UsedMB;
            var tree = ProcessTree.Snapshot();
            var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in sample.Rows)
            {
                if (string.Equals(row.Name, "llama-server", StringComparison.OrdinalIgnoreCase)) { s.ServerMB += row.MB; continue; }
                string app = tree.AppNameFor(row.Pid) ?? row.Name;
                if (!AppSettings.IsCleanup(app) && !AppSettings.IsCleanup(row.Name)) continue;
                s.CleanupMB += row.MB;
                apps.Add(app);
            }
            s.CleanupApps = apps.Count;
            // Per-process commit can exceed resident usage; never claim more than is actually in use.
            s.CleanupMB = Math.Min(s.CleanupMB, Math.Max(0, s.UsedMB - s.ServerMB));
            s.ServerMB = Math.Min(s.ServerMB, s.UsedMB);
            return s;
        }
    }
}
