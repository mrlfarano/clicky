namespace Clicky.Core;

/// <summary>
/// A point in DIPs on the overlay. The Mac flight math runs in SwiftUI points (top-left origin), and
/// its 800-point duration divisor and 80-point arc cap only keep their meaning in DIPs, not physical pixels.
/// </summary>
public readonly record struct DipPoint(double X, double Y);

/// <summary>One frame of the buddy's flight: where it is, which way its tip faces, and how big it is drawn.</summary>
/// <param name="Position">The buddy's position on the quadratic bezier arc.</param>
/// <param name="RotationDegrees">The triangle's rotation; 0° points the tip up, so travelling right is 90°.</param>
/// <param name="Scale">The flight scale pulse: 1.0 at take-off and landing, 1.3 at the midpoint.</param>
public readonly record struct FlightPathSample(DipPoint Position, double RotationDegrees, double Scale);

/// <summary>
/// Pure port of the Mac's bezier flight (OverlayWindow.swift:495-568, task T-041, parity PNT-06, seam 9).
/// The frame timer and the 60-fps frame count (:511-534) belong to the pointing sequencer, which calls
/// <see cref="Sample"/> once per frame with its linear progress.
/// </summary>
public static class FlightPath
{
    /// <summary>
    /// OverlayWindow.swift:508-510: the flight takes distance / 800 seconds, clamped to 0.6–1.4 s, so
    /// short hops are quick and long flights more dramatic.
    /// </summary>
    public static double ComputeFlightDurationSeconds(DipPoint startPosition, DipPoint endPosition)
    {
        double distance = ComputeDistance(startPosition, endPosition);
        return Math.Min(Math.Max(distance / 800.0, 0.6), 1.4);
    }

    /// <summary>
    /// The buddy's position, rotation and scale at <paramref name="linearProgress"/> (0→1 over the flight
    /// duration, OverlayWindow.swift:537). Position and rotation follow the smoothstep-eased progress;
    /// the scale pulse follows the linear progress, exactly as the Swift does.
    /// </summary>
    public static FlightPathSample Sample(double linearProgress, DipPoint startPosition, DipPoint endPosition)
    {
        // OverlayWindow.swift:515-522: the control point sits above the midpoint (negative Y is up) by
        // 20% of the distance, capped at 80 points, so the buddy flies a parabolic arc.
        double distance = ComputeDistance(startPosition, endPosition);
        double midPointX = (startPosition.X + endPosition.X) / 2.0;
        double midPointY = (startPosition.Y + endPosition.Y) / 2.0;
        double arcHeight = Math.Min(distance * 0.2, 80.0);
        double controlPointX = midPointX;
        double controlPointY = midPointY - arcHeight;

        // OverlayWindow.swift:539-540: smoothstep easeInOut, 3t² - 2t³.
        double easedProgress = linearProgress * linearProgress * (3.0 - 2.0 * linearProgress);

        // OverlayWindow.swift:542-549: quadratic bezier B(t) = (1-t)²·P0 + 2(1-t)t·P1 + t²·P2.
        double oneMinusEasedProgress = 1.0 - easedProgress;
        double bezierX = oneMinusEasedProgress * oneMinusEasedProgress * startPosition.X
                       + 2.0 * oneMinusEasedProgress * easedProgress * controlPointX
                       + easedProgress * easedProgress * endPosition.X;
        double bezierY = oneMinusEasedProgress * oneMinusEasedProgress * startPosition.Y
                       + 2.0 * oneMinusEasedProgress * easedProgress * controlPointY
                       + easedProgress * easedProgress * endPosition.Y;

        // OverlayWindow.swift:553-561: face the direction of travel along the tangent
        // B'(t) = 2(1-t)(P1-P0) + 2t(P2-P1); +90° because the tip points up at 0° and atan2 gives 0° for rightward.
        double tangentX = 2.0 * oneMinusEasedProgress * (controlPointX - startPosition.X)
                        + 2.0 * easedProgress * (endPosition.X - controlPointX);
        double tangentY = 2.0 * oneMinusEasedProgress * (controlPointY - startPosition.Y)
                        + 2.0 * easedProgress * (endPosition.Y - controlPointY);
        double rotationDegrees = Math.Atan2(tangentY, tangentX) * (180.0 / Math.PI) + 90.0;

        // OverlayWindow.swift:563-566: a sine pulse peaks at 1.3x mid-flight and returns to 1.0x on landing.
        double scalePulse = Math.Sin(linearProgress * Math.PI);
        double scale = 1.0 + scalePulse * 0.3;

        return new FlightPathSample(new DipPoint(bezierX, bezierY), rotationDegrees, scale);
    }

    // OverlayWindow.swift:504-506.
    private static double ComputeDistance(DipPoint startPosition, DipPoint endPosition) =>
        double.Hypot(endPosition.X - startPosition.X, endPosition.Y - startPosition.Y);
}
