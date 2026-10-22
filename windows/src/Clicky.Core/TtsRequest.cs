using System.Text.Json.Nodes;

namespace Clicky.Core;

/// <summary>
/// Seam 5: the Worker /tts request and its failure mapping, ported from ElevenLabsTTSClient.swift:33-61 and
/// CompanionManager.swift:702-711, 761-766 (parity TTS-01, TTS-03).
/// </summary>
public static class TtsRequest
{
    public const string AcceptHeaderValue = "audio/mpeg";
    public const string ElevenLabsModelId = "eleven_flash_v2_5";
    public const double VoiceStability = 0.5;
    public const double VoiceSimilarityBoost = 0.75;

    /// <summary>CompanionManager.swift:762. Spoken by the system voice for any TTS or response error, not only 402.</summary>
    public const string CreditsErrorFallbackUtterance =
        "I'm all out of credits. Please DM Farza and tell him to bring me back to life.";

    /// <summary>Returns null when spokenText is blank: the caller must then make no /tts call (CompanionManager.swift:702).</summary>
    public static JsonObject? BuildRequestBodyOrNullWhenBlank(string spokenText)
    {
        if (string.IsNullOrWhiteSpace(spokenText))
        {
            return null;
        }

        return new JsonObject
        {
            ["text"] = spokenText,
            ["model_id"] = ElevenLabsModelId,
            ["voice_settings"] = new JsonObject
            {
                ["stability"] = VoiceStability,
                ["similarity_boost"] = VoiceSimilarityBoost,
            },
        };
    }

    /// <summary>ElevenLabsTTSClient.swift:57-61 throws on non-2xx, which CompanionManager.swift:707-711 turns into the fallback.</summary>
    public static string? FallbackUtteranceForHttpStatus(int httpStatusCode) =>
        httpStatusCode is >= 200 and <= 299 ? null : CreditsErrorFallbackUtterance;
}
