using Xunit;

namespace Clicky.Core.Tests;

/// <summary>
/// Pins the pointing sequence of task T-042 (parity PNT-06, TTS-02) on a virtual clock: the flight out, the
/// bubble, the 3 s hold, the flight back and its cursor-move cancel (OverlayWindow.swift:411-672), the
/// idle-before-target ordering (CompanionManager.swift:633-636, 678), and the 0.4 s overlay fade (:819).
/// Expected times and positions are hand-computed from those rules.
/// </summary>
public sealed class PointingSequenceTests
{
    [Fact]
    public void FlyBubbleHoldReturn()
    {
        var pointingSequence = new PointingSequence(new FixedRandom());

        // Buddy at (100, 100) → target (900, 700): 1000 DIP / 800 = 1.25 s, inside the 0.6–1.4 s clamp.
        // The cursor stays at (100, 100), so the flight back aims 35/25 DIP beside it at (135, 125).
        IReadOnlyList<PointingCommand> startCommands = pointingSequence.Start(
            TimeSpan.Zero,
            buddyPositionPx: new VirtualScreenPoint(100, 100),
            targetPx: new VirtualScreenPoint(900, 700),
            cursorPositionPx: new VirtualScreenPoint(100, 100),
            dpiScale: 1.0);
        Assert.Equal(new VirtualScreenPoint(100, 100), Assert.IsType<PointingCommand.MoveBuddy>(Assert.Single(startCommands)).PositionPx);

        List<(TimeSpan StartedAt, PointingSequencePhase Phase)> phaseTimeline = [(TimeSpan.Zero, pointingSequence.Phase)];
        List<PointingCommand> allCommands = [.. startCommands];
        for (int elapsedMilliseconds = 1; elapsedMilliseconds <= 8000; elapsedMilliseconds++)
        {
            TimeSpan now = TimeSpan.FromMilliseconds(elapsedMilliseconds);
            allCommands.AddRange(pointingSequence.Advance(now));
            if (pointingSequence.Phase != phaseTimeline[^1].Phase)
            {
                phaseTimeline.Add((now, pointingSequence.Phase));
            }
            if (elapsedMilliseconds == 625)
            {
                // Mid-flight: eased progress 0.5 on the arc whose control point is 80 DIP above (500, 400):
                // 0.25·(100,100) + 0.5·(500,320) + 0.25·(900,700) = (500, 360), with the 1.3x scale peak.
                var midFlightMove = Assert.IsType<PointingCommand.MoveBuddy>(allCommands[^1]);
                Assert.Equal(500, midFlightMove.PositionPx.X, precision: 9);
                Assert.Equal(360, midFlightMove.PositionPx.Y, precision: 9);
                Assert.Equal(1.3, midFlightMove.Scale, precision: 9);
            }
        }

        Assert.Equal(
            [
                PointingSequencePhase.FlyingToTarget,
                PointingSequencePhase.StreamingBubbleText,
                PointingSequencePhase.HoldingAtTarget,
                PointingSequencePhase.FadingOutBubble,
                PointingSequencePhase.ReturningToCursor,
                PointingSequencePhase.FollowingCursor,
            ],
            phaseTimeline.Select(phaseStart => phaseStart.Phase));
        double flyToTargetSeconds = phaseTimeline[1].StartedAt.TotalSeconds;
        Assert.InRange(flyToTargetSeconds, 0.6, 1.4);
        Assert.Equal(1.25, flyToTargetSeconds, precision: 3);
        // FixedRandom picks "right here!" (11 characters) at 30 ms each: 0.33 s of streaming.
        Assert.Equal(0.33, (phaseTimeline[2].StartedAt - phaseTimeline[1].StartedAt).TotalSeconds, precision: 3);
        Assert.Equal(3.0, (phaseTimeline[3].StartedAt - phaseTimeline[2].StartedAt).TotalSeconds, precision: 3);
        Assert.Equal(0.5, (phaseTimeline[4].StartedAt - phaseTimeline[3].StartedAt).TotalSeconds, precision: 3);
        // (900, 700) → (135, 125): hypot(765, 575) ≈ 957.0005 DIP / 800 ≈ 1.196 s.
        double returnSeconds = (phaseTimeline[5].StartedAt - phaseTimeline[4].StartedAt).TotalSeconds;
        Assert.InRange(returnSeconds, 1.196, 1.198);

        // The landing on the target, the streamed bubble, its fade, then the landing beside the cursor and the clear.
        Assert.Contains(new PointingCommand.MoveBuddy(new VirtualScreenPoint(900, 700), RotationDegrees: -35, Scale: 1), allCommands);
        Assert.Equal(
            ["r", "ri", "rig", "righ", "right", "right ", "right h", "right he", "right her", "right here", "right here!"],
            allCommands.OfType<PointingCommand.ShowBubbleText>().Select(showBubbleText => showBubbleText.VisibleBubbleText));
        Assert.Equal(new PointingCommand.FadeOutBubble(TimeSpan.FromSeconds(0.5)), Assert.Single(allCommands.OfType<PointingCommand.FadeOutBubble>()));
        Assert.Equal(
            [
                new PointingCommand.MoveBuddy(new VirtualScreenPoint(135, 125), RotationDegrees: -35, Scale: 1),
                new PointingCommand.FinishPointing(WasReturnCancelledByCursorMove: false),
            ],
            allCommands.TakeLast(2));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void MoveOver100ptCancelsReturn(double dpiScale)
    {
        var pointingSequence = new PointingSequence(new FixedRandom());
        pointingSequence.Start(
            TimeSpan.Zero,
            buddyPositionPx: new VirtualScreenPoint(100, 100),
            targetPx: new VirtualScreenPoint(1500, 900),
            cursorPositionPx: new VirtualScreenPoint(100, 100),
            dpiScale,
            customBubbleText: "");

        // Forward flight and hold ignore the cursor (OverlayWindow.swift:416-418); the flight back measures
        // from where the cursor is when it begins (:640), here (600, 100).
        Assert.Empty(pointingSequence.CursorMoved(new VirtualScreenPoint(600, 100)));
        Assert.Empty(pointingSequence.Advance(TimeSpan.FromSeconds(3)).OfType<PointingCommand.FinishPointing>());
        Assert.Empty(pointingSequence.CursorMoved(new VirtualScreenPoint(1600, 100)));
        Assert.Empty(pointingSequence.CursorMoved(new VirtualScreenPoint(600, 100)));

        // At most 1.4 s out + 3 s hold + 0.5 s fade = 4.9 s, and the flight back takes at least 0.6 s.
        pointingSequence.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(PointingSequencePhase.ReturningToCursor, pointingSequence.Phase);

        // 99 pt and exactly 100 pt do not cancel. At 150% that is 148.5 and 150 px, well past a raw 100 px.
        Assert.Empty(pointingSequence.CursorMoved(new VirtualScreenPoint(600 + 99 * dpiScale, 100)));
        Assert.Empty(pointingSequence.CursorMoved(new VirtualScreenPoint(600, 100 + 100 * dpiScale)));
        Assert.Equal(PointingSequencePhase.ReturningToCursor, pointingSequence.Phase);

        Assert.Equal(
            [new PointingCommand.FinishPointing(WasReturnCancelledByCursorMove: true)],
            pointingSequence.CursorMoved(new VirtualScreenPoint(600 + 101 * dpiScale, 100)));
        Assert.Equal(PointingSequencePhase.FollowingCursor, pointingSequence.Phase);
        Assert.Empty(pointingSequence.Advance(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void IdleBeforeTargetPublished()
    {
        CompanionSessionState awaitingChatResponseState = CompanionSessionState.Initial with
        {
            VoiceState = CompanionVoiceState.Processing,
            ResponsePhase = CompanionResponsePhase.AwaitingChatResponse,
            LatestChatRequestId = 1,
            InFlightTranscript = "where do i search",
        };

        CompanionSessionTransition transition = CompanionSession.Reduce(
            awaitingChatResponseState,
            new CompanionSessionEvent.ResponseReady(1, "right up here in the search bar [POINT:640,360:search bar]"));

        // The state that publishes the target is already idle, so the triangle, not the spinner, is drawn for the flight.
        Assert.Equal(CompanionVoiceState.Idle, transition.State.VoiceState);
        Assert.True(transition.State.IsPointingAtElement);
        List<CompanionSessionEffect> effects = [.. transition.Effects];
        int pointAtElementIndex = effects.FindIndex(effect => effect is CompanionSessionEffect.PointAtElement);
        int elementPointedIndex = effects.FindIndex(effect => effect is CompanionSessionEffect.TrackAnalyticsEvent { EventName: "element_pointed" });
        Assert.InRange(pointAtElementIndex, 0, elementPointedIndex - 1);

        var elementPointedEffect = (CompanionSessionEffect.TrackAnalyticsEvent)effects[elementPointedIndex];
        AnalyticsEvent elementPointedEvent = new AnalyticsEvents().ElementPointed(elementPointedEffect.EventContent);
        Assert.Equal("search bar", (string?)elementPointedEvent.EventProperties["element_label"]);

        // Pointing starts from the published target and the first frame is the buddy taking off.
        var pointAtElement = (CompanionSessionEffect.PointAtElement)effects[pointAtElementIndex];
        Assert.Equal(new ScreenshotPoint(640, 360), pointAtElement.PointTagParseResult.Coordinate);
        var pointingSequence = new PointingSequence(new FixedRandom());
        IReadOnlyList<PointingCommand> startCommands = pointingSequence.Start(
            TimeSpan.Zero,
            buddyPositionPx: new VirtualScreenPoint(100, 100),
            targetPx: new VirtualScreenPoint(640, 360),
            cursorPositionPx: new VirtualScreenPoint(100, 100),
            dpiScale: 1.0);
        Assert.Equal(PointingSequencePhase.FlyingToTarget, pointingSequence.Phase);
        Assert.IsType<PointingCommand.MoveBuddy>(Assert.Single(startCommands));
    }

    [Fact]
    public void FadeIs400ms()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(400), PointingSequence.OverlayHideFadeOutDuration);

        // Catches drift if the Swift default at OverlayWindow.swift:819 changes.
        string overlayWindowSource = File.ReadAllText(
            Path.Combine(GoldenVectors.RepositoryRootDirectory, "leanring-buddy", "OverlayWindow.swift"));
        Assert.Contains("func fadeOutAndHideOverlay(duration: TimeInterval = 0.4)", overlayWindowSource, StringComparison.Ordinal);
    }

    /// <summary>Always the first pointer phrase ("right here!") and the shortest 30 ms character delay.</summary>
    private sealed class FixedRandom : Random
    {
        public override int Next(int maxValue) => 0;

        public override double NextDouble() => 0;
    }
}
