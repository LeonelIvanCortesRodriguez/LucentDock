# Security

## Reporting a problem

Use GitHub's **Report a vulnerability** (Security tab) for anything sensitive, or open an issue for
anything that is safe to discuss in public. Reports reach Leonel Cortés directly; this is a solo
project, so expect an acknowledgement within a few days rather than hours.

## What the updater will and will not do

- Updates come only from the release source **you** configure (Settings > About > Updates): a local
  folder, or an https address. With neither, LucentDock makes no update request at all.
- `update.json` reads are capped at 64 KB; manifests are accepted over https only; a downloaded
  build is checked against the manifest's size and SHA-256 before anything runs.
- Installation is transactional: a verified last-known-good executable and a separate recovery
  helper are kept, the swap is atomic, and a failed start, failed health check, or a pending journal
  found after process/power loss rolls back.
- An update must carry a higher version than the installed build; same-version builds are not
  offered.

## Signing, honestly

Current releases are an **unsigned preview channel**. The SHA-256 check proves the file arrived
intact; it does not prove who published it — whoever controls the release source could change both
the build and its manifest. Settings and the install prompt say exactly this. Once a release is
signed, LucentDock only accepts future updates signed by the same certificate. Verify any downloaded
release against the SHA-256 published alongside it before first run:

```powershell
Get-FileHash Strata.exe
```

## Scope worth knowing

- The dock runs entirely as your user: no service, no driver, no elevation. `--install` and
  `--uninstall` touch only per-user locations.
- There is no account and no telemetry; the only default network request is the weather forecast
  for a city you chose. [PRIVACY.md](PRIVACY.md) is the complete inventory.
- The release process behind every published build — reproducible builds, privacy scans, attended
  verification — is documented with its real records in [docs/process/](docs/process/).
