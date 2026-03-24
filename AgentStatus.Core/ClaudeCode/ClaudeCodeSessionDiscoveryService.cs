using AgentStatus.Core.Common;
using System.Diagnostics;
using System.Text.Json;

namespace AgentStatus.Core.ClaudeCode;

/// <summary>
/// Discovers running Claude Code sessions and monitors their state
/// by watching ~/.claude/sessions/ and reading session event JSONL files.
/// </summary>
public sealed class ClaudeCodeSessionDiscoveryService : IDisposable
{
    private static readonly string ClaudeDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static readonly string SessionsDir = Path.Combine(ClaudeDir, "sessions");
    private static readonly string ProjectsDir = Path.Combine(ClaudeDir, "projects");

    private readonly Dictionary<string, ClaudeCodeSessionInfo> _sessions = new();
    private readonly object _sessionsLock = new();
    private FileSystemWatcher? _sessionsWatcher;
    private FileSystemWatcher? _projectsWatcher;
    private int _fileChangePending;
    private bool _disposed;

    /// <summary>
    /// Returns a snapshot copy of sessions, safe for enumeration from any thread.
    /// </summary>
    public IReadOnlyDictionary<string, ClaudeCodeSessionInfo> Sessions
    {
        get
        {
            lock (_sessionsLock)
            {
                return new Dictionary<string, ClaudeCodeSessionInfo>(_sessions);
            }
        }
    }

    /// <summary>
    /// Raised when sessions are added, removed, or their state changes.
    /// May be raised on a thread-pool thread — consumers are responsible for dispatching.
    /// </summary>
    public event EventHandler? SessionsChanged;

    public ClaudeCodeSessionDiscoveryService()
    {
        StartFileWatchers();
        _ = Task.Run(PollSessions);
    }

    private void StartFileWatchers()
    {
        try
        {
            if (_disposed) return;

            if (Directory.Exists(SessionsDir))
            {
                var watcher = new FileSystemWatcher(SessionsDir)
                {
                    Filter = "*.json",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += OnFileChanged;
                watcher.Created += OnFileChanged;
                watcher.Deleted += OnFileChanged;
                _sessionsWatcher = watcher;

                if (_disposed) { watcher.EnableRaisingEvents = false; watcher.Dispose(); _sessionsWatcher = null; return; }

                Debug.WriteLine("[ClaudeCode] Watching sessions directory");
            }
            else
            {
                Debug.WriteLine($"[ClaudeCode] Sessions directory not found: {SessionsDir}");
            }

            if (Directory.Exists(ProjectsDir))
            {
                var watcher = new FileSystemWatcher(ProjectsDir)
                {
                    Filter = "*.jsonl",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += OnFileChanged;
                watcher.Created += OnFileChanged;
                _projectsWatcher = watcher;

                if (_disposed) { watcher.EnableRaisingEvents = false; watcher.Dispose(); _projectsWatcher = null; return; }

                Debug.WriteLine("[ClaudeCode] Watching projects directory for JSONL changes");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClaudeCode] Failed to start watchers: {ex.Message}");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _fileChangePending, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(250);
                Interlocked.Exchange(ref _fileChangePending, 0);
                PollSessions();
            });
        }
    }

    private void PollSessions()
    {
        try
        {
            Dictionary<string, SessionFileData> activeSessionFiles = ReadSessionFiles();

            bool changed = false;

            lock (_sessionsLock)
            {
                List<string> toRemove = _sessions.Keys.Where(k => !activeSessionFiles.ContainsKey(k)).ToList();
                foreach (string id in toRemove)
                {
                    _sessions.Remove(id);
                    changed = true;
                }

                foreach ((string sessionId, SessionFileData data) in activeSessionFiles)
                {
                    if (!_sessions.TryGetValue(sessionId, out ClaudeCodeSessionInfo? info))
                    {
                        info = new ClaudeCodeSessionInfo
                        {
                            SessionId = sessionId,
                            ClaudePid = data.Pid,
                            Cwd = data.Cwd,
                            ProjectDirName = data.ProjectDirName,
                            HostAppName = "Terminal",
                        };
                        _sessions[sessionId] = info;
                        changed = true;

                        PopulateProcessTree(info);
                    }

                    AISessionState oldState = info.State;
                    AISessionMode oldMode = info.Mode;
                    ReadSessionState(info);
                    if (info.State != oldState || info.Mode != oldMode)
                    {
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
            Debug.WriteLine($"[ClaudeCode] PollSessions error: {ex.Message}");
        }
    }

    private static Dictionary<string, SessionFileData> ReadSessionFiles()
    {
        Dictionary<string, SessionFileData> result = new();

        try
        {
            if (!Directory.Exists(SessionsDir))
                return result;

            foreach (string file in Directory.GetFiles(SessionsDir, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;

                    int pid = root.GetProperty("pid").GetInt32();
                    string sessionId = root.GetProperty("sessionId").GetString() ?? "";
                    string cwd = root.GetProperty("cwd").GetString() ?? "";

                    if (string.IsNullOrEmpty(sessionId))
                        continue;

                    try
                    {
                        using Process proc = Process.GetProcessById(pid);
                        if (proc.HasExited ||
                            proc.StartTime.ToUniversalTime() > File.GetCreationTimeUtc(file).AddMinutes(1))
                            continue;
                    }
                    catch
                    {
                        continue;
                    }

                    string projectDirName = EncodeProjectDir(cwd);

                    result[sessionId] = new SessionFileData
                    {
                        Pid = pid,
                        Cwd = cwd,
                        ProjectDirName = projectDirName,
                    };
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ClaudeCode] Error reading session file {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClaudeCode] ReadSessionFiles error: {ex.Message}");
        }

        return result;
    }

    private static string EncodeProjectDir(string cwd)
    {
        string encoded = cwd.Replace(":\\", "--").Replace("\\", "-").Replace("/", "-");
        return encoded.TrimEnd('-');
    }

    private static void PopulateProcessTree(ClaudeCodeSessionInfo info)
    {
        try
        {
            using System.Management.ManagementObjectSearcher parentSearch = new(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {info.ClaudePid}");

            foreach (System.Management.ManagementObject obj in parentSearch.Get())
            {
                int shellPid = Convert.ToInt32(obj["ParentProcessId"]);
                info.ShellPid = shellPid;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClaudeCode] PopulateProcessTree error: {ex.Message}");
        }
    }

    private static void ReadSessionState(ClaudeCodeSessionInfo info)
    {
        try
        {
            string eventsPath = Path.Combine(ProjectsDir, info.ProjectDirName, $"{info.SessionId}.jsonl");
            if (!File.Exists(eventsPath))
                return;

            using FileStream fs = new(eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(fs);
            ClaudeCodeSessionStateReader.ReadSessionState(info, reader);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClaudeCode] ReadSessionState error: {ex.Message}");
        }
    }

    public void Refresh()
    {
        _ = Task.Run(PollSessions);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_sessionsWatcher != null)
        {
            _sessionsWatcher.EnableRaisingEvents = false;
            _sessionsWatcher.Dispose();
            _sessionsWatcher = null;
        }
        if (_projectsWatcher != null)
        {
            _projectsWatcher.EnableRaisingEvents = false;
            _projectsWatcher.Dispose();
            _projectsWatcher = null;
        }
    }

    private sealed class SessionFileData
    {
        public int Pid { get; init; }
        public string Cwd { get; init; } = string.Empty;
        public string ProjectDirName { get; init; } = string.Empty;
    }
}
