using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Core;
using Xunit;

namespace Clicky.Windows.Tests;

/// <summary>
/// WS-19 / T-033 (audit B9, B10; AGENTS.md "all buttons show a pointer cursor"): the real CompanionPanelView, laid
/// out on an STA thread in every panel state, then walked. Each state collapses different sections, so walking all
/// of them reaches every control the panel can show.
/// </summary>
public sealed class PanelAccessibilityTests
{
    [Fact]
    public void EveryControlHasAutomationName()
    {
        var automationNamesSeen = new HashSet<string>();
        RenderedCompanionPanel.RunInEveryPanelState(panelView =>
        {
            foreach (Control interactiveControl in RenderedCompanionPanel.FindInteractiveControls(panelView))
            {
                string automationName = AutomationProperties.GetName(interactiveControl);
                Assert.False(
                    string.IsNullOrWhiteSpace(automationName),
                    $"{interactiveControl.GetType().Name} '{RenderedCompanionPanel.Describe(interactiveControl)}' has no AutomationProperties.Name");
                automationNamesSeen.Add(automationName);
            }
        });

        // Guards against a vacuous walk: one control from each section, templated item rows included.
        Assert.Superset(
            new HashSet<string>
            {
                "Close panel", "Email address", "Submit email", "Start", "Sonnet model", "Push to talk with Shift+Ctrl+Space",
                "Got feedback? DM me", "Grant Microphone access", "Report a problem", "Watch Onboarding Again", "Quit Clicky",
            },
            automationNamesSeen);
    }

    [Fact]
    public void EveryButtonHasHandCursor()
    {
        RenderedCompanionPanel.RunInEveryPanelState(panelView =>
        {
            foreach (ButtonBase button in RenderedCompanionPanel.FindInteractiveControls(panelView).OfType<ButtonBase>())
            {
                string buttonName = AutomationProperties.GetName(button);
                // XAML's "Hand" is a separate Cursor instance from Cursors.Hand; ToString names the cursor type.
                Assert.True(button.Cursor?.ToString() == Cursors.Hand.ToString(), $"'{buttonName}' cursor is {button.Cursor}");
                Assert.NotNull(button.ToolTip);
                Assert.Equal(0, ToolTipService.GetInitialShowDelay(button));
            }
        });
    }
}

/// <summary>Lays out the real panel view on a dedicated STA thread, as WPF requires, and walks its visual tree.</summary>
internal static class RenderedCompanionPanel
{
    /// <summary>The panel's distinct states: intro, email step, Start step, set up, and revoked after onboarding.</summary>
    private static readonly (CompanionSettings UserSettings, bool HasMicrophonePermission)[] PanelStates =
    [
        (CompanionSettings.Defaults, false),
        (CompanionSettings.Defaults, true),
        (CompanionSettings.Defaults with { HasSubmittedEmail = true }, true),
        (CompanionSettings.Defaults with { HasSubmittedEmail = true, HasCompletedOnboarding = true }, true),
        (CompanionSettings.Defaults with { HasSubmittedEmail = true, HasCompletedOnboarding = true }, false),
    ];

    public static void RunInEveryPanelState(Action<CompanionPanelView> inspectRenderedPanelView)
    {
        foreach ((CompanionSettings userSettings, bool hasMicrophonePermission) in PanelStates)
        {
            // The store is never written: nothing here selects a model or a shortcut.
            var panelViewModel = new PanelViewModel(
                new ClickyUserSettingsStore(Path.GetTempPath()), userSettings, launchUri: _ => { },
                onSelectedClaudeModelChanged: _ => { }, onPushToTalkShortcutChanged: _ => { }, quitApplication: () => { });
            panelViewModel.Refresh(hasMicrophonePermission, isOverlayVisible: false, CompanionVoiceState.Idle);
            Run(panelViewModel, inspectRenderedPanelView);
        }
    }

    public static void Run(PanelViewModel panelViewModel, Action<CompanionPanelView> inspectRenderedPanelView)
    {
        Exception? capturedException = null;
        var panelStaThread = new Thread(() =>
        {
            try
            {
                var panelView = new CompanionPanelView { DataContext = panelViewModel };
                panelView.Measure(new Size(320, double.PositiveInfinity));
                panelView.Arrange(new Rect(panelView.DesiredSize));
                // Lets deferred binding and item-generation work run before the tree is walked.
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                inspectRenderedPanelView(panelView);
            }
            catch (Exception testException)
            {
                capturedException = testException;
            }
        })
        { IsBackground = true, Name = "clicky-panel-tests" };
        panelStaThread.SetApartmentState(ApartmentState.STA);
        panelStaThread.Start();
        panelStaThread.Join();
        if (capturedException is not null)
        {
            ExceptionDispatchInfo.Capture(capturedException).Throw();
        }
    }

    /// <summary>
    /// Every button, text field and selector in the tree. The walk stops at each one, so the parts inside a stock
    /// template (a TextBox's scroll bars, say) are not counted as panel controls.
    /// </summary>
    public static List<Control> FindInteractiveControls(DependencyObject treeRoot)
    {
        var interactiveControls = new List<Control>();
        foreach (DependencyObject descendant in WalkVisualTree(treeRoot, stopAtInteractiveControls: true))
        {
            if (descendant is ButtonBase or TextBoxBase or Selector)
            {
                interactiveControls.Add((Control)descendant);
            }
        }
        return interactiveControls;
    }

    public static IEnumerable<DependencyObject> WalkVisualTree(DependencyObject treeRoot, bool stopAtInteractiveControls)
    {
        for (int childIndex = 0; childIndex < VisualTreeHelper.GetChildrenCount(treeRoot); childIndex++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(treeRoot, childIndex);
            yield return child;
            if (stopAtInteractiveControls && child is ButtonBase or TextBoxBase or Selector)
            {
                continue;
            }
            foreach (DependencyObject descendant in WalkVisualTree(child, stopAtInteractiveControls))
            {
                yield return descendant;
            }
        }
    }

    public static string Describe(Control control) => control switch
    {
        ContentControl contentControl => contentControl.Content?.ToString() ?? "",
        _ => control.Name,
    };
}
