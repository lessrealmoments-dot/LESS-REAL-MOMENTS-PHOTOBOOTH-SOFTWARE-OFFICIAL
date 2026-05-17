# Project Handoff Notes

Use this file at the end of every work session so we can quickly continue on any machine.

## Main Goal

Ship a stable Less Real Moments photobooth flow where:
- Sony camera capture is reliable.
- Layout packs (`template.xml` + assets + `preview.png`) produce a **sharp, correct** final composite.
- Print and operator workflow match dslrBooth-style production use (HiTi 4×6 / 2×6 strip).

## Current Snapshot

- App repo: **`less-real-moments-photobooth`** (GitHub).
- WPF guest flow: event → layout → countdown → capture → composite → final screen / print.
- **Print track (Phase 6 local):** largely done — dual printers, DEVMODE, layout routing, silent print + 1 s debounce.
- **Compositor track (Phase 2):** v1 works; **image quality upgrade planned** (libvips) — see **`docs/IMAGE_QUALITY_HANDOFF.md`**.

## Where We Left Off (Latest Session)

**Date: 2026-05-17 (Phase 2 libvips/NetVips compositor SHIPPED behind feature flag)**

### Done this session

**Image quality — libvips Phase 2 (SHIPPED)**
- `Services/Vips/VipsTemplateCompositor.cs` — drop-in replacement using `Image.Thumbnail` (Lanczos3 + JPEG shrink-on-load) and `Composite2(Over)`.
- `Services/Compositors.cs` — facade picks engine via `PrintBehavior.UseVipsCompositor` (default `false`); auto-falls back to WPF on probe failure or render error.
- Per-`PhotoNumber` decode cache (parity with WPF path) — same photo in 2 slots = 1 decode.
- New NuGet packages: `NetVips 3.1.0` + `NetVips.Native.win-x64 8.18.2` (libvips-42.dll lands at `runtimes\win-x64\native\` automatically).
- Primitive `(int, int)` overloads on `CompositeQualityDiagnostics` so non-WPF compositors don't need `BitmapSource`.
- A/B harness: `tools/SampleLayoutCheck --ab --runs=N` prints MAE/RMSE/PSNR + per-engine `composeMs`/`peakWS`.
- Verified PSNR ~32 dB on DENHAR strip — visually identical, kernel difference only. See `docs/IMAGE_QUALITY_HANDOFF.md` for benchmarks and the `[Composite] engine=…` log marker.

### Done (earlier in this session, before Phase 2)

**Print & alignment**
- Dual virtual printers, `template.xml` routing, `BoothPrintService` + alignment sliders (100% = 300 DPI centred size).
- Printer 2 can follow Printer 1 alignment; save/load alignment bug fixed (sliders vs text boxes).
- Guest print: **no success popup**; **1 second** Print button cooldown; errors still warn.

**Image quality — diagnostics (Phase A + B)**
- `[CompositeQuality]` logging on every `final/composite.png` (sources, slots, static assets, warnings).
- Composite PNG embeds **template ResolutionDpi** (300), not 96.
- Proved: compositor uses **`originals\` full JPEGs**; softness is **slot downscale** (~555×485 on STRIP), not thumbs/JPEG on print file.

**Tools & samples**
- `D:\PHOTOBOOTH SOFTWARE\sample layout` — STRIP/4R ZIPs + full-res `*_shot_*.JPG` for smoke tests.
- `tools/SampleLayoutCheck` — parse layouts; `dotnet run -- --compose` for vips-era smoke.

**dslrBooth reference scan**
- Install: `D:\PHOTOBOOTH SOFTWARE\dslrBooth ONLINE` — uses **libvips + NetVips**; per-photo `ResizeImageHighestQualityPossible` before composite.

### Not started

- **`final/composite_master.png`** at 2× for gallery (print file unchanged) — deprioritised pending real demand.
- Remote print PC queue (Phase 6).

### Next (Phase 2c — libvips polish & default switch)

1. **Cap libvips operation cache** (`NetVips.Cache.MaxMem = 50_000_000`) so peak working set stays flat across long sessions. Observed climb during A/B: 134 → 200 → 260 MB across 3 consecutive composes in one process.
2. **UI toggle** for `UseVipsCompositor` in `Views/GlobalSettingsWindow.xaml` so QA / operators don't have to edit `global_settings.json`.
3. **Port `OverlayHoleBounds`** alpha scan from WPF `BitmapSource` to libvips `vips_project` so the Vips path is fully WPF-free.
4. **Soak test** 50+ back-to-back composes at strip + 4R on the booth PC.
5. **Flip default** to `UseVipsCompositor = true`. Mark `TemplateCompositor.cs` (WPF) as a future deletion candidate.

## Quick Start Next Time

1. Open **`docs/PROJECT_HANDOFF.md`** (this file).
2. Open **`docs/IMAGE_QUALITY_HANDOFF.md`** if working on composites.
3. Open **`docs/PHOTOBOOTH_PHASES.md`** for full roadmap.
4. `git pull` (after fork: checkout your feature branch).
5. Build from `booth-desktop`:
   ```powershell
   cd "D:\PHOTOBOOTH SOFTWARE\booth-desktop"
   dotnet build -c Release
   ```
   Close `BoothDesktop.exe` first if copy fails.
6. Logs: `%UserProfile%\Documents\LessRealBooth\logs\runtime_*.log` (or custom storage root).

## Key paths

| What | Where |
|------|--------|
| Session originals | `{session}\originals\shot_XXX.jpg` |
| Print composite | `{session}\final\composite.png` |
| Master (planned) | `{session}\final\composite_master.png` |
| UI thumbs | `{session}\final\thumbs\` (not for print) |
| Global settings | `%LocalAppData%\LessRealBooth\global_settings.json` |
| Sample packs | `D:\PHOTOBOOTH SOFTWARE\sample layout` |
| dslrBooth reference install | `D:\PHOTOBOOTH SOFTWARE\dslrBooth ONLINE` |

## Update Template (copy each session)

```
Date: YYYY-MM-DD
Done:
- ...

Next:
1. ...

Logs:
- [CompositeQuality] ...
- [Print] ...
```
