using AgentStatus.Core.Common;
using AgentStatus.Core.GitHubCopilot;

namespace AgentStatus.Core.Tests;

[TestClass]
public class CopilotSessionStateReaderTests
{
    [TestMethod]
    // Done
    [DataRow("""{"type":"session.task_complete","data":{}}""", AISessionState.Done)]
    [DataRow("""{"type":"session.shutdown","data":{}}""", AISessionState.Done)]
    // Thinking
    [DataRow("""{"type":"user.message","data":{"content":"Fix the login bug"}}""", AISessionState.Thinking)]
    // ExecutingTool
    [DataRow("""{"type":"tool.execution_start","data":{"toolCallId":"tc1","toolName":"powershell"}}""", AISessionState.ExecutingTool)]
    // Working
    [DataRow("""{"type":"tool.execution_complete","data":{"toolCallId":"tc1"}}""", AISessionState.Working)]
    [DataRow("""{"type":"tool.user_requested","data":{"toolCallId":"tc1"}}""", AISessionState.Working)]
    [DataRow("""{"type":"assistant.turn_start","data":{}}""", AISessionState.Working)]
    [DataRow("""{"type":"assistant.message","data":{"content":"I'll help you with that"}}""", AISessionState.Idle)]
    [DataRow("""{"type":"assistant.message","data":{"toolRequests":[{"name":"grep","toolCallId":"tc4","arguments":{}}]}}""", AISessionState.Working)]
    [DataRow("""{"type":"hook.start","data":{"hookName":"pre-commit"}}""", AISessionState.Working)]
    [DataRow("""{"type":"hook.end","data":{"hookName":"pre-commit"}}""", AISessionState.Working)]
    [DataRow("""{"type":"subagent.started","data":{"name":"explore"}}""", AISessionState.Working)]
    [DataRow("""{"type":"subagent.completed","data":{"name":"explore"}}""", AISessionState.Working)]
    [DataRow("""{"type":"subagent.failed","data":{"name":"explore"}}""", AISessionState.Working)]
    [DataRow("""{"type":"session.plan_changed","data":{}}""", AISessionState.Working)]
    [DataRow("""{"type":"session.compaction_start","data":{}}""", AISessionState.Working)]
    [DataRow("""{"type":"session.compaction_complete","data":{}}""", AISessionState.Working)]
    [DataRow("""{"type":"session.context_changed","data":{}}""", AISessionState.Working)]
    [DataRow("""{"type":"system.notification","data":{"message":"info"}}""", AISessionState.Working)]
    // Idle
    [DataRow("""{"type":"assistant.turn_end","data":{}}""", AISessionState.Idle)]
    [DataRow("""{"type":"session.start","data":{"mode":"interactive"}}""", AISessionState.Idle)]
    [DataRow("""{"type":"session.resume","data":{}}""", AISessionState.Idle)]
    [DataRow("""{"type":"session.warning","data":{"message":"rate limited"}}""", AISessionState.Idle)]
    [DataRow("""{"type":"session.mode_changed","data":{"newMode":"plan"}}""", AISessionState.Idle)]
    [DataRow("""{"type":"abort","data":{}}""", AISessionState.Idle)]
    // WaitingForUser
    [DataRow("""{"type":"assistant.message","data":{"toolRequests":[{"name":"ask_user","toolCallId":"tc2","arguments":{"question":"Which database?"}}]}}""", AISessionState.WaitingForUser)]
    [DataRow("""{"type":"assistant.message","data":{"toolRequests":[{"name":"exit_plan_mode","toolCallId":"tc3","arguments":{}}]}}""", AISessionState.WaitingForUser)]
    [DataRow("""{"type":"assistant.message","data":{"toolRequests":[{"name":"grep","toolCallId":"tc4","arguments":{}},{"name":"ask_user","toolCallId":"tc5","arguments":{"question":"Confirm?"}}]}}""", AISessionState.WaitingForUser)]
    // Unknown
    [DataRow("""{"type":"some.unknown.event","data":{}}""", AISessionState.Unknown)]
    [DataRow("""{"data":"something"}""", AISessionState.Unknown)]
    public void ReadSessionState_SingleEvent_ReturnsExpectedState(string json, AISessionState expected)
    {
        Assert.AreEqual(expected, ReadState(json).State);
    }

    [TestMethod]
    public void ReadSessionState_TaskCompleteThenTurnEnd_ReturnsDone()
    {
        // task_complete followed by cleanup events should still report Done
        string jsonl = """
            {"type":"session.task_complete","data":{}}
            {"type":"assistant.turn_end","data":{}}
            """;
        Assert.AreEqual(AISessionState.Done, ReadState(jsonl).State);
    }

    [TestMethod]
    public void ReadSessionState_TaskCompleteThenUserMessage_ReturnsThinking()
    {
        // A new user message after task_complete resets the Done state
        string jsonl = """
            {"type":"session.task_complete","data":{}}
            {"type":"user.message","data":{"content":"Do more"}}
            """;
        Assert.AreEqual(AISessionState.Thinking, ReadState(jsonl).State);
    }

    [TestMethod]
    public void ReadSessionState_ExtractsLastUserMessage()
    {
        string jsonl = """
            {"type":"user.message","data":{"content":"First message"}}
            {"type":"assistant.message","data":{"content":"ok"}}
            {"type":"user.message","data":{"content":"Second message"}}
            """;
        Assert.AreEqual("Second message", ReadState(jsonl).LastUserMessage);
    }

    [TestMethod]
    public void ReadSessionState_ExtractsIntent()
    {
        string jsonl = """
            {"type":"tool.execution_start","data":{"toolCallId":"tc1","toolName":"report_intent","arguments":{"intent":"Fixing CSS"}}}
            {"type":"tool.execution_complete","data":{"toolCallId":"tc1"}}
            """;
        Assert.AreEqual("Fixing CSS", ReadState(jsonl).CurrentIntent);
    }

    [TestMethod]
    public void ReadSessionState_ExtractsMode()
    {
        string json = """{"type":"session.start","data":{"mode":"plan"}}""";
        Assert.AreEqual(AISessionMode.Plan, ReadState(json).Mode);
    }

    [TestMethod]
    public void ReadSessionState_ResolvedAskUserFollowedByPlainAnswer_ReturnsIdle()
    {
        // Repro: a turn with an ask_user that the user answered, followed by a
        // second turn that produces only a plain text answer (no toolRequests)
        // and ends with assistant.turn_end. The session is now idle waiting for
        // the next user message — should report Idle, not Working.
        string jsonl = """
            {"type":"user.message","data":{"content":"explain"}}
            {"type":"assistant.turn_start","data":{"turnId":"0"}}
            {"type":"assistant.message","data":{"messageId":"m1","toolRequests":[{"name":"ask_user","toolCallId":"au1","arguments":{"question":"clarify?"}}]}}
            {"type":"tool.execution_start","data":{"toolCallId":"au1","toolName":"ask_user","arguments":{"question":"clarify?"}}}
            {"type":"hook.start","data":{"hookName":"x"}}
            {"type":"hook.end","data":{"hookName":"x"}}
            {"type":"tool.execution_complete","data":{"toolCallId":"au1"}}
            {"type":"assistant.turn_end","data":{"turnId":"0"}}
            {"type":"assistant.turn_start","data":{"turnId":"1"}}
            {"type":"assistant.message","data":{"messageId":"m2","content":"Here is the explanation."}}
            {"type":"assistant.turn_end","data":{"turnId":"1"}}
            """;
        var info = ReadState(jsonl);
        Assert.AreEqual(AISessionState.Idle, info.State);
    }

    [TestMethod]
    public void ReadSessionState_FinalAssistantMessageWithoutToolRequests_ReturnsIdle()
    {
        // When the model produces a final answer (assistant.message with no
        // toolRequests), the agent has finished its turn — the trailing
        // assistant.turn_end is just a marker. If the tray polls in the brief
        // window between these two events, it would otherwise see "Working"
        // while the session is actually idle and waiting for the next user
        // input. State should be Idle in this case.
        string jsonl = """
            {"type":"user.message","data":{"content":"hi"}}
            {"type":"assistant.turn_start","data":{"turnId":"0"}}
            {"type":"assistant.message","data":{"messageId":"m1","content":"Hello!","toolRequests":[]}}
            """;
        Assert.AreEqual(AISessionState.Idle, ReadState(jsonl).State);
    }

    [TestMethod]
    public void ReadSessionState_AssistantMessageWithToolRequests_StaysWorking()
    {
        // Sanity check: assistant.message with non-ask tool requests still
        // means the agent is mid-turn (about to invoke tools). Don't regress.
        string jsonl = """
            {"type":"user.message","data":{"content":"hi"}}
            {"type":"assistant.turn_start","data":{"turnId":"0"}}
            {"type":"assistant.message","data":{"messageId":"m1","toolRequests":[{"name":"grep","toolCallId":"t1","arguments":{}}]}}
            """;
        Assert.AreEqual(AISessionState.Working, ReadState(jsonl).State);
    }

    [TestMethod]
    public void ReadSessionState_EmptyReader_LeavesDefault()
    {
        var info = ReadState("");
        Assert.AreEqual(AISessionState.Unknown, info.State);
    }

    private static CopilotSessionInfo ReadState(string jsonl)
    {
        var info = new CopilotSessionInfo { SessionId = "test-session" };
        CopilotSessionStateReader.ReadSessionState(info, new StringReader(jsonl));
        return info;
    }
}
