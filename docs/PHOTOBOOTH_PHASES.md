# Photobooth app — phased delivery (living roadmap)

Use this doc to pick up work after a break. It reflects the **repo as of early 2026** and the **dslrBooth-style layout ZIP** workflow (each pack: `template.xml` + `preview.png` + assets).

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

**Status: in progress** — compositor v1 shipped; live slot mask + per-event wizard remain.

**Done / partial**

- HTTP + shared-memory integration with `sony_bridge` for live + capture + prefocus.
- ZIP import, catalog JSON, recursive `preview.png` discovery, built-in zip preview support.
- **Template composite (v1):** After the last capture, resolve layout pack (`catalog` extract, loose `Assets/Layouts/{id}`, or full **ZIP** extract under `%LocalAppData%\BoothDesktop\layout_pack_cache\`), parse flexible dslrBooth-style `template.xml` (`Photo` slots + static image elements), **center-crop** guest shots into `PhotoNumber` rects, draw static assets in Z/sequence order, write `session\final\composite.png` and set `session.json` → `finalCompositeRelativePath`.

**Still to build (priority order)**

1. **Compile-from-Zip (finish before deep XML work):** Prove `final\composite.png` against your real packs—only files inside the pack + originals. Use `Documents\LessRealBooth\logs\runtime_*.log` (`[Composite]` lines) when a static asset or slot is skipped; asset paths fall back to **same file name anywhere under the pack** when exact relative paths do not match extract layout.
2. **Template fidelity:** Extend `DslrTemplateParser` / draw order for real dslrBooth/Luma elements (text, rotation, alternate rect attributes) once compositing is visually close.
3. **Live preview vs current slot:** Mask or frame live JPEG to the active slot when layout geometry is known.
4. **New event wizard:** Name, caps, optional start asset hooks (add/rename events without hand-editing `events_registry.json`).
5. **Optional:** QR payload / deep link stub for future gallery URL.

---

## Phase 3 — Media + orientation

- Per-event **start screen** (still image or MP4).
- **Intro MP4** per shot (MediaElement + missing-file fallback).
- **Portrait / landscape** lock; fullscreen / kiosk window.

---

## Phase 4 — Guest gallery (LAN)

- Lightweight HTTP server on booth PC: list session, originals + final, downloads.
- QR: `http://<booth-ip>:<port>/session/<id>`.

---

## Phase 5 — Sharing station (iPad)

- Same server; **station** route: browse sessions, multi-select, QR for selected files.

---

## Phase 6 — Print service

- Companion on print PC: job queue (TCP or HTTP).
- Route jobs by layout / template flags (e.g. 4R vs strip).

---

## Native / parallel track (not gated on WPF phases)

- **`sony_bridge.exe`:** build from `native-poc` (CMake `build_fresh` or your VS output); **copy** next to `BoothDesktop.exe` for auto-launch consistency.
- **Virtual webcam for dslrBooth:** see `native-poc\VIRTUALCAM_NEXT_STEPS.md` (DirectShow real filter + BaseClasses; frames from `SonyBridgeFrameMapV1`). Separate from the WPF guest app.

---

*CrSDK stays in `native-poc`; BoothDesktop talks HTTP (and shared memory for preview) only.*
