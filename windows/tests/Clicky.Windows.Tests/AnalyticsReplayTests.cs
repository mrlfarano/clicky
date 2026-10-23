using System.Text.Json.Nodes;
using Clicky.Core;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// T-044 (WS-24 part 2): one full push-to-talk exchange through the real Clicky.exe --script harness emits the
/// AN-01 events in reducer order, with the catalogue's property keys, into the in-memory sink, and replay
/// sends nothing to PostHog.
/// </summary>
[Collection(ClickyProcessReplayCollection.Name)]
public sealed class AnalyticsReplayTests
{
    [Fact]
    public async Task FullExchangeEventOrder()
    {
        // As in CoordinatorReplayTests.VoiceStateTimelineRecorded: the tracer submission at tMs 0 is still
        // streaming (200 ms per event) when the press at 300 cancels it, and the release at 400 starts the full
        // exchange that runs to its point and spoken reply.
        string scriptPath = Path.Combine(Path.GetTempPath(), $"clicky-replay-script-{Guid.NewGuid():N}.json");
        File.WriteAllText(scriptPath, """{ "events": [ { "tMs": 300, "type": "press" }, { "tMs": 400, "type": "release" } ] }""");
        ReplayRun replayRun;
        try
        {
            replayRun = await ClickyReplayProcess.RunAsync("point-basic.sse", "--script", scriptPath, "--event-delay-ms", "200");
        }
        finally
        {
            File.Delete(scriptPath);
        }
        JsonNode report = replayRun.Report;
        Assert.True(replayRun.ExitCode == 0, $"exit code {replayRun.ExitCode}. Output:\n{replayRun.ProcessOutput}");
        Assert.Null(report["error"]);

        JsonArray analyticsEvents = report["analyticsEvents"]!.AsArray();
        Assert.Equal(
            [
                // The cancelled tracer submission...
                "user_message_sent",
                // ...then the full exchange: press, release, submission, point, reply (CompanionManager.swift:511, 533, 523, 678, 698).
                "push_to_talk_started",
                "push_to_talk_released",
                "user_message_sent",
                "element_pointed",
                "ai_response_received",
            ],
            analyticsEvents.Select(analyticsEvent => (string?)analyticsEvent!["name"]));

        // The properties come from the AN-01 catalogue, not the reducer: exact keys, grapheme character_count.
        Assert.Null(analyticsEvents[1]!["properties"]);
        Assert.Null(analyticsEvents[2]!["properties"]);
        JsonObject userMessageSentProperties = analyticsEvents[3]!["properties"]!.AsObject();
        Assert.Equal(["transcript", "character_count"], userMessageSentProperties.Select(property => property.Key));
        string transcript = (string)userMessageSentProperties["transcript"]!;
        Assert.Equal(AnalyticsEvents.CountCharacters(transcript), (int)userMessageSentProperties["character_count"]!);
        Assert.Equal((string?)report["point"]!["label"], (string?)analyticsEvents[4]!["properties"]!["element_label"]);
        JsonObject aiResponseReceivedProperties = analyticsEvents[5]!["properties"]!.AsObject();
        Assert.Equal(["response", "character_count"], aiResponseReceivedProperties.Select(property => property.Key));
        Assert.Equal((string?)report["spokenText"], (string?)aiResponseReceivedProperties["response"]);
        Assert.Equal(AnalyticsEvents.CountCharacters((string)report["spokenText"]!), (int)aiResponseReceivedProperties["character_count"]!);

        // No request to posthog.com: replay runs the in-memory sink, and the fail-closed manifest handler saw
        // only the Worker's TLS warm-up HEAD and its POST /chat, never a PostHog /i/v0/e/ capture (one would also
        // have failed the run instead of exiting zero).
        Assert.DoesNotContain("posthog", replayRun.ProcessOutput, StringComparison.OrdinalIgnoreCase);
        Assert.All(report["httpRequests"]!.AsArray(), httpRequest => Assert.Contains(
            $"{httpRequest!["method"]} {httpRequest["path"]}", new[] { "HEAD /", "POST /chat" }));
    }
}
