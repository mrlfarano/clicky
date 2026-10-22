using System.Text.Json;

namespace Clicky.Core;

public enum SseLineKind
{
    /// <summary>Not a data line, unparseable, or an event that carries no text.</summary>
    Ignored,
    /// <summary>A content_block_delta whose delta is text_delta; <see cref="SseLineParseResult.TextDelta"/> holds the text.</summary>
    TextDelta,
    /// <summary>The "data: [DONE]" marker. Nothing after it is read.</summary>
    EndOfStream,
}

public readonly record struct SseLineParseResult(SseLineKind Kind, string? TextDelta)
{
    public static readonly SseLineParseResult Ignored = new(SseLineKind.Ignored, null);
    public static readonly SseLineParseResult EndOfStream = new(SseLineKind.EndOfStream, null);
}

/// <summary>Seam 4: one line of the Worker /chat SSE stream, ported from ClaudeAPI.swift:180-208.</summary>
public static class SseLineParser
{
    // Swift checks hasPrefix("data: ") with the space, so "data:{...}" is ignored there too.
    private const string DataLinePrefix = "data: ";
    private const string DoneMarker = "[DONE]";

    public static SseLineParseResult ParseLine(string line)
    {
        if (!line.StartsWith(DataLinePrefix, StringComparison.Ordinal))
        {
            return SseLineParseResult.Ignored;
        }

        string eventJson = line[DataLinePrefix.Length..];
        if (eventJson == DoneMarker)
        {
            return SseLineParseResult.EndOfStream;
        }

        try
        {
            using JsonDocument eventDocument = JsonDocument.Parse(eventJson);
            JsonElement eventPayload = eventDocument.RootElement;
            if (eventPayload.ValueKind != JsonValueKind.Object
                || !TryGetString(eventPayload, "type", out string? eventType)
                || eventType != "content_block_delta"
                || !eventPayload.TryGetProperty("delta", out JsonElement delta)
                || delta.ValueKind != JsonValueKind.Object
                || !TryGetString(delta, "type", out string? deltaType)
                || deltaType != "text_delta"
                || !TryGetString(delta, "text", out string? textChunk))
            {
                return SseLineParseResult.Ignored;
            }
            return new SseLineParseResult(SseLineKind.TextDelta, textChunk);
        }
        catch (JsonException)
        {
            // Swift skips lines whose JSON does not parse and keeps reading.
            return SseLineParseResult.Ignored;
        }
    }

    private static bool TryGetString(JsonElement jsonObject, string propertyName, out string? value)
    {
        if (jsonObject.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }
        value = null;
        return false;
    }
}
