# ADR 001 — Windows strategy, UI framework, and tracer bullet

- **Status:** **Approved** by product owner on 2026-10-22, as written (option B; macOS seam-extraction deferred out of v1; both flagged open items — Worker client auth, PostHog transcript content — deferred as product decisions, tracked in `docs/STATUS.md`)
- **Date:** 2026-10-22
- **Author:** Architect
- **Basis:** `docs/01-portability-audit.md` (cited as "audit"; rows such as C1, sections such as §1b)

## Context

Clicky is a tray-only AI companion: you hold a push-to-talk shortcut, it transcribes your speech, sends the transcript and screenshots of every monitor to Claude through the Cloudflare Worker, speaks the streamed reply, and flies a blue cursor to any `[POINT:x,y:label:screenN]` target. Windows is the next platform, and the Worker stays unchanged (audit header). The audit found that only about 25% of the 7,637 Swift lines are portable core logic, and none of it compiles off Apple platforms as-is (§1b). Test coverage is effectively zero (§0). There is no macOS host in the working environment, so the running Mac app cannot be used as a parity reference until a Mac is available (`docs/STATUS.md` blocker 1, audit risk 3).

## Options considered

Effort shape: S/M/L per area. Areas: **Core** = the ~1.9k core lines (§1b); **Shell** = tray, panel, shortcut, capture, audio, overlay (rows A–F); **Mac** = changes the macOS app must absorb.

### (A) Direct port: keep Swift, use the swift.org Windows toolchain
- The core could be reused almost verbatim, but only after seams 1, 6 and 9 remove `AVAudioPCMBuffer` and Combine (§1b runtime evaluation, "Swift on Windows").
- `URLSession.bytes(for:)`/`AsyncBytes` and WebSocket support in swift-corelibs-foundation are unverified, and these are exactly what G1 and G4 depend on.
- There is no first-party Windows UI, so every B, C, D and E row still needs Win32/WinRT, reached through swift-winrt or a C-ABI bridge to a C# shell, with async callbacks crossing that bridge (§1b).
- Debugger, IDE and packaging support on Windows is thin (§1b).
- Effort: Core S–M, Shell L (plus the bridge), Mac M (seam refactors land in the shipping Mac app).
- **Decisive drawback:** the reuse covers only the ~25% core. The ~75% shell, which holds all three top risks (§1d), still has to be written against Windows APIs, from a language with no Windows UI story.

### (B) Native Windows rewrite in C#/.NET; core duplicated and pinned by shared golden vectors; Worker unchanged
- Every G row (G1–G10) maps 1:1 onto the BCL at confidence H: `HttpClient`, `ClientWebSocket`, `System.Text.Json`, `Regex`, `Channel<T>` (§1b, "C# / .NET 10").
- Shell rows map to native libraries: WPF (A1, B3, B7, E1, E3), CsWin32 (C1, D1, D2, D6), C#/WinRT (D1, E8, F4), NAudio (F1–F6). All are MIT-licensed (§1c).
- About 1.9k Swift lines are re-expressed in C#. Drift between the two cores is contained by the shared JSON vectors for seams 2, 3, 4, 6 and 7 plus the AssemblyAI turn assembly (§1b, "Shared golden test vectors").
- `Clicky.Core` targets plain `net10.0`, so its tests run on any CI runner (§1b).
- Effort: Core M, Shell M, Overlay/shortcut/capture spikes M–L (E1, E2, C1, C2, D1 carry these spikes under any option), Packaging S, Mac S.
- **Decisive drawback:** two cores to maintain. The vectors catch logic drift but cannot catch timing or UX drift, and the Mac side cannot be run here (STATUS blocker 1).

### (C) Shared core in Rust with UniFFI bindings to both shells
- Good fit for the network pieces (`reqwest`, `tokio-tungstenite`), but the core must be rewritten on both platforms (§1b, "Rust").
- The Mac app has to move onto the Rust core for any sharing to happen, which is a large change to a working product.
- A C# or Rust Windows UI is still needed, so the shell work is the same as in B.
- Adds async/callback FFI design, a new language in a Swift-only repo, and UniFFI's MPL-2.0 licence (§1c, flagged).
- Effort: Core L ×2, Shell M–L, Mac L.
- **Decisive drawback:** it is the slowest route to a first Windows build and reduces none of the top-3 risks, which sit in the shell (risks 1 and 2) or are already handled by golden vectors (risk 3). The audit rates it "best long-term, not for the port" (§1b).

### (D) Web shell (Electron or Tauri)
- HTTP, WebSocket and SSE are trivial in TypeScript, the same language as the Worker. Electron covers click-through, `alwaysOnTop('screen-saver')`, `desktopCapturer` and `setContentProtection` → `WDA_EXCLUDEFROMCAPTURE` (§1b, "TypeScript").
- `globalShortcut` cannot register modifier-only combos, so the Ctrl+Alt default (C1) needs a native hook addon anyway. The usual addon, libuiohook, is GPL/LGPL-3.0 and rejected (§1c), so we would write a `WH_KEYBOARD_LL` addon regardless.
- Per-monitor transparent windows under mixed DPI are a known weak spot, which compounds risk 1 (E1, D6). The mic path needs `getUserMedia` plus AudioWorklet resampling (F1, F2).
- Electron costs about 150 MB+ resident for a tray utility (§1b). Tauri is lighter, but its native pieces bring back C's language cost.
- Effort: Core S, Shell M plus native addons M, Mac L if the Mac app is replaced, otherwise this is a third codebase.
- **Decisive drawback:** risks 1 and 2 still need native code, so this is a native port with a browser bolted on. It only pays off if the product goal becomes "one codebase for both OSes", and nobody has stated that goal.

## Decision

**Adopt option B: a native Windows app in C# on .NET 10 with a WPF shell.** Core logic is duplicated in `Clicky.Core` and pinned to the Swift behaviour by shared golden vectors. The Worker is unchanged. This follows the audit's recommendation (§1b) without overruling anything.

Reasoning:
1. The portable core is small (≈25%, §1b) and none of it compiles off Apple platforms unchanged. Code-sharing options (A, C) therefore save at most part of the cheapest quarter of the work, and both require refactoring the Mac app.
2. All three top risks (§1d) are either shell problems (overlay E1/E2/D2/D6, hook C1/C2) or a verification problem (parity). Shell problems are cheapest to solve in the language with first-class Win32/WinRT access. The verification problem is solved by golden vectors under any option.
3. .NET maps every G row at confidence H and every shell row to an MIT library (§1a, §1c).

**UI framework: WPF, not WinUI 3.** The audit's shell mappings are WPF-based (A1, B3, B5, B7, B9, B10, E1, E3, E7). WPF supports layered, transparent, click-through, topmost windows via `AllowsTransparency` plus `GWL_EXSTYLE` (E1), and it deploys unpackaged with no Windows App SDK runtime. The one WinUI-only need is HLS playback for onboarding (E8). That is resolved by the E8 spike: a XAML Island `MediaPlayerElement`, or WebView2 + hls.js, or the browser as last resort. It does not justify WinUI as the shell framework. The audit's CPU warning about full-screen layered windows (E1c, risk 1) is answered by design, not by changing framework: **one small cursor-sized layered window that moves**, never one full-screen window per monitor.

**Layout:**

```
windows/
  global.json                      # pins SDK 10.0.1xx, rollForward latestFeature
  Directory.Build.props            # nullable, warnings-as-errors, common props
  Clicky.slnx
  src/Clicky.Core/                 # net10.0; no Windows references
  src/Clicky.Windows/              # net10.0-windows10.0.19041.0, UseWPF, SupportedOSPlatformVersion 10.0.18362.0
  tests/Clicky.Core.Tests/         # net10.0; runs the shared vectors; any OS runner
  tests/Clicky.Windows.Tests/      # net10.0-windows; replay end-to-end on a Windows runner
shared/
  golden/                          # JSON vectors, consumed by xUnit and XCTest
  prompts/                         # both system prompts as .txt (seam 10)
  fixtures/sse/                    # recorded Worker /chat streams for replay
```

- **.NET version:** .NET 10 LTS, supported to Nov 2028 (§1b). Do not start on .NET 8, which reaches end of support on 2026-11-10 (§1b). .NET 9 (STS) is also ruled out. `global.json` enforces the pin.
- **TFM and baseline:** `Clicky.Windows` compiles against the 19041 SDK for the WinRT projections (§1c) but declares 1903 (18362) as its minimum supported version. APIs that need 19041+ or 20348+ are runtime-guarded: `WDA_EXCLUDEFROMCAPTURE` (D2) and borderless capture (D1).
- **App manifest:** Per-Monitor-V2 DPI awareness (D6), `asInvoker`, `uiAccess="false"`.
- **Configuration:** `appsettings.json` holds `WorkerBaseUrl` (no placeholder hard-coded, per the audit's incidental findings), model ID and shortcut. User settings are JSON in `%LOCALAPPDATA%\Clicky\` (A7, A9).
- **macOS app:** untouched in v1, except for two changes:
  - (a) `shared/golden/*.json` plus one XCTest file that runs them against the existing internal functions via `@testable import`.
  - (b) both system prompts (`CompanionManager.swift:544-577`, `:950-962`) move into `shared/prompts/` and are loaded as bundle resources (§1b seam 10).
  - No other Swift refactors and no seam extraction in the app target. See "Divergences from audit".

## Consequences

**Positive**
- Every subsystem has a native, documented, MIT-licensed path (§1a, §1c), and nothing in the GPL/AGPL/SSPL family enters the build.
- `Clicky.Core` is testable on any runner, so parity logic gets CI coverage on day one, which the Swift code has never had (§0).
- The Mac app carries almost no risk: only resource files and a test file change.
- The Windows build drops onboarding friction: there are no Accessibility or Screen Recording grants (C4, D3, D4).

**Negative**
- **Two cores.** They are kept honest only by `shared/golden/`. Any change to POINT parsing, coordinate mapping, SSE parsing, the push-to-talk detector or the session reducer must update the vectors first, and both runners must pass. Timing behaviour (2.4/2.8/1.4 s fallbacks, transient hide) is only partly vector-testable and relies on the parity sign-off on a real Mac before the phase-8 gate (risk 3, STATUS blocker 1).
- **v1 feature drops**, each justified by the audit:
  - The Apple Speech fallback provider and its permission prompt (F8, F9). This is unreachable in the shipped configuration.
  - The `.shiftFunction` shortcut option, because PC `Fn` never reaches Windows (C3).
  - The Accessibility, Screen Recording and Screen Content permission rows and deep links (C4, D3, D4, B12).
  - The "Find App" button (B13).
  - Dead code I1–I5 and the unused bundle assets (incidental findings) are not ported.
- **Accepted Windows limitations:**
  - The buddy is `HWND_TOPMOST` but renders below Start, Notification Center, taskbar flyouts and the UAC secure desktop, and exclusive-fullscreen apps cover it (E1a, E1b).
  - `uiAccess` is out of scope. It would require a signed per-machine install under Program Files, and the per-user installer chosen below rules that out.
  - The hook is blind over elevated windows, because of UIPI (C1).
  - Win10 builds before 20348 flash a yellow capture border on each push-to-talk (D1). 1903/1909 hide the overlay for the capture frame because `WDA_EXCLUDEFROMCAPTURE` is unavailable (D2).
  - The tray icon starts in the Win11 overflow flyout, and onboarding tells the user to pin it (B1).
  - Opening the panel takes foreground focus (B3).
  - The mic gets no consent prompt when unpackaged, so the app detects denial and deep-links `ms-settings:privacy-microphone` (F4).
  - If the E2 spike fails, the buddy lives on a single virtual desktop.
- **Shortcut:**
  - The default is **left Ctrl + left Alt**: `VK_LCONTROL` + `VK_LMENU`, with the synthetic AltGr LCtrl (scan code `0x21D`) discarded (C1, risk 2). Requiring left-Alt alone already excludes AltGr, which is Right-Alt. The scan-code filter also covers AltGr held together with left keys.
  - **The shortcut is configurable from day one.** The options are the macOS set minus `.shiftFunction`: `controlOption` (default), `shiftControl`, `controlOptionSpace`, `shiftControlSpace` (`BuddyDictationManager.swift:17-22`, C3). Users on layouts where Ctrl+Alt still collides have an exit.
  - Copy says "Ctrl + Alt", not "control + option" (incidental findings).
- **Packaging: Velopack is primary; MSIX is not used in v1.**
  - Velopack produces a per-user `Setup.exe` with no admin rights, delta updates and an update API, all in one MIT tool (A4, A5, §1c).
  - It matches the unpackaged-Win32 assumptions behind A3 (Run key), A6 (no capability model) and D2.
  - MSIX would put borderless and programmatic capture behind *restricted* capabilities (A6), require a machine-trusted certificate for sideloading, and virtualize the startup path (A3).
  - Its one real advantage, a system mic consent prompt (F4), does not outweigh those costs. WiX is rejected (§1c).
- **Signing:**
  - Authenticode via `signtool`, preferably Azure Trusted Signing (OV certificate as fallback), applied to `Clicky.exe`, `Setup.exe` and update packages (A5).
  - New signing identities trigger SmartScreen's "Windows protected your PC" warning until reputation builds up (A5). The plan is a signed beta channel early, keeping the same identity across releases.
  - Do not buy EV for SmartScreen bypass. EV no longer grants instant reputation; re-verify this when tooling is pinned in phase 5.
- **Auto-update:**
  - On macOS, Sparkle is wired but its start call is commented out (`leanring_buddyApp.swift:53`), and `SUFeedURL` points at another product's feed, `julianjear/makesomething-mac-app` (A4).
  - Windows does not reuse `appcast.xml`. It uses Velopack's updater against a feed this project owns, kept disabled until signing and the feed exist.
  - Fixing the Mac feed is out of scope here. It is flagged so nobody re-enables Sparkle against a foreign feed.
- **Not changed, flagged:**
  - The Worker routes have no client authentication, and a Windows client widens the audience for that hole (incidental findings).
  - Analytics parity carries transcript and response text to PostHog (H1).
  - Both need a product-owner call and are listed under Approval.

## Tracer bullet (phase 3 builds this)

**Goal:** one vertical slice through the highest-risk chain (risks 1 and 2): shortcut → capture → Worker `/chat` → SSE → POINT-driven cursor on the correct monitor. Voice is deliberately replaced by a fixed prompt string.

**Location:** `windows/` as laid out above, plus the first vectors in `shared/golden/` and the first fixtures in `shared/fixtures/sse/`.

**IN**
1. **Hook (C1, C2).**
   - `WH_KEYBOARD_LL` installed on a dedicated thread with its own `GetMessage` loop. The callback is O(1): it enqueues to a `Channel<T>` and always calls `CallNextHookEx`.
   - Matches left Ctrl + left Alt only and filters the synthetic AltGr LCtrl by scan code. The hook turns raw key events into neutral modifier/key events, and `Clicky.Core.PushToTalkDetector` (seam 7) decides when the shortcut transitions.
   - A watchdog registers Raw Input (`RIDEV_INPUTSINK`) keyboard input on the same thread. If Raw Input sees a key-down that the hook missed within 500 ms, the hook is reinstalled.
2. **Capture (D1, D2, D5, D6).**
   - On press, enumerate monitors (`EnumDisplayMonitors`, PMv2 physical pixels) with the cursor's monitor first, matching macOS labelling (§1b, `CompanionScreenCaptureUtility`).
   - Per monitor: `Windows.Graphics.Capture` `CreateForMonitor`, take the first frame, dispose. Downscale to 1280 px max on the long edge and encode as JPEG at quality 0.8.
   - Own windows are excluded via `WDA_EXCLUDEFROMCAPTURE` on 19041+, or hidden for the capture frame below that.
3. **Request (G1, G2, seam 4, seam 10).**
   - `Clicky.Core.ClaudeRequestBuilder` builds the Messages body: the system prompt from `shared/prompts/companion-voice-response.txt`, the labelled images, and a fixed user text from `appsettings.json` (`TracerPromptText`).
   - The shell POSTs it to `{WorkerBaseUrl}/chat` through one shared `HttpClient` with `ResponseHeadersRead`.
4. **Replay mode.** `--replay <fixture.sse> [--event-delay-ms N]` swaps in a `ReplayHttpMessageHandler`. It streams the recorded file through the same `HttpClient` → `StreamReader.ReadLineAsync` path, so CI needs no keys and no network. `--record <fixture.sse>` tees a live response to disk.
5. **Parse (seams 2, 4).**
   - `Clicky.Core.SseLineParser` yields `content_block_delta` text.
   - `Clicky.Core.PointTag.Parse` runs on the completed text, ported verbatim from the regex at `CompanionManager.swift:786`.
   - `Clicky.Core.ScreenshotToScreenMapper` (seam 3, top-left origin, physical pixels) maps the point to virtual-screen coordinates. Missing or out-of-range `screenN` falls back to the cursor's screen (`CompanionManager.swift:640-646`).
6. **Render (E1, E6, D6).**
   - One small WPF window: `Topmost`, `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, holding a triangle cursor and a text bubble.
   - It appears at `GetCursorPos` on press and appends streamed text as tokens arrive, holding back any trailing partial `[POINT…` text.
   - At stream end it jumps to the mapped target (no bezier). It is repositioned with `SetWindowPos` in physical pixels, so WPF DIP conversion never touches the position, and it rescales its content on `WM_DPICHANGED`.
7. **Shared vectors.** `shared/golden/{point-tag,screen-mapping,sse-stream,push-to-talk}.json`, executed by `Clicky.Core.Tests`. The XCTest file executes `point-tag` and `push-to-talk` against `CompanionManager.parsePointingCoordinates` and `BuddyPushToTalkShortcut.shortcutTransition(for:keyCode:modifierFlagsRawValue:…)` (`BuddyDictationManager.swift:114`) when a Mac is available.
8. **CI.** Two jobs:
   - `Clicky.Core.Tests` on `ubuntu-latest`.
   - The full solution build plus `Clicky.Windows.Tests` replay end-to-end on `windows-latest`.

**OUT (explicitly):** microphone, transcription and TTS (rows F, G4). Also: the tray icon and panel (B), settings UI, shortcut configurability UI, bezier flight and animations (seam 9, E3–E5, E7), virtual desktops (E2), onboarding video (E8), analytics (H1), the session reducer (seam 6), conversation history, packaging and signing, and any macOS change beyond item 7 and the prompt files.

**Acceptance (pass/fail).** Run on a clean Windows 10 1903 VM, and repeat items 4, 6 and 7 on Windows 10 22H2 or Windows 11. Use two monitors at 100% and 150% scaling, 16:9, with the secondary monitor left of the primary so it has negative coordinates. If the hypervisor cannot present per-monitor DPI, items 7 and 8 run on a physical rig. "CI" means automated on the Windows runner.

1. **CI:** `Clicky.Core.Tests` passes on Linux and Windows. Every vector file has at least one case for each branch of the code under test: `[POINT:none]`, no tag, label without screen, screen out of range, and AltGr sequences.
2. **CI:** with `--replay point-basic.sse`, the app exits 0, and its JSON report shows the expected spoken text with the tag removed.
3. **VM, German (DE) layout:** typing `@` (AltGr+Q), `€` (AltGr+E) and `{` (AltGr+7) 20 times each in Notepad produces **zero** activations in the log, and all 60 characters appear correctly, which proves the hook is listen-only.
4. **VM, US and DE layouts:** left Ctrl + left Alt activates once per press, and release is reported. Right Ctrl + right Alt, and left Ctrl + right Alt, do not activate.
5. **VM:** after 10 minutes with no input, the shortcut still activates on the first press.
6. **VM:** `--debug-stall-hook-once` sleeps 2× `LowLevelHooksTimeout` in one callback, so Windows removes the hook. The watchdog logs a reinstall, and the shortcut activates again within the next two presses.
7. **VM:** every monitor returns a frame at the monitor's aspect ratio in which at least 5% of sampled pixels have a channel above 16, so the frame is not black. On 19041+ the overlay's pixels are absent from every frame. On 1903 the overlay is hidden during capture and reappears within 250 ms.
8. **VM, mixed DPI:**
   - Run replay fixtures `point-quarter-screen1.sse` and `point-quarter-screen2.sse` (`[POINT:320,180:…:screenK]` on a 1280×720 screenshot).
   - The cursor hotspot (read via `GetWindowRect` plus the fixed hotspot offset) lands within **4 physical px** of `rcMonitor.left + 0.25·width`, `rcMonitor.top + 0.25·height` of the right monitor. The expected value is computed independently of `ScreenshotToScreenMapper`.
   - This holds with the cursor starting on either monitor.
9. **CI and VM:** with `--replay … --event-delay-ms 200`, the first token is visible in the overlay within 300 ms of its event being delivered, and before the final event arrives. This proves rendering streams rather than buffers.
10. **VM, live:** with `WorkerBaseUrl` set to the deployed Worker, one shortcut press produces streamed text and a cursor move to the parsed target on the indicated monitor. A successful run is saved as a fixture via `--record`.
11. **VM:** the overlay never takes focus. A Notepad caret keeps typing throughout items 3–10, and clicks pass through the overlay to the window beneath it.

## Divergences from audit

- **Risk 3 mitigation is narrowed.** The audit says to extract seams 1–7 in Swift and run the vectors green on a Mac *before* porting (§1d risk 3). This ADR keeps the macOS app target untouched in v1 instead.
  - Swift-side execution covers only the functions that are already callable through `@testable import`: seam 2 (`parsePointingCoordinates`) and seam 7 (`shortcutTransition`, `:114`).
  - Vectors for seams 3, 4 and 6 are authored from the cited Swift line ranges and executed only by `Clicky.Core.Tests`. Running them on the Mac side waits for a decision to refactor the Mac app.
  - Cause: a v1 constraint adopted by this ADR (not by the product owner): keep the macOS app target untouched — see "Approval", where the alternative (the audit's original mitigation) is put to the product owner — plus the absence of a Mac host (STATUS blocker 1). The residual risk stays on the phase-8 Mac parity sign-off.
- No other divergence. The tracer-bullet watchdog chooses Raw Input as the liveness signal, which is one of the C2 options the audit names, not new analysis.

## Approval

**Product owner:** [x] Approve as written (2026-10-22) · [ ] Approve with amendments: ________________________________
Note: "as written" includes the divergence above — macOS seam-extraction (audit risk-3 mitigation) deferred out of v1, so only the POINT-parser and push-to-talk vectors run on Swift; the rest run on `Clicky.Core.Tests` only. Say so in amendments if you want the audit's original extraction-first path instead.

Open items for the product owner to note while approving: (1) Worker client authentication, and (2) whether transcript text stays in PostHog events (H1). Neither blocks phase 3.

Decision recorded on: 2026-10-22 by product owner (via ZCode PM session). Deferred with the decision: Worker client authentication (stays open), PostHog transcript/response text in analytics (parity carried over).

## Addendum — virtual-desktop spike verdict

Task T-005 (audit E2, parity PNT-07), recorded 2026-10-22 on Windows 11 Pro build 26300, one monitor, one virtual desktop.

**Verdict: ADOPTED** (provisional until the PNT-07 Manual-VM check passes; if it fails, this flips to FALLBACK and the buddy lives on a single virtual desktop, as accepted above).

**What ships.** `VirtualDesktopFollower` runs on every `EVENT_SYSTEM_FOREGROUND` (out-of-context `SetWinEventHook` on the dispatcher thread, live mode only). It asks the documented `IVirtualDesktopManager::GetWindowDesktopId` for the foreground window and the overlay. When both have a desktop and the two differ, it calls `MoveWindowToDesktop` with the foreground window's desktop id. Every COM call uses `PreserveSig`, and if the class cannot be created the app logs that and keeps going. No undocumented interface (the "pin to all desktops" one) is used.

**Verified on this machine (spike probe against the real COM object):**
- `CoCreateInstance(CLSID_VirtualDesktopManager)` succeeds, and no call threw.
- A plain WPF window, a `ShowInTaskbar=false` window and a taskbar-visible tool window each report the current desktop id.
- The overlay as built (`WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT`, topmost):
  - While hidden, `GetWindowDesktopId` fails with `0x8002802B`.
  - Once shown, it returns `S_OK` with `GUID_NULL`, and `IsWindowOnCurrentVirtualDesktop` returns TRUE.
  - `MoveWindowToDesktop` returns `S_OK`, but the id stays `GUID_NULL`.
- A style bisect isolates the cause: adding `WS_EX_TOOLWINDOW` alone, or `WS_EX_NOACTIVATE` alone (without `WS_EX_APPWINDOW`), gives `GUID_NULL`. In other words, the shell assigns the overlay to no desktop.
- A bogus desktop id and another process's window are both refused with a failure HRESULT, which is returned and logged rather than thrown.

**Proven by tests, not by a desktop switch:** `VirtualDesktopFollowTests` uses a fake service. It checks that the follower moves an off-desktop window to the foreground desktop, makes no call when the window is already there, and makes no call when either desktop id is unknown. Unknown covers the taskbar, the desktop, and the overlay's `GUID_NULL` case, where a move would be an `S_OK` no-op on every foreground change.

**Not verified:** no real Win+Ctrl+Arrow switch was performed. Doing so would have created or switched desktops in the developer's live session, so the spike stops at the API. The verdict therefore rests on an inference: the virtual-desktop shell hides only windows that are assigned to a desktop, so an overlay assigned to none stays visible on every desktop. This is inferred, not observed. For the overlay, the follower is consequently a no-op on this build. It is the safety net for builds or styles where the overlay does get a desktop id, and it is the mechanism for any desktop-bound window later (for example the phase-6 panel, if it is not a tool window).

**Flip rule (PNT-07):** 10 Win+Ctrl+Arrow switches in persistent cursor mode, with the buddy visible on the new desktop within 500 ms in 10/10. Any miss flips this to FALLBACK. The buddy is then recreated on the next push-to-talk, as on macOS (`CompanionManager.swift:485-489`).

## Addendum — onboarding video dropped

Task T-038 (audit E8, parity ONB-01 and ONB-05), recorded 2026-10-22.

**Owner decision 2026-10-23:** the onboarding video is dropped on Windows. There is no video host and no 40 s demo trigger, and `onboarding_video_completed` never fires.

- This supersedes the E8 spike named under "UI framework" above: no `MediaPlayerElement`, WebView2 + hls.js or browser fallback is built, and nothing in the app opens a video or a browser for onboarding.
- The onboarding music stays. `ff.mp3` ships as a content file beside `Clicky.exe` and plays at volume 0.3, holding until 90 s and fading to 0 by 93 s, as on the Mac (`CompanionManager.swift:240-282`). It starts only from the onboarding flow's Start and Watch Onboarding Again paths (T-039), never at launch.
- `OnboardingTimingTests.OwnerDecisionRecorded` pins this addendum and the matching ONB-01 and ONB-05 notes in `docs/02-parity-spec.md`.
