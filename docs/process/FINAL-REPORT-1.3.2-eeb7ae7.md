# Strata 1.3.2 — final acceptance report, 2026-10-28

Written for the owner and any future coder picking up this project.

## Shipped

**Strata 1.3.2+eeb7ae7b66ec4e2f171a798d5af1b4a3e3dc0b3c is installed and running** at
`%LOCALAPPDATA%\Programs\Strata\Strata.exe` (SHA-256 `ADAD5A9A6A221D828895EE049184FD701361992660C26A4A499820CFB28A6CEE`,
199,232,481 bytes, verified after copy). Installed 2026-10-28 ~19:04 with the owner's authorization;
first real-profile start completed in ~1 s with a clean taskbar handoff. The previous build is
preserved at `F:\Strata-work\backups\Strata-1.3.0-370dec2.exe` (hash-verified copy); rollback is a
stop → copy back → start.

## What 1.3.2 delivers over 1.3.0

1. **Liquid glass, real.** The dock is a pure layered window composing no DWM material: the softened
   wallpaper strip is the glass, bent at the rim by an SDF lens shader, full-pill silhouette. (DWM
   backdrops compose black for this window class — a root cause found this cycle; never reintroduce
   them for the dock.) Default Glass gets app-owned frost; Dark untouched; accessibility fallbacks
   pixel-identical.
2. **App groups.** Drop a pinned app onto another's central half → a group tile (2×2 icon, founders'
   name, member windows/badges/previews, flyout, Ungroup). Pure `DockGroups` transforms, fully tested.
3. **Media artwork fix** (late-arriving thumbnails no longer lose the cover).
4. **Startup can never again be blocked by a quota read.** The 1.3.1 release candidate was caught by
   the owner gates launching a quota CLI on the UI thread during startup (proven by an in-gate process
   dump); 1.3.2 moves all provider reads off the UI thread (`Task.Run` + `QuotaReadRunner`) with a
   10 s budget, admission control, and guaranteed child cleanup. 682/682 product tests.

## Verification chain (all records on F:)

- Phase 4: `F:\Strata-work\phase4\20261028-1313-1.3.2-eeb7ae7\evidence` (all seven steps).
- Unattended 125% repros: `F:\Strata-work\repro132`.
- Attended owner gates: PASS, 23/23 records, 194/194 artifact hashes — attempt
  `F:\Projects\Strata\evidence\v1.3.2-eeb7ae7\owner-gates\completed\20261028-170933-59dba4c6`.
- Independent audits: `AUDIT-STARTUP-HANG-20261028-claude.md`, `-codex.md`, Codex closeout
  `CODEX-CLOSEOUT-1.3.2-eeb7ae7-20261028.md`, Claude signoff `FINAL-REVIEW-1.3.2-eeb7ae7.md` (this folder).
- Source: branch `v1.3-premium-release`, product `2a601db`, harness `5027d1d`, public ref
  `refs/public/v1.3.2-eeb7ae7`. Public export retained at `F:\Strata-public-1.3.2-2a601db\repo`.
- `pending-gates.json` records the install (authorized by the owner in chat); publication remains
  unauthorized and unperformed.

## The day's defects, for the record

1. **pwsh DPI pre-lock** froze the gate runner's scale detection (environmental, appeared in-session
   overnight). Harness fix `b073652`: per-thread PER_MONITOR_AWARE_V2 read.
2. **In-gate startup hang** (4/4 at 125% geometry): quota fixture launch stuck inside CreateProcess on
   the UI thread. Root-caused by in-harness dump capture (`Save-CandidateHangCapture`, `2d8370d`);
   fixed in product (`8b05994`, `2a601db`); why CreateProcess wedged only in-gate that morning remains
   unproven (Defender half-applied update suspected; pre-reboot baseline unreconstructable) and no
   longer matters to the product.
3. Known cosmetic harness nit: the final summary's `scalesCaptured` omits 150 (records are complete);
   fix next cycle along with the per-candidate hardcoded ExpectedVersion prefix.

## Cleanup performed (owner-directed)

~8.5 GB removed: superseded 1.3.1 candidate + export, all phase-4 build workspaces (evidence kept),
old candidate build trees, the 411 MB hang dump (its stack analysis preserved beside the hang
evidence), research clones (incl. CRITICAL-scanned Windhawk mods), obsolete pre-370dec2 rollback
binary, and every diagnostic/temp folder and scratch file from the cycle. `F:\Strata-work` now holds
only: toolchain (`.net`, `dotnet-home`, `nuget`), `backups`, `handoff`, `install` records, phase
evidence (`phase1`, `phase4`), repro evidence (`repro132`, `repro150`), and `artifacts` identity
records — 1.07 GB total.
