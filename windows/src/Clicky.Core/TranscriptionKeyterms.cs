namespace Clicky.Core;

/// <summary>
/// Keyterms sent to AssemblyAI as keyterms_prompt, ported from BuddyDictationManager.swift:652-684
/// (parity spec STT-07).
/// </summary>
public static class TranscriptionKeyterms
{
    /// <summary>
    /// BuddyDictationManager.swift:653-665 verbatim. "makesomething" and "Learning Buddy" are legacy
    /// terms, kept for parity.
    /// </summary>
    public static IReadOnlyList<string> BaseKeyterms { get; } =
    [
        "makesomething",
        "Learning Buddy",
        "Codex",
        "Claude",
        "Anthropic",
        "OpenAI",
        "SwiftUI",
        "Xcode",
        "Vercel",
        "Next.js",
        "localhost",
    ];

    /// <summary>
    /// Base terms followed by the contextual ones, trimmed, blanks dropped, and de-duplicated
    /// case-insensitively, keeping first-seen order and casing. Nothing feeds contextual terms in v1
    /// (Swift's updateContextualKeyterms has no callers), so the shipped list is the base terms.
    /// </summary>
    public static IReadOnlyList<string> BuildTranscriptionKeyterms(IReadOnlyList<string> contextualKeyterms)
    {
        var uniqueNormalizedKeyterms = new HashSet<string>(StringComparer.Ordinal);
        var orderedKeyterms = new List<string>();

        foreach (string keyterm in BaseKeyterms.Concat(contextualKeyterms))
        {
            string trimmedKeyterm = keyterm.Trim();
            if (trimmedKeyterm.Length == 0)
            {
                continue;
            }

            // Swift compares lowercased() copies; ToLowerInvariant is the culture-free equivalent.
            if (uniqueNormalizedKeyterms.Add(trimmedKeyterm.ToLowerInvariant()))
            {
                orderedKeyterms.Add(trimmedKeyterm);
            }
        }

        return orderedKeyterms;
    }
}
