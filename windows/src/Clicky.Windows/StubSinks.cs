namespace Clicky.Windows;

/// <summary>
/// Seam 8 (parity spec section 8): where analytics events go. The real sink (T-044, WS-24) fire-and-forget
/// POSTs each event to us.i.posthog.com; the app always records through this interface so the sink can be
/// swapped per mode. In replay the in-memory implementation below is the sink, which is what guarantees
/// "stubbed in replay, so nothing reaches PostHog" (parity spec report-table note).
/// </summary>
internal interface IAnalyticsSink
{
    /// <param name="eventProperties">Event properties per the AN-01 catalogue; null when the event has none.</param>
    void TrackEvent(string eventName, IReadOnlyDictionary<string, string?>? eventProperties = null);

    /// <summary>Every event handed to the sink this session, in order; the report copies these.</summary>
    IReadOnlyList<AnalyticsEventReport> RecordedEvents { get; }
}

/// <summary>
/// The in-memory analytics sink (T-002 stub): records every event for the replay report and sends nothing
/// anywhere. Live mode also runs this stub until T-044 lands, so no build phones PostHog before the real
/// sink and its keys exist.
/// </summary>
internal sealed class InMemoryAnalyticsSink : IAnalyticsSink
{
    public List<AnalyticsEventReport> RecordedEvents { get; } = [];

    IReadOnlyList<AnalyticsEventReport> IAnalyticsSink.RecordedEvents => RecordedEvents;

    public void TrackEvent(string eventName, IReadOnlyDictionary<string, string?>? eventProperties = null) =>
        RecordedEvents.Add(new AnalyticsEventReport(
            eventName,
            eventProperties is null ? null : new Dictionary<string, string?>(eventProperties)));
}

/// <summary>
/// Where spoken text goes to become audio: the real implementation (T-029, WS-17) POSTs the text to the
/// Worker's /tts route and plays the returned MP3 through WASAPI. This stub exists so the request side of
/// that pipeline can be observed in replay: it records the text as a report ttsRequests entry and never
/// opens an audio device, which is what keeps replay runs silent and CI-safe.
/// </summary>
internal interface ITextToSpeechSink
{
    Task SpeakAsync(string spokenText, CancellationToken cancellationToken);

    /// <summary>The texts this sink was asked to speak, in order; the report copies these as ttsRequests.</summary>
    IReadOnlyList<TtsRequestReport> RecordedRequests { get; }
}

/// <summary>The in-memory TTS sink (T-002 stub): records each speak request, plays nothing.</summary>
internal sealed class InMemoryTextToSpeechSink : ITextToSpeechSink
{
    public List<TtsRequestReport> RecordedRequests { get; } = [];

    IReadOnlyList<TtsRequestReport> ITextToSpeechSink.RecordedRequests => RecordedRequests;

    public Task SpeakAsync(string spokenText, CancellationToken cancellationToken)
    {
        RecordedRequests.Add(new TtsRequestReport(spokenText));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Where an utterance goes when TTS failed and the system voice must speak it instead (parity spec TTS-03).
/// The real implementation (T-029) uses System.Speech; this stub records the utterance as a report
/// fallbackUtterances entry without synthesizing any audio.
/// </summary>
internal interface IFallbackSpeechSink
{
    Task SpeakFallbackUtteranceAsync(string fallbackUtterance, CancellationToken cancellationToken);

    /// <summary>The utterances this sink was asked to speak, in order; the report copies these.</summary>
    IReadOnlyList<string> RecordedUtterances { get; }
}

/// <summary>The in-memory fallback speech sink (T-002 stub): records each utterance, speaks nothing.</summary>
internal sealed class InMemoryFallbackSpeechSink : IFallbackSpeechSink
{
    public List<string> RecordedUtterances { get; } = [];

    IReadOnlyList<string> IFallbackSpeechSink.RecordedUtterances => RecordedUtterances;

    public Task SpeakFallbackUtteranceAsync(string fallbackUtterance, CancellationToken cancellationToken)
    {
        RecordedUtterances.Add(fallbackUtterance);
        return Task.CompletedTask;
    }
}
