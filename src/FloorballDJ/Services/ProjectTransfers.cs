using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FloorballDJ.Models;

namespace FloorballDJ.Services;

public sealed record TransferVersion(string ManifestPath, PortableBackupManifest Manifest)
{
    public string DisplayName => $"{Manifest.ProjectName} · {Manifest.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Manifest.ComputerName}";
}

public sealed partial class ProjectService
{
    private sealed class TransferProgress(IProgress<PortableBackupProgress>? target, double start, double range) : IProgress<PortableBackupProgress>
    {
        public void Report(PortableBackupProgress value) => target?.Report(value with { Percent = start + Math.Clamp(value.Percent, 0, 100) * range / 100 });
    }
    private static async Task<string> HashAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
    }

    private static async Task<Dictionary<string, string>> HashFilesAsync(string root, IEnumerable<string> files)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) result.Add(Path.GetRelativePath(root, file), await HashAsync(file));
        return result;
    }

    private static async Task VerifyBackupAsync(string root, PortableBackupManifest manifest, IProgress<PortableBackupProgress>? progress)
    {
        if (manifest.FormatVersion > 3) throw new InvalidDataException("Backupen kräver en nyare version av FloorballDJ.");
        if (manifest.Files.Count == 0 && manifest.FormatVersion >= 3) throw new InvalidDataException("Backupens filförteckning saknas.");
        if (manifest.Files.Count > 0 && !manifest.Files.ContainsKey(manifest.ProfileFile)) throw new InvalidDataException("Profilen saknas i backupens filförteckning.");
        var index = 0;
        foreach (var pair in manifest.Files)
        {
            var path = SafeChildPath(root, pair.Key);
            if (!File.Exists(path) || !string.Equals(await HashAsync(path), pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Backupen är inte komplett eller filen har ändrats: {pair.Key}. Låt Google Drive synka färdigt eller kopiera filerna igen.");
            progress?.Report(new PortableBackupProgress("Kontrollerar backupens filer…", 2 + ++index * 6d / manifest.Files.Count, index, manifest.Files.Count, Path.GetFileName(path)));
        }
    }

    private static async Task CopyManifestFilesAsync(string source, string destination, PortableBackupManifest manifest, IProgress<PortableBackupProgress>? progress)
    {
        var count = 0;
        foreach (var file in manifest.Files.Keys)
        {
            var target = SafeChildPath(destination, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = File.OpenRead(SafeChildPath(source, file)))
            await using (var output = File.Create(target)) await input.CopyToAsync(output);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(SafeChildPath(source, file)));
            progress?.Report(new PortableBackupProgress("Kopierar backupfiler…", 8 + ++count * 72d / manifest.Files.Count, count, manifest.Files.Count, Path.GetFileName(file)));
        }
        await VerifyBackupAsync(destination, manifest, null);
    }

    private static async Task<List<string>> FindMissingBackupMediaAsync(FloorballProject project)
    {
        var missing = project.Decks.SelectMany(deck => deck.Jingles).Concat(project.Settings.AutoplayQueueItems ?? [])
            .Where(item => item.IsMissing).Select(item => item.FilePath).ToList();
        var playlists = project.Settings.AutoplayProfiles.Select(item => item.PlaylistPath)
            .Append(project.Settings.AutoplayLastPlaylistPath).Append(project.Settings.AutoplayDefaultPlaylistPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in playlists)
        {
            if (!File.Exists(playlist)) { missing.Add(playlist!); continue; }
            var document = PlaylistService.ReadDocument(await File.ReadAllTextAsync(playlist));
            missing.AddRange((document.Entries ?? []).Select(item => PlaylistService.ResolveMediaPath(item.FilePath, document.Version, playlist!)).Where(path => !File.Exists(path)));
        }
        return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<string> CreateTransferPackageAsync(FloorballProject project, string target, string profileName, IProgress<PortableBackupProgress>? progress = null)
    {
        var owned = CloneForTransfer(project);
        var staging = NewTransferStaging();
        var temporary = Path.GetFullPath(target) + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            var backup = await CreateMediaBackupAsync(owned, staging, new TransferProgress(progress, 0, 85), profileName);
            RequireComplete(backup);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            progress?.Report(new PortableBackupProgress("Packar flyttpaketet…", 90));
            await Task.Run(() => ZipFile.CreateFromDirectory(backup.Directory, temporary, CompressionLevel.NoCompression, false));
            File.Move(temporary, target, true);
            progress?.Report(new PortableBackupProgress("Flyttpaketet är klart", 100));
            return target;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            DeleteTransferStaging(staging);
        }
    }

    public async Task<PortableRestoreResult> RestoreTransferPackageAsync(string package, IProgress<PortableBackupProgress>? progress = null)
    {
        var staging = NewTransferStaging();
        Directory.CreateDirectory(staging);
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(package, staging));
            return await RestorePortableBackupAsync(staging, progress);
        }
        finally { DeleteTransferStaging(staging); }
    }

    private static FloorballProject CloneForTransfer(FloorballProject project)
        => JsonSerializer.Deserialize<FloorballProject>(JsonSerializer.Serialize(project, JsonOptions), JsonOptions)!;
    private string NewTransferStaging() => Path.Combine(AppDataDirectory, "Överföringar", Guid.NewGuid().ToString("N"));
    private static void RequireComplete(PortableBackupResult backup)
    {
        if (backup.MissingFiles.Count > 0) throw new InvalidDataException("Överföringen är ofullständig. Saknade filer:\n" + string.Join("\n", backup.MissingFiles));
    }

    private void DeleteTransferStaging(string path)
    {
        var root = Path.GetFullPath(Path.Combine(AppDataDirectory, "Överföringar")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Ogiltig temporär överföringsmapp.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }

    public IReadOnlyList<TransferVersion> GetTransferVersions(string root)
    {
        var directory = Path.Combine(root, "Versioner");
        if (!Directory.Exists(directory)) return [];
        var result = new List<TransferVersion>();
        foreach (var path in Directory.EnumerateFiles(directory, "floorballdj-backup.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<PortableBackupManifest>(File.ReadAllText(path), JsonOptions);
                if (manifest is not null) result.Add(new(path, manifest));
            }
            catch (IOException) { }
            catch (JsonException) { }
        }
        return result.OrderByDescending(version => version.Manifest.CreatedAt).ToArray();
    }

    public Task<PortableRestoreResult> RestoreTransferVersionAsync(string root, TransferVersion version, IProgress<PortableBackupProgress>? progress = null)
        => RestorePortableBackupAsync(root, progress, version.ManifestPath);

    public async Task<TransferVersion> SaveToSyncFolderAsync(FloorballProject project, string root, string profileName, IProgress<PortableBackupProgress>? progress = null)
    {
        var owned = CloneForTransfer(project);
        var staging = NewTransferStaging();
        root = Path.GetFullPath(root);
        try
        {
            var backup = await CreateMediaBackupAsync(owned, staging, new TransferProgress(progress, 0, 65), profileName);
            RequireComplete(backup);
            var copy = await LoadAsync(backup.ProfilePath);
            var versionRelative = Path.Combine("Versioner", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{SanitizePathSegment(Environment.MachineName, "Dator")}-{Guid.NewGuid():N}");
            var versionDirectory = SafeChildPath(root, versionRelative);
            Directory.CreateDirectory(versionDirectory);
            var profileRelative = Path.Combine(versionRelative, Path.GetFileName(backup.ProfilePath));
            var profileTarget = SafeChildPath(root, profileRelative);
            var manifest = JsonSerializer.Deserialize<PortableBackupManifest>(await File.ReadAllTextAsync(Path.Combine(backup.Directory, "floorballdj-backup.json")), JsonOptions)!;
            manifest.ProfileFile = profileRelative;
            manifest.Files = new(StringComparer.OrdinalIgnoreCase);
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var mediaFiles = Directory.GetFiles(Path.Combine(backup.Directory, "Media"), "*", SearchOption.AllDirectories);
            var completedMedia = 0;
            foreach (var media in mediaFiles)
            {
                var hash = await HashAsync(media);
                var relative = Path.Combine("Media", hash + Path.GetExtension(media).ToLowerInvariant());
                var target = SafeChildPath(root, relative);
                await CopyTransferFileAsync(media, target, hash);
                mapping.Add(Path.GetFullPath(media), target);
                manifest.Files[relative] = hash;
                progress?.Report(new PortableBackupProgress("Kopierar till synkmappen…", 65 + ++completedMedia * 25d / Math.Max(1, mediaFiles.Length), completedMedia, mediaFiles.Length, Path.GetFileName(media)));
            }
            foreach (var item in copy.Decks.SelectMany(deck => deck.Jingles).Concat(copy.Settings.AutoplayQueueItems ?? []))
                if (item.HasAudio) item.FilePath = mapping[item.FilePath];
            var playlistMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            async Task<string?> MovePlaylist(string? source)
            {
                if (string.IsNullOrWhiteSpace(source)) return source;
                if (playlistMapping.TryGetValue(source, out var previous)) return previous;
                var target = Path.Combine(versionDirectory, "Spellistor", Path.GetFileName(source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var document = PlaylistService.ReadDocument(await File.ReadAllTextAsync(source));
                var entries = (document.Entries ?? []).Select(entry => entry with
                {
                    FilePath = Path.GetRelativePath(Path.GetDirectoryName(target)!, mapping[PlaylistService.ResolveMediaPath(entry.FilePath, document.Version, source)])
                }).ToList();
                await PlaylistService.Shared.SaveAsync(target, document with { Version = 3, Entries = entries });
                manifest.Files[Path.GetRelativePath(root, target)] = await HashAsync(target);
                playlistMapping.Add(source, target);
                return target;
            }
            foreach (var playlist in copy.Settings.AutoplayProfiles) playlist.PlaylistPath = await MovePlaylist(playlist.PlaylistPath);
            copy.Settings.AutoplayLastPlaylistPath = await MovePlaylist(copy.Settings.AutoplayLastPlaylistPath);
            copy.Settings.AutoplayDefaultPlaylistPath = await MovePlaylist(copy.Settings.AutoplayDefaultPlaylistPath);
            copy.Settings.MusicFolderPath = Path.Combine(root, "Media");
            await SaveAsync(copy, profileTarget);
            manifest.Files[profileRelative] = await HashAsync(profileTarget);
            var settings = Path.Combine(backup.Directory, "Inställningar");
            foreach (var source in Directory.EnumerateFiles(settings, "*", SearchOption.AllDirectories))
            {
                var relative = Path.Combine(versionRelative, "Inställningar", Path.GetRelativePath(settings, source));
                var hash = await HashAsync(source);
                await CopyTransferFileAsync(source, SafeChildPath(root, relative), hash);
                manifest.Files[relative] = hash;
            }
            await VerifyBackupAsync(root, manifest, new TransferProgress(progress, 92, 7));
            var manifestPath = Path.Combine(versionDirectory, "floorballdj-backup.json");
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            await File.WriteAllTextAsync(manifestPath, json);
            var latestTemporary = Path.Combine(root, $".latest-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(latestTemporary, json);
            File.Move(latestTemporary, Path.Combine(root, "floorballdj-backup.json"), true);
            progress?.Report(new PortableBackupProgress("Versionen är skapad – låt Google Drive synka färdigt", 100));
            return new(manifestPath, manifest);
        }
        finally { DeleteTransferStaging(staging); }
    }

    private static async Task CopyTransferFileAsync(string source, string target, string hash)
    {
        if (File.Exists(target) && await HashAsync(target) == hash) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(temporary)) await input.CopyToAsync(output);
            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
