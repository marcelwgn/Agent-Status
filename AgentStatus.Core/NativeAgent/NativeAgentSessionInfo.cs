using AgentStatus.Core.Common;

namespace AgentStatus.Core.NativeAgent;

/// <summary>
/// Native Agent-specific session information extending the generic AISessionInfo.
/// Native Agent sessions are headless (no terminal window), so there is no
/// shell PID to focus. State is read from JSON files on disk.
/// </summary>
public sealed class NativeAgentSessionInfo : AISessionInfo
{
    /// <summary>The user prompt that started this session.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>
    /// Display name derived from the prompt text rather than a working directory.
    /// </summary>
    public override string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(Prompt))
            {
                return Prompt.Length > 40 ? string.Concat(Prompt.AsSpan(0, 37), "...") : Prompt;
            }

            return SessionId.Length >= 8 ? SessionId[..8] : SessionId;
        }
    }
}
