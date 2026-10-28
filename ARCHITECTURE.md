# Strata 1.3 architecture

## Lifecycle

`Program.Main` handles recovery, exit, install/uninstall, deterministic previews, and the normal single-instance launch. Storage is selected before settings are read. Preview mode points `Storage.Root` at a throwaway fixture root and activates `PrivacyGuard` before constructing any data-backed service.

In a normal launch, `App` owns one instance of each shared service:

```text
App
├─ AppSettings / configurable Storage root
├─ SignalStore
├─ NotificationCenter → Tray
├─ RuleEngine → AutomationPersistence
├─ MediaService
├─ ScreenTimeTracker
├─ TaskbarController
└─ DockWindow
   ├─ WindowTracker / WindowIdentityCache
   ├─ AppBarController
   ├─ LayoutStack + ThemeController
   ├─ shared one-second heartbeat
   └─ widgets / flyouts / menus
```

The dock is shown before live producers start. Shutdown stops the rule engine, timers, waits, flyouts, widgets, tray resource, and hotkey, saves screen time, unregisters the AppBar, and restores the taskbar. Process exit, fatal exceptions, session ending, normal Exit, and `--restore-taskbar` all have recovery paths.

`MediaService` subscribes to manager current-session/session-list events and to the active session's metadata and playback events. `RefreshCoordinator` prevents overlapping reads, coalesces event bursts, contains errors, and supplies a bounded 15-second fallback from the shared heartbeat. Session and manager handlers are detached during shutdown.

## Signals and automations

Widgets and producers publish typed snapshots to `SignalStore`; automations never call widgets directly. Snapshots include source, timestamp, TTL, confidence, error, and fixture pinning. A single heartbeat handles expiry and time-based automation wakeups.

The nine built-in rules use transition, once-per-key, or once-per-day policies with sustain periods, cooldowns, quiet-hour deferral, notification deduplication, priorities, explanations, and persisted state. Each action is isolated. Three consecutive rule failures disable only that rule until the user re-enables it.

`LayoutStack` composes active layers over `AppSettings.LeftWidgets` and `RightWidgets`. Disposing a layer restores the exact saved layout. `ThemeController` similarly overlays an automation theme; a user theme choice is a manual override until that automation ends. The override owner is persisted in `automations.json`.

## Native shell boundary

- `AppBarController` checks `ABM_NEW`, `ABM_QUERYPOS`, `ABM_SETPOS`, and removal results. Failed registration is never treated as success. `AppBarSafety` connects it to the taskbar: a failed register or reserve restores the Windows taskbar with the `AppBarUnavailable` blocker, and only a verified register *and* reserve clears that blocker. Turning space reservation off removes the dependency and clears it.
- `TaskbarController` keeps configured preference separate from effective native state and safety suspension. It saves original state before changing it, verifies requested state, shows all taskbars during recovery, and retains the recovery file if restoration is not confirmed. Guard-triggered recovery suspends timer and Explorer hide enforcement whether restoration succeeds or remains unconfirmed.
- `TaskbarSafetyLifecycle` runs from the live dock heartbeat. If the taskbar is managed as hidden and the native dock window is unavailable for ten seconds, it restores taskbar access. A continuing unsafe state retries only after another full grace interval. Its restore sets the `DockUnavailable` blocker, and seeing the dock visible again clears only that blocker, and only for an episode the lifecycle started itself. Deliberate fullscreen hiding resets unhealthy time and never triggers restoration.
- `DockStartupReadiness` makes startup and Explorer reapplication explicit: the taskbar is restored before dock construction, stable visible HWND/AppBar geometry is observed, and only then is the configured taskbar mode applied. Timeout or an unsuccessful native apply leaves the Windows taskbar visible. Monitor/window inputs are injectable for deterministic mixed-DPI and restart races.
- Safety blockers are origin-aware and compose (`TaskbarSafetyBlockers` flags). Each origin clears only its own blocker; the configured hide/auto-hide is reapplied only when none remain. Explorer restarts keep every blocker and keep the taskbar visible.
- Automatic vs explicit taskbar requests: `ApplyConfigured` (startup, settings saves) records the preference but never ends a suspension. An explicit user restore always shows the taskbar and may clear the suspension; an explicit hide records the preference but cannot override a known AppBar, startup, Explorer-reinitialization, or dock-availability blocker. Hiding resumes only after the owning subsystem verifies recovery.
- `WindowIdentityCache` trusts a HWND only while both PID and process creation time match. Unknown creation times are not cached, dead handles are evicted, and capacity is bounded.
- `ExplorerWindows` enumerates `IShellWindows` folder HWNDs, validates each live `CabinetWClass` owner against `explorer.exe`, and excludes Progman, WorkerW, taskbar, dialog, invisible, cloaked and stale surfaces. Explorer pins match structurally by shell identity rather than display name; each top-level folder window counts once regardless of tabs.
- `Installer` stops the running copy before mutation, retries locked copies, verifies SHA-256, checks shortcuts and leftovers, and returns typed nonzero failures.

## Rendering and input

Three dock styles (`DockStyles.Selectable`): Glass, Clear and Dark (`Theme.For`). Clear is shown as "Liquid glass" (`DockStyles.DisplayName`); its stored name stays `Clear`. `MaterialPolicy` preserves the exact contract: 0 has no fill or blur, 1-99 uses the documented DWM desktop acrylic path when eligible, and 100 is solid. WPF adds restrained tint/luminosity, deterministic fine texture, a top specular highlight and inner rim. High contrast, transparency off, Battery Saver, Remote Desktop, composition loss and unsupported Windows builds use a readable solid fallback; reduced motion suppresses transitions.

`StableWidgetHost` reserves compact width/height contracts for continuously changing widgets. The AI slot uses fixed internal columns, tabular digits, ellipsis for secondary text and complete automation names/tooltips. Overflow planning collapses lower-priority content into a keyboard- and screen-reader-accessible menu when the configured dock cannot fit, and is re-planned whenever a slot changes width. Content refreshes never mutate the parent measurement contract; the one exception is a widget with nothing at all to show, which takes a second width fixed in the same table (today only the media pill, when no track is playing).


Dock and flyout windows use non-layered DWM-backed composition with DWM corner preferences. WPF rounded content uses the same outer geometry; the previous acrylic + `SetWindowRgn` + layered-window combination is gone. Solid/translucent theme fills remain the fallback when the system backdrop is unavailable.

App and widget tiles derive from `DockButton`; menus and flyout actions use real buttons; settings use semantic toggle and radio-style choice controls. `Win+Alt+S` temporarily makes the normally non-activating dock keyboard-focusable (Windows 11 reserves `Win+Alt+D` for its date/time flyout, which made the original shortcut fail to register on every start). `DockHotkey` registers it; if registration fails it logs, sends an urgent notification that quiet hours cannot suppress, and Settings > Taskbar & startup says the shortcut is unavailable and how else to reach Strata. Arrow keys traverse dock items, Enter/Space invoke, Shift+F10 or the Menu key opens a context menu at the focused item (WPF delivers F10 as `Key.System`, see `DockKeyboard`), and Escape returns the dock to non-activating mode. A flyout or menu opened in keyboard mode returns keyboard mode and focus to the item that opened it when it closes by Escape, or by a menu action that leaves Strata's dock or flyout in front; it never pulls focus back after the user clicks or switches elsewhere (`KeyboardFocusReturn`). For the Phase B keyboard gate only, `STRATA_PHASEB_COMMAND_LOG=1` with a sandbox `STRATA_DATA_ROOT` makes the dock append its own keyboard-mode commands (focus, navigation, flyout open/close, focus return, exit) to `phaseb-commands.log` as normalized tokens such as `2:widget:media` or `app`, never names; it records what Strata did, not keystrokes, and there is no keyboard hook (`PhaseBCommandLog`). It is never written against the real `%APPDATA%\Strata`.

`Win+Alt+Space` opens `CommandPaletteWindow`, a fixed 680x400 DIP surface over typed `PaletteCommand` entries. `PaletteMatcher` performs bounded fuzzy ranking; query text is never executed or passed to a shell. The catalog covers installed/running apps, existing windows, settings, widgets, reversible Strata actions, automation controls and taskbar recovery, with no recent-file source. Hotkey failure raises an urgent notification and menu access remains available.

## Icons

The requested shell source size is based on rendered DIP size × monitor DPI and doubled for animated dock icons. Requested pixels and DPI are part of the cache key; DPI changes invalidate and rebuild the cache. Shell/WIC images are preferred before `WM_GETICON`. Alpha normalization preserves premultiplied channels and never converts all zero-alpha pixels into an opaque rectangle.

## AI usage (quota monitor)

`Strata.Quota` turns each provider's own report into a normalized, non-secret `QuotaSnapshot`: windows with the provider's figure kept exactly (`ReportedUsedPercent`, or `ReportedRemainingPercent` where the provider reports that) and the other side derived as 100 minus it, reset times, plan, credits, reset-credit expiry and blocked state. Only snapshots are stored (`quota.json` for Codex, `quota-claude.json` for Claude).

- **Codex** (`CodexAppServerSource`, `CodexRpcSession`, `CodexRateLimits`): Strata starts `codex app-server` for one read every few minutes while the widget is on the dock (about 0.7 s and 55 MB; it starts no other servers). It runs `initialize`, `initialized`, then the read-only `account/rateLimits/read`, and merges any `account/rateLimits/updated` that follows. Both the single-bucket `rateLimits` and the per-limit `rateLimitsByLimitId` view are read, so model-specific buckets become their own windows. `ordinaryUsageAllowed: null` stays "unknown"; it is never inferred from percentages. Errors are classified (sign-in, 429, offline, bad response, timeout); raw provider text is never shown or stored. `codex.exe` is found from the setting, PATH, the Codex Store package or the VS Code extension.
- **Claude** (`ClaudeStatusLine`, `ClaudeCodeConnector`): Settings > AI usage > Connect sets Claude Code's user `statusLine` to `Strata.exe --claude-statusline`. It refuses if another status line exists, backs up the file and keeps every other key. Disconnect restores the exact original file when nothing else changed (otherwise it removes only its key and keeps the backup). Settings are written with relaxed escaping, so the user's own characters stay as written. Claude Code runs status lines in its terminal interface only: in a live test with 2.1.308, the desktop app's Code tab never ran it. The bridge reads the status-line JSON from stdin, keeps only `rate_limits.five_hour`, `seven_day` and `spend_limit`, writes the snapshot, prints a short "used" line, and always exits 0. Workspace and transcript paths, session ids, model and cost are ignored.
- **Monitor** (`QuotaMonitor`): one `ProviderState` per provider: Disabled, NotConnected, WaitingForFirstSample, Fresh, Stale or Error. A failed read keeps the last snapshot, shown as stale with the reason. Failures back off (doubling to 30 min, at least 15 min after a 429). A manual refresh joins one already running.
- **Startup and process lifetime** (`QuotaReadRunner`): source invocation runs on a worker, so a synchronous source prefix cannot block widget construction. Each CLI source also bounds its entire lookup, launch and read to 10 seconds (including both Claude auth and usage), with state updates returning to the monitor's caller context. Timeout and caller cancellation terminate owned children; a timeout uses the existing failed-read/stale-cache path. The public deadline and child termination operate independently of provider cancellation callbacks, so a stuck callback cannot hold up either. Windows cannot cancel a blocked `CreateProcess` call before it returns a handle: the public read still times out, that source refuses additional workers while the old operation retires, and a late child is killed before protocol I/O. Cleanup never makes the UI wait for a native call.
- **Display** (`QuotaFormat`, `QuotaSelector`, `QuotaWidget`):
  - Figures show at most one decimal, so used and remaining add up. A window past its reset waits for a new sample, and suspected clock skew shows only the absolute time.
  - The tile shows one provider's one window: Claude, Codex, the most constrained *fresh* window, or both in rotation. Warning colours apply only at the configured thresholds.
  - Preview mode uses `QuotaMonitor.Sample`, labelled "SAMPLE" on the tile and "SAMPLE DATA" in the flyout. Every live source calls `PrivacyGuard`.

## Media player

`MediaService` reads the session's media properties, playback info and timeline (position, length, last report time, rate, and whether seek, shuffle and repeat are allowed) and refreshes on each of their change events.
- **Position:** estimated between the app's reports (`TrackTimeline.Estimate`).
- **Colour:** `AlbumColor` takes it from the cover: hue bins weighted by saturation and brightness, the strongest bin made vivid enough for the dark card. A cover without real colour gives a fixed soft blue-grey instead of an invented hue. The cover is processed only in memory, and nothing about it is stored.
- **Waves:** `WaveSlider` (a `RangeBase`, so screen readers get a slider with RangeValue) draws three translucent layers of crests over the played part, each a lighter tint of that colour (`WaveShape`).
  - Crests are spaced in DIP, so they keep their size on a bar of any length. A slower second wave gives neighbouring crests different heights, and they taper to nothing at the start and at the position.
  - Their height follows the music. Each frame reads the default playback device's peak level (`AudioEndpoint.Peak`, Core Audio `IAudioMeterInformation`): a number, never audio. An automatic gain (`WaveShape.Loudness`, about 12 s) fills the height at any volume, while a quiet passage stays smaller than the loud one before it. The level rises at once on a beat and falls back more slowly. Where the level can't be read (previews, tests, no playback device), a stand-in beat is used.
  - Each part of the song has its own scale: smooth noise over the track position (`WaveShape.Section`).
  - Paused, the energy eases to 0: the played part is a flat line in the accent.
  - The dock strip is the same waves, smaller and with tighter crests, rising from the tile's bottom edge, without the position dot.
- **Card:** the blurred backdrop is an `ImageBrush`, not an `Image`, so a cover of any shape never changes the card's size. Previous, play/pause and next are one size.
- **Animation cost:** it runs only while playing, visible, and with Windows' animation effects on, at 30 frames a second; paused, the waves ease to a flat line and stop drawing.
- **Flyouts:** the dock never takes focus (`MA_NOACTIVATE`), so a click on a widget whose flyout is open does not close it by itself. `DockWindow.ShowFlyout` treats that second click as a close (`IsSecondClick`), like a taskbar button. The flyout itself closes on Escape and when it loses focus (a click anywhere else).
- **Pill shortcuts:** scroll for the volume, Shift+scroll to skip 10 s where the app allows seeking (`MediaWidget.Scroll`), middle-click to play or pause; the title line briefly shows the new volume or position.

## Window previews

Hovering a running app's icon shows `WindowPreview`: a card with a tile per window (up to 6; the rest are in the right-click menu), each with its title, a close button and a live picture. Clicking a tile switches to that window; middle-clicking it closes the window.
- **Pictures:** DWM thumbnails (`LiveThumbnail`, `DwmRegisterThumbnail`) composed into the card by Windows itself, the way the taskbar does it. Nothing is copied or stored. The card is an ordinary non-layered window, which DWM thumbnails need. A minimized window has no live picture, so its tile shows the app's icon.
- **Focus:** the card never takes focus (`WS_EX_NOACTIVATE` and `MA_NOACTIVATE`, like the dock), so hovering never disturbs the window in use.
- **Timing:** `HoverIntent` shows it after the pointer rests 450 ms on an icon. It switches at once to another icon while it is up, stays while the pointer is over it, and hides 300 ms after the pointer leaves both. A click, a menu or a flyout hides it.
- **Count:** the icon shows how many windows are open, when more than one (`AppButton.CountBadge`). Running apps no longer have a text tooltip; their accessible name still says how many windows are open.
- **AI usage hover:** the tile's tooltip lists every limit of both providers with its reset time (`QuotaWidget.HoverSummary`), never blended.

## Forecast, shortcuts, history, screens and updates

- **Forecast** (`QuotaForecast`): each fresh, real reading adds a sample per limit (every 10 minutes, or sooner when it moves a point) to `quota-history.json`, keyed by limit and reset period, and dropped eight days after its reset. The pace over the last day, needing at least 30 minutes of readings, projects either when the limit is reached (before the reset) or the figure at the reset. The flyout draws the period's trend with `Sparkline`.
- **App shortcuts** (`AppHotkeys`): `RegisterHotKey` Win+Ctrl+Alt+1..9 on the main dock. Windows holds Win, Win+Alt and Win+Ctrl with numbers even with its taskbar hidden (all nine refused here), and Ctrl+Alt is AltGr on many layouts (AltGr+2 types @ on a Spanish one). Combinations another app holds are skipped and counted in Settings. A press does what a click on that app does. Each app's accessible status names its shortcut.
- **Scrolling an app** (`AppButton.NextWindowIndex`): down goes to the next window, up to the previous, wrapping; from another app, its most recent window.
- **Notification history** (`NotificationCenter.History`): what was shown, or held during quiet hours, since the last Clear, newest first; kept in memory for the session only, because reminders can carry event titles. The clock shows a dot and a count in its accessible status for unread ones, and its flyout lists them.
- **A dock on every screen** (`AppSettings.DockOnEveryScreen`, off by default): `App.SyncScreenDocks` opens a `DockWindow` for each other monitor (HMONITOR), and closes it when its screen goes (`DisplaySettingsChanged`). `IMonitorWindowGeometry` supplies monitor enumeration, per-window DPI, physical bounds and placement; `DockPlacementPlanner` keeps negative-coordinate/mixed-DPI positioning and AppBar height deterministic in tests. Extra docks show Start, the clock and the windows on their screen (`DockWindow.OnScreen`); widgets, automations and shortcuts stay on the main dock. Flyouts and previews place themselves on the screen of their item.
- **Updates** (`Updater`): a release folder's `update.json`, made by `tools/make-release.ps1`, or the same files published on the web.
  - **Sources and bounds:** local and web `update.json` reads are capped at 64 KB. `ManifestUri` accepts https only. A GitHub repository address ("https://github.com/owner/repo") becomes `releases/latest/download/update.json`; an address ending in `.json` is used as is; any other address is a folder with `update.json` in it. A web check has a 30 s bound; the release file is downloaded only when the user clicks Install (`StageAsync`), streamed into a new temporary folder and stopped as soon as it is longer than update.json says. Redirects away from https are refused. Local and web staging contain disk/access/read failures, remove partial stages, and return a safe failure. The same SHA-256 and signer checks then run on the copy (`Verify`).
  - **Offer:** a higher version (by number; build metadata ignored) is offered after checking that the file exists at the stated size, and notified once per version.
  - **Unsigned preview channel** (`Updater.RunningUnsigned`, `UnsignedWarning`): while the running Strata is unsigned, the SHA-256 comes from the same `update.json` as the file, so it checks integrity, not the publisher. Settings and the install prompt say so.
  - **Byte-order mark:** `Updater.Parse` drops an optional UTF-8 BOM, so an `update.json` written by Windows PowerShell reads the same from a folder or the web.
  - **Install and rollback:** the release is copied to a new temporary folder and its size/SHA-256 checked there. If the running Strata is signed, the copy must carry a valid signature (`WinVerifyTrust`) from the same certificate. `UpdateTransaction` then verifies a last-known-good copy and a separate hash-verified recovery helper, writes a durable pending journal, atomically replaces the installed executable, and waits at most 30 seconds for a one-use readiness acknowledgement. Failed launch or timeout restores the taskbar and verified previous build immediately. After process/power loss, the next ordinary start detects the journal, restores the taskbar, exits the mapped installed image, and delegates recovery to the separate helper; an already-written health marker finalizes the candidate, otherwise the verified previous build is restored. The only known-good copy is never deleted.

## Dragging, badges, quick settings, layouts and backups

- **Drag to reorder and pin** (`DockWindow.DragApp`/`DropApp`, `DockOrder`): past three drag distances a press on an app becomes a drag. The app follows the pointer and the apps it passes slide aside (render transforms only, so the button keeps mouse capture and the layout doesn't change mid-drag; window refreshes wait until the drop). `DockOrder.PinTarget` decides the landing: a pin moves among the pins (clamped to the last pin); a running app dropped among or just after the pins is pinned there (`Launcher.FromRunning`); further right nothing changes. The release that ends a drag is not a click.
- **Unread badges** (`UnreadCount`): two title shapes only: a bracketed count at the start followed by a space and text ("(3) WhatsApp", "[4] Slack"), and a mail folder with its count ("Inbox (7)", also in Spanish, French, German, Italian and Portuguese). "Report (2).docx" does not count. An app adds up its windows. The red badge takes the place of the window count; the accessible status says "N unread". Title changes already refresh the dock (`WindowTracker.Refresh`).
- **Notification-derived badges (deferred)** (`NotificationBadgeCapability`, `NotificationBadgeCache`): Microsoft's supported `UserNotificationListener` requires the `userNotificationListener` package-manifest capability and explicit permission. The 1.3 executable is intentionally unpackaged, so the source is unavailable and title-derived counts remain active. A future packaged build has a fail-closed gate plus a bounded, exclusion-aware, in-memory count/timestamp boundary with no content fields; denial or revocation falls back quietly. Strata does not equate notification presence with unread-message state.
- **Quick settings** (`QuickSettings`, `Radios`): `WindowsRadios` calls `Radio.RequestAccessAsync` and `Radio.GetRadiosAsync` the first time the volume flyout opens, and `SetStateAsync` only when a tile is tapped. No radio, or access other than Allowed, opens the Settings page instead. Focus uses `PomodoroTimer`; Night light opens `ms-settings:nightlight`. Previews and tests use `FixtureRadios`, and `RadioSource` refuses the real radios in preview mode.
- **Automatic layouts:** the meeting preparation rule adds `CollapseWidgets(distracting, enabledParam: "quiet")`; the new `night-layout` rule (priority 15, lowest of the layout rules) collapses `Automations.NightHidden` on `Signals.Minute` between its `from` and `to` times (`SunCalculator.IsEveningBySetTime`). Because layers compose in priority order, any alert's promote (low battery 90, connectivity 80, resource pressure 50) wins over it.
- **Settings backup** (`SettingsBackup`): `{ format: "strata-settings", formatVersion: 1, app, exportedAt, settings, automations }`. Automations hold only `enabled` and `parameters`, and only for rules the user changed: no history, explanations, fired keys or state, since those can carry event titles. `Parse` refuses other files and newer format versions, normalizes the style, clamps the opacity and drops unknown or repeated widgets. `SettingsBackup.Restore` saves the settings, applies each automation choice through the rule engine, and resets `QuotaService`, so the restored Claude/Codex switches and executable paths apply at once (`QuotaService.For` also rebuilds whenever it is given a different settings object). `App.RestoreSettings` then swaps in the settings, updates the Run key if "Start with Windows" differs, and re-applies the theme and dock.

## Notifications, files and recent documents

- **AI usage alerts** (`QuotaAlerts`): once per limit and reset period when it reaches the warning level (the tile's warning threshold), once when it is reached, and once when a limit that had warned or been reached resets. Only fresh, real readings count. A reset time that moves by under 30 minutes counts as the same period. `quota-alerts.json` remembers levels and sent alerts (window ids, reset times, levels); entries two weeks past their reset are dropped. Quiet hours apply through the notification centre.
- **Dropping files on an app** (`Launcher.OpenWith`):
  - ordinary apps start with the quoted paths;
  - packaged apps use `IApplicationActivationManager.ActivateForFile`, and, for packaged desktop apps such as Windows 11's Paint, Photos and Notepad, which refuse that, the "Open with" handler with their AppUserModelID (`SHAssocEnumHandlers`, `IAssocHandler.Invoke`), as Explorer does.
  - A drag resting 700 ms on a running app brings its latest window forward.
- **Recent files** (`ShellDocuments.Recent`): read from Windows' jump lists (`IApplicationDocumentLists`) when the app's menu opens, never stored. The jump list is looked up by the app's own AppUserModelID, then the one its shortcut declares, then the implicit id Windows derives from its path ("{known folder}\relative\path.exe"). Only existing files are listed. When Windows' recent-items setting (`Start_TrackDocs`) is off, the menu offers to open that setting.

## Failure containment

Widget construction/tick failures are tracked per widget. Repeated failures replace that widget with a keyboard-invokable paused notice; retry releases only that quarantine. UI-thread failures are bounded rather than swallowed forever. Rules, notifications, screen-time sampling, taskbar operations, and weather responses have separate failure boundaries.

`DiagnosticErrorJournal` stores only bounded component categories, exception types and times. `PerformanceSampler` shares the heartbeat and tracks CPU, private memory, handles and threads without another timer. `DiagnosticBundleBuilder` writes only version/hash/size, configuration schema, categorized recent errors, performance summaries and anonymous monitor rectangles. `AutomationActivity` projects bounded rule activity through fixed privacy-safe descriptions of what acted, why, what changed and whether it was undone; raw explanations, window titles and notification content never enter the view.
