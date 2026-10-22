using System.Text.Json;
using System.Text.Json.Serialization;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>appsettings.json next to Clicky.exe (audit A7). WorkerBaseUrl ships empty: there is no hard-coded placeholder.</summary>
internal sealed record ClickySettings(
    string? WorkerBaseUrl,
    string ClaudeModel,
    string TracerPromptText,
    PushToTalkShortcutOption PushToTalkShortcut)
{
    public static ClickySettings LoadFromApplicationDirectory()
    {
        string settingsFilePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var settingsJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };
        ClickySettings settings = JsonSerializer.Deserialize<ClickySettings>(File.ReadAllText(settingsFilePath), settingsJsonOptions)
            ?? throw new InvalidDataException($"{settingsFilePath} is empty.");

        if (string.IsNullOrWhiteSpace(settings.ClaudeModel) || string.IsNullOrWhiteSpace(settings.TracerPromptText))
        {
            throw new InvalidDataException($"{settingsFilePath} needs non-empty ClaudeModel and TracerPromptText.");
        }
        return settings;
    }

    /// <summary>The Worker's /chat route, or null when WorkerBaseUrl is missing or not an absolute http(s) URL.</summary>
    public Uri? TryGetChatEndpoint()
    {
        if (!Uri.TryCreate(WorkerBaseUrl, UriKind.Absolute, out Uri? workerBaseUri)
            || (workerBaseUri.Scheme != Uri.UriSchemeHttps && workerBaseUri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }
        // Appending (not new Uri(base, "chat")) keeps any path prefix in the base URL.
        return new Uri(workerBaseUri.AbsoluteUri.TrimEnd('/') + "/chat");
    }
}
