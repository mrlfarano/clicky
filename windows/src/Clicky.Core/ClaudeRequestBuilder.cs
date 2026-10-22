using System.Text.Json.Nodes;

namespace Clicky.Core;

/// <summary>One captured monitor as Claude sees it: the encoded image and the label sent right after it.</summary>
public sealed record LabeledScreenshot(byte[] ImageBytes, string Label);

/// <summary>
/// Seam 4: builds the Messages API body the Worker's /chat route forwards unchanged, ported from
/// ClaudeAPI.swift:112-148. History goes first as plain-string user/assistant turns; only the current user
/// turn carries images.
/// </summary>
public static class ClaudeRequestBuilder
{
    public const int MaximumResponseTokens = 1024;

    public static JsonObject BuildStreamingMessagesBody(
        string model,
        string systemPrompt,
        IReadOnlyList<LabeledScreenshot> labeledScreenshots,
        IReadOnlyList<ConversationExchange> conversationHistory,
        string userPrompt)
    {
        var messages = new JsonArray();
        foreach (ConversationExchange conversationExchange in conversationHistory)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = conversationExchange.UserTranscript });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = conversationExchange.AssistantResponse });
        }

        var contentBlocks = new JsonArray();
        foreach (LabeledScreenshot labeledScreenshot in labeledScreenshots)
        {
            contentBlocks.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = DetectImageMediaType(labeledScreenshot.ImageBytes),
                    ["data"] = Convert.ToBase64String(labeledScreenshot.ImageBytes),
                },
            });
            contentBlocks.Add(new JsonObject { ["type"] = "text", ["text"] = labeledScreenshot.Label });
        }
        contentBlocks.Add(new JsonObject { ["type"] = "text", ["text"] = userPrompt });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = contentBlocks });

        return new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = MaximumResponseTokens,
            ["stream"] = true,
            ["system"] = systemPrompt,
            ["messages"] = messages,
        };
    }

    /// <summary>
    /// ClaudeAPI.swift:51-62: the API rejects a media_type that does not match the bytes, so sniff the
    /// PNG signature and treat everything else as JPEG (which is what screen captures are).
    /// </summary>
    public static string DetectImageMediaType(ReadOnlySpan<byte> imageBytes)
    {
        ReadOnlySpan<byte> pngSignaturePrefix = [0x89, 0x50, 0x4E, 0x47];
        return imageBytes.StartsWith(pngSignaturePrefix) ? "image/png" : "image/jpeg";
    }
}
