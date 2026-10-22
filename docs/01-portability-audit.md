# 01 — macOS → Windows Portability Audit (Clicky)

Phase 1 deliverable. Author: Architect. Date: 2026-10-22.
Target: Windows 10 1903+ and Windows 11. The Cloudflare Worker (`worker/src/index.ts`) is shared infrastructure and stays as it is.

---

## 0. Inputs examined

**Substitution for `otool -L`.** This machine runs Windows and there is no built macOS binary (AGENTS.md also forbids terminal `xcodebuild`), so `otool -L` could not be run. The linked-framework list below comes from three sources instead: every `import` in every `.swift` file, the `project.pbxproj` build settings and SPM package references, and the `Info.plist` and entitlements keys. Frameworks that are only re-exported (CoreMedia via `CMTime`, QuartzCore via `CAMediaTimingFunction`) were inferred from symbol usage.

**Read in full:**
`AGENTS.md`; `leanring-buddy/Info.plist`; `leanring-buddy/leanring-buddy.entitlements`; `leanring_buddyApp.swift`, `CompanionManager.swift`, `MenuBarPanelManager.swift`, `GlobalPushToTalkShortcutMonitor.swift`, `CompanionScreenCaptureUtility.swift`, `WindowPositionManager.swift`, `OverlayWindow.swift`, `BuddyDictationManager.swift`, `BuddyTranscriptionProvider.swift`, `BuddyAudioConversionSupport.swift`, `AssemblyAIStreamingTranscriptionProvider.swift`, `OpenAIAudioTranscriptionProvider.swift`, `AppleSpeechTranscriptionProvider.swift`, `ClaudeAPI.swift`, `ElevenLabsTTSClient.swift`, `ElementLocationDetector.swift`, `ClickyAnalytics.swift`, `AppBundleConfiguration.swift`; `worker/src/index.ts`, `worker/package.json`, `worker/wrangler.toml`; `appcast.xml`; `leanring-buddyTests/leanring_buddyTests.swift`.

**Read in part or via targeted grep (platform-API and caller searches):**
`CompanionPanelView.swift` (lines 280–700 read; the rest grepped for `NS*`/`AV*`/`TextField`/`systemName`); `DesignSystem.swift` (grepped for `NSCursor`, `NSView`, `NSColor`, `onHover`, `systemName`); `CompanionResponseOverlay.swift` (lines 20–189); `OpenAIAPI.swift` (lines 1–60 plus caller search); `leanring-buddy.xcodeproj/project.pbxproj` (build settings 300–560, package refs 600–635); `scripts/release.sh` (grepped for toolchain steps).

**Not used as evidence:** `graft/leanring-buddy/*.md` cards (not needed; every claim below cites source). `worker/package-lock.json` was not license-scanned (see §3).

### Linked frameworks (substitute for `otool -L`)

| Framework | Evidence |
|---|---|
| Foundation | imported in 15 files |
| SwiftUI | `leanring_buddyApp`, `CompanionManager`, `CompanionPanelView`, `CompanionResponseOverlay`, `DesignSystem`, `MenuBarPanelManager`, `OverlayWindow` |
| AppKit | `BuddyDictationManager`, `CompanionResponseOverlay`, `CompanionScreenCaptureUtility`, `DesignSystem`, `ElementLocationDetector`, `GlobalPushToTalkShortcutMonitor`, `MenuBarPanelManager`, `OverlayWindow`, `WindowPositionManager` |
| AVFoundation (+ CoreMedia via `CMTime`, `CompanionManager.swift:853`) | 10 files |
| Combine | `BuddyDictationManager`, `CompanionManager`, `CompanionResponseOverlay`, `GlobalPushToTalkShortcutMonitor` |
| Speech | `AppleSpeechTranscriptionProvider`, `BuddyDictationManager` |
| ScreenCaptureKit | `CompanionManager`, `CompanionScreenCaptureUtility`, `WindowPositionManager` |
| CoreGraphics | `GlobalPushToTalkShortcutMonitor` (+ `CGPreflightScreenCaptureAccess` in `WindowPositionManager`) |
| ApplicationServices (HIServices / AX) | `WindowPositionManager` |
| ServiceManagement | `leanring_buddyApp` |
| QuartzCore (via AppKit) | `CAMediaTimingFunction`, `OverlayWindow.swift:825` |
| PostHog (SPM `posthog-ios` ≥ 3.0.0 < 4) | `ClickyAnalytics`, `CompanionManager`; `project.pbxproj:613-616` |
| Sparkle (SPM ≥ 2.9.0 < 3) | `leanring_buddyApp`; `project.pbxproj:605-608` |
| Testing / XCTest | test targets only |

**Build settings that matter for the port** (`project.pbxproj`): `MACOSX_DEPLOYMENT_TARGET = 14.2`; `SWIFT_VERSION = 5.0`; `SWIFT_DEFAULT_ACTOR_ISOLATION = MainActor` (:436, :474), which makes every unannotated type implicitly main-actor (a hidden threading assumption); `SWIFT_APPROACHABLE_CONCURRENCY = YES`; `ENABLE_APP_SANDBOX = NO`; `ENABLE_HARDENED_RUNTIME = YES`; `PRODUCT_BUNDLE_IDENTIFIER = com.yourcompany.leanring-buddy`.

**Test coverage: treat it as zero.** `leanring-buddyTests` holds three unit tests, all on `WindowPositionManager`'s pure permission-routing helpers, and that logic is dropped on Windows (§1, D3/C4). `leanring-buddyUITests` are Xcode template stubs. Nothing in the pipeline has a test: not the shortcut, transcription, Claude/SSE, POINT parsing, coordinate mapping, TTS, or overlay.

---

## Confidence legend

| Level | Meaning |
|---|---|
| **H** | A documented Windows API or library exists on the baseline (Win10 1903) and behaves the way the macOS code relies on. No prototype needed. |
| **M** | An equivalent exists, but it behaves differently in a user-visible way, needs a newer build (2004 / 20348 / Win11) for parity, or relies on a library whose fit must be confirmed. |
| **L** | No direct equivalent. Needs a workaround or redesign whose outcome is unknown. |

**Spike = Y** means the item must be prototyped in the phase-3 tracer bullet before the implementation plan commits to it.

---

## 1a. API / framework mapping table

Rows are grouped by subsystem. Every row cites the source location that uses the API. "Feature dropped" says what the user loses if no equivalent is adopted. "none" means the feature survives.

### A. App lifecycle, configuration, packaging

| # | macOS API (where used) | Closest Windows equivalent | Conf | Spike | Feature dropped |
|---|---|---|---|---|---|
| A1 | SwiftUI `App` + `@NSApplicationDelegateAdaptor`, empty `Settings` scene (`leanring_buddyApp.swift:14-26`) | WPF `Application` with `ShutdownMode=OnExplicitShutdown` and no `StartupUri` | H | N | none |
| A2 | `LSUIElement=true` (`Info.plist`) | No main window; all windows `ShowInTaskbar=False` / `WS_EX_TOOLWINDOW` | H | N | none |
| A3 | `SMAppService.mainApp.register()` (`leanring_buddyApp.swift:63-73`) | Unpackaged: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value. MSIX: `Windows.ApplicationModel.StartupTask.RequestEnableAsync` | H | N | none (user disables it in Settings › Apps › Startup instead of Login Items) |
| A4 | Sparkle `SPUStandardUpdaterController` (`leanring_buddyApp.swift:75-88`; the start call is **commented out** at `:53`), `SUFeedURL`/`SUPublicEDKey` (`Info.plist`), `appcast.xml`, `scripts/release.sh` (`sign_update`, `generate_appcast`) | NetSparkleUpdater (reads Sparkle-format appcast with Ed25519 signatures), or Velopack | H | N | none. Auto-update is inactive in the macOS build today, and `SUFeedURL` points to another product's feed (`julianjear/makesomething-mac-app`) |
| A5 | Hardened runtime, Developer ID, notarization, DMG (`project.pbxproj` `ENABLE_HARDENED_RUNTIME`; `release.sh`: `xcodebuild archive`, `create-dmg`, `notarytool`, `stapler`) | Authenticode via `signtool` (Azure Trusted Signing or OV/EV cert); installer from Velopack `Setup.exe` or MSIX | H | N | none. Builds signed with a new certificate show the SmartScreen "Windows protected your PC" warning until reputation builds up |
| A6 | Entitlements `app-sandbox=false`, `network.client`, `device.audio-input`, `device.camera`, `mach-lookup com.apple.screencapturekit.picker` (`leanring-buddy.entitlements`) | Unpackaged Win32: no capability model. MSIX: `runFullTrust`, `internetClient`, `DeviceCapability microphone`, restricted `graphicsCaptureProgrammatic` / `graphicsCaptureWithoutBorder` | H | N | none. `device.camera` is unused (no `.video` capture anywhere), so it is not carried over |
| A7 | `Bundle.main` Info.plist reads: `AppBundleConfiguration.stringValue` (`AppBundleConfiguration.swift:11-27`) for `VoiceTranscriptionProvider`, `OpenAIAPIKey`, `OpenAITranscriptionModel`; `CFBundleShortVersionString` (`leanring_buddyApp.swift:38`, `ClickyAnalytics.swift:28`) | `appsettings.json` next to the exe; version from `Assembly.GetEntryAssembly().GetName().Version` | H | N | none |
| A8 | `Bundle.main.url(forResource:"ff", withExtension:"mp3")` (`CompanionManager.swift:242`) | Content file under `AppContext.BaseDirectory`, or an embedded resource | H | N | none |
| A9 | `UserDefaults.standard` keys `selectedClaudeModel`, `isClickyCursorEnabled`, `hasCompletedOnboarding`, `hasSubmittedEmail`, `hasScreenContentPermission` (`CompanionManager.swift:111-150,345`), screen-recording key (`WindowPositionManager.swift:23`), `NSInitialToolTipDelay` (`leanring_buddyApp.swift:40`) | JSON settings file in `%LOCALAPPDATA%\Clicky\` (unpackaged) or `ApplicationData.Current.LocalSettings` (MSIX) | H | N | none |
| A10 | `NSApp.terminate` (`CompanionPanelView.swift:686`); `NSApplication.shared.activate(ignoringOtherApps:)` before permission prompts (`BuddyDictationManager.swift:360,390`) | `Application.Current.Shutdown()`; `SetForegroundWindow` (subject to foreground-lock rules; only needed under MSIX, since unpackaged apps get no prompt) | H | N | none |

### B. Menu bar item and control panel

| # | macOS API (where used) | Closest Windows equivalent | Conf | Spike | Feature dropped |
|---|---|---|---|---|---|
| B1 | `NSStatusBar.system.statusItem` + `button.action` (`MenuBarPanelManager.swift:63-72`) | `Shell_NotifyIconW` (`NIM_ADD`, `NOTIFYICON_VERSION_4`, `NIN_SELECT` via `uCallbackMessage`), wrapped by H.NotifyIcon.Wpf | H | N | none. Windows 11 puts new tray icons in the overflow (^) flyout by default, so the icon is hidden until the user pins it. Onboarding must tell them |
| B2 | Template icon drawn at runtime with `NSImage.lockFocus` + `NSBezierPath`, `isTemplate=true` (`MenuBarPanelManager.swift:76-108`) | Pre-rendered `.ico` (16/20/24/32 px) in light and dark variants, swapped on `WM_SETTINGCHANGE("ImmersiveColorSet")` by reading `HKCU\…\Themes\Personalize\SystemUsesLightTheme` | H | N | none |
| B3 | Borderless `NSPanel`, `.nonactivatingPanel` with `canBecomeKey=true`, `.floating`, `[.canJoinAllSpaces, .fullScreenAuxiliary]` (`MenuBarPanelManager.swift:23-25,146-176`) | WPF `Window` with `WindowStyle=None`, `AllowsTransparency=True`, `Topmost=True`, `ShowInTaskbar=False` | M | N | none. **Differs:** Windows has no window that is key without being active. The panel has an email `TextField` (`CompanionPanelView.swift:189`), and keyboard focus requires activation, so opening the panel takes the foreground from the user's app (normal for tray flyouts). `WS_EX_NOACTIVATE` would keep focus but break text entry |
| B4 | Panel anchored under the status item via `statusItem.button.window.frame` (`MenuBarPanelManager.swift:178-198`) | `Shell_NotifyIconGetRect` + `SHAppBarMessage(ABM_GETTASKBARPOS)` + `GetMonitorInfoW.rcWork`. Anchor above, below or beside depending on the taskbar edge | H | N | none |
| B5 | `NSEvent.addGlobalMonitorForEvents([.leftMouseDown,.rightMouseDown])` click-outside dismissal (`MenuBarPanelManager.swift:206-235`) | Dismiss on WPF `Window.Deactivated` / `WM_ACTIVATE(WA_INACTIVE)` (follows from B3); `WH_MOUSE_LL` only as a fallback | H | N | none |
| B6 | `NotificationCenter` `.clickyDismissPanel` (`MenuBarPanelManager.swift:17-19,43-49`; posted at `CompanionManager.swift:203,224,492`) | C# event, or `WeakReferenceMessenger` (CommunityToolkit.Mvvm) | H | N | none |
| B7 | SwiftUI panel views hosted in `NSHostingView` (`MenuBarPanelManager.swift:150`; `CompanionPanelView.swift` `Toggle(.switch)` :565, `TextField` :189, `Button(.plain)`, `.onHover` in `DesignSystem.swift`) | WPF XAML: styled `ToggleButton`, `TextBox`, `Button`; `MouseEnter`/`MouseLeave` | H | N | none |
| B8 | SF Symbols `Image(systemName:)` (11 uses in `CompanionPanelView.swift`, e.g. `"mic"`, `"eye"`, `"power"`, `"rectangle.dashed.badge.record"`; 4 in `DesignSystem.swift`) | Fluent UI System Icons, bundled (Segoe Fluent Icons ships only on Win11; Win10 has Segoe MDL2 Assets) | H | N | none |
| B9 | `NSCursor.pointingHand.push/pop`, `addCursorRect` in `NSViewRepresentable` (`DesignSystem.swift:286-588,761-815`) | WPF `Cursor="Hand"` / `Cursors.IBeam` (Win32 `IDC_HAND`, `IDC_IBEAM`) | H | N | none |
| B10 | `NSView.toolTip` via `NSViewRepresentable` (`DesignSystem.swift:822-840`) | WPF `ToolTip` + `ToolTipService.InitialShowDelay="0"` | H | N | none |
| B11 | `NSColor(...).usingColorSpace(.sRGB)` colour blending (`DesignSystem.swift:871-872`) | ARGB arithmetic on `System.Windows.Media.Color` | H | N | none |
| B12 | `NSWorkspace.shared.open` with `x-apple.systempreferences:…Privacy_Microphone / _ScreenCapture / _Accessibility / _SpeechRecognition` (`WindowPositionManager.swift:65,134`; `BuddyDictationManager.swift:834-848`; `CompanionPanelView.swift:472`) and an external URL (`CompanionPanelView.swift:648`) | `ShellExecuteW` / `Process.Start(UseShellExecute=true)` with `ms-settings:privacy-microphone` (and `https://…` URLs) | H | N | The Screen Recording and Accessibility deep links are dropped because Windows has no such settings pages (see C4, D3) |
| B13 | `NSWorkspace.activateFileViewerSelecting` "Find App" (`WindowPositionManager.swift:71-74`; `CompanionPanelView.swift:315`) | `SHOpenFolderAndSelectItems` | H | N | The "Find App" button is dropped. It only exists to help add the app to the macOS Accessibility list |

### C. Global push-to-talk shortcut

| # | macOS API (where used) | Closest Windows equivalent | Conf | Spike | Feature dropped |
|---|---|---|---|---|---|
| C1 | `CGEvent.tapCreate(.cgSessionEventTap, .headInsertEventTap, .listenOnly, flagsChanged\|keyDown\|keyUp)` + `CFMachPortCreateRunLoopSource` + `CFRunLoopAddSource(main)` (`GlobalPushToTalkShortcutMonitor.swift:30-84`) | `SetWindowsHookExW(WH_KEYBOARD_LL)` on a dedicated thread with its own `GetMessage` loop. Always `CallNextHookEx` (listen-only) | M | Y | none. **Differs in three ways.** (1) **AltGr collision:** on German, French, Polish and similar layouts, AltGr emits a synthetic Left-Ctrl plus Right-Alt. Ctrl+Alt detection would then fire push-to-talk whenever the user types `@`, `€` or `{`. The fix is to require `VK_LMENU` and ignore the fake LCtrl (scan code `0x21D`). (2) **UIPI:** the hook sees no keys while an elevated window has focus, unless Clicky is elevated or `uiAccess=true`. (3) There is no consent gate (see C4) |
| C2 | Re-enable on `.tapDisabledByTimeout` / `.tapDisabledByUserInput` (`GlobalPushToTalkShortcutMonitor.swift:104-109`) | No notification exists: a hook that exceeds `LowLevelHooksTimeout` is **silently removed**. Keep the callback O(1) (enqueue only), run a reinstall watchdog, or use Raw Input `RegisterRawInputDevices(RIDEV_INPUTSINK)` instead | M | Y | none |
| C3 | Modifier mapping `CGEventFlags` → `NSEvent.ModifierFlags` `.control/.option/.shift/.function`, keycode 49 = Space (`BuddyDictationManager.swift:54-199`) | `KBDLLHOOKSTRUCT.vkCode` with `VK_LCONTROL/VK_RCONTROL`, `VK_LMENU/VK_RMENU`, `VK_LSHIFT/VK_RSHIFT`, `VK_SPACE`; key state tracked in-hook | H | N | Shortcut option `.shiftFunction` is dropped: PC laptop `Fn` is handled in firmware and never reaches Windows. It is not the active option (`currentShortcutOption = .controlOption`, `:95`) |
| C4 | Accessibility gate `AXIsProcessTrusted` / `AXIsProcessTrustedWithOptions(kAXTrustedCheckOptionPrompt)` before the tap starts (`WindowPositionManager.swift:35-61`; `CompanionManager.swift:311-318`) | None required: low-level hooks need no user consent | H | N | The Accessibility permission row and prompt are dropped. User-visible: one fewer onboarding step and nothing to revoke |

### D. Screen capture and display geometry

| # | macOS API (where used) | Closest Windows equivalent | Conf | Spike | Feature dropped |
|---|---|---|---|---|---|
| D1 | `SCShareableContent.excludingDesktopWindows` + `SCScreenshotManager.captureImage(contentFilter:configuration:)`, `SCStreamConfiguration.width/height` = 1280 max (`CompanionScreenCaptureUtility.swift:31-97`; `CompanionManager.swift:363-372`) | `Windows.Graphics.Capture`: `IGraphicsCaptureItemInterop::CreateForMonitor` → `Direct3D11CaptureFramePool.CreateFreeThreaded` → `GraphicsCaptureSession.StartCapture`, take the first frame, then dispose. D3D11 device from `D3D11CreateDevice` (Vortice / CsWin32); downscale with WIC `IWICBitmapScaler` | M | Y | none. **Differs:** on every Windows 10 build before 20348 (1903 through 22H2), WGC draws a **yellow border** around the captured monitor while the session runs, so it flashes on each push-to-talk. Turning it off (`IsBorderRequired=false` plus `GraphicsCaptureAccess.RequestAccessAsync(Borderless)`) needs Windows 11. Borderless fallback: DXGI Desktop Duplication (`IDXGIOutput1::DuplicateOutput`), which has no border but also no window exclusion |
| D2 | `SCContentFilter(display:excludingWindows:)` excluding own windows by bundle ID (`CompanionScreenCaptureUtility.swift:40-45,81`) | `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` on overlay and panel HWNDs (Win10 2004 / build 19041+) | M | Y | On 1903/1909, `WDA_EXCLUDEFROMCAPTURE` is unsupported. The overlay must be hidden for the capture frame (visible flicker), or the blue cursor appears in the screenshot Claude sees |
| D3 | `CGPreflightScreenCaptureAccess` / `CGRequestScreenCaptureAccess` plus the cached-grant workaround (`WindowPositionManager.swift:79-130`) | No gate on Win10 for programmatic monitor capture. Win11: `GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Programmatic)` | M | N | The Screen Recording permission row is dropped. User-visible: no "Quit and reopen after granting" step (`CompanionPanelView.swift:352`) |
| D4 | "Screen Content" grant persisted after a dummy SC capture triggers the macOS picker consent (`CompanionManager.swift:353-396`; entitlement `com.apple.screencapturekit.picker`) | No analog. `GraphicsCapturePicker` picks a capture target per use; it is not a persistent consent | H | N | The Screen Content row is dropped; `allPermissionsGranted` (`CompanionManager.swift:102-104`) reduces to microphone only |
| D5 | `NSBitmapImageRep(cgImage:).representation(using: .jpeg, compressionFactor: 0.8)` (`CompanionScreenCaptureUtility.swift:99-101`) | WIC JPEG encoder (`Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId`, `ImageQuality = 0.8`) | H | N | none |
| D6 | `NSScreen.screens`, `.frame`, `.visibleFrame`, `deviceDescription["NSScreenNumber"]` → `CGDirectDisplayID` (`CompanionScreenCaptureUtility.swift:53-78`; `WindowPositionManager.swift:28-30,256-262`; `CompanionManager.swift:134,192,216,229,387,487`) | `EnumDisplayMonitors` + `GetMonitorInfoW` (`rcMonitor`, `rcWork`) + `GetDpiForMonitor`; `HMONITOR` as the display ID; `WM_DISPLAYCHANGE` for hot-plug. Process opts into `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` | H | N | none. **Differs:** AppKit uses bottom-left-origin points; the Windows virtual screen is top-left-origin physical pixels (under Per-Monitor-V2). The y-flip in `CompanionManager.swift:667-674` and `:1009` disappears, but each monitor's DPI scale must be applied when converting to WPF DIPs |
| D7 | `NSEvent.mouseLocation` (`CompanionScreenCaptureUtility.swift:38`; `OverlayWindow.swift:120,344,413,478,636`) | `GetCursorPos` (physical pixels under PMv2) | H | N | none |

### E. Cursor overlay

| # | macOS API (where used) | Closest Windows equivalent | Conf | Spike | Feature dropped |
|---|---|---|---|---|---|
| E1 | `OverlayWindow: NSWindow`: borderless, `level = .screenSaver`, `ignoresMouseEvents = true`, `canBecomeKey/Main = false`, one full-screen window per `NSScreen`, content in `NSHostingView` (`OverlayWindow.swift:14-53,779-808`) | Per-monitor WPF `Window` (`AllowsTransparency`, `Topmost`), extended styles set via `SetWindowLongPtrW(GWL_EXSTYLE)`: `WS_EX_LAYERED \| WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW` | M | Y | none. **Differs:** (a) `.screenSaver` puts the buddy above menus and popups. A Windows `HWND_TOPMOST` window renders **below** the Start menu, Notification Center, taskbar flyouts and the UAC secure desktop. Getting above them needs `uiAccess=true` (signed binary installed under Program Files). User-visible: the buddy vanishes behind Start or flyouts and cannot point inside them. (b) Exclusive-fullscreen D3D apps cover it. (c) WPF `AllowsTransparency` windows are software-composited layered windows, and full-screen copies on several 4K monitors redrawn at 60 Hz are CPU-heavy (see §4, risk 1) |
| E2 | `collectionBehavior [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary]` (`OverlayWindow.swift:29`; `MenuBarPanelManager.swift:169`) | No public API pins a window to all virtual desktops. `IVirtualDesktopManager` can only query and move windows, and "show on all desktops" uses an undocumented interface. Workaround: detect desktop switches and call `IVirtualDesktopManager::MoveWindowToDesktop` | L | Y | If the spike fails, the overlay shows only on the virtual desktop where it was created. After Win+Ctrl+→ the buddy is gone until the next push-to-talk recreates the windows (`CompanionManager.swift:485-489`) |
| E3 | SwiftUI rendering of the triangle, glow and bubbles: `Shape`, `.shadow`, `.scaleEffect`, `.rotationEffect`, spring `.animation`, `PreferenceKey` sizing (`OverlayWindow.swift:55-339`) | WPF `Path` + `DropShadowEffect` + `RenderTransform`; springs via custom easing or `Windows.UI.Composition` `SpringVector3NaturalMotionAnimation` | M | N | none (spring feel needs tuning) |
| E4 | `AngularGradient` spinner (`OverlayWindow.swift:749-774`) | No conic gradient brush in WPF or WinUI. Use a pre-rendered arc image rotated by `RotateTransform`, or Direct2D drawing | H | N | none |
| E5 | `TimelineView(.animation(minimumInterval: 1/36))` waveform (`OverlayWindow.swift:716-733`) | `CompositionTarget.Rendering` callback | H | N | none |
| E6 | 60 Hz cursor following and bezier flight driven by `Timer.scheduledTimer(0.016)` / `1/60` (`OverlayWindow.swift:411-443,495-568`) | `CompositionTarget.Rendering` (vsync-paced) + `GetCursorPos` | H | N | none |
| E7 | `NSAnimationContext.runAnimationGroup` + `CAMediaTimingFunction(.easeIn)` window alpha fade (`OverlayWindow.swift:819-835`) | `DoubleAnimation` on `Window.Opacity`, or `SetLayeredWindowAttributes(LWA_ALPHA)` | H | N | none |
| E8 | Onboarding HLS video: `AVPlayer(url: …m3u8)`, `AVPlayerLayer` in `NSViewRepresentable`, `addBoundaryTimeObserver` at 40 s (`CMTime`), `AVPlayerItem.didPlayToEndTimeNotification`, volume fades (`CompanionManager.swift:829-946`; `OverlayWindow.swift:844-881`) | `Windows.Media.Playback.MediaPlayer` with `AdaptiveMediaSource` (HLS), shown through `MediaPlayerElement` (WinUI 3 or a XAML Island inside WPF), or WebView2 + hls.js. 40 s trigger via `MediaPlaybackSession.PositionChanged`; end via `MediaPlayer.MediaEnded` | M | Y | none. WPF `MediaElement` cannot play HLS. Last-resort fallback: open the video in the browser with `ShellExecuteW`, losing the in-overlay presentation |

### F. Audio and speech

| # | macOS API (where used) | Closest Windows equivalent | Conf | Spike | Feature dropped |
|---|---|---|---|---|---|
| F1 | `AVAudioEngine.inputNode.installTap(bufferSize: 1024)`, `prepare/start/stop` (`BuddyDictationManager.swift:549-559`) | WASAPI shared-mode capture `IAudioClient` / `IAudioCaptureClient` (NAudio `WasapiCapture`); `IMMNotificationClient` for default-device changes | H | N | none |
| F2 | `AVAudioConverter` → PCM16 mono 16 kHz (`BuddyAudioConversionSupport.swift:11-70`) | NAudio `WdlResamplingSampleProvider` + `SampleToWaveProvider16`, or `MediaFoundationResampler` | H | N | none |
| F3 | `AVAudioPCMBuffer.floatChannelData` RMS level meter (`BuddyDictationManager.swift:687-721`) | Same math over the WASAPI float32 buffer | H | N | none |
| F4 | `AVCaptureDevice.authorizationStatus / requestAccess(for: .audio)` (`CompanionManager.swift:322,403-409`; `BuddyDictationManager.swift:256-263,790-810`; `CompanionPanelView.swift:468-470`) | Unpackaged: no prompt API. Detect with `Windows.Security.Authorization.AppCapabilityAccess.AppCapability.Create("microphone").CheckAccess()` where available, otherwise treat `E_ACCESSDENIED` from `IAudioClient::Initialize` as denied. MSIX: the system consent prompt appears on first `MediaCapture.InitializeAsync` | M | Y | none. **Differs:** unpackaged desktop apps **never get a consent dialog**. Access is governed by "Let desktop apps access your microphone" (Settings › Privacy). When that is off, capture fails or records silence with no prompt, so onboarding must detect it and deep-link `ms-settings:privacy-microphone` |
| F5 | `AVAudioPlayer(data:)` MP3 TTS playback, `isPlaying` polling, `stop()` (`ElevenLabsTTSClient.swift:65-80`; polled at `CompanionManager.swift:738-741`) | NAudio `Mp3FileReader` / `MediaFoundationReader` over a `MemoryStream` + `WasapiOut` (`PlaybackState`, `PlaybackStopped`) | H | N | none |
| F6 | `AVAudioPlayer(contentsOf: ff.mp3)` + `Timer` volume fade (`CompanionManager.swift:240-282`) | As F5, plus NAudio `VolumeSampleProvider` | H | N | none |
| F7 | `NSSpeechSynthesizer.startSpeaking` out-of-credits fallback (`CompanionManager.swift:761-766`) | `System.Speech.Synthesis.SpeechSynthesizer` (SAPI 5), or `Windows.Media.SpeechSynthesis.SpeechSynthesizer` | H | N | none |
| F8 | `SFSpeechRecognizer` + `SFSpeechAudioBufferRecognitionRequest` fed with engine buffers; `requiresOnDeviceRecognition`, `addsPunctuation` (`AppleSpeechTranscriptionProvider.swift:20-147`) | `System.Speech.Recognition.SpeechRecognitionEngine.SetInputToAudioStream` + `DictationGrammar` (SAPI: offline, accepts an external stream, lower accuracy). `Windows.Media.SpeechRecognition.SpeechRecognizer` cannot take external buffers (it owns the mic) and its dictation needs "Online speech recognition" turned on | L | N | **The Apple Speech fallback provider is dropped for v1.** It is reachable only when Info.plist sets `VoiceTranscriptionProvider=apple` (`BuddyTranscriptionProvider.swift:54`). AssemblyAI's `isConfigured` is hard-coded `true` (`AssemblyAIStreamingTranscriptionProvider.swift:27`), so the shipped config never falls back. User-visible impact in the default config: none |
| F9 | `SFSpeechRecognizer.authorizationStatus / requestAuthorization` (`BuddyDictationManager.swift:256-263,812-832`) | Not applicable (goes with F8) | H | N | The speech-recognition permission prompt is dropped along with F8 |

### G. Networking, concurrency, runtime (the core's hidden Apple dependencies)

| # | macOS API (where used) | Closest Windows equivalent | Conf | Spike | Feature dropped |
|---|---|---|---|---|---|
| G1 | `URLSession.bytes(for:)` + `AsyncBytes.lines` SSE parsing (`ClaudeAPI.swift:156-208`) | `HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)` + `StreamReader.ReadLineAsync` | H | N | none |
| G2 | `URLSession.data(for:)`; `URLSessionConfiguration` timeouts, `waitsForConnectivity`, `urlCache=nil`, `httpCookieStorage=nil` (`ClaudeAPI.swift:25-31`; `ElevenLabsTTSClient.swift:25-28`; `OpenAIAudioTranscriptionProvider.swift:103-107`; `CompanionManager.swift:166-172`) | One shared `HttpClient` over `SocketsHttpHandler` (`UseCookies=false`, `ConnectTimeout`); per-request `CancellationTokenSource.CancelAfter`. `waitsForConnectivity` becomes an explicit retry | H | N | none |
| G3 | TLS warm-up `HEAD` request + `NSLock` (`ClaudeAPI.swift:10-11,66-96`) | The same `HEAD` on the shared `HttpClient` (connections pooled by `SocketsHttpHandler`); C# `lock` | H | N | none |
| G4 | `URLSessionWebSocketTask` send/receive loop, `cancel(with: .goingAway)` (`AssemblyAIStreamingTranscriptionProvider.swift:158-240`) | `System.Net.WebSockets.ClientWebSocket` (`SendAsync` binary/text, `ReceiveAsync` loop, `CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable)` = 1001) | H | N | none |
| G5 | `JSONSerialization`, `JSONDecoder` (all API clients) | `System.Text.Json` | H | N | none |
| G6 | `NSRegularExpression` POINT-tag parser (`CompanionManager.swift:784-823`) | `System.Text.RegularExpressions.Regex` (the pattern at `:786` ports verbatim) | H | N | none |
| G7 | Swift concurrency: `Task`, cancellation, `Task.sleep`, `withChecked(Throwing)Continuation`, `@MainActor`, and `SWIFT_DEFAULT_ACTOR_ISOLATION = MainActor` (`project.pbxproj:436,474`) | `async`/`await` + `CancellationToken` + `TaskCompletionSource` + `Dispatcher.InvokeAsync` / `SynchronizationContext` | H | N | none. The implicit main-thread affinity has to become explicit marshaling |
| G8 | GCD: serial `DispatchQueue`s (`AssemblyAIStreamingTranscriptionProvider.swift:124-125`; `OpenAIAudioTranscriptionProvider.swift:76`), `DispatchWorkItem` + `asyncAfter` fallback deadlines (`BuddyDictationManager.swift:498-511`; `AssemblyAIStreamingTranscriptionProvider.swift:349-365`) | `Channel<T>` or `SemaphoreSlim(1,1)`; `Task.Delay(…, token)` | H | N | none |
| G9 | Combine `PassthroughSubject`, `@Published`, `ObservableObject`, `combineLatest`, `receive(on:)` (`GlobalPushToTalkShortcutMonitor.swift:16-24`; `CompanionManager.swift:422-471`; `BuddyDictationManager.swift:224-237`) | CommunityToolkit.Mvvm `ObservableObject` / `[ObservableProperty]` (`INotifyPropertyChanged`) + C# events | H | N | none |
| G10 | `Timer.scheduledTimer`: 1.5 s permission poll (`CompanionManager.swift:414-420`), prompt typing, fades | `DispatcherTimer` | H | N | none |

### H. Analytics

| # | macOS API (where used) | Closest Windows equivalent | Conf | Spike | Feature dropped |
|---|---|---|---|---|---|
| H1 | `PostHogSDK.shared.setup / capture / identify` (`ClickyAnalytics.swift:16-120`; `CompanionManager.swift:161-163`) | PostHog .NET SDK (`PostHog` NuGet), or a direct `POST https://us.i.posthog.com/i/v0/e/` | M | N | none. The events carry the full transcript and AI response text (`ClickyAnalytics.swift:83-96`), so parity carries that privacy behaviour over unless the product owner decides otherwise |

### I. Present in source but unreachable (do not port)

| # | macOS API (where used) | Closest Windows equivalent | Conf | Spike | Feature dropped |
|---|---|---|---|---|---|
| I1 | `CompanionResponseOverlayManager`: `NSPanel` at `.statusBar` level, `NSAnimationContext` (`CompanionResponseOverlay.swift:24-189`). Never instantiated (no references outside the file) | WPF topmost click-through window (if ever revived) | H | N | none (unreachable; not ported) |
| I2 | `ElementLocationDetector`: `NSImage`, `NSBitmapImageRep`, `NSGraphicsContext` (`ElementLocationDetector.swift:276-322`). Never instantiated, and it calls `api.anthropic.com` directly with a client-side key | WIC `IWICBitmapScaler` | H | N | none (unreachable; not ported, since it would also break the proxy-only key rule) |
| I3 | `OpenAIAPI` (`OpenAIAPI.swift`). Never instantiated | `HttpClient` | H | N | none (unreachable; not ported) |
| I4 | `WindowPositionManager.pinMainWindowToRight` / `shrinkOverlappingFocusedWindow`: `AXUIElementCopyAttributeValue`, `AXUIElementSetAttributeValue`, `NSWorkspace.frontmostApplication` (`WindowPositionManager.swift:157-251`). No callers | `GetForegroundWindow` + `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` + `SetWindowPos` | H | N | none (unreachable; not ported) |
| I5 | Mic-button dictation (`BuddyDictationManager.swift:294-306,324-326`) and the `NSEvent` overload of `shortcutTransition` (`:100-112`). No callers | Not applicable | H | N | none (unreachable; not ported) |

---

## 1b. Module boundary map

**Core** = business logic that can be expressed without OS UI or device APIs. **Shell** = OS integration. **Mixed** = one file holding both; it must be split.

| File (LOC) | Class | Core content | Shell content / hidden dependencies |
|---|---|---|---|
| `ClaudeAPI.swift` (291) | **CORE** | Messages request building, image MIME sniffing, SSE `content_block_delta` parser, history assembly | Transport is `URLSession` (`bytes(for:)` and `AsyncBytes.lines` are Darwin Foundation APIs), `JSONSerialization`, `NSLock` |
| `AssemblyAIStreamingTranscriptionProvider.swift` (478) | **CORE** | Token fetch, WS URL/query construction (`u3-rt-pro`, keyterms), turn-ordered transcript assembly (`:273-347`), ForceEndpoint/Terminate protocol, final-transcript grace/fallback timing | `URLSessionWebSocketTask`, GCD queues, and **`AVAudioPCMBuffer` in `appendAudioBuffer`** (`:182`), which pulls AVFoundation into the core via the protocol |
| `OpenAIAudioTranscriptionProvider.swift` (317) | **CORE** | Buffer-then-upload session, multipart body builder, prompt from keyterms | `URLSession`, GCD, `AVAudioPCMBuffer` (same protocol leak). Reachable only if `OpenAIAPIKey` is configured (it isn't today) |
| `BuddyTranscriptionProvider.swift` (100) | **CORE** | Provider protocol and factory/fallback order | Protocol signature `appendAudioBuffer(_: AVAudioPCMBuffer)` (`:13`) is the main hidden AVFoundation dependency; config comes from `Bundle` via `AppBundleConfiguration` |
| `BuddyAudioConversionSupport.swift` (108) | **MIXED** | `BuddyWAVFileBuilder` (`:72-108`), pure byte math | `BuddyPCM16AudioConverter` is `AVAudioConverter` (shell) |
| `AppBundleConfiguration.swift` (28) | **CORE** (trivial) | Trimmed key/value lookup | `Bundle.main`, `NSDictionary(contentsOfFile:)` |
| `ClickyAnalytics.swift` (121) | **MIXED** | Event catalogue: names and property keys | PostHog iOS SDK calls |
| `ElevenLabsTTSClient.swift` (81) | **MIXED** | Request body (`eleven_flash_v2_5`, voice settings), HTTP error mapping | `AVAudioPlayer` playback. The header comment says "streams", but the code downloads the full MP3 before playing (`:50,65`). Port the actual behaviour |
| `CompanionManager.swift` (1026) | **MIXED** | Voice state machine (`:17-22,430-540`), the two system prompts (`:544-577,950-962`), POINT parser (`:784-823`), screenshot → display coordinate mapping (`:648-675`, duplicated at `:999-1013`), 10-exchange history cap (`:686-694`), transient-hide rules (`:732-756`) | `NSScreen`, `AVPlayer`/`AVAudioPlayer`, `NSSpeechSynthesizer`, `SCShareableContent`, `AVCaptureDevice`, `UserDefaults`, `NotificationCenter`, SwiftUI `withAnimation`, Combine, `Timer` |
| `BuddyDictationManager.swift` (866) | **MIXED** | Push-to-talk detector `BuddyPushToTalkShortcut` (`:16-200`), session lifecycle/flags, draft composition, keyterm de-dupe, RMS smoothing | Typed on `NSEvent.ModifierFlags` / `CGEventType`; `AVAudioEngine`; `AVCaptureDevice` + `SFSpeechRecognizer` permissions; `NSWorkspace`; `NSApplication.activate` |
| `OverlayWindow.swift` (881) | **SHELL** (with extractable math) | Bezier/smoothstep/tangent/scale math (`:495-568`), navigation-mode state (`:90-97`) | `NSWindow`, `NSHostingView`, SwiftUI views, `NSEvent.mouseLocation`, `AVPlayerLayer`, `NSAnimationContext` |
| `CompanionScreenCaptureUtility.swift` (132) | **SHELL** | Screen labels and cursor-screen-first ordering (`:60-111`) | ScreenCaptureKit, `NSScreen`, `NSBitmapImageRep` |
| `GlobalPushToTalkShortcutMonitor.swift` (132) | **SHELL** | none (delegates to `BuddyPushToTalkShortcut`) | CGEvent tap, CFRunLoop, Combine |
| `MenuBarPanelManager.swift` (243) | **SHELL** | none | `NSStatusItem`, `NSPanel`, global event monitor |
| `CompanionPanelView.swift` (761) | **SHELL** | Copy strings, model IDs (`:610-611`) | SwiftUI, `NSWorkspace`, `AVCaptureDevice` |
| `DesignSystem.swift` (880) | **SHELL** | Colour and radius tokens, which port as data | SwiftUI, `NSCursor`, `NSView` tooltips, `NSColor` |
| `WindowPositionManager.swift` (262) | **SHELL** | `permissionRequestPresentationDestination` (pure, tested), which has no Windows use | AX, `CGPreflight…`, `NSWorkspace`, `NSScreen` |
| `AppleSpeechTranscriptionProvider.swift` (147) | **SHELL** | none | Speech framework (dropped, F8) |
| `leanring_buddyApp.swift` (89) | **SHELL** | none | SwiftUI `App`, `SMAppService`, Sparkle |
| `CompanionResponseOverlay.swift` (217) | **SHELL**, dead | none | `NSPanel` (I1) |
| `ElementLocationDetector.swift` (335) | dead | Computer-use resolution picking | AppKit imaging (I2) |
| `OpenAIAPI.swift` (142) | dead | none | none (I3) |

Of 7,637 Swift lines in 22 files, about 1.9k (≈25%) are portable core logic, going by the line ranges above. Every core file still depends on Foundation `URLSession`, GCD, Combine or `AVAudioPCMBuffer`, so **none of it compiles off-Apple unchanged**.

### Proposed extraction seams

1. **`PcmFrameSink` instead of `AVAudioPCMBuffer`.** Change `BuddyStreamingTranscriptionSession.appendAudioBuffer` (`BuddyTranscriptionProvider.swift:13`) to accept PCM16 mono 16 kHz bytes. Move conversion (`BuddyPCM16AudioConverter`) and RMS metering into the shell's audio capture. AssemblyAI and OpenAI then lose their AVFoundation dependency.
2. **`PointTag.parse(text) → (spokenText, x, y, label, screen)`**, a pure function lifted from `CompanionManager.swift:784-823`.
3. **`ScreenshotToScreenMapper.map(point, screenshotSize, displayBounds, originConvention)`**, merging the duplicated blocks at `CompanionManager.swift:648-675` and `:999-1013`. On Windows `originConvention = topLeft` and `displayBounds` is in physical pixels, so the AppKit y-flip goes away.
4. **`ClaudeStreamClient` = request builder + `SseLineParser` + transport interface.** The parser and builder are core; the transport is `HttpClient` / `URLSession`.
5. **`TtsClient` (fetch bytes) + `IAudioPlayer { Play(bytes), IsPlaying, Stop }`**, splitting `ElevenLabsTTSClient`.
6. **`CompanionSession` reducer.** Events `{ShortcutPressed, ShortcutReleased, TranscriptFinal, ResponseReady(text), TtsStarted, TtsEnded, PointingFinished, Error}` → `{voiceState, overlayCommand, history}`. This replaces the Combine wiring at `CompanionManager.swift:430-540` and the transient-hide polling at `:732-756`.
7. **`PushToTalkDetector`.** Lift `BuddyPushToTalkShortcut` (`BuddyDictationManager.swift:100-199`) onto a platform-neutral `{Ctrl, Alt, Shift, Fn}` modifier set plus key code, so the Win32 hook and the CGEvent tap both feed it.
8. **`AnalyticsEvents` catalogue + `IAnalyticsSink`**, splitting `ClickyAnalytics`.
9. **`FlightPath.sample(t, start, end) → (position, rotationDeg, scale)`**, lifted from `OverlayWindow.swift:495-568`.
10. **Prompts as resources.** Move both system prompts out of Swift string literals into shared text files.

**Shared golden test vectors** (JSON fixtures for seams 2, 3, 4, 6, 7 and the AssemblyAI turn assembly) should be checked in once and executed by both XCTest and the Windows test runner. This is the only way to show parity, given the coverage gap stated in §0.

### Target language/runtime for the Windows core: evaluation

| Option | Fit to this code | Interop with Windows shell | Shares code with the Mac app? | Risks | Verdict |
|---|---|---|---|---|---|
| **C# / .NET 10 (LTS to Nov 2028)** | Every G-row maps 1:1 to the BCL (`HttpClient`, `ClientWebSocket`, `System.Text.Json`, `Regex`, `Channel<T>`); the H-confidence rows dominate | Native: WPF, CsWin32 P/Invoke, built-in WinRT projections (`net10.0-windows10.0.19041.0` TFM), NAudio | No. The core logic is duplicated (about 1.9k Swift lines → C#). Golden vectors limit drift | Two cores to maintain. .NET 8 reaches end of support on 2026-11-10, so do **not** start on .NET 8 | **Recommended.** Put the core in `Clicky.Core` (`net10.0`, no Windows references, testable on any CI runner) and the shell in `Clicky.Windows` (`net10.0-windows`) |
| **Swift on Windows** (swift.org toolchain) | The core could be reused almost verbatim, except that Combine is Apple-only, `URLSession.bytes(for:)`/`AsyncBytes` and WebSocket support in swift-corelibs-foundation must be verified, and `AVAudioPCMBuffer` must go (seam 1) | Poor. There is no first-party Windows UI; you need swift-winrt bindings or a C ABI bridge to a C# shell, plus thin debugger/IDE/packaging support | Yes, after seams 1, 6 and 9 remove Combine and AVFoundation | Toolchain maturity on Windows, hiring, and a Swift↔C# bridge across async callbacks (transcript updates, SSE chunks) | Not for v1. Revisit only if code sharing outranks delivery risk |
| **Rust** core + UniFFI bindings (Swift and C#) | Good for websockets/SSE (`tokio-tungstenite`, `reqwest`); needs a rewrite of the core | Good via `windows-rs`, but you still need a C# or Rust UI | Yes, but only if the Mac app also moves to the Rust core | Rewriting the core on both platforms; async/callback FFI design; a new language for a Swift-only repo; UniFFI is MPL-2.0 (§3) | The best long-term shared-core option and the slowest route to a first Windows build. Do not choose for the port itself |
| **TypeScript** (Electron or Tauri shell) | The Worker is already TS; HTTP/WS/SSE are trivial | Electron covers the overlay (`transparent`, `setIgnoreMouseEvents`, `alwaysOnTop('screen-saver')`), capture (`desktopCapturer`) and exclusion (`setContentProtection` → `WDA_EXCLUDEFROMCAPTURE`). `globalShortcut` cannot do modifier-only combos, so a native hook addon is needed (the usual one bundles libuiohook; see §3) | Potentially, if the Mac app is replaced as well | About 150 MB+ resident for a tray utility; per-monitor transparent windows under mixed DPI are a known weak spot; mic needs `getUserMedia` + AudioWorklet resampling | Only if the product goal becomes "one codebase for both OSes". Not a native port |

---

## 1c. Dependency license check

License identifiers come from the upstream projects' published LICENSE files as of this audit. A live GitHub license-API check was attempted this session but not approved. **Re-verify each identifier when versions are pinned in phase 5.**

### Current dependencies

| Dependency | Where | License | Copyleft risk | OK? |
|---|---|---|---|---|
| PostHog `posthog-ios` ≥ 3.0.0 | SPM (`project.pbxproj:613`) | MIT | None | Yes |
| Sparkle ≥ 2.9.0 | SPM (`project.pbxproj:605`) | MIT (bundles permissively licensed components, e.g. ed25519, bsdiff) | None | Yes |
| `wrangler` ^3.0.0 | `worker/package.json` devDependency | MIT OR Apache-2.0 | None | Yes. The transitive tree in `worker/package-lock.json` was **not** scanned; run `npx license-checker` in `worker/` to close this out |
| Worker runtime deps | `worker/src/index.ts` | None: the file imports nothing | None | Yes |

### Windows-side libraries recommended in §1a

| Library | Used for (rows) | License | Copyleft risk | OK? |
|---|---|---|---|---|
| .NET 10 runtime / BCL (`HttpClient`, `ClientWebSocket`, `System.Text.Json`, `Regex`) | G1–G10 | MIT | None | Yes |
| WPF | A1, B3, B7, E1, E3 | MIT | None | Yes |
| `System.Speech` (NuGet, dotnet/runtime) | F7, F8 | MIT | None | Yes |
| H.NotifyIcon.Wpf | B1 | MIT | None | Yes |
| CommunityToolkit.Mvvm | B6, G9 | MIT | None | Yes |
| NAudio | F1, F2, F5, F6 | MIT | None | Yes |
| Microsoft.Windows.CsWin32 | C1, D1, D2, D6, E1 P/Invoke generation | MIT | None | Yes |
| C#/WinRT (CsWinRT, used by the `-windows10.0.19041.0` TFM) | D1, E8, F4 | MIT | None | Yes |
| Vortice.Windows (Direct3D11/DXGI) | D1 device creation | MIT | None | Yes |
| PostHog .NET SDK | H1 | MIT | None | Yes |
| NetSparkleUpdater, **or** Velopack | A4, A5 | MIT (each) | None | Yes |
| Fluent UI System Icons | B8 | MIT | None | Yes |
| Windows App SDK (only if E8 uses WinUI 3 `MediaPlayerElement`) | E8 | MIT source; the redistributable runtime is under Microsoft Software License Terms | None (proprietary but freely redistributable) | Yes |
| WebView2 Runtime + hls.js (only if E8 uses the WPF/WebView2 path) | E8 | Runtime: Microsoft proprietary, free redistribution; hls.js: Apache-2.0 | None | Yes |
| OS components: Win32, `Windows.Graphics.Capture`, WASAPI, Media Foundation, WIC, SAPI | many | Part of Windows; nothing to redistribute | None | Yes |

### Flagged: considered and rejected, or conditional

| Library | Why it came up | License | Risk |
|---|---|---|---|
| LibVLCSharp / libVLC | Alternative HLS player for E8 | LGPL-2.1+ | **Weak copyleft.** Not recommended; use Windows.Media or WebView2 instead |
| WiX Toolset | Alternative MSI installer for A5 | MS-RL (reciprocal); v6+ also requires the Open Source Maintenance Fee for commercial use | **Reciprocal license + fee.** Prefer Velopack or MSIX |
| UniFFI | Only if the Rust core option is chosen | MPL-2.0 | File-level weak copyleft; acceptable if unmodified |
| libuiohook (bundled by `uiohook-napi`) | Only if the Electron option is chosen (modifier-only hotkey) | GPL-3.0 / LGPL-3.0 family | **Copyleft.** Avoid; write a small `WH_KEYBOARD_LL` addon instead |

**Result: no GPL, AGPL or SSPL dependency in the current set or in the recommended Windows set.**

---

## 1d. Top 3 porting risks (ranked)

1. **Overlay fidelity (E1, E2, D2, D6).** *Impact:* pointing is the product's distinctive feature, and on Windows the buddy renders below Start, flyouts and UAC, can drop off virtual desktops, costs a lot of CPU as full-screen layered windows on multi-4K setups, gets its DPI mapping wrong on mixed-DPI monitors, and can leak into screenshots on 1903/1909. *Mitigation (spike + redesign):* the phase-3 tracer bullet proves PMv2 coordinate mapping with POINT golden vectors on a mixed-DPI two-monitor rig. Redesign the overlay as **one small cursor-sized layered window that moves** (as `CompanionResponseOverlay.swift:120-153` already does on macOS) instead of one full-screen window per monitor. Use `WDA_EXCLUDEFROMCAPTURE` on 2004+ and hide-before-capture below that. Accept and document the shell-band z-order limit; `uiAccess` stays out of scope.
2. **Ctrl+Alt push-to-talk via `WH_KEYBOARD_LL` (C1, C2).** *Impact:* AltGr false triggers make the app unusable for typists on international layouts. Silent hook removal makes push-to-talk stop working with no error, and UIPI blinds it over elevated windows. *Mitigation (spike + redesign the default):* match left-Ctrl + left-Alt only, filtering the synthetic AltGr LCtrl (scan code `0x21D`); run the hook on a dedicated thread with an O(1) callback and a reinstall watchdog; compare against Raw Input in the spike; make the shortcut user-configurable from day one.
3. **Unverifiable behavioural parity (§0 coverage, §1b mixed files).** *Impact:* the pipeline's timing behaviour (2.4 / 2.8 / 1.4 s finalize fallbacks, transient hide, cancel-on-repress) lives in untested, mixed core/shell files, and there is no running macOS reference here (`docs/STATUS.md` blocker 1). A Windows rewrite will regress silently. *Mitigation (redesign before porting):* extract seams 1–7 and write the shared golden vectors **first**, run them green against the Swift code on a Mac, then implement `Clicky.Core` against the same vectors. Parity scenarios are signed off on a real Mac before the phase-8 gate.

---

## Incidental findings that affect the port

- The Worker base URL is a hard-coded placeholder in two places (`CompanionManager.swift:73`, `AssemblyAIStreamingTranscriptionProvider.swift:22`). Make it configuration on Windows.
- The Worker routes have no client authentication (`worker/src/index.ts:19-48`). Anyone holding the URL can spend the API credits. A Windows client doesn't change that, but it widens the audience.
- The bundle assets `enter.mp3`, `eshop.mp3`, `steve.jpg` and `codex-add-project.png` (in `leanring-buddy/`) have no references in Swift source. Do not carry them over.
- The onboarding prompt hard-codes "press control + option" (`CompanionManager.swift:897`), and the panel uses Mac key names (`BuddyDictationManager.swift:24-52`). The Windows copy needs "Ctrl + Alt".
