using System.Diagnostics;
using NAudio.Wave;

namespace FloorballDJ.Services;

/// <summary>Scalar metadata only. Readers are opened and disposed on one worker;
/// no decoder or live UI model crosses the boundary.</summary>
internal sealed class AudioMetadataService
{
    internal static AudioMetadataService Shared { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly int _capacity;
    private readonly TimeSpan _lifetime;
    private readonly Func<string, double> _readDuration;
    private sealed record CacheEntry(string Path, long Length, DateTime ModifiedUtc, double Duration, long Timestamp);

    internal AudioMetadataService(int capacity = 256, TimeSpan? lifetime = null, Func<string, double>? readDuration = null)
    {
        _capacity = Math.Max(0, capacity);
        _lifetime = lifetime ?? TimeSpan.FromSeconds(30);
        _readDuration = readDuration ?? ReadDuration;
    }

    internal async Task<AudioFileMetadata[]> ReadAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        // Copy the caller's sequence before yielding; only strings enter the worker.
        var snapshot = paths.ToArray();
        var performance = PerformanceDiagnostics.BeginOperation("MetadataRequested");
        using var total = performance.Measure("MetadataTotal");
        performance.Mark("MetadataItemCount", snapshot.Length);
        using (performance.Measure("MetadataQueueWait"))
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => snapshot.Select(path => Read(path, performance, cancellationToken)).ToArray(),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private AudioFileMetadata Read(string path, PerformanceOperation performance, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string key;
        FileInfo file;
        try
        {
            using var probe = performance.Measure("MetadataFileProbe");
            file = new FileInfo(path);
            key = file.FullName;
            if (!file.Exists)
            {
                Remove(key);
                performance.Mark("MetadataMissing");
                return new(path, false, null);
            }
        }
        catch (Exception exception)
        {
            performance.Mark("MetadataFailed", detail: exception.GetType().Name);
            return new(path, File.Exists(path), null);
        }

        try
        {
            var length = file.Length;
            var modified = file.LastWriteTimeUtc;
            if (_cache.TryGetValue(key, out var cached))
            {
                if (cached.Value.Length == length && cached.Value.ModifiedUtc == modified &&
                    Stopwatch.GetElapsedTime(cached.Value.Timestamp) < _lifetime)
                {
                    _lru.Remove(cached);
                    _lru.AddFirst(cached);
                    performance.Mark("MetadataCacheHit");
                    return new(path, true, cached.Value.Duration);
                }
                Remove(key);
            }

            cancellationToken.ThrowIfCancellationRequested();
            double duration;
            using (performance.Measure("MetadataDurationRead")) duration = _readDuration(path);
            cancellationToken.ThrowIfCancellationRequested();
            file.Refresh();
            // Do not cache a file replaced or removed while its decoder was opening.
            if (file.Exists && file.Length == length && file.LastWriteTimeUtc == modified && _capacity > 0)
            {
                var entry = _lru.AddFirst(new CacheEntry(key, length, modified, duration, Stopwatch.GetTimestamp()));
                _cache.Add(key, entry);
                while (_cache.Count > _capacity) Remove(_lru.Last!.Value.Path);
            }
            return new(path, true, duration);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Remove(key);
            performance.Mark("MetadataFailed", detail: exception.GetType().Name);
            return new(path, true, null);
        }
    }

    private void Remove(string path)
    {
        if (_cache.Remove(path, out var node)) _lru.Remove(node);
    }

    private static double ReadDuration(string path)
    {
        using var reader = new AudioFileReader(path);
        return reader.TotalTime.TotalSeconds;
    }
}

internal sealed record AudioFileMetadata(string Path, bool Exists, double? DurationSeconds);
