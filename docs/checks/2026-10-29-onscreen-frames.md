# Check: on-screen words (ledger-watch `frames`, PaddleOCR)

The frames check reads one frame per narration line (0.9 s after the line starts) with PaddleOCR
and confirms the on-screen headline. The timing files it needs are tracked
(`docs/media/timing-en.json`, `timing-es.json`), so the check can be rerun on any machine that has
the OCR environment.

## Expected

**SKIPPED on this PC.** The build machine (Node01) has no PaddleOCR environment; the lane brief
says to record the check as skipped with the reason rather than install one to make it pass. The
headlines are baked into the scene cards at scene start (0 s offset from each line), so when the
check is rerun on the main PC via `LEDGER_OCR_PYTHON`, the expectation is 7 of 7 headlines read per
language.
