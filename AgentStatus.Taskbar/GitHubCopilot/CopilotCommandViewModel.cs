using Microsoft.CommandPalette.Extensions;

namespace AgentStatusTaskbar.GitHubCopilot;

/// <summary>
/// Copilot-specific CommandViewModel that exposes the underlying command
/// for state updates by <see cref="CopilotSessionManager"/>.
/// </summary>
public partial class CopilotCommandViewModel : CommandViewModel
{
    public CopilotCommandViewModel(ICommand command) : base(command)
    {
    }

    /// <summary>Returns the underlying command for state updates.</summary>
    internal ICommand GetCommand() => base.Model;
}
