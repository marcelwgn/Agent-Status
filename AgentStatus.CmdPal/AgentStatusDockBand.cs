using System.Collections.Generic;
using System.Threading.Tasks;
using AgentStatus.Core.Common;
using AgentStatus.Core.GitHubCopilot;
using AgentStatus.Core.ClaudeCode;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AgentStatusCmdPal;

/// <summary>
/// Dock band that shows live agent session status in the Command Palette dock.
/// Each session appears as a button with an icon reflecting its current state.
/// </summary>
internal sealed partial class AgentStatusDockBand : WrappedDockItem
{
    private readonly CopilotSessionManager _copilotManager;
    private readonly ClaudeCodeSessionManager _claudeManager;

    public AgentStatusDockBand()
        : base([], "com.agentstatus.sessions", "Agent Status")
    {
        Icon = SessionIcons.Provider;

        _copilotManager = new CopilotSessionManager();
        _claudeManager = new ClaudeCodeSessionManager();

        _copilotManager.SessionsChanged += (_, _) => UpdateItems();
        _claudeManager.SessionsChanged += (_, _) => UpdateItems();

        // Initial update after a brief delay to let discovery run
        Task.Run(async () =>
        {
            await Task.Delay(1000);
            UpdateItems();
        });
    }

    private void UpdateItems()
    {
        List<IListItem> items = [];

        foreach (AISessionInfo session in _copilotManager.Sessions)
        {
            items.Add(new ListItem(new SessionFocusCommand(session))
            {
                Title = session.DisplayName,
                Subtitle = FormatState(session),
                Icon = SessionIcons.GetIconForState(session.State),
            });
        }

        foreach (AISessionInfo session in _claudeManager.Sessions)
        {
            items.Add(new ListItem(new SessionFocusCommand(session))
            {
                Title = session.DisplayName,
                Subtitle = FormatState(session),
                Icon = SessionIcons.GetIconForState(session.State),
            });
        }

        if (items.Count == 0)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Subtitle = "No sessions",
                Icon = SessionIcons.NoSession,
            });
        }

        Items = items.ToArray();
    }

    private static string FormatState(AISessionInfo session)
    {
        string state = session.State switch
        {
            AISessionState.Idle => "Idle",
            AISessionState.Thinking => "Thinking",
            AISessionState.Working => "Working",
            AISessionState.ExecutingTool => "Executing",
            AISessionState.WaitingForUser => "Waiting",
            AISessionState.Done => "Done",
            _ => "Unknown",
        };

        if (session.Mode == AISessionMode.Autopilot)
            state += " (agent)";
        else if (session.Mode == AISessionMode.Plan)
            state += " (plan)";

        return state;
    }
}

/// <summary>
/// Command that brings the terminal window for a session to the foreground.
/// </summary>
internal sealed partial class SessionFocusCommand : InvokableCommand
{
    private readonly AISessionInfo _session;

    public SessionFocusCommand(AISessionInfo session)
    {
        _session = session;
        Name = $"Focus {session.DisplayName}";
        Icon = SessionIcons.GetIconForState(session.State);
    }

    public override ICommandResult Invoke()
    {
        Task.Run(() => ShowWindowHelper.BringToFront(_session.ShellPid));
        return CommandResult.Dismiss();
    }
}

/// <summary>
/// No-op command used for placeholder items like "No session".
/// </summary>
internal sealed partial class NoOpCommand : InvokableCommand
{
    public NoOpCommand()
    {
        Name = "No sessions";
    }

    public override ICommandResult Invoke() => CommandResult.KeepOpen();
}
