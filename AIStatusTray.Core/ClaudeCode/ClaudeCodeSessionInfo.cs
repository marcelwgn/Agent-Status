using AIStatusTray.Core.Common;

namespace AIStatusTray.Core.ClaudeCode;

/// <summary>
/// Claude Code-specific session information extending the generic AISessionInfo
/// with process details needed for terminal interaction.
/// </summary>
public sealed class ClaudeCodeSessionInfo : AISessionInfo
{
    /// <summary>PID of the claude process.</summary>
    public int ClaudePid { get; init; }

    /// <summary>Project directory name used in ~/.claude/projects/ (e.g. "D--AI-Status").</summary>
    public string ProjectDirName { get; init; } = string.Empty;
}
