using System;
namespace LiquidGlass
{
    // Edit on the window's UI thread, then call Submit or ApplyGlassOptions.
    public sealed class GlassOptions
    {
        public float CornerRadius = 26;
        public float Refraction = 19;
        public float Blur = 1.2f;
        public float Tint = .035f;
        public int Fps = 60;
        // Zero: adaptive. 1..144: fixed sampling cadence; unchanged frames may reuse the surface.
        public int SampleHz;
        public bool Economy = true;
        public bool DragBoost = true;
        public bool SyncDrag = true;
        public float PredictionMs;
        // Glass material: 0 legacy light glass (adaptive text), 1 dark smoked, 2 light frosted.
        public int Material;
        internal void Validate()
        {
            if (Fps < 10 || Fps > 144 || SampleHz < 0 || SampleHz > 144)
                throw new ArgumentOutOfRangeException("Refresh rate", "Fps must be 10..144; SampleHz must be 0..144.");
            Check(CornerRadius,0,4096,"CornerRadius"); Check(Refraction,0,40,"Refraction");
            if (Material < 0 || Material > 2) throw new ArgumentOutOfRangeException("Material", "Material must be 0..2.");
            Check(Blur,0,12,"Blur"); Check(Tint,0,1,"Tint"); Check(PredictionMs,0,8,"PredictionMs");
        }
        private static void Check(float value,float min,float max,string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max)
                throw new ArgumentOutOfRangeException(name);
        }
    }
}
