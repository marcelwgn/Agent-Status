using System;
using System.IO;
using AgentStatus.Core.Common;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AgentStatusCmdPal;

/// <summary>
/// Provides colored SVG icons for session states and the extension provider.
/// Icons are resolved from the Assets/Icons folder at the application base directory.
/// </summary>
internal static class SessionIcons
{
    private static readonly string IconsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons");

    public static IconInfo Provider { get; } = FromSvg("provider.svg");
    public static IconInfo NoSession { get; } = FromSvg("no-session.svg");

    public static IconInfo GetIconForState(AISessionState state) => state switch
    {
        AISessionState.Idle => FromSvg("idle.svg"),
        AISessionState.Thinking => FromSvg("thinking.svg"),
        AISessionState.Working => FromSvg("working.svg"),
        AISessionState.ExecutingTool => FromSvg("executing.svg"),
        AISessionState.WaitingForUser => FromSvg("waiting.svg"),
        AISessionState.Done => FromSvg("done.svg"),
        _ => FromSvg("unknown.svg"),
    };

    private static IconInfo FromSvg(string fileName)
    {
        return new IconInfo(Path.Combine(IconsDir, fileName));
    }
}
