using AgentStatus.Core.GitHubCopilot;
using AgentStatus.Core.ClaudeCode;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AgentStatusCmdPal;

public partial class AgentStatusCommandsProvider : CommandProvider
{
    private readonly CopilotSessionManager _copilotManager;
    private readonly ClaudeCodeSessionManager _claudeManager;
    private readonly ICommandItem[] _commands;
    private readonly AgentStatusDockBand _dockBand;

    public AgentStatusCommandsProvider()
    {
        DisplayName = "Agent Status";
        Icon = SessionIcons.Provider;

        _copilotManager = new CopilotSessionManager();
        _claudeManager = new ClaudeCodeSessionManager();

        _dockBand = new AgentStatusDockBand(_copilotManager, _claudeManager);
        var page = new AgentStatusPage(_copilotManager, _claudeManager);
        _commands = [new CommandItem(page) { Title = DisplayName, Icon = SessionIcons.Provider }];
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    public override ICommandItem[] GetDockBands()
    {
        return [_dockBand];
    }
}
