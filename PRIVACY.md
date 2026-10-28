# Privacy and deterministic previews

Strata has no account, advertising, analytics, or telemetry.

## Normal-mode data

The default root is `%APPDATA%\Strata`; `STRATA_DATA_ROOT` can select a different root before startup. Files include settings, automation state, taskbar recovery state, local events/todos/notes, capped logs, and per-day screen-time summaries. Writes use temp-file replacement where structured storage is used. With the AI usage widget, `quota.json` and `quota-claude.json` hold only normalized quota figures (percentages, reset times, plan, credits, reset-credit count and expiry); never sign-in tokens, raw provider responses, prompts, conversation text, account ids or paths. Connecting Claude Code changes one key, `statusLine`, in Claude Code's user settings (a `.strata-backup` copy is written first; Disconnect removes only that key). A Phase B audit run can also write `phaseb-commands.log` (the dock's own keyboard-mode commands as position/role tokens, no names or keystrokes), only when `STRATA_PHASEB_COMMAND_LOG=1` and the root is not the default one.

Screen time records the foreground application while the user is active and stops after two minutes idle. It is not uploaded. Notes, todos, and calendar events are local only.

AI usage keeps `quota-history.json` for the forecast: each limit's used percentage over its reset period, about every 10 minutes, dropped eight days after the reset.

The notification history in the clock flyout lives in memory for the session only; it is never written to disk.

The command palette builds its catalog in memory from installed/running applications, existing windows, Strata settings, widgets and actions. Search text is never executed or stored. Recent files are not a palette source in 1.3.

Automation history is bounded and persisted only as fixed rule/signal/action summaries plus time and undo state. Evaluated window titles, notification bodies, event titles, application names, raw condition explanations and exception messages are discarded before save; legacy state is sanitized when loaded. The activity view independently projects those privacy-safe records from fixed definitions.

A diagnostic bundle is created only when the user clicks its Settings action and chooses a destination. It contains the Strata version, executable hash and size, configuration property names and schema hash, categorized exception types, performance summaries and anonymous monitor rectangles. It excludes usernames, paths, window titles, notification/media content, tokens, account identifiers and file names.

Updates read only the release source you choose. A local folder remains local; an https source downloads `update.json` during checks and the executable only after the user chooses Install. The transactional installer keeps a last-known-good executable and a short-lived journal containing only an opaque health token, hashes and time; it contains no user data.

AI usage notifications keep `quota-alerts.json`: which provider windows reached the warning level or their limit in which reset period, and which notifications were sent. No figures beyond that, no account data.

An app's recent files are read from Windows' own jump lists only when you open its menu, and are never stored. Strata doesn't change Windows' recent-items setting; when it's off, the menu only offers to open it.

Window previews show live pictures of your windows, composed by Windows (DWM) directly into Strata's preview card, as the taskbar does. Strata never copies, saves or sends them.

The media player shows the title, artist, cover and position that Windows' media session reports. The cover is processed in memory for its colour. While a track plays, the waves read the default playback device's peak level: a single number many times a second, never the audio itself. None of this is stored.

Weather is Strata's only direct network request that is on by default. The default AI-usage widget invokes the locally installed Codex app-server and Claude Code; those clients may contact their own services using their own sign-ins. Strata does not receive their sign-in tokens and does not persist their raw responses. Updates use the network only if the user enters a web release address (Settings > About > Updates): Strata then downloads that release's update.json once a day, sending only an ordinary request with a "Strata/<version>" user agent, and downloads a new version only when the user clicks Install. With a folder, or with updates off, Strata makes no update request. A city search sends the typed query to Open-Meteo's geocoding endpoint. Forecast requests send the selected coordinates and unit choice to Open-Meteo. Cache keys include location and units; a request token ensures only the newest selection can update the UI.

## Dock upgrades in this round

- Unread badges are read from the window titles the dock already lists, at the moment it lists them; they are never stored or sent anywhere.
- Notification-derived unread counts are not enabled in the unpackaged 1.3 build. Microsoft's supported listener requires package identity, a manifest capability and explicit Windows permission. The prepared boundary is off by default and can hold only bounded in-memory per-app counts and timestamps—never notification bodies, sender names or message text. Denial, revocation or missing identity falls back to the existing title-derived count.
- Quick settings read the Wi-Fi and Bluetooth radio state when the volume flyout opens and change it only when the user taps a tile. Nothing about radios is stored.
- A settings backup is written only where the user chooses, and only when they click Back up. It contains settings.json (including pinned app paths and the weather city) and automation on/off and parameters; it never contains notes, todos, events, screen time, AI usage, automation history or any sign-in.

## Preview mode

`--render-previews` enters `PrivacyGuard` before reading settings. It uses:

- a throwaway storage root rather than the normal Strata directory;
- fixed time and sanitized app/event/media/todo/note/screen-time fixtures;
- fixture volume, weather, system-load, battery, and network values;
- no live window enumeration, screen-time tracking, audio device, media session, or weather request.

Any guarded live access throws and makes the preview command fail. After rendering, `PreviewPrivacy` scans every artifact for `C:\Users\`, the current username, user-profile paths, the normal Strata root, and any supplied live-data markers. The checked-in result is [`previews/privacy-scan.txt`](previews/privacy-scan.txt).

A public release is published as a sanitized repository built from an explicit list of public files, never from the development history. The privacy scan runs on that exact exported tree before anything is published.
