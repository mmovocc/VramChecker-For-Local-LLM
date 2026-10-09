using System;
using System.IO;
using System.Text;

namespace GpuVramWidget
{
    // Test-free estimate of a profile's VRAM need and of how fast it still decodes once the context is full.
    // Inputs: llama-fit-params for the main model, file sizes, and the draft model's architecture from its GGUF
    // header. Coefficients were measured on this machine (RTX 4080 16 GB, Qwen3.8-27B IQ3_XXS + DFlash2 draft and
    // IQ3_S + mmproj, q4_0 KV, natural coding output, 2026-10-07) and carry over to other profiles as an estimate;
    // CALIBRATE replaces them with measurements for one profile.
    //
    //   VRAM(ctx)        = fit fixed + draft/projector fixed + ctx * fitPerToken * 1.075
    //   decode ms/token  = (mainWeightsGB * 2.22 + filledK * kvMiBPer1K * 0.0056) / speculativeSpeedup
    internal static class Estimator
    {
        // llama-fit-params understates the per-token cost by ~7.5% (23.0 vs 21.4 MiB/1K on both profiles measured).
        internal const double FitPerTokenCorrection = 1.075;
        // DFlash draft in VRAM: 2.15x its file (12,377 measured vs 10,034 fit-params for the main model alone).
        internal const double DFlashFixedPerFileMiB = 2.15;
        // Classic draft models: weights + buffers + their own (small) KV; not measured here.
        internal const double DraftFixedPadMiB = 256, DraftPerTokenShare = 0.10;
        // Vision projector: 1.17x its file (12,662 measured vs 11,625 fit-params).
        internal const double ProjectorPerFileMiB = 1.17;
        // Decode without a draft: 2.22 ms per GB of main weights (24.4 ms for 10.96 GB) plus 0.0056 ms per 1K filled
        // tokens per MiB/1K of KV (0.120 ms per 1K at 21.4 MiB/1K).
        internal const double MsPerWeightGB = 2.22, MsPerFilledKPerKvMiB = 0.120 / 21.4;
        // Speculative decoding divides both terms: DFlash measured 1.69x (12.3 ms empty, 0.071 ms per 1K filled);
        // classic drafts assumed 1.4x.
        internal const double DFlashSpeedup = 1.69, DraftSpeedup = 1.4;
        // Kept free on top of AppSettings.HeadroomMiB for the uncertainty of an estimate.
        internal const double EstimateMarginMiB = 512;

        internal sealed class Estimate
        {
            public double FixedMiB, MiBPerToken, DecodeMs0, DecodeMsPerToken;
            public string Draft;   // "dflash", "draft", or null
        }

        internal static Estimate For(LaunchProfile profile, FitResult fit)
        {
            if (fit == null || !fit.Valid) return null;
            double mainKvMiBPerK = 1024.0 / fit.TokensPerMiB;
            var e = new Estimate { FixedMiB = fit.FixedMiB, MiBPerToken = FitPerTokenCorrection / fit.TokensPerMiB };
            double speedup = 1;
            if (profile.Draft != null && File.Exists(profile.Draft))
            {
                double draftMiB = SizeGB(profile.Draft) * 1024;
                bool dflash = string.Equals(GgufArchitecture(profile.Draft), "dflash", StringComparison.OrdinalIgnoreCase);
                e.Draft = dflash ? "dflash" : "draft";
                if (dflash) { e.FixedMiB += draftMiB * DFlashFixedPerFileMiB; speedup = DFlashSpeedup; }
                else { e.FixedMiB += draftMiB + DraftFixedPadMiB; e.MiBPerToken *= 1 + DraftPerTokenShare; speedup = DraftSpeedup; }
            }
            if (profile.Mmproj != null && File.Exists(profile.Mmproj))
                e.FixedMiB += SizeGB(profile.Mmproj) * 1024 * ProjectorPerFileMiB;
            e.DecodeMs0 = SizeGB(profile.Model) * MsPerWeightGB / speedup;
            e.DecodeMsPerToken = MsPerFilledKPerKvMiB * mainKvMiBPerK / 1024 / speedup;
            return e;
        }

        private static double SizeGB(string path)
        {
            try { return path != null && File.Exists(path) ? new FileInfo(path).Length / 1073741824.0 : 0; } catch { return 0; }
        }

        // general.architecture from a GGUF header (it is among the first keys; reading stops there).
        internal static string GgufArchitecture(string path)
        {
            try
            {
                using (var f = new BinaryReader(File.OpenRead(path)))
                {
                    if (Encoding.ASCII.GetString(f.ReadBytes(4)) != "GGUF") return null;
                    f.ReadUInt32(); f.ReadUInt64();
                    ulong kvs = f.ReadUInt64();
                    for (ulong i = 0; i < kvs && i < 64; i++)
                    {
                        string key = Str(f);
                        uint type = f.ReadUInt32();
                        if (key == "general.architecture" && type == 8) return Str(f);
                        Skip(f, type);
                    }
                }
            }
            catch { }
            return null;
        }

        private static string Str(BinaryReader f) { return Encoding.UTF8.GetString(f.ReadBytes((int)f.ReadUInt64())); }

        private static void Skip(BinaryReader f, uint type)
        {
            switch (type)
            {
                case 0: case 1: case 7: f.ReadBytes(1); break;
                case 2: case 3: f.ReadBytes(2); break;
                case 4: case 5: case 6: f.ReadBytes(4); break;
                case 10: case 11: case 12: f.ReadBytes(8); break;
                case 8: Str(f); break;
                case 9:
                    uint inner = f.ReadUInt32(); ulong n = f.ReadUInt64();
                    for (ulong i = 0; i < n; i++) Skip(f, inner);
                    break;
                default: throw new InvalidDataException("GGUF type " + type);
            }
        }
    }
}
