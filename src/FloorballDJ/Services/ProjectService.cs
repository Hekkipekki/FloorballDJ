using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using FloorballDJ.Models;

namespace FloorballDJ.Services;

public sealed class ProjectService
{
    public const int MaximumDeckRows = 50;
    public const int MaximumDeckColumns = 12;
    public const int MaximumDeckPages = 20;
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string AppDataDirectory { get; } = ResolveAppDataDirectory();
    public string DefaultProjectPath => Path.Combine(AppDataDirectory, "autosave.floorballdj.json");

    private static string ResolveAppDataDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("FLOORBALLDJ_DATA_DIR");
        return string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloorballDJ")
            : Path.GetFullPath(overrideDirectory);
    }

    public async Task SaveAsync(FloorballProject project, string path)
    {
        // Ta en stabil ögonblicksbild innan första await. Då kan användaren fortsätta
        // arbeta utan att en pågående serialisering räknar upp muterbara samlingar.
        var snapshot = JsonSerializer.SerializeToUtf8Bytes(project, JsonOptions);
        await SaveGate.WaitAsync().ConfigureAwait(false);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using var processGate = new Semaphore(1, 1, @"Local\FloorballDJ.ProjectSave");
            var ownsProcessGate = await Task.Run(() => processGate.WaitOne(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            if (!ownsProcessGate) throw new IOException("Projektfilen är upptagen. Försök igen om en stund.");
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                                 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(snapshot).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                await Task.Run(() =>
                {
                    CreateRevisionIfChanged(path, temporaryPath);
                    File.Move(temporaryPath, path, true);
                }).ConfigureAwait(false);
            }
            finally { processGate.Release(); }
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            SaveGate.Release();
        }
    }

    public async Task<IReadOnlyList<ProjectRevision>> GetRevisionsAsync(string projectPath)
    {
        var directory = GetRevisionDirectory(projectPath);
        if (!Directory.Exists(directory)) return [];
        var revisions = new List<ProjectRevision>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.floorballdj.json").OrderByDescending(Path.GetFileName))
        {
            try
            {
                var project = await LoadAsync(path);
                var file = new FileInfo(path);
                var metadata = ReadRevisionMetadata(path);
                revisions.Add(new ProjectRevision(path, ParseRevisionTimestamp(file), project.Name,
                    project.Decks.Count, project.Decks.Sum(deck => deck.Jingles.Count(jingle => jingle.HasAudio)), file.Length,
                    metadata?.ChangeDescription ?? "Äldre återställningspunkt"));
            }
            catch { }
        }
        return revisions;
    }

    private void CreateRevisionIfChanged(string projectPath, string newPath)
    {
        if (!File.Exists(projectPath) || FilesEqual(projectPath, newPath)) return;
        var directory = GetRevisionDirectory(projectPath);
        Directory.CreateDirectory(directory);
        var revisionPath = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.floorballdj.json");
        File.Copy(projectPath, revisionPath, false);
        var metadata = new RevisionMetadata { ChangeDescription = DescribeRevisionChange(projectPath, newPath) };
        File.WriteAllText(GetRevisionMetadataPath(revisionPath), JsonSerializer.Serialize(metadata, JsonOptions));

        foreach (var oldRevision in Directory.EnumerateFiles(directory, "*.floorballdj.json")
                     .OrderByDescending(Path.GetFileName).Skip(100))
        {
            try { File.Delete(oldRevision); } catch { }
            try { File.Delete(GetRevisionMetadataPath(oldRevision)); } catch { }
        }
    }

    private static RevisionMetadata? ReadRevisionMetadata(string revisionPath)
    {
        try
        {
            var path = GetRevisionMetadataPath(revisionPath);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<RevisionMetadata>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch { return null; }
    }

    private static string GetRevisionMetadataPath(string revisionPath) => revisionPath + ".meta.json";

    private static string DescribeRevisionChange(string oldPath, string newPath)
    {
        try
        {
            var oldProject = JsonSerializer.Deserialize<FloorballProject>(File.ReadAllText(oldPath), JsonOptions);
            var newProject = JsonSerializer.Deserialize<FloorballProject>(File.ReadAllText(newPath), JsonOptions);
            if (oldProject is null || newProject is null) return "Profilen ändrades";

            var oldJingles = oldProject.Decks.SelectMany(deck => deck.Jingles
                    .Where(jingle => jingle.HasContent)
                    .Select(jingle => (Deck: deck, Jingle: jingle)))
                .ToDictionary(item => item.Jingle.Id);
            var newJingles = newProject.Decks.SelectMany(deck => deck.Jingles
                    .Where(jingle => jingle.HasContent)
                    .Select(jingle => (Deck: deck, Jingle: jingle)))
                .ToDictionary(item => item.Jingle.Id);

            var added = newJingles.Keys.Except(oldJingles.Keys).Select(id => newJingles[id]).FirstOrDefault();
            if (added.Jingle is not null) return $"Före: lade till ‘{DisplayTitle(added.Jingle)}’ i {added.Deck.Name}";
            var removed = oldJingles.Keys.Except(newJingles.Keys).Select(id => oldJingles[id]).FirstOrDefault();
            if (removed.Jingle is not null) return $"Före: tog bort ‘{DisplayTitle(removed.Jingle)}’ från {removed.Deck.Name}";

            foreach (var id in oldJingles.Keys.Intersect(newJingles.Keys))
            {
                var before = oldJingles[id];
                var after = newJingles[id];
                if (!string.Equals(before.Jingle.Title, after.Jingle.Title, StringComparison.Ordinal))
                    return $"Före: ändrade ‘{DisplayTitle(before.Jingle)}’ till ‘{DisplayTitle(after.Jingle)}’";
                if (before.Deck.Id != after.Deck.Id || before.Jingle.Position != after.Jingle.Position)
                    return $"Före: flyttade ‘{DisplayTitle(after.Jingle)}’";
                if (JsonSerializer.Serialize(before.Jingle, JsonOptions) != JsonSerializer.Serialize(after.Jingle, JsonOptions))
                    return $"Före: ändrade egenskaper för ‘{DisplayTitle(after.Jingle)}’";
            }

            if (oldProject.Decks.Count != newProject.Decks.Count)
                return oldProject.Decks.Count < newProject.Decks.Count ? "Före: lade till ett deck" : "Före: tog bort ett deck";
            for (var index = 0; index < Math.Min(oldProject.Decks.Count, newProject.Decks.Count); index++)
            {
                var before = oldProject.Decks[index];
                var after = newProject.Decks[index];
                if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
                    return $"Före: ändrade decknamn från ‘{before.Name}’ till ‘{after.Name}’";
                if (before.Rows != after.Rows || before.Columns != after.Columns || before.PageCount != after.PageCount ||
                    JsonSerializer.Serialize(before.PageLayouts, JsonOptions) != JsonSerializer.Serialize(after.PageLayouts, JsonOptions))
                    return $"Före: ändrade layouten för {after.Name}";
            }

            if (JsonSerializer.Serialize(oldProject.Settings.RandomPoolSetups, JsonOptions) !=
                JsonSerializer.Serialize(newProject.Settings.RandomPoolSetups, JsonOptions) ||
                oldProject.Settings.ActiveRandomPoolSetupId != newProject.Settings.ActiveRandomPoolSetupId)
                return "Före: ändrade slumpprofiler eller slumpgrupper";
            if (!string.Equals(oldProject.Name, newProject.Name, StringComparison.Ordinal))
                return $"Före: ändrade profilnamn till ‘{newProject.Name}’";
            if (JsonSerializer.Serialize(oldProject.Settings, JsonOptions) != JsonSerializer.Serialize(newProject.Settings, JsonOptions))
                return "Före: ändrade programinställningar";
            return "Profilen ändrades";
        }
        catch { return "Profilen ändrades"; }
    }

    private static string DisplayTitle(Jingle jingle) =>
        string.IsNullOrWhiteSpace(jingle.Title) ? Path.GetFileNameWithoutExtension(jingle.FilePath) : jingle.Title;

    private string GetRevisionDirectory(string projectPath)
    {
        var normalized = Path.GetFullPath(projectPath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12];
        return Path.Combine(AppDataDirectory, "Revisions", hash);
    }

    private static bool FilesEqual(string firstPath, string secondPath)
    {
        var first = new FileInfo(firstPath);
        var second = new FileInfo(secondPath);
        if (first.Length != second.Length) return false;
        using var firstStream = File.OpenRead(firstPath);
        using var secondStream = File.OpenRead(secondPath);
        Span<byte> firstBuffer = stackalloc byte[8192];
        Span<byte> secondBuffer = stackalloc byte[8192];
        while (true)
        {
            var firstRead = firstStream.Read(firstBuffer);
            var secondRead = secondStream.Read(secondBuffer);
            if (firstRead != secondRead || !firstBuffer[..firstRead].SequenceEqual(secondBuffer[..secondRead])) return false;
            if (firstRead == 0) return true;
        }
    }

    private static DateTime ParseRevisionTimestamp(FileInfo file)
    {
        var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file.Name));
        return DateTime.TryParseExact(name, "yyyyMMdd-HHmmss-fff", null,
            System.Globalization.DateTimeStyles.AssumeLocal, out var timestamp) ? timestamp : file.LastWriteTime;
    }

    public async Task<FloorballProject> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var project = await JsonSerializer.DeserializeAsync<FloorballProject>(stream, JsonOptions)
            ?? throw new InvalidDataException("Projektfilen är tom eller skadad.");
        if (project.FormatVersion < 2)
        {
            foreach (var jingle in project.Decks.SelectMany(deck => deck.Jingles))
                if (jingle.PlayMode == JinglePlayMode.Mix) jingle.PlayMode = JinglePlayMode.Solo;
            project.FormatVersion = 2;
        }
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var jingle in project.Decks.SelectMany(deck => deck.Jingles))
            if (jingle.HasAudio && !Path.IsPathRooted(jingle.FilePath))
                jingle.FilePath = Path.GetFullPath(Path.Combine(projectDirectory, jingle.FilePath));
        if (!string.IsNullOrWhiteSpace(project.Settings.MusicFolderPath) &&
            !Path.IsPathRooted(project.Settings.MusicFolderPath))
            project.Settings.MusicFolderPath = Path.GetFullPath(Path.Combine(projectDirectory, project.Settings.MusicFolderPath));
        return project;
    }

    public async Task<string> BackupAsync(FloorballProject project)
    {
        var directory = Path.Combine(AppDataDirectory, "Backups");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"FloorballDJ-{DateTime.Now:yyyyMMdd-HHmmss}.floorballdj.json");
        await SaveAsync(project, path);
        return path;
    }

    public async Task<PortableBackupResult> CreateMediaBackupAsync(FloorballProject project, string parentDirectory,
        IProgress<PortableBackupProgress>? progress = null)
    {
        progress?.Report(new PortableBackupProgress("Förbereder backupen…", 1, Detail: project.Name));
        var baseName = $"FloorballDJ-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        var directory = Path.Combine(parentDirectory, baseName);
        var suffix = 2;
        while (Directory.Exists(directory)) directory = Path.Combine(parentDirectory, $"{baseName}-{suffix++}");
        var mediaDirectory = Path.Combine(directory, "Media");
        Directory.CreateDirectory(mediaDirectory);

        var json = JsonSerializer.Serialize(project, JsonOptions);
        var copy = JsonSerializer.Deserialize<FloorballProject>(json, JsonOptions)
            ?? throw new InvalidDataException("Projektet kunde inte kopieras.");
        // Slumpgrupper är profilinställningar. Normalisera den fristående kopian före
        // export så även äldre profiler migreras och aldrig hämtar grupper från någon
        // annan profil på den nya datorn.
        EnsureLayout(copy);
        var totalMediaFiles = copy.Decks
            .SelectMany(deck => deck.Jingles)
            .Where(jingle => jingle.HasAudio && File.Exists(jingle.FilePath))
            .Select(jingle => jingle.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var usedDeckFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copiedMediaCount = 0;
        var missingFiles = new List<string>();
        foreach (var deck in copy.Decks)
        {
            var baseFolderName = SanitizePathSegment(deck.Name, $"Deck {copy.Decks.IndexOf(deck) + 1}");
            var deckFolderName = baseFolderName;
            var folderSuffix = 2;
            while (!usedDeckFolderNames.Add(deckFolderName)) deckFolderName = $"{baseFolderName}-{folderSuffix++}";
            Directory.CreateDirectory(Path.Combine(mediaDirectory, deckFolderName));

            var copiedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var jingle in deck.Jingles.Where(jingle => jingle.HasAudio))
            {
                var source = jingle.FilePath;
                if (!File.Exists(source))
                {
                    missingFiles.Add($"{deck.Name} / {jingle.Title}: {source}");
                    continue;
                }
                if (!copiedFiles.TryGetValue(source, out var relativePath))
                {
                    var originalName = Path.GetFileName(source);
                    var fileName = originalName;
                    var index = 2;
                    while (!usedNames.Add(fileName))
                        fileName = $"{Path.GetFileNameWithoutExtension(originalName)}-{index++}{Path.GetExtension(originalName)}";
                    relativePath = Path.Combine("Media", deckFolderName, fileName);
                    copiedFiles[source] = relativePath;
                    var targetPath = Path.Combine(directory, relativePath);
                    await using (var input = File.OpenRead(source))
                    await using (var output = File.Create(targetPath))
                        await input.CopyToAsync(output);
                    // Loudnessanalysen använder filstorlek och ändringstid för att avgöra
                    // om resultatet fortfarande är giltigt. Behåll därför originalets tid.
                    File.SetLastWriteTimeUtc(targetPath, File.GetLastWriteTimeUtc(source));
                    copiedMediaCount++;
                    var copyPercent = totalMediaFiles == 0 ? 82 : 5 + copiedMediaCount * 77d / totalMediaFiles;
                    progress?.Report(new PortableBackupProgress("Kopierar ljudfiler…", copyPercent,
                        copiedMediaCount, totalMediaFiles, Path.GetFileName(source)));
                }
                jingle.FilePath = relativePath;
            }
        }

        // Maskinspecifika enhets-ID:n ska inte följa med till nästa dator. Alla andra
        // ljud-, layout- och arbetsinställningar ligger kvar i den portabla profilen.
        copy.Settings.OutputDeviceId = null;
        copy.Settings.SecondaryOutputDeviceId = null;
        copy.Settings.MusicFolderPath = "Media";

        progress?.Report(new PortableBackupProgress("Sparar profil och inställningar…", 87,
            copiedMediaCount, totalMediaFiles));
        var projectName = SanitizePathSegment(project.Name, "FloorballDJ-profil");
        var profileFileName = $"{projectName}.floorballdj.json";
        await SaveAsync(copy, Path.Combine(directory, profileFileName));

        var settingsDirectory = Path.Combine(directory, "Inställningar");
        Directory.CreateDirectory(settingsDirectory);
        var presetService = new ColorPresetService();
        var presetsIncluded = File.Exists(presetService.PresetsPath);
        if (presetsIncluded)
            File.Copy(presetService.PresetsPath, Path.Combine(settingsDirectory, "color-presets.json"), true);

        var fontsIncluded = 0;
        var fontsTarget = Path.Combine(settingsDirectory, "Fonts");
        if (Directory.Exists(FontService.FontsDirectory))
        {
            foreach (var source in Directory.EnumerateFiles(FontService.FontsDirectory)
                         .Where(path => Path.GetExtension(path).Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                                        Path.GetExtension(path).Equals(".otf", StringComparison.OrdinalIgnoreCase)))
            {
                Directory.CreateDirectory(fontsTarget);
                File.Copy(source, Path.Combine(fontsTarget, Path.GetFileName(source)), true);
                fontsIncluded++;
            }
        }

        var manifest = new PortableBackupManifest
        {
            CreatedAt = DateTimeOffset.Now,
            ProfileFile = profileFileName,
            ProjectName = project.Name,
            MediaFileCount = copiedMediaCount,
            CustomFontCount = fontsIncluded,
            RandomPoolProfileCount = copy.Settings.RandomPoolSetups.Sum(setup => setup.Profiles.Count),
            TeamDeckProfileCount = copy.Settings.TeamDeckProfiles.Count,
            IncludesColorPresets = presetsIncluded,
            MissingFiles = missingFiles
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "floorballdj-backup.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));
        progress?.Report(new PortableBackupProgress("Skapar backupinformation…", 95,
            copiedMediaCount, totalMediaFiles));
        var missingSummary = missingFiles.Count == 0
            ? "Inga länkade ljudfiler saknades när backupen skapades."
            : $"VARNING: {missingFiles.Count} länkade ljudfiler saknades:\r\n- {string.Join("\r\n- ", missingFiles)}";
        await File.WriteAllTextAsync(Path.Combine(directory, "LÄS MIG - ÅTERSTÄLL BACKUP.txt"),
            $"FloorballDJ flyttbackup\r\nSkapad: {manifest.CreatedAt:yyyy-MM-dd HH:mm:ss zzz}\r\nProfil: {project.Name}\r\n\r\n" +
            "På den andra datorn:\r\n1. Installera och starta FloorballDJ.\r\n2. Välj Profil > Återställ flyttbackup.\r\n3. Välj den här mappen.\r\n4. Välj datorns ljudutgångar under Verktyg > Inställningar.\r\n\r\n" +
            "Licens/provperiod är maskinbunden och följer inte med. Ljudfilerna är rena filkopior utan omkodning.\r\n\r\n" +
            $"Slumpprofiler: {copy.Settings.RandomPoolSetups.Count}. Slumpgrupper totalt: {copy.Settings.RandomPoolSetups.Sum(setup => setup.Profiles.Count)}. Team Deck: {copy.Settings.TeamDeckProfiles.Count}. De är profilbundna och följer med denna profil.\r\n\r\n" +
            missingSummary);

        progress?.Report(new PortableBackupProgress("Backupen är klar", 100,
            copiedMediaCount, totalMediaFiles));
        return new PortableBackupResult(directory, Path.Combine(directory, profileFileName), copiedMediaCount,
            fontsIncluded, copy.Settings.RandomPoolSetups.Sum(setup => setup.Profiles.Count), copy.Settings.TeamDeckProfiles.Count, presetsIncluded, missingFiles);
    }

    public async Task<PortableRestoreResult> RestorePortableBackupAsync(string backupDirectory,
        IProgress<PortableBackupProgress>? progress = null)
    {
        progress?.Report(new PortableBackupProgress("Kontrollerar backupen…", 2));
        var sourceDirectory = Path.GetFullPath(backupDirectory);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException("Den valda backupmappen finns inte.");

        PortableBackupManifest manifest;
        var manifestPath = Path.Combine(sourceDirectory, "floorballdj-backup.json");
        if (File.Exists(manifestPath))
        {
            manifest = JsonSerializer.Deserialize<PortableBackupManifest>(await File.ReadAllTextAsync(manifestPath), JsonOptions)
                       ?? throw new InvalidDataException("Backupinformationen är tom eller skadad.");
        }
        else
        {
            var legacyProfiles = Directory.EnumerateFiles(sourceDirectory, "*.floorballdj.json", SearchOption.TopDirectoryOnly).ToArray();
            if (legacyProfiles.Length != 1)
                throw new InvalidDataException("Mappen är inte en komplett FloorballDJ-backup.");
            manifest = new PortableBackupManifest
            {
                ProfileFile = Path.GetFileName(legacyProfiles[0]),
                ProjectName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(legacyProfiles[0]))
            };
        }

        var sourceProfile = SafeChildPath(sourceDirectory, manifest.ProfileFile);
        if (!File.Exists(sourceProfile))
            throw new FileNotFoundException("Profilfilen som anges i backupen saknas.", sourceProfile);

        var importsRoot = Path.Combine(AppDataDirectory, "Importerade backuper");
        Directory.CreateDirectory(importsRoot);
        var baseName = SanitizePathSegment(manifest.ProjectName, "Importerad profil");
        var destinationDirectory = Path.Combine(importsRoot, $"{baseName}-{DateTime.Now:yyyyMMdd-HHmmss}");
        var suffix = 2;
        while (Directory.Exists(destinationDirectory))
            destinationDirectory = Path.Combine(importsRoot, $"{baseName}-{DateTime.Now:yyyyMMdd-HHmmss}-{suffix++}");
        await CopyDirectoryAsync(sourceDirectory, destinationDirectory, progress);

        var profilePath = SafeChildPath(destinationDirectory, manifest.ProfileFile);
        progress?.Report(new PortableBackupProgress("Installerar typsnitt och färgval…", 86));
        var fontsImported = ImportFonts(Path.Combine(destinationDirectory, "Inställningar", "Fonts"));
        var presetsImported = new ColorPresetService().MergeFrom(
            Path.Combine(destinationDirectory, "Inställningar", "color-presets.json"));

        progress?.Report(new PortableBackupProgress("Förbereder profilen…", 93, Detail: manifest.ProjectName));
        var restoredProject = await LoadAsync(profilePath);
        EnsureLayout(restoredProject);
        restoredProject.Settings.OutputDeviceId = null;
        restoredProject.Settings.SecondaryOutputDeviceId = null;
        await SaveAsync(restoredProject, profilePath);
        var missingCount = restoredProject.Decks.SelectMany(deck => deck.Jingles).Count(jingle => jingle.IsMissing);
        progress?.Report(new PortableBackupProgress("Återställningen är klar", 100,
            Detail: restoredProject.Name));
        return new PortableRestoreResult(profilePath, destinationDirectory, fontsImported, presetsImported,
            restoredProject.Settings.RandomPoolSetups.Sum(setup => setup.Profiles.Count),
            restoredProject.Settings.TeamDeckProfiles.Count, missingCount);
    }

    private static string SafeChildPath(string parentDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Backupen innehåller en ogiltig filsökväg.");
        var parent = Path.GetFullPath(parentDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(parent, relativePath));
        if (!candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backupen försöker använda en filsökväg utanför backupmappen.");
        return candidate;
    }

    private static async Task CopyDirectoryAsync(string sourceDirectory, string destinationDirectory,
        IProgress<PortableBackupProgress>? progress)
    {
        await Task.Run(() =>
        {
            foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
            Directory.CreateDirectory(destinationDirectory);
            var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).ToArray();
            for (var index = 0; index < files.Length; index++)
            {
                var source = files[index];
                var target = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, false);
                var completed = index + 1;
                var copyPercent = files.Length == 0 ? 80 : 8 + completed * 72d / files.Length;
                progress?.Report(new PortableBackupProgress("Kopierar backupfiler…", copyPercent,
                    completed, files.Length, Path.GetFileName(source)));
            }
        });
    }

    private static int ImportFonts(string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory)) return 0;
        Directory.CreateDirectory(FontService.FontsDirectory);
        var count = 0;
        foreach (var source in Directory.EnumerateFiles(sourceDirectory).Where(path =>
                     Path.GetExtension(path).Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetExtension(path).Equals(".otf", StringComparison.OrdinalIgnoreCase)))
        {
            var target = Path.Combine(FontService.FontsDirectory, Path.GetFileName(source));
            if (!File.Exists(target) || !FilesEqual(source, target)) File.Copy(source, target, true);
            count++;
        }
        return count;
    }

    private static string SanitizePathSegment(string? value, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var invalidCharacters = Path.GetInvalidFileNameChars();
        candidate = string.Concat(candidate.Select(character => invalidCharacters.Contains(character) ? '_' : character)).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;
    }

    public static FloorballProject CreateDefault()
    {
        var project = new FloorballProject();
        EnsureLayout(project);
        return project;
    }

    public static int CountHiddenAudioAfterResize(Deck deck, int rows, int columns)
    {
        var slots = Math.Clamp(rows, 1, MaximumDeckRows) * Math.Clamp(columns, 1, MaximumDeckColumns) *
                    MaximumDeckPages;
        return Math.Max(0, deck.Jingles.Count(jingle => jingle.HasContent) - slots);
    }

    public static void ResizeDeckLayout(Deck deck, int rows, int columns)
    {
        var newRows = Math.Clamp(rows, 1, MaximumDeckRows);
        var newColumns = Math.Clamp(columns, 1, MaximumDeckColumns);
        var contentCount = deck.Jingles.Count(jingle => jingle.HasContent);
        var newPageSlots = newRows * newColumns;
        var requiredPages = Math.Max(1, (int)Math.Ceiling(contentCount / (double)newPageSlots));
        var pageCount = Math.Clamp(Math.Max(deck.PageCount, requiredPages), 1, MaximumDeckPages);
        var layouts = Enumerable.Range(0, pageCount)
            .Select(_ => new Deck.PageLayout { Rows = newRows, Columns = newColumns }).ToList();
        RebuildDeckLayout(deck, layouts);
    }

    public static int CountHiddenAudioAfterPageResize(Deck deck, int page, int rows, int columns)
    {
        deck.EnsurePageLayouts();
        var layouts = deck.PageLayouts.Select(layout => new Deck.PageLayout
        {
            Rows = Math.Clamp(layout.Rows, 1, MaximumDeckRows),
            Columns = Math.Clamp(layout.Columns, 1, MaximumDeckColumns)
        }).ToList();
        var normalized = Math.Clamp(page, 0, layouts.Count - 1);
        layouts[normalized] = new Deck.PageLayout
        {
            Rows = Math.Clamp(rows, 1, MaximumDeckRows),
            Columns = Math.Clamp(columns, 1, MaximumDeckColumns)
        };
        var fallback = layouts[normalized].Rows * layouts[normalized].Columns;
        var maximumCapacity = layouts.Sum(layout => layout.Rows * layout.Columns) +
                              Math.Max(0, MaximumDeckPages - layouts.Count) * fallback;
        return Math.Max(0, deck.Jingles.Count(jingle => jingle.HasContent) - maximumCapacity);
    }

    public static void ResizeDeckPageLayout(Deck deck, int page, int rows, int columns)
    {
        deck.EnsurePageLayouts();
        var layouts = deck.PageLayouts.Select(layout => new Deck.PageLayout
        {
            Rows = Math.Clamp(layout.Rows, 1, MaximumDeckRows),
            Columns = Math.Clamp(layout.Columns, 1, MaximumDeckColumns)
        }).ToList();
        var normalized = Math.Clamp(page, 0, layouts.Count - 1);
        layouts[normalized] = new Deck.PageLayout
        {
            Rows = Math.Clamp(rows, 1, MaximumDeckRows),
            Columns = Math.Clamp(columns, 1, MaximumDeckColumns)
        };
        var contentCount = deck.Jingles.Count(jingle => jingle.HasContent);
        while (layouts.Sum(layout => layout.Rows * layout.Columns) < contentCount && layouts.Count < MaximumDeckPages)
            layouts.Add(new Deck.PageLayout { Rows = layouts[normalized].Rows, Columns = layouts[normalized].Columns });
        RebuildDeckLayout(deck, layouts);
    }

    private static void RebuildDeckLayout(Deck deck, List<Deck.PageLayout> targetLayouts)
    {
        deck.EnsurePageLayouts();
        var oldLayouts = deck.PageLayouts.Select(layout => new Deck.PageLayout
        {
            Rows = Math.Clamp(layout.Rows, 1, MaximumDeckRows),
            Columns = Math.Clamp(layout.Columns, 1, MaximumDeckColumns)
        }).ToArray();
        var oldStarts = new int[oldLayouts.Length];
        for (var page = 1; page < oldStarts.Length; page++)
            oldStarts[page] = oldStarts[page - 1] + oldLayouts[page - 1].Rows * oldLayouts[page - 1].Columns;

        targetLayouts = targetLayouts.Take(MaximumDeckPages).ToList();
        var targetStarts = new int[targetLayouts.Count];
        for (var page = 1; page < targetStarts.Length; page++)
            targetStarts[page] = targetStarts[page - 1] + targetLayouts[page - 1].Rows * targetLayouts[page - 1].Columns;
        var targetCapacity = targetLayouts.Sum(layout => layout.Rows * layout.Columns);
        var visible = new Jingle?[targetCapacity];
        var overflow = new List<Jingle>();

        foreach (var jingle in deck.Jingles.OrderBy(item => item.Position))
        {
            if (!jingle.HasContent) continue;
            var oldPage = oldLayouts.Length - 1;
            for (var page = 0; page < oldLayouts.Length; page++)
            {
                var end = oldStarts[page] + oldLayouts[page].Rows * oldLayouts[page].Columns;
                if (jingle.Position < end) { oldPage = page; break; }
            }
            var local = Math.Max(0, jingle.Position - oldStarts[oldPage]);
            var row = local / oldLayouts[oldPage].Columns;
            var column = local % oldLayouts[oldPage].Columns;
            if (oldPage < targetLayouts.Count && row < targetLayouts[oldPage].Rows && column < targetLayouts[oldPage].Columns)
            {
                var target = targetStarts[oldPage] + row * targetLayouts[oldPage].Columns + column;
                if (visible[target] is null) { visible[target] = jingle; continue; }
            }
            overflow.Add(jingle);
        }

        var overflowIndex = 0;
        for (var position = 0; position < visible.Length && overflowIndex < overflow.Count; position++)
            if (visible[position] is null) visible[position] = overflow[overflowIndex++];

        deck.Rows = targetLayouts[0].Rows;
        deck.Columns = targetLayouts[0].Columns;
        deck.PageCount = targetLayouts.Count;
        deck.PageLayouts = targetLayouts;
        deck.NotifyPageLayoutChanged();
        deck.ActivePage = Math.Clamp(deck.ActivePage, 0, deck.PageCount - 1);
        deck.Jingles.Clear();
        for (var position = 0; position < visible.Length; position++)
        {
            var jingle = visible[position] ?? new Jingle();
            jingle.Position = position;
            deck.Jingles.Add(jingle);
        }
        while (overflowIndex < overflow.Count)
        {
            var jingle = overflow[overflowIndex++];
            jingle.Position = deck.Jingles.Count;
            deck.Jingles.Add(jingle);
        }
    }

    public static void EnsureLayout(FloorballProject project)
    {
        project.Settings.RandomPoolShortcut = ShortcutService.Normalize(project.Settings.RandomPoolShortcut);
        project.Settings.AutoplayShortcut = ShortcutService.Normalize(project.Settings.AutoplayShortcut);
        project.Settings.AutoplayProfiles ??= [];
        if (project.Settings.AutoplayProfiles.Count == 0 &&
            (!string.IsNullOrWhiteSpace(project.Settings.AutoplayDefaultPlaylistPath) ||
             !string.IsNullOrWhiteSpace(project.Settings.AutoplayShortcut)))
        {
            project.Settings.AutoplayProfiles.Add(new AutoplayProfile
            {
                Name = "Standard",
                PlaylistPath = project.Settings.AutoplayDefaultPlaylistPath,
                Shortcut = project.Settings.AutoplayShortcut,
                VolumeDb = project.Settings.AutoplayDefaultPlaylistVolumeDb
            });
        }
        foreach (var profile in project.Settings.AutoplayProfiles)
        {
            if (profile.Id == Guid.Empty) profile.Id = Guid.NewGuid();
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Autoplay" : profile.Name.Trim();
            profile.PlaylistPath = string.IsNullOrWhiteSpace(profile.PlaylistPath) ? null : profile.PlaylistPath;
            profile.Shortcut = ShortcutService.Normalize(profile.Shortcut);
            profile.VolumeDb = Math.Clamp(profile.VolumeDb, -60, 12);
        }
        project.Settings.RandomPoolDeckIds ??= [];
        project.Settings.RandomPoolJingleIds ??= [];
        project.Settings.RandomPoolProfiles ??= [];
        if (project.Settings.RandomPoolProfiles.Count == 0 &&
            (!string.IsNullOrWhiteSpace(project.Settings.RandomPoolShortcut) ||
             project.Settings.RandomPoolDeckIds.Count > 0 || project.Settings.RandomPoolJingleIds.Count > 0))
        {
            project.Settings.RandomPoolProfiles.Add(new RandomPoolProfile
            {
                Name = "Slumpgrupp 1",
                Shortcut = project.Settings.RandomPoolShortcut,
                DeckIds = project.Settings.RandomPoolDeckIds.Distinct().ToList(),
                JingleIds = project.Settings.RandomPoolJingleIds.Distinct().ToList()
            });
        }
        foreach (var profile in project.Settings.RandomPoolProfiles)
        {
            if (profile.Id == Guid.Empty) profile.Id = Guid.NewGuid();
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Slumpgrupp" : profile.Name.Trim();
            profile.Shortcut = ShortcutService.Normalize(profile.Shortcut);
            profile.DeckIds = profile.DeckIds?.Distinct().ToList() ?? [];
            profile.JingleIds = profile.JingleIds?.Distinct().ToList() ?? [];
            profile.FollowUpJingleIds = profile.FollowUpJingleIds?.Distinct().ToList() ?? [];
            profile.FollowUpFadeOutSeconds = Math.Clamp(profile.FollowUpFadeOutSeconds, 0, 30);
            profile.FollowUpFadeInSeconds = Math.Clamp(profile.FollowUpFadeInSeconds, 0, 30);
        }
        project.Settings.RandomPoolSetups ??= [];
        if (project.Settings.RandomPoolSetups.Count == 0)
        {
            project.Settings.RandomPoolSetups.Add(new RandomPoolSetup
            {
                Name = "Standard",
                Profiles = project.Settings.RandomPoolProfiles
            });
        }
        foreach (var setup in project.Settings.RandomPoolSetups)
        {
            if (setup.Id == Guid.Empty) setup.Id = Guid.NewGuid();
            setup.Name = string.IsNullOrWhiteSpace(setup.Name) ? "Slumpprofil" : setup.Name.Trim();
            setup.Profiles ??= [];
            foreach (var profile in setup.Profiles)
            {
                if (profile.Id == Guid.Empty) profile.Id = Guid.NewGuid();
                profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Slumpgrupp" : profile.Name.Trim();
                profile.Shortcut = ShortcutService.Normalize(profile.Shortcut);
                profile.DeckIds = profile.DeckIds?.Distinct().ToList() ?? [];
                profile.JingleIds = profile.JingleIds?.Distinct().ToList() ?? [];
                profile.FollowUpJingleIds = profile.FollowUpJingleIds?.Distinct().ToList() ?? [];
                profile.FollowUpFadeOutSeconds = Math.Clamp(profile.FollowUpFadeOutSeconds, 0, 30);
                profile.FollowUpFadeInSeconds = Math.Clamp(profile.FollowUpFadeInSeconds, 0, 30);
            }
        }
        var activeRandomSetup = project.Settings.RandomPoolSetups
            .FirstOrDefault(setup => setup.Id == project.Settings.ActiveRandomPoolSetupId)
            ?? project.Settings.RandomPoolSetups[0];
        if (activeRandomSetup.Profiles.Count == 0 && project.Settings.RandomPoolProfiles.Count > 0)
            activeRandomSetup.Profiles = project.Settings.RandomPoolProfiles;
        project.Settings.ActiveRandomPoolSetupId = activeRandomSetup.Id;
        // Keep the old field synchronized so profiles remain readable by older builds.
        project.Settings.RandomPoolProfiles = activeRandomSetup.Profiles;
        project.Settings.TeamDeckProfiles ??= [];
        foreach (var team in project.Settings.TeamDeckProfiles)
        {
            if (team.Id == Guid.Empty) team.Id = Guid.NewGuid();
            team.Name = string.IsNullOrWhiteSpace(team.Name) ? "Team Deck" : team.Name.Trim();
            team.Shortcut = ShortcutService.Normalize(team.Shortcut);
            if (team.TransitionAtSeconds is < 0) team.TransitionAtSeconds = 0;
            team.DefaultFadeOutSeconds = Math.Clamp(team.DefaultFadeOutSeconds, 0, 30);
            team.PlayerFadeInSeconds = Math.Clamp(team.PlayerFadeInSeconds, 0, 30);
            team.Players ??= [];
            foreach (var player in team.Players)
            {
                if (player.Id == Guid.Empty) player.Id = Guid.NewGuid();
                player.Name = string.IsNullOrWhiteSpace(player.Name) ? "Spelare" : player.Name.Trim();
                player.Number = player.Number?.Trim() ?? "";
                if (player.StartSecondsOverride is < 0) player.StartSecondsOverride = 0;
                player.ButtonColor = string.IsNullOrWhiteSpace(player.ButtonColor) ? "#17304A" : player.ButtonColor;
                player.TextColor = string.IsNullOrWhiteSpace(player.TextColor) ? "#F3F7FC" : player.TextColor;
            }
        }
        while (project.Decks.Count < project.Settings.DeckCount)
            project.Decks.Add(new Deck { Name = $"Deck {project.Decks.Count + 1}" });

        foreach (var deck in project.Decks)
        {
            if (deck.Rows <= 0) deck.Rows = project.Settings.Rows;
            if (deck.Columns <= 0) deck.Columns = project.Settings.Columns;
            deck.Rows = Math.Clamp(deck.Rows, 1, MaximumDeckRows);
            deck.Columns = Math.Clamp(deck.Columns, 1, MaximumDeckColumns);
            deck.PageLayouts ??= [];
            if (deck.PageLayouts.Count > deck.PageCount) deck.PageCount = Math.Min(deck.PageLayouts.Count, MaximumDeckPages);
            deck.EnsurePageLayouts();
            foreach (var layout in deck.PageLayouts)
            {
                layout.Rows = Math.Clamp(layout.Rows, 1, MaximumDeckRows);
                layout.Columns = Math.Clamp(layout.Columns, 1, MaximumDeckColumns);
            }
            while (deck.Jingles.Count > deck.TotalCapacity && deck.PageCount < MaximumDeckPages)
            {
                deck.PageCount++;
                deck.EnsurePageLayouts();
            }
            deck.NotifyPageLayoutChanged();
            deck.ActivePage = Math.Clamp(deck.ActivePage, 0, deck.PageCount - 1);
            foreach (var jingle in deck.Jingles)
            {
                if (!jingle.HasAudio && string.Equals(jingle.Title, "Tom plats", StringComparison.OrdinalIgnoreCase))
                    jingle.Title = "";
                jingle.Shortcut = ShortcutService.Normalize(jingle.Shortcut);
            }
            while (deck.Jingles.Count < deck.TotalCapacity)
                deck.Jingles.Add(new Jingle { Position = deck.Jingles.Count });
            for (var index = 0; index < deck.Jingles.Count; index++) deck.Jingles[index].Position = index;
        }
    }
}

public sealed record ProjectRevision(string Path, DateTime Timestamp, string ProjectName, int DeckCount, int JingleCount,
    long FileSize, string ChangeDescription)
{
    public string TimestampText => Timestamp.ToString("yyyy-MM-dd  HH:mm:ss");
    public string Summary => $"{DeckCount} deck • {JingleCount} jinglar";
    public string SizeText => FileSize < 1024 ? $"{FileSize} B" : $"{FileSize / 1024d:0.#} KB";
}

public sealed class RevisionMetadata
{
    public string ChangeDescription { get; set; } = "Profilen ändrades";
}

public sealed class PortableBackupManifest
{
    public int FormatVersion { get; set; } = 2;
    public DateTimeOffset CreatedAt { get; set; }
    public string ProfileFile { get; set; } = "";
    public string ProjectName { get; set; } = "FloorballDJ-profil";
    public int MediaFileCount { get; set; }
    public int CustomFontCount { get; set; }
    public int RandomPoolProfileCount { get; set; }
    public int TeamDeckProfileCount { get; set; }
    public bool IncludesColorPresets { get; set; }
    public List<string> MissingFiles { get; set; } = [];
}

public sealed record PortableBackupResult(string Directory, string ProfilePath, int MediaFileCount,
    int CustomFontCount, int RandomPoolProfileCount, int TeamDeckProfileCount, bool IncludesColorPresets, IReadOnlyList<string> MissingFiles);

public sealed record PortableBackupProgress(string Phase, double Percent, int CompletedFiles = 0,
    int TotalFiles = 0, string? Detail = null);

public sealed record PortableRestoreResult(string ProfilePath, string Directory, int CustomFontCount,
    int ColorPresetCount, int RandomPoolProfileCount, int TeamDeckProfileCount, int MissingMediaCount);
