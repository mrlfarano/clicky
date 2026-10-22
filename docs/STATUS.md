# Clicky Windows Port — Program Status

Program: bring Clicky (macOS menu-bar AI companion) to Windows, per the 8-phase playbook.
PM artifacts: this file (status), `docs/adr/*.md` (decisions), `tasks/tasks.json` (single source of truth for work).

Last updated: 2026-10-22 — PM

## Current phase

**Phase 6 — Development, wave 1 in flight.** Done so far (each: worktree → implement → tests → PR → CI green → squash-merge → tasks.json recorded): **T-006** (PR [#1](https://github.com/mrlfarano/clicky/pull/1)), **T-019** (PR [#2](https://github.com/mrlfarano/clicky/pull/2)), **T-017** (PR [#3](https://github.com/mrlfarano/clicky/pull/3), GoldenVectors.cs conflict with T-006 resolved locally, 78/78 Core tests post-merge). In flight (4 parallel dev agents in isolated worktrees): T-001 replay-manifest, T-003 overlay placement, T-013 Velopack CI, T-016 crash reporting. Blocked on macOS: T-008, T-009. Phases 0–5 all green.

## Phase / gate ledger

| # | Phase | Gate | Status |
|---|-------|------|--------|
| 0 | PM scaffolding | `docs/STATUS.md`, `tasks/`, `docs/adr/` exist | GREEN |
| 1 | Portability audit | `docs/01-portability-audit.md` table complete, no "TBD" cells | GREEN (2026-10-22: 61 mapped API rows in 9 groups, 0 TBD; boundary map of all 22 Swift files; licenses all permissive, 4 rejected libs flagged; top risks = overlay fidelity, Ctrl+Alt hook, parity verification) |
| 2 | Strategy ADR + tracer-bullet spec | ADR 001 approved by product owner | GREEN (approved as written 2026-10-22; divergence ratified: Swift seam extraction deferred, residual parity risk on phase-8 Mac sign-off) |
| 3 | Parity spec + CI + tracer bullet | CI green on tracer bullet on a real Windows runner | GREEN (2026-10-22: `docs/02-parity-spec.md` (~50 scenarios, 8 groups); `.github/workflows/ci.yml`; tracer bullet in `windows/` + `shared/` — build 0 warnings, 60/60 tests local; fork `mrlfarano/clicky` run 39796154625: macos-parity ✓ linux-core-tests ✓ windows-build ✓; branch protection on fork main: required checks macos-parity/linux-core-tests/windows-build, strict contexts, admin exempt. **Still open → phase 7 QA:** interactive VM acceptance matrix ADR items 3–11 (DE AltGr, watchdog stall+reinstall, mixed-DPI 4 px, focus/click-through, live Worker round-trip) and the Swift-side golden-vector XCTest (needs Mac). Clean-VM install evidence = CI replay e2e on windows-latest; interactive install pending QA.) |
| 4 | Implementation plan | doc complete, items mapped to 1a rows + 3a scenarios | GREEN (2026-10-22: `docs/03-implementation-plan.md` — 24 risk-first workstreams WS-01…24, packaging/signing/installer/crash-reporting first-class WS-08…11, dependency shape, §1a coverage checklist; caveats: several "verify" pins — EV/Trusted-Signing guidance, WebView2 bundling, interactive-desktop availability of windows-latest for keystroke injection) |
| 5 | Task breakdown | tasks.json populated; every task has ≥1 automatable acceptance criterion | GREEN (2026-10-22: 44 tasks, schema-validated, no dangling deps; roots T-001/T-003/T-006/T-013/T-016/T-017/T-019; T-008, T-009 blocked=macOS. Known gap: plan cites parity scenario PNT-07 which docs/02 does not define — owning dev task must add it to the spec) |
| 6 | Development | per-task PR + CI green, tasks.json updated | IN PROGRESS (wave 1 dispatching) |
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
2. **`gh` CLI has no working github.com auth** (invalid github.disney.com token only). Resolved for program purposes: git credential manager holds a working `mrlfarano` github.com credential; work continues on the **fork `mrlfarano/clicky`** (created 2026-10-22, remote alias `fork`; `origin` = upstream `farzaa/clicky`, read-only). CI runs, pushes, branch protection and in-fork PRs work via git + REST API. Revisit if the owner wants PRs against upstream.
3. **Toolchain substitution:** playbook-specified `claude -p` (Opus/Sonnet, high effort) is available (CLI v2.1.308, verified) and is used as specified. Development phase (6) will use ZCode sub-agents (GLM-5.3) per playbook.
4. `graft/` is a local greppable-cards cache of the source (gitignored, regenerable) — orientation aid only, never authoritative.

## Next action

Wave 1 remainder (T-001, T-003, T-013, T-016) running in dev agents — on completion: PM pushes each branch, opens PR to `feature/windows-build` (protected: required checks macos-parity/linux-core-tests/windows-build), merges on green, records in tasks.json. Then wave 2 per `depends_on` (next up: T-002 overlay window redesign, T-004/T-005 hook hardening, T-010/T-011/T-012 packaging-crash-update chain, T-014+ pipeline tasks). Known sequencing note: branches made before a merge may conflict; resolve locally like T-017's GoldenVectors.cs (both changes kept, tests re-run). Phase 7 blind QA needs interactive-VM access (ADR acceptance items 3–11) — owner should provision or accept a manual-QA session.
