using System.Windows.Input;
using Clicky.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clicky.Windows;

/// <summary>One row of the panel's PERMISSIONS section (CompanionPanelView.swift:439-490).</summary>
internal sealed record PanelPermissionRow(string Title, ICommand GrantCommand);

/// <summary>One segment of the push-to-talk shortcut picker, a Windows addition (ADR: "configurable from day one").</summary>
internal sealed partial class PanelShortcutChoice(PushToTalkShortcutOption shortcutOption, string label) : ObservableObject
{
    public PushToTalkShortcutOption ShortcutOption { get; } = shortcutOption;

    /// <summary>Windows key names for BuddyPushToTalkShortcut.displayText (BuddyDictationManager.swift:24-37).</summary>
    public string Label { get; } = label;

    [ObservableProperty]
    private bool isSelected;
}

/// <summary>
/// The companion panel's state behind <see cref="CompanionPanelView"/> (WS-19, T-032 and T-033): the header status
/// (PNL-06), the onboarding copy, the microphone-only permissions section (PNL-04), the Sonnet/Opus model picker
/// (PNL-03, CLA-08), the shortcut picker, the feedback and Report-a-problem links, and the footer.
/// The shell calls <see cref="Refresh"/> with the live microphone, overlay and voice state whenever the panel shows.
/// </summary>
internal sealed partial class PanelViewModel : ObservableObject
{
    /// <summary>CompanionPanelView.swift:610-611.</summary>
    public const string SonnetModelId = CompanionSettings.DefaultClaudeModel;

    public const string OpusModelId = "claude-opus-4-6";

    /// <summary>Unpackaged apps get no consent prompt, so Grant opens the Settings page instead (audit F4).</summary>
    public const string MicrophonePrivacySettingsUri = "ms-settings:privacy-microphone";

    /// <summary>The "Got feedback? DM me" link (CompanionPanelView.swift:648).</summary>
    public const string FeedbackUri = "https://x.com/farzatv";

    /// <summary>The Windows port's repository, where Windows problems are triaged (WS-11).</summary>
    public const string NewGitHubIssueUri = "https://github.com/mrlfarano/clicky/issues/new";

    private readonly ClickyUserSettingsStore userSettingsStore;
    private readonly Action<string> launchUri;
    private readonly Action<string> onSelectedClaudeModelChanged;
    private readonly Action<PushToTalkShortcutOption> onPushToTalkShortcutChanged;
    private readonly Action quitApplication;
    private readonly OnboardingFlow onboardingFlow;
    private CompanionSettings currentUserSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PushToTalkHintText))]
    private CompanionPanelStatus panelStatus;

    // Onboarded with the microphone granted: the model and shortcut pickers and the feedback link show
    // (CompanionPanelView.swift:28-34, 61-67).
    [ObservableProperty]
    private bool isSetupComplete;

    // The intro copy before onboarding and the microphone grant (CompanionPanelView.swift:162-179).
    [ObservableProperty]
    private bool isIntroCopyVisible;

    // "Drop your email…" with the field and Submit (CompanionPanelView.swift:134-143, 187-222).
    [ObservableProperty]
    private bool isEmailStepVisible;

    // "You're all set…" with Start (CompanionPanelView.swift:144-148, 223-239).
    [ObservableProperty]
    private bool isStartStepVisible;

    // Shown only while a permission is missing (CompanionPanelView.swift:36-42); the microphone is the only one.
    [ObservableProperty]
    private bool isPermissionsSectionVisible;

    // The "permissions were revoked" copy (CompanionPanelView.swift:150-160), reduced to the microphone (PNL-04).
    [ObservableProperty]
    private bool isPermissionsRevokedCopyVisible;

    // CompanionPanelView.swift:699.
    [ObservableProperty]
    private bool isWatchOnboardingAgainVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSonnetSelected), nameof(IsOpusSelected))]
    private string selectedClaudeModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitEmailCommand))]
    private string emailInput = "";

    /// <param name="launchUri">Opens a URI or folder with the shell; tests pass a fake so nothing really opens.</param>
    /// <param name="onSelectedClaudeModelChanged">Applies a newly chosen model to the next request (CompanionManager.swift:116).</param>
    /// <param name="onPushToTalkShortcutChanged">Hot-swaps the hook's shortcut (GlobalPushToTalkShortcutMonitor.ChangeShortcut, T-007).</param>
    /// <param name="quitApplication">PNL-05's ordered stop path.</param>
    /// <param name="onboardingFlow">Submit, Start and Watch Onboarding Again (ONB-01, ONB-05).</param>
    public PanelViewModel(
        ClickyUserSettingsStore userSettingsStore,
        CompanionSettings currentUserSettings,
        Action<string> launchUri,
        Action<string> onSelectedClaudeModelChanged,
        Action<PushToTalkShortcutOption> onPushToTalkShortcutChanged,
        Action quitApplication,
        OnboardingFlow onboardingFlow)
    {
        this.userSettingsStore = userSettingsStore;
        this.currentUserSettings = currentUserSettings;
        this.launchUri = launchUri;
        this.onSelectedClaudeModelChanged = onSelectedClaudeModelChanged;
        this.onPushToTalkShortcutChanged = onPushToTalkShortcutChanged;
        this.quitApplication = quitApplication;
        this.onboardingFlow = onboardingFlow;
        selectedClaudeModel = currentUserSettings.SelectedClaudeModel;
        // Accessibility, Screen Recording and Screen Content are dropped on Windows (audit C4, D3, D4; ADR).
        PermissionRows = [new PanelPermissionRow("Microphone", new RelayCommand(() => launchUri(MicrophonePrivacySettingsUri)))];
        // "Option" is the Alt key on Windows (PushToTalkDetector); no spaces, so the default hint stays PNL-06's copy.
        ShortcutChoices =
        [
            new PanelShortcutChoice(PushToTalkShortcutOption.ControlOption, "Ctrl+Alt"),
            new PanelShortcutChoice(PushToTalkShortcutOption.ShiftControl, "Shift+Ctrl"),
            new PanelShortcutChoice(PushToTalkShortcutOption.ControlOptionSpace, "Ctrl+Alt+Space"),
            new PanelShortcutChoice(PushToTalkShortcutOption.ShiftControlSpace, "Shift+Ctrl+Space"),
        ];
        MarkSelectedShortcutChoice();
        panelStatus = CompanionPanelStatus.From(currentUserSettings.HasCompletedOnboarding, false, false, CompanionVoiceState.Idle);
        Refresh(hasMicrophonePermission: false, isOverlayVisible: false, CompanionVoiceState.Idle);
    }

    public IReadOnlyList<PanelPermissionRow> PermissionRows { get; }

    public IReadOnlyList<PanelShortcutChoice> ShortcutChoices { get; }

    public bool IsSonnetSelected => SelectedClaudeModel == SonnetModelId;

    public bool IsOpusSelected => SelectedClaudeModel == OpusModelId;

    /// <summary>PNL-06's hint, naming the chosen shortcut; null while the panel shows setup copy instead.</summary>
    public string? PushToTalkHintText => PanelStatus.PushToTalkHintText is null
        ? null
        : $"Hold {ShortcutChoices.Single(shortcutChoice => shortcutChoice.ShortcutOption == currentUserSettings.PushToTalkShortcut).Label} to talk.";

    public void Refresh(bool hasMicrophonePermission, bool isOverlayVisible, CompanionVoiceState voiceState)
    {
        bool hasCompletedOnboarding = currentUserSettings.HasCompletedOnboarding;
        PanelStatus = CompanionPanelStatus.From(hasCompletedOnboarding, hasMicrophonePermission, isOverlayVisible, voiceState);
        IsSetupComplete = hasCompletedOnboarding && hasMicrophonePermission;
        IsIntroCopyVisible = !hasCompletedOnboarding && !hasMicrophonePermission;
        IsEmailStepVisible = !hasCompletedOnboarding && hasMicrophonePermission && !currentUserSettings.HasSubmittedEmail;
        IsStartStepVisible = !hasCompletedOnboarding && hasMicrophonePermission && currentUserSettings.HasSubmittedEmail;
        IsPermissionsSectionVisible = !hasMicrophonePermission;
        IsPermissionsRevokedCopyVisible = hasCompletedOnboarding && !hasMicrophonePermission;
        IsWatchOnboardingAgainVisible = hasCompletedOnboarding;
    }

    /// <summary>CompanionManager.swift:113-117: persist the choice, then use it for the next request.</summary>
    [RelayCommand]
    private void SelectClaudeModel(string modelId)
    {
        if (modelId == SelectedClaudeModel)
        {
            return;
        }
        if (!TrySaveUserSettings(currentUserSettings with { SelectedClaudeModel = modelId }, "selectedClaudeModel"))
        {
            // Keep the old selection so the picker stays truthful.
            OnPropertyChanged(nameof(IsSonnetSelected));
            OnPropertyChanged(nameof(IsOpusSelected));
            return;
        }
        SelectedClaudeModel = modelId;
        onSelectedClaudeModelChanged(modelId);
    }

    /// <summary>Persists pushToTalkShortcut, then hot-swaps the running hook so the new shortcut works at once.</summary>
    [RelayCommand]
    private void SelectPushToTalkShortcut(PushToTalkShortcutOption shortcutOption)
    {
        if (shortcutOption != currentUserSettings.PushToTalkShortcut
            && TrySaveUserSettings(currentUserSettings with { PushToTalkShortcut = shortcutOption }, "pushToTalkShortcut"))
        {
            onPushToTalkShortcutChanged(shortcutOption);
            OnPropertyChanged(nameof(PushToTalkHintText));
        }
        // Also after a failed save, so a clicked segment falls back to the shortcut that is really active.
        MarkSelectedShortcutChoice();
    }

    /// <summary>WS-11: the local crash reports never upload themselves, so the user attaches them to a prefilled issue.</summary>
    [RelayCommand]
    private void ReportProblem()
    {
        // The folder only exists after a first crash; opening a missing folder would show an Explorer error.
        Directory.CreateDirectory(ClickyCrashReporter.CrashDirectoryPath);
        launchUri(ClickyCrashReporter.CrashDirectoryPath);
        launchUri(BuildNewGitHubIssueUri());
    }

    [RelayCommand]
    private void OpenFeedbackLink() => launchUri(FeedbackUri);

    [RelayCommand]
    private void Quit() => quitApplication();

    /// <summary>CompanionManager.swift:153-173: hasSubmittedEmail is written only once FormSpark accepted the email.</summary>
    [RelayCommand(CanExecute = nameof(CanSubmitEmail))]
    private async Task SubmitEmailAsync()
    {
        if (await onboardingFlow.SubmitEmailAsync(EmailInput)
            && TrySaveUserSettings(currentUserSettings with { HasSubmittedEmail = true }, "hasSubmittedEmail"))
        {
            // The email step only shows with the microphone granted and onboarding not done, so Start is next.
            IsEmailStepVisible = false;
            IsStartStepVisible = true;
        }
    }

    // ONB-01: Submit is enabled only for a non-blank trimmed value.
    private bool CanSubmitEmail() => !string.IsNullOrWhiteSpace(EmailInput);

    /// <summary>CompanionManager.swift:201-218. A failed save still runs onboarding; the Start step then returns next launch.</summary>
    [RelayCommand]
    private void StartOnboarding()
    {
        TrySaveUserSettings(currentUserSettings with { HasCompletedOnboarding = true }, "hasCompletedOnboarding");
        onboardingFlow.Start();
    }

    [RelayCommand]
    private void WatchOnboardingAgain() => onboardingFlow.Replay();

    private bool TrySaveUserSettings(CompanionSettings updatedUserSettings, string changedSettingName)
    {
        try
        {
            userSettingsStore.Save(updatedUserSettings);
        }
        catch (Exception saveException) when (saveException is IOException or UnauthorizedAccessException)
        {
            // The write is atomic, so the old file is intact and the old value stays in effect.
            ClickyLog.Write($"settings: could not save {changedSettingName}: {saveException.Message}");
            return false;
        }
        currentUserSettings = updatedUserSettings;
        return true;
    }

    private void MarkSelectedShortcutChoice()
    {
        foreach (PanelShortcutChoice shortcutChoice in ShortcutChoices)
        {
            shortcutChoice.IsSelected = shortcutChoice.ShortcutOption == currentUserSettings.PushToTalkShortcut;
        }
    }

    // Version and OS only: no folder path (it holds the Windows user name), and the crash files stay local
    // until the user chooses to attach them.
    private static string BuildNewGitHubIssueUri()
    {
        string clickyVersion = typeof(PanelViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";
        string issueBody =
            $"**What happened?**\n\n\n**Clicky version:** {clickyVersion}\n**Windows:** {Environment.OSVersion.VersionString}\n\n"
            + "If Clicky crashed, attach the newest .txt from the crashes folder that opened with this page.";
        return $"{NewGitHubIssueUri}?title={Uri.EscapeDataString("[Windows] ")}&body={Uri.EscapeDataString(issueBody)}";
    }
}
