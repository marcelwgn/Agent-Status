using AgentStatus.Core.Common;

namespace AgentStatus.Core.ClaudeCode;

/// <summary>
/// Claude Code-specific session information extending the generic AISessionInfo
/// with process details needed for terminal interaction.
/// </summary>
public sealed class ClaudeCodeSessionInfo : AISessionInfo
{
    /// <summary>PID of the claude process.</summary>
    public int ClaudePid { get; init; }

    /// <summary>Project directory name used in ~/.claude/projects/ .</summary>
    public string ProjectDirName { get; init; } = string.Empty;
}
