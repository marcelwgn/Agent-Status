using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIStatusTrayCmdPal;

public partial class AIStatusCommandsProvider : CommandProvider
{
    private readonly AIStatusDockBand _dockBand = new();

    public AIStatusCommandsProvider()
    {
        DisplayName = "AI-Status-Tray";
        Icon = new IconInfo("\uE9D5"); // Robot icon
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
