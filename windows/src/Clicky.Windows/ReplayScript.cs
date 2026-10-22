using System.Text.Json;
using System.Text.Json.Nodes;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// One scripted input event of a --script file: a push-to-talk transition due at a virtual-clock time,
/// in milliseconds since the replay interaction started. T-002's stub delivers the event onto that clock
/// and records the voice-state change it causes; T-012 (WS-07) rewires the same events into the session
/// coordinator so a scripted press cancels an in-flight response like a real one does.
/// </summary>
internal sealed record ReplayScriptEvent(long TMs, PushToTalkTransition Transition);

/// <summary>
/// The parsed --script file (WS-04 part 2): a JSON object of the shape
/// <code>{ "events": [ { "tMs": 300, "type": "press" }, { "tMs": 900, "type": "release" } ] }</code>.
/// Events are sorted by tMs on load, so delivery is in chronological order however the file lists them.
/// </summary>
/// <param name="Events">At least one event; a script exists to deliver events, so an empty one is a usage error.</param>
internal sealed record ReplayScript(IReadOnlyList<ReplayScriptEvent> Events)
{
    /// <summary>
    /// Parses and validates a script file, throwing <see cref="CommandLineUsageException"/> for anything a
    /// caller could have mistyped (surfaces as exit code 2, matching the replay manifest loader).
    /// </summary>
    public static ReplayScript LoadFrom(string scriptFilePath)
    {
        JsonObject scriptDocument;
        try
        {
            // "null" parses to a null JsonNode, and a JSON array or string is not a script, so both cases
            // land in the same usage error as unparsable text.
            scriptDocument = (JsonNode.Parse(File.ReadAllText(scriptFilePath)) as JsonObject)
                ?? throw new CommandLineUsageException($"Input script {scriptFilePath} is not a JSON object.");
        }
        catch (JsonException scriptJsonException)
        {
            throw new CommandLineUsageException($"Input script {scriptFilePath} is not valid JSON: {scriptJsonException.Message}");
        }

        JsonNode? eventsNode = scriptDocument["events"];
        if (eventsNode is null)
        {
            throw new CommandLineUsageException($"Input script {scriptFilePath} needs an 'events' array.");
        }
        if (eventsNode is not JsonArray eventsArray || eventsArray.Count == 0)
        {
            throw new CommandLineUsageException($"Input script {scriptFilePath}: 'events' must be a non-empty array.");
        }
        if (scriptDocument.Count > 1)
        {
            string unknownPropertyName = scriptDocument.First(property => property.Key != "events").Key;
            throw new CommandLineUsageException(
                $"Input script {scriptFilePath}: unknown property '{unknownPropertyName}'; the only property is 'events'.");
        }

        List<ReplayScriptEvent> scriptEvents = [];
        foreach (JsonNode? eventNode in eventsArray)
        {
            scriptEvents.Add(ReadEvent(scriptFilePath, eventNode));
        }
        return new ReplayScript([.. scriptEvents.OrderBy(scriptEvent => scriptEvent.TMs)]);
    }

    private static ReplayScriptEvent ReadEvent(string scriptFilePath, JsonNode? eventNode)
    {
        if (eventNode is not JsonObject eventObject)
        {
            throw new CommandLineUsageException($"Input script {scriptFilePath}: every event must be an object.");
        }
        foreach (string eventPropertyName in eventObject.Select(eventProperty => eventProperty.Key))
        {
            if (eventPropertyName is not ("tMs" or "type"))
            {
                throw new CommandLineUsageException(
                    $"Input script {scriptFilePath}: unknown event property '{eventPropertyName}'; known properties: tMs, type.");
            }
        }

        JsonNode? tMsNode = eventObject["tMs"];
        if (tMsNode is not JsonValue tMsValue
            || !tMsValue.TryGetValue<long>(out long eventTMs)
            || eventTMs < 0)
        {
            throw new CommandLineUsageException($"Input script {scriptFilePath}: event tMs must be a whole number of milliseconds >= 0.");
        }

        JsonNode? typeNode = eventObject["type"];
        if (typeNode is not JsonValue typeValue || typeValue.GetValueKind() != JsonValueKind.String)
        {
            throw new CommandLineUsageException($"Input script {scriptFilePath}: event type must be the string 'press' or 'release'.");
        }
        // Lowercase names only, exactly as the harness writes them; a capitalized "Press" is a typo worth failing on.
        PushToTalkTransition eventTransition = (string?)typeValue switch
        {
            "press" => PushToTalkTransition.Pressed,
            "release" => PushToTalkTransition.Released,
            _ => throw new CommandLineUsageException(
                $"Input script {scriptFilePath}: event type must be 'press' or 'release', got '{(string?)typeValue}'."),
        };
        return new ReplayScriptEvent(eventTMs, eventTransition);
    }
}
