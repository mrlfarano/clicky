using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Clicky.Core;
using Windows.Graphics.Imaging;
using Windows.Win32;

namespace Clicky.Windows;

/// <summary>
/// The tracer bullet's interaction plumbing (ADR 001): capture every monitor, send the transcript to the
/// Worker's /chat, stream the reply into the bubble, jump the cursor to a POINT target, and write the report.
/// It decides nothing: <see cref="CompanionCoordinator"/> calls it when the session reducer asks for a chat
/// request or a point, and owns every state transition (T-012). Runs on the UI thread.
/// </summary>
internal sealed class TracerInteractionRunner(
    ClickyCommandLineOptions options,
    string systemPrompt,
    string claudeModel,
    CompanionSettings effectiveUserSettings,
    HttpClient workerHttpClient,
    Uri chatEndpoint,
    CursorOverlayWindow overlayWindow,
    MonitorScreenCapturer screenCapturer,
    IAnalyticsSink analyticsSink,
    ITextToSpeechSink textToSpeechSink,
    IFallbackSpeechSink fallbackSpeechSink)
{
    /// <summary>The monitors of the latest exchange, in report order, so its POINT tag maps onto the screens Claude saw.</summary>
    private IReadOnlyList<DisplayMonitor> latestExchangeMonitors = [];

    public TracerRunReport CreateReport() => new()
    {
        Mode = options.IsReplay ? "replay" : "live",
        // A manifest run names the manifest; a single-fixture run names the .sse file.
        Fixture = options.ReplayManifestPath ?? options.ReplayFixturePath,
        EffectiveSettings = EffectiveSettingsReport.FromCompanionSettings(effectiveUserSettings),
    };

    /// <summary>
    /// Captures, sends <paramref name="transcript"/> and streams the reply into the bubble. Returns the full
    /// response text, or null when the exchange failed and the failure is recorded in the report; cancellation
    /// by a newer press propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<string?> RunChatExchangeAsync(TracerRunReport report, string transcript, CancellationToken cancellationToken)
    {
        try
        {
            return await CaptureSendAndStreamAsync(report, transcript, cancellationToken);
        }
        catch (Exception interactionException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordInteractionFailure(report, interactionException);
            return null;
        }
    }

    /// <summary>Points at the tag's target on the latest exchange's monitors; failures are recorded like the exchange's.</summary>
    public async Task PointAtAsync(PointTagParseResult pointTag, TracerRunReport report, CancellationToken cancellationToken)
    {
        try
        {
            await PointAtTargetAsync(pointTag, (ScreenshotPoint)pointTag.Coordinate!, report, cancellationToken);
        }
        catch (Exception pointingException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordInteractionFailure(report, pointingException);
        }
    }

    public void WriteReport(TracerRunReport report)
    {
        CopySinkRecordsIntoReport(report);
        // The overlay's own SetWindowPos count, read after every path that could have moved the window, for
        // the stationary-frames-to-zero-calls guarantee (T-004).
        report.SetWindowPosCalls = overlayWindow.SetWindowPosCallCount;
        report.WriteTo(options.ReportPath);
        ClickyLog.Write($"report written: {options.ReportPath}");
    }

    private static void RecordInteractionFailure(TracerRunReport report, Exception interactionException)
    {
        report.Error = interactionException.Message;
        report.ExitCode = 1;
        ClickyLog.Write($"interaction failed: {interactionException}");
    }

    /// <summary>
    /// Snapshots what the app-lifetime stub sinks recorded into the report. A replay run writes one report,
    /// so per-run copying is exact; the real sinks (T-029, T-044) keep recording through the same interfaces.
    /// </summary>
    private void CopySinkRecordsIntoReport(TracerRunReport report)
    {
        report.AnalyticsEvents.AddRange(analyticsSink.RecordedEvents);
        report.TtsRequests.AddRange(textToSpeechSink.RecordedRequests);
        report.FallbackUtterances.AddRange(fallbackSpeechSink.RecordedUtterances);
    }

    private async Task<string> CaptureSendAndStreamAsync(TracerRunReport report, string transcript, CancellationToken cancellationToken)
    {
        PInvoke.GetCursorPos(out System.Drawing.Point cursorPositionPx);
        overlayWindow.ShowWithHotspotAt(cursorPositionPx);

        IReadOnlyList<DisplayMonitor> monitors = DisplayMonitorEnumerator.EnumerateCursorMonitorFirst(cursorPositionPx);
        if (options.DebugTriggerOnboardingDemo)
        {
            // ONB-02: the onboarding demo sends only the cursor's monitor, so Claude cannot pick something
            // on a different screen the demo cannot point at (performOnboardingDemoInteraction,
            // CompanionManager.swift:975-978).
            monitors = [monitors.First(monitor => monitor.IsCursorMonitor)];
        }
        latestExchangeMonitors = monitors;
        List<(LabeledScreenshot LabeledScreenshot, PixelSize ScreenshotSize)> capturedScreenshots =
            await CaptureLabeledScreenshotsAsync(monitors, report, cancellationToken);

        JsonObject requestBody = ClaudeRequestBuilder.BuildStreamingMessagesBody(
            // claudeModel is the effective user-settings model: loaded from the data-directory
            // settings.json (ONB-04/CLA-08) with its claude-sonnet-4-6 default, or a
            // --settings-override value when a parity scenario pins it.
            claudeModel,
            systemPrompt,
            capturedScreenshots.Select(capturedScreenshot => capturedScreenshot.LabeledScreenshot).ToList(),
            // shortcut: the coordinator does not hand the reducer's history down yet, so every request is a
            // first exchange; pass StartChatRequest.ConversationHistory here when that is wired.
            [],
            transcript);
        report.RequestBody = TracerRunReport.SummarizeRequestBody(
            requestBody,
            capturedScreenshots.Select(capturedScreenshot => capturedScreenshot.ScreenshotSize).ToList());

        string responseText = await StreamChatResponseIntoBubbleAsync(requestBody, report, cancellationToken);

        PointTagParseResult pointTag = PointTag.Parse(responseText);
        report.ResponseText = responseText;
        report.SpokenText = pointTag.SpokenText;
        // WS-11 privacy rule: the log records the spoken text's LENGTH only, never its content — the
        // crash report embeds the last log lines and must stay shareable without leaking the answer.
        ClickyLog.Write($"spoken text: {pointTag.SpokenText.Length} chars");
        if (pointTag.Coordinate is null)
        {
            ClickyLog.Write($"pointing: {pointTag.ElementLabel ?? "no tag"}");
        }
        return responseText;
    }

    private async Task<List<(LabeledScreenshot LabeledScreenshot, PixelSize ScreenshotSize)>> CaptureLabeledScreenshotsAsync(
        IReadOnlyList<DisplayMonitor> monitors,
        TracerRunReport report,
        CancellationToken cancellationToken)
    {
        // A scripted release can start a second exchange in one replay report; the report describes the latest one.
        report.Monitors.Clear();
        report.CaptureErrors.Clear();

        // Step 1: grab first frames. Below 19041 the overlay cannot be excluded from capture, so it is hidden,
        // and only for the grabs; encoding happens after it is back (ADR 001 acceptance 7: back within 250 ms).
        var capturedFrames = new SoftwareBitmap?[monitors.Count];
        bool mustHideOverlayForCapture = !overlayWindow.IsExcludedFromCapture;
        var overlayHiddenDuration = Stopwatch.StartNew();
        if (mustHideOverlayForCapture)
        {
            overlayWindow.Hide();
            // Wait for the next desktop composition, so the frame being captured no longer contains the overlay.
            PInvoke.DwmFlush();
            ClickyLog.Write("overlay hidden for capture");
        }
        try
        {
            for (int monitorIndex = 0; monitorIndex < monitors.Count; monitorIndex++)
            {
                try
                {
                    capturedFrames[monitorIndex] = await screenCapturer.CaptureFirstFrameAsync(monitors[monitorIndex], cancellationToken);
                }
                catch (Exception captureException) when (captureException is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // One failed monitor should not cost the user the answer: Claude still gets the other screens.
                    string captureError = $"screen {monitorIndex + 1} {monitors[monitorIndex].DeviceName}: {captureException.Message}";
                    report.CaptureErrors.Add(captureError);
                    ClickyLog.Write($"capture failed: {captureError}");
                }
            }
        }
        finally
        {
            if (mustHideOverlayForCapture)
            {
                overlayWindow.Show();
                ClickyLog.Write($"overlay shown after capture, hidden for {overlayHiddenDuration.ElapsedMilliseconds} ms");
            }
        }

        // Step 2: encode and label. Screen numbers count every monitor, including one whose capture failed,
        // so a POINT tag's screenN always means the same monitor.
        var capturedScreenshots = new List<(LabeledScreenshot LabeledScreenshot, PixelSize ScreenshotSize)>();
        for (int monitorIndex = 0; monitorIndex < monitors.Count; monitorIndex++)
        {
            DisplayMonitor monitor = monitors[monitorIndex];
            PixelSize screenshotSize = ScreenCapturePlanning.ComputeScreenshotSize(monitor.Bounds.Width, monitor.Bounds.Height);
            string screenLabel = ScreenCapturePlanning.BuildScreenLabel(monitorIndex, monitors.Count, monitor.IsCursorMonitor, screenshotSize);
            double? nonBlackPixelRatio = null;

            using SoftwareBitmap? capturedFrame = capturedFrames[monitorIndex];
            if (capturedFrame is not null)
            {
                nonBlackPixelRatio = MonitorScreenCapturer.MeasureNonBlackPixelRatio(capturedFrame);
                byte[] jpegBytes = await MonitorScreenCapturer.EncodeScaledJpegAsync(capturedFrame, screenshotSize);
                capturedScreenshots.Add((new LabeledScreenshot(jpegBytes, screenLabel), screenshotSize));
                ClickyLog.Write(
                    $"capture screen {monitorIndex + 1} {monitor.DeviceName}: frame {capturedFrame.PixelWidth}x{capturedFrame.PixelHeight}, " +
                    $"jpeg {screenshotSize.Width}x{screenshotSize.Height} {jpegBytes.Length} bytes, nonBlackPixelRatio {nonBlackPixelRatio:F3}");

                if (options.SaveCapturesDirectory is string saveCapturesDirectory)
                {
                    Directory.CreateDirectory(saveCapturesDirectory);
                    await File.WriteAllBytesAsync(Path.Combine(saveCapturesDirectory, $"screen{monitorIndex + 1}.jpg"), jpegBytes, cancellationToken);
                }
            }

            report.Monitors.Add(new MonitorReport(
                monitorIndex,
                monitor.DeviceName,
                monitor.Bounds,
                monitor.EffectiveDpi,
                monitor.IsCursorMonitor,
                screenshotSize,
                screenLabel,
                nonBlackPixelRatio));
        }
        return capturedScreenshots;
    }

    /// <summary>POSTs to /chat and reads the SSE stream line by line as it arrives (audit G1), updating the bubble per delta.</summary>
    private async Task<string> StreamChatResponseIntoBubbleAsync(JsonObject requestBody, TracerRunReport report, CancellationToken cancellationToken)
    {
        string requestJson = requestBody.ToJsonString();
        using var chatRequest = new HttpRequestMessage(HttpMethod.Post, chatEndpoint)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        ClickyLog.Write($"chat request: POST {chatEndpoint} ({requestJson.Length} chars)");

        // ResponseHeadersRead returns as soon as headers arrive; otherwise HttpClient buffers the whole stream.
        using HttpResponseMessage chatResponse = await workerHttpClient.SendAsync(chatRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!chatResponse.IsSuccessStatusCode)
        {
            string errorBody = await chatResponse.Content.ReadAsStringAsync(cancellationToken);
            // Same message shape as ClaudeAPI.swift:166-178.
            throw new HttpRequestException($"API Error ({(int)chatResponse.StatusCode}): {errorBody}");
        }

        await using Stream responseStream = await chatResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var responseReader = new StreamReader(responseStream, Encoding.UTF8);
        await using StreamWriter? recordWriter = options.RecordFixturePath is string recordFixturePath
            ? new StreamWriter(recordFixturePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" }
            : null;

        Stopwatch? sinceFirstEvent = null;
        string accumulatedResponseText = "";
        while (await responseReader.ReadLineAsync(cancellationToken) is string sseLine)
        {
            sinceFirstEvent ??= Stopwatch.StartNew();
            long eventTMs = sinceFirstEvent.ElapsedMilliseconds;
            report.LastEventTMs = eventTMs;
            if (recordWriter is not null)
            {
                await recordWriter.WriteLineAsync(sseLine);
            }

            SseLineParseResult parsedLine = SseLineParser.ParseLine(sseLine);
            if (parsedLine.Kind == SseLineKind.EndOfStream)
            {
                break;
            }
            if (parsedLine.Kind != SseLineKind.TextDelta)
            {
                continue;
            }

            accumulatedResponseText += parsedLine.TextDelta;
            if (ShowBubbleTextIfChanged(StreamingBubbleText.VisibleTextFor(accumulatedResponseText), report, eventTMs, sinceFirstEvent))
            {
                // Waiting at Render priority lets WPF lay out and render the change first, so the timeline
                // records when the text became visible, not just when it was set.
                await overlayWindow.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render, cancellationToken);
                report.DisplayTimeline[^1] = report.DisplayTimeline[^1] with { TMs = sinceFirstEvent.ElapsedMilliseconds };
            }
        }

        if (sinceFirstEvent is not null)
        {
            // At stream end, a fragment held back as a possible tag that never became one is spoken text after all.
            ShowBubbleTextIfChanged(PointTag.Parse(accumulatedResponseText).SpokenText, report, sinceFirstEvent.ElapsedMilliseconds, sinceFirstEvent);
        }
        ClickyLog.Write($"chat response: {accumulatedResponseText.Length} chars");
        return accumulatedResponseText;
    }

    private bool ShowBubbleTextIfChanged(string bubbleText, TracerRunReport report, long eventTMs, Stopwatch sinceFirstEvent)
    {
        string currentBubbleText = report.DisplayTimeline.Count == 0 ? "" : report.DisplayTimeline[^1].Text;
        if (bubbleText == currentBubbleText)
        {
            return false;
        }
        overlayWindow.SetBubbleText(bubbleText);
        report.DisplayTimeline.Add(new DisplayTimelineEntry(sinceFirstEvent.ElapsedMilliseconds, eventTMs, bubbleText));
        return true;
    }

    private async Task PointAtTargetAsync(
        PointTagParseResult pointTag,
        ScreenshotPoint screenshotPoint,
        TracerRunReport report,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DisplayMonitor> monitors = latestExchangeMonitors;
        // The cursor's monitor was sorted first, but look it up rather than rely on that.
        int cursorScreenIndex = monitors.ToList().FindIndex(monitor => monitor.IsCursorMonitor);
        int targetScreenIndex = ScreenshotToScreenMapper.SelectTargetScreenIndex(pointTag.ScreenNumber, monitors.Count, cursorScreenIndex);
        DisplayMonitor targetMonitor = monitors[targetScreenIndex];

        VirtualScreenPoint mappedTarget = ScreenshotToScreenMapper.MapToVirtualScreen(
            screenshotPoint, report.Monitors[targetScreenIndex].Screenshot, targetMonitor.Bounds);
        var targetPx = new System.Drawing.Point(
            (int)Math.Round(mappedTarget.X, MidpointRounding.AwayFromZero),
            (int)Math.Round(mappedTarget.Y, MidpointRounding.AwayFromZero));

        // No bezier flight in the tracer bullet: the cursor jumps straight to the target (ADR 001 tracer item 6).
        // The overlay matches the monitor by device name and places its content-sized window through Core's
        // OverlayPlacement, so the glyph's tip (the hotspot) lands exactly on the target (T-003/T-004).
        overlayWindow.PointAt(targetPx, targetMonitor.DeviceName);
        // Let a WM_DPICHANGED from crossing monitors, and the re-positioning after it, finish before reading back.
        await overlayWindow.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle, cancellationToken);

        System.Drawing.Point hotspotPx = overlayWindow.ReadHotspotPositionPx();
        string overlayMonitorDevice = overlayWindow.ReadCurrentMonitorDeviceName();
        report.Point = new PointReport(
            pointTag.ScreenNumber,
            targetScreenIndex,
            pointTag.ElementLabel,
            screenshotPoint,
            overlayMonitorDevice,
            new PhysicalPixelPointReport(targetPx.X, targetPx.Y),
            new PhysicalPixelPointReport(hotspotPx.X, hotspotPx.Y));
        ClickyLog.Write(
            $"pointing: \"{pointTag.ElementLabel}\" screen {targetScreenIndex + 1} ({targetMonitor.DeviceName}) " +
            $"target ({targetPx.X},{targetPx.Y}) hotspot ({hotspotPx.X},{hotspotPx.Y}) on {overlayMonitorDevice}");
    }
}
