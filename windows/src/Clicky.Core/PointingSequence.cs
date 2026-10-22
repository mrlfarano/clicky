using System.Globalization;

namespace Clicky.Core;

/// <summary>
/// Where the pointing sequence is. Swift's BuddyNavigationMode (OverlayWindow.swift:91-100) has three modes;
/// the steps it runs on DispatchQueue delays inside them get their own phase here so a virtual clock can drive them.
/// </summary>
public enum PointingSequencePhase
{
    /// <summary>.followingCursor: not pointing, the shell keeps the buddy beside the cursor.</summary>
    FollowingCursor,

    /// <summary>.navigatingToTarget on the way out (OverlayWindow.swift:481-489).</summary>
    FlyingToTarget,

    /// <summary>.pointingAtTarget while the bubble phrase streams in one character at a time (:603-630).</summary>
    StreamingBubbleText,

    /// <summary>.pointingAtTarget during the 3 s hold after the last character (:590-593).</summary>
    HoldingAtTarget,

    /// <summary>.pointingAtTarget while the bubble fades out before the flight back (:594-598).</summary>
    FadingOutBubble,

    /// <summary>.navigatingToTarget with isReturningToCursor, the only phase a cursor move can cancel (:633-647).</summary>
    ReturningToCursor,
}

/// <summary>What the shell must render next. Pure data: the shell owns the window, the visuals and the timers.</summary>
public abstract record PointingCommand
{
    /// <summary>Place the buddy's tip at <paramref name="PositionPx"/>, rotated and scaled as given.</summary>
    /// <param name="RotationDegrees">0° points the tip up; at rest the Mac pointer sits at -35° (OverlayWindow.swift:576).</param>
    /// <param name="Scale">The flight scale pulse; 1.0 at rest.</param>
    public sealed record MoveBuddy(VirtualScreenPoint PositionPx, double RotationDegrees, double Scale) : PointingCommand;

    /// <summary>
    /// Show the bubble with this much of the phrase. The first one of a pointing pops the bubble in from
    /// 0.5x to 1x on a spring (response 0.4, damping 0.6) at full opacity (OverlayWindow.swift:580-582, 290, 619-622).
    /// </summary>
    public sealed record ShowBubbleText(string VisibleBubbleText) : PointingCommand;

    /// <summary>Fade the bubble's opacity to 0, ease-out (OverlayWindow.swift:291, 594).</summary>
    public sealed record FadeOutBubble(TimeSpan FadeDuration) : PointingCommand;

    /// <summary>
    /// finishNavigationAndResumeFollowing (OverlayWindow.swift:660-672): rotation back to -35°, scale 1, bubble
    /// cleared, the buddy follows the cursor again and the target is cleared, so the shell dispatches
    /// <see cref="CompanionSessionEvent.PointingFinished"/>.
    /// </summary>
    /// <param name="WasReturnCancelledByCursorMove">True when a cursor move cut the flight back short (:420-428, 651-658).</param>
    public sealed record FinishPointing(bool WasReturnCancelledByCursorMove) : PointingCommand;
}

/// <summary>
/// The Mac pointing sequence of OverlayWindow.swift:455-672 on a virtual clock (task T-042, parity PNT-06):
/// fly to the target on the <see cref="FlightPath"/> arc, stream the bubble, hold 3 s, fade the bubble, fly back
/// to the cursor, then clear the target. A cursor move of more than 100 pt during the flight back cancels it.
/// The shell passes its own clock reading to every call, so tests step time instead of waiting.
/// Positions in and out are physical pixels on the virtual screen; the flight math runs in DIPs at the given DPI.
/// shortcut: one dpiScale per pointing (the target monitor's), so a return flight to a cursor on a monitor with a
/// different DPI keeps the target's scale; split the flight per monitor if the mixed-DPI QA recording shows a jump.
/// </summary>
public sealed class PointingSequence(Random random)
{
    /// <summary>OverlayWindow.swift:175-182.</summary>
    public static readonly IReadOnlyList<string> NavigationPointerPhrases =
        ["right here!", "this one!", "over here!", "click this!", "here it is!", "found it!"];

    /// <summary>OverlayWindow.swift:592: the bubble stays up this long after its last character.</summary>
    public static readonly TimeSpan HoldAtTargetDuration = TimeSpan.FromSeconds(3);

    /// <summary>OverlayWindow.swift:291, 595: the bubble fades over 0.5 s and the flight back waits for it.</summary>
    public static readonly TimeSpan BubbleFadeOutDuration = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// OverlayWindow.swift:819-823: the transient hide (CompanionManager.swift:753) fades the overlay window's
    /// opacity to 0 over 0.4 s, ease-in. The shell runs this animation for <see cref="CompanionSessionEffect.HideOverlay"/>.
    /// </summary>
    public static readonly TimeSpan OverlayHideFadeOutDuration = TimeSpan.FromMilliseconds(400);

    /// <summary>OverlayWindow.swift:426: how far the cursor may move from where the flight back began before it cancels.</summary>
    public const double ReturnCancelCursorDistanceDip = 100;

    /// <summary>OverlayWindow.swift:439-441, 636: the buddy follows 35 pt right and 25 pt below the cursor.</summary>
    public const double CursorTrackingOffsetXDip = 35;

    public const double CursorTrackingOffsetYDip = 25;

    /// <summary>OverlayWindow.swift:576, 667: the pointer's resting rotation.</summary>
    public const double RestingRotationDegrees = -35;

    public PointingSequencePhase Phase { get; private set; } = PointingSequencePhase.FollowingCursor;

    private double dpiScale = 1;
    private DipPoint flightStartPositionDip;
    private DipPoint flightDestinationDip;
    private TimeSpan flightStartedAt;
    private double flightDurationSeconds;
    private string bubblePhrase = "";
    private int bubblePhraseCharacterCount;
    private int visibleBubbleCharacterCount;
    private TimeSpan nextStepAt;
    private VirtualScreenPoint latestCursorPositionPx;
    private VirtualScreenPoint cursorPositionWhenReturnStartedPx;

    /// <summary>
    /// startNavigatingToElement (OverlayWindow.swift:455-489): starts or restarts pointing from wherever the buddy is.
    /// The tip flies exactly onto <paramref name="targetPx"/> (decision PNT-04 drops the Mac's 8/12 pt offset and clamp).
    /// </summary>
    /// <param name="customBubbleText">The onboarding demo's text; null picks a random pointer phrase (:584-588).</param>
    public IReadOnlyList<PointingCommand> Start(
        TimeSpan now,
        VirtualScreenPoint buddyPositionPx,
        VirtualScreenPoint targetPx,
        VirtualScreenPoint cursorPositionPx,
        double dpiScale,
        string? customBubbleText = null)
    {
        this.dpiScale = dpiScale;
        latestCursorPositionPx = cursorPositionPx;
        bubblePhrase = customBubbleText ?? NavigationPointerPhrases[random.Next(NavigationPointerPhrases.Count)];
        // Swift streams Characters, which are grapheme clusters, not UTF-16 units.
        bubblePhraseCharacterCount = new StringInfo(bubblePhrase).LengthInTextElements;
        StartFlight(now, ConvertToDip(buddyPositionPx), ConvertToDip(targetPx), PointingSequencePhase.FlyingToTarget);
        return Advance(now);
    }

    /// <summary>The shell's per-frame tick: everything due by <paramref name="now"/>, in order.</summary>
    public IReadOnlyList<PointingCommand> Advance(TimeSpan now)
    {
        var commands = new List<PointingCommand>();
        while (true)
        {
            switch (Phase)
            {
                case PointingSequencePhase.FlyingToTarget or PointingSequencePhase.ReturningToCursor:
                    double linearProgress = (now - flightStartedAt).TotalSeconds / flightDurationSeconds;
                    if (linearProgress < 1)
                    {
                        FlightPathSample flightPathSample = FlightPath.Sample(linearProgress, flightStartPositionDip, flightDestinationDip);
                        commands.Add(new PointingCommand.MoveBuddy(ConvertToPx(flightPathSample.Position), flightPathSample.RotationDegrees, flightPathSample.Scale));
                        return commands;
                    }
                    // OverlayWindow.swift:530-531, 576: land exactly on the destination at rest.
                    commands.Add(new PointingCommand.MoveBuddy(ConvertToPx(flightDestinationDip), RestingRotationDegrees, 1));
                    if (Phase == PointingSequencePhase.ReturningToCursor)
                    {
                        commands.Add(new PointingCommand.FinishPointing(WasReturnCancelledByCursorMove: false));
                        Phase = PointingSequencePhase.FollowingCursor;
                        return commands;
                    }
                    Phase = PointingSequencePhase.StreamingBubbleText;
                    visibleBubbleCharacterCount = 0;
                    nextStepAt = flightStartedAt + TimeSpan.FromSeconds(flightDurationSeconds);
                    break;
                case PointingSequencePhase.StreamingBubbleText when now >= nextStepAt:
                    if (visibleBubbleCharacterCount == bubblePhraseCharacterCount)
                    {
                        Phase = PointingSequencePhase.HoldingAtTarget;
                        nextStepAt += HoldAtTargetDuration;
                        break;
                    }
                    visibleBubbleCharacterCount++;
                    commands.Add(new PointingCommand.ShowBubbleText(
                        new StringInfo(bubblePhrase).SubstringByTextElements(0, visibleBubbleCharacterCount)));
                    // OverlayWindow.swift:624: 30-60 ms per character for a natural speaking rhythm.
                    nextStepAt += TimeSpan.FromSeconds(0.03 + random.NextDouble() * 0.03);
                    break;
                case PointingSequencePhase.HoldingAtTarget when now >= nextStepAt:
                    commands.Add(new PointingCommand.FadeOutBubble(BubbleFadeOutDuration));
                    Phase = PointingSequencePhase.FadingOutBubble;
                    nextStepAt += BubbleFadeOutDuration;
                    break;
                case PointingSequencePhase.FadingOutBubble when now >= nextStepAt:
                    // OverlayWindow.swift:633-647: fly from the target to beside the cursor as it is now, and
                    // measure the cancel distance from there.
                    cursorPositionWhenReturnStartedPx = latestCursorPositionPx;
                    DipPoint cursorPositionDip = ConvertToDip(latestCursorPositionPx);
                    StartFlight(
                        nextStepAt,
                        flightDestinationDip,
                        new DipPoint(cursorPositionDip.X + CursorTrackingOffsetXDip, cursorPositionDip.Y + CursorTrackingOffsetYDip),
                        PointingSequencePhase.ReturningToCursor);
                    break;
                default:
                    return commands;
            }
        }
    }

    /// <summary>
    /// The cursor tracking tick (OverlayWindow.swift:411-430). Only the flight back can be cancelled, and only once
    /// the cursor is more than 100 pt from where it was when that flight began; 100 pt is 150 px at 150%.
    /// </summary>
    public IReadOnlyList<PointingCommand> CursorMoved(VirtualScreenPoint cursorPositionPx)
    {
        latestCursorPositionPx = cursorPositionPx;
        double distanceFromReturnStartPx = double.Hypot(
            cursorPositionPx.X - cursorPositionWhenReturnStartedPx.X,
            cursorPositionPx.Y - cursorPositionWhenReturnStartedPx.Y);
        if (Phase != PointingSequencePhase.ReturningToCursor || distanceFromReturnStartPx <= ReturnCancelCursorDistanceDip * dpiScale)
        {
            return [];
        }
        Phase = PointingSequencePhase.FollowingCursor;
        return [new PointingCommand.FinishPointing(WasReturnCancelledByCursorMove: true)];
    }

    private void StartFlight(TimeSpan startedAt, DipPoint startPositionDip, DipPoint destinationDip, PointingSequencePhase flightPhase)
    {
        Phase = flightPhase;
        flightStartedAt = startedAt;
        flightStartPositionDip = startPositionDip;
        flightDestinationDip = destinationDip;
        flightDurationSeconds = FlightPath.ComputeFlightDurationSeconds(startPositionDip, destinationDip);
    }

    private DipPoint ConvertToDip(VirtualScreenPoint pointPx) => new(pointPx.X / dpiScale, pointPx.Y / dpiScale);

    private VirtualScreenPoint ConvertToPx(DipPoint pointDip) => new(pointDip.X * dpiScale, pointDip.Y * dpiScale);
}
