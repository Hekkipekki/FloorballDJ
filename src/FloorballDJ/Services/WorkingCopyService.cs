using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FloorballDJ.Services;

public sealed record WorkingCopyInfo(string ProfilePath, string? SourcePath, string ProfileName, DateTimeOffset UpdatedAt);

/// <summary>Recovery files are local and never become a named profile or a recent-profile entry.</summary>
public sealed class WorkingCopyService
{
    private readonly string _root;
    private readonly string _revisionsRoot;
    public WorkingCopyService(string appDataDirectory)
    {
        _root = Path.Combine(appDataDirectory, "Arbetskopior");
        _revisionsRoot = Path.Combine(appDataDirectory, "Revisions");
    }

    public string GetPath(string? sourcePath, Guid profileId)
    {
        var key = sourcePath is null ? "new-" + profileId.ToString("N")
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath).ToUpperInvariant())));
        return Path.Combine(_root, key, "arbetskopia.floorballdj.json");
    }

    public void Prepare(string path, string? sourcePath, string profileName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var info = new WorkingCopyInfo(path, sourcePath, profileName, DateTimeOffset.Now);
        var metadata = Path.Combine(Path.GetDirectoryName(path)!, "recovery.json");
        var temporary = metadata + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(info));
        File.Move(temporary, metadata, true);
    }

    public IReadOnlyList<WorkingCopyInfo> Find()
    {
        if (!Directory.Exists(_root)) return [];
        var copies = new List<WorkingCopyInfo>();
        foreach (var metadata in Directory.EnumerateFiles(_root, "recovery.json", SearchOption.AllDirectories))
        {
            try
            {
                var info = JsonSerializer.Deserialize<WorkingCopyInfo>(File.ReadAllText(metadata));
                var path = Path.Combine(Path.GetDirectoryName(metadata)!, "arbetskopia.floorballdj.json");
                if (info is not null && File.Exists(path)) copies.Add(info with { ProfilePath = path, UpdatedAt = File.GetLastWriteTimeUtc(path) });
            }
            catch (IOException) { }
            catch (JsonException) { }
        }
        return copies.OrderByDescending(copy => copy.UpdatedAt).ToArray();
    }

    public void Delete(string path)
    {
        var directory = Path.GetFullPath(Path.GetDirectoryName(path)!);
        var root = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Ogiltig arbetskopia.");
        // No recursive deletion: only this service's two files are removed.
        if (File.Exists(path)) File.Delete(path);
        var metadata = Path.Combine(directory, "recovery.json");
        if (File.Exists(metadata)) File.Delete(metadata);
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..12];
        var revisions = Path.GetFullPath(Path.Combine(_revisionsRoot, hash));
        var revisionsRoot = Path.GetFullPath(_revisionsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!revisions.StartsWith(revisionsRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Ogiltig arbetskopiehistorik.");
        if (Directory.Exists(revisions)) Directory.Delete(revisions, true);
    }
}
