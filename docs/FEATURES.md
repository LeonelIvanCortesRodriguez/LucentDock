# Everything LucentDock does

The complete feature list, moved here from the README's front page so the front page can tell the
story first. Version 1.3.2.

## Apps

- Pinned apps and grouped open windows; activate, minimize, cycle, open a new window, pin, reorder,
  or close. **Drag** an app along the dock to reorder the pins, or drag a running app in among them
  to pin it (the menu's Move left/right does the same from the keyboard).
- App groups: drop one pinned app onto another and a folder-tile is born, with a 2×2 icon, named
  after its founders, owning its members' windows, badges and previews; its flyout focuses running
  members or launches them; Ungroup pours the members back.
- File Explorer folder windows that predate LucentDock are counted and activated like other apps. A
  normal click activates an existing folder window, and opens one when none is open; while folder
  windows cannot be enumerated it opens nothing, and middle-click and **New window** remain explicit.
- Unread badges: a red count on an app whose window title shows unread messages, such as
  "(3) WhatsApp" or "Inbox (3)". Windows has no public way to read another app's taskbar badge, so
  the count comes from the titles the dock already lists; nothing is stored. On by default
  (Settings > Taskbar & startup > Unread badges).
- Hovering a running app shows a live preview of each of its windows, as the taskbar does: click one
  to switch to it; close it from its tile (or middle-click the tile). Minimized windows show the
  app's icon. The icon shows how many windows are open when there is more than one.
- An app's right-click menu lists its recent files from Windows' own jump lists. If Windows'
  "Show recently opened items" setting is off, the menu offers to open that setting instead.
- Drop files on a dock app to open them with it; rest a drag on a running app to bring its window
  forward and drop into it.
- Win+Ctrl+Alt+1 to 9 open or switch to the dock's first nine apps. Scrolling over an app steps
  through its windows.

## Widgets (14)

- Clock, CPU, memory, network, battery, volume, media, calendar, todos, notes, focus timer, weather,
  screen time, and AI usage.
- Clicking a widget opens its flyout; clicking it again closes it, like a taskbar button. Escape or a
  click anywhere else also closes it.
- Live widgets render inside deliberately stable slots: rotations, clocks, status text and value
  refreshes never resize the dock or move neighboring widgets.
- The clock shows a dot when notifications arrived; its flyout lists this session's notifications,
  including those held during quiet hours.
- AI usage reads Claude and Codex plan limits from each provider's own interface, remembers each
  limit through its reset period, shows the trend ("At this pace: limit reached Thu 14:00, before
  the reset"), and notifies once at the warning level, once at the limit, once at the reset.
  Hovering lists every limit of both providers with its reset time.

## Now playing

- A compact pill: round cover, "Title • Artist", previous/play/next, and progress waves along its
  bottom edge. Scroll to change volume, Shift+scroll to skip 10 seconds, middle-click to play/pause.
- The flyout is a full card: the cover (blurred, as background and in person), a waveform progress
  bar you can drag or move with arrow keys to seek, elapsed and total time, and round controls with
  repeat and shuffle where the playing app supports them. Everything takes the colour of the album
  cover.
- The waves rise and fall with the music, following the playback level of the default playback
  device (a level only, never audio).

## Automations (9)

- Meeting preparation, low battery, focus mode, sunset theme, resource pressure, connectivity,
  screen-time intervention, upcoming-event reminders, and the night layout.
- Automation changes are overlays: they can promote, hide, or emphasize widgets and request a theme
  without rewriting the saved layout. Disposing a layer restores the exact saved layout.
- During a meeting (from 10 minutes before until 15 minutes in), the dock also hides media, weather
  and screen time (a toggle on the automation). At night (22:00 to 07:00, changeable), the night
  layout hides CPU, memory, network and AI usage for a calmer dock; an alert such as low battery
  still brings its widget forward.

## Styles (3)

- Glass (dark acrylic, the default), Liquid glass and Dark (near-opaque dark mode).
- Liquid glass bends the actual wallpaper at its rim with a lens shader in a full-pill silhouette,
  and has a **Glass opacity** slider: 0% is fully transparent (no fill, no blur), the middle is
  blurred, translucent glass, and 100% is a solid fill; each widget sits on its own glass chip with
  a light-catching rim, so text stays readable at every setting.

## Command palette and keyboard

- `Win+Alt+Space` opens a fixed-size, keyboard-first fuzzy search over apps, windows, settings,
  widgets, reversible actions, automation controls and taskbar restoration. It never interprets the
  query as a command and reports urgently if the hotkey is unavailable.
- Full accessibility: semantic buttons/toggles/choices/sliders, accessible names, visible focus,
  Enter/Space activation, arrow navigation, Shift+F10 context menus, Escape-to-close, and
  `Win+Alt+S` to focus the dock (a visible warning appears if another app already uses it).

## Quick settings

- In the volume flyout: Wi-Fi and Bluetooth switch on and off (through Windows.Devices.Radios; if
  Windows won't let the dock switch one, its settings page opens), Focus starts or stops a focus
  session, and Night light opens its settings page.

## Safety

- Startup restores the Windows taskbar first and hides it only after stable dock/AppBar readiness;
  timeouts and Explorer restarts fail visible. The live dock-loss guard, crash-recovery command,
  repeated-failure quarantine, HWND/PID/process-start validation, and bounded transactional updater
  all fail closed.
- Updates from a release folder you choose, or from an https address: manifest and download sizes
  are bounded, size and SHA-256 are checked (and, for signed builds, the same trusted publisher).
  Installation keeps a verified last-known-good executable plus a separate recovery helper, swaps
  atomically, and recovers on failed start, failed health check, or a pending journal found after
  process or power loss.

## Privacy

- No account, no telemetry. Preview generation is isolated, deterministic, fixture-only, and
  privacy-scanned. See [PRIVACY.md](../PRIVACY.md) for the complete data and network story.
- Settings backup (Settings > Privacy & data) writes one file with the dock, widgets, pins, styles
  and each automation's choices; Restore checks the file and asks first. Notes, todos, events,
  screen time, AI usage and automation history are never in it.
- Privacy-safe support tools: Settings can write a redacted diagnostic bundle and show bounded
  automation activity explaining the rule, reason, change and undo state without copying live
  titles or content.

## Multi-monitor and more

- Optionally a dock on every screen: each other screen gets Start, the clock and the windows on that
  screen.
- Weather by Open-Meteo for a city you choose; forecasts for its coordinates only.
- Notification-listener counts are deferred in this unpackaged build: the supported Windows API
  requires package identity and explicit permission; LucentDock does not silently package itself.

## Commands

```text
Strata.exe                          Start the dock; a second launch opens Settings
Strata.exe --exit                   Ask the running dock to exit cleanly
Strata.exe --restore-taskbar        Restore the Windows taskbar without starting the dock
Strata.exe --install                Per-user install and verified copy
Strata.exe --uninstall [--purge]    Remove the dock; --purge also removes user data
Strata.exe --render-previews <dir>  Render deterministic fixture-only PNG previews
Strata.exe --claude-statusline      Claude Code status-line command (set up by Settings > AI usage)
```

Command failures return nonzero exit codes. `--restore-taskbar` retains recovery state when
restoration cannot be confirmed.
