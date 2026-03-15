using AIStatusTray.Core.Common;
using AIStatusTray.Core.GitHubCopilot;
using AIStatusTray.Core.ClaudeCode;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIStatusTray.CmdPalExtension;

/// <summary>
/// Dock band that shows live AI session status in the Command Palette dock.
/// Each session appears as a button with an icon reflecting its current state.
/// </summary>
internal sealed partial class AIStatusDockBand : WrappedDockItem
{
    private readonly CopilotSessionManager _copilotManager;
    private readonly ClaudeCodeSessionManager _claudeManager;

    public AIStatusDockBand()
        : base([], "com.aistatus.sessions", "AI Sessions")
    {
        Icon = new IconInfo("\uE9D5");

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
                Icon = GetIconForState(session.State),
            });
        }

        foreach (AISessionInfo session in _claudeManager.Sessions)
        {
            items.Add(new ListItem(new SessionFocusCommand(session))
            {
                Title = session.DisplayName,
                Subtitle = FormatState(session),
                Icon = GetIconForState(session.State),
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

    private static IconInfo GetIconForState(AISessionState state) => state switch
    {
        AISessionState.Idle => new IconInfo("\uE8BD"),
        AISessionState.Thinking => new IconInfo("\uE9CE"),
        AISessionState.Working => new IconInfo("\uE9F5"),
        AISessionState.ExecutingTool => new IconInfo("\uE90F"),
        AISessionState.WaitingForUser => new IconInfo("\uEA39"),
        AISessionState.Done => new IconInfo("\uE930"),
        _ => new IconInfo("\uE946"),
    };
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
        Icon = GetIconForState(session.State);
    }

    public override ICommandResult Invoke()
    {
        Task.Run(() => ShowWindowHelper.BringToFront(_session.ShellPid));
        return CommandResult.Dismiss();
    }

    private static IconInfo GetIconForState(AISessionState state) => state switch
    {
        AISessionState.Idle => new IconInfo("\uE8BD"),
        AISessionState.Thinking => new IconInfo("\uE9CE"),
        AISessionState.Working => new IconInfo("\uE9F5"),
        AISessionState.ExecutingTool => new IconInfo("\uE90F"),
        AISessionState.WaitingForUser => new IconInfo("\uEA39"),
        AISessionState.Done => new IconInfo("\uE930"),
        _ => new IconInfo("\uE946"),
    };
}
