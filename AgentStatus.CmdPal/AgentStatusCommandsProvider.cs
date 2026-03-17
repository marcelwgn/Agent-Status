using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AgentStatusCmdPal;

public partial class AgentStatusCommandsProvider : CommandProvider
{
    private readonly AgentStatusDockBand _dockBand = new();

    public AgentStatusCommandsProvider()
    {
        DisplayName = "Agent Status";
        Icon = SessionIcons.Provider;
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return [];
    }

    public override ICommandItem[] GetDockBands()
    {
        return [_dockBand];
    }
}
