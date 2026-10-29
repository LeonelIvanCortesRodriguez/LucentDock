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
