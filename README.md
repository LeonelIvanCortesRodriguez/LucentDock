# LucentDock 1.3

**LucentDock** is this project's public name; the code, evidence and process documents keep its internal codename **Strata**.

> A smart dock for Windows: your apps, your day and your limits in one bar, which rearranges itself when something needs you.

Strata is a native Windows 10/11 dock built with C# and WPF. It combines pinned and running apps, 14 live widgets, and 9 signal-driven automations. Automation changes are overlays: they can promote, hide, or emphasize widgets and request a theme without rewriting the user's saved layout.

## What is included

- Apps: pinned apps and grouped open windows; activate, minimize, cycle, open a new window, pin, reorder, or close. **Drag** an app along the dock to reorder the pins, or drag a running app in among them to pin it (the menu's Move left/right does the same from the keyboard).
- File Explorer folder windows that predate Strata are counted and activated like other apps. A normal click activates an existing folder window, and opens one when none is open; while folder windows cannot be enumerated it opens nothing, and middle-click and **New window** remain explicit.
- Unread badges: a red count on an app whose window title shows unread messages, such as "(3) WhatsApp" or "Inbox (3)". Windows has no public way to read another app's taskbar badge, so the count comes from the titles the dock already lists; nothing is stored. On by default (Settings > Taskbar & startup > Unread badges).
- Notification-listener counts are deferred in this unpackaged build: the supported Windows API requires package identity, a manifest capability and explicit permission. Strata does not silently package itself or request notification access, and notification presence would not always mean an unread message.
- Widgets (14): clock, CPU, memory, network, battery, volume, media, calendar, todos, notes, focus timer, weather, screen time, and AI usage.
- AI usage (new in 1.2; on the dock by default, and added once to docks set up before 1.2): Claude and Codex plan limits from each provider's own interface: Codex through `codex app-server` (`account/rateLimits/read`), Claude in the background through Claude Code's own `/usage` (`claude -p /usage`, hidden; no tokens; nothing saved to your sessions), after a silent `claude auth status` check. When a sign-in is needed, the flyout offers "Sign in to Claude…", the only thing that opens a window. The status-line `rate_limits` connection stays as an optional extra. Every figure says "used" or "remaining"; missing data reads "Unavailable" or "Waiting for first sample", never 0; windows from different providers are never combined. No private endpoints are used, and the native usage panels are never scraped.
- Automations (9): meeting preparation, low battery, focus mode, sunset theme, resource pressure, connectivity, screen-time intervention, upcoming-event reminders, and the night layout.
  - Automatic layouts: during a meeting (from 10 minutes before until 15 minutes in), the dock also hides media, weather and screen time (a toggle on the automation). At night (22:00 to 07:00, changeable), the night layout hides CPU, memory, network and AI usage for a calmer dock; an alert such as low battery or a busy PC still brings its widget forward.
- Styles (3): Glass (dark acrylic, the default), Liquid glass and Dark (near-opaque dark mode). Liquid glass has a **Glass opacity** slider: 0% is fully transparent (no fill, no blur), the middle is blurred, translucent glass, and 100% is a solid fill; each widget sits on its own glass chip with a light-catching rim, so text stays readable at every setting. Settings saved by 1.1 move to the nearest style: Frost and White become Liquid glass, Midnight and Black become Dark.
- Live widgets render inside deliberate stable slots. AI-provider rotation, clocks, status text, now-playing updates and ordinary value refreshes do not resize the dock or move neighboring widgets; exceptional secondary text ellipsizes while its complete accessible name and tooltip remain available. One state change does resize a slot: with nothing playing the media pill is a single small icon and its slot takes a second, equally fixed width, gliding between the two as a track starts or stops.
- Safety: startup restores the Windows taskbar first and hides it only after stable dock/AppBar readiness; timeouts and Explorer restarts fail visible. The live dock-loss guard, crash-recovery command, repeated-failure quarantine, HWND/PID/process-start validation, and bounded transactional updater all fail closed.
- Now Playing: Windows media-session change events update session, metadata, playback and timeline state, backed by a serialized 15-second fallback refresh.
  - The dock shows a compact pill: round cover, "Title • Artist", previous/play/next, and progress waves along its bottom edge.
  - The flyout is a full card:
    - the cover, blurred, as its background;
    - the cover itself and the title;
    - a waveform progress bar you can drag or move with the arrow keys to seek;
    - elapsed and total time;
    - round controls, with repeat and shuffle where the playing app supports them.
  - Everything takes the colour of the album cover.
  - The waves rise and fall with the music, following the playback level of the default playback device (a level only, never audio). Each part of the song has its own scale. Paused, the bar is a flat line in the cover's colour. The waves move only while music plays and Windows' animation effects are on.
- Quick settings in the volume flyout: Wi-Fi and Bluetooth switch on and off (through Windows.Devices.Radios; if Windows won't let Strata switch one, its settings page opens), Focus starts or stops a focus session, and Night light opens its settings page (Windows has no public way for an app to switch it).
- Settings backup (Settings > Privacy & data > Back up settings): one file with the dock, widgets, pins, styles and each automation's choices, to keep a copy or move to another PC; Restore checks the file and asks first. Notes, todos, events, screen time, AI usage and automation history are never in it.
- Clicking a widget opens its flyout; clicking it again closes it, like a taskbar button. Escape or a click anywhere else also closes it.
- On the media pill: scroll to change the volume, Shift+scroll to skip 10 seconds, middle-click to play or pause.
- Hovering the AI usage tile lists every limit of both providers with its reset time. AI usage also notifies once when a limit reaches the warning level, once when it is reached, and once when it resets (Settings > AI usage > Notifications; quiet hours apply).
- Drop files on a dock app to open them with it; rest a drag on a running app to bring its window forward and drop into it.
- AI usage remembers each limit's figure through its reset period: the flyout shows the trend and where it is heading ("At this pace: limit reached Thu 14:00, before the reset", or "about 62% used by the reset").
- Win+Ctrl+Alt+1 to 9 open or switch to the dock's first nine apps. Windows keeps Win+number for its own taskbar even while it is hidden, and Ctrl+Alt is AltGr on many keyboards. Scrolling over an app steps through its windows.
- The clock shows a dot when notifications arrived; its flyout lists this session's notifications, including those held during quiet hours.
- Optionally a dock on every screen (Settings > Taskbar & startup): each other screen gets Start, the clock and the windows on that screen.
- Updates from a release folder you choose, or from an https address such as the project's GitHub repository: Strata bounds manifest/download sizes, contains staging I/O failures, checks size and SHA-256 and, for signed builds, the same trusted publisher. Installation keeps a verified last-known-good executable plus a separate recovery helper, swaps atomically, waits for bounded dock readiness, and recovers on failed start, failed health check, or a pending journal found after process/power loss.
- An app's right-click menu lists its recent files from Windows' own jump lists. If Windows' "Show recently opened items" setting is off, the menu offers to open that setting instead.
- Hovering a running app shows a live preview of each of its windows, as the taskbar does: click one to switch to it; close it from its tile (or middle-click the tile). Minimized windows show the app's icon. The icon shows how many windows are open when there is more than one.
- Accessibility: semantic buttons/toggles/choices/sliders, accessible names, visible focus, Enter/Space activation, arrow navigation, Shift+F10 context menus, Escape-to-close, and `Win+Alt+S` to focus the dock (a visible warning appears if Windows or another app already uses it).
- Command palette: `Win+Alt+Space` opens a fixed-size, keyboard-first fuzzy search over apps, windows, Strata settings, widgets, reversible actions, automation controls and taskbar restoration. It never interprets the query as a command, reports urgently if the hotkey is unavailable, and has no recent-file source in 1.3.
- Privacy-safe support tools: Settings can write a redacted diagnostic bundle and show bounded automation activity explaining the rule, reason, change and undo state without copying live titles or content.
- Privacy: no account or telemetry. Preview generation is isolated, deterministic, fixture-only, and privacy-scanned.

## Commands

```text
Strata.exe                          Start the dock; a second launch opens Settings
Strata.exe --exit                   Ask the running dock to exit cleanly
Strata.exe --restore-taskbar        Restore the Windows taskbar without starting Strata
Strata.exe --install                Per-user install and verified copy
Strata.exe --uninstall [--purge]    Remove Strata; --purge also removes user data
Strata.exe --render-previews <dir>  Render deterministic fixture-only PNG previews
Strata.exe --claude-statusline      Claude Code status-line command (set up by Settings > AI usage > Connect)
```

Actual command failures return nonzero exit codes. `--restore-taskbar` retains recovery state when restoration cannot be confirmed.

## Releases, signing and updates

`pwsh tools/make-release.ps1 -Out <folder>` copies `dist\Strata.exe` into a release folder with `update.json` (version, SHA-256, size) and release notes. Point Strata's Settings > About > Updates at that folder.
- **Signing:** with `-CertThumbprint <thumbprint> -TimestampServer <https-url>`, the release is signed and timestamped first. CI may provide `STRATA_SIGNING_CERT_THUMBPRINT` and `STRATA_TIMESTAMP_SERVER`; private keys and passwords are not command-line parameters. A public release needs a trusted code-signing authority or Azure Trusted Signing; a self-signed certificate is not a substitute.
- **Updates between signed builds:** once Strata is signed, it accepts updates only signed by the same certificate.
- **Unsigned builds are a preview channel.** An unsigned Strata checks each download against the SHA-256 in the same `update.json`. That is integrity checking (the file arrived intact), not publisher authentication: whoever controls the release can change both. Settings and the install prompt say so. Sign releases before recommending automatic updates to the public.
- **Versions:** an update must have a higher version (`<Version>` in `src/Strata/Strata.csproj`); builds of the same version are not offered.

## Build, test, preview, publish

Requires the .NET 10 SDK. In this development environment the SDK executable is `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`.

```powershell
dotnet build src\Strata\Strata.csproj -c Release --no-incremental
dotnet test tests\Strata.Tests\Strata.Tests.csproj -c Release
dotnet src\Strata\bin\Release\net10.0-windows10.0.19041.0\Strata.dll --render-previews previews
dotnet publish src\Strata\Strata.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=none -o dist
```

The tracked preview inventory is in [`previews/index.txt`](previews/index.txt), with the scan result in [`previews/privacy-scan.txt`](previews/privacy-scan.txt).

## Data and network

Normal data lives under `%APPDATA%\Strata`, or `STRATA_DATA_ROOT` when explicitly configured. Weather is Strata's only direct network request that is on by default; city search sends the entered query to Open-Meteo and forecasts send the selected coordinates. The default AI-usage widget also invokes the locally installed Codex app-server and Claude Code, and those clients may contact their own services with their own sign-ins; for Claude, `/usage` is answered without a model request. Updates go online only if you enter a web release address. See [PRIVACY.md](PRIVACY.md).

## Design and verification

- [ARCHITECTURE.md](ARCHITECTURE.md) — lifecycle, signals, automations, layout overlays, native integration, and failure boundaries.
- [`tests/`](tests/) — the automated test suite; [`previews/`](previews/) — fixture-only screenshots of every style, flyout and Settings page.

Each release candidate is also verified live (UI Automation, display scaling, keyboard, Explorer behavior and a 30-minute soak) and independently audited. For the 1.3.2 release, the audit briefs, independent audits, closeout and final acceptance report are included, privacy-scrubbed, in [docs/process/](docs/process/).

Licensed under the [MIT License](LICENSE).
