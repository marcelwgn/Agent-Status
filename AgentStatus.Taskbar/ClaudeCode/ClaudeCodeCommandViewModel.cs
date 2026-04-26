using Microsoft.CommandPalette.Extensions;

namespace AgentStatusTaskbar.ClaudeCode;

/// <summary>
/// Claude Code-specific CommandViewModel that exposes the underlying command
/// for state updates by <see cref="ClaudeCodeSessionManager"/>.
/// </summary>
public partial class ClaudeCodeCommandViewModel : CommandViewModel
{
    public ClaudeCodeCommandViewModel(ICommand command) : base(command)
    {
    }

    /// <summary>Returns the underlying command for state updates.</summary>
    internal ICommand GetCommand() => base.Model;
}
