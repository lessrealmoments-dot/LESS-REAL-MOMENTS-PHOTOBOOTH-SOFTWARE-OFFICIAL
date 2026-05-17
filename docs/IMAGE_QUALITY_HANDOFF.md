# Image quality & compositor ù fork handoff

**Read first when continuing work on sharper composites / libvips.**  
Companion docs: `PROJECT_HANDOFF.md` (session checkpoint), `PHOTOBOOTH_PHASES.md` (roadmap).

Last updated: **2026-05-17** (Phase 2 NetVips compositor shipped behind feature flag)

---

## Problem statement

- **Original captures** from Sony bridge (`session\originals\shot_XXX.jpg`) are sharp (often ~9504ù6336).
- **Final layout** (`session\final\composite.png`) can look softer than dslrBooth output on the **same strip layouts**, even though both target **1200ù1800 @ 300 DPI** for 4ù6 print.
- Goal: preserve real detail from originals **without** artificial sharpening or changing the print file contract.

---

## What we proved (diagnostics ù shipped)

**Phase A + B** are implemented in `main` as of 2026-05-17:

| Finding | Evidence |
|---------|----------|
| Compositor uses **full originals** | `TemplateCompositor` loads paths from `session.json` ? `originals\`; not live view, not `final\thumbs\` |
| Print uses **composite.png only** | `SessionPrintResolver` refuses `thumbs\` paths |
| Softness is mostly **slot pixel budget + resampling** | `[CompositeQuality]` logs: e.g. STRIP slots **555ù485**, `coverScale ? 0.077` from 9504ù6336 |
| PNG export had **96 DPI** metadata | Fixed: `RenderTargetBitmap(..., templateDpi, templateDpi)` embeds **300** (from `ParsedTemplate.ResolutionDpi`) |
| JPEG over-compression on composite | **No** ù composite is **lossless PNG**; thumbs are separate JPEG q=82 |

### Log location

- `Documents\LessRealBooth\logs\runtime_YYYYMMDD.log` (or custom storage root from `global_settings.json`)
- Search: **`[CompositeQuality]`**

### Sample layouts (operator machine)

- Folder: `D:\PHOTOBOOTH SOFTWARE\sample layout`
- ZIPs: Ross STRIP / 4R packs; JPGs: `*_shot_001.JPG` etc.
- Smoke tool: `booth-desktop\tools\SampleLayoutCheck`
  - Parse: `dotnet run` (default)
  - Compose smoke: `dotnet run -- --compose` ? writes `sample layout\_compose_smoke_out\composite_smoke.png`

### Reference STRIP geometry (Ross Musacchia Birthday STRIP.zip)

- Canvas: **1200ù1800**, Resolution **300**
- 6 photo layers, 3 unique captures
- Each slot: **555ù485** (left + right columns)

dslrBooth built-in ùThree poses, double stripù uses **600ù400** slots ù similar class, not larger by an order of magnitude.

---

## Current compositor architecture (Less Real Booth)

```
Capture ? originals\shot_NNN.jpg (File.Copy from bridge, optional RAW?JPEG sidecar)
       ? SessionWorkspace.TryComposeFinalPrint()
       ? TemplateCompositor.TryComposeToPng()
            - WPF DrawingVisual + DrawImageCover (photos)
            - DrawImage (static PNGs from layout ZIP)
            - RenderTargetBitmap(canvasW, canvasH, dpi, dpi)
            - PngBitmapEncoder ? final\composite.png
       ? SessionPrintResolver ? BoothPrintService (unchanged path)
```

**Key files**

| File | Role |
|------|------|
| `Services/TemplateCompositor.cs` | Composite render |
| `Services/CompositeQualityDiagnostics.cs` | `[CompositeQuality]` report |
| `Services/DslrTemplateParser.cs` | `template.xml` ? canvas + layers |
| `Services/SessionWorkspace.cs` | `TryComposeFinalPrint()` |
| `Services/SessionPrintResolver.cs` | Print path resolution |
| `Services/SessionThumbnailService.cs` | UI thumbs only (280/720 px JPEG) |

**UI vs print**

- Final screen preview is **scaled** (`Viewbox` ~520px) ù do not judge print quality from UI alone.
- Session grid uses `final\thumbs\composite_grid.jpg` ù not for print.

---

## dslrBooth reference (install scan)

Install folder used for analysis: `D:\PHOTOBOOTH SOFTWARE\dslrBooth ONLINE`

| Evidence | Implication |
|----------|-------------|
| `libvips-42.dll`, `NetVips.dll` | Print-quality pipeline uses **libvips** |
| `dslrBooth.Core.dll` strings: `ResizeImageHighestQualityPossible`, `OverlayUsingVips`, `calculateScaleAndCrop`, `CreateResizedImage`, `VipsThumbnail64Only` | **Per-photo high-quality resize** before composite; **separate** thumb path |
| Settings: ùCrop to size of photo placeholderùù | Fit to template rect, not preview size |
| Built-in templates | Same **1200ù1800** canvas; strip slots ~**600ù400** |

**Conclusion:** dslrBooth strip sharpness is not magic slot size; it is **libvips Lanczos-style downscale into each slot**, then composite. Our gap is **sampling architecture**, not wrong source files.

**GitHub references (implementation)**

- [libvips/libvips](https://github.com/libvips/libvips) ù core (`thumbnail`, `resize`, `composite`)
- [kleisauke/net-vips](https://github.com/kleisauke/net-vips) ù .NET binding (same stack dslrBooth uses)
- NuGet: `NetVips` + `NetVips.Native.win-x64` for BoothDesktop (.NET 8)

**libvips API guidance**

- Camera JPEG ? exact slot size: prefer **`Image.Thumbnail(path, width, height: ù)`** (shrink-on-load).
- General resize: **`resize`** with **`kernel: lanczos3`**.
- Avoid depending on `thumbnail_image` when loading from file path is possible.

---

## Agreed product contract (do not break)

| Artifact | Spec | Used by |
|----------|------|---------|
| `final/composite.png` | **1200ù1800 @ 300 DPI** (for standard 4ù6 portrait packs) | HiTi print, `SessionPrintResolver`, auto-print, event `prints\` mirror |
| `final/composite_master.png` | **Not implemented yet** ù planned **2ù** (2400ù3600 @ 600 DPI metadata) | Future gallery / digital delivery only |
| `originals\` | Full bridge copy | Compositor input only |
| `final\thumbs\` | JPEG previews | UI only |

**Explicitly out of scope for quality track**

- No sharpening / ùbeautyù on composite
- No change to capture / bridge
- No printing from master file
- No renaming `composite.png` without migration plan

---

## Phase 2 NetVips compositor ó SHIPPED 2026-05-17

The libvips/NetVips backend is wired in behind a feature flag. WPF compositor stays as the default and as the automatic fallback if libvips fails to load or a render throws.

### Files

| File | Role |
|------|------|
| `Services/Vips/VipsTemplateCompositor.cs` | libvips backend (Lanczos3, JPEG shrink-on-load, RGBA `Composite2`). Mirrors `TemplateCompositor` public API and respects `OverlayHoleBounds`. |
| `Services/Compositors.cs` | Facade that picks WPF or Vips per `PrintBehavior.UseVipsCompositor` and falls back to WPF on Vips probe failure or render exception. |
| `Models/BoothModels.cs` | New `PrintBehavior.UseVipsCompositor` (default `false`). |
| `Services/CompositeQualityDiagnostics.cs` | New primitive (int) overloads so non-WPF compositors don't need `BitmapSource`. |
| `Services/SessionWorkspace.cs` | `TryComposeFinalPrint` calls `Compositors.TryCompose(preferVips, Ö)`. |
| `tools/SampleLayoutCheck/Program.cs` | New flags: `--vips`, `--wpf`, `--ab`, `--runs=N`. `--ab` renders both engines, prints MAE/RMSE/max diff/PSNR, file sizes. |

### Packages

```xml
<PackageReference Include="NetVips" Version="3.1.0" />
<PackageReference Include="NetVips.Native.win-x64" Version="8.18.2" />
```

`libvips-42.dll` lands in `bin\Ö\runtimes\win-x64\native\` automatically (NuGet RID convention). .NET 8's runtime resolver loads it on first NetVips call.

### How to flip the flag

Edit `%LOCALAPPDATA%\LessRealBooth\global_settings.json`:

```json
"printBehavior": {
  "useVipsCompositor": true
}
```

Restart the app. Verify with `[Composite]` log line: `engine=Vips session=Ö`.
If libvips fails to load, the next log line is `[Compositors] UseVipsCompositor=true but libvips is unavailable Ö falling back to WPF`.

### A/B smoke

```powershell
$tool = "booth-desktop\tools\SampleLayoutCheck\bin\Release\net8.0-windows\SampleLayoutCheck.exe"
& $tool "--pack=<layout_dir>" "--shots=<originals_dir>" "--out=D:\diff.png" "--ab" "--runs=3"
```

Outputs `D:\diff.wpf.png`, `D:\diff.vips.png`, and a pixel-diff summary.

### Verified results (2026-05-17, DENHAR 6-photo strip layout, 3 unique originals across 6 slots)

| Engine | composeMs (warm, run 3) | Peak WS | Notes |
|---|---|---|---|
| WPF | 816 ms | 135 MB | Fant resample, Pbgra32 |
| Vips | 1012 ms | 182 MB | Lanczos3, RGBA `Composite2`, per-photo decode cache |
| Pixel diff (WPF?Vips) | MAE 2.37, RMSE 6.46, max 144, **PSNR 31.9 dB** | | Near-identical structural output; kernel differences only |

Vips runs ~25 % slower wall time and uses ~50 MB more RSS than WPF on this layout. Quality (Lanczos3 vs Fant) is the win ó visually identical layout, sharper resampling at heavy downscales (`coverScale ? 0.15`). Both engines complete well under the 3 s print budget.

### Known limitations / Phase 3 candidates

1. `OverlayHoleBounds` still uses WPF `BitmapSource` for alpha scan on the Vips path (one cheap load per compose). Port to libvips `vips_project` to make Vips path fully WPF-free.
2. libvips operation cache grows over consecutive composes in the same process (peak WS climbs 134?200?260 MB across 3 runs). Cap it via `NetVips.Cache.MaxMem = 50_000_000` if it bites in long sessions.
3. UI toggle for `UseVipsCompositor` not yet added ó flip via JSON for now.
4. No A/B sharpening pass; Lanczos3 alone already meets the quality bar requested.

---

## Original plan (Phases 0ñ4) ó superseded by the above where overlapping

### Strategy: orchestrator + dual backend + fallback

```
TryComposeFinalPrint()
  ?? TemplateComposeService (new)
       ?? composite.png        scale=1, backend=Vips|Wpf
       ?? composite_master.png scale=2, optional
```

### Phase 0 ù Bootstrap

- Add `NetVips` + `NetVips.Native.win-x64` to `BoothDesktop.csproj`
- `Services/Vips/VipsBootstrap.cs` ù init, version log
- Smoke: thumbnail one sample JPG ? 555ù485

### Phase 1 ù Vips photo slots + WPF assembly (recommended first ship)

- `VipsPhotoSlotRenderer` ù centre-cover from file path ? exact slot WùH (lanczos3 / thumbnail)
- `TemplateCompositor` ù draw **pre-sized** slot bitmaps 1:1 (no `DrawImageCover` from full 24MP in one pass)
- Settings: `CompositeBackend` = `Wpf` | `Vips` | `VipsWithWpfFallback` (default fallback until proven)
- Static overlays: still WPF at first (pack assets are 1200ù1800 @ 1:1)

### Phase 2 ù Master export

- After successful `composite.png`, compose `composite_master.png` at `scaleFactor=2`
- `session.json`: optional `finalCompositeMasterRelativePath`
- Master failure = warn only; print must succeed

### Phase 3 ù Full vips composite (optional)

- Static layers via `Composite2` / `OverlayUsingVips` pattern
- Run compose off UI thread; marshal preview only

### Phase 4 ù Gallery (separate PR)

- Phase 4 HTTP gallery serves master when present

### Settings (proposed fields in `GlobalAppSettings` / `PrintBehavior`)

```json
"compositeBackend": "VipsWithWpfFallback",
"masterExportScale": 2
```

`PrintSharpening` ù **do not implement** unless product explicitly requests; field exists but unused.

---

## Other session work (already shipped ù same fork base)

### Printer alignment (Global settings)

- Sliders + preview; **CommitAlignmentToDraft** on save only (not on every slider tick)
- **Follow Printer 1** copies alignment to printer 2 on save
- `BoothPrintLayout`: 100% = natural size @ 300 DPI centred; alignment offsets on top
- Debug `DebugAgentLog` **removed** after verification

### Guest print UX

- Silent print on success (no success `MessageBox`)
- 1 s cooldown on Print button (double-tap guard)
- Errors still show warning dialog

---

## Verification checklist (after vips work)

1. Same STRIP ZIP + same three originals: A/B `composite.png` at **100%** zoom (face regions in slots).
2. `[CompositeQuality]` with `backend=Vips`, `scale=1` and `scale=2`.
3. HiTi print from **`composite.png` only** ù alignment unchanged.
4. Compare to dslrBooth output same pack/session if possible.
5. Memory: complete 3-shot STRIP + 2ù master on target booth PC without OOM.

---

## Fork branch suggestion

- Branch name: `feature/vips-composite` or `feature/image-quality-vips`
- First PR: Phase 0 + 1 + diagnostics tag `backend=`
- Second PR: `composite_master.png` + manifest field
- Keep `tools/SampleLayoutCheck --compose` updated for CI-less smoke

---

## Update template (per session)

```
Date:
Done:
- ...
Next:
1. ...
Logs checked:
- runtime_*.log [CompositeQuality]
- runtime_*.log [Print]
```
