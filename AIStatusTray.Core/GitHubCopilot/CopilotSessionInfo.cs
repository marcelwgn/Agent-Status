using AIStatusTray.Core.Common;

namespace AIStatusTray.Core.GitHubCopilot;

/// <summary>
/// Copilot-specific session information extending the generic AISessionInfo
/// with process tree details needed for terminal interaction.
/// </summary>
public sealed class CopilotSessionInfo : AISessionInfo
{
    /// <summary>PID of copilot.exe</summary>
    public int CopilotPid { get; init; }

    /// <summary>PID of the parent process</summary>
    public int ParentPid { get; set; }
}
