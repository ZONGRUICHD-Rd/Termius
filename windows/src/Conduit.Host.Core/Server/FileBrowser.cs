using System.Text.Json.Nodes;

namespace Conduit.Host.Core.Server;

/// <summary>Directory-only browsing so the phone can pick a project folder. Never returns file contents.</summary>
public static class FileBrowser
{
    public static JsonObject List(string? path, IEnumerable<string> recents)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var target = string.IsNullOrWhiteSpace(path) ? home : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException($"Folder not found: {target}");

        var dirs = new JsonArray();
        try
        {
            foreach (var d in new DirectoryInfo(target).EnumerateDirectories()
                         .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !d.Name.StartsWith('.'))
                         .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                         .Take(500))
            {
                dirs.Add(new JsonObject
                {
                    ["name"] = d.Name,
                    ["path"] = d.FullName,
                    ["isRepo"] = Directory.Exists(Path.Combine(d.FullName, ".git")),
                });
            }
        }
        catch (UnauthorizedAccessException) { }

        var roots = new JsonArray();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Network or DriveType.Removable))
            roots.Add(drive.RootDirectory.FullName);

        return new JsonObject
        {
            ["path"] = target,
            ["parent"] = Directory.GetParent(target)?.FullName,
            ["home"] = home,
            ["isRepo"] = Directory.Exists(Path.Combine(target, ".git")),
            ["dirs"] = dirs,
            ["roots"] = roots,
            ["recents"] = new JsonArray(recents.Where(Directory.Exists).Take(12).Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        };
    }
}
