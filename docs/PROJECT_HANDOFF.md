# Project Handoff Notes

Use this file at the end of every work session so we can quickly continue on any machine.

## Main Goal

Ship a stable Less Real Moments photobooth flow where:
- Sony camera capture is reliable.
- Layout packs (`template.xml` + assets + `preview.png`) produce a correct final composite.
- Event setup and operator workflow are simple enough for live production use.

## Current Snapshot

- App repo is now on GitHub: `less-real-moments-photobooth`.
- WPF guest flow is working (event -> layout -> countdown -> capture -> composite preview path).
- Layout ZIP import and compositor v1 are already implemented.
- Highest priority is still validating and hardening real pack compositing.

## Where We Left Off (Latest Session)

Date: 2026-04-29
Done:
- Published `booth-desktop` to GitHub.
- Added baseline `.gitignore`.

Next:
1. Run real event layout ZIPs end-to-end and verify `final/composite.png` output quality.
2. Review runtime logs for any skipped assets or slot mapping issues.
3. Improve template fidelity only after compile-from-zip output is visually correct.

## Quick Start Next Time

1. Open this file first (`docs/PROJECT_HANDOFF.md`).
2. Open roadmap (`docs/PHOTOBOOTH_PHASES.md`) for full detail.
3. Pull latest changes:
   - `git pull`
4. Build and run the desktop app from `booth-desktop`.

## Update Template (copy each session)

Date: YYYY-MM-DD
Done:
- ...
- ...

Next:
1. ...
2. ...
3. ...
