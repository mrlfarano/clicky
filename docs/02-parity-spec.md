# 02 — Parity spec: Clicky macOS → Windows behavioural contract

Phase 3a deliverable. Author: Senior Software Engineer. Date: 2026-10-22.
Basis: [ADR 001](adr/001-windows-strategy.md) (approved), [portability audit](01-portability-audit.md) (rows such as C1, seams 1–10 from §1b), [AGENTS.md](../AGENTS.md), and the Swift sources in `leanring-buddy/`.

## How to read this document

**Adaptation: scenarios are derived from code, not from the running app.** The playbook wants parity scenarios "derived from the RUNNING macOS app". This environment has no macOS host ([STATUS](STATUS.md) blocker 1), so every scenario below is derived from the Swift source and AGENTS.md, and every Swift claim links to the exact lines it rests on. Each scenario has a **Capture status** (in the [traceability table](#traceability)):

| Capture status | Meaning |
|---|---|
| `code-derived` | Behaviour read from the cited Swift lines. Not yet observed on a running Mac. |
| `adr-derived` | Windows-only behaviour with no Swift equivalent. Its source is ADR 001 or the audit, not the Mac app. On the Mac, sign-off means confirming that the Mac behaves as the "Mac reference" line says (which is usually *different*). |
| `mac-verified` | Set during the phase-8 parity session, once someone has run the scenario on a real Mac and ticked **Mac sign-off**. |

The **Mac sign-off** checkbox is ticked only during the phase-8 parity session on a real Mac. Until then, every scenario is unverified against the running app. This is the residual risk that ADR 001 "Divergences from audit" puts on the phase-8 gate.

**Where the tests live: `tests/parity/` maps to `shared/`.** The playbook's `tests/parity/` concept maps onto the approved ADR layout as follows:

| Playbook concept | Location in this repo | Contents |
|---|---|---|
| parity vectors (pure logic) | `shared/golden/*.json` | Input → expected-output cases. Run by `Clicky.Core.Tests`; `point-tag` and `push-to-talk` are also run by XCTest when a Mac is available (ADR tracer item 7). |
| replayable Worker `/chat` streams | `shared/fixtures/sse/*.sse` | Raw SSE bodies. This is the ADR's name for the folder, kept as is. |
| other replayable recordings | `shared/fixtures/parity/` | AssemblyAI websocket transcripts (`*.jsonl`), non-2xx and `/tts` responses (`*.json`). Added by this spec; not in the ADR layout, because the tracer bullet does not need them. |

**Harness names used below**

| Harness | What runs it | CI job |
|---|---|---|
| **Core** | `windows/tests/Clicky.Core.Tests`: loads the named `shared/golden` file and asserts every case | `linux-core-tests`, `windows-build` |
| **Replay** | `windows/tests/Clicky.Windows.Tests`: launches `Clicky.exe --replay <fixture> [--event-delay-ms N]` with no keys and no network, then asserts on the app's JSON replay report (ADR tracer item 4, acceptance 2) | `windows-build` |
| **XCTest** | One XCTest file on a Mac, run against `parsePointingCoordinates` and `shortcutTransition(for:keyCode:modifierFlagsRawValue:…)` only (ADR tracer item 7) | none yet (needs a Mac; see `ci.yml`) |
| **Manual-VM** | A person following the Given/When steps on the ADR acceptance rig: Win10 1903 VM plus Win10 22H2 or Win11, two monitors at 100% and 150%, secondary monitor *left* of primary. The scenario names the **evidence** QA must attach. | none |

**Lands** says when the fixture and test have to exist: `3c` means the tracer bullet (ADR "Tracer bullet"), `6` means the development phase. Scenarios that land in 6 are the contract the 03-plan tasks are written against.

### Golden vector file format (validated by CI)

Every `shared/golden/*.json` file has this shape. The `macos-parity` job in `.github/workflows/ci.yml` checks it.

```json
{
  "schema": 1,
  "seam": "point-tag",
  "swiftAnchor": "leanring-buddy/CompanionManager.swift:784-823",
  "cases": [
    {
      "id": "full-tag-with-screen",
      "branch": "coordinate+label+screen",
      "input": { "text": "over there [POINT:400,300:terminal:screen2]" },
      "expected": { "spokenText": "over there", "x": 400, "y": 300, "label": "terminal", "screen": 2 }
    }
  ]
}
```

Rules:
- `schema` is `1`.
- `seam` and `swiftAnchor` are non-empty strings.
- `cases` is a non-empty array.
- Each case has a unique string `id` and has both `input` and `expected`.
- `branch` is optional. When present, it names the code branch the case covers. ADR acceptance 1 requires every branch to be covered.

### Replay report fields this spec relies on

`Clicky.exe --replay` writes a JSON report (ADR acceptance 2). The scenarios below assert on these fields. Fields marked 3c must exist in the tracer bullet; the others are added in phase 6.

| Field | Lands | Meaning |
|---|---|---|
| `exitCode` | 3c | Process exit code |
| `spokenText` | 3c | Final text with the POINT tag removed |
| `displayTimeline[] {tMs, text}` | 3c | Bubble text each time it changes, with the ms since the first SSE event was delivered |
| `point {screenIndex, monitorDevice, targetPx {x,y}, hotspotPx {x,y}}` | 3c | Parsed target plus where the overlay hotspot ended up (`null` when nothing was pointed at) |
| `requestBody` | 3c | Outgoing `/chat` JSON, with each image's base64 replaced by `{sha256, width, height, bytes}` |
| `captureTimeline[]` | 6 | Virtual-clock tMs of every screen capture, one per submitted transcript (CLA-01) |
| `voiceStateTimeline[] {tMs, state}` | 6 | `idle/listening/processing/responding` changes |
| `analyticsEvents[] {name, properties}` | 6 | Events sent to the analytics sink (stubbed in replay, so nothing reaches PostHog) |
| `ttsRequests[]`, `fallbackUtterances[]` | 6 | `/tts` bodies sent, and system-voice fallback strings spoken |
| `conversationHistory[] {userTranscript, assistantResponse}` | 6 | The reducer's history when the report was written (CLA-06) |
| `httpRequests[] {method, path}` | 6 | Every request the replay handler received, answered or not: the TLS warm-up `HEAD` and each `/chat` attempt (G2, G3) |
| `webSocketFrames[] {session, tMs, dir, frame}` | 6 | Every AssemblyAI websocket frame of a replay with a recorded socket, in the recording's line format (section 2) |

---

## 1. Push-to-talk shortcut (PTT)

Swift detector: `BuddyPushToTalkShortcut` ([BuddyDictationManager.swift:16-200](../leanring-buddy/BuddyDictationManager.swift#L16-L200)). Event source: the CGEvent tap ([GlobalPushToTalkShortcutMonitor.swift:30-131](../leanring-buddy/GlobalPushToTalkShortcutMonitor.swift#L30-L131)). Consumer: [CompanionManager.swift:473-540](../leanring-buddy/CompanionManager.swift#L473-L540). Windows: a `WH_KEYBOARD_LL` hook feeds `Clicky.Core.PushToTalkDetector` (seam 7).

**PTT-01 · Press starts recording; release finalizes**
- **Given** the app is onboarded, the microphone is granted, no dictation is in progress, and the onboarding video is not playing.
- **When** the user presses the shortcut (default left Ctrl + left Alt), holds it, then releases it.
- **Then**:
  - Exactly one `pressed` transition fires on the press and one `released` on the release ([BuddyDictationManager.swift:163-177](../leanring-buddy/BuddyDictationManager.swift#L163-L177)).
  - On press: the panel is dismissed, `push_to_talk_started` fires, and recording starts. `voiceState` goes `processing` (preparing) → `listening` ([CompanionManager.swift:491-527](../leanring-buddy/CompanionManager.swift#L491-L527), [:430-461](../leanring-buddy/CompanionManager.swift#L430-L461)).
  - On release: `push_to_talk_released` fires and finalization begins (`voiceState = processing`) ([CompanionManager.swift:528-536](../leanring-buddy/CompanionManager.swift#L528-L536), [BuddyDictationManager.swift:476-512](../leanring-buddy/BuddyDictationManager.swift#L476-L512)).
  - Holding the keys never produces a second `pressed`, because auto-repeat is guarded by `wasShortcutPreviouslyPressed`.
- **Fixture & harness**:
  - `shared/golden/push-to-talk.json` (Lands 3c), cases `ctrl-alt-press`, `ctrl-alt-release`, `ctrl-alt-repeat-no-retrigger`. Run by Core, and by XCTest on a Mac.
  - Manual-VM (ADR acceptance 4), on US and DE layouts. **Evidence:** the app log lines `ptt pressed` / `ptt released`, one pair per press, for 10 presses.

**PTT-02 · Re-press while the reply is in flight cancels it; re-press while finalizing is ignored**
- **Given** (a) a transcript has been submitted and the Claude request or TTS playback is in flight; or (b) the shortcut was just released and the transcript is still finalizing.
- **When** the user presses the shortcut again.
- **Then**:
  - (a) The in-flight response task is cancelled, TTS stops, the pointing target is cleared, and a new recording starts ([CompanionManager.swift:494-497](../leanring-buddy/CompanionManager.swift#L494-L497)). The cancelled response does *not* add to history, emit `ai_response_received`, or speak ([:598](../leanring-buddy/CompanionManager.swift#L598), [:623](../leanring-buddy/CompanionManager.swift#L623), [:713-714](../leanring-buddy/CompanionManager.swift#L713-L714)).
  - (b) The press is **ignored**. Dictation is still in progress, so `isDictationInProgress` blocks it before `push_to_talk_started` fires ([CompanionManager.swift:476](../leanring-buddy/CompanionManager.swift#L476), [BuddyDictationManager.swift:239-241](../leanring-buddy/BuddyDictationManager.swift#L239-L241)). The release that follows still emits `push_to_talk_released` ([CompanionManager.swift:533](../leanring-buddy/CompanionManager.swift#L533)). See AN-02.
  - Both (a) and (b) show `voiceState = processing`, so the spinner alone does not tell you which rule applies.
- **Fixture & harness**:
  - `shared/golden/session-reducer.json` (Lands 6), cases `repress-during-response-cancels`, `repress-during-finalize-ignored`. Run by Core.
  - Replay with `shared/fixtures/sse/point-basic.sse --event-delay-ms 200` and a scripted second press at 300 ms. Assert that `spokenText` is empty, `ttsRequests` is empty, and the `analyticsEvents` order is correct.

**PTT-03 · A quick tap does not leave the overlay stuck**
- **Given** an idle app.
- **When** the user presses and releases within one frame, before the async start has begun recording.
- **Then**:
  - The pending start task is cancelled and dictation resets. `voiceState` returns to `idle`, and in transient mode the transient hide is scheduled ([CompanionManager.swift:528-536](../leanring-buddy/CompanionManager.swift#L528-L536), [BuddyDictationManager.swift:412-416](../leanring-buddy/BuddyDictationManager.swift#L412-L416), [:444-448](../leanring-buddy/BuddyDictationManager.swift#L444-L448), [CompanionManager.swift:449-459](../leanring-buddy/CompanionManager.swift#L449-L459)).
  - No `/chat` request is made.
- **Fixture & harness**: `shared/golden/session-reducer.json` case `tap-without-speech` (Lands 6), run by Core. Manual-VM **evidence:** the log shows `ptt pressed`, `ptt released` and `dictation reset`, followed by no `chat request` line, and a screenshot 2 s later shows no waveform.

**PTT-04 · Shortcut variants map to Windows keys**
- **Given** the shortcut option is each of `controlOption` (default), `shiftControl`, `controlOptionSpace` or `shiftControlSpace`. `shiftFunction` is dropped on Windows (audit C3, ADR).
- **When** the hook delivers the key sequences below.
- **Then** the detector returns the same transitions as Swift:

| Option | Swift rule | Windows keys |
|---|---|---|
| `controlOption` | Modifier-only. `pressed` when the flags *contain* Ctrl+Option, `released` when either is lifted. Extra modifiers such as Shift do not block it ([:54-65](../leanring-buddy/BuddyDictationManager.swift#L54-L65), [:163-177](../leanring-buddy/BuddyDictationManager.swift#L163-L177)) | `VK_LCONTROL` + `VK_LMENU`, with left keys only (ADR "Shortcut") |
| `shiftControl` | Same rule with Shift+Ctrl | Shift + `VK_LCONTROL`. Whether right Shift counts is decided in 03-plan |
| `controlOptionSpace` | `pressed` on Space (keycode 49) keyDown when the flags are a *superset* of Ctrl+Option. `released` on Space keyUp **whatever the modifiers are**, so lifting Ctrl/Option first does not release ([:67-80](../leanring-buddy/BuddyDictationManager.swift#L67-L80), [:179-198](../leanring-buddy/BuddyDictationManager.swift#L179-L198), [:96](../leanring-buddy/BuddyDictationManager.swift#L96)) | `VK_SPACE` + `VK_LCONTROL` + `VK_LMENU` |
| `shiftControlSpace` | Same rule with Shift+Ctrl | `VK_SPACE` + Shift + `VK_LCONTROL` |

  The copy uses Windows key names: "Ctrl + Alt", "Shift + Ctrl", "Ctrl + Alt + Space", "Shift + Ctrl + Space", replacing [:24-52](../leanring-buddy/BuddyDictationManager.swift#L24-L52).
- **Fixture & harness**: `shared/golden/push-to-talk.json` (Lands 3c for `controlOption`; the other options land in 6), one case group per option. Cases are written in the platform-neutral `{Ctrl, Alt, Shift, Fn} + keyCode` form (seam 7). Run by Core, and by XCTest on a Mac (the XCTest maps neutral modifiers to `CGEventFlags`).

**PTT-05 · AltGr on the DE layout must not trigger** — `adr-derived`
- **Given** the German (DE) layout and the default shortcut.
- **When** the user types `@` (AltGr+Q), `€` (AltGr+E) and `{` (AltGr+7) 20 times each in Notepad. AltGr generates a synthetic LCtrl (scan code `0x21D`) plus `VK_RMENU`.
- **Then**:
  - Zero `pressed` transitions occur, and all 60 characters appear, which proves the hook only listens.
  - Right Ctrl + right Alt, and left Ctrl + right Alt, also do not activate (ADR acceptance 3–4, audit C1).
- **Mac reference**: macOS has no AltGr. Option is a plain modifier there, so there is nothing to sign off beyond "not applicable".
- **Fixture & harness**:
  - `shared/golden/push-to-talk.json` (Lands 3c), cases `altgr-q-no-trigger`, `altgr-with-left-ctrl-held`, `rctrl-ralt-no-trigger`, `lctrl-ralt-no-trigger`. Run by Core. These cases are Windows-only, so the XCTest must skip any case tagged `"platform": "windows"`.
  - Manual-VM ADR acceptance 3. **Evidence:** a Notepad screenshot showing the 60 characters, plus a log excerpt showing 0 `ptt pressed` lines.

**PTT-06 · Hook death is recovered by the watchdog** — `adr-derived`
- **Given** the hook is installed.
- **When** `--debug-stall-hook-once` makes one callback sleep 2× `LowLevelHooksTimeout`, so Windows silently removes the hook.
- **Then** the Raw Input watchdog sees a key-down that the hook missed within 500 ms, reinstalls the hook, and logs it. The shortcut activates again within the next two presses (ADR acceptance 6, audit C2).
- **Mac reference**: the tap is re-enabled on `.tapDisabledByTimeout` / `.tapDisabledByUserInput` ([GlobalPushToTalkShortcutMonitor.swift:104-109](../leanring-buddy/GlobalPushToTalkShortcutMonitor.swift#L104-L109)).
- **Fixture & harness**: Manual-VM (Lands 3c). **Evidence:** the log lines `hook stalled (debug)`, `watchdog: hook reinstalled`, `ptt pressed`, in that order.

**PTT-07 · The shortcut survives idle time** — `adr-derived`
- **Given** the app has run for 10 minutes with no input.
- **When** the user presses the shortcut.
- **Then** it activates on the first press (ADR acceptance 5).
- **Fixture & harness**: Manual-VM (Lands 3c). **Evidence:** the timestamped log shows `ptt pressed` ≥ 600 s after the previous input event.

**PTT-08 · Elevated foreground window (accepted limitation)** — `adr-derived`
- **Given** an elevated window, for example an admin terminal, has focus.
- **When** the user presses the shortcut.
- **Then** nothing activates and nothing crashes. Once a non-elevated window has focus, the next press works (UIPI, audit C1, ADR "Accepted Windows limitations").
- **Fixture & harness**: Manual-VM (Lands 6). **Evidence:** the log has no `ptt pressed` while the admin terminal is focused, and does have one after switching back.

---

## 2. Transcription: AssemblyAI streaming (STT)

Swift: [AssemblyAIStreamingTranscriptionProvider.swift](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift) (seam 1 and the turn assembly), and the session lifecycle in [BuddyDictationManager.swift:377-650](../leanring-buddy/BuddyDictationManager.swift#L377-L650). All STT scenarios land in phase 6 (the tracer bullet has no microphone; ADR "OUT").

AssemblyAI recordings use `shared/fixtures/stt/assemblyai-*.jsonl` (JSONL has no comments, so the format lives here). Each line is `{"tMs": <offset>, "dir": "in"|"out", "frame": <frame>}`, with `tMs` in milliseconds since the session's connect. A `frame` is an AssemblyAI JSON message, or one of three strings: the websocket URL (always the first `out` line), `"<binary N bytes>"` (one PCM16 audio message) or `"<close 1001>"` (the client's going-away close). A `--replay-manifest` stages a recording with `"assemblyaiSocket": {"framesFile": …}`. Replay then runs the real session over a fake transport (`ReplayWebSocketConnector`) and the real microphone pipeline over a fake 16 kHz source. Every session plays the `in` frames at their `tMs`. Nothing is delivered after the client's close, and once the last frame is sent the server stalls until the client closes. The report's `webSocketFrames` holds both directions in the same line format, plus a `session` number. The tests compare its `out` frames with the recording: binary sizes follow the capture timing, so a run of binary frames counts as one entry. The `.jsonl` files were written from the Swift protocol and AssemblyAI v3 message shapes. Re-recording them on the VM (`--record` extended to websocket frames) is still open.

**STT-01 · Session open: token, URL, Begin**
- **Given** push-to-talk was pressed.
- **When** the provider starts a session.
- **Then**:
  - `POST {WorkerBaseUrl}/transcribe-token` is sent. A non-2xx status, or a body without `token`, is an error ([AssemblyAIStreamingTranscriptionProvider.swift:61-84](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L61-L84)).
  - The websocket URL is `wss://streaming.assemblyai.com/v3/ws` with query `sample_rate=16000`, `encoding=pcm_s16le`, `format_turns=true`, `speech_model=u3-rt-pro`, `keyterms_prompt=<JSON array>` (only if any keyterms are non-empty) and `token=…` ([:437-477](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L437-L477)).
  - The session counts as ready on `Begin` or `Termination` ([:249-255](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L249-L255)).
  - Audio is sent as binary PCM16 mono 16 kHz frames ([:182-196](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L182-L196)).
- **Fixture & harness**: `shared/golden/transcription-keyterms.json` (URL-construction cases), run by Core. Replay with `shared/fixtures/stt/assemblyai-session-basic.jsonl`, asserting the first `out` frame URL and the binary frames.

**STT-02 · Partial transcripts stream internally but are not shown**
- **Given** a recording is in progress.
- **When** `Turn` messages arrive with `end_of_turn=false`.
- **Then**:
  - The composed transcript updates on every message ([:273-300](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L273-L300), [BuddyDictationManager.swift:522-526](../leanring-buddy/BuddyDictationManager.swift#L522-L526)).
  - **No partial text is drawn.** The overlay shows only the waveform while `listening` ([CompanionManager.swift:517-519](../leanring-buddy/CompanionManager.swift#L517-L519), [OverlayWindow.swift:327](../leanring-buddy/OverlayWindow.swift#L327)).
- **Fixture & harness**: Replay with `assemblyai-session-basic.jsonl`. Assert that `displayTimeline` holds no transcript text while `voiceState == listening`.

**STT-03 · Turn assembly**
- **Given** `Turn` messages arrive with mixed `turn_order`, `end_of_turn` and `turn_is_formatted`.
- **When** they are composed.
- **Then**:
  - Committed turns are joined by spaces in ascending `turn_order`, and the trimmed active turn is appended.
  - A formatted turn is never overwritten by an unformatted one for the same order. Empty turns are not stored.
  - A missing `turn_order` falls back to the active order, otherwise to max+1 ([:273-347](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L273-L347)).
- **Fixture & harness**: `shared/golden/assemblyai-turns.json` (the audit §1b names this vector), run by Core.

**STT-04 · Final transcript on key-up**
- **Given** a recording with at least one partial.
- **When** the user releases the shortcut.
- **Then**:
  - Audio stops, `{"type":"ForceEndpoint"}` is sent and the 1.4 s grace deadline is armed ([:198-206](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L198-L206)).
  - The first `Turn` with `end_of_turn` or `turn_is_formatted` that arrives while waiting delivers the best available transcript immediately ([:302-308](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L302-L308)).
  - Delivery happens once and sends `{"type":"Terminate"}` ([:367-374](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L367-L374)).
  - The manager submits the trimmed transcript to the Claude pipeline ([BuddyDictationManager.swift:581-606](../leanring-buddy/BuddyDictationManager.swift#L581-L606), [CompanionManager.swift:520-525](../leanring-buddy/CompanionManager.swift#L520-L525)).
- **Fixture & harness**: `shared/golden/transcript-finalize.json` (a virtual-clock event timeline), case `end-of-turn-before-grace`, run by Core. Replay with `assemblyai-final-on-keyup.jsonl`.

**STT-05 · Finalize fallback timings**

Three timers apply:

| Timer | Value | Swift | Starts | On expiry |
|---|---|---|---|---|
| Provider grace | **1.4 s** | `explicitFinalTranscriptGracePeriodSeconds` [AssemblyAIStreamingTranscriptionProvider.swift:113](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L113), scheduled at [:349-365](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L349-L365) | `requestFinalTranscript` on key-up | Delivers the best available transcript (possibly empty) ([:352-356](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L352-L356), [:410-419](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L410-L419)) |
| Session fallback | **2.8 s** | `finalTranscriptFallbackDelaySeconds` [AssemblyAIStreamingTranscriptionProvider.swift:115](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L115), read at [BuddyDictationManager.swift:491-492](../leanring-buddy/BuddyDictationManager.swift#L491-L492) | `stopPushToTalk` | The manager finishes with `latestRecognizedText` ([BuddyDictationManager.swift:498-511](../leanring-buddy/BuddyDictationManager.swift#L498-L511)) |
| Default fallback | **2.4 s** | `defaultFinalTranscriptFallbackDelaySeconds` [BuddyDictationManager.swift:219](../leanring-buddy/BuddyDictationManager.swift#L219) | `stopPushToTalk` when **no session exists yet** (released while the token fetch or websocket open is still running) | Same as above |

- **Given / When / Then** (one case each):
  - (a) No end-of-turn arrives after key-up → the transcript is delivered at 1.4 s ± 100 ms.
  - (b) The provider never calls back (stalled transport) → the manager finishes at 2.8 s ± 100 ms using the last partial.
  - (c) The user releases before the session opens → the manager finishes at 2.4 s with empty text. Nothing is submitted ([BuddyDictationManager.swift:603](../leanring-buddy/BuddyDictationManager.swift#L603)), and the cancelled start task tears the half-open session down ([:452-459](../leanring-buddy/BuddyDictationManager.swift#L452-L459)).
  - (d) Whichever path fires first wins. Later ones are no-ops (`hasDeliveredFinalTranscript` / `hasFinishedCurrentDictationSession`, [AssemblyAIStreamingTranscriptionProvider.swift:368](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L368), [BuddyDictationManager.swift:582](../leanring-buddy/BuddyDictationManager.swift#L582)).
- **Fixture & harness**: `shared/golden/transcript-finalize.json`, cases `grace-1400ms`, `session-fallback-2800ms`, `default-fallback-2400ms`, `first-wins`. Run by Core with a virtual clock. The ±100 ms wall-clock tolerance applies only to Replay with `assemblyai-stall-after-keyup.jsonl`.
- **Note**: these timings are only partly vector-testable (ADR "Negative: two cores"). Mac sign-off has to observe them on hardware.

**STT-06 · Termination while awaiting final**
- **Given** the app is waiting for the final transcript.
- **When** a `Termination` message arrives before any end-of-turn.
- **Then** the best available transcript is delivered ([:254-260](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L254-L260)).
- **Fixture & harness**: `transcript-finalize.json` case `termination-delivers`, run by Core.

**STT-07 · Keyterm contextual injection**
- **Given** the base keyterms ([BuddyDictationManager.swift:653-665](../leanring-buddy/BuddyDictationManager.swift#L653-L665)) plus any contextual keyterms.
- **When** a session starts.
- **Then**:
  - Keyterms are trimmed, empty ones are dropped, and they are de-duplicated case-insensitively. Order and first-seen casing are kept ([:667-684](../leanring-buddy/BuddyDictationManager.swift#L667-L684)).
  - The result is sent as the `keyterms_prompt` JSON array (STT-01).
- **Note**: `updateContextualKeyterms` ([:290-292](../leanring-buddy/BuddyDictationManager.swift#L290-L292)) has **no callers** in the app, so in the shipped config only the 11 base terms are sent. Windows matches that: the injection path exists but nothing feeds it in v1. The list includes legacy terms ("makesomething", "Learning Buddy"); they are kept verbatim for parity.
- **Fixture & harness**: `shared/golden/transcription-keyterms.json`, cases `base-only`, `contextual-dedupe-case-insensitive`, `blank-dropped`. Run by Core.

**STT-08 · Provider error → no transcript**
- **Given / When / Then**:
  - (a) Token fetch fails (non-2xx, or a body without `token`) → session start throws, dictation resets, no `/chat` request is made, and `voiceState` returns to `idle` ([BuddyDictationManager.swift:465-473](../leanring-buddy/BuddyDictationManager.swift#L465-L473)).
  - (b) A websocket error or AssemblyAI `{"type":"Error"}` arrives during recording with no text → `onError` fires, dictation is cancelled without submitting, and no `/chat` request is made ([AssemblyAIStreamingTranscriptionProvider.swift:261-264](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L261-L264), [:392-408](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L392-L408), [BuddyDictationManager.swift:562-579](../leanring-buddy/BuddyDictationManager.swift#L562-L579)).
  - (c) An error arrives during finalization when a partial exists → the partial is delivered as the final transcript and **is** submitted ([AssemblyAIStreamingTranscriptionProvider.swift:397-402](../leanring-buddy/AssemblyAIStreamingTranscriptionProvider.swift#L397-L402), [BuddyDictationManager.swift:567-570](../leanring-buddy/BuddyDictationManager.swift#L567-L570)).
  - (d) The final transcript is empty or whitespace → nothing is submitted ([BuddyDictationManager.swift:603](../leanring-buddy/BuddyDictationManager.swift#L603)).
  - In every case **no error UI is shown**. `lastErrorMessage` is set, but nothing outside `BuddyDictationManager` reads it. Windows matches this and logs the error instead.
- **Fixture & harness**:
  - `transcript-finalize.json`, cases `error-no-text-cancels`, `error-with-partial-submits`, `empty-final-not-submitted`. Run by Core.
  - Replay with `shared/fixtures/parity/transcribe-token-http-500.json` and `assemblyai-error-midstream.jsonl`. Assert there is no `requestBody` and the last state in `voiceStateTimeline` is `idle`.

---

## 3. Claude round-trip (CLA)

Swift: [ClaudeAPI.swift:101-212](../leanring-buddy/ClaudeAPI.swift#L101-L212), [CompanionManager.swift:586-726](../leanring-buddy/CompanionManager.swift#L586-L726), [CompanionScreenCaptureUtility.swift:30-131](../leanring-buddy/CompanionScreenCaptureUtility.swift#L30-L131).

**CLA-01 · Screenshot set: per monitor, cursor first, labelled, ≤1280 long edge, JPEG q0.8**
- **Given** N monitors, with the cursor on one of them.
- **When** a transcript is submitted, never at the shortcut press ([CompanionManager.swift:520-524](../leanring-buddy/CompanionManager.swift#L520-L524), [:596](../leanring-buddy/CompanionManager.swift#L596)).
- **Then**:
  - One JPEG per monitor, with the **cursor's monitor first** ([CompanionScreenCaptureUtility.swift:60-68](../leanring-buddy/CompanionScreenCaptureUtility.swift#L60-L68)).
  - The long edge is 1280, and the short edge is `Int(1280 / aspect)`, **truncated**. For example 1512×982 → 1280×831 ([:83-92](../leanring-buddy/CompanionScreenCaptureUtility.swift#L83-L92)).
  - JPEG compression is 0.8 ([:99-100](../leanring-buddy/CompanionScreenCaptureUtility.swift#L99-L100)).
  - Labels ([:104-111](../leanring-buddy/CompanionScreenCaptureUtility.swift#L104-L111)) are `user's screen (cursor is here)` for a single monitor, otherwise `screen K of N — cursor is on this screen (primary focus)` or `screen K of N — secondary screen`. Each label is suffixed with ` (image dimensions: WxH pixels)` ([CompanionManager.swift:603-606](../leanring-buddy/CompanionManager.swift#L603-L606)).
  - The app's own windows are excluded ([CompanionScreenCaptureUtility.swift:40-45](../leanring-buddy/CompanionScreenCaptureUtility.swift#L40-L45)). On Windows that means `WDA_EXCLUDEFROMCAPTURE` on 19041+, or hiding the window for the capture frame on 1903 (audit D2).
- **Fixture & harness**:
  - `shared/golden/capture-plan.json` (Lands 6): monitors plus cursor → order, labels and sizes. Run by Core.
  - Manual-VM ADR acceptance 7 (Lands 3c). **Evidence:** the saved frame PNGs per monitor with their sizes, the non-black pixel ratio printed in the log, and on 1903 a log line showing the overlay hidden→shown in under 250 ms.
  - Replay asserts `requestBody` image `{width,height}` and the label strings, and that `captureTimeline` has one entry per submitted transcript and none at a press (`ReplayEndToEndTests.CaptureHappensAtSubmit`).

**CLA-02 · Request body**
- **Given** a transcript T, history H, model M, and the labelled images from CLA-01.
- **When** `/chat` is called.
- **Then**:
  - The body is `{"model": M, "max_tokens": 1024, "stream": true, "system": <companion-voice-response prompt>, "messages": [...]}`.
  - `messages` is H as alternating `user` / `assistant` **plain-string** contents (no images in history), followed by one `user` message whose content blocks are `[image, text(label)]` per monitor, then `text(T)` ([ClaudeAPI.swift:112-148](../leanring-buddy/ClaudeAPI.swift#L112-L148)).
  - The image `media_type` is sniffed from the bytes: `image/png` when the data starts with `89 50 4E 47`, otherwise `image/jpeg` ([:51-62](../leanring-buddy/ClaudeAPI.swift#L51-L62)).
  - The system prompt is loaded from `shared/prompts/companion-voice-response.txt`. It is moved verbatim from [CompanionManager.swift:544-577](../leanring-buddy/CompanionManager.swift#L544-L577) (seam 10). Its Mac-specific wording ("menu bar", "command option c") is kept unless the product owner changes it.
- **Fixture & harness**:
  - `shared/golden/claude-request.json` (Lands 6; image bytes stubbed), run by Core.
  - Replay (Lands 3c): with `TracerPromptText` as T and empty H, `requestBody` matches the expected structure.

**CLA-03 · SSE parsing**
- **Given** a `/chat` 200 response.
- **When** lines stream in.
- **Then**:
  - Only lines starting with `data: ` are considered.
  - `data: [DONE]` ends the stream.
  - Unparseable JSON is skipped.
  - Only `content_block_delta` events with `delta.type == "text_delta"` append text ([ClaudeAPI.swift:180-208](../leanring-buddy/ClaudeAPI.swift#L180-L208)).
- **Fixture & harness**: `shared/golden/sse-stream.json` (Lands 3c), cases `basic-deltas`, `done-marker`, `malformed-json-skipped`, `non-text-delta-ignored`, `ping-and-message-events-ignored`. Run by Core.

**CLA-04 · First token and later deltas render incrementally** — `adr-derived`
- **Given** a replayed stream with `--event-delay-ms 200`.
- **When** the first `text_delta` is delivered.
- **Then** its text is visible in the overlay bubble within 300 ms of delivery and before the final event arrives. Each later delta extends the bubble (ADR tracer item 6, acceptance 9).
- **Mac reference — this differs:** the Mac shows **no streaming text**. `onTextChunk` is a no-op, and the spinner stays until TTS starts ([CompanionManager.swift:591-592](../leanring-buddy/CompanionManager.swift#L591-L592), [:618-620](../leanring-buddy/CompanionManager.swift#L618-L620), [:700-706](../leanring-buddy/CompanionManager.swift#L700-L706)). Mac sign-off confirms the spinner-only behaviour. Whether Windows keeps the bubble after phase 6 (when TTS arrives) is an open 03-plan decision.
- **Fixture & harness**: Replay with `shared/fixtures/sse/point-basic.sse --event-delay-ms 200` (Lands 3c). Assert `displayTimeline[0].tMs ≤ 300` and `displayTimeline.length ≥ 2` before the last event.

**CLA-05 · A partial `[POINT…` tag is held back from the bubble** — `adr-derived`
- **Given** the accumulated text ends with an incomplete tag prefix, such as `… right here [PO` or `… [POINT:400,30`.
- **When** the bubble is updated.
- **Then**:
  - The bubble shows only the text before the `[`.
  - Once the tag completes and is the trailing tag, it is never shown, and the bubble shows `spokenText` (PNT-01).
  - A `[` that cannot start a POINT tag, for example `[note]`, is shown once it is disambiguated (ADR tracer item 6).
- **Mac reference**: not applicable (no streaming text; see CLA-04).
- **Fixture & harness**:
  - `shared/golden/sse-stream.json` (Lands 3c). Each case also carries `expected.displayedAfterEachEvent[]`, with cases `tag-split-across-deltas`, `bracket-not-a-tag`. Run by Core.
  - `point-basic.sse` must split its tag across at least two deltas, so that Replay exercises the hold-back.

**CLA-06 · Non-2xx from `/chat`**
- **Given** the Worker returns a non-2xx status.
- **When** the request completes.
- **Then**:
  - The error is built from the status and the whole body ([ClaudeAPI.swift:166-178](../leanring-buddy/ClaudeAPI.swift#L166-L178)).
  - `response_error` fires, the system-voice fallback speaks (TTS-03), and `voiceState` goes `responding` → `idle` ([CompanionManager.swift:715-724](../leanring-buddy/CompanionManager.swift#L715-L724)).
  - No history entry is added.
- **Fixture & harness**: Replay with `shared/fixtures/parity/chat-http-529.json` (`{status, headers, body}`) (Lands 6). Assert `fallbackUtterances[0]` and `analyticsEvents`.

**CLA-07 · Conversation history is capped at 10 exchanges**
- **Given** 10 completed exchanges.
- **When** an 11th completes.
- **Then**:
  - The oldest exchange is dropped, so the history holds 10 ([CompanionManager.swift:691-694](../leanring-buddy/CompanionManager.swift#L691-L694)).
  - Each stored assistant entry is `spokenText`, with the POINT tag stripped ([:684-689](../leanring-buddy/CompanionManager.swift#L684-L689)).
  - Entries are added only on a non-cancelled success (after parsing, before TTS). A TTS failure still keeps the entry.
  - The history is in memory only and is lost on quit (no persistence in Swift).
- **Fixture & harness**: `shared/golden/session-reducer.json` (Lands 6), cases `history-cap-10`, `history-stores-spoken-text`, `cancelled-not-stored`. Run by Core.

**CLA-08 · Model switch**
- **Given** the default model `claude-sonnet-4-6` ([CompanionManager.swift:111](../leanring-buddy/CompanionManager.swift#L111), AGENTS.md "Sonnet 4.6 default, Opus 4.6 optional").
- **When** the user picks "Opus" in the panel ([CompanionPanelView.swift:609-611](../leanring-buddy/CompanionPanelView.swift#L609-L611)).
- **Then**:
  - The next request's `model` is `claude-opus-4-6`, and the choice persists across restarts ([CompanionManager.swift:113-117](../leanring-buddy/CompanionManager.swift#L113-L117)). See ONB-04 for where it is stored.
  - A request already in flight keeps its model.
- **Fixture & harness**: Replay (Lands 6) with `--settings-override selectedClaudeModel=claude-opus-4-6`, asserting `requestBody.model`. Manual-VM **evidence:** a panel screenshot with Opus selected after a restart, plus the settings file contents.

---

## 4. POINT parsing and pointing (PNT)

Parser: [CompanionManager.swift:784-823](../leanring-buddy/CompanionManager.swift#L784-L823). The regex at [:786](../leanring-buddy/CompanionManager.swift#L786) is ported verbatim (audit G6). Mapping: [:638-677](../leanring-buddy/CompanionManager.swift#L638-L677), with a duplicate at [:958-973](../leanring-buddy/CompanionManager.swift#L958-L973) (seam 3). All PNT parser vectors land in **3c** in `shared/golden/point-tag.json`, run by Core and by XCTest on a Mac.

**PNT-01 · Valid tag (all branches of the parser)**

| Case id | Input tail | spokenText | coord | label | screen | Swift branch |
|---|---|---|---|---|---|---|
| `full-with-screen` | `…monitor. [POINT:400,300:terminal:screen2]` | text before tag, trimmed | (400,300) | `terminal` | 2 | [:807-822](../leanring-buddy/CompanionManager.swift#L807-L822) |
| `label-no-screen` | `[POINT:1100,42:color inspector]` | trimmed | (1100,42) | `color inspector` | nil | same |
| `coords-only` | `[POINT:10,20]` | trimmed | (10,20) | nil | nil | same |
| `spaces-around-comma` | `[POINT:10 , 20:x]` | trimmed | (10,20) | `x` | nil | `\s*,\s*` |
| `trailing-whitespace` | `[POINT:1,2:a]\n  ` | trimmed | (1,2) | `a` | nil | `\s*$` |
| `none` | `[POINT:none]` | trimmed | nil | `none` | nil | [:798-805](../leanring-buddy/CompanionManager.swift#L798-L805) |
| `no-tag` | `just text  ` | **unchanged, not trimmed** | nil | nil | nil | [:789-792](../leanring-buddy/CompanionManager.swift#L789-L792) |
| `tag-not-at-end` | `[POINT:1,2:a] more` | whole input, unchanged | nil | nil | nil | `$` anchor |
| `negative-or-decimal` | `[POINT:-5,2.5:a]` | whole input, unchanged (tag is spoken) | nil | nil | nil | `\d+` only |
| `label-leading-space` | `[POINT:1,2: a]` | whole input, unchanged | nil | nil | nil | `[^\]:\s]` first char |
| `label-looks-like-screen` | `[POINT:1,2:screen2]` | trimmed | (1,2) | **pinned by XCTest** | **pinned by XCTest** | greedy optional label group; the expected value must come from a Swift run |
| `screen-overflow` | `[POINT:1,2:a:screen99999999999999999999]` | trimmed | (1,2) | `a` | nil (Swift `Int()` overflow → nil; .NET must not throw) | [:812-815](../leanring-buddy/CompanionManager.swift#L812-L815) |
| `two-tags` | `[POINT:1,1:a] then [POINT:2,2:b]` | text before the last tag | (2,2) | `b` | nil | first match of an end-anchored pattern |

- **Given** each input above. **When** it is parsed. **Then** the result matches the row.
- **Fixture & harness**: `shared/golden/point-tag.json` (Lands 3c), run by Core and XCTest. ADR acceptance 1 requires at least the `none`, `no-tag`, `label-no-screen` and `screen-out-of-range` (PNT-02) cases.

**PNT-02 · Screen out of range → cursor's screen**
- **Given** a tag with `screenN`, where N is 0 or greater than the number of captured monitors, or N is missing.
- **When** the target monitor is chosen.
- **Then** the cursor's monitor is used ([CompanionManager.swift:640-646](../leanring-buddy/CompanionManager.swift#L640-L646)). Because of CLA-01 ordering that is the first capture, and the cursor's monitor is also "screen 1" in the labels.
- **Fixture & harness**: `shared/golden/screen-mapping.json` (Lands 3c), cases `screen0-falls-back`, `screen3-of-2-falls-back`, `missing-screen-uses-cursor`. Run by Core.

**PNT-03 · Coordinate mapping per monitor (mixed DPI, negative origin)**
- **Given** a screenshot of W×H for monitor M, whose `rcMonitor` is in **physical pixels** under Per-Monitor-V2 (ADR, audit D6).
- **When** the point (x, y) is mapped.
- **Then**:
  - Clamp `x` to [0, W] and `y` to [0, H], inclusive at W and H ([:659-661](../leanring-buddy/CompanionManager.swift#L659-L661)).
  - Scale: `X = rcMonitor.left + x·(monitorWidthPx / W)`, `Y = rcMonitor.top + y·(monitorHeightPx / H)` ([:663-665](../leanring-buddy/CompanionManager.swift#L663-L665)).
  - The AppKit y-flip ([:667-674](../leanring-buddy/CompanionManager.swift#L667-L674)) does not exist on Windows, because Windows uses a top-left origin.
- Required cases:
  - `primary-100pct`: 1920×1080 at (0,0), shot 1280×720, point (320,180) → (480, 270).
  - `secondary-150pct-left-negative`: 2880×1620 physical at (-2880, 0), shot 1280×720, point (320,180) → (-2160, 405).
  - `portrait`: 1080×1920, shot 720×1280.
  - `clamp-beyond-edge`: (2000, 900) on 1280×720 → right/bottom edge.
  - `truncated-short-edge`: 1512×982 → shot 1280×831.
- **Fixture & harness**: `shared/golden/screen-mapping.json` (Lands 3c), run by Core. Expected values are computed by hand in the vector, never by `ScreenshotToScreenMapper`.

**PNT-04 · The cursor lands within tolerance** — `adr-derived` tolerance
- **Given** the acceptance rig (100% and 150%, secondary monitor on the left with negative coordinates), with the cursor starting on either monitor.
- **When** replaying `point-quarter-screen1.sse` and `point-quarter-screen2.sse` (`[POINT:320,180:…:screenK]` on 1280×720).
- **Then** the overlay hotspot (`GetWindowRect` plus the fixed hotspot offset) is within **4 physical px** of `rcMonitor.left + 0.25·width`, `rcMonitor.top + 0.25·height` of monitor K (ADR acceptance 8).
- **Mac reference**: the Mac draws the buddy 8 pt right of and 12 pt below the target, clamped 20 pt inside the screen, after a bezier flight ([OverlayWindow.swift:463-474](../leanring-buddy/OverlayWindow.swift#L463-L474), [:495-568](../leanring-buddy/OverlayWindow.swift#L495-L568)). Whether Windows copies that visual offset is decided in 03-plan. The tolerance applies to the hotspot either way.
- **Fixture & harness**: `shared/fixtures/sse/point-quarter-screen1.sse`, `point-quarter-screen2.sse` (Lands 3c). Replay reports `point.targetPx` / `point.hotspotPx`. Manual-VM **evidence:** for each of the 4 runs (2 fixtures × 2 starting monitors), the replay report JSON and a screenshot of the target region.

**PNT-05 · The blue cursor appears only on the indicated monitor**
- **Given** two monitors and a tag for monitor K.
- **When** pointing starts.
- **Then** exactly one buddy is visible, and it is on monitor K.
- **Mac reference**: per-screen overlays hide themselves while another screen navigates ([OverlayWindow.swift:379-383](../leanring-buddy/OverlayWindow.swift#L379-L383), [:395-407](../leanring-buddy/OverlayWindow.swift#L395-L407)).
- **Windows**: there is a single moving window (ADR "UI framework"), so this reduces to `MonitorFromWindow(overlay) == monitor K`.
- **Fixture & harness**: Replay `point.monitorDevice` equals monitor K's device name (Lands 3c). Manual-VM **evidence:** a full-desktop screenshot showing one buddy.

**PNT-06 · Pointing state and flight**
- **Given** a parsed coordinate.
- **When** pointing starts.
- **Then**:
  - `voiceState` becomes `idle` *before* the target is published, so the triangle is visible ([CompanionManager.swift:629-636](../leanring-buddy/CompanionManager.swift#L629-L636)).
  - `element_pointed` fires with the label ([:678](../leanring-buddy/CompanionManager.swift#L678)).
  - Phase 6 behaviour: the buddy flies along a bezier arc (0.6–1.4 s), shows a bubble, holds 3 s, then flies back. Moving the mouse more than 100 pt during the return cancels the flight back. Afterwards the target is cleared ([OverlayWindow.swift:495-568](../leanring-buddy/OverlayWindow.swift#L495-L568), [:572-601](../leanring-buddy/OverlayWindow.swift#L572-L601), [:420-428](../leanring-buddy/OverlayWindow.swift#L420-L428), [:660-672](../leanring-buddy/OverlayWindow.swift#L660-L672)).
  - In the tracer bullet (3c) the buddy jumps to the target, with no bezier.
- **Fixture & harness**: `shared/golden/flight-path.json` (seam 9, Lands 6), run by Core. Manual-VM **evidence:** a screen recording (≥30 fps) of one flight and its return.

**PNT-07 · The buddy follows the user across virtual desktops** — `adr-derived`
- **Given** persistent cursor mode with the buddy visible, and at least two virtual desktops.
- **When** the user switches desktops with Win+Ctrl+→ / Win+Ctrl+←, 10 times.
- **Then** the buddy is visible on the new desktop within 500 ms, 10 times out of 10. Any miss flips the ADR 001 addendum "virtual-desktop spike verdict" to FALLBACK: the buddy lives on one desktop and is recreated on the next press.
- **Mac reference**: the overlay joins all Spaces through `collectionBehavior [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary]` ([OverlayWindow.swift:29](../leanring-buddy/OverlayWindow.swift#L29)).
- **Windows**: there is no documented "all desktops" pin (audit E2). The overlay's tool-window styles leave it assigned to no desktop, and `VirtualDesktopFollower` moves any desktop-bound followed window on `EVENT_SYSTEM_FOREGROUND` (T-005).
- **Fixture & harness**: `VirtualDesktopFollowTests` (fake desktop service, Lands 3c). Manual-VM **evidence:** a screen recording of the 10 switches, plus the app log. A `virtual desktop: MoveWindowToDesktop failed` line marks a refused move.

---

## 5. TTS

Swift: [ElevenLabsTTSClient.swift](../leanring-buddy/ElevenLabsTTSClient.swift), [CompanionManager.swift:700-766](../leanring-buddy/CompanionManager.swift#L700-L766). All TTS scenarios land in phase 6.

**TTS-01 · Playback starts after the full response**
- **Given** the stream has completed and `spokenText` is not blank.
- **When** TTS runs.
- **Then**:
  - `POST {WorkerBaseUrl}/tts` with `Accept: audio/mpeg` and body `{"text": spokenText, "model_id": "eleven_flash_v2_5", "voice_settings": {"stability": 0.5, "similarity_boost": 0.75}}` ([ElevenLabsTTSClient.swift:33-48](../leanring-buddy/ElevenLabsTTSClient.swift#L33-L48)).
  - The **whole MP3 is downloaded before playback** ([:50](../leanring-buddy/ElevenLabsTTSClient.swift#L50), [:65-67](../leanring-buddy/ElevenLabsTTSClient.swift#L65-L67)), despite the file's "streams" comment (audit §1b).
  - `voiceState` stays `processing` until playback starts, then becomes `responding` ([CompanionManager.swift:700-706](../leanring-buddy/CompanionManager.swift#L700-L706)). When playback ends, `voiceState` → `idle` ([:721-724](../leanring-buddy/CompanionManager.swift#L721-L724)).
  - If `spokenText` is blank, no `/tts` call is made ([:702](../leanring-buddy/CompanionManager.swift#L702)).
- **Fixture & harness**: Replay with `point-basic.sse` plus `shared/fixtures/parity/tts-ok.json` (`{status:200, bodyFile:"tts-ok.mp3"}`). Assert `ttsRequests[0]` and the `voiceStateTimeline` order. Manual-VM **evidence:** audible playback, plus log lines `tts bytes=N` and `tts playing`.

**TTS-02 · Transient cursor mode stays visible until TTS ends + 1 s**
- **Given** transient mode (`isClickyCursorEnabled = false`), with the overlay shown for this interaction ([CompanionManager.swift:484-489](../leanring-buddy/CompanionManager.swift#L484-L489)).
- **When** the response finishes.
- **Then**:
  - The app polls every 200 ms until TTS stops, then every 200 ms until the pointing target is cleared, then waits **1 s**, then fades the overlay out over 0.4 s ([CompanionManager.swift:732-756](../leanring-buddy/CompanionManager.swift#L732-L756), [OverlayWindow.swift:819](../leanring-buddy/OverlayWindow.swift#L819)).
  - A new press during that wait cancels the hide ([CompanionManager.swift:480-482](../leanring-buddy/CompanionManager.swift#L480-L482)).
  - The hide is also scheduled after a press with no speech ([:451-459](../leanring-buddy/CompanionManager.swift#L451-L459)).
- **Fixture & harness**: `shared/golden/session-reducer.json`, cases `transient-hide-after-tts-plus-1s`, `transient-hide-waits-for-pointing`, `transient-hide-cancelled-by-press`. Run by Core with a virtual clock. Manual-VM **evidence:** a screen recording showing the overlay gone 1.0–1.6 s after the audio ends.

**TTS-03 · Failure → system-voice fallback**
- **Given** `/tts` returns non-2xx (for example out of ElevenLabs credits), or any error happens in the response pipeline.
- **When** the error is caught.
- **Then**:
  - `tts_error` or `response_error` fires.
  - The system voice speaks exactly `I'm all out of credits. Please DM Farza and tell him to bring me back to life.`
  - `voiceState` → `responding` ([CompanionManager.swift:707-711](../leanring-buddy/CompanionManager.swift#L707-L711), [:715-719](../leanring-buddy/CompanionManager.swift#L715-L719), [:761-766](../leanring-buddy/CompanionManager.swift#L761-L766)).
  - Windows speaks it through SAPI (`System.Speech`, audit F7).
  - The fallback fires for *any* error, not only for running out of credits. That is the Swift behaviour, and Windows matches it.
- **Fixture & harness**: Replay with `point-basic.sse` plus `shared/fixtures/parity/tts-http-402.json`. Assert `fallbackUtterances == [<string above>]` and that `analyticsEvents` contains `tts_error`. Manual-VM **evidence:** audible system voice and the log line `fallback voice spoken`.

**TTS-04 · A new press stops playback**
- **Given** TTS is playing.
- **When** the shortcut is pressed.
- **Then** playback stops immediately ([CompanionManager.swift:496](../leanring-buddy/CompanionManager.swift#L496), [ElevenLabsTTSClient.swift:77-80](../leanring-buddy/ElevenLabsTTSClient.swift#L77-L80)). This is part of PTT-02.
- **Fixture & harness**: Manual-VM **evidence:** a recording, or the log `tts stopped (preempted)` within 100 ms of `ptt pressed`.

---

## 6. Panel and tray (PNL)

Swift: [MenuBarPanelManager.swift](../leanring-buddy/MenuBarPanelManager.swift), [CompanionPanelView.swift](../leanring-buddy/CompanionPanelView.swift). Windows: H.NotifyIcon tray plus a WPF panel (audit B1–B5). Everything here is UI-only and lands in phase 6.

**PNL-01 · Clicking the icon toggles the panel**
- **Given** the tray icon is present. On Win11 it may sit in the overflow flyout until the user pins it (audit B1).
- **When** the user clicks it once, then again.
- **Then** the panel opens anchored to the icon, then closes ([MenuBarPanelManager.swift:119-125](../leanring-buddy/MenuBarPanelManager.swift#L119-L125), [:178-198](../leanring-buddy/MenuBarPanelManager.swift#L178-L198)). On Windows the anchor depends on the taskbar edge (audit B4).
- **Fixture & harness**: Manual-VM. **Evidence:** screenshots with the taskbar on the bottom and on the left.

**PNL-02 · Clicking outside dismisses the panel**
- **Given** the panel is open.
- **When** the user clicks outside it.
- **Then**:
  - The panel hides after 0.3 s ([MenuBarPanelManager.swift:206-235](../leanring-buddy/MenuBarPanelManager.swift#L206-L235)). On Windows it hides on `Deactivated` (audit B5).
  - Swift keeps the panel open while permissions are missing and the app is inactive ([:226-230](../leanring-buddy/MenuBarPanelManager.swift#L226-L230)). On Windows the only permission is the microphone and there is no system prompt (audit F4). So the panel stays open while the mic is denied, letting the user follow the settings deep link.
  - The panel also hides when push-to-talk is pressed, and on its `×` button ([CompanionManager.swift:491-492](../leanring-buddy/CompanionManager.swift#L491-L492), [CompanionPanelView.swift:106-107](../leanring-buddy/CompanionPanelView.swift#L106-L107)).
- **Fixture & harness**: Manual-VM. **Evidence:** the log lines `panel hidden reason=deactivated|ptt|close`.

**PNL-03 · The model picker persists**
- Covered by CLA-08 (the picker is only shown once the user is onboarded and the mic is granted, [CompanionPanelView.swift:28-34](../leanring-buddy/CompanionPanelView.swift#L28-L34)).
- **Fixture & harness**: as CLA-08.

**PNL-04 · Permissions UI is reduced to the microphone** — `adr-derived` reduction
- **Given** the microphone is not granted.
- **When** the panel opens.
- **Then**:
  - The PERMISSIONS section shows **only** the Microphone row. The Accessibility, Screen Recording and Screen Content rows are dropped (audit C4, D3, D4; ADR). `allPermissionsGranted` is `micGranted` ([CompanionManager.swift:102-104](../leanring-buddy/CompanionManager.swift#L102-L104), [CompanionPanelView.swift:245-262](../leanring-buddy/CompanionPanelView.swift#L245-L262)).
  - "Grant" opens `ms-settings:privacy-microphone`, because unpackaged apps get no prompt (audit F4; Swift: [CompanionPanelView.swift:465-476](../leanring-buddy/CompanionPanelView.swift#L465-L476)).
  - The revoked copy "Grant all four below" ([:156](../leanring-buddy/CompanionPanelView.swift#L156)) becomes microphone-only copy.
  - Polling detects a later grant within 1.5 s ([CompanionManager.swift:414-420](../leanring-buddy/CompanionManager.swift#L414-L420)).
- **Fixture & harness**: Manual-VM with the mic toggled off in Settings › Privacy. **Evidence:** a panel screenshot showing one row, the Settings page that opened, and a screenshot of the row flipping to "Granted" within 1.5 s after the mic is turned back on.

**PNL-05 · Quit works**
- **Given** the app is running.
- **When** the user clicks "Quit Clicky" ([CompanionPanelView.swift:685-691](../leanring-buddy/CompanionPanelView.swift#L685-L691)).
- **Then**:
  - The stop path runs: the hook is removed, dictation is cancelled, the overlay is hidden, and in-flight work is cancelled ([leanring_buddyApp.swift:56-58](../leanring-buddy/leanring_buddyApp.swift#L56-L58), [CompanionManager.swift:290-303](../leanring-buddy/CompanionManager.swift#L290-L303)).
  - The process exits with code 0, and the tray icon is removed with no ghost icon.
- **Fixture & harness**: Manual-VM. **Evidence:** Task Manager or `Get-Process Clicky` shows no process, the log line `shutdown complete`, and a screenshot of the tray after hovering over it.

**PNL-06 · Header status text**
- **Given** the combinations of onboarded, permissions, overlay visible and `voiceState`.
- **When** the panel renders.
- **Then**:
  - The status text is `Setup` (not onboarded or mic missing), `Ready` (overlay hidden), or `Active` / `Listening` / `Processing` / `Responding` ([CompanionPanelView.swift:742-759](../leanring-buddy/CompanionPanelView.swift#L742-L759)).
  - The dot colour follows [:728-740](../leanring-buddy/CompanionPanelView.swift#L728-L740).
  - The hint copy says "Hold Ctrl+Alt to talk.", replacing "Control+Option" ([:130](../leanring-buddy/CompanionPanelView.swift#L130)).
- **Fixture & harness**: `shared/golden/session-reducer.json` case group `panel-status`, run by Core.

**PNL-07 · The panel auto-opens when setup is incomplete**
- **Given** the user is not onboarded, or the mic is not granted.
- **When** the app launches.
- **Then** the panel opens about 0.3 s after the tray icon appears ([leanring_buddyApp.swift:47-51](../leanring-buddy/leanring_buddyApp.swift#L47-L51), [MenuBarPanelManager.swift:112-117](../leanring-buddy/MenuBarPanelManager.swift#L112-L117)).
- **Fixture & harness**: Manual-VM on a clean profile. **Evidence:** a screenshot within 2 s of launch.

---

## 7. State and onboarding (ONB)

**ONB-01 · First-run onboarding flow** (Windows copy: "Ctrl + Alt")
- **Given** a clean `%LOCALAPPDATA%\Clicky\` and the mic granted.
- **When** the user follows the panel.
- **Then**:
  - Panel copy goes intro → "Drop your email…" → "You're all set. Hit Start…" ([CompanionPanelView.swift:127-180](../leanring-buddy/CompanionPanelView.swift#L127-L180)).
  - Email submit is enabled only for a non-blank trimmed value. Submitting POSTs `{"email"}` to FormSpark, calls PostHog `identify(email, {email})`, and persists `hasSubmittedEmail` ([CompanionManager.swift:152-173](../leanring-buddy/CompanionManager.swift#L152-L173), [CompanionPanelView.swift:184-222](../leanring-buddy/CompanionPanelView.swift#L184-L222)).
  - "Start" ([:224-225](../leanring-buddy/CompanionPanelView.swift#L224-L225)) dismisses the panel, persists `hasCompletedOnboarding=true`, fires `onboarding_started`, and plays `ff.mp3` at volume 0.3, fading out over 3 s after 90 s ([CompanionManager.swift:201-218](../leanring-buddy/CompanionManager.swift#L201-L218), [:240-282](../leanring-buddy/CompanionManager.swift#L240-L282)).
  - The overlay's first appearance types "hey! i'm clicky" ([OverlayWindow.swift:173](../leanring-buddy/OverlayWindow.swift#L173), [:352-361](../leanring-buddy/OverlayWindow.swift#L352-L361)), then the HLS video plays (audit E8). The demo interaction fires at 40 s (ONB-02).
  - When the video ends, the app waits 2 s + 0.3 s and then types `press Ctrl + Alt and introduce yourself`, which auto-dismisses after 10 s ([CompanionManager.swift:862-879](../leanring-buddy/CompanionManager.swift#L862-L879), [:896-927](../leanring-buddy/CompanionManager.swift#L896-L927); Mac copy at [:897](../leanring-buddy/CompanionManager.swift#L897)).
  - Push-to-talk is ignored while the video plays ([:477-478](../leanring-buddy/CompanionManager.swift#L477-L478)).
  - Onboarding tells the user to pin the tray icon (ADR, audit B1).
- **Fixture & harness**: Manual-VM (Lands 6). **Evidence:** a screen recording of the whole flow, plus the settings file before and after.
- **Owner decision 2026-10-23 (Windows):** the onboarding video is dropped on Windows. There is no video host and no 40 s demo trigger, and `onboarding_video_completed` never fires. The bullets above that key off the video (HLS playback, the 40 s demo, the video-end closing prompt, PTT ignored while the video plays) have no video to key off on Windows. The music still plays as specified: `ff.mp3` at 0.3, fading to 0 between 90 s and 93 s (T-038, `OnboardingTimingTests.MusicFadesOver3sAfter90s`). See ADR 001, "Addendum — onboarding video dropped".

**ONB-02 · Onboarding demo interaction**
- **Given** the video reaches 40 s, and `voiceState` is `idle` or `responding`.
- **When** the demo fires.
- **Then**:
  - Only the **cursor's monitor** is captured and sent, with the onboarding-demo system prompt (`shared/prompts/onboarding-demo.txt`, moved from [CompanionManager.swift:950-962](../leanring-buddy/CompanionManager.swift#L950-L962)) and the user text `look around my screen and find something interesting to point at` ([:964-990](../leanring-buddy/CompanionManager.swift#L964-L990)).
  - A parsed coordinate is mapped as in PNT-03, and the bubble shows Claude's comment ([:955-985](../leanring-buddy/CompanionManager.swift#L955-L985)).
  - With no coordinate, nothing happens.
  - `onboarding_demo_triggered` fires.
- **Fixture & harness**: Replay with `shared/fixtures/sse/onboarding-demo.sse` and `--debug-trigger-onboarding-demo` (Lands 6). Assert `requestBody` has 1 image and `point.screenIndex == 0`.

**ONB-03 · Transient vs persistent cursor mode**
- **Given** `isClickyCursorEnabled` is true (default when the key is absent, [CompanionManager.swift:122-124](../leanring-buddy/CompanionManager.swift#L122-L124)) or false.
- **When** the setting changes ([:126-140](../leanring-buddy/CompanionManager.swift#L126-L140)), or the app starts while onboarded ([:186-194](../leanring-buddy/CompanionManager.swift#L186-L194)).
- **Then**:
  - When true, the overlay is shown at once and persists across interactions.
  - When false, the overlay is hidden and appears only per interaction (TTS-02).
- **Note**: the "Show Clicky" toggle row exists ([CompanionPanelView.swift:548-575](../leanring-buddy/CompanionPanelView.swift#L548-L575)) but is **not rendered** ([:52-59](../leanring-buddy/CompanionPanelView.swift#L52-L59)). On the Mac, transient mode is reachable only by editing defaults. Windows carries the setting and its behaviour but, for parity, renders **no** toggle. Exposing it is a product call.
- **Fixture & harness**: `shared/golden/session-reducer.json`, cases `persistent-shows-on-start`, `transient-hidden-on-start`. Run by Core. Replay with `--settings-override isClickyCursorEnabled=false`.

**ONB-04 · Settings persistence (%LOCALAPPDATA%)**
- **Given** these keys: `selectedClaudeModel` (default `claude-sonnet-4-6`), `isClickyCursorEnabled` (default `true`), `hasCompletedOnboarding` (default `false`), `hasSubmittedEmail` (default `false`) ([CompanionManager.swift:111-150](../leanring-buddy/CompanionManager.swift#L111-L150)). `hasScreenContentPermission` ([:345](../leanring-buddy/CompanionManager.swift#L345)) is dropped on Windows (audit D4).
- **When** each key is changed and the app is restarted.
- **Then**:
  - The values survive the restart, stored as JSON under `%LOCALAPPDATA%\Clicky\` (ADR "Configuration", audit A9). The file name is set in 03-plan.
  - A missing key takes its default.
  - A missing or corrupt file starts with all defaults and is rewritten on the next change. This rule is Windows-only (`adr-derived`): `UserDefaults` has no corrupt state.
- **Fixture & harness**: `shared/golden/settings-defaults.json` (Lands 6), run by Core. Manual-VM **evidence:** file contents before and after a restart.

**ONB-05 · Replaying onboarding**
- **Given** the user is onboarded.
- **When** they click "Watch Onboarding Again" ([CompanionPanelView.swift:699-715](../leanring-buddy/CompanionPanelView.swift#L699-L715)).
- **Then** the panel is dismissed, `onboarding_replayed` fires, the music starts, and the overlay is recreated as a first appearance ([CompanionManager.swift:223-231](../leanring-buddy/CompanionManager.swift#L223-L231)).
- **Fixture & harness**: Manual-VM. **Evidence:** a recording.
- **Owner decision 2026-10-23 (Windows):** the onboarding video is dropped on Windows. There is no video host and no 40 s demo trigger, and `onboarding_video_completed` never fires. Replaying onboarding restarts the music and the first-appearance overlay only.

---

## 8. Analytics (AN)

Swift catalogue: [ClickyAnalytics.swift:16-120](../leanring-buddy/ClickyAnalytics.swift#L16-L120). Windows: the PostHog .NET SDK or a direct POST (audit H1), behind an `IAnalyticsSink` (seam 8). All AN scenarios land in phase 6.

**AN-01 · Event names, properties and triggers**

| Event | Properties | Fired at |
|---|---|---|
| `app_opened` | `app_version` | [leanring_buddyApp.swift:43](../leanring-buddy/leanring_buddyApp.swift#L43), [ClickyAnalytics.swift:27-32](../leanring-buddy/ClickyAnalytics.swift#L27-L32) |
| `onboarding_started` | — | [CompanionManager.swift:209](../leanring-buddy/CompanionManager.swift#L209) |
| `onboarding_replayed` | — | [CompanionManager.swift:225](../leanring-buddy/CompanionManager.swift#L225) |
| `onboarding_video_completed` | — | [CompanionManager.swift:869](../leanring-buddy/CompanionManager.swift#L869) |
| `onboarding_demo_triggered` | — | [CompanionManager.swift:858](../leanring-buddy/CompanionManager.swift#L858) |
| `permission_granted` | `permission` | [CompanionManager.swift:332-341](../leanring-buddy/CompanionManager.swift#L332-L341), [:382](../leanring-buddy/CompanionManager.swift#L382). On Windows only `"microphone"` can fire |
| `all_permissions_granted` | — | [CompanionManager.swift:348-350](../leanring-buddy/CompanionManager.swift#L348-L350). On Windows it fires on the same transition as the microphone grant |
| `push_to_talk_started` | — | [CompanionManager.swift:511](../leanring-buddy/CompanionManager.swift#L511) |
| `push_to_talk_released` | — | [CompanionManager.swift:533](../leanring-buddy/CompanionManager.swift#L533) |
| `user_message_sent` | `transcript`, `character_count` | [CompanionManager.swift:523](../leanring-buddy/CompanionManager.swift#L523), [ClickyAnalytics.swift:83-88](../leanring-buddy/ClickyAnalytics.swift#L83-L88) |
| `ai_response_received` | `response` (spoken text, tag stripped), `character_count` | [CompanionManager.swift:698](../leanring-buddy/CompanionManager.swift#L698), [ClickyAnalytics.swift:91-96](../leanring-buddy/ClickyAnalytics.swift#L91-L96) |
| `element_pointed` | `element_label` (`"unknown"` if nil) | [CompanionManager.swift:678](../leanring-buddy/CompanionManager.swift#L678), [ClickyAnalytics.swift:100-104](../leanring-buddy/ClickyAnalytics.swift#L100-L104) |
| `response_error` | `error` | [CompanionManager.swift:716](../leanring-buddy/CompanionManager.swift#L716) |
| `tts_error` | `error` | [CompanionManager.swift:708](../leanring-buddy/CompanionManager.swift#L708) |
| identify | distinct id = email, `{email}` | [CompanionManager.swift:161-163](../leanring-buddy/CompanionManager.swift#L161-L163) |

- **Given** each trigger. **When** it happens. **Then** exactly that event is sent with exactly those property keys, to host `https://us.i.posthog.com` with the same project key ([ClickyAnalytics.swift:16-22](../leanring-buddy/ClickyAnalytics.swift#L16-L22)).
- `character_count` is Swift `String.count`, which counts grapheme clusters. Windows must count text elements (`StringInfo.LengthInTextElements`), not UTF-16 `Length`.
- **Fixture & harness**: `shared/golden/analytics-events.json`, cases per event plus `character-count-emoji` (`"👍🏽ok"` → 3). Run by Core. Replay asserts the `analyticsEvents` order for one full exchange.

**AN-02 · Press/release asymmetry is preserved**
- **Given** a press that is ignored (dictation in progress, PTT-02b, or the onboarding video is playing).
- **When** the user presses and releases.
- **Then** no `push_to_talk_started` is sent, but `push_to_talk_released` **is** sent ([CompanionManager.swift:476-478](../leanring-buddy/CompanionManager.swift#L476-L478) return before [:511](../leanring-buddy/CompanionManager.swift#L511); [:533](../leanring-buddy/CompanionManager.swift#L533) has no guard). This keeps the dashboard funnels comparable across platforms.
- **Fixture & harness**: `session-reducer.json` case `ignored-press-still-emits-release`, run by Core.

**AN-03 · Transcript and response content (deferred owner decision)**
- **Given** the product owner deferred the decision on transcript content ([STATUS](STATUS.md) "Open product items"; ADR "Not changed, flagged"; audit H1).
- **When** `user_message_sent` / `ai_response_received` fire.
- **Then** the **full** transcript and response text are sent, as on the Mac ([ClickyAnalytics.swift:83-96](../leanring-buddy/ClickyAnalytics.swift#L83-L96)). The Windows sink must make these two properties removable through **one** switch (a single place in the catalogue), so that if the owner later decides to strip them the change is one line. Until then, parity means the content is sent.
- **Fixture & harness**: `analytics-events.json` cases `transcript-included`, `response-included`, run by Core. This scenario must be re-signed if the owner's decision changes.

---

## Not specified (deliberately)

- **TLS warm-up HEAD** ([ClaudeAPI.swift:64-96](../leanring-buddy/ClaudeAPI.swift#L64-L96)), **request timeouts** ([:25-31](../leanring-buddy/ClaudeAPI.swift#L25-L31)) and **RMS waveform smoothing** ([BuddyDictationManager.swift:687-734](../leanring-buddy/BuddyDictationManager.swift#L687-L734)): performance and visuals, not behaviour. They are owned by 03-plan.
- **Dead code** I1–I5 (audit §1a I): not ported, so no scenarios.
- **Login-item / Run-key registration** (audit A3) and **auto-update** (ADR "Auto-update"): packaging-phase concerns, gated in `ci.yml`.

---

## Traceability

Lands: `3c` = tracer bullet, `6` = development. "03-plan §x" is a placeholder until the implementation plan exists. Mac sign-off is ticked only during the phase-8 session on a real Mac.

| ID | Audit rows | Seam | Plan ref | Lands | Capture status | Mac sign-off |
|---|---|---|---|---|---|---|
| PTT-01 | C1, C3, G9 | 7 | 03-plan §x | 3c | code-derived | [ ] |
| PTT-02 | G7, F5 | 6 | 03-plan §x | 6 | code-derived | [ ] |
| PTT-03 | G7 | 6 | 03-plan §x | 6 | code-derived | [ ] |
| PTT-04 | C3 | 7 | 03-plan §x | 3c (default) / 6 | code-derived | [ ] |
| PTT-05 | C1 | 7 | 03-plan §x | 3c | adr-derived | [ ] |
| PTT-06 | C2 | — | 03-plan §x | 3c | adr-derived | [ ] |
| PTT-07 | C1, C2 | — | 03-plan §x | 3c | adr-derived | [ ] |
| PTT-08 | C1 | — | 03-plan §x | 6 | adr-derived | [ ] |
| STT-01 | G4, G2 | 1 | 03-plan §x | 6 | code-derived | [ ] |
| STT-02 | G4, F1 | 1 | 03-plan §x | 6 | code-derived | [ ] |
| STT-03 | G4 | turn assembly | 03-plan §x | 6 | code-derived | [ ] |
| STT-04 | G4, G8 | 1 | 03-plan §x | 6 | code-derived | [ ] |
| STT-05 | G8 | 6 | 03-plan §x | 6 | code-derived | [ ] |
| STT-06 | G4 | 1 | 03-plan §x | 6 | code-derived | [ ] |
| STT-07 | G4 | 1 | 03-plan §x | 6 | code-derived | [ ] |
| STT-08 | G4, G2 | 1, 6 | 03-plan §x | 6 | code-derived | [ ] |
| CLA-01 | D1, D2, D5, D6, D7 | 4 | 03-plan §x | 3c (VM) / 6 (vector) | code-derived | [ ] |
| CLA-02 | G1, G5 | 4, 10 | 03-plan §x | 3c (replay) / 6 (vector) | code-derived | [ ] |
| CLA-03 | G1 | 4 | 03-plan §x | 3c | code-derived | [ ] |
| CLA-04 | G1, E1 | 4 | 03-plan §x | 3c | adr-derived | [ ] |
| CLA-05 | G6, E1 | 2, 4 | 03-plan §x | 3c | adr-derived | [ ] |
| CLA-06 | G1 | 4, 6 | 03-plan §x | 6 | code-derived | [ ] |
| CLA-07 | — | 6 | 03-plan §x | 6 | code-derived | [ ] |
| CLA-08 | A9, B7 | — | 03-plan §x | 6 | code-derived | [ ] |
| PNT-01 | G6 | 2 | 03-plan §x | 3c | code-derived | [ ] |
| PNT-02 | D6 | 3 | 03-plan §x | 3c | code-derived | [ ] |
| PNT-03 | D6 | 3 | 03-plan §x | 3c | code-derived | [ ] |
| PNT-04 | D6, E1, E6 | 3 | 03-plan §x | 3c | adr-derived | [ ] |
| PNT-05 | E1, D6 | — | 03-plan §x | 3c | code-derived | [ ] |
| PNT-06 | E3, E6 | 6, 9 | 03-plan §x | 3c (jump) / 6 (flight) | code-derived | [ ] |
| PNT-07 | E2 | — | ADR 001 addendum | 3c | adr-derived | [ ] |
| TTS-01 | F5, G2 | 5 | 03-plan §x | 6 | code-derived | [ ] |
| TTS-02 | E7, G10 | 6 | 03-plan §x | 6 | code-derived | [ ] |
| TTS-03 | F7 | 5, 6 | 03-plan §x | 6 | code-derived | [ ] |
| TTS-04 | F5 | 5, 6 | 03-plan §x | 6 | code-derived | [ ] |
| PNL-01 | B1, B2, B4 | — | 03-plan §x | 6 | code-derived | [ ] |
| PNL-02 | B3, B5, B6 | — | 03-plan §x | 6 | code-derived | [ ] |
| PNL-03 | A9, B7 | — | 03-plan §x | 6 | code-derived | [ ] |
| PNL-04 | C4, D3, D4, F4, B12 | — | 03-plan §x | 6 | adr-derived | [ ] |
| PNL-05 | A10 | — | 03-plan §x | 6 | code-derived | [ ] |
| PNL-06 | B7 | 6 | 03-plan §x | 6 | code-derived | [ ] |
| PNL-07 | A1, B1 | — | 03-plan §x | 6 | code-derived | [ ] |
| ONB-01 | E8, F6, A8, H1 | 6, 10 | 03-plan §x | 6 | code-derived | [ ] |
| ONB-02 | E8, D1 | 2, 3, 10 | 03-plan §x | 6 | code-derived | [ ] |
| ONB-03 | A9, E7 | 6 | 03-plan §x | 6 | code-derived | [ ] |
| ONB-04 | A9 | — | 03-plan §x | 6 | code-derived (+ adr-derived corrupt-file rule) | [ ] |
| ONB-05 | E8 | 6 | 03-plan §x | 6 | code-derived | [ ] |
| AN-01 | H1 | 8 | 03-plan §x | 6 | code-derived | [ ] |
| AN-02 | H1 | 6, 8 | 03-plan §x | 6 | code-derived | [ ] |
| AN-03 | H1 | 8 | 03-plan §x | 6 | code-derived | [ ] |

### Files this spec requires

| Path | Lands | Used by |
|---|---|---|
| `shared/golden/point-tag.json` | 3c | PNT-01 |
| `shared/golden/screen-mapping.json` | 3c | PNT-02, PNT-03 |
| `shared/golden/sse-stream.json` | 3c | CLA-03, CLA-05 |
| `shared/golden/push-to-talk.json` | 3c | PTT-01, PTT-04, PTT-05 |
| `shared/fixtures/sse/point-basic.sse` | 3c | CLA-02, CLA-04, CLA-05, TTS-01, TTS-03 |
| `shared/fixtures/sse/point-quarter-screen1.sse`, `point-quarter-screen2.sse` | 3c | PNT-04, PNT-05 |
| `shared/prompts/companion-voice-response.txt` | 3c | CLA-02 |
| `shared/golden/{assemblyai-turns,transcript-finalize,transcription-keyterms,capture-plan,claude-request,session-reducer,flight-path,settings-defaults,analytics-events}.json` | 6 | STT, CLA, PNT-06, TTS-02, PNL-06, ONB, AN |
| `shared/prompts/onboarding-demo.txt`, `shared/fixtures/sse/onboarding-demo.sse` | 6 | ONB-02 |
| `shared/fixtures/stt/{assemblyai-session-basic,assemblyai-final-on-keyup,assemblyai-stall-after-keyup,assemblyai-error-midstream}.jsonl` | 6 | STT |
| `shared/fixtures/parity/{transcribe-token-http-500,chat-http-529,tts-ok,tts-http-402}.json`, `tts-ok.mp3` | 6 | STT-08, CLA-06, TTS |
