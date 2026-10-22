namespace Clicky.Core;

/// <summary>How Clicky's own windows (the cursor overlay, and the companion panel once it exists) stay out of screenshots.</summary>
public enum OwnWindowCaptureExclusion
{
    /// <summary>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE), Windows 10 2004 (build 19041) and later (audit D2).</summary>
    ExcludeFromCapture,

    /// <summary>Hide the window for the capture frame, because 1903/1909 do not support WDA_EXCLUDEFROMCAPTURE (audit D2).</summary>
    HideDuringCapture,
}

/// <summary>
/// The Windows.Graphics.Capture settings that depend on the OS build (audit D1/D2, CLA-01). Decided from a plain
/// build number so the shell's version branches are covered by tests on any machine.
/// </summary>
/// <param name="RequestBorderlessAccess">
/// True on 20348+, where GraphicsCaptureAccess.RequestAccessAsync(Borderless) plus IsBorderRequired=false removes
/// the yellow capture border. Older Windows 10 builds keep the border (an accepted limitation, task T-025).
/// </param>
public readonly record struct CaptureOptions(bool RequestBorderlessAccess, OwnWindowCaptureExclusion OwnWindowExclusion)
{
    public const long BorderlessCaptureMinimumBuild = 20348;
    public const long ExcludeFromCaptureMinimumBuild = 19041;

    /// <summary>The value GraphicsCaptureSession.IsBorderRequired is set to: the border stays unless borderless access is requested.</summary>
    public bool IsBorderRequired => !RequestBorderlessAccess;

    public static CaptureOptions Decide(long osBuild) => new(
        RequestBorderlessAccess: osBuild >= BorderlessCaptureMinimumBuild,
        OwnWindowExclusion: osBuild >= ExcludeFromCaptureMinimumBuild
            ? OwnWindowCaptureExclusion.ExcludeFromCapture
            : OwnWindowCaptureExclusion.HideDuringCapture);
}
