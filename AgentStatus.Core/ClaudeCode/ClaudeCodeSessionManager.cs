using AgentStatus.Core.Common;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace AgentStatus.Core.ClaudeCode;

/// <summary>
/// Core session manager for Claude Code CLI. Wraps <see cref="ClaudeCodeSessionDiscoveryService"/>
/// and maintains an <see cref="ObservableCollection{AISessionInfo}"/> of active sessions.
/// UI consumers should listen to <see cref="Sessions"/> collection changes or
/// the <see cref="SessionsChanged"/> event for updates.
/// </summary>
public sealed class ClaudeCodeSessionManager : ISessionManager
{
    private readonly ClaudeCodeSessionDiscoveryService _discovery;
    private readonly Dictionary<string, ClaudeCodeSessionInfo> _tracked = [];
    private readonly Dictionary<string, (AISessionState state, AISessionMode mode)> _previousStates = [];
    private bool _isFirstPoll = true;

    public ObservableCollection<AISessionInfo> Sessions { get; } = [];

    /// <summary>Fires on a thread-pool thread whenever sessions are synced.</summary>
    public event EventHandler? SessionsChanged;

    public ClaudeCodeSessionManager()
    {
        _discovery = new ClaudeCodeSessionDiscoveryService();
        _discovery.SessionsChanged += (_, _) => SyncSessions();
    }

    public void Refresh() => _discovery.Refresh();

    public void Dispose() => _discovery.Dispose();

    private void SyncSessions()
    {
        IReadOnlyDictionary<string, ClaudeCodeSessionInfo> discovered = _discovery.Sessions;

        // Remove sessions that no longer exist
        List<string> toRemove = _tracked.Keys.Where(k => !discovered.ContainsKey(k)).ToList();
        foreach (string id in toRemove)
        {
            if (_tracked.TryGetValue(id, out ClaudeCodeSessionInfo? old))
            {
                Sessions.Remove(old);
                _tracked.Remove(id);
            }
        }

        // Add or update sessions
        foreach ((string sessionId, ClaudeCodeSessionInfo info) in discovered)
        {
            if (_tracked.TryGetValue(sessionId, out ClaudeCodeSessionInfo? existing))
            {
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

    private void LogStateChanges(IReadOnlyDictionary<string, ClaudeCodeSessionInfo> sessions, List<string> removed)
    {
        if (_isFirstPoll)
        {
            _isFirstPoll = false;
            Debug.WriteLine("=== Claude Code Sessions ===");
            foreach ((string sessionId, ClaudeCodeSessionInfo info) in sessions)
            {
                Debug.WriteLine($"  {info.DisplayName}, {info.State}");
                _previousStates[sessionId] = (info.State, info.Mode);
            }
            if (sessions.Count == 0)
                Debug.WriteLine("  (none)");
            Debug.WriteLine("============================");
        }
        else
        {
            foreach (string id in removed)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Claude {id[..Math.Min(8, id.Length)]}: removed");
                _previousStates.Remove(id);
            }

            foreach ((string sessionId, ClaudeCodeSessionInfo info) in sessions)
            {
                if (_previousStates.TryGetValue(sessionId, out var prev))
                {
                    if (prev.state != info.State || prev.mode != info.Mode)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Claude {info.DisplayName}: {info.State}");
                        _previousStates[sessionId] = (info.State, info.Mode);
                    }
                }
                else
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Claude {info.DisplayName}: {info.State} (new)");
                    _previousStates[sessionId] = (info.State, info.Mode);
                }
            }
        }
    }
}
