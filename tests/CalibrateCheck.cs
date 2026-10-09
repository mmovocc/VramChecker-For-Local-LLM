using System;
using GpuVramWidget;

// Runs the widget's own Calibrator on one profile (hidden llama-server on 127.0.0.1), stores the result like the
// console's CALIBRATE button does, and prints the limits against the script's ctx.
// Usage: CalibrateCheck.exe <profile.bat>
internal static class CalibrateCheck
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (System.Diagnostics.Process.GetProcessesByName("llama-server").Length > 0) { Console.WriteLine("SKIP: llama-server is running"); return 2; }
        AppSettings.Load();
        var profile = LaunchProfile.Parse(args[0]);
        var fit = FitAdvisor.Analyze(profile);
        var budget = VramBudget.Measure(GpuStats.Collect());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cal = Calibrator.Run(profile, fit, budget.FreeNow, delegate (string s) { Console.WriteLine("  " + watch.Elapsed.ToString(@"mm\:ss") + " " + s); });
        CalibrationStore.Put(cal);
        var estimate = CtxLimits.For(profile, fit, null, budget);
        var limits = CtxLimits.For(profile, fit, cal, budget);
        Console.WriteLine("memory: {0:0} MiB + {1:0.0} MiB per 1K ctx (tested ctx {2})", cal.MemInterceptMiB, cal.MemMiBPerToken * 1024, cal.TestedCtx);
        Console.WriteLine("decode: {0:0.00} ms + {1:0.000} ms per 1K filled  -> {2:0.0} t/s empty, {3:0.0} t/s at {4} full", cal.DecodeMs0, cal.DecodeMsPerToken * 1024,
            cal.DecodeTpsAt(0), cal.DecodeTpsAt(profile.Ctx), profile.Ctx);
        Console.WriteLine("prefill: {0:0} t/s", cal.PrefillTps);
        Console.WriteLine("limits @ {0} t/s: memory {1}, speed {2}, healthy {3} | script ctx {4} | took {5:mm\\:ss}",
            AppSettings.MinDecodeTps, limits.MemoryNow, limits.Speed, limits.HealthyNow, profile.Ctx, watch.Elapsed);
        Console.WriteLine("test-free estimate: need {0:0} MiB at {1} (incl. margin), decode full {2:0.0} t/s, limits memory {3} speed {4} healthy {5}",
            estimate.NeedMiB(profile.Ctx), profile.Ctx, estimate.DecodeTpsFull(profile.Ctx), estimate.MemoryNow, estimate.Speed, estimate.HealthyNow);
        Console.WriteLine("calibrated need at {0}: {1:0} MiB", profile.Ctx, cal.NeedMiB(profile.Ctx));
        return 0;
    }
}
