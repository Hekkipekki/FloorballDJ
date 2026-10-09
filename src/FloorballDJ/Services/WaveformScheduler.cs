using NAudio.Wave;

namespace FloorballDJ.Services;

internal readonly record struct WavePeak(float Minimum, float Maximum);

/// <summary>One decoder, bounded admitted jobs, shared consumers, and a byte/count bounded LRU.</summary>
internal sealed class WaveformScheduler : IDisposable
{
    internal static WaveformScheduler Shared { get; } = new();
    internal sealed class Job(string key, string path, PerformanceOperation performance)
    {
        internal readonly string Key = key;
        internal readonly string Path = path;
        internal readonly PerformanceOperation Performance = performance;
        internal readonly long QueuedAt = performance.Timestamp;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<WavePeak[]> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Job>? Node;
        internal int Consumers;
        internal int VisibleConsumers;
        internal bool QueuedVisible;
        internal bool Finished;
    }

    private sealed record CacheEntry(string Key, WavePeak[] Peaks)
    {
        internal long Bytes => (long)Peaks.Length * 2 * sizeof(float);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Job> _pending = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _recency = new();
    private readonly Func<string, CancellationToken, WavePeak[]> _read;
    private readonly long _maximumCacheBytes;
    private readonly int _maximumCacheEntries;
    private readonly int _maximumJobs;
    private TaskCompletionSource _capacityChanged = NewSignal();
    private Job? _active;
    private bool _workerRunning;
    private Task _worker = Task.CompletedTask;
    private int _waitingAdmissions;
    private long _cachedBytes;
    private bool _disposed;

    internal WaveformScheduler(Func<string, CancellationToken, WavePeak[]>? read = null,
        long maximumCacheBytes = 32 * 1024 * 1024, int maximumCacheEntries = 128, int maximumJobs = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCacheBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCacheEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumJobs, 1);
        _read = read ?? WaveformPeakReader.Read;
        _maximumCacheBytes = maximumCacheBytes;
        _maximumCacheEntries = maximumCacheEntries;
        _maximumJobs = maximumJobs;
    }

    internal static string CacheKey(string path)
    {
        var info = new FileInfo(path);
        return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    internal async Task<Request> AcquireAsync(string key, string path, bool visible, CancellationToken cancellationToken)
    {
        var performance = PerformanceDiagnostics.BeginOperation("WaveformRequestQueued");
        while (true)
        {
            Task capacity;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (_cache.TryGetValue(key, out var cached))
                {
                    _recency.Remove(cached);
                    _recency.AddFirst(cached);
                    performance.Mark("WaveformCacheHit");
                    return new Request(Task.FromResult(cached.Value.Peaks), cancellationToken);
                }
                if (_jobs.TryGetValue(key, out var existing))
                {
                    performance.Mark("WaveformSharedJob");
                    return Attach(existing, visible, cancellationToken);
                }
                if (_pending.Count + (_active is null ? 0 : 1) < _maximumJobs)
                {
                    performance.Mark("WaveformCacheMiss");
                    var job = new Job(key, path, performance);
                    _jobs.Add(key, job);
                    job.Node = _pending.AddLast(job);
                    var request = Attach(job, visible, cancellationToken);
                    RecordState(performance);
                    if (!_workerRunning)
                    {
                        _workerRunning = true;
                        _worker = Task.Run(ProcessQueue);
                    }
                    return request;
                }
                // Do not create another decoder/task while full. Each view's superseded
                // request cancels its admission wait, just like an admitted consumer.
                capacity = _capacityChanged.Task;
                _waitingAdmissions++;
                performance.Mark("WaveformAdmissionWait");
                RecordState(performance);
            }
            try { await capacity.WaitAsync(cancellationToken).ConfigureAwait(false); }
            finally
            {
                lock (_gate)
                {
                    _waitingAdmissions--;
                    RecordState(performance);
                }
            }
        }
    }

    private Request Attach(Job job, bool visible, CancellationToken cancellationToken)
    {
        job.Consumers++;
        if (visible) job.VisibleConsumers++;
        Reprioritize(job);
        return new Request(this, job, visible, cancellationToken);
    }

    private void Reprioritize(Job job)
    {
        var visible = job.VisibleConsumers > 0;
        if (job.Node is null || job.QueuedVisible == visible) return;
        job.QueuedVisible = visible;
        _pending.Remove(job.Node);
        // Stable FIFO within each priority class. A newly visible request does not
        // interrupt a decoder whose other consumers still need its result.
        var before = _pending.First;
        if (visible)
            while (before is not null && before.Value.VisibleConsumers > 0) before = before.Next;
        else before = null;
        job.Node = before is null ? _pending.AddLast(job) : _pending.AddBefore(before, job);
    }

    private void Release(Job job, bool visible)
    {
        bool dispose = false;
        lock (_gate)
        {
            job.Consumers--;
            if (visible) job.VisibleConsumers--;
            if (job.Finished) return;
            if (job.Consumers > 0) { Reprioritize(job); return; }
            RemoveJob(job);
            if (job.Node is not null)
            {
                _pending.Remove(job.Node);
                job.Node = null;
                job.Finished = true;
                dispose = true;
                SignalCapacity();
            }
            job.Performance.Mark("WaveformOrphanCancelled");
            RecordState(job.Performance);
        }
        Cancel(job);
        job.Completion.TrySetCanceled();
        if (dispose) job.Cancellation.Dispose();
    }

    private void ChangePriority(Job job, bool oldVisible, bool visible)
    {
        lock (_gate)
        {
            if (job.Finished || oldVisible == visible) return;
            job.VisibleConsumers += visible ? 1 : -1;
            Reprioritize(job);
        }
    }

    private void ProcessQueue()
    {
        while (true)
        {
            Job job;
            lock (_gate)
            {
                if (_pending.First is null)
                {
                    _workerRunning = false;
                    return;
                }
                job = _pending.First.Value;
                _pending.RemoveFirst();
                job.Node = null;
                _active = job;
                job.Performance.Duration("WaveformQueueDelay", job.QueuedAt);
                job.Performance.Mark("WaveformDecodeRequested");
                RecordState(job.Performance);
            }
            WavePeak[]? peaks = null;
            Exception? failure = null;
            try
            {
                using var duration = job.Performance.Measure("WaveformDecode");
                job.Cancellation.Token.ThrowIfCancellationRequested();
                peaks = _read(job.Path, job.Cancellation.Token);
                job.Cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (Exception exception) { failure = exception; }
            lock (_gate)
            {
                _active = null;
                job.Finished = true;
                RemoveJob(job);
                // A decoder that ignores cancellation must still never publish/cache
                // an orphan's result. The current consumer can always retry a miss.
                if (failure is null && peaks is not null && job.Consumers > 0 && !_disposed)
                    Retain(job.Key, peaks);
                else if (failure is null) failure = new OperationCanceledException();
                SignalCapacity();
                RecordState(job.Performance);
            }
            if (failure is OperationCanceledException) job.Completion.TrySetCanceled();
            else if (failure is not null)
            {
                job.Performance.Mark("WaveformDecodeFailed", detail: failure.GetType().Name);
                job.Completion.TrySetException(failure);
                _ = job.Completion.Task.Exception; // Observe even if all waiters have left.
            }
            else job.Completion.TrySetResult(peaks!);
            job.Cancellation.Dispose();
        }
    }

    private void Retain(string key, WavePeak[] peaks)
    {
        var entry = new CacheEntry(key, peaks);
        if (entry.Bytes > _maximumCacheBytes) return;
        while (_recency.Last is not null &&
            (_cache.Count >= _maximumCacheEntries || _cachedBytes + entry.Bytes > _maximumCacheBytes))
        {
            var oldest = _recency.Last.Value;
            _cachedBytes -= oldest.Bytes;
            _cache.Remove(oldest.Key);
            _recency.RemoveLast();
        }
        _cache.Add(key, _recency.AddFirst(entry));
        _cachedBytes += entry.Bytes;
    }

    private void RemoveJob(Job job)
    {
        if (_jobs.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job)) _jobs.Remove(job.Key);
    }

    private static void Cancel(Job job)
    {
        try { job.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { } // Completion/disposal may win this race.
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void SignalCapacity()
    {
        var previous = _capacityChanged;
        _capacityChanged = NewSignal();
        previous.TrySetResult();
    }

    internal (int Active, int Pending, int WaitingAdmissions, int CachedEntries, long CachedBytes) State
    {
        get { lock (_gate) return (_active is null ? 0 : 1, _pending.Count, _waitingAdmissions, _cache.Count, _cachedBytes); }
    }
    internal Task WhenIdleAsync() { lock (_gate) return _worker; }

    private void RecordState(PerformanceOperation performance)
    {
        performance.Mark("WaveformActiveDecoders", _active is null ? 0 : 1);
        performance.Mark("WaveformPendingJobs", _pending.Count);
        performance.Mark("WaveformWaitingAdmissions", _waitingAdmissions);
        performance.Mark("WaveformCachedBytes", _cachedBytes);
    }

    public void Dispose()
    {
        Job[] jobs;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            jobs = _pending.Concat(_active is null ? [] : new[] { _active }).ToArray();
            foreach (var job in _pending) { job.Node = null; job.Finished = true; }
            _pending.Clear();
            _jobs.Clear();
            _cache.Clear();
            _recency.Clear();
            _cachedBytes = 0;
            SignalCapacity();
        }
        foreach (var job in jobs)
        {
            Cancel(job);
            job.Completion.TrySetCanceled();
            if (job.Finished) job.Cancellation.Dispose();
        }
    }

    internal sealed class Request : IDisposable
    {
        private readonly object _gate = new();
        private readonly WaveformScheduler? _owner;
        private readonly Job? _job;
        private CancellationTokenRegistration _registration;
        private bool _visible;
        private bool _released;
        internal Task<WavePeak[]> Task { get; }

        internal Request(Task<WavePeak[]> task, CancellationToken token) => Task = task.WaitAsync(token);
        internal Request(WaveformScheduler owner, Job job, bool visible, CancellationToken token)
        {
            _owner = owner; _job = job; _visible = visible;
            Task = job.Completion.Task.WaitAsync(token);
            _registration = token.Register(static state => ((Request)state!).Release(), this);
        }
        internal void SetVisible(bool visible)
        {
            lock (_gate)
            {
                if (_released || _owner is null) return;
                _owner.ChangePriority(_job!, _visible, visible);
                _visible = visible;
            }
        }
        private void Release()
        {
            lock (_gate)
            {
                if (_released) return;
                _released = true;
                _owner?.Release(_job!, _visible);
            }
        }
        public void Dispose()
        {
            Release();
            _registration.Dispose();
        }
    }
}

internal static class WaveformPeakReader
{
    internal static WavePeak[] Read(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new AudioFileReader(path);
        return Read(reader, reader.TotalTime, cancellationToken);
    }

    internal static WavePeak[] Read(ISampleProvider reader, TimeSpan duration, CancellationToken cancellationToken)
    {
        var count = Math.Clamp((int)Math.Ceiling(duration.TotalSeconds * 80), 6000, 240000);
        var totalSamples = Math.Max(1L, (long)Math.Ceiling(duration.TotalSeconds *
            reader.WaveFormat.SampleRate * reader.WaveFormat.Channels));
        var samplesPerPeak = Math.Max(1L, totalSamples / count);
        var result = new List<WavePeak>(count);
        var buffer = new float[65536];
        long accumulated = 0;
        float minimum = 0, maximum = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (read <= 0) break;
            for (var i = 0; i < read; i++)
            {
                minimum = Math.Min(minimum, buffer[i]);
                maximum = Math.Max(maximum, buffer[i]);
                accumulated++;
                if (accumulated < samplesPerPeak) continue;
                result.Add(new WavePeak(minimum, maximum));
                accumulated = 0;
                minimum = maximum = 0;
            }
        }
        if (accumulated > 0) result.Add(new WavePeak(minimum, maximum));
        cancellationToken.ThrowIfCancellationRequested();
        return [.. result];
    }
}
