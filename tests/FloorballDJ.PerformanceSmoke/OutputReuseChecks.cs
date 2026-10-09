using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FloorballDJ.Models;
using FloorballDJ.Services;
using NAudio.Wave;

internal static class OutputReuseChecks
{
    internal static async Task RunAsync(string root, string? output = null)
    {
        CheckPoolOwnership();
        CheckBoundsExpiryAndInvalidation();
        CheckFailuresAndEpochs();
        await CheckStopConfirmation();
        var facts = new List<object>();
        foreach (var reuse in new[] { false, true }) facts.Add(await CheckRealPlayback(root, reuse));
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "output-reuse-checks.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, optimization = "p2-stopped-output-reuse-v1", checkedUtc = DateTimeOffset.UtcNow,
                measurement = "Controlled backend ownership/byte checks and quiet real WASAPI behavior checks. No physical onset, unplug/sleep or long-session qualification.",
                passed = true, hardwareChecks = facts
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PASS: output reuse byte/EOF parity, separate active/preview/format/endpoint leases, captured stop callbacks/epochs, idle bounds/expiry/invalidation/failures, and dual-path real WASAPI repeat/fade/pause/seek/loop/trim/completion/polyphony checks.");
    }

    private static void CheckPoolOwnership()
    {
        var callbacks = new Queue<Action>();
        var backends = new List<FakeOutput>();
        IWavePlayer Create() { var backend = new FakeOutput(); backends.Add(backend); return backend; }
        using var pool = new AudioOutputPool(dispatch: callbacks.Enqueue, startTimer: false);
        var firstSource = new Bytes(48000, 2, [.1f, -.2f, .3f, -.4f]);
        var first = pool.Acquire("endpoint", false, firstSource, Create);
        var notifications = new List<string>();
        first.PlaybackStopped += (_, _) => notifications.Add("first");
        first.Play();
        var expected = new byte[40]; var actual = new byte[40];
        Array.Fill(expected, (byte)77); Array.Fill(actual, (byte)77);
        var reference = new Bytes(48000, 2, [.1f, -.2f, .3f, -.4f]);
        Check(reference.Read(expected, 3, 24) == backends[0].Source!.Read(actual, 3, 24) && expected.SequenceEqual(actual), "Reusable source must preserve exact bytes, offsets, short reads and EOF");
        Check(backends[0].Source!.Read(actual, 3, 24) == 0, "EOF must remain EOF rather than padded silence");
        first.Dispose();
        Check(pool.IdleCount == 1 && backends[0].PlaybackState == PlaybackState.Stopped, "Idle output must be stopped/reset and detached");
        Check(backends[0].Source!.Read(actual, 3, 24) == 0, "Released output must no longer read its old source");
        var second = pool.Acquire("endpoint", false, new Bytes(48000, 2, [.8f, -.8f]), Create);
        second.PlaybackStopped += (_, _) => notifications.Add("second");
        second.Play();
        Check(backends.Count == 1 && backends[0].Initializations == 1, "Matching warm start must skip output creation and initialization");
        while (callbacks.TryDequeue(out var callback)) callback();
        Check(notifications.SequenceEqual(new[] { "first" }), "Delayed old stop callback must not target the new lease");
        using var concurrent = pool.Acquire("endpoint", false, new Bytes(48000, 2, [0, 0]), Create);
        concurrent.Play();
        using var preview = pool.Acquire("endpoint", true, new Bytes(48000, 2, [0, 0]), Create);
        preview.Play();
        Check(backends.Count == 3, "Concurrent voices and preview must have independent outputs even on the same endpoint");
        second.Pause();
        Check(second.PlaybackState == PlaybackState.Paused && concurrent.PlaybackState == PlaybackState.Playing && preview.PlaybackState == PlaybackState.Playing, "Pausing one lease must not pause other routes/voices");
        second.Play(); second.Dispose();
        using var changedFormat = pool.Acquire("endpoint", false, new Bytes(44100, 1, [0]), Create);
        using var changedEndpoint = pool.Acquire("different-endpoint", false, new Bytes(48000, 2, [0, 0]), Create);
        Check(backends.Count == 5, "Different source formats/endpoints must not reuse an incompatible initialization");
    }

    private static void CheckBoundsExpiryAndInvalidation()
    {
        long now = 0;
        var outputs = new List<FakeOutput>();
        IWavePlayer Create() { var backend = new FakeOutput(); outputs.Add(backend); return backend; }
        using var pool = new AudioOutputPool(maximumIdle: 2, timestamp: () => now, startTimer: false, dispatch: action => action());
        for (var index = 0; index < 3; index++)
        {
            using var voice = pool.Acquire("endpoint-" + index, false, new Bytes(48000, 2, [0, 0]), Create);
            voice.Play();
        }
        Check(pool.IdleCount == 2 && outputs[0].Disposed, "Idle retention must be globally bounded with oldest eviction");
        now += 31 * Stopwatch.Frequency;
        pool.EvictExpired();
        Check(pool.IdleCount == 0 && outputs.All(backend => backend.Disposed), "Expired initialized outputs must release backend resources");
        using var active = pool.Acquire("active", false, new Bytes(48000, 2, [0, 0]), Create);
        active.Play();
        pool.Invalidate();
        Check(active.PlaybackState == PlaybackState.Playing, "Invalidation must leave existing playback intact");
        active.Dispose();
        Check(pool.IdleCount == 0 && outputs[^1].Disposed, "Invalidated active generation must never return to the idle cache");
        using var warm = pool.Acquire("warm", false, new Bytes(48000, 2, [0, 0]), Create);
        warm.Dispose(); pool.Invalidate();
        Check(pool.IdleCount == 0 && outputs[^1].Disposed, "Device/profile routing invalidation must retire idle outputs");
        pool.Dispose();
        try { pool.Acquire("late", false, new Bytes(48000, 2, [0, 0]), Create); throw new InvalidOperationException("Disposed pool accepted a lease"); }
        catch (ObjectDisposedException) { }
    }

    private static void CheckFailuresAndEpochs()
    {
        using var pool = new AudioOutputPool(startTimer: false, dispatch: action => action());
        var failedInit = new FakeOutput { FailInit = true };
        try { pool.Acquire("endpoint", false, new Bytes(48000, 2, [0, 0]), () => failedInit); throw new InvalidOperationException("Initialization failure vanished"); }
        catch (IOException) { }
        Check(failedInit.Disposed && pool.IdleCount == 0, "Failed initialization must dispose without poisoning cache");
        var failedPlay = new FakeOutput { FailPlay = true };
        using (var lease = pool.Acquire("endpoint", false, new Bytes(48000, 2, [0, 0]), () => failedPlay))
        {
            try { lease.Play(); throw new InvalidOperationException("Play failure vanished"); }
            catch (IOException) { }
        }
        Check(failedPlay.Disposed && pool.IdleCount == 0, "Synchronous restart failure must retire the initialized output");
        var backend = new FakeOutput();
        using var current = (AudioOutputPool.Lease)pool.Acquire("endpoint", false, new Bytes(48000, 2, [0, 0]), () => backend);
        current.Play(); backend.Complete();
        Check(current.TryConsumeStopped(out var error) && error is null && !current.TryConsumeStopped(out _), "Natural stop must be consumed exactly once");
        current.Play();
        Check(!current.TryConsumeStopped(out _), "Old completion must not apply to restarted loop epoch");
        backend.Complete(new IOException("device invalidated"));
        Check(current.TryConsumeStopped(out error) && error is IOException, "Asynchronous device failure must retain its exception");
        current.Dispose();
        Check(backend.Disposed && pool.IdleCount == 0, "Asynchronous failure must retire its output");
    }

    private static async Task CheckStopConfirmation()
    {
        foreach (var reuse in new[] { false, true })
        {
            using var pool = new AudioOutputPool(startTimer: false, dispatch: _ => { });
            var backend = new FakeOutput();
            var source = new Bytes(48000, 2, [.1f, -.1f]);
            IVoiceOutput voice;
            if (reuse) voice = pool.Acquire("reset-gap", false, source, () => backend);
            else { backend.Init(source); voice = new DirectVoiceOutput(backend); }
            voice.Play();
            // Reproduce WasapiOut's Stopped -> Reset -> terminal event ordering.
            backend.BeginStoppedBeforeReset();
            var release = Task.Run(voice.Dispose);
            try
            {
                await backend.StopCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check(!voice.StopConfirmed && !release.IsCompleted && pool.IdleCount == 0 && !backend.Disposed,
                    "A Stopped state alone must not permit disposal or handing the output to a new source");
            }
            finally { backend.FinishReset(); await release.WaitAsync(TimeSpan.FromSeconds(2)); }
            Check(reuse ? pool.IdleCount == 1 && !backend.Disposed : backend.Disposed,
                "Confirmed Reset must release the direct output or admit the reusable output");
        }
    }

    private static async Task<object> CheckRealPlayback(string root, bool reuse)
    {
        var stereo = Path.Combine(root, "output-stereo.wav");
        var mono = Path.Combine(root, "output-mono.wav");
        var third = Path.Combine(root, "output-third.wav");
        var shortFile = Path.Combine(root, "output-short.wav");
        foreach (var item in new[] { (stereo, 48000, 2, 4d), (mono, 44100, 1, 4d), (third, 48000, 2, 4d), (shortFile, 48000, 2, .2d) })
            using (var writer = new WaveFileWriter(item.Item1, new WaveFormat(item.Item2, 16, item.Item3)))
            {
                var silence = new byte[(int)(item.Item2 * item.Item3 * 2 * item.Item4)];
                writer.Write(silence, 0, silence.Length);
            }
        var capture = Path.Combine(root, reuse ? "reuse-behavior" : "legacy-behavior");
        var session = PerformanceDiagnostics.Start(capture);
        var completed = new List<Guid>();
        var failures = new List<string>();
        var owningThread = Environment.CurrentManagedThreadId;
        var callbacksOnOwningThread = true;
        using var engine = new AudioEngine(reuse);
        engine.Configure(null, null, -60, -12, .03, .06);
        engine.PlaybackCompleted += (_, jingle) =>
        {
            callbacksOnOwningThread &= Environment.CurrentManagedThreadId == owningThread;
            lock (completed) completed.Add(jingle.Id);
        };
        engine.PlaybackFailed += (_, message) => { lock (failures) failures.Add(message); };
        var first = new Jingle { FilePath = stereo, FadeInOverrideSeconds = 0 };
        var second = new Jingle { FilePath = mono, FadeInOverrideSeconds = 0 };
        var next = new Jingle { FilePath = third, FadeInOverrideSeconds = 0 };
        try
        {
            Check(engine.Play(first) == PlaybackAction.Started, "Initial start");
            await WaitUntil(() => engine.GetCurrentPosition() is { TotalSeconds: > 0 });
            Check(engine.Play(first) == PlaybackAction.FadingOut, "Repeat press must remain a fade action");
            await engine.FadeOutAllAsync(.03).WaitAsync(TimeSpan.FromSeconds(4));
            engine.Play(first);
            await Task.Delay(80);
            var reversible = engine.FadeOutAllAsync(.5, allowSpaceResume: true);
            await Task.Delay(50);
            Check(engine.TryResumeSpaceFade(), "Space must reverse a live fade");
            await reversible.WaitAsync(TimeSpan.FromSeconds(4));
            await engine.PauseOrResumeAsync().WaitAsync(TimeSpan.FromSeconds(4));
            var paused = engine.GetCurrentPosition();
            await Task.Delay(80);
            Check(engine.GetCurrentPosition() == paused, "Paused reader must stop advancing");
            engine.Seek(TimeSpan.FromSeconds(1));
            await engine.PauseOrResumeAsync().WaitAsync(TimeSpan.FromSeconds(4));
            await WaitUntil(() => engine.GetCurrentPosition() is { TotalSeconds: > 1 });
            engine.Play(second);
            await Task.Delay(160);
            engine.Play(next);
            await Task.Delay(160);
            engine.SetSecondaryOutput(true);
            Check(engine.Play(first) == PlaybackAction.Started, "Preview start on independent route");
            await Task.Delay(80);
            engine.SeekSecondary(TimeSpan.FromSeconds(.5));
            await WaitUntil(() => engine.GetSecondarySnapshot().Position.TotalSeconds > .5);
            engine.SetSecondaryOutput(false);
            engine.StopSecondaryOutput();
            await WaitUntil(() => engine.GetSecondarySnapshot().JingleId is null);
            Check(engine.GetCurrentPosition() is not null, "Stopping preview must preserve main output");
            engine.StopAll();
            var polyphonic = new Jingle { FilePath = stereo, PlayMode = JinglePlayMode.Mix, AllowMultipleClicks = true };
            for (var index = 0; index < 8; index++) Check(engine.Play(polyphonic) == PlaybackAction.Started, "Independent polyphony start");
            Check(engine.Play(polyphonic) == PlaybackAction.PolyphonyLimitReached, "Eight-instance polyphony limit");
            engine.StopAll();
            lock (completed) Check(completed.Count == 0, "Manual stops/fades/preview/polyphony must not advance natural completion");
            var natural = new Jingle { FilePath = shortFile, FadeInOverrideSeconds = 0 };
            engine.Play(natural);
            await WaitUntil(() => { engine.PublishSnapshot(); lock (completed) return completed.Contains(natural.Id); });
            await Task.Delay(100);
            lock (completed) Check(completed.Count(id => id == natural.Id) == 1, "Natural EOF must notify once even when timer beats callback");
            var trimmed = new Jingle { FilePath = stereo, StartSeconds = .1, EndSeconds = .2, FadeInOverrideSeconds = 0 };
            engine.Play(trimmed);
            await WaitUntil(() => { engine.PublishSnapshot(); lock (completed) return completed.Contains(trimmed.Id); });
            lock (completed) Check(completed.Count(id => id == trimmed.Id) == 1, "Trimmed end must retain natural completion");
            var loop = new Jingle { FilePath = shortFile, Loop = true, FadeInOverrideSeconds = 0 };
            engine.Play(loop);
            await Task.Delay(800);
            engine.PublishSnapshot();
            Check(engine.GetCurrentPosition() is not null, "Natural loop must restart without disposing its lease");
            lock (completed) Check(!completed.Contains(loop.Id), "Loop must not trigger queue/follow-up completion");
            engine.StopAll();
            lock (failures) Check(failures.Count == 0, "Playback checks must not report device/decoder errors");
            Check(callbacksOnOwningThread, "Completion notifications must retain the playback caller's UI context");
        }
        finally { engine.StopAll(); await session.DisposeAsync(); }
        var events = File.ReadLines(Path.Combine(capture, "events.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        int Count(string stage) => events.Count(item => item.GetProperty("type").GetString() == "event" && item.GetProperty("stage").GetString() == stage);
        Check(Count("PlaybackFailed") == 0 && session.DroppedEvents == 0 && session.Failure is null, "Behavior capture must be complete and successful");
        if (reuse) Check(Count("OutputReused") > 0, "Real compatible starts must actually reuse initialized outputs");
        return new { mode = reuse ? "reusePrototype" : "perVoice", outputInitializations = Count("OutputInitialization"),
            reusedOutputs = Count("OutputReused"), firstBuffers = Count("FirstBufferReturned"), naturalCompletions = completed.Count };
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        var deadline = Stopwatch.StartNew();
        while (!ready())
        {
            if (deadline.Elapsed.TotalSeconds > 5) throw new TimeoutException("Audio-output fixture did not reach expected state");
            await Task.Delay(5);
        }
    }
    private sealed class Bytes : IWaveProvider
    {
        private readonly byte[] _bytes;
        private int _position;
        public WaveFormat WaveFormat { get; }
        internal Bytes(int rate, int channels, float[] values)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
            _bytes = new byte[values.Length * sizeof(float)];
            Buffer.BlockCopy(values, 0, _bytes, 0, _bytes.Length);
        }
        public int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, _bytes.Length - _position);
            Array.Copy(_bytes, _position, buffer, offset, read); _position += read; return read;
        }
    }
    private sealed class FakeOutput : IWavePlayer
    {
        internal IWaveProvider? Source;
        internal int Initializations;
        internal bool Disposed, FailInit, FailPlay;
        internal readonly TaskCompletionSource StopCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => Source?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public float Volume { get; set; }
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider source)
        {
            Initializations++; Source = source;
            if (FailInit) throw new IOException("init fixture");
        }
        public void Play() { if (FailPlay) throw new IOException("play fixture"); PlaybackState = PlaybackState.Playing; }
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() { StopCalled.TrySetResult(); if (PlaybackState != PlaybackState.Stopped) Complete(); }
        internal void BeginStoppedBeforeReset() => PlaybackState = PlaybackState.Stopped;
        internal void FinishReset() => PlaybackStopped?.Invoke(this, new StoppedEventArgs(null));
        internal void Complete(Exception? error = null)
        {
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
        }
        public void Dispose() { Stop(); Disposed = true; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
