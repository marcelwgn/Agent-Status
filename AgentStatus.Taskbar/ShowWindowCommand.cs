using AgentStatus.Core.Common;
using System.Diagnostics;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Win32.Foundation;

namespace AgentStatusTaskbar;

/// <summary>
/// Command that brings an agent session's host application window to the foreground.
/// Traces the process tree from the shell PID up to find the parent window.
/// </summary>
internal partial class ShowWindowCommand : InvokableCommand
{
    private readonly AISessionInfo _session;

    public ShowWindowCommand(AISessionInfo session)
    {
        _session = session;
        Id = $"session-focus-{session.SessionId}";
        Name = session.DisplayName;
        Icon = GetIconForState(session.State);
    }

    public void UpdateState(AISessionState state)
    {
        Icon = GetIconForState(state);
        Name = _session.DisplayName;
    }

    public override ICommandResult Invoke()
    {
        try
        {
            ShowWindowHelper.BringToFront(_session.ShellPid);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ShowWindowCommand error: {ex.Message}");
        }

        return CommandResult.KeepOpen();
    }

    /// <summary>
    /// Walks up the process tree from the shell PID to find the host window.
    /// </summary>
    internal static HWND FindTerminalWindowPublic(int shellPid)
    {
        nint handle = ShowWindowHelper.FindTerminalWindow(shellPid);
        return new HWND(handle);
    }

    /// <summary>
    /// Returns a Segoe Fluent Icon for the given session state.
    /// </summary>
    internal static IconInfo GetIconForState(AISessionState state) => state switch
    {
        AISessionState.Idle => new IconInfo("\uE8BD"),
        AISessionState.Thinking => new IconInfo("\uE9CE"),
        AISessionState.Working => new IconInfo("\uE9F5"),
        AISessionState.ExecutingTool => new IconInfo("\uE90F"),
        AISessionState.WaitingForUser => new IconInfo("\uEA39"),
        AISessionState.Done => new IconInfo("\uE930"),
        _ => new IconInfo("\uE946"),
    };

    /// <summary>
    /// Determines the tab index for a shell PID.
    /// </summary>
    internal static int FindTabIndexPublic(int shellPid)
    {
        return ShowWindowHelper.FindTabIndex(shellPid);
    }

    /// <summary>
    /// Uses wt.exe to switch to the specified tab index.
    /// </summary>
    internal static void SwitchTerminalTabPublic(int tabIndex)
    {
        ShowWindowHelper.SwitchTerminalTab(tabIndex);
    }
}
