# Codex audit of the 125% startup hang - preserved from her session, 2026-10-28

## Audit conclusion

The evidence does not identify a root cause. It does establish that this is not a conventional, non-responsive UI-thread hang.

The failure is more precisely:

> `OnStartup` remains incomplete, the dock never becomes UIA-visible, but the application dispatcher continues servicing work until the harness requests shutdown.

The binary identity is verified: ProductVersion, 199,224,289-byte size, and SHA-256 all match the request. I made no changes and did not run the candidate.

### Proven timing constraints

Across all three failures:

- `StartupNotReady` proves startup reached [App.cs:123](F:/Projects/Strata/src/Strata/App.cs:123).
- The +10 s timeout can only be logged after `_dock` is non-null because [ObserveDockReadiness](F:/Projects/Strata/src/Strata/App.cs:193) returns immediately when it is null. Therefore the `DockWindow` constructor had returned.
- The timeout is a `DispatcherTimer` callback. Its on-time execution proves that the dispatcher serviced work during the apparent hang.
- The +20 s graceful stop is also dispatched from the named-event listener at [App.cs:165](F:/Projects/Strata/src/Strata/App.cs:165). This independently proves dispatcher responsiveness.
- `Strata started.` at [App.cs:161](F:/Projects/Strata/src/Strata/App.cs:161) was never reached.
- No `ui-thread failed:` entry exists. `OnUiException` would normally produce that through `FailureTracker.Record`, so the logs do not support an unhandled managed exception as the explanation.
- The candidate was alive for approximately 20 seconds, not 32 seconds. The remaining worker time was failure handling and taskbar restoration before `worker-error.json` was written.

Evidence: [failed application log](F:/Projects/Strata/evidence/v1.3.1-d90a8fe/owner-gates/attempts/20261028-082136-13a9c99a/work/sandbox-geometry-125/strata.log), [latest worker record](F:/Projects/Strata/evidence/v1.3.1-d90a8fe/owner-gates/attempts/20261028-082136-13a9c99a/scales/125/geometry/worker-run.json).

A re-entrant native/COM wait or WPF layout/render loop that pumps dispatcher messages remains compatible with these facts. A simple blocking managed call that does not pump messages does not.

## Answers to the audit questions

1. **What runner-only condition could explain it?**

The standalone UIA poller eliminates `FindFirst` itself as the trigger, but it does not reproduce the full attended environment.

Runner-only differences include:

- The visible, long-lived attended-runner window.
- Candidate launch as a grandchild of a redirected-output worker.
- Parent polling every 150 ms through `Get-PhaseBState`.
- A runner process that existed across the 150%→125% transition.

The most directly candidate-visible difference is the live top-level window inventory. Before `Show()`, `RestoreThemeOverride()` invokes a complete `Rebuild()` at [DockWindow.cs:1652](F:/Projects/Strata/src/Strata/Views/DockWindow.cs:1652). That rebuild synchronously refreshes the desktop window snapshot at [DockWindow.cs:295](F:/Projects/Strata/src/Strata/Views/DockWindow.cs:295).

That path contains several unbounded shell/COM operations:

- `IShellWindows` enumeration: [ExplorerWindows.cs:57](F:/Projects/Strata/src/Strata/Core/ExplorerWindows.cs:57)
- Per-window property-store lookup: [ShellInterop.cs:177](F:/Projects/Strata/src/Strata/Native/ShellInterop.cs:177)
- Synchronous shell icon extraction: [IconCache.cs:21](F:/Projects/Strata/src/Strata/Core/IconCache.cs:21)

The attended runner changes the set of windows and icons that this pre-show work processes. This is the best-fitting runner-only path found in source, but without a dump or checkpoints it is not proven to be the active path.

2. **Can a 1.3.1 change block startup without logging?**

Yes, structurally:

- The dock changed from non-layered to `AllowsTransparency = true`: [DockWindow.cs:146](F:/Projects/Strata/src/Strata/Views/DockWindow.cs:146).
- `Show()` now reaches synchronous wallpaper registry/file reads, bitmap decoding, bitmap realization and transformations: [WallpaperGlass.cs:97](F:/Projects/Strata/src/Strata/Effects/WallpaperGlass.cs:97).
- Window positioning invokes wallpaper reconstruction synchronously: [DockWindow.cs:512](F:/Projects/Strata/src/Strata/Views/DockWindow.cs:512).
- Exceptions in the wallpaper path are deliberately swallowed, and none of its I/O or decoding has a timeout.

However:

- The failing style is default Glass, not Liquid Glass. It does not attach the new liquid shader.
- The successful 125% Clear/40 material cycle did exercise the layered window, wallpaper, and liquid shader.
- Exact geometry settings passed at 150% and in four standalone starts.

Therefore the evidence does not support “shader alone,” “wallpaper alone,” or “layered window alone” as the cause. A configuration/environment interaction remains possible. App-group changes are inactive because this gate has no pins.

3. **What do the timings say about the startup location?**

They constrain it to after `DockWindow` construction and before normal startup completion, probably before or during `Show()`.

They do not distinguish among:

- The pre-show `RestoreThemeOverride → Rebuild → RefreshWindowSnapshot` path.
- `Show()`/`OnSourceInitialized`.
- Layered-window layout/render or wallpaper work triggered during initial sizing.

The missing quota-read lines are consistent with startup stalling during the pre-show rebuild or initial show before the AI widget’s asynchronous continuations complete. They are suggestive, not conclusive.

4. **Is the readiness design causing the window not to appear?**

No evidence supports that.

On timeout it restores the taskbar and stops its own timer; it does not hide, close, or cancel the dock. See [App.cs:197](F:/Projects/Strata/src/Strata/App.cs:197).

The design has one secondary limitation: if a dock appeared after ten seconds, the stopped timer would not reapply the configured taskbar mode. But the harness continued polling UIA until approximately +20 s and still found no window. Thus the ten-second fail-closed action did not cause this observed absence.

Starting the deadline before dock construction does make the readiness timer a poor diagnostic boundary: it measures all startup work, not merely post-show geometry stabilization.

5. **Are the environmental facts connected?**

- The pwsh DPI change was demonstrably connected to frozen harness scale detection.
- No direct connection to candidate startup is established. The candidate contains an embedded PMv2 manifest, and Microsoft documents the manifest as the process-default DPI declaration; SYSTEM_AWARE processes instead retain a single system DPI for their lifetime. [Microsoft DPI-awareness documentation](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process), [DPI context definitions](https://learn.microsoft.com/en-us/windows/win32/hidpi/dpi-awareness-context).
- The candidate’s manifest therefore weakens a simple “candidate inherited pwsh’s SYSTEM_AWARE mode” explanation.
- `PendingFileRenameOperations`, the PowerShell update, and lack of reboot have no demonstrated link in the supplied evidence.

## Most decisive next investigation

A single full dump from the candidate at the +10 s timeout, collected by an unrestricted same-user process, should resolve this. The prior attempt failed only because the dumping shell lacked access.

If a dump cannot be obtained, a diagnostic build should add monotonic before/after checkpoints around:

1. `RestoreThemeOverride`
2. `RefreshWindowSnapshot`
3. `IShellWindows.Enumerate`
4. Each window identity and icon lookup
5. `_dock.Show`
6. `OnSourceInitialized/Rebuild`
7. `UpdateWallpaperRefraction`

Log only operation names, durations, HWNDs and PIDs—not titles or paths.

The highest-value A/B is to launch the exact Geometry worker:

- under the attended runner versus headlessly;
- with the runner window visible versus hidden;
- with the runner freshly started at 125% versus retained from 150%;
- with live-window refresh deferred until after `Show()` in a diagnostic build.

That matrix separates runner-window enumeration, parent DPI history, and the new composition path without changing the gate’s product settings.
