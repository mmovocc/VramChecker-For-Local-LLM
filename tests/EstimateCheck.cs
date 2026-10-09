using System;
using GpuVramWidget;

// Test-free estimates (no calibration) for every profile in a folder, against the current VRAM budget.
// Usage: EstimateCheck.exe <folder with llama-server scripts>
internal static class EstimateCheck
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        AppSettings.Load();
        AppSettings.ProfileDirs.Clear(); AppSettings.ProfileDirs.Add(args[0]);
        var budget = VramBudget.Measure(GpuStats.Collect());
        Console.WriteLine("budget: total {0:0} used {1:0} free-now {2:0} MiB (after {3} MiB headroom) · min decode {4} t/s",
            budget.TotalMB, budget.UsedMB, budget.FreeNow, AppSettings.HeadroomMiB, AppSettings.MinDecodeTps);
        foreach (var p in LaunchProfile.Scan())
        {
            var fit = FitAdvisor.Analyze(p);
            var e = Estimator.For(p, fit);
            var l = CtxLimits.For(p, fit, null, budget);
            Console.WriteLine("{0}\n   draft={1} fixed={2:0} MiB  per1K={3:0.0} MiB  decode empty={4:0.0} t/s | script ctx {5}: need {6:0} MiB, full {7:0.0} t/s | memory {8}  speed {9}  healthy {10}",
                p.Name, e != null ? (e.Draft ?? "-") : "?", e != null ? e.FixedMiB : 0, l.MiBPer1K, e != null ? 1000 / e.DecodeMs0 : 0,
                p.Ctx, l.NeedMiB(p.Ctx), l.DecodeTpsFull(p.Ctx), l.MemoryNow, l.Speed, l.HealthyNow);
        }
        return 0;
    }
}
