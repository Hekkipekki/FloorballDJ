using System.Diagnostics;
using NAudio.Wave;

namespace FloorballDJ.Services;

internal interface IVoiceOutput : IDisposable
{
    PlaybackState PlaybackState { get; }
    event EventHandler<StoppedEventArgs>? PlaybackStopped;
    bool StopConfirmed { get; }
    bool TryConsumeStopped(out Exception? error);
    void Play();
    void Pause();
    void Stop();
    void Discard();
}

/// <summary>Consume each terminal playback event once, including when a loop restarts.</summary>
internal sealed class OutputStopEpoch
{
    private int _play, _stopped, _consumed;
    private Exception? _error;
    internal void Start() => Interlocked.Increment(ref _play);
    internal void Record(Exception? error)
    {
        _error = error;
        Volatile.Write(ref _stopped, Volatile.Read(ref _play));
    }
    internal bool TryConsume(out Exception? error)
    {
        error = null;
        var epoch = Volatile.Read(ref _stopped);
        var consumed = Volatile.Read(ref _consumed);
        if (epoch != Volatile.Read(ref _play) || epoch <= consumed ||
            Interlocked.CompareExchange(ref _consumed, epoch, consumed) != consumed) return false;
        error = _error;
        return true;
    }
}

/// <summary>Fresh per-voice output creation with the shared completion/stop guard.</summary>
internal sealed class DirectVoiceOutput : IVoiceOutput
{
    private readonly IWavePlayer _output;
    private readonly ManualResetEventSlim _stopped = new(true);
    private readonly OutputStopEpoch _epoch = new();
    private readonly EventHandler<StoppedEventArgs> _handler;
    private int _disposed;
    internal DirectVoiceOutput(IWavePlayer output)
    {
        _output = output;
        var context = SynchronizationContext.Current;
        _handler = (_, args) =>
        {
            var callback = PlaybackStopped;
            _epoch.Record(args.Exception);
            _stopped.Set();
            if (callback is null) return;
            if (context is not null) context.Post(_ => callback(this, args), null);
            else ThreadPool.QueueUserWorkItem(_ => callback(this, args));
        };
        _output.PlaybackStopped += _handler;
    }
    public PlaybackState PlaybackState => _output.PlaybackState;
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public bool StopConfirmed => _stopped.IsSet;
    public bool TryConsumeStopped(out Exception? error) => _epoch.TryConsume(out error);
    public void Play()
    {
        if (_output.PlaybackState == PlaybackState.Stopped)
        {
            _stopped.Wait(); _epoch.Start(); _stopped.Reset();
        }
        try { _output.Play(); }
        catch { _stopped.Set(); throw; }
    }
    public void Pause() => _output.Pause();
    public void Stop() { _output.Stop(); _stopped.Wait(); }
    public void Discard() { }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Stop(); }
        finally
        {
            _output.PlaybackStopped -= _handler;
            _output.Dispose();
            _stopped.Dispose();
            PlaybackStopped = null;
        }
    }
}

/// <summary>
/// Opt-in prototype: reuse only stopped, reset outputs with identical route/endpoint/source
/// format. Each simultaneous voice still owns its output and unchanged sample pipeline.
/// </summary>
internal sealed class AudioOutputPool : IDisposable
{
    internal static bool Requested => Environment.GetCommandLineArgs().Contains("--reuse-audio-outputs", StringComparer.OrdinalIgnoreCase);
    internal readonly record struct Key(string Endpoint, bool Secondary, int Rate, int Channels, int Bits,
        WaveFormatEncoding Encoding, int BlockAlign, int AverageBytes, int ExtraSize);
    internal sealed class SourceSwitch(WaveFormat format) : IWaveProvider
    {
        private readonly object _gate = new();
        private IWaveProvider? _source;
        public WaveFormat WaveFormat { get; } = format;
        internal void Bind(IWaveProvider? source)
        {
            if (source is not null && !source.WaveFormat.Equals(WaveFormat))
                throw new ArgumentException("A reusable output requires an identical source format.", nameof(source));
            lock (_gate) _source = source;
        }
        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_gate) return _source?.Read(buffer, offset, count) ?? 0;
        }
    }
    internal sealed class Slot(IWavePlayer output, SourceSwitch source, long generation)
    {
        internal readonly IWavePlayer Output = output;
        internal readonly SourceSwitch Source = source;
        internal readonly long Generation = generation;
        internal readonly ManualResetEventSlim Stopped = new(true);
        internal Lease? Current;
        internal volatile bool Faulted;
        internal long ReturnedAt;
        internal EventHandler<StoppedEventArgs>? Handler;
    }

    private readonly object _gate = new();
    private readonly List<(Key Key, Slot Slot)> _idle = [];
    private readonly int _maximumIdle;
    private readonly long _lifetimeTicks;
    private readonly Func<long> _timestamp;
    private readonly Action<Action> _dispatch;
    private readonly Action<Action>? _callbackDispatch;
    private readonly Timer? _timer;
    private long _generation;
    private bool _disposed;

    internal AudioOutputPool(int maximumIdle = 2, TimeSpan? idleLifetime = null,
        Func<long>? timestamp = null, Action<Action>? dispatch = null, bool startTimer = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumIdle, 1);
        _maximumIdle = maximumIdle;
        var lifetime = idleLifetime ?? TimeSpan.FromSeconds(30);
        ArgumentOutOfRangeException.ThrowIfLessThan(lifetime, TimeSpan.Zero);
        _lifetimeTicks = (long)(lifetime.TotalSeconds * Stopwatch.Frequency);
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _callbackDispatch = dispatch;
        _dispatch = dispatch ?? (action => ThreadPool.QueueUserWorkItem(static work => work(), action, preferLocal: false));
        if (startTimer) _timer = new Timer(_ => EvictExpired(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    internal IVoiceOutput Acquire(string endpoint, bool secondary, IWaveProvider source,
        Func<IWavePlayer> createOutput, PerformanceOperation performance = default)
    {
        var format = source.WaveFormat;
        var key = new Key(endpoint, secondary, format.SampleRate, format.Channels, format.BitsPerSample,
            format.Encoding, format.BlockAlign, format.AverageBytesPerSecond, format.ExtraSize);
        EvictExpired();
        Slot? slot = null;
        long generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = _generation;
            for (var index = _idle.Count - 1; index >= 0; index--)
                if (_idle[index].Key == key)
                {
                    slot = _idle[index].Slot;
                    _idle.RemoveAt(index);
                    break;
                }
        }
        if (slot is not null)
        {
            performance.Mark("OutputReused");
            var lease = new Lease(this, slot, key, performance);
            slot.Current = lease;
            slot.Source.Bind(source);
            return lease;
        }
        performance.Mark("OutputCreated");
        IWavePlayer? output = null;
        try
        {
            using (performance.Measure("OutputConstruction")) output = createOutput();
            slot = new Slot(output, new SourceSwitch(format), generation);
            var lease = new Lease(this, slot, key, performance);
            slot.Current = lease;
            slot.Source.Bind(source);
            slot.Handler = (_, args) =>
            {
                // The WASAPI factory deliberately captures no UI synchronization
                // context. Snapshot the old lease BEFORE signalling safe handoff.
                var stoppedLease = Volatile.Read(ref slot.Current);
                var callback = stoppedLease?.GetStoppedHandler();
                if (args.Exception is not null) slot.Faulted = true;
                stoppedLease?.RecordStopped(args.Exception);
                slot.Stopped.Set();
                if (callback is not null) stoppedLease!.Notify(() => callback(stoppedLease, args));
            };
            output.PlaybackStopped += slot.Handler;
            using (performance.Measure("OutputInitialization")) output.Init(slot.Source);
            return lease;
        }
        catch
        {
            if (slot is not null)
            {
                if (slot.Handler is not null) slot.Output.PlaybackStopped -= slot.Handler;
                slot.Source.Bind(null);
                slot.Stopped.Dispose();
            }
            output?.Dispose();
            throw;
        }
    }

    private void Return(Slot slot, Key key, PerformanceOperation performance)
    {
        var keep = !slot.Faulted;
        try
        {
            slot.Output.Stop();
            // PlaybackState changes to Stopped just before the native Reset call.
            // The synchronous backend event confirms it has actually finished.
            slot.Stopped.Wait();
        }
        catch { keep = false; }
        slot.Source.Bind(null);
        slot.Current = null;
        List<Slot> retire = [];
        lock (_gate)
        {
            keep &= !_disposed && !slot.Faulted && slot.Generation == _generation;
            if (keep)
            {
                slot.ReturnedAt = _timestamp();
                _idle.Add((key, slot));
                while (_idle.Count > _maximumIdle)
                {
                    retire.Add(_idle[0].Slot);
                    _idle.RemoveAt(0);
                }
                performance.Mark("OutputRetained");
                performance.Mark("IdleOutputCount", _idle.Count);
            }
            else retire.Add(slot);
        }
        foreach (var retired in retire) Retire(retired);
    }

    internal int IdleCount { get { lock (_gate) return _idle.Count; } }
    internal void EvictExpired()
    {
        List<Slot> retire = [];
        lock (_gate)
        {
            var now = _timestamp();
            for (var index = _idle.Count - 1; index >= 0; index--)
                if (_idle[index].Slot.Faulted || now - _idle[index].Slot.ReturnedAt >= _lifetimeTicks)
                {
                    retire.Add(_idle[index].Slot);
                    _idle.RemoveAt(index);
                }
        }
        foreach (var slot in retire) Retire(slot);
    }

    internal void Invalidate(bool disposeAsynchronously = false)
    {
        Slot[] retire;
        lock (_gate)
        {
            _generation++;
            retire = _idle.Select(entry => entry.Slot).ToArray();
            _idle.Clear();
        }
        void DisposeRetired() { foreach (var slot in retire) Retire(slot); }
        if (disposeAsynchronously && retire.Length > 0) _dispatch(DisposeRetired);
        else DisposeRetired();
    }

    private static void Retire(Slot slot)
    {
        if (slot.Handler is not null) slot.Output.PlaybackStopped -= slot.Handler;
        try { slot.Output.Dispose(); } catch { }
        slot.Stopped.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer?.Dispose();
        Invalidate();
    }

    internal sealed class Lease : IVoiceOutput
    {
        private readonly AudioOutputPool _owner;
        private readonly Slot _slot;
        private readonly Key _key;
        private readonly PerformanceOperation _performance;
        internal readonly Action<Action> Notify;
        private int _disposed;
        private readonly OutputStopEpoch _epoch = new();
        internal Lease(AudioOutputPool owner, Slot slot, Key key, PerformanceOperation performance)
        {
            _owner = owner; _slot = slot; _key = key; _performance = performance;
            var context = SynchronizationContext.Current;
            Notify = owner._callbackDispatch ?? (context is null ? owner._dispatch :
                action => context.Post(static state => ((Action)state!)(), action));
        }
        public PlaybackState PlaybackState => _slot.Output.PlaybackState;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        internal EventHandler<StoppedEventArgs>? GetStoppedHandler() => PlaybackStopped;
        public bool StopConfirmed => _slot.Stopped.IsSet;
        internal void RecordStopped(Exception? error)
        {
            _epoch.Record(error);
        }
        public bool TryConsumeStopped(out Exception? error) => _epoch.TryConsume(out error);
        public void Play()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_slot.Output.PlaybackState == PlaybackState.Stopped)
            {
                _slot.Stopped.Wait();
                _epoch.Start();
                _slot.Stopped.Reset();
            }
            try { _slot.Output.Play(); }
            catch
            {
                _slot.Faulted = true;
                _slot.Stopped.Set();
                throw;
            }
        }
        public void Pause() => _slot.Output.Pause();
        public void Stop() => _slot.Output.Stop();
        public void Discard() => _slot.Faulted = true;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.Return(_slot, _key, _performance);
            PlaybackStopped = null;
        }
    }
}
