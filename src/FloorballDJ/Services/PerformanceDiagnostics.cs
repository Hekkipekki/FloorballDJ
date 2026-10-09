using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;

namespace FloorballDJ.Services;

/// <summary>Opt-in, bounded diagnostics. Producers never write files or wait for the writer.</summary>
internal static class PerformanceDiagnostics
{
    private static readonly AsyncLocal<PerformanceCommand?> AmbientCommand = new();
    private static PerformanceSession? _session;

    internal static bool Enabled => Volatile.Read(ref _session)?.IsRecording == true;

    internal static void Initialize(string[] arguments, Dispatcher dispatcher)
    {
        if (!arguments.Contains("--performance-diagnostics", StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            var index = Array.FindIndex(arguments, item => item.Equals("--performance-dir", StringComparison.OrdinalIgnoreCase));
            var directory = index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloorballDJ", "Diagnostics",
                    $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Start(directory, dispatcher);
        }
        catch (Exception exception)
        {
            // Diagnostics must never prevent the application from starting.
            Debug.WriteLine($"Performance diagnostics unavailable: {exception.GetType().Name}");
        }
    }

    internal static PerformanceSession Start(string directory, Dispatcher? dispatcher = null,
        int capacity = 8192, long maximumBytes = 128 * 1024 * 1024)
    {
        if (Volatile.Read(ref _session) is not null)
            throw new InvalidOperationException("A performance capture is already active.");
        var session = new PerformanceSession(Path.GetFullPath(directory), capacity, maximumBytes);
        if (Interlocked.CompareExchange(ref _session, session, null) is not null)
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("A performance capture is already active.");
        }
        session.Start(dispatcher);
        return session;
    }

    internal static void Detach(PerformanceSession session) => Interlocked.CompareExchange(ref _session, null, session);

    internal static void Stop()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;
        try { session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception exception) { Debug.WriteLine(exception.GetType().Name); }
    }

    internal static PerformanceCommandScope BeginCommand(string source)
    {
        var session = Volatile.Read(ref _session);
        if (session?.IsRecording != true) return default;
        var previous = AmbientCommand.Value;
        var command = new PerformanceCommand(session, session.NextId());
        AmbientCommand.Value = command;
        session.Record(command.Id, 0, "CommandReceived", detail: source);
        return new PerformanceCommandScope(command, previous);
    }

    internal static PerformanceOperation BeginOperation(string stage)
    {
        var session = Volatile.Read(ref _session);
        if (session?.IsRecording != true) return default;
        var command = AmbientCommand.Value;
        var commandId = command is { Active: true } && ReferenceEquals(command.Session, session) ? command.Id : 0;
        var operation = new PerformanceOperation(session, commandId, session.NextId());
        operation.Mark(stage);
        return operation;
    }

    internal static void RouteResolved(string route)
    {
        if (!Enabled) return;
        var command = AmbientCommand.Value;
        if (command is { Active: true }) command.Session.Record(command.Id, 0, "RouteResolved", detail: route);
    }

    internal static void EndCommand(PerformanceCommand command, PerformanceCommand? previous)
    {
        command.Active = false;
        command.Session.Record(command.Id, 0, "CommandHandlerCompleted");
        AmbientCommand.Value = previous;
    }
}

internal sealed class PerformanceCommand(PerformanceSession session, long id)
{
    internal PerformanceSession Session { get; } = session;
    internal long Id { get; } = id;
    internal volatile bool Active = true;
}

internal readonly struct PerformanceCommandScope(PerformanceCommand? command, PerformanceCommand? previous) : IDisposable
{
    public void Dispose()
    {
        if (command is not null) PerformanceDiagnostics.EndCommand(command, previous);
    }
}

internal readonly struct PerformanceOperation(PerformanceSession? session, long commandId, long id)
{
    internal bool Enabled => session?.IsRecording == true;
    internal long Timestamp => Enabled ? Stopwatch.GetTimestamp() : 0;
    internal void Mark(string stage, double? value = null, string? detail = null)
        => session?.Record(commandId, id, stage, value: value, detail: detail);
    internal void Duration(string stage, long started, double? value = null)
    {
        if (started != 0) session?.Record(commandId, id, stage, Stopwatch.GetTimestamp() - started, value);
    }
    internal PerformanceDuration Measure(string stage) => new(this, stage, Timestamp);
}

internal readonly struct PerformanceDuration(PerformanceOperation operation, string stage, long started) : IDisposable
{
    public void Dispose() => operation.Duration(stage, started);
}

internal readonly record struct PerformanceEvent(long Timestamp, long CommandId, long OperationId,
    string Stage, int ThreadId, long DurationTicks, double? Value, string? Detail);

internal sealed class PerformanceEventBuffer
{
    private readonly Channel<PerformanceEvent> _channel;
    private long _dropped;
    internal PerformanceEventBuffer(int capacity)
        => _channel = Channel.CreateBounded<PerformanceEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    internal long Dropped => Interlocked.Read(ref _dropped);
    internal bool TryWrite(PerformanceEvent item)
    {
        if (_channel.Writer.TryWrite(item)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }
    internal bool TryRead(out PerformanceEvent item) => _channel.Reader.TryRead(out item);
    internal void Complete() => _channel.Writer.TryComplete();
}

internal sealed class PerformanceSession : IAsyncDisposable
{
    private readonly PerformanceEventBuffer _buffer;
    private readonly CancellationTokenSource _stop = new();
    private readonly long _maximumBytes;
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private Task _writer = Task.CompletedTask;
    private Task _sampler = Task.CompletedTask;
    private long _nextId;
    private int _recording = 1;
    private int _disposed;
    private int _probePending;
    internal string DirectoryPath { get; }
    internal bool IsRecording => Volatile.Read(ref _recording) == 1;
    internal string? Failure { get; private set; }
    internal string StopReason { get; private set; } = "CaptureClosed";
    internal long WrittenEvents { get; private set; }
    internal long DroppedEvents => _buffer.Dropped;

    internal PerformanceSession(string directory, int capacity, long maximumBytes)
    {
        if (maximumBytes < 4096) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        DirectoryPath = directory;
        _buffer = new PerformanceEventBuffer(capacity);
        _maximumBytes = maximumBytes;
    }

    internal void Start(Dispatcher? dispatcher)
    {
        _writer = Task.Run(WriteAsync);
        _sampler = Task.Run(() => SampleAsync(dispatcher));
    }
    internal long NextId() => Interlocked.Increment(ref _nextId);
    internal void Record(long commandId, long operationId, string stage, long durationTicks = -1,
        double? value = null, string? detail = null)
    {
        if (!IsRecording) return;
        _buffer.TryWrite(new PerformanceEvent(Stopwatch.GetTimestamp(), commandId, operationId,
            stage, Environment.CurrentManagedThreadId, durationTicks, value, detail));
    }

    private async Task WriteAsync()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var metadata = new
            {
                schemaVersion = 1, diagnosticRevision = "primary-output-keep-alive-v1", startedUtc = _startedUtc, stopwatchFrequency = Stopwatch.Frequency,
                applicationVersion = typeof(AudioEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                runtime = Environment.Version.ToString(), os = Environment.OSVersion.VersionString,
                logicalProcessors = Environment.ProcessorCount, processId = Environment.ProcessId,
                maximumEventBytes = _maximumBytes,
                measurement = "Managed handler/setup/provider timings; not physical key-to-speaker latency",
                firstSignalThreshold = 0.0001,
                privacy = "No typed keys, song titles, media paths, profile content, or license data"
            };
            // CreateNew makes capture directories single-use and prevents overwriting an earlier run.
            await using (var metadataFile = new FileStream(Path.Combine(DirectoryPath, "session.json"), FileMode.CreateNew,
                             FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(metadataFile, metadata, new JsonSerializerOptions { WriteIndented = true });
            await using var file = new FileStream(Path.Combine(DirectoryPath, "events.jsonl"), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
            await using var writer = new StreamWriter(file, new UTF8Encoding(false), 65536);
            long bytes = 0;
            long discarded = 0;
            using var flushTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                while (_buffer.TryRead(out var item))
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        type = "event", elapsedMs = (item.Timestamp - _origin) * 1000d / Stopwatch.Frequency,
                        commandId = item.CommandId, operationId = item.OperationId, stage = item.Stage,
                        threadId = item.ThreadId,
                        durationMs = item.DurationTicks < 0 ? (double?)null : item.DurationTicks * 1000d / Stopwatch.Frequency,
                        value = item.Value, detail = item.Detail
                    });
                    var size = Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(writer.NewLine);
                    if (bytes + size > _maximumBytes - 2048)
                    {
                        StopReason = "EventByteLimit";
                        discarded++;
                        Interlocked.Exchange(ref _recording, 0);
                        _stop.Cancel();
                        break;
                    }
                    await writer.WriteLineAsync(json);
                    bytes += size;
                    WrittenEvents++;
                }
                await writer.FlushAsync();
                if (StopReason == "EventByteLimit" || _stop.IsCancellationRequested) break;
            } while (await flushTimer.WaitForNextTickAsync());
            while (_buffer.TryRead(out _)) discarded++;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                type = "summary", writtenEvents = WrittenEvents, droppedEvents = DroppedEvents,
                discardedEvents = discarded, stopReason = StopReason,
                elapsedMs = (Stopwatch.GetTimestamp() - _origin) * 1000d / Stopwatch.Frequency
            }));
            await writer.FlushAsync();
        }
        catch (Exception exception)
        {
            Failure = exception.GetType().Name;
            StopReason = "WriterFailed";
            Debug.WriteLine($"Performance capture writer failed: {Failure}");
        }
        finally
        {
            Interlocked.Exchange(ref _recording, 0);
            _stop.Cancel();
            _buffer.Complete();
        }
    }

    private async Task SampleAsync(Dispatcher? dispatcher)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            var previousCpu = process.TotalProcessorTime;
            var previousTimestamp = Stopwatch.GetTimestamp();
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                process.Refresh();
                var timestamp = Stopwatch.GetTimestamp();
                var cpu = process.TotalProcessorTime;
                var wallSeconds = (timestamp - previousTimestamp) / (double)Stopwatch.Frequency;
                Record(0, 0, "CpuCoreEquivalents", value: (cpu - previousCpu).TotalSeconds / wallSeconds);
                Record(0, 0, "WorkingSetBytes", value: process.WorkingSet64);
                Record(0, 0, "ManagedHeapBytes", value: GC.GetTotalMemory(false));
                Record(0, 0, "AllocatedBytes", value: GC.GetTotalAllocatedBytes(false));
                Record(0, 0, "Gen0Collections", value: GC.CollectionCount(0));
                Record(0, 0, "Gen1Collections", value: GC.CollectionCount(1));
                Record(0, 0, "Gen2Collections", value: GC.CollectionCount(2));
                Record(0, 0, "ThreadCount", value: process.Threads.Count);
                Record(0, 0, "HandleCount", value: process.HandleCount);
                previousCpu = cpu;
                previousTimestamp = timestamp;
                if (dispatcher is not null && !dispatcher.HasShutdownStarted && Interlocked.CompareExchange(ref _probePending, 1, 0) == 0)
                {
                    var queued = Stopwatch.GetTimestamp();
                    _ = dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        Record(0, 0, "DispatcherQueueDelay", Stopwatch.GetTimestamp() - queued);
                        Volatile.Write(ref _probePending, 0);
                    }));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Debug.WriteLine($"Performance sampler stopped: {exception.GetType().Name}"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await _writer; return; }
        PerformanceDiagnostics.Detach(this);
        Interlocked.Exchange(ref _recording, 0);
        _buffer.Complete();
        _stop.Cancel();
        await Task.WhenAll(_writer, _sampler).ConfigureAwait(false);
        _stop.Dispose();
    }
}
