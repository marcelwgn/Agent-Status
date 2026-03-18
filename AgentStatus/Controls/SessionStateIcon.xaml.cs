using AgentStatus.Core.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentStatus.Controls;

public sealed partial class SessionStateIcon : UserControl
{
    public SessionStateIcon()
    {
        InitializeComponent();
        this.Loaded += (_, _) =>
        {
            UpdateSessionState(State);
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

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SessionStateIcon icon)
        {
            icon.UpdateSessionState((AISessionState)e.NewValue);
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


}
