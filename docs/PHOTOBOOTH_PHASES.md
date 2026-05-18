# Photobooth app — phased delivery (living roadmap)

Use this doc to pick up work after a break. It reflects the **repo as of early 2026** and the **dslrBooth-style layout ZIP** workflow (each pack: `template.xml` + `preview.png` + assets).

For quick session continuity (goal + last checkpoint), open `docs/PROJECT_HANDOFF.md` first.

**Image quality / libvips fork:** see `docs/IMAGE_QUALITY_HANDOFF.md`.

---

## Checkpoint — what already works

- **Guest flow (Phase 1):** opening → layout picker → per-shot flow → countdown → capture → slot fill → composite preview UI; dark/gold theme; session print/photo limits (mock decrements).
- **Sony bridge:** `BoothDesktop` can auto-launch `sony_bridge.exe` (prefers **same folder as `BoothDesktop.exe`**). Bridge serves `http://127.0.0.1:18080` (`/health`, `/live.jpg`, `/capture`, `/prefocus`, `/latest`). Live preview uses **shared memory** (`Local\SonyBridgeFrameMapV1`) when available, with HTTP fallback. Capture path pulls files into session **originals** via bridge.
- **Bridge behavior (recent):** capture is **not** blocked behind live-view mutex pacing; photobooth policy uses **skip-focus-gate** capture; countdown triggers capture at **zero** with optional prefocus arming earlier.
- **Layouts — import:** **Events → Import layout (ZIP)** unpacks under `Documents\LessRealBooth\catalog\layouts\`, parses `template.xml` for display name + shot count, finds **`preview.png` anywhere** in the extract for thumbnails.
- **Layouts — built-in trio:** `lay_af1`, `lay_h2`, `lay_h3` can show previews from **`preview.png` inside a `.zip`** placed in `Assets\Layouts\{id}\` (cached under `%LocalAppData%\BoothDesktop\layout_preview_cache\`), or loose `preview.png`, or overrides under `Documents\LessRealBooth\layout_previews\{id}\`.

---

## Layout pack contract (keep this in mind)

Standard dslrBooth export ZIPs are the source of truth:

| File | Role |
|------|------|
| `template.xml` | Layout metadata: name (`Name` on root when present), `<Photo PhotoNumber="…">` → unique numbers = distinct shots; duplicates = same image reused. Future: slot geometry + PNG references for compositing. |
| `preview.png` | Picker / card thumbnail only. |
| PNG assets | Referenced by template for final composite (Phase 2 engine). |

**Operator workflow today:** import each pack once via **Import layout (ZIP)…** (e.g. `cleo and heart af 1.zip`, `CLEO AND HEART 2.zip`, `CLEO AND HEART 3.zip`). ZIP **filename** is arbitrary; content matters. **Per-event layout visibility:** Events → **Layouts for this event…** (`events_registry.json`).

**Composite-first rule:** Treat each ZIP as the single source of truth—`template.xml` + bundled PNGs + session originals should produce **`final\composite.png`** without hand-moving assets. Harden that path (asset resolution, logging), **then** extend XML/schema fidelity for odd dslrBooth/Luma exports.

---

## Phase 1 — Guest UI shell

**Status: delivered** (polish only as needed).

- Event list + enter photobooth; layout grid; simulated vs real capture wiring; limits UI; theme.

---

## Phase 2 — Layout engine + event setup + camera hardening

**Status: in progress** — compositor v1 + quality diagnostics shipped; **libvips compositor upgrade** is next fork; live slot mask + per-event wizard remain.

**Done / partial**

- HTTP + shared-memory integration with `sony_bridge` for live + capture + prefocus.
- ZIP import, catalog JSON, recursive `preview.png` discovery, built-in zip preview support.
- **Template composite (v1):** After the last capture, resolve layout pack (`catalog` extract, loose `Assets/Layouts/{id}`, or full **ZIP** extract under `%LocalAppData%\BoothDesktop\layout_pack_cache\`), parse flexible dslrBooth-style `template.xml` (`Photo` slots + static image elements), **center-crop** guest shots into `PhotoNumber` rects, draw static assets in Z/sequence order, write `session\final\composite.png` and set `session.json` → `finalCompositeRelativePath`.
- **Composite quality diagnostics:** `[CompositeQuality]` logs per composite (sources, slot geometry, downscale warnings). PNG embeds **300 DPI** from template `ResolutionDpi`.
- **Sample / smoke:** `tools/SampleLayoutCheck`; packs in `D:\PHOTOBOOTH SOFTWARE\sample layout`.

**Phase 2b — Image quality (SHIPPED 2026-05-17) — see `IMAGE_QUALITY_HANDOFF.md` for full notes**

1. **DONE** — Phase 0 telemetry: `[CompositeQuality]` reports `composeMs`, `workingSetBefore/After/Peak`, `decodedFromNative`, `effectivePx`, per-slot cover metrics.
2. **DONE** — Phase 1 WPF RAM optimisation: JPEG shrink-on-load via `BitmapImage.DecodePixelWidth/Height` at 2× max slot edge; per-`PhotoNumber` decode cache. Photo decode dropped from ~60 MB to ~3 MB.
3. **DONE** — Phase 2 libvips/NetVips backend: `Services/Vips/VipsTemplateCompositor.cs` uses `Image.Thumbnail` (Lanczos3 + JPEG shrink-on-load) and `Composite2(Over)`. Per-`PhotoNumber` decode cache parity with WPF path. `Services/Compositors.cs` facade routes between WPF and Vips via `PrintBehavior.UseVipsCompositor` (default `false`) and falls back to WPF automatically if libvips probe fails or render throws.
4. **DONE** — A/B harness in `tools/SampleLayoutCheck`: `--ab --runs=N` renders both engines and prints MAE/RMSE/max diff/PSNR + file sizes. Verified PSNR ~32 dB on the DENHAR strip — visually identical, Lanczos3 vs Fant kernel difference only.
5. **DONE** — `KeepAspect=False` cover-crop default for photos (was: stretch); designer-only `IsLocked` layers skipped; overlay alpha holes (`OverlayHoleBounds`) drive the actual photo destination rect.
6. **NOT YET** — No sharpening pass (user explicitly deferred; Lanczos3 alone meets the bar).
7. **NOT YET** — `final/composite_master.png` 2× export (was Phase 2 master goal; deprioritised pending real demand from digital/gallery track).

**Phase 2c — libvips polish & default switch (IN PROGRESS)**

Pre-requisites for flipping `UseVipsCompositor` to `true` by default:

1. **DONE** — Cap libvips operation cache (`Cache.MaxMem = 50 MB`, `Cache.Max = 20 ops` in `VipsTemplateCompositor._available`). Verified: peak WS went from 134→200→260 MB (unbounded) to 126→160→162→166 MB (steady) across 4 back-to-back composes.
2. **DONE** — UI toggle in Global Settings → "Image quality (composite engine)" section with `UseVipsCompositor` checkbox + live libvips probe status hint. Persisted through `GlobalSettingsService.TrySave`.
3. **DONE** — Port `OverlayHoleBounds` to libvips. New `Services/Vips/VipsOverlayHoleBounds.cs` extracts the overlay's alpha band once per compose, then per slot does `image < 200 → FindTrim(background: [0])` to get the hole bbox. Vips path now has zero WPF imaging dependency. Verified pixel-diff bit-identical to the WPF-helper version (MAE 2.369, PSNR 31.93 dB unchanged) and warm Vips run improved from ~1012 ms / 181 MB peak to ~952 ms / 173 MB peak.
4. **TODO** — Soak test on the booth PC: 50+ back-to-back composes at strip + 4R sizes; confirm no RAM growth, no file lock issues, no driver / OOM regressions.
5. **TODO** — Flip default `UseVipsCompositor = true` after the soak. Mark `TemplateCompositor.cs` (WPF) as a future deletion candidate.

**Still to build (priority order)**

1. **Phase 2c above** — finish libvips rollout.
2. **Compile-from-Zip hardening:** `[Composite]` warnings; asset path fallbacks for odd ZIP layouts.
3. **Template fidelity:** rotation, text elements, alternate rect attributes.
4. **Live preview vs current slot:** mask live JPEG to active slot.
5. **New event wizard:** events without hand-editing `events_registry.json`.
6. **Optional:** QR / gallery stub (Phase 4); serve `composite_master.png` when present.

---

## Phase 3 — Media + orientation

- Per-event **start screen** (still image or MP4).
- **Intro MP4** per shot (MediaElement + missing-file fallback).
- **Portrait / landscape** lock; fullscreen / kiosk window.

---

## Phase 4 — Guest gallery (LAN)

- Lightweight HTTP server on booth PC: list session, originals + final, downloads.
- Prefer **`final/composite_master.png`** when present; fall back to `composite.png`.
- QR: `http://<booth-ip>:<port>/session/<id>`.

---

## Phase 5 — Sharing station (iPad)

- Same server; **station** route: browse sessions, multi-select, QR for selected files.

---

## Phase 6 — Print service

- **Implemented (local):** Two virtual printers in Global settings (Windows queue + copies each). `template.xml` routes layouts to printer 1 or 2 (`SecondaryPrinter`, `PrintTo`, `PrinterNumber`, or a `Printing` child node — dslrBooth “secondary printer”). `BoothPrintService` prints composite PNG via native driver defaults.
- **Still planned:** Companion on print PC: job queue (TCP or HTTP).
- Route jobs by layout / template flags (e.g. 4R vs strip).

---

## Native / parallel track (not gated on WPF phases)

- **`sony_bridge.exe`:** build from `native-poc` (CMake `build_fresh` or your VS output); **copy** next to `BoothDesktop.exe` for auto-launch consistency.
- **Virtual webcam for dslrBooth:** see `native-poc\VIRTUALCAM_NEXT_STEPS.md` (DirectShow real filter + BaseClasses; frames from `SonyBridgeFrameMapV1`). Separate from the WPF guest app.

---

*CrSDK stays in `native-poc`; BoothDesktop talks HTTP (and shared memory for preview) only.*
