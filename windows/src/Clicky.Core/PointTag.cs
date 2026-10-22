using System.Globalization;
using System.Text.RegularExpressions;

namespace Clicky.Core;

/// <summary>A coordinate in the screenshot's pixel space (top-left origin), as Claude wrote it in the tag.</summary>
public readonly record struct ScreenshotPoint(double X, double Y);

/// <summary>
/// Result of parsing the trailing [POINT:…] tag. Mirrors Swift's
/// <c>CompanionManager.PointingParseResult</c> field for field.
/// </summary>
/// <param name="SpokenText">The response with the tag removed; this is what gets spoken and shown.</param>
/// <param name="Coordinate">Null for [POINT:none] or when there is no tag.</param>
/// <param name="ElementLabel">The tag's label, "none" for [POINT:none], null when there is no tag.</param>
/// <param name="ScreenNumber">1-based screen number from ":screenN". It is a long because Swift's Int is 64-bit, so values that overflow Int32 still parse the same way.</param>
public sealed record PointTagParseResult(
    string SpokenText,
    ScreenshotPoint? Coordinate,
    string? ElementLabel,
    long? ScreenNumber)
{
    /// <summary>True when a tag matched at the end of the text, including [POINT:none].</summary>
    public bool FoundTrailingTag => Coordinate is not null || ElementLabel is not null;
}

/// <summary>Port of <c>CompanionManager.parsePointingCoordinates</c> (CompanionManager.swift:784-823), seam 2.</summary>
public static class PointTag
{
    // Ported verbatim from CompanionManager.swift:786. Both NSRegularExpression (ICU) and .NET are
    // backtracking engines, so the same pattern gives the same groups for the same input.
    private static readonly Regex TrailingPointTagPattern = new(
        @"\[POINT:(?:none|(\d+)\s*,\s*(\d+)(?::([^\]:\s][^\]:]*?))?(?::screen(\d+))?)\]\s*$",
        RegexOptions.CultureInvariant);

    public static PointTagParseResult Parse(string responseText)
    {
        Match trailingTagMatch = TrailingPointTagPattern.Match(responseText);
        if (!trailingTagMatch.Success)
        {
            // Swift returns the input untouched (not even trimmed) when there is no tag.
            return new PointTagParseResult(responseText, Coordinate: null, ElementLabel: null, ScreenNumber: null);
        }

        string spokenText = responseText[..trailingTagMatch.Index].Trim();

        Group xGroup = trailingTagMatch.Groups[1];
        Group yGroup = trailingTagMatch.Groups[2];
        // Swift parses with Double(...); a digit string Double cannot read (for example non-ASCII digits,
        // which \d also matches) falls into the same branch as [POINT:none].
        if (!xGroup.Success
            || !yGroup.Success
            || !double.TryParse(xGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out double pointX)
            || !double.TryParse(yGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out double pointY))
        {
            return new PointTagParseResult(spokenText, Coordinate: null, ElementLabel: "none", ScreenNumber: null);
        }

        Group labelGroup = trailingTagMatch.Groups[3];
        string? elementLabel = labelGroup.Success ? TrimSpacesAndTabs(labelGroup.Value) : null;

        Group screenGroup = trailingTagMatch.Groups[4];
        long? screenNumber = null;
        // Swift's Int(...) returns nil on overflow; TryParse gives the same result without throwing.
        if (screenGroup.Success
            && long.TryParse(screenGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedScreenNumber))
        {
            screenNumber = parsedScreenNumber;
        }

        return new PointTagParseResult(spokenText, new ScreenshotPoint(pointX, pointY), elementLabel, screenNumber);
    }

    /// <summary>
    /// Swift trims the label with CharacterSet.whitespaces, which is spaces and tabs but not newlines,
    /// so string.Trim() (which also removes newlines) would differ.
    /// </summary>
    private static string TrimSpacesAndTabs(string text)
    {
        static bool IsSpaceOrTab(char character) =>
            character == '\t' || char.GetUnicodeCategory(character) == UnicodeCategory.SpaceSeparator;

        int firstKeptIndex = 0;
        int endIndexExclusive = text.Length;
        while (firstKeptIndex < endIndexExclusive && IsSpaceOrTab(text[firstKeptIndex]))
        {
            firstKeptIndex++;
        }
        while (endIndexExclusive > firstKeptIndex && IsSpaceOrTab(text[endIndexExclusive - 1]))
        {
            endIndexExclusive--;
        }
        return text[firstKeptIndex..endIndexExclusive];
    }
}
