# Audit request: 125% geometry-gate startup hang — facts only, 2026-10-28 ~11:30

Written for independent auditors (Claude subagent and Codex). This document states observations and
evidence paths only. It deliberately contains no builder hypotheses about cause or fix, so the audit
starts unbiased. Form your own view from the evidence.

## The candidate under verification

| | |
|---|---|
| Binary | `F:\Projects\Strata\dist\1.3.1-preview-d90a8fe\Strata.exe` |
| ProductVersion | `1.3.1+d90a8fe085a59eefe4e8c7f7b32854213b574a32` |
| SHA-256 / bytes | `628A2A52EA321AA727E81FF0CC09671AA9970F3D7BD4CDF6B40D75B5A55E1AF7` / 199,224,289 |
| Source commit | `56641b5` (branch `v1.3-premium-release`); public export ref `refs/public/v1.3.1-d90a8fe` |
| Prior verification | Phase 4 all green (`F:\Strata-work\phase4\20261028-001956`); unattended gates at 150% all green (`F:\Strata-work\repro150\20261028-*`), including this exact geometry configuration |
| Attended attempt | `F:\Projects\Strata\evidence\v1.3.1-d90a8fe\owner-gates\attempts\20261028-082136-13a9c99a` — 150% gates and 125% material PASS and sealed; 125% geometry has now failed three times |

## Issue under audit: the candidate's dock never becomes ready at the 125% geometry gate

Three occurrences, all in the attended runner, all with the identical signature:

| Occurrence | Candidate start | Evidence |
|---|---|---|
| 1 | 08:47:38 | `scales\125\geometry\worker-error.json` (time 08:48:09), sandbox log in `work\sandbox-geometry-125\strata.log` of that run |
| 2 | 09:45:01 | failure record `failures\failure-20261028-094647.json`, worker-error at 09:45:32 |
| 3 | ~11:05:50 | failure record `failures\failure-20261028-111557.json`; external watchdog saw the signature at 11:06:00.418 |

The signature, from the sandbox `strata.log` (3 lines total, every time):
1. `Taskbar safety suspension: StartupNotReady.` at ~+1 s after process start.
2. `Dock readiness timed out; the Windows taskbar remains visible.` at exactly +10 s.
3. `Strata stopped.` at ~+20 s (the worker's stop request — the process answers it promptly).

Never present: `Strata started.` (end of `App.OnStartup`), any `... failed:` exception line, any quota
fixture read lines, `taskbar-state.json` in the sandbox. The harness (`Start-CandidateStrata`,
`PhaseB.psm1`) polls UI Automation for the dock window for 20 s and never finds it. The gate fails
safe: the Windows taskbar remains visible throughout; restoration passes.

Gate configuration (`run-v13-owner-gates.ps1`, `Invoke-GeometryWorker`): sandbox settings
style Glass (default), pill, bottom, taskbarMode Hide, reserveSpace true, no pins, no start/search
buttons, leftWidgets empty, rightWidgets aiusage+cpu+ram+clock, quota fixtures enabled, all
automations disabled.

## Contrast: where the same binary starts cleanly

- 125% material gate, same mornings, minutes earlier in the same runner: three start/stop cycles,
  each logs `Strata started.` ~6 s in, taskbar applies, captures pass. (Different sandbox settings:
  style Clear, five pins, start/search buttons on, many widgets, no aiusage tile.)
- Overnight unattended runs at 150%: this exact geometry configuration passed, plus soaks.
- Four standalone reproduction attempts today, exact geometry sandbox recipe, candidate launched the
  same way (same env vars, installed dock stopped first):
  1. At 150%: `Strata started.` in 1.5 s.
  2. At 125%: 1.8 s.
  3. At 125% with a concurrent harness-style UIA FindFirst poller from t=0: 1.8 s; poller found the window immediately.
  4. At 125% launched 14.3 s after a live 150%→125% scale change, after a baseline `--restore-taskbar` step: 1.5 s.
  Scripts and transcripts: `F:\Strata-work\diag-startup-hang\` (diag-repro.ps1, diag-repro2.ps1,
  uia-poller.ps1, poller-*.txt, sandbox-*).

So far the hang has occurred only when the candidate is started by the attended runner's geometry
worker, and reproduced zero times outside it (4/4 clean standalone).

## Same-day environmental facts (established this morning, separate from the issue above)

- The machine has not rebooted since 2026-10-27 10:32. Store PowerShell is 7.6.9 since 2026-10-26 22:32.
- Between ~02:00 and 08:21 on 2026-10-28, in the same Windows session, new pwsh processes began
  starting with process DPI awareness pre-locked to SYSTEM_AWARE (verified by probes in
  `<dev-scratch>\dpi-probe*.ps1`).
  Yesterday evening's passing attended run used the same pwsh binary and tracked live scale changes.
  No AppCompatFlags DPI overrides exist for pwsh; no third-party injectors found; Windows Defender
  modules only. `PendingFileRenameOperations` is non-empty; no CBS/WU reboot-pending flags.
- Consequence (resolved): the runner's scale detection froze at launch-time scale. Fixed in harness
  commit `b073652` (`PhaseB.psm1`, per-thread DPI context for `PrimaryMonitorDpi`); harness suites
  pass in both shells. The fix is detection-only; it does not touch candidate startup.
- A watchdog (`F:\Strata-work\diag-startup-hang\gate-watchdog.ps1`) detected occurrence 3 live and
  ran `dotnet-dump collect` at +10 s; the attach failed: "Invalid process id 17916 - Access is
  denied. (5)" (the watchdog ran from a sandboxed/restricted shell). No dump exists yet.

## Relevant source (candidate commit 56641b5)

- `src/Strata/App.cs` — `OnStartup` (lines ~83–162), `ObserveDockReadiness` (~193), exception
  handlers wired at ~105–109. The readiness timer starts at line ~127, before the heavy startup work.
- `src/Strata/Views/DockWindow.cs` — constructor (built at OnStartup line ~138; `Show()` at ~149),
  heavily changed in 1.3.1 (wallpaper refraction layer, layered window, SystemEvents subscription).
- `src/Strata/Effects/` — new shader pipeline; `src/Strata/Core/Theme.cs` — new Glass/Liquid palettes.
- `tools/phaseb/PhaseB.psm1` — `Start-CandidateStrata` (~159), `Get-DockWindow` (~489, UIA),
  `New-PhaseBSandbox` (~108), `Initialize-CandidateBaseline` (~242).
- `tools/phaseb/run-v13-owner-gates.ps1` — `Invoke-GeometryWorker` (~243), `Run-Worker` (~622).

## Questions for the auditor (answer from evidence; propose whatever investigation you see fit)

1. What in-runner condition, absent from the four standalone reproductions, could explain a
   deterministic in-gate hang of `OnStartup` after the DockWindow constructor and before `Show()`
   completes/the window becomes UIA-visible?
2. Is there a 1.3.1 product change (candidate diff `81757aa..56641b5`, especially DockWindow,
   composition, wallpaper, shader, app groups) that can block startup >10 s without logging?
3. Do the three logged timings constrain where startup sits when the readiness timer fires? Note the
   timer tick requires the dispatcher; the process answers the exit signal at +20 s.
4. Is the readiness design itself (10 s window, fail-closed, timer stopped after timeout) implicated
   in the window never appearing to UIA?
5. Anything in the environmental facts section you judge connected rather than coincidental?
