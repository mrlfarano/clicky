using System.Net.Http.Headers;
using System.Speech.Synthesis;
using System.Text;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// The real TTS sink (WS-17): records the text as a ttsRequests entry, like the stub, then POSTs it to the
/// Worker's /tts route and returns the whole MP3. ElevenLabsTTSClient.swift:50 downloads everything before
/// playing despite its "streams" comment (audit §1b), so this does not stream either.
/// </summary>
internal sealed class WorkerTextToSpeechSink(HttpClient workerHttpClient, Uri ttsEndpoint) : ITextToSpeechSink
{
    public List<TtsRequestReport> RecordedRequests { get; } = [];

    IReadOnlyList<TtsRequestReport> ITextToSpeechSink.RecordedRequests => RecordedRequests;

    public async Task<byte[]> DownloadSpeechAsync(string spokenText, CancellationToken cancellationToken)
    {
        RecordedRequests.Add(new TtsRequestReport(spokenText));
        // The reducer only asks to speak non-blank text (CompanionManager.swift:702), so the body is never null here.
        string requestJson = TtsRequest.BuildRequestBodyOrNullWhenBlank(spokenText)!.ToJsonString();
        using var ttsRequest = new HttpRequestMessage(HttpMethod.Post, ttsEndpoint)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        ttsRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(TtsRequest.AcceptHeaderValue));

        using HttpResponseMessage ttsResponse = await workerHttpClient.SendAsync(ttsRequest, cancellationToken);
        byte[] responseBytes = await ttsResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!ttsResponse.IsSuccessStatusCode)
        {
            // Same message shape as ElevenLabsTTSClient.swift:57-61.
            throw new HttpRequestException($"TTS API error ({(int)ttsResponse.StatusCode}): {Encoding.UTF8.GetString(responseBytes)}");
        }
        return responseBytes;
    }
}

/// <summary>
/// The real fallback sink (audit row F7): records the utterance as a fallbackUtterances entry, like the stub,
/// then speaks it through SAPI with System.Speech (MIT) on the default audio device.
/// </summary>
internal sealed class SystemVoiceFallbackSpeechSink : IFallbackSpeechSink
{
    // Created on first use, so a live session that never fails TTS never loads a SAPI voice.
    private SpeechSynthesizer? speechSynthesizer;

    public List<string> RecordedUtterances { get; } = [];

    IReadOnlyList<string> IFallbackSpeechSink.RecordedUtterances => RecordedUtterances;

    public Task SpeakFallbackUtteranceAsync(string fallbackUtterance, CancellationToken cancellationToken)
    {
        RecordedUtterances.Add(fallbackUtterance);
        speechSynthesizer ??= new SpeechSynthesizer();
        // NSSpeechSynthesizer.startSpeaking (CompanionManager.swift:764) does not wait for the voice to finish, so neither does this.
        speechSynthesizer.SpeakAsync(fallbackUtterance);
        return Task.CompletedTask;
    }
}
