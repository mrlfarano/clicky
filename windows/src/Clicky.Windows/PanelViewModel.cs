using System.Windows.Input;
using Clicky.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clicky.Windows;

/// <summary>One row of the panel's PERMISSIONS section (CompanionPanelView.swift:439-490).</summary>
internal sealed record PanelPermissionRow(string Title, ICommand GrantCommand);

/// <summary>
/// The companion panel's state behind <see cref="CompanionPanelView"/> (WS-19 part 1, T-032): the header status
/// (PNL-06), the microphone-only permissions section (PNL-04) and the Sonnet/Opus model picker (PNL-03, CLA-08).
/// The shell calls <see cref="Refresh"/> with the live microphone, overlay and voice state whenever the panel shows.
/// </summary>
internal sealed partial class PanelViewModel : ObservableObject
{
    /// <summary>CompanionPanelView.swift:610-611.</summary>
    public const string SonnetModelId = CompanionSettings.DefaultClaudeModel;

    public const string OpusModelId = "claude-opus-4-6";

    /// <summary>Unpackaged apps get no consent prompt, so Grant opens the Settings page instead (audit F4).</summary>
    public const string MicrophonePrivacySettingsUri = "ms-settings:privacy-microphone";

    private readonly ClickyUserSettingsStore userSettingsStore;
    private readonly Action<string> onSelectedClaudeModelChanged;
    private CompanionSettings currentUserSettings;

    [ObservableProperty]
    private CompanionPanelStatus panelStatus;

    // The Swift panel hides the picker until setup is done (CompanionPanelView.swift:28-34).
    [ObservableProperty]
    private bool isModelPickerVisible;

    // Shown only while a permission is missing (CompanionPanelView.swift:36-42); the microphone is the only one.
    [ObservableProperty]
    private bool isPermissionsSectionVisible;

    // The "permissions were revoked" copy (CompanionPanelView.swift:150-160), reduced to the microphone (PNL-04).
    [ObservableProperty]
    private bool isPermissionsRevokedCopyVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSonnetSelected), nameof(IsOpusSelected))]
    private string selectedClaudeModel;

    /// <param name="launchUri">Opens a URI with the shell; tests pass a fake so nothing really opens.</param>
    /// <param name="onSelectedClaudeModelChanged">Applies a newly chosen model to the next request (CompanionManager.swift:116).</param>
    public PanelViewModel(
        ClickyUserSettingsStore userSettingsStore,
        CompanionSettings currentUserSettings,
        Action<string> launchUri,
        Action<string> onSelectedClaudeModelChanged)
    {
        this.userSettingsStore = userSettingsStore;
        this.currentUserSettings = currentUserSettings;
        this.onSelectedClaudeModelChanged = onSelectedClaudeModelChanged;
        selectedClaudeModel = currentUserSettings.SelectedClaudeModel;
        // Accessibility, Screen Recording and Screen Content are dropped on Windows (audit C4, D3, D4; ADR).
        PermissionRows = [new PanelPermissionRow("Microphone", new RelayCommand(() => launchUri(MicrophonePrivacySettingsUri)))];
        panelStatus = CompanionPanelStatus.From(currentUserSettings.HasCompletedOnboarding, false, false, CompanionVoiceState.Idle);
        Refresh(hasMicrophonePermission: false, isOverlayVisible: false, CompanionVoiceState.Idle);
    }

    public IReadOnlyList<PanelPermissionRow> PermissionRows { get; }

    public bool IsSonnetSelected => SelectedClaudeModel == SonnetModelId;

    public bool IsOpusSelected => SelectedClaudeModel == OpusModelId;

    public void Refresh(bool hasMicrophonePermission, bool isOverlayVisible, CompanionVoiceState voiceState)
    {
        bool hasCompletedOnboarding = currentUserSettings.HasCompletedOnboarding;
        PanelStatus = CompanionPanelStatus.From(hasCompletedOnboarding, hasMicrophonePermission, isOverlayVisible, voiceState);
        IsModelPickerVisible = hasCompletedOnboarding && hasMicrophonePermission;
        IsPermissionsSectionVisible = !hasMicrophonePermission;
        IsPermissionsRevokedCopyVisible = hasCompletedOnboarding && !hasMicrophonePermission;
    }

    /// <summary>CompanionManager.swift:113-117: persist the choice, then use it for the next request.</summary>
    [RelayCommand]
    private void SelectClaudeModel(string modelId)
    {
        if (modelId == SelectedClaudeModel)
        {
            return;
        }
        CompanionSettings updatedUserSettings = currentUserSettings with { SelectedClaudeModel = modelId };
        try
        {
            userSettingsStore.Save(updatedUserSettings);
        }
        catch (Exception saveException) when (saveException is IOException or UnauthorizedAccessException)
        {
            // The write is atomic, so the old file is intact; keep the old selection so the picker stays truthful.
            ClickyLog.Write($"settings: could not save selectedClaudeModel: {saveException.Message}");
            OnPropertyChanged(nameof(IsSonnetSelected));
            OnPropertyChanged(nameof(IsOpusSelected));
            return;
        }
        currentUserSettings = updatedUserSettings;
        SelectedClaudeModel = modelId;
        onSelectedClaudeModelChanged(modelId);
    }
}
