using System.Text.Json;
using Clicky.Core;

namespace Clicky.Windows;

/// <summary>
/// Ship-time values from appsettings.json next to Clicky.exe (audit A7, plan WS-12): the Worker proxy
/// URL (ships empty: no hard-coded placeholder), the update feed URL (empty = the updater stays off,
/// WS-10), the PostHog host and project key (the public client key ClickyAnalytics.swift:16-22
/// hard-codes), the Claude model list the picker offers, and the tracer prompt (removed once WS-07
/// lands). WHICH model answers and the push-to-talk shortcut are user choices, so they live in the
/// data-directory settings.json (<see cref="ClickyUserSettingsStore"/>), never in this file.
/// </summary>
internal sealed record ClickySettings(
    string? WorkerBaseUrl,
    string? UpdateFeedUrl,
    string? PostHogHost,
    string? PostHogApiKey,
    IReadOnlyList<string> ClaudeModels,
    string TracerPromptText)
{
    /// <summary>
    /// A7: the version macOS reads from CFBundleShortVersionString comes from the assembly on Windows,
    /// never from appsettings.json, so the shipped file can never disagree with the running build.
    /// </summary>
    public string ApplicationVersion { get; } = typeof(ClickySettings).Assembly.GetName().Version?.ToString() ?? "unknown";

    public static ClickySettings LoadFromApplicationDirectory() =>
        LoadFromFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));

    /// <summary>Reads the ship-time file from an explicit path, so tests pin the file that actually ships.</summary>
    internal static ClickySettings LoadFromFile(string settingsFilePath)
    {
        var settingsJsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        ClickySettings settings = JsonSerializer.Deserialize<ClickySettings>(File.ReadAllText(settingsFilePath), settingsJsonOptions)
            ?? throw new InvalidDataException($"{settingsFilePath} is empty.");

        if (settings.ClaudeModels is null || settings.ClaudeModels.Count == 0
            || settings.ClaudeModels.Any(string.IsNullOrWhiteSpace)
            || string.IsNullOrWhiteSpace(settings.TracerPromptText))
        {
            throw new InvalidDataException($"{settingsFilePath} needs a non-empty ClaudeModels list (no blank ids) and a non-empty TracerPromptText.");
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
