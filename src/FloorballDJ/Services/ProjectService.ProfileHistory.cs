using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloorballDJ.Models;

namespace FloorballDJ.Services;

public enum ProfileRevisionKind { Autosave, ManualSave, Original, Discarded, BeforeRestore, Legacy, LegacySaved }
public sealed record ProfileBackupTarget(string IdentityPath, string ProfileName, string? OriginalPath, string WorkingCopyPath);

public sealed partial class ProjectService
{
    public const int AutomaticRevisionLimit = 100;

    public string ProfileBackupDirectory(ProfileBackupTarget target) => Path.Combine(AppDataDirectory, "Profilbackuper", HistoryKey(target.IdentityPath));
    public string ProfileBackupPath(ProfileBackupTarget target) => Path.Combine(ProfileBackupDirectory(target), SafeProfileName(target.ProfileName) + " backup.floorballdj.json");
    private static string HistoryKey(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..24];
    private static bool Protected(ProfileRevisionKind kind) => kind is not (ProfileRevisionKind.Autosave or ProfileRevisionKind.Legacy);
    private static string SafeProfileName(string name)
    {
        var safe = string.Concat((name ?? "Profil").Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? "Profil" : safe[..Math.Min(100, safe.Length)];
    }

    // History operations share the existing ordered save worker and process gate.
    // A history query cannot overtake an autosave or race a manual replacement.
    private Task<T> HistoryWorkAsync<T>(Func<T> action)
    {
        lock (SaveOrderGate)
        {
            var predecessor = _lastSave;
            var task = Task.Run(async () =>
            {
                try { await predecessor.ConfigureAwait(false); } catch { }
                await SaveGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var gate = new Semaphore(1, 1, @"Local\FloorballDJ.ProjectSave");
                    if (!gate.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("Backuphistoriken är upptagen. Försök igen.");
                    try { return action(); } finally { gate.Release(); }
                }
                finally { SaveGate.Release(); }
            });
            _lastSave = task;
            return task;
        }
    }

    public Task EnsureProfileBackupAsync(ProfileBackupTarget target) => HistoryWorkAsync(() => { EnsureProfileHistory(target, verifyOriginal: true); return true; });
    public Task<IReadOnlyList<ProjectRevision>> GetProfileHistoryAsync(ProfileBackupTarget target) => HistoryWorkAsync<IReadOnlyList<ProjectRevision>>(() =>
    {
        EnsureProfileHistory(target, verifyOriginal: true);
        return ReadHistory(target).Select(item => new ProjectRevision(item.Path, item.Metadata.Timestamp.LocalDateTime,
            item.Metadata.ProfileName, item.Metadata.DeckCount, item.Metadata.JingleCount, new FileInfo(item.Path).Length, item.Metadata.Summary)
            { Kind = item.Metadata.Kind, ChangeLog = item.Metadata.ChangeLog }).OrderByDescending(item => item.Timestamp).ToArray();
    });

    public Task PreserveRecoveryAsync(WorkingCopyInfo copy) => HistoryWorkAsync(() =>
    {
        var target = new ProfileBackupTarget(copy.SourcePath ?? copy.ProfilePath, copy.SourcePath is null ? "Osparad profil" : copy.ProfileName, copy.SourcePath, copy.ProfilePath);
        EnsureProfileHistory(target, verifyOriginal: true);
        if (File.Exists(copy.ProfilePath)) RecordSnapshot(target, NormalizeHistorySnapshot(File.ReadAllBytes(copy.ProfilePath), Path.GetDirectoryName(copy.ProfilePath)!),
            ProfileRevisionKind.Discarded, DateTimeOffset.UtcNow);
        return true;
    });

    private void PreserveProfileSnapshot(ProfileBackupTarget target, byte[] bytes, string mediaDirectory,
        ProfileRevisionKind kind, ProfileBackupTarget? branchFrom)
    {
        // Save As forks a new profile's history. Later writes remain independent.
        if (kind == ProfileRevisionKind.ManualSave && branchFrom is not null &&
            !string.Equals(HistoryKey(target.IdentityPath), HistoryKey(branchFrom.IdentityPath), StringComparison.Ordinal) &&
            !File.Exists(target.OriginalPath) && !Directory.Exists(ProfileBackupDirectory(target)))
        {
            EnsureProfileHistory(branchFrom);
            var destination = Path.Combine(ProfileBackupDirectory(target), "Revisioner");
            Directory.CreateDirectory(destination);
            foreach (var record in ReadHistory(branchFrom))
            {
                var path = Path.Combine(destination, Path.GetFileName(record.Path));
                AtomicHistoryWrite(path, File.ReadAllBytes(record.Path));
                AtomicHistoryWrite(path + ".meta.json", JsonSerializer.SerializeToUtf8Bytes(record.Metadata, JsonOptions));
            }
        }
        EnsureProfileHistory(target, verifyOriginal: kind == ProfileRevisionKind.ManualSave);
        RecordSnapshot(target, NormalizeHistorySnapshot(bytes, mediaDirectory), kind, DateTimeOffset.UtcNow);
    }

    private void EnsureProfileHistory(ProfileBackupTarget target, bool verifyOriginal = false)
    {
        var folder = ProfileBackupDirectory(target);
        Directory.CreateDirectory(Path.Combine(folder, "Revisioner"));
        ImportLegacyHistory(target, target.OriginalPath, ProfileRevisionKind.LegacySaved);
        ImportLegacyHistory(target, target.WorkingCopyPath, ProfileRevisionKind.Legacy);
        var info = ReadBackupInfo(target);
        if (target.OriginalPath is { } original && File.Exists(original))
        {
            var file = new FileInfo(original);
            var stamp = $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
            // Always inspect actual bytes before a manual overwrite. External
            // editors/sync tools can preserve both file length and timestamps.
            if (verifyOriginal || info.OriginalStamp != stamp)
            {
                var bytes = NormalizeHistorySnapshot(File.ReadAllBytes(original), Path.GetDirectoryName(original)!);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (!ReadHistory(target).Any(record => record.Metadata.Hash == hash && Protected(record.Metadata.Kind)))
                    RecordSnapshot(target, bytes, ProfileRevisionKind.Original, file.LastWriteTimeUtc, updateLatest: info.LatestRecord is null);
                info = ReadBackupInfo(target);
                info.OriginalStamp = stamp;
                WriteBackupInfo(target, info);
            }
        }
        // Preserve the last recovery file from older versions before it can be
        // overwritten or dismissed. Import markers make retries idempotent.
        var marker = Path.Combine(folder, "recovery-imported.json");
        if (!File.Exists(marker))
        {
            if (File.Exists(target.WorkingCopyPath))
                RecordSnapshot(target, NormalizeHistorySnapshot(File.ReadAllBytes(target.WorkingCopyPath), Path.GetDirectoryName(target.WorkingCopyPath)!),
                    ProfileRevisionKind.Legacy, File.GetLastWriteTimeUtc(target.WorkingCopyPath));
            AtomicHistoryWrite(marker, "true"u8.ToArray());
        }
    }

    private void ImportLegacyHistory(ProfileBackupTarget target, string? source, ProfileRevisionKind kind)
    {
        if (source is null) return;
        var marker = Path.Combine(ProfileBackupDirectory(target), "imported-" + HistoryKey(source) + ".json");
        if (File.Exists(marker)) return;
        var legacy = GetRevisionDirectory(source);
        if (Directory.Exists(legacy))
            foreach (var file in Directory.EnumerateFiles(legacy, "*.floorballdj.json").OrderBy(Path.GetFileName))
            {
                try
                {
                    var mediaBase = Path.GetDirectoryName(source)!;
                    var bytes = NormalizeHistorySnapshot(File.ReadAllBytes(file), mediaBase);
                    var imported = "import-" + HistoryKey(file) + ".floorballdj.json";
                    var path = Path.Combine(ProfileBackupDirectory(target), "Revisioner", imported);
                    if (File.Exists(path + ".meta.json")) continue;
                    var description = ReadRevisionMetadata(file)?.ChangeDescription ?? "Importerad återställningspunkt";
                    var metadata = DescribeSnapshot(bytes, kind, ParseRevisionTimestamp(new FileInfo(file)), description, description);
                    AtomicHistoryWrite(path, bytes);
                    AtomicHistoryWrite(path + ".meta.json", JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions));
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or NullReferenceException) { /* Keep unreadable legacy files in place. */ }
            }
        AtomicHistoryWrite(marker, "true"u8.ToArray());
    }

    private void RecordSnapshot(ProfileBackupTarget target, byte[] bytes, ProfileRevisionKind kind, DateTimeOffset timestamp, bool updateLatest = true)
    {
        var info = ReadBackupInfo(target);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var latestPath = ProfileBackupPath(target);
        var previous = File.Exists(latestPath) ? File.ReadAllBytes(latestPath) : null;
        if (updateLatest && info.LatestHash == hash && info.LatestRecord is { } record &&
            previous is not null && Convert.ToHexString(SHA256.HashData(previous)) == hash &&
            File.Exists(Path.Combine(ProfileBackupDirectory(target), "Revisioner", Path.GetFileName(record))) &&
            File.Exists(Path.Combine(ProfileBackupDirectory(target), "Revisioner", Path.GetFileName(record)) + ".meta.json"))
        {
            if (Protected(kind))
            {
                var metadataPath = Path.Combine(ProfileBackupDirectory(target), "Revisioner", Path.GetFileName(record)) + ".meta.json";
                var metadata = JsonSerializer.Deserialize<ProfileHistoryMetadata>(File.ReadAllBytes(metadataPath), JsonOptions)!;
                // Autosaves never demote a manual milestone; an explicit action
                // can promote an identical autosave without duplicating content.
                if (!Protected(metadata.Kind) || kind == ProfileRevisionKind.ManualSave &&
                    metadata.Kind is not (ProfileRevisionKind.ManualSave or ProfileRevisionKind.Original or ProfileRevisionKind.LegacySaved))
                {
                    metadata.Kind = kind;
                    metadata.Timestamp = timestamp;
                    AtomicHistoryWrite(metadataPath, JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions));
                }
            }
            return;
        }
        var changes = DescribeHistoryChanges(previous, bytes);
        var summary = changes.Count == 1 ? changes[0] : $"{changes.Count} ändringar · {changes[0]}";
        var log = string.Join(Environment.NewLine, changes.Take(100).Select(change => "• " + change));
        if (changes.Count > 100) log += $"{Environment.NewLine}• Ytterligare {changes.Count - 100} ändringar";
        var meta = DescribeSnapshot(bytes, kind, timestamp, summary, log);
        var filename = $"{timestamp.UtcDateTime:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.floorballdj.json";
        var path = Path.Combine(ProfileBackupDirectory(target), "Revisioner", filename);
        AtomicHistoryWrite(path, bytes);
        AtomicHistoryWrite(path + ".meta.json", JsonSerializer.SerializeToUtf8Bytes(meta, JsonOptions));
        if (updateLatest)
        {
            AtomicHistoryWrite(latestPath, bytes);
            info.LatestHash = hash; info.LatestRecord = filename;
            WriteBackupInfo(target, info);
        }
        // Only dense automatic checkpoints rotate. Saved originals, manual
        // saves, Don't save and pre-restore states are never removed here.
        foreach (var old in ReadHistory(target).Where(record => !Protected(record.Metadata.Kind))
                     .OrderByDescending(record => record.Metadata.Timestamp).Skip(AutomaticRevisionLimit))
        {
            if (Path.GetFileName(old.Path) == info.LatestRecord) continue;
            File.Delete(old.Path);
            File.Delete(old.Path + ".meta.json");
        }
    }

    private static ProfileHistoryMetadata DescribeSnapshot(byte[] bytes, ProfileRevisionKind kind, DateTimeOffset timestamp, string summary, string log)
    {
        var project = JsonSerializer.Deserialize<FloorballProject>(bytes, JsonOptions) ?? throw new InvalidDataException("Backupen är tom.");
        return new ProfileHistoryMetadata { Timestamp = timestamp, Kind = kind, Hash = Convert.ToHexString(SHA256.HashData(bytes)),
            ProfileName = project.Name, DeckCount = project.Decks.Count, JingleCount = project.Decks.Sum(deck => deck.Jingles.Count(jingle => jingle.HasAudio)),
            Summary = summary, ChangeLog = log };
    }

    private IEnumerable<(string Path, ProfileHistoryMetadata Metadata)> ReadHistory(ProfileBackupTarget target)
    {
        var directory = Path.Combine(ProfileBackupDirectory(target), "Revisioner");
        if (!Directory.Exists(directory)) yield break;
        foreach (var path in Directory.EnumerateFiles(directory, "*.floorballdj.json.meta.json"))
        {
            ProfileHistoryMetadata? metadata = null;
            try { metadata = JsonSerializer.Deserialize<ProfileHistoryMetadata>(File.ReadAllBytes(path), JsonOptions); }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            var snapshot = path[..^".meta.json".Length];
            if (metadata is not null && File.Exists(snapshot)) yield return (snapshot, metadata);
        }
    }

    private ProfileBackupInfo ReadBackupInfo(ProfileBackupTarget target)
    {
        var path = Path.Combine(ProfileBackupDirectory(target), "profil.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<ProfileBackupInfo>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidDataException("Backupinformationen är skadad.") : new();
    }
    private void WriteBackupInfo(ProfileBackupTarget target, ProfileBackupInfo info)
    {
        info.SourcePath = target.OriginalPath; info.ProfileName = target.ProfileName;
        AtomicHistoryWrite(Path.Combine(ProfileBackupDirectory(target), "profil.json"), JsonSerializer.SerializeToUtf8Bytes(info, JsonOptions));
    }
    private static void AtomicHistoryWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static byte[] NormalizeHistorySnapshot(byte[] bytes, string mediaDirectory)
    {
        var project = JsonSerializer.Deserialize<FloorballProject>(bytes, JsonOptions) ?? throw new InvalidDataException("Profilen är tom.");
        string? Resolve(string? path) => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(mediaDirectory, path));
        foreach (var jingle in project.Decks.SelectMany(deck => deck.Jingles).Concat(project.Settings.AutoplayQueueItems ?? [])) jingle.FilePath = Resolve(jingle.FilePath)!;
        project.Settings.MusicFolderPath = Resolve(project.Settings.MusicFolderPath);
        project.Settings.AutoplayDefaultPlaylistPath = Resolve(project.Settings.AutoplayDefaultPlaylistPath);
        project.Settings.AutoplayLastPlaylistPath = Resolve(project.Settings.AutoplayLastPlaylistPath);
        foreach (var playlist in project.Settings.AutoplayProfiles ?? []) playlist.PlaylistPath = Resolve(playlist.PlaylistPath);
        return JsonSerializer.SerializeToUtf8Bytes(project, JsonOptions);
    }

    private static IReadOnlyList<string> DescribeHistoryChanges(byte[]? beforeBytes, byte[] afterBytes)
    {
        if (beforeBytes is null) return ["Första lokala säkerhetskopian av profilen"];
        var before = JsonSerializer.Deserialize<FloorballProject>(beforeBytes, JsonOptions)!;
        var after = JsonSerializer.Deserialize<FloorballProject>(afterBytes, JsonOptions)!;
        var changes = new List<string>();
        var oldDecks = before.Decks.GroupBy(deck => deck.Id).ToDictionary(group => group.Key, group => group.First());
        var newDecks = after.Decks.GroupBy(deck => deck.Id).ToDictionary(group => group.Key, group => group.First());
        foreach (var id in newDecks.Keys.Except(oldDecks.Keys)) changes.Add($"Lade till deck ‘{newDecks[id].Name}’");
        foreach (var id in oldDecks.Keys.Except(newDecks.Keys)) changes.Add($"Tog bort deck ‘{oldDecks[id].Name}’");
        foreach (var id in oldDecks.Keys.Intersect(newDecks.Keys))
        {
            var old = oldDecks[id]; var current = newDecks[id];
            if (old.Name != current.Name) changes.Add($"Bytte decknamn: ‘{old.Name}’ → ‘{current.Name}’");
            if (old.PageCount != current.PageCount || old.Rows != current.Rows || old.Columns != current.Columns ||
                JsonSerializer.Serialize(old.PageLayouts, JsonOptions) != JsonSerializer.Serialize(current.PageLayouts, JsonOptions)) changes.Add($"Ändrade layout eller sidor i ‘{current.Name}’");
        }
        if (oldDecks.Keys.ToHashSet().SetEquals(newDecks.Keys) && !before.Decks.Select(deck => deck.Id).SequenceEqual(after.Decks.Select(deck => deck.Id))) changes.Add("Ändrade deckordningen");
        var oldSongs = before.Decks.SelectMany(deck => deck.Jingles.Where(song => song.HasContent).Select(song => (Deck: deck, Song: song))).GroupBy(item => item.Song.Id).ToDictionary(group => group.Key, group => group.First());
        var newSongs = after.Decks.SelectMany(deck => deck.Jingles.Where(song => song.HasContent).Select(song => (Deck: deck, Song: song))).GroupBy(item => item.Song.Id).ToDictionary(group => group.Key, group => group.First());
        foreach (var id in newSongs.Keys.Except(oldSongs.Keys)) changes.Add($"Lade till ‘{DisplayTitle(newSongs[id].Song)}’ i ‘{newSongs[id].Deck.Name}’");
        foreach (var id in oldSongs.Keys.Except(newSongs.Keys)) changes.Add($"Tog bort ‘{DisplayTitle(oldSongs[id].Song)}’ från ‘{oldSongs[id].Deck.Name}’");
        foreach (var id in oldSongs.Keys.Intersect(newSongs.Keys))
        {
            var old = oldSongs[id]; var current = newSongs[id]; var title = DisplayTitle(current.Song);
            if (old.Song.Title != current.Song.Title) changes.Add($"Bytte låtnamn: ‘{DisplayTitle(old.Song)}’ → ‘{title}’");
            if (old.Deck.Id != current.Deck.Id || old.Song.Position != current.Song.Position) changes.Add($"Flyttade ‘{title}’ till ‘{current.Deck.Name}’, plats {current.Song.Position + 1}");
            if (old.Song.FilePath != current.Song.FilePath) changes.Add($"Bytte ljudfil för ‘{title}’");
            var oldProperties = JsonSerializer.SerializeToElement(old.Song, JsonOptions);
            var currentProperties = JsonSerializer.SerializeToElement(current.Song, JsonOptions);
            var fields = currentProperties.EnumerateObject().Where(property => property.Name is not ("id" or "title" or "position" or "filePath") &&
                (!oldProperties.TryGetProperty(property.Name, out var previous) || previous.GetRawText() != property.Value.GetRawText())).Select(property => HistoryFieldName(property.Name)).Distinct().ToArray();
            if (fields.Length > 0) changes.Add($"Ändrade egenskaper för ‘{title}’: {string.Join(", ", fields)}");
        }
        var oldSettings = JsonSerializer.SerializeToElement(before.Settings, JsonOptions);
        foreach (var setting in JsonSerializer.SerializeToElement(after.Settings, JsonOptions).EnumerateObject())
            if (!oldSettings.TryGetProperty(setting.Name, out var old) || old.GetRawText() != setting.Value.GetRawText())
                changes.Add(setting.Name switch
                {
                    "autoplayQueueItems" => "Ändrade låtar eller ordning i Autoplay-kön",
                    "randomPoolSetups" or "randomPoolProfiles" => "Ändrade slumpprofiler eller slumpgrupper",
                    "autoplayProfiles" => "Ändrade sparade Autoplay-listor",
                    _ => $"Ändrade {HistoryFieldName(setting.Name)}"
                });
        if (before.Name != after.Name) changes.Add($"Bytte profilnamn: ‘{before.Name}’ → ‘{after.Name}’");
        return changes.Count == 0 ? ["Profilens data ändrades"] : changes.Distinct().ToArray();
    }

    private static string HistoryFieldName(string name) => name switch
    {
        "gainDb" or "masterVolumeDb" => "volym", "normalizationEnabled" or "normalizationGainDb" => "normalisering",
        "startSeconds" => "startpunkt", "endSeconds" => "slutpunkt", "fadeInOverrideSeconds" or "fadeInSeconds" => "intoning",
        "fadeOutOverrideSeconds" or "fadeOutSeconds" => "uttoning", "shortcut" or "shortcutSwitchesDeck" => "snabbtangent",
        "buttonColor" or "textColor" => "färg", "playMode" => "spelläge", "loopEnabled" => "loop",
        "pitchSemitones" => "tonhöjd", "tempoPercent" => "tempo", "ratePercent" => "uppspelningshastighet",
        "autoplayQueueShuffleEnabled" => "slumpning i Autoplay", "autoplayQueueLoopEnabled" => "loop i Autoplay",
        "autoplayTransitionSeconds" => "övergångstid i Autoplay", "musicFolderPath" => "musikmapp",
        "secondaryOutputDeviceId" or "outputDeviceId" => "ljudutgång", "trackSession" => "sessionsinställning",
        "activeRandomPoolSetupId" => "aktiv slumpprofil", "teamDeckProfiles" => "Team Deck",
        "fontFamily" or "showJingleDuration" or "deckTabWidth" or "deckTabHeight" or "deckTabsPerRow" => "utseende",
        _ => "övriga profilinställningar"
    };

    private sealed class ProfileBackupInfo
    {
        public string? SourcePath { get; set; }
        public string ProfileName { get; set; } = "";
        public string? LatestHash { get; set; }
        public string? LatestRecord { get; set; }
        public string? OriginalStamp { get; set; }
    }
    private sealed class ProfileHistoryMetadata
    {
        public DateTimeOffset Timestamp { get; set; }
        public ProfileRevisionKind Kind { get; set; }
        public string Hash { get; set; } = "";
        public string ProfileName { get; set; } = "";
        public int DeckCount { get; set; }
        public int JingleCount { get; set; }
        public string Summary { get; set; } = "";
        public string ChangeLog { get; set; } = "";
    }
}
