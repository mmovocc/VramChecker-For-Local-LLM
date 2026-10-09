# local_llm_vram_console 0.1.0

A Windows desktop widget and console that free GPU memory for a local LLM, check whether a llama-server
profile fits, and launch and watch it. Built on the LiquidGlass.WinForms renderer (`GpuVramWidget.exe`).

Previous builds: `v2026.10.07-launcher` → `v2026.10.07-dotmatrix-gpuprefs-terminal` → `…-solid`.

## New in 0.1.0 — healthy context
- **Your script's `--ctx-size` is the healthy baseline.** Launches always use it; the "launch at max ctx" switch is gone.
- **Reference limits beside it:** memory limit (no spill into system RAM, 512 MB headroom) and speed limit (decode ≥ 30 t/s with the context full; 25 / 30 / 35 selectable).
- **Test-free estimate** from llama-fit-params, model files and the draft's GGUF header, with coefficients measured on an RTX 4080:
  `VRAM = fit fixed + draft/projector + ctx × fit per-token × 1.075`, `decode ms/token = (main GB × 2.22 + filled K × KV MiB/1K × 0.0056) ÷ speculative speedup (DFlash 1.69)`.
  Replaces the earlier estimate that ignored draft-model memory and decode speed and suggested contexts that spilled.
- **Status:** red "Would spill", amber "Tight" (inside the safety margins only) or "< 30 t/s when full", otherwise "Fits now".
- **Live spill detection:** a running llama-server with > 1 GB of GPU allocations in system RAM is flagged.
- **Optional CALIBRATE** (console › 02): measures one profile in a hidden server on 127.0.0.1 (~2–4 min).
- Measured on IQ3_XXS + DFlash2: 12,377 MB + 23.0 MB per 1K ctx; spill near 196K drops prefill 1,365 → 141 t/s and decode 87 → 32 t/s; decode ~50 t/s at 110,592 full. IQ3_S + mmproj (no draft): ~27 t/s at 110,592 full.

## Included from earlier builds
- Dot-matrix UI in English (widget, launch console, menus, dialogs); solid card by default, dark / light / legacy glass optional.
- One-click clean (graceful close, then force) → launch → monitor; restore closed apps.
- llama-server in a Windows Terminal tab (PowerShell); stopping from the widget closes that tab.
- GPU preferences list with a startup snapshot and one-click restore.
- Pre-flight via llama-fit-params; D3DKMT VRAM budget.

## Files
`GpuVramWidget.exe`, `LiquidGlass.WinForms.dll`, `LiquidGlass.Native.dll`, `LiquidGlass.hlsl` (keep together), `GlassDemo.exe`.
Settings, calibration and the GPU-preference snapshot live in `%APPDATA%\GpuVramWidget`.

## License
The `ui/` presentation layer is ported from karminski's dot-matrix-dashboard-ui (CC BY-NC-SA 4.0): non-commercial
use, attribution, share-alike. See `THIRD-PARTY-NOTICES.md`.
