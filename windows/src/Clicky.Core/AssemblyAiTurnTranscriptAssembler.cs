namespace Clicky.Core;

/// <summary>The fields of an AssemblyAI "Turn" message the transcript depends on. Any of them may be missing.</summary>
public sealed record AssemblyAiTurnMessage(string? Transcript, int? TurnOrder, bool? EndOfTurn, bool? TurnIsFormatted);

/// <summary>
/// Composes the running transcript from AssemblyAI Turn messages, ported from
/// AssemblyAIStreamingTranscriptionProvider.swift:273-347 (parity spec STT-03). Not thread-safe:
/// Swift serializes this on its state queue, and callers here must do the same.
/// </summary>
public sealed class AssemblyAiTurnTranscriptAssembler
{
    private readonly record struct StoredTurnTranscript(string TranscriptText, bool IsFormatted);

    private readonly SortedDictionary<int, StoredTurnTranscript> storedTurnTranscriptsByOrder = [];
    private int? activeTurnOrder;
    private string activeTurnTranscriptText = "";

    /// <summary>Applies one Turn message and returns the full transcript composed after it.</summary>
    public string ApplyTurnMessage(AssemblyAiTurnMessage turnMessage)
    {
        string transcriptText = turnMessage.Transcript?.Trim() ?? "";

        // A message without turn_order belongs to the active turn, or else starts the next one.
        int turnOrder = turnMessage.TurnOrder
            ?? activeTurnOrder
            ?? (storedTurnTranscriptsByOrder.Count == 0 ? 0 : storedTurnTranscriptsByOrder.Keys.Max() + 1);

        if (turnMessage.EndOfTurn == true || turnMessage.TurnIsFormatted == true)
        {
            activeTurnOrder = null;
            activeTurnTranscriptText = "";
            StoreTurnTranscript(transcriptText, turnOrder, isFormatted: turnMessage.TurnIsFormatted == true);
        }
        else
        {
            activeTurnOrder = turnOrder;
            activeTurnTranscriptText = transcriptText;
        }

        return ComposeFullTranscript();
    }

    /// <summary>Committed turns in ascending turn order, then the active turn, joined by spaces.</summary>
    public string ComposeFullTranscript()
    {
        List<string> transcriptSegments = storedTurnTranscriptsByOrder.Values
            .Select(storedTurnTranscript => storedTurnTranscript.TranscriptText)
            .ToList();

        string trimmedActiveTurnTranscriptText = activeTurnTranscriptText.Trim();
        if (trimmedActiveTurnTranscriptText.Length > 0)
        {
            transcriptSegments.Add(trimmedActiveTurnTranscriptText);
        }

        return string.Join(" ", transcriptSegments);
    }

    private void StoreTurnTranscript(string transcriptText, int turnOrder, bool isFormatted)
    {
        // Empty turns are never stored, so they cannot blank out an earlier transcript for the same order.
        if (transcriptText.Length == 0)
        {
            return;
        }

        // AssemblyAI sends the unformatted end-of-turn and then a formatted copy; a late unformatted one must not win.
        if (storedTurnTranscriptsByOrder.TryGetValue(turnOrder, out StoredTurnTranscript existingTurnTranscript)
            && existingTurnTranscript.IsFormatted
            && !isFormatted)
        {
            return;
        }

        storedTurnTranscriptsByOrder[turnOrder] = new StoredTurnTranscript(transcriptText, isFormatted);
    }
}
