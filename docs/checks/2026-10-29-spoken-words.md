# Check: Spanish narration spoken-words (ledger-watch `words`)

Run on `docs/media/narration.es.json` before any narration is recorded, with the lane handoff's
`watch.mjs`. The narration was written to pronounce no English words: product and personal names
appear on screen only, "la barra" stands in for the dock, and no word uses English spellings
(w, k, sh, th, ck, oo, dg, gh) or endings Spanish words do not have.

## Expected

SPOKEN WORDS PASS, zero flagged words, exit 0.
