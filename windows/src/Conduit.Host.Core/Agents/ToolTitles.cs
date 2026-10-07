using System.Text.Json.Nodes;
using Conduit.Host.Core.Protocol;

namespace Conduit.Host.Core.Agents;

/// <summary>Human friendly one-liners for tool calls ("Edit App.swift", "Run npm test").</summary>
public static class ToolTitles
{
    public static string For(string toolName, JsonNode? input)
    {
        var o = input as JsonObject;
        string? S(string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var name = toolName.ToLowerInvariant();

        if (name is "bash" or "shell" or "exec" or "command" or "local_shell" or "powershell")
        {
            var cmd = S("command") ?? S("cmd") ?? (o?["command"] is JsonArray arr ? string.Join(' ', arr.Select(x => x?.ToString())) : null);
            return cmd is null ? "Run command" : "Run " + Text.FirstLine(cmd, 80);
        }
        if (name is "edit" or "multiedit" or "str_replace_editor" or "apply_patch" or "patch")
            return "Edit " + Text.FileName(S("file_path") ?? S("filePath") ?? S("path"));
        if (name is "write" or "create")
            return "Write " + Text.FileName(S("file_path") ?? S("filePath") ?? S("path"));
        if (name is "read" or "view" or "cat")
            return "Read " + Text.FileName(S("file_path") ?? S("filePath") ?? S("path"));
        if (name is "grep" or "search" or "rg")
            return "Search " + Text.Clip(S("pattern") ?? S("query") ?? "", 60);
        if (name is "glob" or "find" or "ls" or "list")
            return "List " + Text.Clip(S("pattern") ?? S("path") ?? "", 60);
        if (name is "webfetch" or "fetch")
            return "Fetch " + Text.Clip(S("url") ?? "", 60);
        if (name is "websearch" or "web_search")
            return "Web search " + Text.Clip(S("query") ?? "", 60);
        if (name is "todowrite" or "todo" or "todoread")
            return "Update plan";
        if (name is "task" or "agent")
            return "Sub-agent: " + Text.Clip(S("description") ?? S("prompt") ?? "", 60);

        var hint = S("file_path") ?? S("path") ?? S("command") ?? S("query") ?? S("url");
        return hint is null ? toolName : $"{toolName} {Text.Clip(Text.FirstLine(hint), 60)}";
    }

    public static string InputPreview(JsonNode? input)
    {
        if (input is null) return "";
        if (input is JsonObject o)
        {
            if (o["command"] is JsonValue c) return c.ToString();
            if (o["old_string"] is not null || o["new_string"] is not null)
                return $"- {o["old_string"]}\n+ {o["new_string"]}";
            if (o["content"] is JsonValue content) return content.ToString();
        }
        return input.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Flattens tool results that may be a string, an array of content blocks or an object.</summary>
    public static string ResultText(JsonNode? node)
    {
        switch (node)
        {
            case null: return "";
            case JsonValue v: return v.ToString();
            case JsonArray a:
                return string.Join("\n", a.Select(x => x is JsonObject b && b["text"] is JsonNode t ? t.ToString() : ResultText(x)));
            case JsonObject o:
                if (o["content"] is JsonNode c) return ResultText(c);
                if (o["text"] is JsonNode t2) return t2.ToString();
                if (o["output"] is JsonNode op) return ResultText(op);
                return o.ToJsonString();
            default: return node.ToJsonString();
        }
    }
}
