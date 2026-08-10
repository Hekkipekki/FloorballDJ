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

    public async Task<string> CreateMediaBackupAsync(FloorballProject project, string parentDirectory)
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
        var copiedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var jingle in copy.Decks.SelectMany(deck => deck.Jingles).Where(jingle => jingle.HasAudio))
        {
            var source = jingle.FilePath;
            if (!File.Exists(source)) continue;
            if (!copiedFiles.TryGetValue(source, out var relativePath))
            {
                var originalName = Path.GetFileName(source);
                var fileName = originalName;
                var index = 2;
                while (!usedNames.Add(fileName))
                    fileName = $"{Path.GetFileNameWithoutExtension(originalName)}-{index++}{Path.GetExtension(originalName)}";
                relativePath = Path.Combine("Media", fileName);
                copiedFiles[source] = relativePath;
                await using var input = File.OpenRead(source);
                await using var output = File.Create(Path.Combine(directory, relativePath));
                await input.CopyToAsync(output);
            }
            jingle.FilePath = relativePath;
        }

        var projectName = string.Concat(project.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        await SaveAsync(copy, Path.Combine(directory, $"{projectName}.floorballdj.json"));
        return directory;
    }

    public static FloorballProject CreateDefault()
    {
        var project = new FloorballProject();
        EnsureLayout(project);
        return project;
    }

    public static void EnsureLayout(FloorballProject project)
    {
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
