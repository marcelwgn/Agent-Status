using AgentStatus.Core.Common;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace AgentStatus.Core.NativeAgent;

/// <summary>
/// Core session manager for Native Agent (CmdPal extension). Wraps
/// <see cref="NativeAgentSessionDiscoveryService"/> and maintains an
/// <see cref="ObservableCollection{AISessionInfo}"/> of active sessions.
/// </summary>
public sealed class NativeAgentSessionManager : ISessionManager
{
    private readonly NativeAgentSessionDiscoveryService _discovery;
    private readonly Dictionary<string, NativeAgentSessionInfo> _tracked = [];

    public ObservableCollection<AISessionInfo> Sessions { get; } = [];

    /// <summary>Fires on a thread-pool thread whenever sessions are synced.</summary>
    public event EventHandler? SessionsChanged;

    public NativeAgentSessionManager()
    {
        _discovery = new NativeAgentSessionDiscoveryService();
        _discovery.SessionsChanged += (_, _) => SyncSessions();
    }

    public void Refresh() => _discovery.Refresh();

    public void Dispose() => _discovery.Dispose();

    private void SyncSessions()
    {
        IReadOnlyDictionary<string, NativeAgentSessionInfo> discovered = _discovery.Sessions;

        // Remove sessions that no longer exist
        List<string> toRemove = _tracked.Keys.Where(k => !discovered.ContainsKey(k)).ToList();
        foreach (string id in toRemove)
        {
            if (_tracked.TryGetValue(id, out NativeAgentSessionInfo? old))
            {
                Sessions.Remove(old);
                _tracked.Remove(id);
            }
        }

        // Add or update sessions
        foreach ((string sessionId, NativeAgentSessionInfo info) in discovered)
        {
            if (_tracked.TryGetValue(sessionId, out NativeAgentSessionInfo? existing))
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

        LogChanges(discovered, toRemove);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LogChanges(IReadOnlyDictionary<string, NativeAgentSessionInfo> sessions, List<string> removed)
    {
        foreach (string id in removed)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] NativeAgent {id[..Math.Min(8, id.Length)]}: removed");
        }

        foreach ((string _, NativeAgentSessionInfo info) in sessions)
        {
            if (!_tracked.ContainsKey(info.SessionId) || _tracked[info.SessionId].State != info.State)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] NativeAgent {info.DisplayName}: {info.State}");
            }
        }
    }
}
