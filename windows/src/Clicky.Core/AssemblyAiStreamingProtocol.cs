using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Clicky.Core;

/// <summary>Mirrors Swift's AssemblyAIStreamingTranscriptionProviderError: a failure with a readable message.</summary>
public sealed class AssemblyAiTranscriptionException(string message) : Exception(message);

/// <summary>
/// Parses the Worker's /transcribe-token response, ported from
/// AssemblyAIStreamingTranscriptionProvider.swift:61-84 (parity spec STT-01).
/// </summary>
public static class AssemblyAiTemporaryTokenResponseParser
{
    /// <summary>Returns the token, or throws when the status is not 2xx or the body has no string "token".</summary>
    public static string ParseTemporaryToken(int httpStatusCode, string responseBody)
    {
        if (httpStatusCode is < 200 or > 299)
        {
            throw new AssemblyAiTranscriptionException(
                $"Failed to fetch AssemblyAI token (HTTP {httpStatusCode}): {responseBody}");
        }

        try
        {
            using JsonDocument responseDocument = JsonDocument.Parse(responseBody);
            JsonElement responseObject = responseDocument.RootElement;
            if (responseObject.ValueKind == JsonValueKind.Object
                && responseObject.TryGetProperty("token", out JsonElement tokenProperty)
                && tokenProperty.ValueKind == JsonValueKind.String)
            {
                return tokenProperty.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Swift's try? turns unparseable JSON into the same "invalid response" error below.
        }

        throw new AssemblyAiTranscriptionException("Invalid token response from proxy.");
    }
}

/// <summary>
/// Builds the AssemblyAI v3 streaming websocket URL, ported from
/// AssemblyAIStreamingTranscriptionProvider.swift:437-477 (parity spec STT-01).
/// </summary>
public static class AssemblyAiWebsocketUrlBuilder
{
    public const string WebsocketBaseUrl = "wss://streaming.assemblyai.com/v3/ws";

    // Characters Foundation's URLComponents.queryItems leaves unescaped in a value: the RFC 3986 query
    // set, minus '&', '=' and '+' so a value can never be misread as a separator or a space.
    private const string UnescapedQueryValueCharacters =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~!$'()*,;:@/?";

    // Swift's JSONSerialization writes non-ASCII as-is; the default encoder would \u-escape it.
    private static readonly JsonSerializerOptions KeytermsJsonOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string BuildWebsocketUrl(string temporaryToken, IReadOnlyList<string> keyterms)
    {
        var queryParameters = new List<KeyValuePair<string, string>>
        {
            new("sample_rate", "16000"),
            new("encoding", "pcm_s16le"),
            new("format_turns", "true"),
            new("speech_model", "u3-rt-pro"),
        };

        List<string> normalizedKeyterms = keyterms
            .Select(keyterm => keyterm.Trim())
            .Where(trimmedKeyterm => trimmedKeyterm.Length > 0)
            .ToList();

        // keyterms_prompt is sent only when at least one keyterm survives trimming.
        if (normalizedKeyterms.Count > 0)
        {
            queryParameters.Add(new("keyterms_prompt", JsonSerializer.Serialize(normalizedKeyterms, KeytermsJsonOptions)));
        }

        queryParameters.Add(new("token", temporaryToken));

        string query = string.Join("&", queryParameters.Select(
            queryParameter => queryParameter.Key + "=" + PercentEncodeQueryValue(queryParameter.Value)));
        return WebsocketBaseUrl + "?" + query;
    }

    private static string PercentEncodeQueryValue(string queryValue)
    {
        var encodedQueryValue = new StringBuilder();
        foreach (byte utf8Byte in Encoding.UTF8.GetBytes(queryValue))
        {
            if (utf8Byte < 0x80 && UnescapedQueryValueCharacters.Contains((char)utf8Byte))
            {
                encodedQueryValue.Append((char)utf8Byte);
            }
            else
            {
                encodedQueryValue.Append('%').Append(utf8Byte.ToString("X2"));
            }
        }
        return encodedQueryValue.ToString();
    }
}
