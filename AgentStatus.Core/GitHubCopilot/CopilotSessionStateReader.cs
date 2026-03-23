using AgentStatus.Core.Common;
using System.Diagnostics;
using System.Text.Json;

namespace AgentStatus.Core.GitHubCopilot;

/// <summary>
/// Reads Copilot CLI session JSONL event streams and derives the current session state.
/// </summary>
internal static class CopilotSessionStateReader
{
    internal static void ReadSessionState(CopilotSessionInfo info, TextReader reader)
    {
        string? lastLine = null;
        string? lastStateLine = null;
        string? lastStateType = null;
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
            "session.start", "session.resume", "session.warning", "session.mode_changed",
            "session.plan_changed", "session.compaction_start", "session.compaction_complete",
            "session.context_changed",
            "user.message",
            "tool.execution_start", "tool.execution_complete", "tool.user_requested",
            "assistant.turn_end", "assistant.turn_start", "assistant.message",
            "hook.start", "hook.end",
            "subagent.started", "subagent.completed", "subagent.failed",
            "system.notification",
            "abort",
        };

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

        if (lastLine == null)
            return;

        // Derive state from last state-defining event
        bool hasWaitingTools = lastStateType == "assistant.message" && lastStateLine != null
            && HasWaitingToolRequestFromLine(lastStateLine);
        info.State = DeriveState(lastStateType, hasWaitingTools);

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

    private static bool HasWaitingToolRequestFromLine(string jsonLine)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonLine);
            JsonElement data = doc.RootElement.TryGetProperty("data", out JsonElement d) ? d : doc.RootElement;
            return HasWaitingToolRequest(data);
        }
        catch { return false; }
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
}
