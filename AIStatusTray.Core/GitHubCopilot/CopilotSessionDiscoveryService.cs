using AIStatusTray.Core.Common;
using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace AIStatusTray.Core.GitHubCopilot;

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
                        ReadWorkspaceMetadata(info);
                    }
                    else if (string.IsNullOrEmpty(info.Cwd) && string.IsNullOrEmpty(info.Repository))
                    {
                        ReadWorkspaceMetadata(info);
                        if (!string.IsNullOrEmpty(info.Cwd) || !string.IsNullOrEmpty(info.Repository))
                            changed = true;
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
            // Collect all running copilot.exe PIDs and their command lines
            Dictionary<int, string> runningPids = new();

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
                    result[match.Groups[1].Value] = (pid, cmdLine);
                }
            }

            // Strategy 2: scan lock files for sessions not found via --resume.
            // Track claimed PIDs to prevent stale lock files from matching
            // a different copilot.exe instance through PID reuse.
            if (Directory.Exists(SessionStatePath))
            {
                HashSet<int> claimedPids = new(result.Values.Select(v => v.Item1));

                foreach (string sessionDir in Directory.EnumerateDirectories(SessionStatePath))
                {
                    string sessionId = Path.GetFileName(sessionDir);

                    // Skip sessions already discovered via --resume
                    if (result.ContainsKey(sessionId))
                        continue;

                    foreach (string lockFile in Directory.EnumerateFiles(sessionDir, "inuse.*.lock"))
                    {
                        Match lockMatch = LockFilePidRegex().Match(Path.GetFileName(lockFile));
                        if (lockMatch.Success && int.TryParse(lockMatch.Groups[1].Value, out int lockPid)
                            && !claimedPids.Contains(lockPid)
                            && runningPids.TryGetValue(lockPid, out string? cmdLine))
                        {
                            result[sessionId] = (lockPid, cmdLine);
                            claimedPids.Add(lockPid);
                            break;
                        }
                    }
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
                return;

            string? lastLine = null;
            string? lastStateLine = null;
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

                    foreach (string eventType in stateDefiningTypes)
                    {
                        if (line.Contains($"\"type\":\"{eventType}\""))
                        {
                            lastStateLine = line;
                            break;
                        }
                    }

                    if (line.Contains("\"type\":\"session.start\"") || line.Contains("\"session.start\""))
                    {
                        string? mode = ExtractJsonValue(line, "mode");
                        if (mode != null)
                            lastMode = mode;
                    }

                    if (line.Contains("\"type\":\"session.task_complete\""))
                        sawTaskComplete = true;

                    if (line.Contains("\"type\":\"tool.execution_complete\""))
                    {
                        string? toolCallId = ExtractJsonValue(line, "toolCallId");
                        if (toolCallId != null)
                            completedToolCallIds.Add(toolCallId);
                    }

                    if (line.Contains("\"type\":\"tool.execution_start\""))
                    {
                        string? toolCallId = ExtractJsonValue(line, "toolCallId");
                        if (toolCallId != null)
                        {
                            startedToolCallIds.Add(toolCallId);
                            if (line.Contains("\"ask_user\"") || line.Contains("\"exit_plan_mode\""))
                                askUserStarts.Add((toolCallId, line));
                        }
                    }

                    if (line.Contains("\"type\":\"tool.user_requested\""))
                    {
                        string? toolCallId = ExtractJsonValue(line, "toolCallId");
                        if (toolCallId != null)
                            userRequestedToolCallIds.Add(toolCallId);
                    }

                    if (line.Contains("\"type\":\"assistant.message\""))
                    {
                        lastAssistantMessageLine = line;
                    }

                    if (line.Contains("\"type\":\"user.message\""))
                    {
                        sawTaskComplete = false;
                        string? content = ExtractJsonValue(line, "content");
                        if (content != null)
                            lastUserMessage = content;
                    }

                    if (line.Contains("\"report_intent\"") && line.Contains("\"intent\":"))
                    {
                        string? intent = ExtractJsonValue(line, "intent");
                        if (intent != null)
                            lastIntent = intent;
                    }

                    if (line.Contains("\"type\":\"session.mode_changed\""))
                    {
                        string? newMode = ExtractJsonValue(line, "newMode");
                        if (newMode != null)
                            lastMode = newMode;
                    }
                }
            }

            if (lastLine == null)
                return;

            info.State = DeriveStateFromEvent(lastStateLine ?? lastLine);

            // task_complete is followed by hook/tool cleanup events and
            // assistant.turn_end, which would otherwise override the state
            // to Idle. Restore Done when no new user.message followed.
            if (sawTaskComplete && info.State != AISessionState.Thinking)
                info.State = AISessionState.Done;

            if (info.State == AISessionState.Working && lastStateLine != null &&
                lastStateLine.Contains("\"type\":\"tool.execution_complete\""))
            {
                string? completedToolCallId = ExtractJsonValue(lastStateLine, "toolCallId");
                if (completedToolCallId != null && userRequestedToolCallIds.Contains(completedToolCallId))
                    info.State = AISessionState.Idle;
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

    private static string? ExtractJsonValue(string json, string key)
    {
        string pattern = $"\"{key}\":\"";
        int start = json.IndexOf(pattern, StringComparison.Ordinal);
        if (start < 0) return null;

        start += pattern.Length;
        int end = json.IndexOf('"', start);
        return end > start ? json[start..end] : null;
    }

    private static void ParseAskUserArguments(string jsonLine, CopilotSessionInfo info)
    {
        try
        {
            int argsIdx = jsonLine.IndexOf("\"arguments\":", StringComparison.Ordinal);
            if (argsIdx < 0) return;

            int braceStart = jsonLine.IndexOf('{', argsIdx);
            if (braceStart < 0) return;

            int depth = 0;
            int braceEnd = -1;
            for (int i = braceStart; i < jsonLine.Length; i++)
            {
                if (jsonLine[i] == '{') depth++;
                else if (jsonLine[i] == '}') { depth--; if (depth == 0) { braceEnd = i; break; } }
            }

            if (braceEnd < 0) return;

            string argsJson = jsonLine[braceStart..(braceEnd + 1)];
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(argsJson);

            if (doc.RootElement.TryGetProperty("question", out System.Text.Json.JsonElement questionEl))
            {
                info.PendingQuestion = questionEl.GetString();
            }

            if (doc.RootElement.TryGetProperty("choices", out System.Text.Json.JsonElement choicesEl) &&
                choicesEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                info.PendingChoices = choicesEl.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();
            }

            if (doc.RootElement.TryGetProperty("summary", out System.Text.Json.JsonElement summaryEl))
            {
                info.PendingQuestion ??= summaryEl.GetString();
            }

            if (info.PendingChoices == null &&
                doc.RootElement.TryGetProperty("actions", out System.Text.Json.JsonElement actionsEl) &&
                actionsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
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
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(assistantMessageLine);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return result;
            if (!data.TryGetProperty("toolRequests", out var toolRequests))
                return result;
            if (toolRequests.ValueKind != System.Text.Json.JsonValueKind.Array)
                return result;

            foreach (System.Text.Json.JsonElement toolReq in toolRequests.EnumerateArray())
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
                    args.ValueKind == System.Text.Json.JsonValueKind.Object)
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

    private static AISessionState DeriveStateFromEvent(string jsonLine)
    {
        if (jsonLine.Contains("\"type\":\"session.task_complete\""))
            return AISessionState.Done;

        if (jsonLine.Contains("\"type\":\"session.shutdown\""))
            return AISessionState.Done;

        if (jsonLine.Contains("\"type\":\"user.message\""))
            return AISessionState.Thinking;

        if (jsonLine.Contains("\"type\":\"tool.execution_start\""))
            return AISessionState.ExecutingTool;

        if (jsonLine.Contains("\"type\":\"tool.execution_complete\""))
            return AISessionState.Working;

        if (jsonLine.Contains("\"type\":\"assistant.turn_end\""))
            return AISessionState.Idle;

        if (jsonLine.Contains("\"type\":\"assistant.turn_start\""))
            return AISessionState.Working;

        if (jsonLine.Contains("\"type\":\"assistant.message\""))
        {
            if (jsonLine.Contains("\"ask_user\"") || jsonLine.Contains("\"exit_plan_mode\""))
                return AISessionState.WaitingForUser;

            return AISessionState.Working;
        }

        if (jsonLine.Contains("\"type\":\"hook.start\"") || jsonLine.Contains("\"type\":\"hook.end\""))
            return AISessionState.Working;

        if (jsonLine.Contains("\"type\":\"subagent.started\"") ||
            jsonLine.Contains("\"type\":\"subagent.completed\"") ||
            jsonLine.Contains("\"type\":\"subagent.failed\""))
            return AISessionState.Working;

        if (jsonLine.Contains("\"type\":\"tool.user_requested\""))
            return AISessionState.Working;

        if (jsonLine.Contains("\"type\":\"session.plan_changed\"") ||
            jsonLine.Contains("\"type\":\"session.compaction_start\"") ||
            jsonLine.Contains("\"type\":\"session.compaction_complete\"") ||
            jsonLine.Contains("\"type\":\"session.context_changed\"") ||
            jsonLine.Contains("\"type\":\"system.notification\""))
            return AISessionState.Working;

        if (jsonLine.Contains("\"type\":\"session.start\"") ||
            jsonLine.Contains("\"type\":\"session.resume\"") ||
            jsonLine.Contains("\"type\":\"session.warning\"") ||
            jsonLine.Contains("\"type\":\"session.mode_changed\"") ||
            jsonLine.Contains("\"type\":\"abort\""))
            return AISessionState.Idle;

        return AISessionState.Unknown;
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
