using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GpuVramWidget
{
    internal sealed class StatusInfo
    {
        public string Text;
        public Color Color;
        public bool Clickable;
    }

    // UI-thread owner of the clean -> launch -> monitor flow. The widget's action bar and the launch console
    // both call into this, so one-click and detailed paths behave identically.
    internal sealed class LauncherController : IDisposable
    {
        internal readonly ServerMonitor Server;
        internal List<LaunchProfile> Profiles = new List<LaunchProfile>();
        internal bool Busy { get; private set; }
        internal event EventHandler Changed;
        internal event EventHandler VramChanged;   // ask the widget to resample now

        private readonly Control _ui;
        private readonly Func<VramSample> _sample;
        private readonly Dictionary<string, FitResult> _fits = new Dictionary<string, FitResult>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _fitting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _status;
        private DateTime _statusUntil;

        internal LauncherController(Control ui, Func<VramSample> sample)
        {
            _ui = ui; _sample = sample;
            Rescan();
            Server = new ServerMonitor(ui);
            Server.Changed += delegate { Raise(); };
        }

        internal void Rescan() { Profiles = LaunchProfile.Scan(); }

        internal LaunchProfile Current
        {
            get
            {
                foreach (var p in Profiles) if (string.Equals(p.Path, AppSettings.LastProfile, StringComparison.OrdinalIgnoreCase)) return p;
                return Profiles.Count > 0 ? Profiles[0] : null;
            }
            set { AppSettings.LastProfile = value != null ? value.Path : ""; AppSettings.Save(); Raise(); }
        }

        internal bool ServerActive
        {
            get { var s = Server.State; return s == ServerState.Ready || s == ServerState.Loading || s == ServerState.Starting; }
        }

        // Memory cost of a profile. Measured once in the background (llama-fit-params, ~2 s) and cached:
        // it depends on the model and arguments, not on what else is using VRAM. null = still measuring.
        internal FitResult Fit(LaunchProfile profile, bool remeasure = false)
        {
            if (profile == null) return null;
            FitResult fit;
            if (!remeasure && _fits.TryGetValue(profile.Path, out fit)) return fit;
            if (_fitting.Add(profile.Path))
            {
                if (remeasure) _fits.Remove(profile.Path);
                Task.Factory.StartNew(delegate
                {
                    FitResult result;
                    try { result = FitAdvisor.Analyze(profile); }
                    catch (Exception ex) { result = new FitResult { Error = ex.Message }; }
                    Post(delegate { _fits[profile.Path] = result; _fitting.Remove(profile.Path); Raise(); });
                });
            }
            return null;
        }

        // Healthy context limits for a profile against the current VRAM budget; null while still measuring.
        internal CtxLimits Limits(LaunchProfile profile)
        {
            if (profile == null) return null;
            var fit = Fit(profile);
            var cal = CalibrationStore.Get(profile);
            if (fit == null && cal == null) return null;
            return CtxLimits.For(profile, fit, cal, VramBudget.Measure(_sample()));
        }

        // The healthy context is the one the user tuned by hand in the launch script (--ctx-size). Only a script
        // without a ctx falls back to the computed limit. The widget never raises the ctx on its own.
        internal static int HealthyCtx(LaunchProfile profile, CtxLimits limits)
        {
            if (profile.Ctx > 0) return profile.Ctx;
            return limits != null ? limits.HealthyAfter : 0;
        }

        // Measures the profile in a hidden server (memory and decode speed vs. context fill). Minutes, whole GPU.
        internal void Calibrate(LaunchProfile profile)
        {
            if (profile == null || Busy) return;
            if (ServerActive) { Flash("Stop llama-server before calibrating"); return; }
            if (profile.Error != null) { Flash("Cannot calibrate: " + profile.Error); return; }
            Busy = true; _status = "Calibrating " + profile.Name + "…"; Raise();
            Task.Factory.StartNew(delegate
            {
                string message;
                try
                {
                    var fit = FitAdvisor.Analyze(profile);
                    var budget = VramBudget.Measure(GpuStats.Collect());
                    var cal = Calibrator.Run(profile, fit, budget.FreeNow, delegate (string s) { Post(delegate { _status = s; Raise(); }); });
                    CalibrationStore.Put(cal);
                    var limits = CtxLimits.For(profile, fit, cal, budget);
                    message = "Calibrated · measured limit ~" + Short(limits.HealthyNow) + " (memory " + Short(limits.MemoryNow) + ", "
                        + (int)AppSettings.MinDecodeTps + " t/s up to " + Short(Math.Min(limits.Speed, 1 << 30)) + ")"
                        + (profile.Ctx > 0 ? " · yours " + Short(profile.Ctx) : "");
                }
                catch (Exception ex) { message = "Calibration failed: " + ex.Message; }
                Post(delegate { Busy = false; Flash(message); });
            });
        }

        // One status line: transient progress, then the server, then whether the current profile fits.
        internal StatusInfo Status()
        {
            var p = DotUi.P;
            int restore = AppSettings.RestoreQueue.Count;
            if (_status != null && (Busy || DateTime.Now < _statusUntil)) return Info(_status, p.Accent, false);
            switch (Server.State)
            {
                case ServerState.Ready:
                    if (Server.Spilling)
                        return Info("Spilling " + CleanupResult.FormatMB(Server.SharedMiB) + " into system RAM · slow · lower ctx", p.Crit, false);
                    return Info("llama-server ready · :" + Server.Port + (Server.Ctx > 0 ? " · ctx " + Server.Ctx.ToString("N0") : ""), p.Accent, false);
                case ServerState.Loading:
                    return Info("Loading model · " + (int)(DateTime.Now - Server.Since).TotalSeconds + " s", p.Warn, false);
                case ServerState.Starting:
                    return Info("Starting llama-server…", p.Warn, false);
                case ServerState.Stopped:
                    return Info(restore > 0 ? "llama-server stopped · click to restore " + restore + " apps" : "llama-server stopped · click to dismiss", p.Muted, true);
            }
            if (restore > 0) return Info(restore + " apps closed for the model · click to restore", p.Muted, true);

            var profile = Current;
            if (profile == null) return Info("No launch profile · open the console to add one", p.Muted, false);
            var limits = Limits(profile);
            if (limits == null) return Info("Measuring " + profile.Name + "…", p.Muted, false);
            if (!limits.Valid) { var fit = Fit(profile); return Info(fit != null && fit.Error != null ? fit.Error : "Pre-flight unavailable", p.Crit, false); }
            var budget = VramBudget.Measure(_sample());
            int ctx = HealthyCtx(profile, limits);
            double need = limits.NeedMiB(ctx);
            string tail = " · ctx " + Short(ctx) + " · limit ~" + Short(limits.HealthyAfter) + (limits.Calibrated ? "" : " est.");
            // Past physical free VRAM it spills (slow); inside the safety margins it is only tight.
            double spare = budget.FreeAfter + AppSettings.HeadroomMiB - limits.RawNeedMiB(ctx);
            if (spare < 0) return Info("Would spill " + CleanupResult.FormatMB(-spare) + tail, p.Crit, false);
            if (need > budget.FreeAfter) return Info("Tight · " + CleanupResult.FormatMB(spare) + " spare" + tail, p.Warn, false);
            if (ctx > limits.Speed) return Info("< " + (int)AppSettings.MinDecodeTps + " t/s when full" + tail, p.Warn, false);
            if (need > budget.FreeNow) return Info("Fits after clean" + tail, p.Warn, false);
            return Info("Fits now" + tail, p.Accent, false);
        }

        internal static string Short(int ctx) { return ctx >= 1024 ? (ctx / 1024) + "K" : ctx.ToString(); }

        private static StatusInfo Info(string text, Color color, bool clickable) { return new StatusInfo { Text = text, Color = color, Clickable = clickable }; }

        internal void StatusClicked()
        {
            if (AppSettings.RestoreQueue.Count > 0) Restore();
            Server.Dismiss();
            Raise();
        }

        internal void Restore()
        {
            int n = VramCleaner.Restore();
            Flash(n > 0 ? "Reopened " + n + " apps" : "Nothing to restore");
        }

        internal void Clean(Action<CleanupResult> then)
        {
            if (Busy) return;
            if (AppSettings.Cleanup.Count == 0) { Flash("Clean list is empty · add apps in the console"); return; }
            Busy = true; _status = "Preparing clean…"; Raise();
            var names = new List<string>(AppSettings.Cleanup);
            int grace = AppSettings.GraceSeconds;
            Task.Factory.StartNew(delegate
            {
                CleanupResult result = null;
                try { result = VramCleaner.Run(names, grace, delegate (string s) { Post(delegate { _status = s; Raise(); }); }); }
                catch (Exception ex) { result = new CleanupResult(); result.Failed.Add(ex.Message); }
                Post(delegate
                {
                    Busy = false;
                    Flash(result.Summary());
                    var vram = VramChanged; if (vram != null) vram(this, EventArgs.Empty);
                    if (then != null) then(result);
                });
            });
        }

        // Clean, then start the profile exactly as scripted: its ctx is the healthy baseline.
        internal void CleanAndLaunch(LaunchProfile profile)
        {
            if (profile == null || Busy) return;
            if (ServerActive) Server.Stop();
            if (AppSettings.Cleanup.Count > 0) Clean(delegate { Start(profile, 0, null); });
            else Start(profile, 0, null);
        }

        private void Start(LaunchProfile profile, int ctx, string note)
        {
            try
            {
                var launch = profile.Start(ctx);
                Server.ExpectLaunch(profile.Port, launch);
                string where = launch.InTerminal ? " in Windows Terminal" : launch.StopFlag != null ? " (Windows Terminal unavailable, PowerShell window)" : "";
                Flash(note ?? ("Launched " + profile.Name + (ctx > 0 && ctx != profile.Ctx ? " · ctx " + ctx.ToString("N0") : "") + where));
            }
            catch (Exception ex) { Flash("Launch failed: " + ex.Message); }
        }

        internal void Flash(string text) { _status = text; _statusUntil = DateTime.Now.AddSeconds(8); Raise(); }

        private void Post(Action action)
        {
            try { if (!_ui.IsDisposed) _ui.BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        private void Raise() { var h = Changed; if (h != null) h(this, EventArgs.Empty); }

        public void Dispose() { Server.Dispose(); }
    }
}
