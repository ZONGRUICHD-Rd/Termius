using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Conduit.Host.Core.Agents;

namespace Conduit.Host.Core.Git;

/// <summary>
/// The "review and ship" side of remote control: what the agent changed (status + diffs), and commit /
/// push / branch / pull request — like Codex and Claude Code on mobile. Shells out to git and gh.
/// </summary>
public static class GitService
{
    private const int MaxDiffBytes = 512 * 1024;

    public sealed record Result(int ExitCode, string Stdout, string Stderr)
    {
        public bool Ok => ExitCode == 0;
        public string Message => (Stderr.Trim().Length > 0 ? Stderr : Stdout).Trim();
    }

    public static async Task<Result> RunAsync(string tool, string cwd, IEnumerable<string> args, CancellationToken ct, int timeoutSec = 60)
    {
        var exe = AgentCatalog.FindOnPath(tool)
                  ?? (OperatingSystem.IsWindows() && tool == "git" && File.Exists(@"C:\Program Files\Git\cmd\git.exe") ? @"C:\Program Files\Git\cmd\git.exe" : null)
                  ?? throw new InvalidOperationException(tool == "gh" ? "这台电脑没有安装 GitHub CLI（gh），无法创建 PR。" : "这台电脑没有安装 Git。");
        var psi = JsonlProcess.CreateStartInfo(exe, args, cwd);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (Exception) { }
            throw new TimeoutException($"{tool} 超时");
        }
        return new Result(p.ExitCode, await stdout, await stderr);
    }

    private static Task<Result> Git(string cwd, CancellationToken ct, params string[] args) => RunAsync("git", cwd, args, ct);

    public static async Task<JsonObject> StatusAsync(string cwd, CancellationToken ct)
    {
        var inside = await Git(cwd, ct, "rev-parse", "--is-inside-work-tree");
        if (!inside.Ok) return new JsonObject { ["isRepo"] = false };

        var status = await Git(cwd, ct, "status", "--porcelain=v1", "-b", "-uall");
        var numstat = await Git(cwd, ct, "diff", "HEAD", "--numstat");
        var stats = new Dictionary<string, (int Add, int Del)>();
        foreach (var line in numstat.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            int.TryParse(parts[0], out var a);
            int.TryParse(parts[1], out var d);
            stats[parts[2].Trim()] = (a, d);
        }

        string branch = "", upstream = "";
        int ahead = 0, behind = 0;
        var files = new JsonArray();
        int totalAdd = 0, totalDel = 0;
        foreach (var raw in status.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith("## "))
            {
                // ## main...origin/main [ahead 1, behind 2]
                var head = raw[3..];
                var bracket = head.IndexOf(" [", StringComparison.Ordinal);
                var info = bracket >= 0 ? head[(bracket + 2)..].TrimEnd(']') : "";
                var names = bracket >= 0 ? head[..bracket] : head;
                var dots = names.IndexOf("...", StringComparison.Ordinal);
                branch = dots >= 0 ? names[..dots] : names;
                upstream = dots >= 0 ? names[(dots + 3)..] : "";
                foreach (var part in info.Split(", ", StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.StartsWith("ahead ")) int.TryParse(part[6..], out ahead);
                    if (part.StartsWith("behind ")) int.TryParse(part[7..], out behind);
                }
                continue;
            }
            if (raw.Length < 4) continue;
            var code = raw[..2];
            var path = raw[3..].Trim().Trim('"');
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path[(arrow + 4)..];
            var kind = code switch
            {
                "??" => "added",
                _ when code.Contains('D') => "deleted",
                _ when code.Contains('A') => "added",
                _ when code.Contains('R') => "renamed",
                _ => "modified",
            };
            var (add, del) = stats.TryGetValue(path, out var st) ? st : (code == "??" ? (CountLines(Path.Combine(cwd, path)), 0) : (0, 0));
            totalAdd += add;
            totalDel += del;
            files.Add(new JsonObject { ["path"] = path, ["status"] = kind, ["added"] = add, ["deleted"] = del, ["staged"] = code[0] != ' ' && code[0] != '?' });
        }
        return new JsonObject
        {
            ["isRepo"] = true,
            ["branch"] = branch == "HEAD (no branch)" ? "HEAD" : branch,
            ["upstream"] = upstream,
            ["ahead"] = ahead,
            ["behind"] = behind,
            ["files"] = files,
            ["added"] = totalAdd,
            ["deleted"] = totalDel,
            ["hasGh"] = AgentCatalog.FindOnPath("gh") is not null,
        };
    }

    private static int CountLines(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 2_000_000) return 0;
            return File.ReadLines(file).Count();
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Unified diff against HEAD for one file (or everything), including untracked files.</summary>
    public static async Task<JsonObject> DiffAsync(string cwd, string? path, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var args = new List<string> { "diff", "HEAD", "--no-color", "--no-ext-diff" };
        if (path is not null) { args.Add("--"); args.Add(path); }
        var tracked = await RunAsync("git", cwd, args, ct);
        sb.Append(tracked.Stdout);

        // Untracked files don't show in `git diff HEAD`.
        var untracked = await Git(cwd, ct, "ls-files", "--others", "--exclude-standard");
        foreach (var f in untracked.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
        {
            if (path is not null && f != path) continue;
            if (sb.Length > MaxDiffBytes) break;
            var nd = await Git(cwd, ct, "diff", "--no-color", "--no-index", "--", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", f);
            sb.Append(nd.Stdout);
        }
        var text = sb.ToString();
        var truncated = text.Length > MaxDiffBytes;
        return new JsonObject { ["diff"] = truncated ? text[..MaxDiffBytes] : text, ["truncated"] = truncated };
    }

    public static async Task<JsonObject> CommitAsync(string cwd, string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("请填写提交说明");
        var add = await Git(cwd, ct, "add", "-A");
        if (!add.Ok) throw new InvalidOperationException(add.Message);
        var commit = await Git(cwd, ct, "commit", "-m", message.Trim());
        if (!commit.Ok) throw new InvalidOperationException(commit.Message);
        var sha = await Git(cwd, ct, "rev-parse", "--short", "HEAD");
        return new JsonObject { ["sha"] = sha.Stdout.Trim(), ["output"] = commit.Stdout.Trim() };
    }

    public static async Task<JsonObject> PushAsync(string cwd, CancellationToken ct)
    {
        var upstream = await Git(cwd, ct, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        var push = upstream.Ok
            ? await RunAsync("git", cwd, ["push"], ct, 180)
            : await RunAsync("git", cwd, ["push", "-u", "origin", "HEAD"], ct, 180);
        if (!push.Ok) throw new InvalidOperationException(push.Message);
        return new JsonObject { ["output"] = push.Message };
    }

    public static async Task<JsonObject> CreateBranchAsync(string cwd, string name, CancellationToken ct)
    {
        var r = await Git(cwd, ct, "checkout", "-b", name.Trim());
        if (!r.Ok) throw new InvalidOperationException(r.Message);
        return new JsonObject { ["branch"] = name.Trim() };
    }

    public static async Task<JsonObject> CreatePullRequestAsync(string cwd, string title, string body, bool draft, CancellationToken ct)
    {
        await PushAsync(cwd, ct);
        var args = new List<string> { "pr", "create", "--title", title, "--body", string.IsNullOrWhiteSpace(body) ? "Created with Conduit" : body };
        if (draft) args.Add("--draft");
        var r = await RunAsync("gh", cwd, args, ct, 120);
        if (!r.Ok) throw new InvalidOperationException(r.Message);
        var url = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.StartsWith("http"))?.Trim() ?? r.Stdout.Trim();
        return new JsonObject { ["url"] = url };
    }

    public static async Task<JsonObject> DiscardFileAsync(string cwd, string path, CancellationToken ct)
    {
        var tracked = await Git(cwd, ct, "ls-files", "--error-unmatch", "--", path);
        if (tracked.Ok)
        {
            var r = await Git(cwd, ct, "checkout", "HEAD", "--", path);
            if (!r.Ok) throw new InvalidOperationException(r.Message);
        }
        else
        {
            var full = Path.GetFullPath(Path.Combine(cwd, path));
            if (!full.StartsWith(Path.GetFullPath(cwd), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("路径无效");
            if (File.Exists(full)) File.Delete(full);
        }
        return new JsonObject();
    }

    /// <summary>Totals for session cards ("+12 −3 · 4 个文件").</summary>
    public static async Task<(int Added, int Deleted, int Files, string Branch)?> QuickStatsAsync(string cwd, CancellationToken ct)
    {
        try
        {
            var s = await StatusAsync(cwd, ct);
            if ((bool?)s["isRepo"] != true) return null;
            return ((int?)s["added"] ?? 0, (int?)s["deleted"] ?? 0, (s["files"] as JsonArray)?.Count ?? 0, (string?)s["branch"] ?? "");
        }
        catch (Exception)
        {
            return null;
        }
    }
}
