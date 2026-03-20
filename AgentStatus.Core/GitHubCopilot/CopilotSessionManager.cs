using AgentStatus.Core.Common;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace AgentStatus.Core.GitHubCopilot;

/// <summary>
/// Core session manager for GitHub Copilot CLI. Wraps <see cref="CopilotSessionDiscoveryService"/>
/// and maintains an <see cref="ObservableCollection{AISessionInfo}"/> of active sessions.
/// UI consumers should listen to <see cref="Sessions"/> collection changes or
/// the <see cref="SessionsChanged"/> event for updates.
/// </summary>
public sealed class CopilotSessionManager : ISessionManager
{
    private readonly CopilotSessionDiscoveryService _discovery;
    private readonly Dictionary<string, CopilotSessionInfo> _tracked = [];
    private readonly Dictionary<string, (AISessionState state, AISessionMode mode)> _previousStates = [];
    private bool _isFirstPoll = true;

    public ObservableCollection<AISessionInfo> Sessions { get; } = [];

    /// <summary>Fires on a thread-pool thread whenever sessions are synced.</summary>
    public event EventHandler? SessionsChanged;

    public CopilotSessionManager()
    {
        _discovery = new CopilotSessionDiscoveryService();
        _discovery.SessionsChanged += (_, _) => SyncSessions();
    }

    public void Refresh() => _discovery.Refresh();

    public void Dispose() => _discovery.Dispose();

    private void SyncSessions()
    {
        IReadOnlyDictionary<string, CopilotSessionInfo> discovered = _discovery.Sessions;

        // Remove sessions that no longer exist
        List<string> toRemove = _tracked.Keys.Where(k => !discovered.ContainsKey(k)).ToList();
        foreach (string id in toRemove)
        {
            if (_tracked.TryGetValue(id, out CopilotSessionInfo? old))
            {
                Sessions.Remove(old);
                _tracked.Remove(id);
            }
        }

        // Add or update sessions
        foreach ((string sessionId, CopilotSessionInfo info) in discovered)
        {
            if (_tracked.TryGetValue(sessionId, out CopilotSessionInfo? existing))
            {
                // Update in-place: replace the entry
                int idx = Sessions.IndexOf(existing);
                if (idx >= 0)
                    Sessions[idx] = info;
                _tracked[sessionId] = info;
            }
            else
            {
                _tracked[sessionId] = info;
                Sessions.Add(info);
            }
        }

        LogStateChanges(discovered, toRemove);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LogStateChanges(IReadOnlyDictionary<string, CopilotSessionInfo> sessions, List<string> removed)
    {
        if (_isFirstPoll)
        {
            _isFirstPoll = false;
            Debug.WriteLine("=== Copilot Sessions ===");
            foreach ((string sessionId, CopilotSessionInfo info) in sessions)
            {
                Debug.WriteLine($"  {info.DisplayName}, {FormatMode(info.Mode)}, {info.State}, {info.SessionId}");
                _previousStates[sessionId] = (info.State, info.Mode);
            }
            if (sessions.Count == 0)
                Debug.WriteLine("  (none)");
            Debug.WriteLine("========================");
        }
        else
        {
            foreach (string id in removed)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] {id[..Math.Min(8, id.Length)]}: removed");
                _previousStates.Remove(id);
            }

            foreach ((string sessionId, CopilotSessionInfo info) in sessions)
            {
                if (_previousStates.TryGetValue(sessionId, out var prev))
                {
                    if (prev.state != info.State || prev.mode != info.Mode)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] {info.DisplayName}: {FormatMode(info.Mode)}, {info.State}");
                        _previousStates[sessionId] = (info.State, info.Mode);
                    }
                }
                else
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] {info.DisplayName}: {FormatMode(info.Mode)}, {info.State} (new)");
                    _previousStates[sessionId] = (info.State, info.Mode);
                }
            }
        }
    }

    private static string FormatMode(AISessionMode mode) => mode switch
    {
        AISessionMode.Autopilot => "agent",
        AISessionMode.Plan => "planner",
        _ => "normal",
    };
}
