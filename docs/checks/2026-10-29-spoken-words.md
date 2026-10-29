# Check: Spanish narration spoken-words (ledger-watch `words`)

Run on `docs/media/narration.es.json` before any narration is recorded, with the lane handoff's
`watch.mjs`. The narration was written to pronounce no English words: product and personal names
appear on screen only, "la barra" stands in for the dock, and no word uses English spellings
(w, k, sh, th, ck, oo, dg, gh) or endings Spanish words do not have.

## Expected

SPOKEN WORDS PASS, zero flagged words, exit 0.

## Actual (run 2026-10-29, expected committed first)

First run: **FAIL, 2 flagged** — `coordinando` (a genuine Spanish "oo") and `deshaz` (an "s·h"
across the morpheme boundary of *des-hacer*). Both are correct Spanish that the heuristic cannot
tell from English spellings; reported to the ledger as false-positive cases for the skill. Per lane
practice the sentences were rewritten instead of excepted (`dirigiendo`, `revierte`), and the second
run is **PASS, exit 0**. The narration change is in the same commit as this Actual section.
