using System.Text.Json.Nodes;

namespace Conduit.Host.Core.Protocol;

/// <summary>Session lifecycle states shared with the phone (docs/PROTOCOL.md §4).</summary>
public static class SessionStates
{
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Waiting = "waiting";   // blocked on an approval
    public const string Idle = "idle";         // turn finished, ready for input
    public const string Error = "error";
    public const string Stopped = "stopped";   // agent process not running (resumable)

    public static bool IsActive(string s) => s is Starting or Running or Waiting;
}

public static class ApprovalDecisions
{
    public const string Allow = "allow";
    public const string AllowAlways = "allow_always";
    public const string Deny = "deny";
}

/// <summary>
/// Normalised agent events. Every adapter (Claude, Codex, Pi, opencode) maps its native stream onto these,
/// so the phone renders one transcript model regardless of the agent.
/// </summary>
public static class Ev
{
    public const string KUser = "user";
    public const string KDelta = "delta";
    public const string KMessage = "message";
    public const string KReasoning = "reasoning";
    public const string KTool = "tool";
    public const string KApproval = "approval";
    public const string KApprovalDone = "approval_done";
    public const string KStatus = "status";
    public const string KPlan = "plan";
    public const string KError = "error";
    public const string KNotice = "notice";

    public static string Kind(JsonObject ev) => (string?)ev["k"] ?? "";

    public static JsonObject User(string text, int images = 0)
    {
        var ev = new JsonObject { ["k"] = KUser, ["text"] = text };
        if (images > 0) ev["images"] = images;
        return ev;
    }

    /// <summary>Streaming assistant text, appended to message <paramref name="mid"/>.</summary>
    public static JsonObject Delta(string mid, string text) => new() { ["k"] = KDelta, ["mid"] = mid, ["text"] = text };

    /// <summary>Authoritative assistant text for <paramref name="mid"/>; replaces any streamed deltas.</summary>
    public static JsonObject Message(string mid, string text) => new() { ["k"] = KMessage, ["mid"] = mid, ["text"] = text };

    public static JsonObject Reasoning(string mid, string text, bool isDelta) =>
        new() { ["k"] = KReasoning, ["mid"] = mid, ["text"] = text, ["delta"] = isDelta };

    public static JsonObject Tool(string tid, string name, string title, string status, string? input = null, string? output = null) =>
        new()
        {
            ["k"] = KTool,
            ["tid"] = tid,
            ["name"] = name,
            ["title"] = title,
            ["status"] = status, // running | ok | error
            ["input"] = input is null ? null : Text.Clip(input, 2000),
            ["output"] = output is null ? null : Text.Clip(output, 6000),
        };

    public static JsonObject Approval(string aid, string title, string detail, string kind, bool canAlways = true) =>
        new() { ["k"] = KApproval, ["aid"] = aid, ["title"] = title, ["detail"] = Text.Clip(detail, 4000), ["kind"] = kind, ["always"] = canAlways };

    public static JsonObject ApprovalDone(string aid, string decision) => new() { ["k"] = KApprovalDone, ["aid"] = aid, ["decision"] = decision };

    public static JsonObject Status(string state, string? detail = null) => new() { ["k"] = KStatus, ["state"] = state, ["detail"] = detail };

    /// <summary>Plan / todo list. Items: { text, status: pending|in_progress|completed }.</summary>
    public static JsonObject Plan(IEnumerable<(string Text, string Status)> items)
    {
        var arr = new JsonArray();
        foreach (var (text, status) in items)
            arr.Add(new JsonObject { ["text"] = text, ["status"] = NormalizePlanStatus(status) });
        return new JsonObject { ["k"] = KPlan, ["items"] = arr };
    }

    public static JsonObject Error(string text) => new() { ["k"] = KError, ["text"] = text };

    public static JsonObject Notice(string text) => new() { ["k"] = KNotice, ["text"] = text };

    public static string NormalizePlanStatus(string s) => s.ToLowerInvariant() switch
    {
        "completed" or "complete" or "done" => "completed",
        "in_progress" or "inprogress" or "in-progress" or "active" or "running" => "in_progress",
        "cancelled" or "canceled" => "completed",
        _ => "pending",
    };
}

public static class Text
{
    public static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public static string FirstLine(string s, int max = 120)
    {
        var line = s.Split('\n', 2)[0].Trim();
        return Clip(line, max);
    }

    public static string FileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var p = path.Replace('\\', '/').TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i >= 0 ? p[(i + 1)..] : p;
    }
}
