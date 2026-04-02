using System.Collections.Generic;
using AgentStatus.Core.Common;
using AgentStatus.Core.GitHubCopilot;
using AgentStatus.Core.ClaudeCode;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AgentStatusCmdPal;

/// <summary>
/// Full-page list of all running agent sessions, shown when the extension
/// is selected from the Command Palette top-level commands.
/// </summary>
internal sealed partial class AgentStatusPage : ListPage
{
    private readonly CopilotSessionManager _copilotManager;
    private readonly ClaudeCodeSessionManager _claudeManager;

    public AgentStatusPage(CopilotSessionManager copilotManager, ClaudeCodeSessionManager claudeManager)
    {
        _copilotManager = copilotManager;
        _claudeManager = claudeManager;

        Icon = SessionIcons.Provider;
        Title = "Agent Status";
        Name = "Open";

        _copilotManager.SessionsChanged += (_, _) => RaiseItemsChanged();
        _claudeManager.SessionsChanged += (_, _) => RaiseItemsChanged();
    }

    public override IListItem[] GetItems()
    {
        List<IListItem> items = [];
        HashSet<string> seen = [];

        foreach (AISessionInfo session in _copilotManager.Sessions)
        {
            if (!seen.Add(session.SessionId))
                continue;

            items.Add(new ListItem(new SessionFocusCommand(session))
            {
                Title = session.DisplayName,
                Subtitle = FormatState(session),
                Icon = SessionIcons.GetIconForState(session.State),
            });
        }

        foreach (AISessionInfo session in _claudeManager.Sessions)
        {
            if (!seen.Add(session.SessionId))
                continue;

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
                Title = "No agents running",
                Icon = SessionIcons.NoSession,
            });
        }

        return items.ToArray();
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
