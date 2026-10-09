using System.Diagnostics;
using System.Text.Json;

namespace FloorballDJ.Services;

internal sealed record PlaylistEntry(string Title, string FilePath);
internal sealed record PlaylistDocument(int Version, bool ShuffleEnabled, bool LoopEnabled, List<PlaylistEntry>? Entries);
internal sealed record PlaylistDefinition(bool ShuffleEnabled, bool LoopEnabled, IReadOnlyList<PlaylistEntry> Entries);
internal sealed record PlaylistPreparation(PlaylistDefinition Definition, PlaylistEntry[] ExistingEntries, IReadOnlyDictionary<string, string> FolderFiles);

/// <summary>Owned playlist data only; file I/O never enumerates live UI models.</summary>
internal sealed class PlaylistService
{
    internal static PlaylistService Shared { get; } = new();
    internal static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".aiff", ".aif", ".wma", ".m4a", ".aac", ".flac", ".mp4", ".ogg" };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, LinkedListNode<Cached>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Cached> _lru = new();
    private readonly int _capacity;
    private readonly long _byteLimit;
    private readonly TimeSpan _lifetime;
    private readonly Func<string, string> _readText;
    private long _bytes;
    private sealed record Cached(string Path, long Length, DateTime ModifiedUtc, long Timestamp, long Bytes, PlaylistDefinition Definition);
    internal int CacheCount => _cache.Count;
    internal long CacheBytes => _bytes;

    internal PlaylistService(int capacity = 32, long byteLimit = 4 * 1024 * 1024, TimeSpan? lifetime = null,
        Func<string, string>? readText = null)
    {
        _capacity = Math.Max(0, capacity);
        _byteLimit = Math.Max(0, byteLimit);
        _lifetime = lifetime ?? TimeSpan.FromSeconds(30);
        _readText = readText ?? File.ReadAllText;
    }

    internal async Task<PlaylistPreparation?> PrepareAsync(string path, string? musicFolder,
        bool allowMissing = false, bool forceFresh = false, CancellationToken cancellationToken = default)
    {
        var performance = PerformanceDiagnostics.BeginOperation("PlaylistRequested");
        using var total = performance.Measure("PlaylistPreparation");
        using (performance.Measure("PlaylistQueueWait"))
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Prepare(path, musicFolder, allowMissing, forceFresh, performance, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private PlaylistPreparation? Prepare(string path, string? folder, bool allowMissing, bool forceFresh,
        PerformanceOperation performance, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (allowMissing && (string.IsNullOrWhiteSpace(path) || !File.Exists(path)))
        {
            if (!string.IsNullOrWhiteSpace(path)) Remove(Path.GetFullPath(path));
            return null;
        }
        var file = new FileInfo(path);
        var key = file.FullName;
        PlaylistDefinition definition;
        if (!forceFresh && file.Exists && _cache.TryGetValue(key, out var node) &&
            node.Value.Length == file.Length && node.Value.ModifiedUtc == file.LastWriteTimeUtc &&
            Stopwatch.GetElapsedTime(node.Value.Timestamp) < _lifetime)
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            definition = node.Value.Definition;
            performance.Mark("PlaylistCacheHit");
        }
        else
        {
            Remove(key);
            var length = file.Exists ? file.Length : -1;
            var modified = file.LastWriteTimeUtc;
            string json;
            using (performance.Measure("PlaylistRead")) json = _readText(path);
            token.ThrowIfCancellationRequested();
            using (performance.Measure("PlaylistParse"))
            {
                var document = ReadDocument(json);
                definition = new(document.ShuffleEnabled, document.LoopEnabled,
                    (document.Entries ?? []).Select(entry => entry with { FilePath = ResolveMediaPath(entry.FilePath, document.Version, path) }).ToArray());
            }
            // Conservative retained payload estimate, including strings/list/record overhead.
            var bytes = 256L + definition.Entries.Sum(entry => 128L + 2L * ((entry.Title?.Length ?? 0) + (entry.FilePath?.Length ?? 0)));
            file.Refresh();
            if (_capacity > 0 && bytes <= _byteLimit && file.Exists && file.Length == length && file.LastWriteTimeUtc == modified)
            {
                var added = _lru.AddFirst(new Cached(key, length, modified, Stopwatch.GetTimestamp(), bytes, definition));
                _cache.Add(key, added);
                _bytes += bytes;
                while (_cache.Count > _capacity || _bytes > _byteLimit) Remove(_lru.Last!.Value.Path);
            }
        }
        token.ThrowIfCancellationRequested();
        using var validation = performance.Measure("PlaylistValidation");
        var exists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<PlaylistEntry>();
        foreach (var entry in definition.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.FilePath)) continue;
            if (!exists.TryGetValue(entry.FilePath, out var found))
            {
                found = File.Exists(entry.FilePath);
                exists.Add(entry.FilePath, found);
                performance.Mark("PlaylistMediaProbe");
            }
            if (found) entries.Add(entry);
        }
        var folderFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            // Walk only the ancestors of playlist members. Literal enumeration paths
            // preserve RC1 matching (including relative paths and path spelling).
            var reachable = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [folder] = Directory.Exists(folder) ? folder : null };
            reachable[Path.TrimEndingDirectorySeparator(folder)] = reachable[folder];
            var children = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var files = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var prefix = Path.Combine(folder, "_")[..^1];
            string? ResolveDirectory(string directory)
            {
                token.ThrowIfCancellationRequested();
                if (reachable.TryGetValue(directory, out var known)) return known;
                var parent = Path.GetDirectoryName(directory);
                var resolvedParent = parent is null ? null : ResolveDirectory(parent);
                if (resolvedParent is null) return reachable[directory] = null;
                if (!children.TryGetValue(resolvedParent, out var listing))
                {
                    listing = ListDirectory(resolvedParent, directories: true, performance, token);
                    children.Add(resolvedParent, listing);
                }
                return reachable[directory] = listing.TryGetValue(directory, out var actual) ? actual : null;
            }
            foreach (var entry in entries.DistinctBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var candidate = entry.FilePath;
                if (!AudioExtensions.Contains(Path.GetExtension(candidate)) || !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var relative = candidate[prefix.Length..];
                if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "" or "." or "..") || relative.Contains(Path.AltDirectorySeparatorChar)) continue;
                var parent = Path.GetDirectoryName(candidate);
                // The immediate parent of a file under a trailing-separator root
                // need not have the separator returned by GetDirectoryName.
                if (relative.IndexOf(Path.DirectorySeparatorChar) < 0) parent = folder;
                var resolvedParent = parent is null ? null : ResolveDirectory(parent);
                if (resolvedParent is null) continue;
                if (!files.TryGetValue(resolvedParent, out var listing))
                {
                    listing = ListDirectory(resolvedParent, directories: false, performance, token);
                    files.Add(resolvedParent, listing);
                }
                if (listing.TryGetValue(candidate, out var actualFile)) folderFiles.Add(candidate, actualFile);
            }
        }
        performance.Mark("PlaylistEntryCount", entries.Count);
        return new(definition, entries.ToArray(), folderFiles);
    }

    private static HashSet<string> ListDirectory(string directory, bool directories, PerformanceOperation performance, CancellationToken token)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            performance.Mark("PlaylistTargetDirectory");
            foreach (var item in directories ? Directory.EnumerateDirectories(directory) : Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                result.Add(item);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return result;
    }

    internal static PlaylistDocument ReadDocument(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.ValueKind == JsonValueKind.Array
            ? new(1, false, true, JsonSerializer.Deserialize<List<PlaylistEntry>>(json) ?? [])
            : JsonSerializer.Deserialize<PlaylistDocument>(json) ?? new(2, false, true, []);
    }

    internal static string ResolveMediaPath(string path, int version, string playlistPath)
        => version >= 3 && !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path)
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(playlistPath))!, path)) : path;

    internal async Task SaveAsync(string path, PlaylistDocument document)
    {
        var snapshot = document with { Entries = document.Entries?.ToList() };
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                Remove(Path.GetFullPath(path));
                File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void Remove(string key)
    {
        if (!_cache.Remove(key, out var node)) return;
        _lru.Remove(node);
        _bytes -= node.Value.Bytes;
    }
}

/// <summary>A bounded OS-cache hint only. No decoder, PCM or handle is retained.</summary>
internal sealed class MediaPrefixPrefetcher
{
    internal static MediaPrefixPrefetcher Shared { get; } = new();
    internal const int MaximumBytes = 256 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal async Task<int> ReadAsync(string path, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var performance = PerformanceDiagnostics.BeginOperation("QueuePrefetchRequested");
                using var duration = performance.Measure("QueuePrefetchRead");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[16 * 1024];
                var count = 0;
                while (count < MaximumBytes)
                {
                    token.ThrowIfCancellationRequested();
                    var read = stream.Read(buffer, 0, Math.Min(buffer.Length, MaximumBytes - count));
                    if (read == 0) break;
                    count += read;
                }
                performance.Mark("QueuePrefetchBytes", count);
                return count;
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
