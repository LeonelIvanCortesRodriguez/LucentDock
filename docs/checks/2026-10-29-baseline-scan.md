# Check: baseline pattern scan and privacy sweep (untouched clone)

Run on the fresh `gate6-lane1b` clone at `9c9a1db` before any lane change, with the ledger's
`patterns.mjs` and `privacy-sweep.mjs` from the lane handoff folder.

## Expected

- Privacy sweep: CLEAN, exit 0. If anything is found, it will be inside `docs/process/` quoted
  audit text, not in code, and the lane's fix set 7 must remove or neutralize it.
- Pattern scan: 10 hits or fewer, every one of them quoted text in `docs/process/` audit records
  or README prose, none in `src/`, `tests/` or `tools/` code. Exit 2 is possible solely because a
  quoted finding matches a BLOCK rule (the ledger learned in lane 1 that its own quoted notes
  trip the scanner by design).

## Actual (run 2026-10-29, after the expected section was committed in fabaf13)

- Pattern scan: **PASS, 0 hits, exit 0** — better than expected; nothing in this repo quotes an
  attack pattern.
- Privacy sweep: **FOUND 26, exit 1 — the expected CLEAN was wrong, but not because of a leak.**
  By rule: 24 × "account name", 1 × "local path", 1 × "home path". This machine's Windows account
  uses a generic uppercase word, and the sweep's old account-name rule matched it case-insensitively as a
  whole word, so every ordinary English "user" in README/PRIVACY/ARCHITECTURE/audit prose flags.
  The development repo's own privacy scanner matches account names case-sensitively for exactly
  this reason. The other two hits quote generic paths in their own documentation (`PRIVACY.md`
  describing the drive-rooted profile marker its preview scanner looks for; `tools/claude-terminal.ps1`
  naming `~/.claude/settings.json`). Disposition: the two quoted-path lines get reworded in this
  lane's fix set 7; the 24 word-"user" hits are a tool limitation on this PC, reported to the
  ledger as a skills finding, and the final sweep is expected to print FOUND with only that class
  remaining.
