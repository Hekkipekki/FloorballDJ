using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using FloorballDJ.Models;

namespace FloorballDJ.Services;

public sealed class ProjectService
{
    public const int MaximumDeckRows = 50;
    public const int MaximumDeckColumns = 12;
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
                revisions.Add(new ProjectRevision(path, ParseRevisionTimestamp(file), project.Name,
                    project.Decks.Count, project.Decks.Sum(deck => deck.Jingles.Count(jingle => jingle.HasAudio)), file.Length));
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

        foreach (var oldRevision in Directory.EnumerateFiles(directory, "*.floorballdj.json")
                     .OrderByDescending(Path.GetFileName).Skip(100))
            try { File.Delete(oldRevision); } catch { }
    }

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

    public async Task<PortableBackupResult> CreateMediaBackupAsync(FloorballProject project, string parentDirectory)
    {
        var baseName = $"FloorballDJ-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        var directory = Path.Combine(parentDirectory, baseName);
        var suffix = 2;
        while (Directory.Exists(directory)) directory = Path.Combine(parentDirectory, $"{baseName}-{suffix++}");
        var mediaDirectory = Path.Combine(directory, "Media");
        Directory.CreateDirectory(mediaDirectory);

        var json = JsonSerializer.Serialize(project, JsonOptions);
        var copy = JsonSerializer.Deserialize<FloorballProject>(json, JsonOptions)
            ?? throw new InvalidDataException("Projektet kunde inte kopieras.");
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
                }
                jingle.FilePath = relativePath;
            }
        }

        // Maskinspecifika enhets-ID:n ska inte följa med till nästa dator. Alla andra
        // ljud-, layout- och arbetsinställningar ligger kvar i den portabla profilen.
        copy.Settings.OutputDeviceId = null;
        copy.Settings.SecondaryOutputDeviceId = null;
        copy.Settings.MusicFolderPath = "Media";

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
            IncludesColorPresets = presetsIncluded,
            MissingFiles = missingFiles
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "floorballdj-backup.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));
        var missingSummary = missingFiles.Count == 0
            ? "Inga länkade ljudfiler saknades när backupen skapades."
            : $"VARNING: {missingFiles.Count} länkade ljudfiler saknades:\r\n- {string.Join("\r\n- ", missingFiles)}";
        await File.WriteAllTextAsync(Path.Combine(directory, "LÄS MIG - ÅTERSTÄLL BACKUP.txt"),
            $"FloorballDJ flyttbackup\r\nSkapad: {manifest.CreatedAt:yyyy-MM-dd HH:mm:ss zzz}\r\nProfil: {project.Name}\r\n\r\n" +
            "På den andra datorn:\r\n1. Installera och starta FloorballDJ.\r\n2. Välj Profil > Återställ flyttbackup.\r\n3. Välj den här mappen.\r\n4. Välj datorns ljudutgångar under Verktyg > Inställningar.\r\n\r\n" +
            "Licens/provperiod är maskinbunden och följer inte med. Ljudfilerna är rena filkopior utan omkodning.\r\n\r\n" +
            missingSummary);

        return new PortableBackupResult(directory, Path.Combine(directory, profileFileName), copiedMediaCount,
            fontsIncluded, presetsIncluded, missingFiles);
    }

    public async Task<PortableRestoreResult> RestorePortableBackupAsync(string backupDirectory)
    {
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
        await CopyDirectoryAsync(sourceDirectory, destinationDirectory);

        var profilePath = SafeChildPath(destinationDirectory, manifest.ProfileFile);
        var fontsImported = ImportFonts(Path.Combine(destinationDirectory, "Inställningar", "Fonts"));
        var presetsImported = new ColorPresetService().MergeFrom(
            Path.Combine(destinationDirectory, "Inställningar", "color-presets.json"));

        var restoredProject = await LoadAsync(profilePath);
        restoredProject.Settings.OutputDeviceId = null;
        restoredProject.Settings.SecondaryOutputDeviceId = null;
        await SaveAsync(restoredProject, profilePath);
        var missingCount = restoredProject.Decks.SelectMany(deck => deck.Jingles).Count(jingle => jingle.IsMissing);
        return new PortableRestoreResult(profilePath, destinationDirectory, fontsImported, presetsImported, missingCount);
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

    private static async Task CopyDirectoryAsync(string sourceDirectory, string destinationDirectory)
    {
        await Task.Run(() =>
        {
            foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
            Directory.CreateDirectory(destinationDirectory);
            foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, false);
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
        var slots = Math.Clamp(rows, 1, MaximumDeckRows) * Math.Clamp(columns, 1, MaximumDeckColumns);
        return Math.Max(0, deck.Jingles.Count(jingle => jingle.HasContent) - slots);
    }

    public static void ResizeDeckLayout(Deck deck, int rows, int columns)
    {
        var oldRows = Math.Clamp(deck.Rows, 1, MaximumDeckRows);
        var oldColumns = Math.Clamp(deck.Columns, 1, MaximumDeckColumns);
        var newRows = Math.Clamp(rows, 1, MaximumDeckRows);
        var newColumns = Math.Clamp(columns, 1, MaximumDeckColumns);
        var newSlots = newRows * newColumns;
        var visible = new Jingle?[newSlots];
        var overflowContent = new List<Jingle>();

        // Preserve the physical row/column for every cell that still fits. This means
        // fewer columns trim the right edge and fewer rows trim the bottom edge.
        foreach (var jingle in deck.Jingles.OrderBy(jingle => jingle.Position))
        {
            var oldPosition = jingle.Position;
            if (oldPosition >= 0 && oldPosition < oldRows * oldColumns)
            {
                var row = oldPosition / oldColumns;
                var column = oldPosition % oldColumns;
                if (row < newRows && column < newColumns)
                {
                    var newPosition = row * newColumns + column;
                    if (visible[newPosition] is null)
                    {
                        visible[newPosition] = jingle;
                        continue;
                    }
                }
            }

            if (jingle.HasContent) overflowContent.Add(jingle);
        }

        // Audio outside the new right/bottom edges is moved only into genuinely empty
        // visible cells. If capacity is still insufficient it remains hidden after the
        // visible range, so resizing never deletes a jingle.
        var overflowIndex = 0;
        for (var position = 0; position < visible.Length && overflowIndex < overflowContent.Count; position++)
        {
            if (visible[position]?.HasContent == true) continue;
            visible[position] = overflowContent[overflowIndex++];
        }

        deck.Rows = newRows;
        deck.Columns = newColumns;
        deck.Jingles.Clear();
        for (var position = 0; position < visible.Length; position++)
        {
            var jingle = visible[position] ?? new Jingle();
            jingle.Position = position;
            deck.Jingles.Add(jingle);
        }
        while (overflowIndex < overflowContent.Count)
        {
            var jingle = overflowContent[overflowIndex++];
            jingle.Position = deck.Jingles.Count;
            deck.Jingles.Add(jingle);
        }
    }

    public static void EnsureLayout(FloorballProject project)
    {
        project.Settings.RandomPoolShortcut = ShortcutService.Normalize(project.Settings.RandomPoolShortcut);
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
        }
        while (project.Decks.Count < project.Settings.DeckCount)
            project.Decks.Add(new Deck { Name = $"Deck {project.Decks.Count + 1}" });

        foreach (var deck in project.Decks)
        {
            if (deck.Rows <= 0) deck.Rows = project.Settings.Rows;
            if (deck.Columns <= 0) deck.Columns = project.Settings.Columns;
            deck.Rows = Math.Clamp(deck.Rows, 1, MaximumDeckRows);
            deck.Columns = Math.Clamp(deck.Columns, 1, MaximumDeckColumns);
            var slots = deck.Rows * deck.Columns;
            foreach (var jingle in deck.Jingles)
            {
                if (!jingle.HasAudio && string.Equals(jingle.Title, "Tom plats", StringComparison.OrdinalIgnoreCase))
                    jingle.Title = "";
                jingle.Shortcut = ShortcutService.Normalize(jingle.Shortcut);
            }
            while (deck.Jingles.Count < slots)
                deck.Jingles.Add(new Jingle { Position = deck.Jingles.Count });
        }
    }
}

public sealed record ProjectRevision(string Path, DateTime Timestamp, string ProjectName, int DeckCount, int JingleCount, long FileSize)
{
    public string TimestampText => Timestamp.ToString("yyyy-MM-dd  HH:mm:ss");
    public string Summary => $"{DeckCount} deck • {JingleCount} jinglar";
    public string SizeText => FileSize < 1024 ? $"{FileSize} B" : $"{FileSize / 1024d:0.#} KB";
}

public sealed class PortableBackupManifest
{
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public string ProfileFile { get; set; } = "";
    public string ProjectName { get; set; } = "FloorballDJ-profil";
    public int MediaFileCount { get; set; }
    public int CustomFontCount { get; set; }
    public bool IncludesColorPresets { get; set; }
    public List<string> MissingFiles { get; set; } = [];
}

public sealed record PortableBackupResult(string Directory, string ProfilePath, int MediaFileCount,
    int CustomFontCount, bool IncludesColorPresets, IReadOnlyList<string> MissingFiles);

public sealed record PortableRestoreResult(string ProfilePath, string Directory, int CustomFontCount,
    int ColorPresetCount, int MissingMediaCount);
