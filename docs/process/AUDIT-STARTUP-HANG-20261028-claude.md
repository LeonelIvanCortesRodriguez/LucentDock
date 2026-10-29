# Independent audit: 125% geometry-gate startup hang (candidate 1.3.1+d90a8fe, source 56641b5)

Auditor: Claude (fresh session, read-only). Written 2026-10-28. Nothing was launched, killed, installed or modified; no display or
system state was touched. Evidence read: the handoff request, the attempt directory
`F:\Projects\Strata\evidence\v1.3.1-d90a8fe\owner-gates\attempts\20261028-082136-13a9c99a` (worker-error/run/progress, the three
failure-*.json, the `superseded\` copies for occurrences 1 and 2, the preserved `work\sandbox-*` folders), `F:\Strata-work\diag-startup-hang`,
earlier handoffs (`HANG-ROOT-CAUSE-AND-PLAN.md`, `AUDIT-POST-81757AA-20261027.md`), the full evidence tree's `strata.log` history, and source
(`git diff 81757aa..56641b5`, `App.cs`, `DockWindow.cs`, `WindowComposition.cs`, `WallpaperGlass.cs`, `LiquidGlassEffect.cs`, `Theme.cs`,
`MaterialPolicy.cs`, `Quota*`, `PhaseB.psm1`, `run-v13-owner-gates.ps1`).

## Verdict

**The cause is not identified, and the current evidence cannot identify it.** What it does establish is narrow and useful: the candidate's UI
thread was *alive and dispatching* (timer tick at +10 s, exit signal served at +20 s) while `App.OnStartup` had *not completed*, and the stall
sits between the `DockWindow` constructor returning (App.cs:138) and the first line the AI-usage widget logs. That combination excludes every
"UI thread blocked in a plain non-pumping call" theory (file I/O, SendMessage, DWM/Explorer round-trip, Process.Start) and leaves only (a) a
message-pumping wait inside first-window creation, or (b) OnStartup aborted by a failure whose log line never landed. A stack of the hung
process decides between them in one shot; no stack has been captured yet. The four "4/4 clean" standalone reproductions are also weaker
controls than the handoff presents (see Section 3), so "reproduces zero times outside the gate" should not yet be read as "the product is fine
outside the gate" or "the harness is to blame".

---

## 1. Facts I verified (and two corrections to the record)

**Timeline of the preserved occurrence (3), `work\sandbox-geometry-125\strata.log` (the only sandbox log of the three that still exists):**

| Time | Event | Source |
|---|---|---|
| 11:05:48.19 | Geometry worker (pwsh 7.6.9, pid 13016) starts | worker-run.json |
| 11:05:49.19 | worker step "Geometry start"; hash of the 199 MB candidate precedes sandbox creation | worker-progress.log |
| 11:05:49.47-.53 | sandbox files written; `phaseb.pid` (17916) written right after `Process.Start` returns | file mtimes |
| 11:05:49 | `Taskbar safety suspension: StartupNotReady.` (App.cs:123) | strata.log |
| 11:05:59 | `Dock readiness timed out` (App.cs:197-201) | strata.log |
| 11:06:09 (request at .64) | `Strata stopped.` after the worker's 20 s poll gave up | strata.log, geometry-accessibility.json |

Sandbox state after the run, compared with the passing 150% geometry run of the same attempt (08:22) and the passing standalone runs:

| Artifact | Hung run | Healthy runs |
|---|---|---|
| `quota.json` | 496 B, mtime = creation (never rewritten) | rewritten (~661 B) at startup |
| `automations.json` | 522 B, never rewritten | rewritten (3.5 KB) |
| `taskbar-state.json` | absent | present |
| `Codex/Claude usage read failed: NotInstalled` lines | absent | present, *before* `Strata started.`, same second |
| `fixture-claude-config` dir | created by harness | same |

Source-order consequences (all line numbers at 56641b5):

1. A readiness-timeout line can only be written when `_dock != null` (App.cs:~193 `if (_dock is null) return;`), so the `DockWindow` constructor
   at App.cs:138 **returned**.
2. `QuotaWidget`'s constructor calls `QuotaService.For` and `_monitor.Tick()` (QuotaWidget.cs:~30-35); `Tick` runs the Codex and Claude reads
   synchronously up to their NotInstalled failure, which is where the two log lines and the later `quota.json` save come from. Neither happened.
   The widget is built inside `DockWindow.Rebuild()` (DockWindow.cs:214), which runs inside `OnSourceInitialized` (DockWindow.cs:188-211),
   which runs inside `_dock.Show()` (App.cs:149). `Automations.Start()` (App.cs:152, the first `automations.json` write) is after `Show()`.
   So the stall is in `[ctor return, first QuotaWidget log line]`: the rest of `Show()` up to HWND creation, `OnSourceInitialized` (hotkeys),
   or `Rebuild()` up to and including the start of the `aiusage` widget (`CloseFlyout`, `MaterialPolicy.Decide`, `Theme.Apply`,
   `ApplyLiquidGlassEffect` -> `UpdateWallpaperRefraction`, layout assembly, `QuotaService.For`, the first `Process.Start`).
3. The `DispatcherTimer` (250 ms, App.cs:~61) fired its tick at +10 s and `Dispatcher.BeginInvoke(Quit)` (App.cs:~170, `ListenFor`) ran at
   +20 s. Both need the UI thread to dispatch. The 1-second log resolution is consistent with the tick landing on its first eligible 250 ms
   slot (+10.0 to +10.25 s) in all three occurrences, i.e. no sign of a stall that merely ended at a random time after +10 s.
4. If the UI thread had been blocked in a *non-pumping* call from about +1 s onward, neither (3) event could have happened on time. A hung
   `SHAppBarMessage`, `SetWindowCompositionAttribute`/`DwmSetWindowAttribute`, a WIC decode, a registry or file wait, or a `CreateProcess` stall
   is therefore **not** what the evidence shows, unless it released exactly at +10.0 s and again by +20 s, which I treat as implausible.
5. A plain exception inside `OnStartup` would be routed to `OnUiException` (App.cs:105, 160-ish) and `FailureTracker.Record` writes
   `ui-thread failed: ...` first thing (FailureTracker.cs:21). No such line exists. That is the only thing that keeps "OnStartup aborted,
   dispatcher idle, window half-built" (which would produce this log trio perfectly) from being the leading explanation.

**Corrections to the handoff:**

- *The three-line signature is verified for occurrence 3 only.* The sandbox for occurrences 1 and 2 was deleted when the gate re-ran
  (`Invoke-GeometryWorker` removes `sandbox-geometry-$scale` first, run-v13-owner-gates.ps1:~247); `superseded\094458-geometry` and
  `110547-geometry` hold only worker records. Their worker-error/run files agree on timing (worker start, 20 s poll, stop request at about
  +20 s, "candidate dock window did not appear"), but the claims "exactly +10 s", "no `Strata started.`", "no exception line" for 1 and 2 are
  unverifiable from the tree.
- *The 6-second startup is not new and not geometry-specific.* Across the 253 `strata.log` sessions in the evidence tree, 15 Material-gate
  candidates (Clear style, pins) took 5-6 s to reach `Strata started.` (10-27 and 10-28, scales 125/150/200, opacity 0/40/100), including three
  of today's six material starts. Standalone and geometry starts take 0-1 s. That is a pre-existing, unexplained ~5 s startup stall that occurs
  in the gate context, in 1.3.0 as well, roughly 6 percent of the time. It is the same family of evidence as the 8-11 s stalls in
  `AUDIT-POST-81757AA` (125% only). The only previous "never started" sessions in the tree are the 1.3.0 `ade3a32c` defect attempt
  (10-26 14:20, old harness) and the controlled Explorer-restart sandbox (expected).

**Where the new product code is on this path (and where it is not):**

- `App.cs` is **unchanged** in `81757aa..56641b5`. OnStartup order, the readiness design, and the handlers are identical to 1.3.0.
- Everything in the stall interval that is new or changed in 1.3.1 lives in `DockWindow`/`WindowComposition`/`Effects`/`Theme`:
  `AllowsTransparency = true` (DockWindow.cs:153), `SystemEvents.UserPreferenceChanged +=` in the ctor (:168, before the ctor returns, so
  not the stall), `ApplyLiquidGlassEffect()` (:255, :403-418), `UpdateWallpaperRefraction()` (:438-500) with
  `Effects.LiquidGlassEffect.IsSupported` (LiquidGlassEffect.cs:38-39: `RenderCapability.Tier` + `IsPixelShaderVersionSupported`, **first use of
  `RenderCapability` anywhere in the product**), `System.Windows.Forms.SystemInformation.VirtualScreen` (:462, first WinForms touch) and
  `WallpaperGlass.GetStrip` (WallpaperGlass.cs:118-161: registry, `File.Exists`, `FileStream`, `BitmapDecoder.Create`, `WriteableBitmap`, all
  synchronous on the UI thread, no timeout, blanket `catch { return null; }`).
- **But the material gate exercises that same code in the same runner at 125% and passes.** Clear opacity 40 is `NativeAcrylic` +
  `Liquid` (MaterialPolicy.cs:96-103), so it evaluates `IsSupported`, `GetStrip`, the shader branch, and the layered window; those three
  candidates started in 2 s at 125% and 1-2 s at 150%. Glass (the geometry style) differs from Clear/40 before the first widget only by taking
  the flat-tint `else` branch (DockWindow.cs:~486-492) instead of creating the shader. So **no pre-widget 1.3.1 code is exclusive to the geometry
  configuration.** What is exclusive to it is the `aiusage` widget (and empty pins/left widgets), which is inside the stall interval only
  through `QuotaService.For`/`Tick`/the first `Process.Start`, none of which changed in 1.3.1.

---

## 2. Ranked hypotheses

Confidence is my subjective probability that the hypothesis (or its mechanism family) is the explanation, not a measurement.

### H1. In-gate cross-process dependency stalls a pumping wait in the candidate's first-window creation (35%)

*Mechanism.* The candidate UI thread is inside a message-pumping wait (managed STA wait, COM call, or a nested Windows modal pump) during
`Show()` / `OnSourceInitialized` / early `Rebuild`. The thing it waits for depends on a counterparty that exists only in the gate: the worker's
UIA client, the owner-session full-token desktop (Explorer, DWM, TSF/ctfmon, input services), or a re-entrant `WM_GETOBJECT` served *into* the
half-built WPF tree (the pumping wait lets UIA's incoming sent message run `Window.OnCreateAutomationPeer`/layout while `Rebuild` is mid-flight).
Because the wait pumps, the 250 ms readiness tick and the later `Quit` run on time, exactly as observed. The 20 s harness poll is the same
length as the failure, which may be causal (UIA keeps re-triggering) or merely the harness's own deadline; the candidate was never left alone
to show whether it would recover.

*For.*
- Dispatcher alive + OnStartup incomplete is what a pumping wait produces (Section 1, items 3-4).
- `Start-CandidateStrata` (PhaseB.psm1:167-182) begins UIA `FindFirst` polling at t=0 every 200 ms, so a poll lands on window creation
  essentially every time. The only standalone "poller" run (repro 3) started its poller via a second `Start-Process pwsh` *after* the candidate,
  a roughly 1-1.5 s head start; its first hit was at the moment the window already existed (`FOUND ... FindFirst 39 ms`, log `Started` same
  second), so it did not reproduce the in-gate overlap with window creation.
- The same gate context produces unexplained ~5-6 s startup stalls in about 6 percent of material starts and 8-11 s dispatcher stalls at 125% in
  1.3.0 (AUDIT-POST-81757AA); 5 s is the Windows hung-window / `SendMessageTimeout` horizon. A cross-process wait that is released by a
  timeout fits both the short stalls and, if the dependency keeps re-arming, a long one.
- Environment is hostile at 125% specifically: every locked pwsh (runner, worker, UIA client) sees System DPI 144 against a live 120, while at
  150% locked and live agree. That asymmetry exists only in-gate at 125%.

*Against.*
- Material workers use the same `Start-CandidateStrata`, the same t=0 polling, at 125% and 150%, and pass 6/6 (with some 6 s starts).
- 150% geometry (same config, same runner) passed at 08:22.
- I cannot name the pumping wait. I found no `Task.Wait`, `.Result`, `WaitOne`, `Join`, `MessageBox`, `ShowDialog`, `PushFrame` or `DoEvents` in `src/Strata`;
  any such wait would have to be inside WPF/WinForms/COM internals reached from the listed calls.
- Specificity to geometry/125% is unexplained.

### H2. A 1.3.1-introduced defect on the Glass/layered-window/fractional-scale path that the standalone controls did not actually exercise (20%)

*Mechanism.* The new layered dock (`AllowsTransparency = true`), `RenderCapability` query, wallpaper strip, or sizing feedback
(`SizeChanged -> PositionWindow -> UpdateWallpaperRefraction`, DockWindow.cs:176, :513-526) misbehaves in the owner's real session at 125% and
works in the builder's shell because the shell's token/desktop/wallpaper access differs.

*For.*
- 1.3.0 passed 125% geometry in the attended runner every time (about 10 attempts, 10-26/27); 1.3.1 has failed 3/3. All code that changed in
  the stall interval is 1.3.1.
- The builder's shell is demonstrably not the owner's context: `dotnet-dump` run from the watchdog got *Access is denied* opening the
  gate-launched candidate (same user, same integrity should not do that), which suggests a restricted token or sandbox. A restricted token can
  silently disable exactly the reads `GetStrip` makes (registry `Control Panel\Desktop`, `%APPDATA%\...\TranscodedWallpaper`); `GetStrip`
  swallows every exception, so the standalone dock may have started with *no strip at all* and never run the decode, `WriteableBitmap`,
  `TransformedBitmap` chain, or `ImageBrush` path.
- `RenderCapability.Tier` is new, synchronous and driver-dependent (it creates/queries D3D state through the MIL channel on the UI thread).

*Against.*
- The material gate runs the same `IsSupported`/`GetStrip` code (Clear/40) in the same runner at 125% and passes.
- I found no loop and no unbounded wait in the new code, and the dispatcher-alive fact needs a pump the new code does not contain. A hang in
  `GetStrip` (non-pumping file/WIC) is excluded by Section 1 item 4.
- Geometry at 150% passed in-gate with identical code.

### H3. OnStartup aborted by an exception whose log line was lost; dispatcher idle, window created but never shown (15%)

*Mechanism.* An exception thrown in `Show()`/`OnSourceInitialized`/`Rebuild` is handled by `OnUiException` (Handled = true). The timer keeps
running (started at App.cs:127, before the dock exists), `_dock.ObserveReadiness()` reports not-ready because the window was never shown,
the timeout fires at +10 s, the exit event is served at +20 s, `Automations.Start()` and the widgets never run, `quota.json`/`automations.json`
stay untouched, UIA never sees an invisible top-level window. That reproduces every observation including dispatcher liveness **without** needing
a pumping wait.

*For.* Simplest account of the log trio and of dispatcher liveness. Uncaught throw sites exist in new code (`UpdateWallpaperRefraction`
outside `GetStrip`: `_monitorGeometry.Bounds`, `SystemInformation.VirtualScreen`, `ImageBrush`, `_refractionEffect.Apply`; `WindowComposition.Apply`
via `ApplyBackdrop`). The in-memory `DiagnosticErrorJournal` records the failure even if the log write is lost.

*Against.* `FailureTracker.Record` calls `Log.Write` immediately and the log file demonstrably accepted lines at +0, +10 and +20 s; a silent
loss of the one line written at about +1 s, three times out of three, needs a cause I cannot name (a share violation from a scanner or IDE watcher
on a sandbox inside a watched git working tree is the only candidate, and it would be rare). An exception from `Failures.Record`'s own message
formatting would have crashed the process, which did not happen.

### H4. The pre-existing 125%-correlated startup/dispatcher stall family, now longer than 20 s (15%)

*Mechanism.* Whatever produces the 5-6 s startup stalls (6 percent of material starts) and the 8-11 s dispatcher stalls at 125% in 1.3.0
(three events, zero at 150%) has a heavier tail under today's conditions, and the first-window sequence is where it bites. It is machine/session
level (DWM, GPU, Explorer, input services, AV), made more likely by today's per-process-system-DPI behavior of unaware hosts, and unrelated to
1.3.1 code.

*For.* Strong precedent in this exact setup, same scale correlation, a ~5 s quantum visible in the startup data.

*Against.* A 20 s+ stall is far outside 5-11 s; the standalone controls at 125% never showed even a 5 s stall (0/4 plus 3 more standalone
starts at 1 s); 3/3 determinism in a row is unlike a 6 percent event. Also it cannot by itself explain why the wait pumps.

### H5. Something else (unknown), including a genuinely different launch context (15%)

Grandchild of a runner chain with a redirected-stdio parent (inherits the worker's console and inheritable pipe handles; `Process.Start`
without redirection still uses `bInheritHandles = TRUE`), sandbox located in a git working tree watched by IDE/AV tooling (the standalone
sandboxes were not), and a fresh WinForms runner that is 10-70 s old at every candidate start. None of these has positive evidence.

**Not supported as a cause:** the 10 s readiness window or its fail-closed design (App.cs:190-227); the Windows taskbar stayed visible and
restoration passed every time, which is the designed outcome. See Q4.

---

## 3. Weaknesses in the existing controls

1. **Context.** All four standalone repros ran in the builder's shell, which could not even open the gate-launched candidate for a dump
   (access denied). The handoff itself calls it "a sandboxed/restricted shell". That means the controls may not share token, integrity level,
   job object, desktop/window-station, or user-profile access with the gate candidate. Since `GetStrip` hides failures, a standalone dock may
   not have exercised the decode path at all.
2. **Poller timing.** Repro 3's poller was launched after the candidate (pwsh start plus `Add-Type` is about 1 s); it was not "from t=0".
3. **Surroundings.** No Run-Worker redirected-stdio chain, no `Get-PhaseBState` polling every 150 ms (which does `Get-Process Strata`, an
   `ABM_GETSTATE` to Explorer, and per-thread DPI context toggling), no WinForms runner, a sandbox outside the git tree.
4. **The comparison with 1.3.0 is confounded by today's environment change**: the per-process-system-DPI behavior of unaware hosts began between
   02:00 and 08:21 today, after every 1.3.0 gate pass. No 1.3.0 run exists in today's environment.
5. **Sample size.** 3/3 against 0/4 is roughly p = 0.03 one-sided; suggestive, not conclusive, particularly as the sets differ in context.

---

## 4. The one piece of evidence I would collect next

**A native+managed stack dump of the hung candidate at about +12 s into an in-gate occurrence, taken from the candidate's own security context.**

Do not take it from the sandboxed shell. Take it from the owner's normal shell, or better, make `Start-CandidateStrata` do it itself: the worker
is the candidate's parent with the identical token, so `dotnet-dump collect -p $p.Id` (or `comsvcs.dll MiniDump`) from the worker on timeout
cannot be denied. Collect at +12 s, not at the 20 s throw, so the process is surely still wedged.

What each family looks like in the dump:

| If the UI thread is... | Then |
|---|---|
| in a wait whose frames include UIAutomationCore / `HwndAutomation` / `AutomationPeer`, COM `CoWaitForMultipleHandles`, or a WPF `PenThread`/`TextServicesContext`/`MediaContext.Channel.SyncFlush` wait, under `Show` / `OnSourceInitialized` / `Rebuild` | H1 (and the frames name the counterparty) |
| in `Strata.Effects.WallpaperGlass.GetStrip`, `RenderCapability`, `UpdateWallpaperRefraction`, or a WPF layout/`PositionWindow` cycle | H2 |
| idle in `Dispatcher.Run` with no `OnStartup`/`Show` frame, and `App.DiagnosticErrors._items` (heap) holding a `ui-thread` entry | H3 (the journal even names the exception type) |
| anywhere in dwm/GPU driver frames with a long native wait | H4 |

Zero-cost add-ons for the same capture, all from the worker: `EnumWindows` for the candidate pid (does an HWND exist? visible? class
`HwndWrapper[Strata.exe;...]`? `IsHungAppWindow`), `Process.Responding`, per-thread `ThreadState`/`WaitReason`/CPU time over two samples (busy loop vs
blocked), and the tail of `strata.log`. "HWND exists but invisible" alone separates H3 from a stall before window creation.

Two cheap controls that become informative once the dump exists (neither replaces it): (a) the same worker, same gate, 1.3.0 candidate
`370dec2` at 125% (product vs environment); (b) in-gate, hold off UIA polling until the sandbox log contains `Strata started.` or for the first
5 s, and separately stop polling for 30 s after the failure and watch whether the candidate ever recovers (the existing flow kills it at +20 s,
so recovery has never been observable).

---

## 5. Code-level findings in the 1.3.1 diff that could block `OnStartup` >10 s without logging

None of these is proven to be the cause. Each can stall, abort or re-enter startup without a log line today.

| # | Where | Concern |
|---|---|---|
| C1 | `DockWindow.cs:438-500` `UpdateWallpaperRefraction`, called from `Rebuild` via `ApplyLiquidGlassEffect` (:255, :403-418), from `PositionWindow` (:526, and so from every 250 ms readiness tick and every `SizeChanged`), and from `_refractionLayer.SizeChanged` (:414) | New synchronous UI-thread work with no timeout, not wrapped in try/catch except inside `GetStrip`. A throw aborts `Rebuild`/`Show` (H3); a slow call lengthens startup. It is reachable **re-entrantly** from the readiness tick (App.cs:127 timer is started before the dock exists) if the UI thread is in any pumping wait during `Show()`. |
| C2 | `Effects/LiquidGlassEffect.cs:38-39` `IsSupported` (used at DockWindow.cs:406, :446) | First use of `RenderCapability.Tier` / `IsPixelShaderVersionSupported` in the product. It forces synchronous MIL capability/D3D initialization on the UI thread at an unfavourable time (inside `SourceInitialized`, before first show). Nothing logs its duration or result. |
| C3 | `Effects/WallpaperGlass.cs:118-161` `GetStrip` | Registry read, `File.Exists`, `File.GetLastWriteTimeUtc`, `FileStream` open with `ReadWrite` share, WIC decode `BitmapCacheOption.OnLoad`, `WriteableBitmap` copy, three `TransformedBitmap` stages, all on the UI thread; `catch { return null; }` hides every failure and every unexpected path, so a standalone run in a lesser token and a gate run in the full token can take different branches without any trace. A wallpaper on a slow or cloud-backed path would stall startup silently. |
| C4 | `DockWindow.cs:462` `System.Windows.Forms.SystemInformation.VirtualScreen` | First WinForms load/initialization inside a WPF window's SourceInitialized, new in 1.3.1. Uncaught. |
| C5 | `DockWindow.cs:153` `AllowsTransparency = true` plus `Native/WindowComposition.cs:35-64` | The dock is now a layered window, creating a different HwndSource/HwndTarget path than 1.3.0. `SetAccent` (:64, :83-100) is a new undocumented `SetWindowCompositionAttribute` call; a refusal is logged but a block is not. Plain (non-pumping) blocks are excluded by the timing evidence, but this changes the window-creation/UIA surface that H1 depends on. |
| C6 | `DockWindow.cs:168` `SystemEvents.UserPreferenceChanged += ...` in the constructor | Starts the `SystemEvents` hidden-window thread and blocks the constructor until it is ready. Not the stall (the ctor returned) but a new startup dependency; the same pattern at App.cs:157 (`DisplaySettingsChanged`) is older. |
| C7 | `App.cs:127` readiness timer starts before `_dock` exists and ticks `_dock.ObserveReadiness()` (DockWindow.cs:550-565), which calls `PositionWindow()` and now `UpdateWallpaperRefraction()` | Pre-existing design, but 1.3.1 made the tick's body heavier and re-entrant into a half-built dock if any nested pump occurs during `Show()`. |
| C8 | Missing breadcrumbs: no log line between "Taskbar safety suspension" (App.cs:123) and "Strata started." (App.cs:161) | This is why three failures produced a three-line log and no localization. An `OnStartup` stage log ("dock constructed", "Show begin", "SourceInitialized", "Rebuild begin", "widgets built", "Show returned") would localize the stall without a dump. |

Unchanged and therefore not new risk: `App.OnStartup` ordering, `DockStartupReadiness` (10 s), `FailureTracker`, `QuotaMonitor`, `QuotaWidget`,
`MaterialPolicy` (only the harness-facing `Decide`, unchanged), and all of `Program.cs`.

---

## 6. Harness-path findings

- `PhaseB.psm1:167-182` `Start-CandidateStrata`: on timeout it throws with no diagnostic capture. A single added block before the throw
  (EnumWindows by pid, `Responding`, thread wait reasons, `strata.log` tail, dump) would have answered this audit.
- UIA polling starts at t=0 and ticks at 200 ms for 20 s (`Wait-Until`, PhaseB.psm1:87-91, :180); the geometry gate, like the material gate,
  uses it. It is the largest cross-process, in-gate-only activity concurrent with candidate window creation.
- `Run-Worker` (run-v13-owner-gates.ps1:622-667) polls `Get-PhaseBState` every 150 ms via `Invoke-BoundedProcess` `-OnPoll` plus `Pump 150`; each
  call does a process enumeration, an Explorer round-trip (`ABM_GETSTATE`) and DPI reads, throughout candidate startup.
- `Invoke-GeometryWorker` (run-v13-owner-gates.ps1:243-256) deletes the previous sandbox before creating the next, so evidence from a failed
  occurrence is destroyed on resume; only `superseded\` worker files survive. The failure-record builder does not copy the sandbox log.
- `Initialize-CandidateBaseline` and `New-PhaseBSandbox` look sound: the baseline was clean in all three (`worker-baseline.json`: taskbar visible,
  no Strata process, dpi 120/125%), and the sandbox settings and fixtures are byte-equivalent to the standalone recipe except for location.
- b073652 (PrimaryMonitorDpi per-thread context) post-dates occurrence 1 (08:47 vs 09:08) and precedes 2 and 3, which both failed; it is neither
  cause nor cure.

---

## 7. Answers to the handoff's questions

**Q1. What in-runner condition, absent from the four standalone reproductions, could explain a deterministic hang after the ctor and before `Show()` completes?**
Differences I can substantiate: (1) UIA polling begins at t=0 at 200 ms cadence, versus repro 3's poller starting about a second late and landing
after the window existed; (2) a different security context for the controls (the dump denial is the evidence); (3) a runner/worker parent chain with
redirected stdio, a live WinForms runner, and a 150 ms `Get-PhaseBState`/Explorer polling loop during startup; (4) a sandbox inside a watched git
working tree; (5) every harness process is DPI-locked with System DPI 144 against a live 120 (only at 125%). None is proven causal. The first two are
the ones I would test, via the dump and the 1.3.0 control.

**Q2. Is there a 1.3.1 product change that can block startup >10 s without logging?**
Yes in the sense of "can": C1-C5 above are new, synchronous, unbounded, mostly silent on the UI thread inside the stall interval. But I could not
find a 1.3.1 change that blocks in a way compatible with the dispatcher staying alive, and the same code runs successfully in the material gate at
125%. So: plausible surface, no mechanism, and the geometry-specific element (`aiusage`, empty pins) is in code that did not change. App groups
(`DockGroups`, `AppButton`) only touch pin sanitizing and click/drag paths and are not on the stall interval for a no-pins configuration.

**Q3. Do the three timings constrain where startup sits when the timer fires?**
Yes, strongly: (a) OnStartup reached App.cs:123 within about 0.5 s of process creation; (b) `_dock != null` at +10 s, so the ctor returned; (c) the UI
thread dispatched the readiness tick at about +10.0 s and the exit event at about +20.2 s, so it was pumping, not blocked in a plain call; (d) no
quota lines, no `quota.json`/`automations.json` writes: the first `AddWidget` had not completed `Tick`. Together: stalled inside `Show()`/
`OnSourceInitialized`/early `Rebuild`, in a state that still services timers and posted messages.

**Q4. Is the readiness design (10 s window, fail-closed, timer stopped after timeout) implicated in the window never appearing to UIA?**
No. Readiness only governs *when the taskbar is hidden*; it does not gate window creation or `Show()`. Its timeout fired exactly as designed and
kept the taskbar visible. The window is absent to UIA because the harness waited 20 s (twice the readiness window) and either no window was ever
shown or `Show()` never reached `ShowWindow`. Two secondary points: the tick runs `PositionWindow`/`UpdateWallpaperRefraction` on a possibly half-built
dock if a nested pump exists (C1/C7), and once it times out the timer is stopped, so a late-appearing dock would not hide the taskbar until a
reapplication event (safe, but it means "slow but successful" startups are reported as failures by the gate's `taskbar did not hide` step, not by
the missing window).

**Q5. Anything in the environmental facts that is connected rather than coincidental?**
Probably partly connected, but not proven. The per-process-system-DPI behavior of unaware hosts (the described "pre-locked at launch-time scale"
matches Windows' *Fix scaling for apps* / `EnablePerProcessSystemDPI` behavior, which I did not verify and which is detection-only for the candidate,
whose PerMonitorV2 manifest ignores it) creates a 144-vs-120 mismatch for the entire harness chain at 125% and not at 150%, and the failure is 125%-only.
Because that also holds for the builder's shells, it cannot be sufficient alone. It does remove the one control that mattered: no 1.3.0 gate has been
run since it began. What changed at about 02:00 (a new process, a setting flip, a Store/OS update on build 26300) is worth identifying; the reboot-pending
state and PowerShell 7.6.9 are, as far as I can see, coincidental. The DPI-lock fix (b073652) is correct and orthogonal.

---

## 8. Recommended order of work (no product change needed to start)

1. Add failure-time capture to `Start-CandidateStrata` (EnumWindows by pid, thread states and CPU delta, `Responding`, `strata.log` tail, dump at +12 s) and
   preserve the sandbox before it is deleted. Re-run the 125% geometry gate once.
2. Same gate with the 1.3.0 candidate to settle product-versus-environment.
3. If the dump or the controls point at product code, instrument `OnStartup`/`Show`/`Rebuild` stages (C8) in a non-gate diagnostic build and add a
   bounded, logged guard around `UpdateWallpaperRefraction`/`IsSupported`.
4. Do not treat "4/4 clean standalone" as clearing the product until a standalone run from the owner's own unrestricted shell with the poller already
   running before the candidate launches is also clean.
