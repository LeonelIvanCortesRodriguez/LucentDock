# How this release was built and verified

These are the real working documents of the LucentDock (codename Strata) 1.3.2 release cycle,
2026-10-28, privacy-scrubbed but otherwise unedited. They show how the owner runs software work as a
coordinator of multiple AI systems rather than as a hands-on programmer.

## The workflow

- **The owner** sets direction, authorizes each phase, and personally executes every attended
  verification gate (display-scale changes, keyboard gates, screen-reader checks, final approvals).
  Nothing ships without an attended owner-gate PASS.
- **Claude** (Anthropic) acted as builder and final reviewer: implementing features, running the
  release harness, root-causing failures, and signing off only after independently re-verifying
  every claim (re-running the test suite, recomputing all 194 evidence hashes).
- **Codex** (OpenAI) acted as implementer and independent auditor: writing the final startup fix,
  running its own evidence audit, and producing the closeout.
- **Independent audit subagents** were briefed blind — the owner's explicit rule was that audit
  briefs state only the issues and evidence, never the builder's hypotheses — so each auditor formed
  its own view before comparing.

## Reading order

1. [`AUDIT-REQUEST-STARTUP-HANG-20261028.md`](AUDIT-REQUEST-STARTUP-HANG-20261028.md) — the
   facts-only brief for a release-blocking failure: a deterministic in-gate startup hang that four
   standalone reproductions could not trigger.
2. [`AUDIT-STARTUP-HANG-20261028-claude.md`](AUDIT-STARTUP-HANG-20261028-claude.md) and
   [`AUDIT-STARTUP-HANG-20261028-codex.md`](AUDIT-STARTUP-HANG-20261028-codex.md) — two independent
   audits that converged on the same decisive next step: a process dump taken from the right
   security context. The harness was extended to capture it, and the dump named the exact frozen
   call: a quota CLI launch stuck inside `CreateProcess` on the UI thread during startup.
3. [`CODEX-CLOSEOUT-1.3.2-eeb7ae7-20261028.md`](CODEX-CLOSEOUT-1.3.2-eeb7ae7-20261028.md) — the fix
   (all provider reads moved off the UI thread with a bounded budget), the re-minted candidate, and
   the completed owner-gate run.
4. [`FINAL-REVIEW-1.3.2-eeb7ae7.md`](FINAL-REVIEW-1.3.2-eeb7ae7.md) — the final signoff, listing
   exactly what was re-verified independently and which residual unknowns were accepted and why.
5. [`FINAL-REPORT-1.3.2-eeb7ae7.md`](FINAL-REPORT-1.3.2-eeb7ae7.md) — the acceptance report: what
   shipped, the full verification chain, and the day's complete defect ledger.

## What was scrubbed

Local user-profile paths and development session identifiers were replaced (`%USERPROFILE%`,
`<dev-scratch>`). Evidence file paths on the development machine (`F:\...`) are kept as historical
references; the evidence itself stays with the release candidate, outside this repository, as the
project's privacy policy requires.
