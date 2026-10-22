# 03 — Implementation plan: Clicky for Windows v1

Phase 4 deliverable. Author: Senior Software Engineer. Date: 2026-10-22.
Basis: [ADR 001](adr/001-windows-strategy.md) (approved), [audit](01-portability-audit.md) (§1a rows, §1b seams, §1d risks), [parity spec](02-parity-spec.md) (scenario IDs, fixtures, harnesses), [STATUS](STATUS.md), and the phase-3 tracer bullet in `windows/` + `shared/`.

## How to read this

- **Contract for phase 5.** Each workstream (WS) becomes one or more tasks in `tasks/tasks.json`. *Depends on* lists WS IDs and maps straight onto `depends_on`; *External* names a blocker that is not code. Phase 5 also replaces the spec's `03-plan §x` placeholders with these WS IDs.
- **Effort:** S ≤ 3 dev-days, M ≤ 2 weeks, L > 2 weeks. *Rows* are the audit §1a rows a WS owns, and every surviving row has exactly one owner ([checklist](#coverage-checklist)). *Scenarios* are the parity-spec IDs the WS closes.
- **Order is risk-first.** WS-01–07 retire audit risks 1 (overlay), 2 (hook) and 3 (parity). WS-08–11 come next because of lead time: SmartScreen reputation only accrues over calendar time, and the signing identity needs owner action. The rest follow in descending risk; a prerequisite pulled forward is marked as such.
- **Harnesses:** *Core* = `Clicky.Core.Tests`, run in the `linux-core-tests` and `windows-build` jobs. *Replay* = `Clicky.Windows.Tests`, in `windows-build`. *XCTest* = `macos-parity`. *Manual-VM* = the ADR acceptance rig, where phase 7 QA attaches evidence.
- **The tracer is the base.** Already working: the hook with its Raw Input watchdog, `LowLevelKeyboardEventTranslator`, `PushToTalkDetector`, WGC capture, `CursorOverlayWindow`, `ClaudeRequestBuilder`, `SseLineParser`, `PointTag`, `ScreenshotToScreenMapper`, `StreamingBubbleText`, replay/record, and 4 golden files (60 tests green).
- **STATUS items** (Show Clicky toggle, re-press during finalize, dead keyterms, music volume) follow the behaviour the spec encodes; this plan does not reopen them.

## Workstreams (ordered)

### WS-01 · Overlay: small moving window + mixed-DPI mapping — L (risk 1)
- **Rows** E1, D6, D7, E6 · **Scenarios** PNT-02–05, CLA-04, CLA-05 · **Depends on** —
- **Work:**
  - Replace the tracer's fixed 420×240-DIP window with one content-sized window (glyph + bubble) that moves. It is positioned in physical px with `SetWindowPos`.
  - Cursor-follow runs on `CompositionTarget.Rendering` and calls `SetWindowPos` only when the position changes. Re-enumerate monitors on `WM_DISPLAYCHANGE`.
  - **Decision (PNT-04):** the tip is the hotspot and sits exactly on the target. The Mac's 8/12 pt offset moves to the bubble, which flips to stay 20 DIP inside `rcWork` (`OverlayWindow.swift:463-474`).
  - **Decision (CLA-04):** keep the streaming bubble while `processing`; from TTS start, use the Mac's `responding` visuals.
- **Tests:** `ScreenMappingGoldenVectorTests` and the Replay point/stream tests already exist. Write Core unit tests for a new pure `OverlayPlacement` (flip and clamp at each edge), plus a Replay assert that `hotspotPx == targetPx`. Manual-VM: ADR acceptance 8 and 11.
- **DoD:** All 4 PNT-04 runs land ≤ 4 px on the mixed-DPI rig, and PNT-05 shows one buddy. Following the cursor on a 4K@150% monitor costs < 2% of one core over 60 s (`Get-Counter`).

### WS-02 · Virtual-desktop spike — M (risk 1; audit E2 = L)
- **Rows** E2 · **Scenarios** none yet; add the Manual-VM check PNT-07 (adr-derived) to the spec · **Depends on** WS-01
- **Work:** Documented APIs only. On `EVENT_SYSTEM_FOREGROUND`, if `IsWindowOnCurrentVirtualDesktop(overlay)` is false, call `MoveWindowToDesktop(overlay, GetWindowDesktopId(foreground))`. This works because the windows are our own process's. Apply the same to the panel.
- **Tests:** Manual-VM only: 10 desktop switches in persistent mode.
- **DoD:** The verdict is recorded as an ADR addendum. If adopted, the buddy is visible on the new desktop within 500 ms in 10/10 switches. Otherwise the ADR fallback (recreate on the next press) is the documented behaviour.

### WS-03 · Keyboard-hook hardening + configurable shortcut — M (risk 2)
- **Rows** C1, C2, C3 · **Scenarios** PTT-01, PTT-04–08 · **Depends on** — (the settings key lands in WS-12)
- **Work:**
  - The watchdog also reinstalls the hook on `PBT_APMRESUMEAUTOMATIC` and `WTS_SESSION_UNLOCK` (via `WTSRegisterSessionNotification` on the existing message window), and resets the translator and detector.
  - The shortcut option hot-swaps through the translator channel, and Core supplies the Windows copy.
  - The hook never logs or persists key codes; it records transitions only.
  - **Decision (PTT-04):** either Shift key counts for the Shift options. macOS `.shift` is side-agnostic, and Shift has no AltGr hazard. Remove the `shortcut:` comment.
- **Tests:**
  - `push-to-talk.json` exists (21 cases, including AltGr). Add `shift-ctrl-space-press-release`, `shift-ctrl-right-shift-presses` and `shift-ctrl-release-on-ctrl-up`.
  - Write a `SendInput` smoke test in Replay (trait `Desktop`, skipped when the runner has no input desktop). It checks the press/release logs and the `--debug-stall-hook-once` reinstall.
  - Manual-VM: ADR acceptance 3–6, PTT-08, sleep/resume, and lock/unlock.
- **DoD:** The vectors cover every option × branch and pass on `linux-core-tests`. On the VM, ADR acceptance 3–6 pass, and the first press after resume and after unlock activates.

### WS-04 · Parity-harness extension — M (risk 3)
- **Rows** — (infrastructure) · **Scenarios** enables every Replay scenario below · **Depends on** —
- **Work:**
  - Add `--replay-manifest <json>`, which maps `/chat` to `.sse`, `/tts` and `/transcribe-token` to `{status, headers, body|bodyFile}`, and the AssemblyAI socket to a `.jsonl` fake transport. Unmapped routes fail closed.
  - Add `--settings-override k=v`, `--script <presses.json>` and `--debug-trigger-onboarding-demo`.
  - Add the report fields `voiceStateTimeline`, `analyticsEvents`, `ttsRequests` and `fallbackUtterances`, with stubbed audio and analytics sinks.
  - Every timer takes the BCL `TimeProvider`. Tests use `Microsoft.Extensions.TimeProvider.Testing` (MIT, test-only).
- **Tests:** `ClickyReplayProcess` exists. Write self-tests: a bad manifest exits 2, and an unmapped route exits 1 without opening a socket.
- **DoD:** `windows-build` replays at least one fixture of each new kind, every new report field is asserted, and the fail-closed test passes.

### WS-05 · Swift-side golden XCTest — S (risk 3; **needs macOS**)
- **Rows** — (runs seams 2 and 7 on Swift) · **Scenarios** PNT-01, PTT-01, PTT-04 (Swift half) · **Depends on** — · **External:** a macOS host. The `macos-latest` runner can compile and run the test through push-and-wait; a local Mac is strongly preferred, and the phase-8 sign-off needs a real Mac regardless (STATUS blocker 1).
- **Work:**
  - Add `leanring-buddyTests/GoldenVectorTests.swift`. Synchronized groups mean no `pbxproj` edit.
  - Mark it `@MainActor` (default isolation is MainActor). It loads both vector files via a `#filePath`-relative path.
  - It calls `parsePointingCoordinates` (`CompanionManager.swift:784`) and `shortcutTransition(for:keyCode:modifierFlagsRawValue:…)` (`BuddyDictationManager.swift:114`), mapping neutral modifiers to `CGEventFlags`.
  - It skips cases with `"platform":"windows"`, and pins `label-looks-like-screen` from the Swift output.
  - In `macos-parity`, add `xcodebuild test -only-testing:leanring-buddyTests/GoldenVectorTests CODE_SIGNING_ALLOWED=NO`, which resolves the `ci.yml` TODO.
- **Tests:** This WS is the test.
- **DoD:** `macos-parity` runs both vector files against Swift and is green, and `label-looks-like-screen` holds Swift-observed values.

### WS-06 · Mac prompts as bundle resources (ADR macOS change b) — S (risk 3; **needs macOS**)
- **Rows** — (seam 10) · **Scenarios** CLA-02, ONB-02 (prompt source) · **Depends on** WS-05 (same Mac CI loop) · **External:** as WS-05
- **Work:**
  - Add both `shared/prompts/*.txt` files to the app target as resources.
  - Replace the literals at `CompanionManager.swift:544-577` and `:950-962` with a `Bundle.main` loader.
  - Change `CompanionVoiceResponsePromptTests` to assert that the literal is gone and that the loader names match the files.
- **Tests:** An XCTest checks that both prompts load from the built bundle.
- **DoD:** The loaded prompts are byte-identical to `shared/prompts/`, and `CompanionManager.swift` contains no prompt literal.

### WS-07 · Session reducer (seam 6) + app coordinator — L (risk 3)
- **Rows** G7, G9, G10 · **Scenarios** PTT-02, PTT-03, CLA-07, TTS-02 (timing), ONB-03, PNL-06 (vector), AN-02 (vector) · **Depends on** WS-04
- **Work:**
  - Add a pure Core `CompanionSession` reducer: events in, `{voiceState, overlayCommands, history, analyticsEvents, panelStatus}` out.
  - It ports `CompanionManager.swift:430-540, 586-726, 732-756`:
    - A re-press cancels the response or TTS in flight, but is ignored during finalize.
    - A tap without speech resets the session.
    - History keeps 10 exchanges of `spokenText`.
    - The transient hide fires 1 s after TTS and pointing end, and is cancelled by a press. Mac polls become events on `TimeProvider`.
  - A shell `CompanionCoordinator` takes over the orchestration from `TracerInteractionRunner`. It marshals onto the `Dispatcher` (G7), exposes CommunityToolkit.Mvvm state (G9, MIT) and uses `TimeProvider` timers (G10).
- **Tests:** Write `shared/golden/session-reducer.json` with every seam-6 case ID the spec names, run on a virtual clock. Write the PTT-02 Replay test with a scripted re-press at 300 ms; WS-17 completes its `ttsRequests` assert.
- **DoD:** The vector passes on both Core jobs, and `TracerInteractionRunner` no longer makes state transitions itself.

### WS-08 · Velopack packaging pipeline in CI — M (lead time)
- **Rows** A6 · **Scenarios** — (the spec leaves packaging to the plan) · **Depends on** —
- **Work:**
  - Add the `Velopack` NuGet (MIT; pin it and re-verify the licence), with `VelopackApp.Build().Run()` as the first line of `Main`.
  - In CI, the zip step becomes `vpk pack --packId Clicky --packVersion <SemVer>-ci.<run> --channel win-beta --mainExe Clicky.exe`, using the same pinned `vpk` version. The Defender scan also covers `Setup.exe` and the `.nupkg`.
  - A6: the app stays unpackaged Win32, with no MSIX and no camera capability.
  - The Velopack root `%LOCALAPPDATA%\Clicky\` is also the ADR settings directory, so `settings.json`, `logs\` and `crashes\` sit beside `current\`. They must survive updates (verify), and uninstall removes them (accepted).
- **Tests:** On `windows-latest`, silent-install `Setup.exe` and run the installed `Clicky.exe --replay point-basic.sse`; it must exit 0.
- **DoD:** Every `windows-build` produces a Defender-clean `Setup.exe`, a full `.nupkg` and `releases.win-beta.json`, and the installed copy passes the replay.

### WS-09 · Authenticode signing, gated on secrets — S (lead time)
- **Rows** A5 · **Scenarios** — · **Depends on** WS-08 · **External:** the owner provisions the signing identity and secrets; Trusted Signing identity validation takes time.
- **Work:**
  - **Primary:** Azure Trusted Signing via `vpk pack --azureTrustedSignFile <metadata.json>`, using the secrets `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `AZURE_CLIENT_SECRET`. It signs `Clicky.exe`, `Update.exe` and `Setup.exe`.
  - **OV fallback:** since the 2023-06 CA/B rule, new OV keys must live in an HSM, so the `.pfx`-secret step in `ci.yml` cannot work with a new certificate. Replace it with an HSM-backed certificate (for example Azure Key Vault Premium) applied via `vpk --signTemplate` + AzureSignTool (MIT).
  - No EV (per the ADR; re-verify when pinning).
  - The sign step runs only when its secrets exist, so fork PRs always skip it. The release job (WS-21) refuses unsigned output.
- **Tests:** When secrets are present, CI runs `Get-AuthenticodeSignature` on our binaries and requires `Valid`, a timestamp, and the expected subject.
- **DoD:** With secrets, the three exes verify as signed and timestamped. Without secrets, the job is green and logs `signing: SKIPPED`.

### WS-10 · Auto-update feed — M (lead time)
- **Rows** A4 · **Scenarios** — · **Depends on** WS-08, WS-09 (to enable), WS-12
- **Work:**
  - The feed is GitHub Releases on the project-owned repo (`vpk upload github`, `GithubSource`). Its URL is `UpdateFeedUrl` in `appsettings.json`; empty means the updater is off (the default until WS-09). Moving the feed later is a config change plus one release.
  - Channels are `win-beta` and `win`. Check at startup and every 6 h, and download deltas in the background.
  - Apply updates only while `voiceState == idle`: via "Restart to update" in the panel, or `WaitExitThenApplyUpdates` on quit. Errors are logged and retried on the next cycle.
  - A `win-v*` tag runs pack → sign → upload. The Mac Sparkle feed is not touched.
- **Tests:** CI builds N and N+1, publishes N+1 to a local directory feed, installs N, and runs `--debug-update-now`.
- **DoD:** Installed N updates itself to N+1 with `settings.json` byte-identical. With `UpdateFeedUrl` empty, no update request is made.

### WS-11 · Crash reporting + file log — S (lead time: the beta needs it)
- **Rows** — · **Scenarios** — · **Depends on** —
- **Decision:** a local minidump plus a crash text file, with a user-initiated upload hook. **No crash SDK.**
  - The Sentry .NET SDK (MIT) would send stack traces, breadcrumbs and exception messages to a new processor, and our messages embed Worker response bodies (`API Error (status): body`). That is new privacy surface needing owner sign-off.
  - The local option adds none. It gives up aggregate telemetry, which is acceptable for a small beta.
- **Work:**
  - `ClickyLog` writes rolling files `%LOCALAPPDATA%\Clicky\logs\clicky-*.log` (5 × 1 MB). Spec evidence strings are unchanged, and the log records text lengths, never transcript or response text.
  - `AppDomain.UnhandledException` and `DispatcherUnhandledException` write `crashes\<utc>.txt` (version, OS build, exception, last 200 log lines) plus a CsWin32 `MiniDumpWriteDump`. Keep the newest 5.
  - "Report a problem" (WS-19) opens the crash folder and a prefilled GitHub issue.
  - Native crashes go to WER only (accepted).
- **Tests:** `--debug-crash` run with a temp `CLICKY_DATA_DIR` must exit non-zero and leave exactly one `.txt` and one `.dmp`, and no file may contain the fixture's response text.
- **DoD:** The test passes on `windows-build`, and the replay log shows the length of `spokenText` but not its content.

### WS-12 · Settings persistence — S (pulled forward: prerequisite of WS-03, 10, 16, 18–24)
- **Rows** A7, A9 · **Scenarios** ONB-04, CLA-08 (persistence), PNL-03 · **Depends on** —
- **Work:**
  - `appsettings.json` holds ship-time values: `WorkerBaseUrl`, `UpdateFeedUrl`, the PostHog host/key and the model list. `TracerPromptText` is removed once WS-07 lands.
  - **Decision (ONB-04):** user settings live in `%LOCALAPPDATA%\Clicky\settings.json`, with the keys `selectedClaudeModel`, `isClickyCursorEnabled`, `hasCompletedOnboarding`, `hasSubmittedEmail`, `pushToTalkShortcut` and `analyticsDistinctId`.
  - A missing or corrupt file means defaults, and the next change rewrites it atomically (temp file + `File.Replace`).
  - Defaults and merge logic live in Core. `CLICKY_DATA_DIR` overrides the root (tests). The version comes from the assembly (A7).
- **Tests:** Write `shared/golden/settings-defaults.json` (missing key, corrupt file, unknown key), plus a Replay test that a corrupt file becomes valid after one change.
- **DoD:** Both pass in CI.

### WS-13 · Microphone capture + permission detection — M (F4 spike)
- **Rows** F1, F2, F3, F4 · **Scenarios** STT-02 (audio side), PNL-04 (detection) · **Depends on** —
- **Work:**
  - Capture with NAudio `WasapiCapture` in shared mode, re-opened on default-device change (`IMMNotificationClient`).
  - Output PCM16 mono 16 kHz byte frames (seam 1, `PcmFrameSink`), with RMS computed on the float buffer (F3).
  - F4 spike: use `AppCapability.Create("microphone").CheckAccess()`; where that is unavailable, treat `E_ACCESSDENIED` from `IAudioClient::Initialize` as denied. Poll every 1.5 s for a re-grant.
- **Tests:** Write Core unit tests: a 48 kHz stereo float sine resamples to the expected 16 kHz frame count, and RMS of a known buffer matches. Replay uses a fake source, because the runner has no audio device. Manual-VM: PNL-04.
- **DoD:** The Core tests pass on Linux. On the VM, toggling the desktop-app mic privacy switch is detected within 1.5 s in both directions, and 5 s of audio yields 160 000 B ± 1%.

### WS-14 · AssemblyAI streaming transcription — L
- **Rows** G4, G8 · **Scenarios** STT-01–08 · **Depends on** WS-04, WS-07, WS-13
- **Work:**
  - Core holds the token parse and WS URL (STT-01), keyterm normalisation with the 11 base terms verbatim (STT-07), turn assembly (STT-03), and the finalize machine on `TimeProvider` (STT-04/05/06/08): 1.4 s grace, then 2.8 s and 2.4 s fallbacks, first one wins.
  - The shell uses `ClientWebSocket.ConnectAsync(uri, sharedInvoker, ct)` so all sessions share one handler (the AGENTS.md lesson). It closes with 1001 and serializes through a `Channel<T>` (G8).
  - Partial transcripts are never drawn.
- **Tests:**
  - Write the vectors `assemblyai-turns`, `transcript-finalize` and `transcription-keyterms`.
  - Write `parity/assemblyai-{session-basic,final-on-keyup,stall-after-keyup,error-midstream}.jsonl` and `transcribe-token-http-500.json`. Record them on the VM by extending `--record` to WebSocket frames.
  - Add one Replay test per STT scenario.
- **DoD:** All vectors and `.jsonl` replays pass in CI, and the stall replay delivers at 1.4 s ± 100 ms.

### WS-15 · Screen capture hardening — M
- **Rows** D1, D2, D5 · **Scenarios** CLA-01 · **Depends on** WS-01, WS-07
- **Work:**
  - Capture moves from key press to transcript submit (CLA-01).
  - On build 20348+, call `RequestAccessAsync(Borderless)` once and set `IsBorderRequired = false`. The older Win10 yellow border stays an accepted limitation.
  - The panel HWND gets the same exclusion or hide path as the overlay. Reuse the D3D device.
- **Tests:** `--save-captures` (ADR acceptance 7) exists. Write `shared/golden/capture-plan.json` (ordering, " — " labels, 1512×982 → 1280×831), plus a Replay assert on `requestBody` image sizes and labels.
- **DoD:** The vector passes on both Core jobs. ADR acceptance 7 passes on 1903 (overlay back within 250 ms) and on Win11 (no border).

### WS-16 · Claude client completion — S
- **Rows** G1, G2, G3, G5, G6 · **Scenarios** CLA-02, CLA-03, CLA-06, CLA-08, PNT-01 · **Depends on** WS-04, WS-07, WS-12
- **Work:**
  - Add history (plain-string turns, with images only in the last user turn) and PNG/JPEG sniffing.
  - Read the model from settings; an in-flight request keeps its model.
  - Add a TLS warm-up `HEAD` (G3), `CancelAfter` timeouts and one retry on connect failure (G2).
  - G1, G5 and G6 exist already and stay green.
- **Tests:** `point-tag.json` and `sse-stream.json` exist. Write `claude-request.json`, a `parity/chat-http-529.json` replay, and an Opus `--settings-override` replay.
- **DoD:** The new vector and both replays pass on `windows-build`.

### WS-17 · TTS playback + system-voice fallback — M
- **Rows** F5, F7 · **Scenarios** TTS-01, TTS-03, TTS-04, PTT-02 (replay half) · **Depends on** WS-04, WS-07
- **Work:**
  - Core builds the `/tts` body and maps errors (seam 5).
  - The shell downloads the whole MP3 (parity), then plays it via NAudio `Mp3FileReader` → `WasapiOut`. `PlaybackStopped` signals `TtsEnded`, and a re-press stops playback.
  - On any pipeline error, `System.Speech` (MIT) speaks the exact fallback string.
- **Tests:** Write `parity/tts-ok.json` (with a generated `tts-ok.mp3`) and `tts-http-402.json`. Replay asserts `ttsRequests`, `fallbackUtterances` and `voiceStateTimeline`.
- **DoD:** The replays pass in CI with the sink stubbed. On the VM, `tts stopped (preempted)` is logged ≤ 100 ms after `ptt pressed`.

### WS-18 · Tray icon + panel window (WinExe) — M
- **Rows** A1, A2, A10, B1–B6 · **Scenarios** PNL-01, PNL-02, PNL-05, PNL-07 · **Depends on** WS-07, WS-11, WS-12
- **Work:**
  - Switch to `WinExe`. A per-user named mutex enforces a single instance, so there is never a second hook.
  - The tray uses H.NotifyIcon.Wpf, with light and dark `.ico` files swapped on `ImmersiveColorSet` (B2).
  - The panel anchors via `Shell_NotifyIconGetRect` and the taskbar edge (B4), and activates when opened (B3, accepted).
  - The panel hides on `Deactivated`, on push-to-talk, or on ×, except while the mic is denied (B5). It uses a C# event (B6), and auto-opens 0.3 s after start when setup is incomplete.
  - Quit shuts down in order, exits 0 and removes the tray icon (A10).
- **Tests:** `--debug-quit-after-ms` exits 0 and logs `shutdown complete`. A second-instance test sees exactly one `hook installed`. Manual-VM: PNL-01/02/05/07 with the taskbar at the bottom and on the left.
- **DoD:** The CI tests pass, and the 4 PNL scenarios pass on the VM in both taskbar positions.

### WS-19 · Panel content — M
- **Rows** B7–B12 · **Scenarios** PNL-03, PNL-04, PNL-06 · **Depends on** WS-07, WS-13, WS-18
- **Work:**
  - Port to XAML:
    - Header status and model picker.
    - Mic-only permissions; "Grant" opens `ms-settings:privacy-microphone` (B12). The Mac's Screen Recording and Accessibility links are dropped.
    - Email field, Quit, "Watch Onboarding Again", "Report a problem" (WS-11), and the feedback link (B12).
    - A shortcut picker. This is a Windows addition, required by the ADR ("configurable from day one").
  - DS tokens become XAML resources (B11), and Fluent UI icons are bundled (B8).
  - `Cursor="Hand"` on every button (B9, AGENTS.md), and 0-delay tooltips (B10).
  - "Show Clicky" is not rendered (ONB-03 parity).
- **Tests:** Manual-VM: PNL-03/04/06 screenshots on Win10 and Win11, plus a Tab-order pass and a Narrator pass.
- **DoD:** Every control has an `AutomationProperties.Name` and a Hand cursor, and the PNL evidence is captured on both OSes.

### WS-20 · Installer experience + start-at-login — S
- **Rows** A3 · **Scenarios** — · **Depends on** WS-08, WS-18
- **Work:**
  - A per-user `Setup.exe` with no UAC prompt.
  - The Velopack after-install hook writes `HKCU\…\Run\Clicky` to the stable exe path (verify it across an update). The before-uninstall hook deletes it.
  - Install adds a Start-menu shortcut. The first launch auto-opens the panel (PNL-07).
- **Tests:** A silent install/uninstall on `windows-latest` asserts the Run value and shortcut are created without elevation and then removed. Manual-VM: clean 1903 and Win11.
- **DoD:** The CI test passes, and the installed app runs on a clean 1903 VM with no .NET preinstalled.

### WS-21 · Signed beta channel + SmartScreen plan — S
- **Rows** — · **Scenarios** — · **Depends on** WS-09, WS-10, WS-20, and a working voice loop (WS-14, WS-16, WS-17)
- **Work:**
  - A `win-v<semver>-beta.N` tag runs pack → sign → upload to `win-beta`. Unsigned output is refused.
  - Every release uses the same signing identity, and no EV certificate.
  - Testers are told to expect "Windows protected your PC" (More info → Run anyway) until reputation builds.
  - Each release records whether SmartScreen warned on a clean VM. Promote to `win` once the warnings stop, or when the owner accepts them.
- **Tests:** A tag build on the fork without secrets must fail at the unsigned gate.
- **DoD:** The first signed beta is live on `win-beta`, and the no-secrets tag build fails before upload.

### WS-22 · Onboarding — L (E8 spike)
- **Rows** E8, F6, A8 · **Scenarios** ONB-01, ONB-02, ONB-05 · **Depends on** WS-07, WS-12, WS-15, WS-17, WS-19
- **Work:**
  - E8 spike, tried in this order:
    1. WebView2 + hls.js (Apache-2.0). If the runtime is missing, bootstrap it through Velopack (verify that this works).
    2. A XAML Island `MediaPlayerElement`.
    3. The browser.

    The winner must provide the 40 s trigger, an end event and volume fades.
  - `ff.mp3` ships as a content file and plays at volume 0.3, fading over 3 s after 90 s (F6, A8).
  - The ONB-01 flow uses Windows copy, adds a "pin the tray icon" step, does the FormSpark POST + identify, and ignores PTT while the video plays.
  - `shared/prompts/onboarding-demo.txt` is copied verbatim from `:950-962` and pinned to the Swift literal (like `CompanionVoiceResponsePromptTests`) until WS-06 lands.
- **Tests:** Write `fixtures/sse/onboarding-demo.sse`. Replay with `--debug-trigger-onboarding-demo` asserts 1 image and `screenIndex == 0`. Manual-VM: recordings of ONB-01 and ONB-05.
- **DoD:** The ONB-02 replay passes in CI. On 1903 and on Win11 the video plays (or uses the spike's recorded fallback), and the demo fires at 40 s ± 1 s.

### WS-23 · Pointing flight + overlay animations — M
- **Rows** E3, E4, E5, E7 · **Scenarios** PNT-06, TTS-02 (fade) · **Depends on** WS-01, WS-07, WS-13
- **Work:**
  - Core `FlightPath.Sample(t, start, end)` (seam 9) ports `OverlayWindow.swift:495-568`: a bezier over 0.6–1.4 s, with rotation and scale.
  - The sequence is fly → bubble → 3 s hold → return. A mouse move of more than 100 pt (converted to px at the monitor's DPI) cancels the return.
  - Rendering:
    - `Path` + `DropShadowEffect` (E3).
    - A pre-rendered spinner arc under a `RotateTransform` (E4).
    - The RMS waveform on `CompositionTarget.Rendering` (E5).
    - A 0.4 s `Opacity` fade (E7).
- **Tests:** Write `shared/golden/flight-path.json`, authored from the Swift lines. Manual-VM: a ≥ 30 fps recording, re-checking the WS-01 CPU budget during flight.
- **DoD:** The vector passes on both Core jobs, and the recording on the mixed-DPI rig shows fly-out, the 3 s hold, and the return.

### WS-24 · Analytics — S
- **Rows** H1 · **Scenarios** AN-01, AN-02, AN-03 · **Depends on** WS-04, WS-07, WS-12
- **Work:**
  - The Core `AnalyticsEvents` catalogue (seam 8). `character_count` uses `StringInfo.LengthInTextElements`.
  - Transcript and response properties sit behind one switch (AN-03).
  - **Decision:** a direct `POST` to `us.i.posthog.com` on the shared `HttpClient`. With a single endpoint, the PostHog SDK isn't worth the dependency.
  - Sends are fire-and-forget, are dropped on failure, and never block. The distinct ID is `analyticsDistinctId` until identify.
- **Tests:** Write `shared/golden/analytics-events.json` (every event, plus `character-count-emoji`, `transcript-included` and `response-included`), and a full-exchange Replay that asserts event order.
- **DoD:** The vector passes on both Core jobs, and the replay emits exactly the AN-01 order with no request to posthog.com.

## Dependency shape

- **Critical path:** WS-04 → 07 → 14 → 21 (first signed beta). WS-08 → 09 → 10 runs in parallel. The longest external lead is the owner's signing identity (WS-09).
- **Parallel from day one:** WS-01, 03, 04, 05, 08, 11, 12, 13.
- **Mac-gated:** WS-05 and WS-06 block nothing on Windows, only the phase-8 parity gate.
- **Phase 7 QA still owes** ADR acceptance 3–11 on the interactive rig. WS-01, 03, 15 and 23 restate those checks in their DoD.

## Pre-release decisions (product owner; not engineering items until decided)

1. **Worker client authentication:** `/chat`, `/tts` and `/transcribe-token` have no client auth. Decide before a public Windows release.
2. **PostHog transcript content:** `user_message_sent` and `ai_response_received` carry full text (H1). WS-24 makes removing it a one-line switch.

## Coverage checklist

| Group | Surviving §1a row → owning WS |
|---|---|
| A | A1→18 · A2→18 · A3→20 · A4→10 · A5→09 · A6→08 · A7→12 · A8→22 · A9→12 · A10→18 |
| B | B1→18 · B2→18 · B3→18 · B4→18 · B5→18 · B6→18 · B7→19 · B8→19 · B9→19 · B10→19 · B11→19 · B12→19 (mic deep link + external URL only) |
| C | C1→03 · C2→03 · C3→03 |
| D | D1→15 · D2→15 · D5→15 · D6→01 · D7→01 |
| E | E1→01 · E2→02 · E3→23 · E4→23 · E5→23 · E6→01 · E7→23 · E8→22 |
| F | F1→13 · F2→13 · F3→13 · F4→13 · F5→17 · F6→22 · F7→17 |
| G | G1→16 · G2→16 · G3→16 · G4→14 · G5→16 · G6→16 · G7→07 · G8→14 · G9→07 · G10→07 |
| H | H1→24 |

- **Rows:** 56 surviving rows, one owner each.
- **Not ported:**
  - Dropped per the ADR: B13, C4, D3, D4, F8, F9, and B12's macOS-only deep links.
  - Unreachable code: I1–I5.
- **Scenarios:** all 49 spec scenarios have an owner:
  - PTT-01, 04–08 → 03; PTT-02, 03 → 07.
  - STT-* → 14; STT-02 audio → 13.
  - CLA-01 → 15; CLA-02, 03, 06, 08 → 16; CLA-04, 05 → 01; CLA-07 → 07.
  - PNT-01 → 16; PNT-02–05 → 01; PNT-06 → 23.
  - TTS-01, 03, 04 → 17; TTS-02 → 07 and 23.
  - PNL-01, 02, 05, 07 → 18; PNL-03, 04, 06 → 19.
  - ONB-01, 02, 05 → 22; ONB-03 → 07; ONB-04 → 12.
  - AN-* → 24.
