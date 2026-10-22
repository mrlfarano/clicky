using System.Text.Json.Nodes;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class ClaudeRequestGoldenVectorTests
{
    private const string SeamName = "claude-request";

    public static TheoryData<string> CaseIds => GoldenVectors.CaseIds(SeamName);

    private static readonly string CompanionVoiceResponsePrompt = File.ReadAllText(
        Path.Combine(GoldenVectors.RepositoryRootDirectory, "shared", "prompts", "companion-voice-response.txt"));

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void RequestBodyMatchesGoldenVector(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);
        JsonObject expected = vectorCase["expected"]!.AsObject();

        JsonObject requestBody = BuildRequestBody(vectorCase["input"]!);

        Assert.Equal((string)expected["model"]!, (string)requestBody["model"]!);
        Assert.Equal(1024, (int)expected["max_tokens"]!);
        Assert.Equal((int)expected["max_tokens"]!, (int)requestBody["max_tokens"]!);
        Assert.True((bool)expected["stream"]!);
        Assert.Equal((bool)expected["stream"]!, (bool)requestBody["stream"]!);
        Assert.Equal(CompanionVoiceResponsePrompt, (string)requestBody["system"]!);
        Assert.True(
            JsonNode.DeepEquals(expected["messages"], requestBody["messages"]),
            $"messages differ.\nexpected: {expected["messages"]!.ToJsonString()}\nactual:   {requestBody["messages"]!.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void HistoryIsAlternatingPlainStringsAndImagesAreOnlyInTheLastUserTurn(string caseId)
    {
        JsonNode input = GoldenVectors.LoadCase(SeamName, caseId)["input"]!;
        JsonArray conversationHistory = input["conversationHistory"]!.AsArray();
        JsonArray images = input["images"]!.AsArray();

        JsonArray messages = BuildRequestBody(input)["messages"]!.AsArray();

        Assert.Equal(conversationHistory.Count * 2 + 1, messages.Count);
        for (int exchangeIndex = 0; exchangeIndex < conversationHistory.Count; exchangeIndex++)
        {
            JsonNode userTurn = messages[exchangeIndex * 2]!;
            JsonNode assistantTurn = messages[exchangeIndex * 2 + 1]!;
            Assert.Equal("user", (string)userTurn["role"]!);
            Assert.Equal((string)conversationHistory[exchangeIndex]!["userTranscript"]!, PlainStringContent(userTurn));
            Assert.Equal("assistant", (string)assistantTurn["role"]!);
            Assert.Equal((string)conversationHistory[exchangeIndex]!["assistantResponse"]!, PlainStringContent(assistantTurn));
        }

        // The last turn is [image, text(label)] per monitor in capture order, then text(T).
        JsonNode lastUserTurn = messages[^1]!;
        Assert.Equal("user", (string)lastUserTurn["role"]!);
        JsonArray contentBlocks = lastUserTurn["content"]!.AsArray();
        Assert.Equal(images.Count * 2 + 1, contentBlocks.Count);
        for (int imageIndex = 0; imageIndex < images.Count; imageIndex++)
        {
            Assert.Equal("image", (string)contentBlocks[imageIndex * 2]!["type"]!);
            JsonNode labelBlock = contentBlocks[imageIndex * 2 + 1]!;
            Assert.Equal("text", (string)labelBlock["type"]!);
            Assert.Equal((string)images[imageIndex]!["label"]!, (string)labelBlock["text"]!);
        }
        Assert.Equal("text", (string)contentBlocks[^1]!["type"]!);
        Assert.Equal((string)input["userPrompt"]!, (string)contentBlocks[^1]!["text"]!);
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void MediaTypeIsPngOnlyWhenBytesStartWithPngSignature(string caseId)
    {
        JsonObject vectorCase = GoldenVectors.LoadCase(SeamName, caseId);

        IEnumerable<string> sniffedMediaTypes = vectorCase["input"]!["images"]!.AsArray()
            .Select(image => ClaudeRequestBuilder.DetectImageMediaType(Convert.FromHexString((string)image!["bytesHex"]!)));

        Assert.Equal(
            vectorCase["expected"]!["mediaTypes"]!.AsArray().Select(mediaTypeNode => (string)mediaTypeNode!),
            sniffedMediaTypes);
    }

    private static JsonObject BuildRequestBody(JsonNode input) =>
        ClaudeRequestBuilder.BuildStreamingMessagesBody(
            (string)input["model"]!,
            CompanionVoiceResponsePrompt,
            input["images"]!.AsArray()
                .Select(image => new LabeledScreenshot(
                    Convert.FromHexString((string)image!["bytesHex"]!),
                    (string)image["label"]!))
                .ToList(),
            input["conversationHistory"]!.AsArray()
                .Select(exchange => new ConversationExchange(
                    (string)exchange!["userTranscript"]!,
                    (string)exchange["assistantResponse"]!))
                .ToList(),
            (string)input["userPrompt"]!);

    /// <summary>History content must be a bare JSON string, never a content-block array (so it can carry no image).</summary>
    private static string PlainStringContent(JsonNode turn)
    {
        JsonNode content = turn["content"]!;
        Assert.IsAssignableFrom<JsonValue>(content);
        return (string)content!;
    }
}
