using System.Text.Json.Nodes;
using Conduit.Host.Core.Agents;
using Conduit.Host.Core.Protocol;
using Xunit;

namespace Conduit.Host.Core.Tests;

internal sealed class RecordingSink : IAgentSink
{
    public List<JsonObject> Events { get; } = [];
    public string? Resume { get; private set; }
    public void Emit(JsonObject ev) => Events.Add(ev);
    public void SetResumeToken(string token) => Resume = token;
    public void Exited(int? exitCode, string? reason) { }
    public IEnumerable<JsonObject> Of(string kind) => Events.Where(e => Ev.Kind(e) == kind);
}

/// <summary>Feeds recorded native protocol lines into adapters and checks the normalised output.</summary>
public class AdapterParsingTests
{
    private static JsonObject J(string s) => (JsonObject)JsonNode.Parse(s)!;

    private static T WithSink<T>(T adapter, RecordingSink sink) where T : class
    {
        typeof(T).GetField("_sink", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(adapter, sink);
        return adapter;
    }

    [Fact]
    public void ClaudeStreamJson()
    {
        var sink = new RecordingSink();
        var a = WithSink(new ClaudeAdapter(), sink);
        a.OnMessage(J("""{"type":"system","subtype":"init","session_id":"abc"}"""));
        a.OnMessage(J("""{"type":"stream_event","parent_tool_use_id":null,"event":{"type":"message_start","message":{"id":"msg_1"}}}"""));
        a.OnMessage(J("""{"type":"stream_event","parent_tool_use_id":null,"event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}}"""));
        a.OnMessage(J("""{"type":"assistant","parent_tool_use_id":null,"message":{"id":"msg_1","content":[{"type":"text","text":"Hi there"}]}}"""));
        a.OnMessage(J("""{"type":"assistant","parent_tool_use_id":null,"message":{"id":"msg_1","content":[{"type":"tool_use","id":"tu_1","name":"Bash","input":{"command":"npm test"}}]}}"""));
        a.OnMessage(J("""{"type":"assistant","parent_tool_use_id":null,"message":{"id":"msg_1","content":[{"type":"tool_use","id":"tu_2","name":"TodoWrite","input":{"todos":[{"content":"a","status":"completed"},{"content":"b","status":"in_progress"}]}}]}}"""));
        a.OnMessage(J("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu_1","content":"ok","is_error":false}]}}"""));
        a.OnMessage(J("""{"type":"result","subtype":"success","is_error":false,"result":"Hi there"}"""));

        Assert.Equal("abc", sink.Resume);
        Assert.Equal("Hi", (string?)sink.Of(Ev.KDelta).Single()["text"]);
        Assert.Equal("Hi there", (string?)sink.Of(Ev.KMessage).Single()["text"]);
        var tools = sink.Of(Ev.KTool).ToList();
        Assert.Equal("Run npm test", (string?)tools[0]["title"]);
        Assert.Equal("ok", (string?)tools[1]["status"]);
        Assert.Equal(2, ((JsonArray)sink.Of(Ev.KPlan).Single()["items"]!).Count);
        Assert.Equal(SessionStates.Idle, (string?)sink.Of(Ev.KStatus).Last()["state"]);
    }

    [Fact]
    public void CodexAppServer()
    {
        var sink = new RecordingSink();
        var a = WithSink(new CodexAdapter(), sink);
        a.OnMessage(J("""{"method":"turn/started","params":{"turn":{"id":"turn_1","status":"inProgress"}}}"""));
        a.OnMessage(J("""{"method":"item/agentMessage/delta","params":{"itemId":"i1","delta":"Wor"}}"""));
        a.OnMessage(J("""{"method":"item/completed","params":{"item":{"type":"agentMessage","id":"i1","text":"Working"}}}"""));
        a.OnMessage(J("""{"method":"item/started","params":{"item":{"type":"commandExecution","id":"c1","command":"cargo test","status":"inProgress"}}}"""));
        a.OnMessage(J("""{"method":"turn/plan/updated","params":{"turnId":"turn_1","plan":[{"step":"x","status":"completed"},{"step":"y","status":"inProgress"}]}}"""));
        a.OnMessage(J("""{"method":"turn/completed","params":{"turn":{"id":"turn_1","status":"completed"}}}"""));

        Assert.Equal("Wor", (string?)sink.Of(Ev.KDelta).Single()["text"]);
        Assert.Equal("Working", (string?)sink.Of(Ev.KMessage).Single()["text"]);
        Assert.Equal("Run cargo test", (string?)sink.Of(Ev.KTool).Single()["title"]);
        var plan = (JsonArray)sink.Of(Ev.KPlan).Single()["items"]!;
        Assert.Equal("in_progress", (string?)plan[1]!["status"]);
        Assert.Equal(SessionStates.Idle, (string?)sink.Of(Ev.KStatus).Last()["state"]);
    }

    [Fact]
    public void PiRpc()
    {
        var sink = new RecordingSink();
        var a = WithSink(new PiAdapter(), sink);
        a.OnMessage(J("""{"type":"agent_start"}"""));
        a.OnMessage(J("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""));
        a.OnMessage(J("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"Hel"}}"""));
        a.OnMessage(J("""{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"Hello"}]}}"""));
        a.OnMessage(J("""{"type":"tool_execution_start","toolCallId":"t1","toolName":"read","args":{"path":"src/a.ts"}}"""));
        a.OnMessage(J("""{"type":"tool_execution_end","toolCallId":"t1","toolName":"read","result":{"content":[{"type":"text","text":"x"}]},"isError":false}"""));
        a.OnMessage(J("""{"type":"agent_settled"}"""));

        Assert.Equal("Hello", (string?)sink.Of(Ev.KMessage).Single()["text"]);
        Assert.Equal("Read a.ts", (string?)sink.Of(Ev.KTool).First()["title"]);
        Assert.Equal("x", (string?)sink.Of(Ev.KTool).Last()["output"]);
        Assert.Equal(SessionStates.Idle, (string?)sink.Of(Ev.KStatus).Last()["state"]);
    }

    [Fact]
    public void OpencodeEvents()
    {
        var sink = new RecordingSink();
        var a = WithSink(new OpencodeAdapter(), sink);
        typeof(OpencodeAdapter).GetField("_sessionId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(a, "ses_1");
        a.OnEvent(J("""{"type":"message.updated","properties":{"info":{"id":"msg_u","sessionID":"ses_1","role":"user"}}}"""));
        a.OnEvent(J("""{"type":"message.part.updated","properties":{"part":{"id":"p0","messageID":"msg_u","sessionID":"ses_1","type":"text","text":"do it"}}}"""));
        a.OnEvent(J("""{"type":"message.updated","properties":{"info":{"id":"msg_a","sessionID":"ses_1","role":"assistant"}}}"""));
        a.OnEvent(J("""{"type":"session.status","properties":{"sessionID":"ses_1","status":{"type":"busy"}}}"""));
        a.OnEvent(J("""{"type":"message.part.updated","properties":{"part":{"id":"p1","messageID":"msg_a","sessionID":"ses_1","type":"text","text":"On"},"delta":"On"}}"""));
        a.OnEvent(J("""{"type":"message.part.updated","properties":{"part":{"id":"p2","messageID":"msg_a","sessionID":"ses_1","type":"tool","tool":"bash","callID":"call_1","state":{"status":"running","input":{"command":"ls"}}}}}"""));
        a.OnEvent(J("""{"type":"permission.updated","properties":{"id":"per_1","sessionID":"ses_1","type":"bash","title":"Run rm -rf"}}"""));
        a.OnEvent(J("""{"type":"todo.updated","properties":{"sessionID":"ses_1","todos":[{"content":"a","status":"completed"}]}}"""));
        a.OnEvent(J("""{"type":"session.idle","properties":{"sessionID":"other"}}"""));
        a.OnEvent(J("""{"type":"session.idle","properties":{"sessionID":"ses_1"}}"""));

        Assert.Empty(sink.Of(Ev.KMessage));
        Assert.Equal("On", (string?)sink.Of(Ev.KDelta).Single()["text"]);
        Assert.Equal("Run ls", (string?)sink.Of(Ev.KTool).Single()["title"]);
        Assert.Equal("per_1", (string?)sink.Of(Ev.KApproval).Single()["aid"]);
        Assert.Single(sink.Of(Ev.KPlan));
        Assert.Equal(SessionStates.Idle, (string?)sink.Of(Ev.KStatus).Last()["state"]);
    }
}
