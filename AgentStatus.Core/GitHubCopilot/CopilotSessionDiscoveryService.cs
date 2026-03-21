using AgentStatus.Core.Common;
using System.Diagnostics;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStatus.Core.GitHubCopilot;

/// <summary>
/// Discovers running Copilot CLI sessions and monitors their state
/// by polling Win32_Process and reading session files.
/// </summary>
public sealed partial class CopilotSessionDiscoveryService : IDisposable
{
    private static readonly string SessionStatePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");

    private readonly Dictionary<string, CopilotSessionInfo> _sessions = new();
    private readonly object _sessionsLock = new();
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _lockWatcher;
    private Timer? _livenessTimer;
    private int _fileChangePending;
    private bool _disposed;

    /// <summary>
    /// Returns a snapshot copy of sessions, safe for enumeration from any thread.
    /// </summary>
    public IReadOnlyDictionary<string, CopilotSessionInfo> Sessions
    {
        get
        {
            lock (_sessionsLock)
            {
                return new Dictionary<string, CopilotSessionInfo>(_sessions);
            }
        }
    }

    /// <summary>
    /// Raised when sessions are added, removed, or their state changes.
    /// May be raised on a thread-pool thread — consumers are responsible for dispatching.
    /// </summary>
    public event EventHandler? SessionsChanged;

    public CopilotSessionDiscoveryService()
    {
        StartFileWatcher();
        _livenessTimer = new Timer(_ => CheckProcessLiveness(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        _ = Task.Run(PollSessions);
    }

    private void StartFileWatcher()
    {
        try
        {
            if (_disposed) return;

            if (!Directory.Exists(SessionStatePath))
            {
                Debug.WriteLine($"[FileWatcher] Session state directory not found: {SessionStatePath}");
                return;
            }

            // Watch for events.jsonl changes for near-instant state updates
            var watcher = new FileSystemWatcher(SessionStatePath)
            {
                Filter = "events.jsonl",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };

            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            _watcher = watcher;

            // Watch for lock files to detect newly started sessions.
            // New sessions create inuse.<PID>.lock before events.jsonl exists,
            // so without this watcher they aren't discovered until a command is issued.
            var lockWatcher = new FileSystemWatcher(SessionStatePath)
            {
                Filter = "*.lock",
                NotifyFilter = NotifyFilters.FileName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };

            lockWatcher.Created += OnFileChanged;
            lockWatcher.Deleted += OnFileChanged;
            _lockWatcher = lockWatcher;

            // If disposed during creation, clean up immediately
            if (_disposed)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
                _watcher = null;
                lockWatcher.EnableRaisingEvents = false;
                lockWatcher.Dispose();
                _lockWatcher = null;
                return;
            }

            Debug.WriteLine("[FileWatcher] Watching session-state for events.jsonl and lock file changes");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileWatcher] Failed to start: {ex.Message}");
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

    /// <summary>
    /// Lightweight periodic check: if any tracked session's process has exited,
    /// trigger a full repoll so the session is removed promptly.
    /// </summary>
    private void CheckProcessLiveness()
    {
        try
        {
            bool anyExited = false;
            lock (_sessionsLock)
            {
                foreach (CopilotSessionInfo info in _sessions.Values)
                {
                    try
                    {
                        using Process proc = Process.GetProcessById(info.CopilotPid);
                        if (proc.HasExited)
                        {
                            anyExited = true;
                            break;
                        }
                    }
                    catch
                    {
                        anyExited = true;
                        break;
                    }
                }
            }

            if (anyExited)
                PollSessions();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CheckProcessLiveness error: {ex.Message}");
        }
    }

    private void PollSessions()
    {
        try
        {
            Dictionary<string, (int copilotPid, string commandLine)> running = FindRunningCopilotProcesses();

            bool changed = false;

            lock (_sessionsLock)
            {
                List<string> toRemove = _sessions.Keys.Where(k => !running.ContainsKey(k)).ToList();
                foreach (string id in toRemove)
                {
                    _sessions.Remove(id);
                    changed = true;
                }

                foreach ((string sessionId, (int copilotPid, string commandLine)) in running)
                {
                    if (!_sessions.TryGetValue(sessionId, out CopilotSessionInfo? info))
                    {
                        info = new CopilotSessionInfo
                        {
                            SessionId = sessionId,
                            CopilotPid = copilotPid,
                            HostAppName = "Terminal",
                        };
                        _sessions[sessionId] = info;
                        changed = true;

                        PopulateProcessTree(info);
                    }

                    // Re-read workspace metadata every poll so summary/branch
                    // updates are picked up promptly.
                    string oldSummary = info.Summary;
                    string oldBranch = info.Branch;
                    ReadWorkspaceMetadata(info);
                    if (info.Summary != oldSummary || info.Branch != oldBranch)
                        changed = true;

                    AISessionState oldState = info.State;
                    AISessionMode oldMode = info.Mode;
                    string? oldIntent = info.CurrentIntent;
                    string? oldPendingQuestion = info.PendingQuestion;
                    ReadSessionState(info);
                    if (info.State != oldState || info.Mode != oldMode
                        || info.CurrentIntent != oldIntent
                        || info.PendingQuestion != oldPendingQuestion)
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
            Debug.WriteLine($"CopilotSessionDiscoveryService.PollSessions error: {ex.Message}");
        }
    }

    [GeneratedRegex(@"--resume\s+([0-9a-f\-]{36})", RegexOptions.IgnoreCase)]
    private static partial Regex ResumeSessionIdRegex();

    [GeneratedRegex(@"^inuse\.(\d+)\.lock$", RegexOptions.IgnoreCase)]
    private static partial Regex LockFilePidRegex();

    /// <summary>
    /// Finds running copilot.exe processes and maps them to session IDs.
    /// Uses two strategies:
    /// 1. Parse --resume &lt;uuid&gt; from the command line (resumed sessions).
    /// 2. Scan session-state lock files (inuse.&lt;PID&gt;.lock) for new sessions
    ///    that don't have --resume on the command line.
    /// </summary>
    private static Dictionary<string, (int pid, string cmdLine)> FindRunningCopilotProcesses()
    {
        Dictionary<string, (int, string)> result = new();

        try
        {
            // Collect all running copilot.exe PIDs and their command lines via WMI.
            // WMI may be unavailable in trimmed/packaged builds, so failures here
            // are non-fatal — Strategy 2 (lock files) can work independently.
            Dictionary<int, string> runningPids = new();

            try
            {
                using ManagementObjectSearcher searcher = new(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'copilot.exe'");

                foreach (ManagementObject obj in searcher.Get())
                {
                    string? cmdLine = obj["CommandLine"]?.ToString();
                    int pid = Convert.ToInt32(obj["ProcessId"]);

                    if (string.IsNullOrEmpty(cmdLine))
                        continue;

                    runningPids[pid] = cmdLine;

                    // Strategy 1: extract session ID from --resume flag
                    Match match = ResumeSessionIdRegex().Match(cmdLine);
                    if (match.Success)
                    {
                        string sessionId = match.Groups[1].Value;
                        string sessionDir = Path.Combine(SessionStatePath, sessionId);

                        // Only include sessions that have an active lock file
                        if (Directory.Exists(sessionDir) &&
                            Directory.EnumerateFiles(sessionDir, "inuse.*.lock").Any())
                        {
                            result[sessionId] = (pid, cmdLine);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WMI query failed, falling back to lock-file-only discovery: {ex.Message}");
            }

            // Strategy 2: scan lock files for sessions not found via --resume.
            if (Directory.Exists(SessionStatePath))
            {
                foreach (string sessionDir in Directory.EnumerateDirectories(SessionStatePath))
                {
                    string sessionId = Path.GetFileName(sessionDir);

                    // Skip sessions already discovered via --resume
                    if (result.ContainsKey(sessionId))
                        continue;

                    foreach (string lockFile in Directory.EnumerateFiles(sessionDir, "inuse.*.lock"))
                    {
                        Match lockMatch = LockFilePidRegex().Match(Path.GetFileName(lockFile));
                        if (!lockMatch.Success || !int.TryParse(lockMatch.Groups[1].Value, out int lockPid))
                            continue;

                        // Use WMI data when available, otherwise verify process liveness directly
                        if (runningPids.TryGetValue(lockPid, out string? cmdLine))
                        {
                            result[sessionId] = (lockPid, cmdLine);
                            break;
                        }

                        try
                        {
                            using Process proc = Process.GetProcessById(lockPid);
                            if (!proc.HasExited)
                            {
                                result[sessionId] = (lockPid, "");
                                break;
                            }
                        }
                        catch
                        {
                            // Process no longer exists — stale lock file
                        }
                    }
                }
            }

            // When a PID maps to multiple sessions (e.g. stale --resume flag
            // pointing at an old session while the lock file points at the
            // current one), keep only the session with the most recent events.
            Dictionary<int, List<string>> pidToSessions = new();
            foreach ((string sessionId, (int pid, string _)) in result)
            {
                if (!pidToSessions.TryGetValue(pid, out List<string>? list))
                {
                    list = [];
                    pidToSessions[pid] = list;
                }
                list.Add(sessionId);
            }

            foreach ((int _, List<string> sessionIds) in pidToSessions)
            {
                if (sessionIds.Count <= 1)
                    continue;

                string? bestSession = null;
                DateTime bestTime = DateTime.MinValue;

                foreach (string sid in sessionIds)
                {
                    string eventsPath = Path.Combine(SessionStatePath, sid, "events.jsonl");
                    try
                    {
                        if (File.Exists(eventsPath))
                        {
                            DateTime lastWrite = File.GetLastWriteTimeUtc(eventsPath);
                            if (lastWrite > bestTime)
                            {
                                bestTime = lastWrite;
                                bestSession = sid;
                            }
                        }
                    }
                    catch { /* best-effort */ }
                }

                foreach (string sid in sessionIds)
                {
                    if (sid != bestSession)
                        result.Remove(sid);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FindRunningCopilotProcesses error: {ex.Message}");
        }

        return result;
    }

    private static void PopulateProcessTree(CopilotSessionInfo info)
    {
        try
        {
            using ManagementObjectSearcher parentSearch = new(
                $"SELECT ProcessId, ParentProcessId FROM Win32_Process WHERE ProcessId = {info.CopilotPid}");

            foreach (ManagementObject obj in parentSearch.Get())
            {
                int parentPid = Convert.ToInt32(obj["ParentProcessId"]);
                info.ParentPid = parentPid;

                using ManagementObjectSearcher shellSearch = new(
                    $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {parentPid}");

                foreach (ManagementObject shellObj in shellSearch.Get())
                {
                    info.ShellPid = Convert.ToInt32(shellObj["ParentProcessId"]);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PopulateProcessTree error: {ex.Message}");
        }
    }

    private static void ReadWorkspaceMetadata(CopilotSessionInfo info)
    {
        try
        {
            string yamlPath = Path.Combine(SessionStatePath, info.SessionId, "workspace.yaml");
            if (!File.Exists(yamlPath))
                return;

            foreach (string line in File.ReadAllLines(yamlPath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("cwd:"))
                    info.Cwd = trimmed["cwd:".Length..].Trim();
                else if (trimmed.StartsWith("repository:"))
                    info.Repository = trimmed["repository:".Length..].Trim();
                else if (trimmed.StartsWith("branch:"))
                    info.Branch = trimmed["branch:".Length..].Trim();
                else if (trimmed.StartsWith("summary:"))
                    info.Summary = trimmed["summary:".Length..].Trim();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ReadWorkspaceMetadata error: {ex.Message}");
        }
    }

    private static void ReadSessionState(CopilotSessionInfo info)
    {
        try
        {
            string eventsPath = Path.Combine(SessionStatePath, info.SessionId, "events.jsonl");
            if (!File.Exists(eventsPath))
            {
                // No events yet — session is alive (lock file exists) but idle.
                info.State = AISessionState.Idle;
                return;
            }

            string? lastLine = null;
            string? lastStateLine = null;
            string? lastStateType = null;
            bool lastStateHasWaitingTools = false;
            HashSet<string> completedToolCallIds = new();
            HashSet<string> startedToolCallIds = new();
            HashSet<string> userRequestedToolCallIds = new();
            List<(string toolCallId, string line)> askUserStarts = new();
            string? lastUserMessage = null;
            string? lastIntent = null;
            string? lastMode = null;
            string? lastAssistantMessageLine = null;
            bool sawTaskComplete = false;

            HashSet<string> stateDefiningTypes = new()
            {
                "session.task_complete", "session.shutdown",
                "user.message",
                "tool.execution_start", "tool.execution_complete", "tool.user_requested",
                "assistant.turn_end", "assistant.turn_start", "assistant.message",
                "hook.start", "hook.end",
                "subagent.started", "subagent.completed", "subagent.failed",
                "abort",
            };

            using (FileStream fs = new(eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new(fs))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    lastLine = line;

                    JsonDocument? doc;
                    try { doc = JsonDocument.Parse(line); }
                    catch { continue; }

                    using (doc)
                    {
                        JsonElement root = doc.RootElement;

                        string? eventType = root.TryGetProperty("type", out JsonElement typeProp)
                            ? typeProp.GetString()
                            : null;
                        if (eventType == null)
                            continue;

                        if (stateDefiningTypes.Contains(eventType))
                        {
                            lastStateLine = line;
                            lastStateType = eventType;
                            lastStateHasWaitingTools = false;
                        }

                        JsonElement data = root.TryGetProperty("data", out JsonElement d) ? d : root;

                        switch (eventType)
                        {
                            case "session.start":
                                if (data.TryGetProperty("mode", out JsonElement modeEl))
                                    lastMode = modeEl.GetString();
                                break;

                            case "session.task_complete":
                                sawTaskComplete = true;
                                break;

                            case "tool.execution_complete":
                                if (data.TryGetProperty("toolCallId", out JsonElement tcIdEl))
                                {
                                    string? tcId = tcIdEl.GetString();
                                    if (tcId != null)
                                        completedToolCallIds.Add(tcId);
                                }
                                break;

                            case "tool.execution_start":
                            {
                                string? toolCallId = data.TryGetProperty("toolCallId", out JsonElement startTcEl)
                                    ? startTcEl.GetString() : null;
                                string? toolName = data.TryGetProperty("toolName", out JsonElement tnEl)
                                    ? tnEl.GetString() : null;

                                if (toolCallId != null)
                                {
                                    startedToolCallIds.Add(toolCallId);
                                    if (toolName is "ask_user" or "exit_plan_mode")
                                        askUserStarts.Add((toolCallId, line));
                                }

                                if (toolName == "report_intent" &&
                                    data.TryGetProperty("arguments", out JsonElement argsEl) &&
                                    argsEl.ValueKind == JsonValueKind.Object &&
                                    argsEl.TryGetProperty("intent", out JsonElement intentEl))
                                {
                                    lastIntent = intentEl.GetString();
                                }
                                break;
                            }

                            case "tool.user_requested":
                                if (data.TryGetProperty("toolCallId", out JsonElement urTcEl))
                                {
                                    string? tcId = urTcEl.GetString();
                                    if (tcId != null)
                                        userRequestedToolCallIds.Add(tcId);
                                }
                                break;

                            case "assistant.message":
                                lastAssistantMessageLine = line;

                                if (stateDefiningTypes.Contains(eventType))
                                    lastStateHasWaitingTools = HasWaitingToolRequest(data);

                                if (data.TryGetProperty("toolRequests", out JsonElement trEl) &&
                                    trEl.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (JsonElement req in trEl.EnumerateArray())
                                    {
                                        string? reqName = req.TryGetProperty("name", out JsonElement reqNameEl)
                                            ? reqNameEl.GetString() : null;
                                        if (reqName == "report_intent" &&
                                            req.TryGetProperty("arguments", out JsonElement reqArgsEl) &&
                                            reqArgsEl.ValueKind == JsonValueKind.Object &&
                                            reqArgsEl.TryGetProperty("intent", out JsonElement reqIntentEl))
                                        {
                                            lastIntent = reqIntentEl.GetString();
                                        }
                                    }
                                }
                                break;

                            case "user.message":
                                sawTaskComplete = false;
                                if (data.TryGetProperty("content", out JsonElement contentEl))
                                    lastUserMessage = contentEl.GetString();
                                break;

                            case "session.resume":
                                sawTaskComplete = false;
                                break;

                            case "session.mode_changed":
                                sawTaskComplete = false;
                                if (data.TryGetProperty("newMode", out JsonElement newModeEl))
                                    lastMode = newModeEl.GetString();
                                break;
                        }
                    }
                }
            }

            if (lastLine == null)
                return;

            info.State = DeriveState(lastStateType, lastStateHasWaitingTools);

            // task_complete is followed by hook/tool cleanup events and
            // assistant.turn_end, which would otherwise override the state
            // to Idle. Restore Done when no new user.message followed.
            if (sawTaskComplete && info.State != AISessionState.Thinking)
                info.State = AISessionState.Done;

            if (info.State == AISessionState.Working && lastStateType == "tool.execution_complete"
                && lastStateLine != null)
            {
                try
                {
                    using JsonDocument lastDoc = JsonDocument.Parse(lastStateLine);
                    JsonElement lastData = lastDoc.RootElement.TryGetProperty("data", out JsonElement ld)
                        ? ld : lastDoc.RootElement;
                    string? completedToolCallId = lastData.TryGetProperty("toolCallId", out JsonElement ctcEl)
                        ? ctcEl.GetString() : null;
                    if (completedToolCallId != null && userRequestedToolCallIds.Contains(completedToolCallId))
                        info.State = AISessionState.Idle;
                }
                catch { /* best-effort */ }
            }

            info.LastUserMessage = lastUserMessage;
            info.CurrentIntent = lastIntent;
            info.Mode = lastMode switch
            {
                "plan" => AISessionMode.Plan,
                "autopilot" => AISessionMode.Autopilot,
                _ => AISessionMode.Interactive,
            };

            info.PendingQuestion = null;
            info.PendingChoices = null;
            info.PendingCommands = null;

            foreach ((string toolCallId, string askLine) in askUserStarts)
            {
                if (!completedToolCallIds.Contains(toolCallId))
                {
                    ParseAskUserArguments(askLine, info);
                    info.State = AISessionState.WaitingForUser;
                    break;
                }
            }

            bool requiresApproval = lastMode != "autopilot";

            if (requiresApproval && !info.HasPendingChoices && lastAssistantMessageLine != null)
            {
                List<PendingCommand> pendingCmds = ParsePendingCommands(
                    lastAssistantMessageLine, startedToolCallIds, completedToolCallIds, userRequestedToolCallIds);
                if (pendingCmds.Count > 0)
                {
                    info.PendingCommands = pendingCmds;
                    info.State = AISessionState.WaitingForUser;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ReadSessionState error: {ex.Message}");
        }
    }

    private static bool HasWaitingToolRequest(JsonElement data)
    {
        if (!data.TryGetProperty("toolRequests", out JsonElement toolRequests) ||
            toolRequests.ValueKind != JsonValueKind.Array)
            return false;

        foreach (JsonElement req in toolRequests.EnumerateArray())
        {
            if (req.TryGetProperty("name", out JsonElement nameEl) &&
                nameEl.ValueKind == JsonValueKind.String)
            {
                string? name = nameEl.GetString();
                if (name is "ask_user" or "exit_plan_mode")
                    return true;
            }
        }
        return false;
    }

    private static void ParseAskUserArguments(string jsonLine, CopilotSessionInfo info)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonLine);
            JsonElement root = doc.RootElement;

            JsonElement args;
            if (root.TryGetProperty("data", out JsonElement data) &&
                data.TryGetProperty("arguments", out JsonElement dataArgs) &&
                dataArgs.ValueKind == JsonValueKind.Object)
            {
                args = dataArgs;
            }
            else if (root.TryGetProperty("arguments", out JsonElement rootArgs) &&
                     rootArgs.ValueKind == JsonValueKind.Object)
            {
                args = rootArgs;
            }
            else
            {
                return;
            }

            if (args.TryGetProperty("question", out JsonElement questionEl))
            {
                info.PendingQuestion = questionEl.GetString();
            }

            if (args.TryGetProperty("choices", out JsonElement choicesEl) &&
                choicesEl.ValueKind == JsonValueKind.Array)
            {
                info.PendingChoices = choicesEl.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();
            }

            if (args.TryGetProperty("summary", out JsonElement summaryEl))
            {
                info.PendingQuestion ??= summaryEl.GetString();
            }

            if (info.PendingChoices == null &&
                args.TryGetProperty("actions", out JsonElement actionsEl) &&
                actionsEl.ValueKind == JsonValueKind.Array)
            {
                info.PendingChoices = actionsEl.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(a => a switch
                    {
                        "autopilot" => "Start (autopilot)",
                        "interactive" => "Start (interactive)",
                        "exit_only" => "Exit plan mode",
                        _ => a,
                    })
                    .ToArray();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ParseAskUserArguments error: {ex.Message}");
        }
    }

    private static List<PendingCommand> ParsePendingCommands(
        string assistantMessageLine,
        HashSet<string> startedToolCallIds,
        HashSet<string> completedToolCallIds,
        HashSet<string> userRequestedToolCallIds)
    {
        List<PendingCommand> result = new();

        HashSet<string> approvalTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "powershell", "edit", "create", "write_powershell", "stop_powershell"
        };

        try
        {
            using JsonDocument doc = JsonDocument.Parse(assistantMessageLine);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return result;
            if (!data.TryGetProperty("toolRequests", out var toolRequests))
                return result;
            if (toolRequests.ValueKind != JsonValueKind.Array)
                return result;

            foreach (JsonElement toolReq in toolRequests.EnumerateArray())
            {
                string? toolCallId = toolReq.TryGetProperty("toolCallId", out var tcId) ? tcId.GetString() : null;
                string? toolName = toolReq.TryGetProperty("name", out var tn) ? tn.GetString() : null;

                if (toolCallId == null || toolName == null) continue;
                if (!approvalTools.Contains(toolName)) continue;
                if (completedToolCallIds.Contains(toolCallId)) continue;
                if (startedToolCallIds.Contains(toolCallId)) continue;

                string? command = null;
                string? description = null;
                if (toolReq.TryGetProperty("arguments", out var args) &&
                    args.ValueKind == JsonValueKind.Object)
                {
                    command = args.TryGetProperty("command", out var cmd) ? cmd.GetString() : null;
                    description = args.TryGetProperty("description", out var desc) ? desc.GetString() : null;

                    if (command == null && args.TryGetProperty("path", out var pathEl))
                    {
                        string? path = pathEl.GetString();
                        if (path != null)
                        {
                            command = path;
                            description ??= toolName switch
                            {
                                "edit" => "Edit file",
                                "create" => "Create file",
                                _ => null,
                            };
                        }
                    }
                }

                result.Add(new PendingCommand
                {
                    ToolName = toolName,
                    Description = description,
                    Command = command,
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ParsePendingCommands error: {ex.Message}");
        }

        return result;
    }

    private static AISessionState DeriveState(string? eventType, bool hasWaitingTools)
    {
        return eventType switch
        {
            "session.task_complete" or "session.shutdown"
                => AISessionState.Done,

            "user.message"
                => AISessionState.Thinking,

            "tool.execution_start"
                => AISessionState.ExecutingTool,

            "tool.execution_complete" or "tool.user_requested"
                => AISessionState.Working,

            "assistant.turn_end"
                => AISessionState.Idle,

            "assistant.turn_start"
                => AISessionState.Working,

            "assistant.message" when hasWaitingTools
                => AISessionState.WaitingForUser,
            "assistant.message"
                => AISessionState.Working,

            "hook.start" or "hook.end"
            or "subagent.started" or "subagent.completed" or "subagent.failed"
            or "session.plan_changed" or "session.compaction_start" or "session.compaction_complete"
            or "session.context_changed" or "system.notification"
                => AISessionState.Working,

            "session.start" or "session.resume" or "session.warning"
            or "session.mode_changed" or "abort"
                => AISessionState.Idle,

            _ => AISessionState.Unknown,
        };
    }

    public void Refresh()
    {
        _ = Task.Run(PollSessions);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _livenessTimer?.Dispose();
        _livenessTimer = null;
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        if (_lockWatcher != null)
        {
            _lockWatcher.EnableRaisingEvents = false;
            _lockWatcher.Dispose();
            _lockWatcher = null;
        }
    }
}
