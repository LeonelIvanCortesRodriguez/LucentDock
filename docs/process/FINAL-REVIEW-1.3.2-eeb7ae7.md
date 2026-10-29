# Final review: Strata 1.3.2+eeb7ae7 — SIGNED OFF

Reviewer: Claude (builder, final reviewer per the owner's workflow). 2026-10-28 ~18:45.
Scope: independent verification of the Codex closeout (`CODEX-CLOSEOUT-1.3.2-eeb7ae7-20261028.md`).
Verdict: **APPROVED for installation.** Installation itself remains gated on the owner's explicit
authorization and was not performed as part of this review.

## What I verified myself (not taken from any report)

1. **Identity chain.** `dist\1.3.2-preview-eeb7ae7\Strata.exe`: SHA-256 and size recomputed and match
   (`ADAD5A9A…CEE`, 199,232,481 bytes); ProductVersion `1.3.2+eeb7ae7…`; `refs/public/v1.3.2-eeb7ae7`
   resolves to the public commit; tracked working tree clean at harness HEAD `5027d1d`.
2. **Product tests.** Ran the full suite myself: **682/682 pass** (Release, 24 s).
3. **Owner-gate evidence.** Recomputed every artifact hash in the completed attempt
   `completed\20261028-170933-59dba4c6`: **23/23 gate records pass, 194/194 hashes match.** The three
   150% records are present and passing; the `scalesCaptured` summary omission is cosmetic.
   `progress.json`, `final-summary.json` and `latest-owner-gate-result.json` agree on PASS, bound to
   the exact candidate hash; no active attempt remains; `pending-gates.json` = OWNER_GATES_COMPLETE.
4. **The fix itself (code review).** The dumped root cause — a quota CLI launch reaching
   `CreateProcess` on the UI thread during widget construction — is removed by two independent
   layers: `QuotaMonitor.Tick` invokes sources via `Task.Run` (so even a source blocking before its
   first await cannot touch the caller's thread), and `QuotaReadRunner` bounds every provider read
   with a 10 s budget whose public deadline is isolated from worker cancellation, with admission
   control against worker pile-up and guaranteed child cleanup (late-handle kill, process-tree kill
   with fallback). Diff footprint is exactly the quota subsystem plus 498 lines of new regression
   tests (startup non-blocking, read budget, cancellation cleanup). Codex's static review found and
   resolved two real cancellation-ordering defects before minting; her stated limits are accurate.

## Accepted residual items (non-blocking, carried forward)

- **Environmental trigger unproven.** Why `CreateProcess` of the fixture file wedged only inside the
  gate on the morning session (Defender half-applied update suspected) was never proven; the
  pre-reboot baseline cannot be reconstructed. The 1.3.2 design no longer depends on the answer: a
  wedged launch now costs one failed quota read, never the dock.
- **125% stall family.** The historical 8–11 s sampling stalls remain characterized-not-explained;
  the final attended 125% soak had zero stalls/blanks over 258 samples. Instrumentation stays.
- **Harness nits for next cycle:** `scalesCaptured` summary omits 150; the per-candidate hardcoded
  ExpectedVersion prefix remains a ritual gotcha (documented in memory and handoffs).

## The day's full defect ledger (for the record)

1. pwsh DPI pre-lock froze runner scale detection → fixed in harness `b073652` (per-thread DPI read).
2. In-gate startup hang, 4/4 at 125% geometry → root-caused by in-harness dump capture `2d8370d`
   (`Save-CandidateHangCapture`), fixed in product `8b05994` + `2a601db` (Strata 1.3.2), verified by
   two independent audits (Claude subagent + Codex) that converged on the dump as decisive.
3. Owner-gate run on 1.3.2: PASS, all scales, keyboard, palette, Explorer, policies, Narrator.

## Remaining steps (owner-gated)

Back up installed 1.3.0+370dec2 → install 1.3.2 (hash-verified copy) → smoke test → final
acceptance report. Publication/outward steps remain out of scope.
