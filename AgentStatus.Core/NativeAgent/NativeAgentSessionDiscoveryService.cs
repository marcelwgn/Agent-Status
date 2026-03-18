using AgentStatus.Core.Common;
using System.Diagnostics;
using System.Text.Json;

namespace AgentStatus.Core.NativeAgent;

/// <summary>
/// Discovers running Native Agent sessions by watching JSON state files
/// written to <c>~/.nativeagent/sessions/</c>.
/// </summary>
public sealed class NativeAgentSessionDiscoveryService : IDisposable
{
    private static readonly string SessionStatePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nativeagent", "sessions");

    private readonly Dictionary<string, NativeAgentSessionInfo> _sessions = new();
    private readonly object _sessionsLock = new();
    private FileSystemWatcher? _watcher;
    private int _fileChangePending;
    private bool _disposed;

    /// <summary>
    /// Returns a snapshot copy of sessions, safe for enumeration from any thread.
    /// </summary>
    public IReadOnlyDictionary<string, NativeAgentSessionInfo> Sessions
    {
        get
        {
            lock (_sessionsLock)
            {
                return new Dictionary<string, NativeAgentSessionInfo>(_sessions);
            }
        }
    }

    /// <summary>
    /// Raised when sessions are added, removed, or their state changes.
    /// May be raised on a thread-pool thread.
    /// </summary>
    public event EventHandler? SessionsChanged;

    public NativeAgentSessionDiscoveryService()
    {
        StartFileWatcher();
        _ = Task.Run(PollSessions);
    }

    private void StartFileWatcher()
    {
        try
        {
            if (_disposed) return;

            if (!Directory.Exists(SessionStatePath))
            {
                // Directory doesn't exist yet — start a timer to check periodically
                // until Native Agent creates it.
                _ = Task.Run(async () =>
                {
                    while (!_disposed && !Directory.Exists(SessionStatePath))
                    {
                        await Task.Delay(5000);
                    }

                    if (!_disposed && Directory.Exists(SessionStatePath))
                    {
                        StartFileWatcher();
                        PollSessions();
                    }
                });

                Debug.WriteLine($"[NativeAgent] Session state directory not found: {SessionStatePath}");
                return;
            }

            var watcher = new FileSystemWatcher(SessionStatePath)
            {
                Filter = "*.json",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };

            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            _watcher = watcher;

            if (_disposed)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
                _watcher = null;
                return;
            }

            Debug.WriteLine("[NativeAgent] Watching session state directory");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NativeAgent] Failed to start watcher: {ex.Message}");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _fileChangePending, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                Interlocked.Exchange(ref _fileChangePending, 0);
                PollSessions();
            });
        }
    }

    /// <summary>
    /// Re-reads all session-state JSON files and updates the session map.
    /// </summary>
    public void PollSessions()
    {
        try
        {
            if (!Directory.Exists(SessionStatePath))
                return;

            // Read all current session files
            Dictionary<string, NativeAgentSessionInfo> discovered = new();

            foreach (string file in Directory.EnumerateFiles(SessionStatePath, "*.json"))
            {
                try
                {
                    NativeAgentSessionInfo? info = ReadSessionFile(file);
                    if (info != null)
                    {
                        discovered[info.SessionId] = info;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NativeAgent] Error reading {file}: {ex.Message}");
                }
            }

            bool changed = false;

            lock (_sessionsLock)
            {
                // Remove sessions whose files are gone
                List<string> toRemove = _sessions.Keys.Where(k => !discovered.ContainsKey(k)).ToList();
                foreach (string id in toRemove)
                {
                    _sessions.Remove(id);
                    changed = true;
                }

                // Add or update sessions
                foreach ((string sessionId, NativeAgentSessionInfo info) in discovered)
                {
                    if (_sessions.TryGetValue(sessionId, out NativeAgentSessionInfo? existing))
                    {
                        // Check if anything changed
                        if (existing.State != info.State || existing.Summary != info.Summary)
                        {
                            _sessions[sessionId] = info;
                            changed = true;
                        }
                    }
                    else
                    {
                        _sessions[sessionId] = info;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                SessionsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NativeAgent] PollSessions error: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a single session-state JSON file and returns a <see cref="NativeAgentSessionInfo"/>,
    /// or null if the file is invalid or unreadable.
    /// </summary>
    private static NativeAgentSessionInfo? ReadSessionFile(string filePath)
    {
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using JsonDocument doc = JsonDocument.Parse(fs);
        JsonElement root = doc.RootElement;

        string? id = root.TryGetProperty("id", out JsonElement idProp) ? idProp.GetString() : null;
        if (string.IsNullOrEmpty(id))
            return null;

        string? status = root.TryGetProperty("status", out JsonElement statusProp) ? statusProp.GetString() : null;
        string? summary = root.TryGetProperty("summary", out JsonElement summaryProp) ? summaryProp.GetString() : null;
        string? prompt = root.TryGetProperty("prompt", out JsonElement promptProp) ? promptProp.GetString() : null;

        AISessionState state = status switch
        {
            "running" => AISessionState.Working,
            "completed" => AISessionState.Done,
            "error" => AISessionState.Unknown,
            _ => AISessionState.Unknown,
        };

        return new NativeAgentSessionInfo
        {
            SessionId = id,
            State = state,
            Summary = summary ?? string.Empty,
            Prompt = prompt ?? string.Empty,
            Mode = AISessionMode.Autopilot,
            HostAppName = "CmdPal",
        };
    }

    public void Refresh() => PollSessions();

    public void Dispose()
    {
        _disposed = true;
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
