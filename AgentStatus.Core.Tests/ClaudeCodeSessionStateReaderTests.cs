using AgentStatus.Core.Common;
using AgentStatus.Core.ClaudeCode;

namespace AgentStatus.Core.Tests;

[TestClass]
public class ClaudeCodeSessionStateReaderTests
{
    [TestMethod]
    // Thinking — user text messages
    [DataRow("""{"type":"user","message":{"role":"user","content":"hello"}}""", AISessionState.Thinking)]
    [DataRow("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Fix the bug"}]}}""", AISessionState.Thinking)]
    // Working — assistant streaming (stop_reason null)
    [DataRow("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Let me think..."}],"stop_reason":null}}""", AISessionState.Working)]
    [DataRow("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"Bash","id":"t1","input":{}}],"stop_reason":null}}""", AISessionState.Working)]
    // ExecutingTool — auto-approved tool_use content with stop_reason "tool_use"
    [DataRow("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"Read","id":"t1","input":{"path":"f.txt"}}],"stop_reason":"tool_use"}}""", AISessionState.ExecutingTool)]
    [DataRow("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I'll read this"},{"type":"tool_use","name":"Glob","id":"t2","input":{}}],"stop_reason":"tool_use"}}""", AISessionState.ExecutingTool)]
    // Idle — assistant end_turn or no stop_reason property
    [DataRow("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done!"}],"stop_reason":"end_turn"}}""", AISessionState.Idle)]
    [DataRow("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"Bash","id":"t1","input":{}}],"stop_reason":"end_turn"}}""", AISessionState.Idle)]
    [DataRow("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hi"}]}}""", AISessionState.Idle)]
    // Unknown — tool_result, skipped types, unrecognised, missing type
    [DataRow("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""", AISessionState.Unknown)]
    [DataRow("""{"type":"progress","data":{}}""", AISessionState.Unknown)]
    [DataRow("""{"type":"system","message":{"content":"init"}}""", AISessionState.Unknown)]
    [DataRow("""{"data":"something"}""", AISessionState.Unknown)]
    public void ReadSessionState_SingleEvent_ReturnsExpectedState(string json, AISessionState expected)
    {
        Assert.AreEqual(expected, ReadState(json).State);
    }

    [TestMethod]
    public void ReadSessionState_ToolUseFollowedByToolResult_StaysExecutingTool()
    {
        // Assistant requests tool, then user sends tool_result — tool_result doesn't change the top-level type
        string jsonl = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"Bash","id":"t1","input":{"command":"ls"}}],"stop_reason":"tool_use"}}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"file1.txt"}]}}
            """;
        // The last state-defining event was the assistant with tool_use, tool_result doesn't override
        Assert.AreEqual(AISessionState.ExecutingTool, ReadState(jsonl).State);
    }

    [TestMethod]
    public void ReadSessionState_ExtractsLastUserMessage()
    {
        string jsonl = """
            {"type":"user","message":{"role":"user","content":"First message"}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}}
            {"type":"user","message":{"role":"user","content":"Second message"}}
            """;
        Assert.AreEqual("Second message", ReadState(jsonl).LastUserMessage);
    }

    [TestMethod]
    public void ReadSessionState_AskUserQuestion_SetsWaitingForUser()
    {
        // Assistant asks a question via AskUserQuestion tool with stop_reason tool_use
        string json = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"AskUserQuestion","id":"t1","input":{"question":"Which framework?"}}],"stop_reason":"tool_use"}}""";
        var info = ReadState(json);
        Assert.AreEqual(AISessionState.WaitingForUser, info.State);
        Assert.AreEqual("Which framework?", info.PendingQuestion);
    }

    [TestMethod]
    public void ReadSessionState_NonAutoApprovedTool_SetsWaitingWithPendingCommand()
    {
        // Assistant requests a non-auto-approved tool (e.g., Bash)
        string json = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"Bash","id":"t1","input":{"command":"rm -rf /"}}],"stop_reason":"tool_use"}}""";
        var info = ReadState(json);
        Assert.AreEqual(AISessionState.WaitingForUser, info.State);
        Assert.IsNotNull(info.PendingCommands);
        Assert.HasCount(1, info.PendingCommands);
        Assert.AreEqual("Bash", info.PendingCommands[0].ToolName);
    }

    [TestMethod]
    public void ReadSessionState_AutoApprovedTool_StaysExecutingTool()
    {
        // Assistant requests an auto-approved tool (e.g., Read)
        string json = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"Read","id":"t1","input":{"path":"file.txt"}}],"stop_reason":"tool_use"}}""";
        var info = ReadState(json);
        Assert.AreEqual(AISessionState.ExecutingTool, info.State);
        Assert.IsNull(info.PendingCommands);
    }

    [TestMethod]
    public void ReadSessionState_EmptyReader_LeavesDefault()
    {
        var info = ReadState("");
        Assert.AreEqual(AISessionState.Unknown, info.State);
    }

    private static ClaudeCodeSessionInfo ReadState(string jsonl)
    {
        var info = new ClaudeCodeSessionInfo { SessionId = "test-session-id" };
        ClaudeCodeSessionStateReader.ReadSessionState(info, new StringReader(jsonl));
        return info;
    }
}
