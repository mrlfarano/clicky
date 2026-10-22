namespace Clicky.Core;

/// <summary>
/// What the overlay bubble shows while a reply streams in (parity spec CLA-05, Windows-only: the Mac
/// shows no streaming text). The user must never see tag syntax, so a trailing fragment that could
/// still turn into a [POINT:…] tag is held back until the next delta settles it.
/// </summary>
public static class StreamingBubbleText
{
    private const string PointTagOpening = "[POINT:";

    public static string VisibleTextFor(string accumulatedResponseText)
    {
        PointTagParseResult trailingTag = PointTag.Parse(accumulatedResponseText);
        if (trailingTag.FoundTrailingTag)
        {
            return trailingTag.SpokenText;
        }

        int lastOpeningBracketIndex = accumulatedResponseText.LastIndexOf('[');
        if (lastOpeningBracketIndex < 0)
        {
            return accumulatedResponseText;
        }

        string trailingFragment = accumulatedResponseText[lastOpeningBracketIndex..];
        if (trailingFragment.Contains(']'))
        {
            // The bracket is closed and it was not a trailing tag, so it is ordinary spoken text.
            return accumulatedResponseText;
        }

        // "[", "[PO" and "[POINT:400,30" can all still become a tag; "[note" cannot.
        bool couldStillBecomePointTag =
            PointTagOpening.StartsWith(trailingFragment, StringComparison.Ordinal)
            || trailingFragment.StartsWith(PointTagOpening, StringComparison.Ordinal);
        return couldStillBecomePointTag
            ? accumulatedResponseText[..lastOpeningBracketIndex]
            : accumulatedResponseText;
    }
}
