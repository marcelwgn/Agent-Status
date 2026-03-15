using AIStatusTray.Core.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIStatusTray.Controls;

public sealed partial class SessionStateIcon : UserControl
{
    public SessionStateIcon()
    {
        InitializeComponent();
        this.Loaded += (_, _) =>
        {
            UpdateSessionState(State);
            UpdateModeState(Mode);
        };
    }

    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(AISessionState), typeof(SessionStateIcon),
            new PropertyMetadata(AISessionState.Unknown, OnStateChanged));

    public AISessionState State
    {
        get => (AISessionState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(nameof(Mode), typeof(AISessionMode), typeof(SessionStateIcon),
            new PropertyMetadata(AISessionMode.Interactive, OnModeChanged));

    public AISessionMode Mode
    {
        get => (AISessionMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SessionStateIcon icon)
        {
            icon.UpdateSessionState((AISessionState)e.NewValue);
        }
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SessionStateIcon icon)
        {
            icon.UpdateModeState((AISessionMode)e.NewValue);
        }
    }

    private void UpdateSessionState(AISessionState state)
    {
        string stateName = state switch
        {
            AISessionState.Idle => "IdleState",
            AISessionState.Thinking => "ThinkingState",
            AISessionState.Working => "WorkingState",
            AISessionState.ExecutingTool => "ExecutingToolState",
            AISessionState.WaitingForUser => "WaitingForUserState",
            AISessionState.Done => "DoneState",
            _ => "UnknownState",
        };
        VisualStateManager.GoToState(this, stateName, true);
    }

    private void UpdateModeState(AISessionMode mode)
    {
        string modeName = mode switch
        {
            AISessionMode.Interactive => "InteractiveMode",
            AISessionMode.Plan => "PlanMode",
            AISessionMode.Autopilot => "AutopilotMode",
            _ => "InteractiveMode",
        };
        VisualStateManager.GoToState(this, modeName, true);
    }
}
