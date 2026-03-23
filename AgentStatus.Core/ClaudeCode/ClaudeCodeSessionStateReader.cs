using AgentStatus.Core.Common;
using System.Diagnostics;
using System.Text.Json;

namespace AgentStatus.Core.ClaudeCode;

/// <summary>
/// Reads Claude Code session JSONL event streams and derives the current session state.
/// </summary>
internal static class ClaudeCodeSessionStateReader
{
    internal static void ReadSessionState(ClaudeCodeSessionInfo info, TextReader reader)
    {
        string? lastLine = null;
        string? lastUserMessage = null;
        string? lastTopLevelType = null;
        bool hasToolUseInLastAssistant = false;
        bool lastAssistantHadStopReasonToolUse = false;
        bool lastAssistantStopReasonNull = false;
        bool lastIsToolResult = false;
        string? lastToolUseName = null;
        string? pendingQuestion = null;
        string[]? pendingChoices = null;

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

                string? type = root.TryGetProperty("type", out JsonElement typeProp)
                    ? typeProp.GetString() : null;
                if (type == null)
                    continue;

                if (type is "file-history-snapshot" or "system" or "queue-operation")
                    continue;

                // Navigate to message object if present
                JsonElement msg = root.TryGetProperty("message", out JsonElement m) ? m : root;

                if (type == "user")
                {
                    if (HasContentBlockOfType(msg, "tool_result"))
                    {
                        lastIsToolResult = true;
                    }
                    else
                    {
                        lastTopLevelType = "user";
                        lastIsToolResult = false;

                        if (msg.TryGetProperty("content", out JsonElement contentEl) &&
                            contentEl.ValueKind == JsonValueKind.String)
                        {
                            string? content = contentEl.GetString();
                            if (content != null && !content.StartsWith("[{"))
                                lastUserMessage = content;
                        }
                    }
                }
                else if (type == "assistant")
                {
                    lastTopLevelType = "assistant";
                    lastIsToolResult = false;

                    hasToolUseInLastAssistant = false;
                    lastAssistantHadStopReasonToolUse = false;
                    lastAssistantStopReasonNull = false;
                    lastToolUseName = null;
                    pendingQuestion = null;
                    pendingChoices = null;

                    if (msg.TryGetProperty("content", out JsonElement contentEl) &&
                        contentEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement block in contentEl.EnumerateArray())
                        {
                            string? blockType = block.TryGetProperty("type", out JsonElement bt)
                                ? bt.GetString() : null;
                            if (blockType != "tool_use")
                                continue;

                            hasToolUseInLastAssistant = true;
                            string? toolName = block.TryGetProperty("name", out JsonElement nameEl)
                                ? nameEl.GetString() : null;
                            if (toolName != null)
                                lastToolUseName = toolName;

                            if (toolName == "AskUserQuestion" &&
                                block.TryGetProperty("input", out JsonElement inputEl) &&
                                inputEl.ValueKind == JsonValueKind.Object &&
                                inputEl.TryGetProperty("question", out JsonElement qEl))
                            {
                                pendingQuestion = qEl.GetString();
                                pendingChoices = null;
                            }
                        }
                    }

                    if (msg.TryGetProperty("stop_reason", out JsonElement srEl))
                    {
                        if (srEl.ValueKind == JsonValueKind.String)
                        {
                            string? sr = srEl.GetString();
                            if (sr == "tool_use")
                                lastAssistantHadStopReasonToolUse = true;
                        }
                        else if (srEl.ValueKind == JsonValueKind.Null)
                        {
                            lastAssistantStopReasonNull = true;
                        }
                    }
                }
            }
        }

        if (lastLine == null)
            return;

        info.State = DeriveState(lastTopLevelType, hasToolUseInLastAssistant,
            lastAssistantHadStopReasonToolUse, lastAssistantStopReasonNull);

        info.LastUserMessage = lastUserMessage;
        info.Mode = AISessionMode.Interactive;

        if (pendingQuestion != null && info.State is AISessionState.ExecutingTool or AISessionState.Working)
        {
            info.PendingQuestion = pendingQuestion;
            info.PendingChoices = pendingChoices;
            info.State = AISessionState.WaitingForUser;
        }
        else if (info.State != AISessionState.WaitingForUser)
        {
            info.PendingQuestion = null;
            info.PendingChoices = null;
        }

        if (lastAssistantHadStopReasonToolUse && !lastIsToolResult &&
            lastTopLevelType == "assistant" && hasToolUseInLastAssistant)
        {
            bool isAutoApproved = lastToolUseName is "Read" or "Glob" or "Grep"
                or "Agent" or "Explore" or "Plan" or "ToolSearch" or "Skill";

            if (!isAutoApproved)
            {
                info.PendingCommands = [new PendingCommand
                {
                    ToolName = lastToolUseName ?? "unknown",
                    Description = $"Approve {lastToolUseName}",
                }];
                info.State = AISessionState.WaitingForUser;
            }
        }
        else
        {
            info.PendingCommands = null;
        }

        Debug.WriteLine($"[ClaudeCode] {info.SessionId[..8]}: State={info.State}, lastType={lastTopLevelType}");
    }

    private static bool HasContentBlockOfType(JsonElement msg, string blockType)
    {
        if (!msg.TryGetProperty("content", out JsonElement contentEl) ||
            contentEl.ValueKind != JsonValueKind.Array)
            return false;

        foreach (JsonElement block in contentEl.EnumerateArray())
        {
            if (block.TryGetProperty("type", out JsonElement bt) &&
                bt.ValueKind == JsonValueKind.String &&
                bt.GetString() == blockType)
                return true;
        }
        return false;
    }

    private static AISessionState DeriveState(string? lastTopLevelType,
        bool hasToolUse, bool stopReasonToolUse, bool stopReasonNull)
    {
        if (lastTopLevelType == "user")
            return AISessionState.Thinking;

        if (lastTopLevelType == "assistant")
        {
            if (stopReasonNull)
                return AISessionState.Working;
            if (hasToolUse && stopReasonToolUse)
                return AISessionState.ExecutingTool;
            return AISessionState.Idle;
        }

        return AISessionState.Unknown;
    }
}
