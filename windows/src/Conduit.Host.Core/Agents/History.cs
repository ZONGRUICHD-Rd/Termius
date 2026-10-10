using System.Text.Json;
using System.Text.Json.Nodes;
using Conduit.Host.Core.Protocol;

namespace Conduit.Host.Core.Agents;

/// <summary>
/// Conversations that were started on this PC (Claude Desktop / Claude Code, Codex app / CLI), read from the
/// agents' own transcript files so the phone can list them and continue them — "remote control" for
/// existing work. Claude: ~/.claude/projects/*/&lt;session&gt;.jsonl, Codex: ~/.codex/sessions/**/rollout-*.jsonl.
/// </summary>
public static class History
{
    public sealed record Item(string Agent, string Id, string Title, string Cwd, DateTimeOffset UpdatedAt, string Path);

    /// <summary>Tests point this at a fake home directory.</summary>
    internal static string? HomeOverride { get; set; }

    private static string Home => HomeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static bool Supports(string agent) => agent is AgentCatalog.Claude or AgentCatalog.Codex;

    public static List<Item> List(string agent, int max = 60)
    {
        var files = agent switch
        {
            AgentCatalog.Claude => Files(Path.Combine(Home, ".claude", "projects"), "*.jsonl"),
            AgentCatalog.Codex => Files(Path.Combine(Home, ".codex", "sessions"), "rollout-*.jsonl"),
            _ => [],
        };
        var items = new List<Item>();
        foreach (var f in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(max * 2))
        {
            var item = agent == AgentCatalog.Claude ? ReadClaudeHeader(f) : ReadCodexHeader(f);
            if (item is not null) items.Add(item);
            if (items.Count >= max) break;
        }
        return items;
    }

    private static IEnumerable<FileInfo> Files(string root, string pattern)
    {
        if (!Directory.Exists(root)) return [];
        try { return new DirectoryInfo(root).EnumerateFiles(pattern, SearchOption.AllDirectories).Where(f => f.Length > 0).ToList(); }
        catch (Exception) { return []; }
    }

    private static IEnumerable<JsonObject> Lines(string path, int max = int.MaxValue)
    {
        var n = 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line && n++ < max)
        {
            JsonObject? o = null;
            try { o = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { }
            if (o is not null) yield return o;
        }
    }

    private static string FlattenText(JsonNode? content) => content switch
    {
        JsonValue v => v.ToString(),
        JsonArray a => string.Join("\n", a.OfType<JsonObject>()
            .Where(b => (string?)b["type"] is "text" or "input_text" or "output_text")
            .Select(b => (string?)b["text"] ?? "")),
        _ => "",
    };

    private static bool IsNoise(string text) =>
        text.Length == 0 || text.StartsWith('<') || text.StartsWith("Caveat:", StringComparison.Ordinal);

    // ---------------- Claude ----------------

    private static Item? ReadClaudeHeader(FileInfo f)
    {
        string? title = null, summary = null, cwd = null;
        foreach (var o in Lines(f.FullName, 400))
        {
            cwd ??= (string?)o["cwd"];
            if ((string?)o["type"] == "summary") summary ??= (string?)o["summary"];
            if (title is null && (string?)o["type"] == "user" && o["isSidechain"]?.GetValue<bool>() != true && o["isMeta"]?.GetValue<bool>() != true)
            {
                var t = FlattenText(o["message"]?["content"]).Trim();
                if (!IsNoise(t)) title = t;
            }
            if (title is not null && cwd is not null && summary is not null) break;
        }
        if (title is null && summary is null) return null;
        return new Item(AgentCatalog.Claude, Path.GetFileNameWithoutExtension(f.Name), Text.FirstLine(summary ?? title!, 80),
            cwd ?? "", f.LastWriteTimeUtc, f.FullName);
    }

    // ---------------- Codex ----------------

    private static Item? ReadCodexHeader(FileInfo f)
    {
        string? id = null, cwd = null, title = null;
        foreach (var o in Lines(f.FullName, 200))
        {
            var p = o["payload"] as JsonObject;
            switch ((string?)o["type"])
            {
                case "session_meta":
                    id ??= (string?)p?["id"];
                    cwd ??= (string?)p?["cwd"];
                    break;
                case "event_msg" when ((string?)p?["type"]) == "user_message":
                    var m = ((string?)p?["message"])?.Trim() ?? "";
                    if (title is null && !IsNoise(m)) title = m;
                    break;
                case "response_item" when ((string?)p?["type"]) == "message" && ((string?)p?["role"]) == "user":
                    var t = FlattenText(p?["content"]).Trim();
                    if (title is null && !IsNoise(t)) title = t;
                    break;
            }
            if (id is not null && title is not null) break;
        }
        if (id is null || title is null) return null;
        return new Item(AgentCatalog.Codex, id, Text.FirstLine(title, 80), cwd ?? "", f.LastWriteTimeUtc, f.FullName);
    }

    // ---------------- transcript import ----------------

    public static Item? Find(string agent, string id) =>
        List(agent, 500).FirstOrDefault(i => i.Id == id);

    /// <summary>Converts the agent's transcript into Conduit events (last ~400) so the phone shows the history.</summary>
    public static List<JsonObject> LoadEvents(Item item)
    {
        var events = new List<JsonObject>();
        if (item.Agent == AgentCatalog.Claude)
        {
            var toolNames = new Dictionary<string, string>();
            foreach (var o in Lines(item.Path))
            {
                if (o["isSidechain"]?.GetValue<bool>() == true || o["isMeta"]?.GetValue<bool>() == true) continue;
                var msg = o["message"] as JsonObject;
                switch ((string?)o["type"])
                {
                    case "user":
                        if (msg?["content"] is JsonArray blocks && blocks.OfType<JsonObject>().Any(b => (string?)b["type"] == "tool_result"))
                        {
                            foreach (var b in blocks.OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_result"))
                            {
                                var tid = (string?)b["tool_use_id"] ?? "";
                                var ev = Ev.Tool(tid, toolNames.GetValueOrDefault(tid, "tool"), "", (bool?)b["is_error"] == true ? "error" : "ok", null, ToolTitles.ResultText(b["content"]));
                                ev.Remove("title");
                                events.Add(ev);
                            }
                        }
                        else
                        {
                            var t = FlattenText(msg?["content"]).Trim();
                            if (!IsNoise(t)) events.Add(Ev.User(t));
                        }
                        break;
                    case "assistant":
                        var mid = (string?)msg?["id"] ?? Guid.NewGuid().ToString("n");
                        foreach (var b in (msg?["content"] as JsonArray ?? []).OfType<JsonObject>())
                        {
                            switch ((string?)b["type"])
                            {
                                case "text":
                                    events.Add(Ev.Message(mid + ":" + events.Count, (string?)b["text"] ?? ""));
                                    break;
                                case "tool_use" when (string?)b["name"] != "TodoWrite":
                                    var tid = (string?)b["id"] ?? "";
                                    var name = (string?)b["name"] ?? "tool";
                                    toolNames[tid] = name;
                                    events.Add(Ev.Tool(tid, name, ToolTitles.For(name, b["input"]), "ok", ToolTitles.InputPreview(b["input"])));
                                    break;
                            }
                        }
                        break;
                }
            }
        }
        else if (item.Agent == AgentCatalog.Codex)
        {
            foreach (var o in Lines(item.Path))
            {
                var p = o["payload"] as JsonObject;
                if ((string?)o["type"] != "response_item" || p is null) continue;
                switch ((string?)p["type"])
                {
                    case "message":
                        var text = FlattenText(p["content"]).Trim();
                        if (IsNoise(text)) break;
                        events.Add((string?)p["role"] == "user" ? Ev.User(text) : Ev.Message("cx" + events.Count, text));
                        break;
                    case "function_call":
                    case "local_shell_call":
                        var cid = (string?)p["call_id"] ?? ("c" + events.Count);
                        JsonNode? args = null;
                        try { args = (string?)p["arguments"] is { } a ? JsonNode.Parse(a) : p["action"]; } catch (JsonException) { }
                        var nm = (string?)p["name"] ?? "shell";
                        events.Add(Ev.Tool(cid, nm, ToolTitles.For(nm, args), "ok", ToolTitles.InputPreview(args)));
                        break;
                    case "function_call_output":
                        var oid = (string?)p["call_id"] ?? "";
                        var outEv = Ev.Tool(oid, "shell", "", "ok", null, ToolTitles.ResultText(p["output"]));
                        outEv.Remove("title");
                        events.Add(outEv);
                        break;
                }
            }
        }
        return events.Count > 400 ? events[^400..] : events;
    }
}
