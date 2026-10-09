using System;
using System.Collections.Generic;
using System.IO;
using GpuVramWidget;

// Read-only checks of the launcher backend against real scripts/models on this machine:
// profile parsing, ctx rewrite, llama-fit-params pre-flight, app grouping. Never closes processes
// or writes the registry. Usage: LauncherCheck.exe <folder with llama-server .bat files>
internal static class LauncherCheck
{
    private static int _failures;

    private static void Expect(bool condition, string what)
    {
        Console.WriteLine((condition ? "  ok   " : "  FAIL ") + what);
        if (!condition) _failures++;
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var tokens = LaunchProfile.Tokenize("\"C:\\a b\\llama-server.exe\" -m \"P:\\x y.gguf\"  --ctx-size 4096");
        Expect(tokens.Count == 5 && tokens[0] == "C:\\a b\\llama-server.exe" && tokens[2] == "P:\\x y.gguf", "tokenize quotes and spaces");

        if (args.Length > 0)
        {
            AppSettings.ProfileDirs.Clear(); AppSettings.ProfileDirs.Add(args[0]);
            var profiles = LaunchProfile.Scan();
            Expect(profiles.Count > 0, profiles.Count + " profiles found");
            foreach (var p in profiles)
            {
                Console.WriteLine("  - " + p.Name + " | ctx " + p.Ctx + " | port " + p.Port + " | draft " + (p.Draft != null) + " | mmproj " + (p.Mmproj != null) + (p.Error != null ? " | " + p.Error : ""));
                Expect(p.Error == null && File.Exists(p.Model), "parsed " + p.Name);
                var rewritten = p.ArgsWithCtx(12288);
                Expect(rewritten.FindAll(delegate (string a) { return a == "-c" || a == "--ctx-size"; }).Count == 1 && rewritten.Contains("12288"), "ctx rewrite keeps one -c");
                Expect(rewritten.Count == p.Args.Count + (p.Ctx > 0 ? 0 : 2), "other args untouched");
            }
            var sample0 = GpuStats.Collect();
            var budget = VramBudget.Measure(sample0);
            Console.WriteLine("  budget: total " + Math.Round(budget.TotalMB) + " used " + Math.Round(budget.UsedMB) + " server " + Math.Round(budget.ServerMB) + " freeAfter " + Math.Round(budget.FreeAfter) + " MiB");
            foreach (var p in profiles)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var fit = FitAdvisor.Analyze(p);
                Console.WriteLine("  fit " + p.Name + ": " + (fit.Error ?? "ok") + " fixed=" + Math.Round(fit.FixedMiB) + " extra=" + fit.ExtraMiB
                    + " tok/MiB=" + fit.TokensPerMiB.ToString("F2") + " need@" + p.Ctx + "=" + Math.Round(fit.NeedMiB(p.Ctx)) + " maxAfter=" + fit.MaxCtxFor(budget.FreeAfter)
                    + " (" + watch.ElapsedMilliseconds + " ms)");
                Expect(fit.Valid, "fit measured for " + p.Name);
                Expect(fit.MaxCtxFor(1e9) <= fit.CtxTrain, "never above trained context");
                string ps = p.PowerShellScript(98304);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "lc-" + p.Name + ".ps1"), ps, new System.Text.UTF8Encoding(true));
                Expect(ps.Contains("'98304'") && ps.Contains("& '" + p.ServerExe + "' @serverArgs"), "PowerShell script carries ctx override");
            }
            // Ground truth: compare the prediction with a running server's measured VRAM.
            if (budget.ServerMB > 0)
            {
                var server = new ServerProbe();
                if (server.Ctx > 0 && server.Model != null)
                {
                    var match = profiles.Find(delegate (LaunchProfile p) { return p.Model != null && Path.GetFileNameWithoutExtension(p.Model) == server.Model && p.Ctx == server.Ctx; });
                    if (match != null)
                    {
                        var fit = FitAdvisor.Analyze(match);
                        double err = fit.NeedMiB(server.Ctx) - budget.ServerMB;
                        Console.WriteLine("  live server " + match.Name + ": predicted " + Math.Round(fit.NeedMiB(server.Ctx)) + " MiB, measured " + Math.Round(budget.ServerMB) + " MiB (" + (err >= 0 ? "+" : "") + Math.Round(err) + ")");
                        Expect(Math.Abs(err) < 600, "prediction within 600 MiB of the running server");
                        Expect(server.Ctx <= fit.MaxCtxFor(budget.FreeAfter), "running ctx reported as fitting after restart");
                    }
                }
            }
        }

        var sample = GpuStats.Collect();
        Console.WriteLine("  VRAM " + Math.Round(sample.UsedMB) + " / " + Math.Round(sample.TotalMB) + " MB, " + sample.Rows.Count + " GPU processes");
        var tree = ProcessTree.Snapshot();
        foreach (var row in sample.Rows)
        {
            string app = tree.AppNameFor(row.Pid);
            string exe = ProcessTree.ImagePath(row.Pid);
            Console.WriteLine("    " + row.Name.PadRight(28) + " -> " + (app ?? "?").PadRight(22) + Math.Round(row.MB).ToString().PadLeft(6) + " MB"
                + (ProcessTree.IsProtected(app) ? "  [protected]" : "") + "  gpuPref=" + (exe != null ? GpuPreference.Get(exe).ToString() : "-"));
        }
        double used, total; GpuStats.QueryTotals(out used, out total);
        Expect(total > 0 && Math.Abs(total - sample.TotalMB) < 1, "QueryTotals matches Collect");
        Expect(ProcessTree.IsProtected("dwm") && ProcessTree.IsProtected("explorer") && !ProcessTree.IsProtected("firefox"), "protected list");
        Expect(!GpuPreference.Supported(@"C:\Program Files\WindowsApps\x\a.exe") && GpuPreference.Supported(@"C:\a.exe"), "packaged apps excluded from GPU preference");

        Console.WriteLine(_failures == 0 ? "PASS: launcher backend" : "FAILED: " + _failures);
        return _failures == 0 ? 0 : 1;
    }
}

// Reads the running llama-server's ctx/model through the widget's own command-line reader.
internal sealed class ServerProbe
{
    public int Ctx; public string Model;
    public ServerProbe()
    {
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("llama-server"))
        {
            using (proc)
            using (var searcher = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + proc.Id))
            foreach (System.Management.ManagementObject mo in searcher.Get())
            {
                var t = LaunchProfile.Tokenize((mo["CommandLine"] as string) ?? "");
                for (int i = 0; i < t.Count - 1; i++)
                {
                    if (t[i] == "-c" || t[i] == "--ctx-size") int.TryParse(t[i + 1], out Ctx);
                    if (t[i] == "-m" || t[i] == "--model") Model = Path.GetFileNameWithoutExtension(t[i + 1]);
                }
            }
        }
    }
}

