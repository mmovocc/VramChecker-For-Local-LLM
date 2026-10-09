using System;

namespace GpuVramWidget
{
    // How large a context a profile can use healthily on this machine (reference figures; the script's own ctx
    // remains the launch baseline):
    //   memory limit - largest ctx whose VRAM need fits the budget without spilling into system RAM
    //   speed limit  - largest ctx that still decodes at AppSettings.MinDecodeTps once completely filled
    //   healthy      - the smaller of the two, never above the model's trained context
    // Without a calibration both come from Estimator (test-free); a calibration replaces them with measurements.
    internal sealed class CtxLimits
    {
        public bool Calibrated;
        public int MemoryNow, MemoryAfter, Speed = int.MaxValue, HealthyNow, HealthyAfter;
        public Calibration Calibration;
        private Estimator.Estimate _estimate;
        private double _fixed, _perToken, _ms0, _msPerToken, _margin;

        internal static CtxLimits For(LaunchProfile profile, FitResult fit, Calibration cal, VramBudget.Snapshot budget)
        {
            var l = new CtxLimits { Calibration = cal, Calibrated = cal != null };
            if (cal != null)
            {
                l._fixed = cal.MemInterceptMiB; l._perToken = cal.MemMiBPerToken; l._ms0 = cal.DecodeMs0; l._msPerToken = cal.DecodeMsPerToken;
            }
            else
            {
                l._estimate = Estimator.For(profile, fit);
                if (l._estimate == null) return l;
                l._fixed = l._estimate.FixedMiB; l._perToken = l._estimate.MiBPerToken;
                l._ms0 = l._estimate.DecodeMs0; l._msPerToken = l._estimate.DecodeMsPerToken;
                l._margin = Estimator.EstimateMarginMiB;
            }
            int train = fit != null && fit.CtxTrain > 0 ? fit.CtxTrain : int.MaxValue;
            l.MemoryNow = l.MemoryFor(budget.FreeNow, train);
            l.MemoryAfter = l.MemoryFor(budget.FreeAfter, train);
            double ms = 1000.0 / AppSettings.MinDecodeTps;
            if (l._msPerToken > 0) l.Speed = ms <= l._ms0 ? 0 : (int)Math.Min(int.MaxValue, (ms - l._ms0) / l._msPerToken);
            l.HealthyNow = FitResult.RoundCtx(Math.Min(l.MemoryNow, l.Speed));
            l.HealthyAfter = FitResult.RoundCtx(Math.Min(l.MemoryAfter, l.Speed));
            return l;
        }

        internal bool Valid { get { return Calibrated || _estimate != null; } }

        // VRAM the profile needs at this ctx, including the estimate's uncertainty margin.
        internal double NeedMiB(int ctx) { return Valid ? _fixed + _perToken * ctx + _margin : 0; }

        // The expected need itself, without any margin: past what is physically free, the driver spills.
        internal double RawNeedMiB(int ctx) { return Valid ? _fixed + _perToken * ctx : 0; }

        internal double MiBPer1K { get { return _perToken * 1024; } }

        // Decode speed with the context completely filled.
        internal double DecodeTpsFull(int ctx) { return Valid && ctx > 0 ? 1000.0 / (_ms0 + _msPerToken * ctx) : 0; }

        private int MemoryFor(double budgetMiB, int train)
        {
            double ctx = _perToken > 0 ? (budgetMiB - _fixed - _margin) / _perToken : 0;
            return FitResult.RoundCtx(Math.Max(0, Math.Min(ctx, train)));
        }
    }
}
