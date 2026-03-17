namespace AgentStatus.Core.Common;

/// <summary>
/// Generic state of an AI coding session, independent of the underlying AI system.
/// </summary>
public enum AISessionState
{
    /// <summary>AI finished its turn, waiting for user input.</summary>
    Idle,

    /// <summary>User sent a message, AI is thinking.</summary>
    Thinking,

    /// <summary>AI is generating a response or orchestrating actions.</summary>
    Working,

    /// <summary>A tool is currently executing.</summary>
    ExecutingTool,

    /// <summary>AI asked the user a question or needs approval.</summary>
    WaitingForUser,

    /// <summary>Session task completed.</summary>
    Done,

    /// <summary>State could not be determined.</summary>
    Unknown,
}

/// <summary>
/// The operating mode of an AI coding session.
/// </summary>
public enum AISessionMode
{
    Interactive,
    Plan,
    Autopilot,
}

/// <summary>
/// Base class for agent session information, agnostic of the specific AI system.
/// Subclass this for system-specific session data (e.g., process IDs, API handles).
/// </summary>
public class AISessionInfo
{
    public string SessionId { get; init; } = string.Empty;

    /// <summary>Working directory of the session.</summary>
    public string Cwd { get; set; } = string.Empty;

    /// <summary>Repository (owner/name) if available.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>Git branch.</summary>
    public string Branch { get; set; } = string.Empty;

    /// <summary>Session summary text.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Current state of the session.</summary>
    public AISessionState State { get; set; } = AISessionState.Unknown;

    /// <summary>Current operating mode.</summary>
    public AISessionMode Mode { get; set; } = AISessionMode.Interactive;

    /// <summary>Timestamp of the last event processed.</summary>
    public DateTime LastEventTimestamp { get; set; }

    /// <summary>PID of the shell process hosting this session (e.g. pwsh.exe, cmd.exe).</summary>
    public int ShellPid { get; set; }

    /// <summary>Name of the host application window (e.g. "Terminal", "Visual Studio").</summary>
    public string HostAppName { get; set; } = string.Empty;

    /// <summary>Pending question for the user, if any.</summary>
    public string? PendingQuestion { get; set; }

    /// <summary>Pending choices for the user, if any.</summary>
    public string[]? PendingChoices { get; set; }

    /// <summary>Whether there's a pending question with choices.</summary>
    public bool HasPendingChoices => PendingChoices is { Length: > 0 };

    /// <summary>Commands awaiting user approval.</summary>
    public List<PendingCommand>? PendingCommands { get; set; }

    /// <summary>Whether there are commands waiting for approval.</summary>
    public bool HasPendingCommands => PendingCommands is { Count: > 0 };

    /// <summary>Whether the session needs user attention.</summary>
    public bool NeedsAttention => HasPendingChoices || HasPendingCommands;

    /// <summary>The last user message sent in the session.</summary>
    public string? LastUserMessage { get; set; }

    /// <summary>The current intent/activity.</summary>
    public string? CurrentIntent { get; set; }

    /// <summary>
    /// Short display name for the session (always the folder name).
    /// </summary>
    public virtual string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(Cwd))
            {
                string name = Path.GetFileName(Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name))
                    return Path.GetPathRoot(Cwd) ?? Cwd;
                return name;
            }

            return SessionId.Length >= 8 ? SessionId[..8] : SessionId;
        }
    }
}

/// <summary>
/// A command awaiting user approval.
/// </summary>
public sealed class PendingCommand
{
    public string ToolName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Command { get; init; }
}
