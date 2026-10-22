# Clicky Windows Port — Program Status

Program: bring Clicky (macOS menu-bar AI companion) to Windows, per the 8-phase playbook.
PM artifacts: this file (status), `docs/adr/*.md` (decisions), `tasks/tasks.json` (single source of truth for work).

Last updated: 2026-10-22 — PM

## Current phase

**Phase 3 — Parity spec + CI + tracer bullet (in progress).** 3a + 3b complete; 3c (tracer-bullet build) in progress. ADR 001 APPROVED as written 2026-10-22 (option B; macOS seam-extraction deferred; Worker auth + PostHog-content deferred as open product items below).

## Phase / gate ledger

| # | Phase | Gate | Status |
|---|-------|------|--------|
| 0 | PM scaffolding | `docs/STATUS.md`, `tasks/`, `docs/adr/` exist | GREEN |
| 1 | Portability audit | `docs/01-portability-audit.md` table complete, no "TBD" cells | GREEN (2026-10-22: 61 mapped API rows in 9 groups, 0 TBD; boundary map of all 22 Swift files; licenses all permissive, 4 rejected libs flagged; top risks = overlay fidelity, Ctrl+Alt hook, parity verification) |
| 2 | Strategy ADR + tracer-bullet spec | ADR 001 approved by product owner | GREEN (approved as written 2026-10-22; divergence ratified: Swift seam extraction deferred, residual parity risk on phase-8 Mac sign-off) |
| 3 | Parity spec + CI + tracer bullet | CI green on tracer bullet on a real Windows runner | not started |
| 4 | Implementation plan | doc complete, items mapped to 1a rows + 3a scenarios | not started |
| 5 | Task breakdown | tasks.json populated; every task has ≥1 automatable acceptance criterion | not started |
| 6 | Development | per-task PR + CI green, tasks.json updated | not started |
| 7 | Blind QA | all tasks qa_result=pass | not started |
| 8 | Release gate | all gates green per playbook §8 | not started |

## Open product items (deferred by owner with ADR 001 approval, 2026-10-22)

- **Worker client authentication** — `/chat`, `/tts`, `/transcribe-token` have no client auth; anyone with the URL can spend API credits. A Windows client widens the audience. Owner deferred; revisit before public Windows release.
- **PostHog transcript content** — analytics events carry full transcript + Claude response text (`ClickyAnalytics.swift:83-96`); Windows v1 carries this over for parity. Owner deferred.
- **"Show Clicky" toggle unreachable in UI** (found during 3a): the settings toggle row is never rendered in `CompanionPanelView`; transient mode is only reachable by editing defaults. Product call: expose the toggle on Windows, or keep the hidden default. Spec encodes current behavior.
- **Re-press during transcript finalization is ignored, not cancelling** (found during 3a): re-press cancels only during the Claude request or TTS. Spec encodes actual behavior; flagging in case parity-with-intent was expected instead.
- **Contextual keyterms dead path** (found during 3a): `updateContextualKeyterms` has no callers; only the 11 base keyterms are ever sent. Windows v1 matches this.
- **Onboarding music volume**: code comment says 60%, code sets 0.3. Spec follows the code (0.3).

## Blockers / deviations

1. **No macOS host in this environment.** `otool -L` on a built binary is impossible (no macOS, no built artifact; AGENTS.md forbids terminal xcodebuild anyway). Phase 1 substitutes `import`/pbxproj/entitlements analysis — recorded in the audit doc. Phase 3a requires parity "derived from the RUNNING macOS app"; scenarios will be drafted from code + AGENTS.md and **must be validated against a running macOS app before the phase-8 gate**. This is a standing program risk.
2. **`gh` has no working github.com auth** (only an invalid github.disney.com token). All push/PR/CI verification from phase 3 onward is blocked until `gh auth login` for github.com (or a PAT). Remote is `https://github.com/farzaa/clicky.git`; push rights unverified.
3. **Toolchain substitution:** playbook-specified `claude -p` (Opus/Sonnet, high effort) is available (CLI v2.1.308, verified) and is used as specified. Development phase (6) will use ZCode sub-agents (GLM-5.3) per playbook.
4. `graft/` is a local greppable-cards cache of the source (gitignored, regenerable) — orientation aid only, never authoritative.

## Next action

**Blocked on product-owner approval of `docs/adr/001-windows-strategy.md` (phase-2 gate).** On approval: dispatch phase 3 (parity spec, CI matrix workflow, tracer-bullet build + clean-VM verification). Phase 3 also needs blocker 2 cleared (working `gh`/github.com auth) before CI results can be verified on a real runner.
