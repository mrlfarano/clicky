using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Clicky.Core;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// WS-19 part 1 / T-032: the panel view-model behind CompanionPanelView.xaml (parity spec PNL-03, PNL-04, PNL-06).
/// View-model level: the header status is checked against the session-reducer vector's panel-status group, the
/// Grant command against a fake URI launcher, and the model picker against the real settings store on a temp directory.
/// </summary>
public sealed class PanelViewModelTests
{
    public static TheoryData<string> PanelStatusCaseIds
    {
        get
        {
            var caseIds = new TheoryData<string>();
            foreach (JsonObject vectorCase in LoadPanelStatusCases())
            {
                caseIds.Add((string)vectorCase["id"]!);
            }
            return caseIds;
        }
    }

    [Theory]
    [MemberData(nameof(PanelStatusCaseIds))]
    public void HeaderStatusMatchesReducerVector(string caseId)
    {
        JsonObject vectorCase = LoadPanelStatusCases().Single(panelStatusCase => (string)panelStatusCase["id"]! == caseId);
        JsonNode input = vectorCase["input"]!;
        JsonNode expected = vectorCase["expected"]!;
        PanelViewModel panelViewModel = CreatePanelViewModel(
            CompanionSettings.Defaults with { HasCompletedOnboarding = (bool)input["hasCompletedOnboarding"]! },
            Path.GetTempPath(),
            launchedUris: []);

        foreach (CompanionVoiceState voiceState in Enum.GetValues<CompanionVoiceState>())
        {
            panelViewModel.Refresh((bool)input["hasMicrophonePermission"]!, (bool)input["isOverlayVisible"]!, voiceState);
            JsonNode expectedForVoiceState = expected["byVoiceState"]![voiceState.ToString().ToLowerInvariant()]!;

            Assert.Equal((string)expectedForVoiceState["statusText"]!, panelViewModel.PanelStatus.StatusText);
            Assert.Equal((string)expectedForVoiceState["dotColor"]!, JsonNamingPolicy.CamelCase.ConvertName(panelViewModel.PanelStatus.StatusDotColor.ToString()));
            Assert.Equal((string?)expected["hintText"], panelViewModel.PanelStatus.PushToTalkHintText);
        }
    }

    [Fact]
    public void OnlyMicrophoneRow()
    {
        var launchedUris = new List<string>();
        PanelViewModel panelViewModel = CreatePanelViewModel(
            CompanionSettings.Defaults with { HasCompletedOnboarding = true }, Path.GetTempPath(), launchedUris);

        panelViewModel.Refresh(hasMicrophonePermission: false, isOverlayVisible: false, CompanionVoiceState.Idle);

        // PNL-04: Accessibility, Screen Recording and Screen Content are dropped; the microphone is the one row.
        Assert.True(panelViewModel.IsPermissionsSectionVisible);
        Assert.True(panelViewModel.IsPermissionsRevokedCopyVisible);
        PanelPermissionRow microphoneRow = Assert.Single(panelViewModel.PermissionRows);
        Assert.Equal("Microphone", microphoneRow.Title);
        microphoneRow.GrantCommand.Execute(null);
        Assert.Equal(["ms-settings:privacy-microphone"], launchedUris);

        // Once granted the whole section goes, as allPermissionsGranted hides it on the Mac (CompanionPanelView.swift:36).
        panelViewModel.Refresh(hasMicrophonePermission: true, isOverlayVisible: false, CompanionVoiceState.Idle);
        Assert.False(panelViewModel.IsPermissionsSectionVisible);
        Assert.False(panelViewModel.IsPermissionsRevokedCopyVisible);
    }

    [Fact]
    public void ModelPickerPersists()
    {
        foreach ((bool hasCompletedOnboarding, bool hasMicrophonePermission) in new[] { (false, false), (false, true), (true, false) })
        {
            PanelViewModel setupIncompleteViewModel = CreatePanelViewModel(
                CompanionSettings.Defaults with { HasCompletedOnboarding = hasCompletedOnboarding }, Path.GetTempPath(), launchedUris: []);
            setupIncompleteViewModel.Refresh(hasMicrophonePermission, isOverlayVisible: false, CompanionVoiceState.Idle);
            Assert.False(setupIncompleteViewModel.IsSetupComplete, $"onboarded={hasCompletedOnboarding} mic={hasMicrophonePermission}");
        }

        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            var selectedModelsApplied = new List<string>();
            CompanionSettings onboardedSettings = CompanionSettings.Defaults with { HasCompletedOnboarding = true };
            var panelViewModel = new PanelViewModel(
                new ClickyUserSettingsStore(dataDirectoryPath), onboardedSettings, launchUri: _ => { }, selectedModelsApplied.Add,
                onPushToTalkShortcutChanged: _ => { }, quitApplication: () => { }, OnboardingFlowTests.CreateIdleOnboardingFlow());
            panelViewModel.Refresh(hasMicrophonePermission: true, isOverlayVisible: false, CompanionVoiceState.Idle);
            Assert.True(panelViewModel.IsSetupComplete);
            Assert.True(panelViewModel.IsSonnetSelected);

            panelViewModel.SelectClaudeModelCommand.Execute("claude-opus-4-6");

            Assert.True(panelViewModel.IsOpusSelected);
            Assert.False(panelViewModel.IsSonnetSelected);
            Assert.Equal(["claude-opus-4-6"], selectedModelsApplied);
            // A fresh store reads what landed on disk: the choice and nothing else changed.
            CompanionSettingsMergeResult reloaded = new ClickyUserSettingsStore(dataDirectoryPath).Load();
            Assert.False(reloaded.UserSettingsFileWasCorrupt);
            Assert.Equal(onboardedSettings with { SelectedClaudeModel = "claude-opus-4-6" }, reloaded.Settings);
            Assert.Equal("claude-opus-4-6", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(dataDirectoryPath, "settings.json")))!["selectedClaudeModel"]);
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public void ShortcutPickerWritesSettingAndHotSwaps()
    {
        string dataDirectoryPath = ClickyProcessLauncher.CreateTempDataDirectory();
        try
        {
            // Never started, so no system-wide hook is installed; ChangeShortcut only writes the translator channel.
            var shortcutMonitor = new GlobalPushToTalkShortcutMonitor(
                PushToTalkShortcutOption.ControlOption, stallFirstHookCallback: false, TimeProvider.System, new CountingKeyboardHookInstallerFake());
            CompanionSettings onboardedSettings = CompanionSettings.Defaults with { HasCompletedOnboarding = true };
            var panelViewModel = new PanelViewModel(
                new ClickyUserSettingsStore(dataDirectoryPath), onboardedSettings, launchUri: _ => { }, onSelectedClaudeModelChanged: _ => { },
                shortcutMonitor.ChangeShortcut, quitApplication: () => { }, OnboardingFlowTests.CreateIdleOnboardingFlow());
            panelViewModel.Refresh(hasMicrophonePermission: true, isOverlayVisible: false, CompanionVoiceState.Idle);
            Assert.Equal(CompanionPanelStatus.PushToTalkHintCopy, panelViewModel.PushToTalkHintText);
            Assert.Equal([PushToTalkShortcutOption.ControlOption], SelectedShortcutOptions(panelViewModel));

            panelViewModel.SelectPushToTalkShortcutCommand.Execute(PushToTalkShortcutOption.ShiftControlSpace);

            CompanionSettingsMergeResult reloaded = new ClickyUserSettingsStore(dataDirectoryPath).Load();
            Assert.Equal(onboardedSettings with { PushToTalkShortcut = PushToTalkShortcutOption.ShiftControlSpace }, reloaded.Settings);
            Assert.Equal(
                "shiftControlSpace",
                (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(dataDirectoryPath, "settings.json")))![CompanionSettingsMerger.PushToTalkShortcutSettingsFileKeyName]);
            Assert.True(shortcutMonitor.KeyboardHookSignals.Reader.TryRead(out KeyboardHookSignal hotSwapSignal));
            Assert.Equal(KeyboardHookSignal.ShortcutOptionChangedTo(PushToTalkShortcutOption.ShiftControlSpace), hotSwapSignal);
            Assert.Equal([PushToTalkShortcutOption.ShiftControlSpace], SelectedShortcutOptions(panelViewModel));
            Assert.Equal("Hold Shift+Ctrl+Space to talk.", panelViewModel.PushToTalkHintText);

            // Choosing the shortcut already in effect sends nothing more to the hook.
            panelViewModel.SelectPushToTalkShortcutCommand.Execute(PushToTalkShortcutOption.ShiftControlSpace);
            Assert.False(shortcutMonitor.KeyboardHookSignals.Reader.TryRead(out _));
        }
        finally
        {
            Directory.Delete(dataDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public void ShowClickyToggleNotRendered()
    {
        var launchedUris = new List<string>();
        // Onboarded with the microphone granted: the state in which the Mac's commented-out toggle would show (CompanionPanelView.swift:53).
        PanelViewModel panelViewModel = CreatePanelViewModel(
            CompanionSettings.Defaults with { HasCompletedOnboarding = true, HasSubmittedEmail = true }, Path.GetTempPath(), launchedUris);
        panelViewModel.Refresh(hasMicrophonePermission: true, isOverlayVisible: true, CompanionVoiceState.Idle);

        RenderedCompanionPanel.Run(panelViewModel, panelView =>
        {
            List<DependencyObject> renderedElements = RenderedCompanionPanel.WalkVisualTree(panelView, stopAtInteractiveControls: false).ToList();
            // No switch: the only toggle buttons are the picker segments.
            Assert.All(renderedElements.OfType<ToggleButton>(), toggleButton => Assert.IsType<RadioButton>(toggleButton));
            Assert.DoesNotContain(renderedElements, renderedElement => ReadRenderedText(renderedElement).Contains("Show Clicky", StringComparison.OrdinalIgnoreCase));

            Button reportProblemButton = RenderedCompanionPanel.FindInteractiveControls(panelView)
                .OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Report a problem");
            reportProblemButton.Command.Execute(reportProblemButton.CommandParameter);
        });

        Assert.Equal(2, launchedUris.Count);
        Assert.Equal(ClickyCrashReporter.CrashDirectoryPath, launchedUris[0]);
        Assert.True(Directory.Exists(ClickyCrashReporter.CrashDirectoryPath));
        var newIssueUri = new Uri(launchedUris[1]);
        Assert.Equal("https://github.com/mrlfarano/clicky/issues/new", newIssueUri.GetLeftPart(UriPartial.Path));
        string decodedIssueQuery = Uri.UnescapeDataString(newIssueUri.Query);
        Assert.StartsWith("?title=[Windows] &body=", decodedIssueQuery);
        Assert.Contains($"**Clicky version:** {typeof(PanelViewModel).Assembly.GetName().Version}", decodedIssueQuery);
        Assert.Contains($"**Windows:** {Environment.OSVersion.VersionString}", decodedIssueQuery);
        // The crash folder path carries the Windows user name, so it must not land in a public issue.
        Assert.DoesNotContain(ClickyCrashReporter.CrashDirectoryPath, decodedIssueQuery);
    }

    private static PushToTalkShortcutOption[] SelectedShortcutOptions(PanelViewModel panelViewModel) =>
        panelViewModel.ShortcutChoices.Where(shortcutChoice => shortcutChoice.IsSelected).Select(shortcutChoice => shortcutChoice.ShortcutOption).ToArray();

    private static string ReadRenderedText(DependencyObject renderedElement) => renderedElement switch
    {
        TextBlock textBlock => textBlock.Text,
        ContentControl { Content: string contentText } => contentText,
        _ => AutomationProperties.GetName(renderedElement),
    };

    // Only ModelPickerPersists and ShortcutPickerWritesSettingAndHotSwaps write settings, each to its own temp directory.
    private static PanelViewModel CreatePanelViewModel(CompanionSettings userSettings, string settingsDirectoryPath, List<string> launchedUris) =>
        new(new ClickyUserSettingsStore(settingsDirectoryPath), userSettings, launchedUris.Add, onSelectedClaudeModelChanged: _ => { },
            onPushToTalkShortcutChanged: _ => { }, quitApplication: () => { }, OnboardingFlowTests.CreateIdleOnboardingFlow());

    private static IEnumerable<JsonObject> LoadPanelStatusCases() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(ClickyReplayProcess.RepositoryRootDirectory, "shared", "golden", "session-reducer.json")))!
            ["cases"]!.AsArray()
            .Select(vectorCase => vectorCase!.AsObject())
            .Where(vectorCase => (string?)vectorCase["group"] == "panel-status");
}
