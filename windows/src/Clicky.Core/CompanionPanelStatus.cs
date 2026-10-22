namespace Clicky.Core;

/// <summary>The DS colour token the panel header's status dot takes (CompanionPanelView.swift:728-740).</summary>
public enum CompanionStatusDotColor
{
    TextTertiary,
    Success,
    Blue400,
}

/// <summary>
/// Parity spec PNL-06: the panel header's status text and dot, and the push-to-talk hint, which is null when
/// the panel shows setup copy instead (CompanionPanelView.swift:129-130).
/// </summary>
public sealed record CompanionPanelStatus(string StatusText, CompanionStatusDotColor StatusDotColor, string? PushToTalkHintText)
{
    /// <summary>The Mac's "Hold Control+Option to talk." with Windows key names.</summary>
    public const string PushToTalkHintCopy = "Hold Ctrl+Alt to talk.";

    /// <summary>The microphone is the only Windows permission (audit D4), so it stands in for allPermissionsGranted.</summary>
    public static CompanionPanelStatus From(
        bool hasCompletedOnboarding,
        bool hasMicrophonePermission,
        bool isOverlayVisible,
        CompanionVoiceState voiceState)
    {
        bool isSetupComplete = hasCompletedOnboarding && hasMicrophonePermission;
        return new CompanionPanelStatus(
            ResolveStatusText(isSetupComplete, isOverlayVisible, voiceState),
            ResolveStatusDotColor(isOverlayVisible, voiceState),
            isSetupComplete ? PushToTalkHintCopy : null);
    }

    /// <summary>CompanionPanelView.swift:742-759.</summary>
    private static string ResolveStatusText(bool isSetupComplete, bool isOverlayVisible, CompanionVoiceState voiceState)
    {
        if (!isSetupComplete)
        {
            return "Setup";
        }
        if (!isOverlayVisible)
        {
            return "Ready";
        }
        return voiceState switch
        {
            CompanionVoiceState.Idle => "Active",
            CompanionVoiceState.Listening => "Listening",
            CompanionVoiceState.Processing => "Processing",
            CompanionVoiceState.Responding => "Responding",
            _ => throw new ArgumentOutOfRangeException(nameof(voiceState), voiceState, null),
        };
    }

    /// <summary>CompanionPanelView.swift:728-740: the dot ignores setup, so a visible overlay colours it even during Setup.</summary>
    private static CompanionStatusDotColor ResolveStatusDotColor(bool isOverlayVisible, CompanionVoiceState voiceState)
    {
        if (!isOverlayVisible)
        {
            return CompanionStatusDotColor.TextTertiary;
        }
        return voiceState == CompanionVoiceState.Idle ? CompanionStatusDotColor.Success : CompanionStatusDotColor.Blue400;
    }
}
