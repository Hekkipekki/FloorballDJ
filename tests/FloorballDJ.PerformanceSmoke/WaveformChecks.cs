using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using FloorballDJ.Controls;
using FloorballDJ.Services;
using NAudio.Wave;

internal static class WaveformChecks
{
    internal static async Task RunAsync(string root, string? output = null)
    {
        CheckPeakParity(root);
        await CheckSharingAndCancellation();
        var bounds = await CheckBoundsAndPriority();
        await CheckCacheAndFailures();
        await CheckControlLifecycle(root);
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "waveform-checks.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, optimization = "p1-waveform-v1", checkedUtc = DateTimeOffset.UtcNow,
                measurement = "Deterministic controlled reader/state checks, not hotkey-to-speaker or real media decoding benchmarks.",
                rapidReplacements = 500, maximumObservedConcurrentDecoders = bounds.Maximum,
                obsoleteQueuedFilesDecoded = bounds.ObsoleteDecoded,
                parityFormats = new[] { "8kHz mono 16-bit PCM WAV", "44.1kHz stereo float WAV", "48kHz mono float WAV" },
                passed = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PASS: waveform peak parity, block cancellation, one shared decoder, orphan/retry handling, queue admission/visibility priority, rapid replacements, byte/count LRU, failures and control unload/reload/stale-result protection.");
    }

    private static void CheckPeakParity(string root)
    {
        foreach (var format in new[] { new WaveFormat(8000, 16, 1), WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), WaveFormat.CreateIeeeFloatWaveFormat(48000, 1) })
        {
            var path = Path.Combine(root, $"waveform-{format.SampleRate}-{format.Channels}.wav");
            using (var writer = new WaveFileWriter(path, format))
                for (var sample = 0; sample < format.SampleRate * format.Channels * 2 + 17; sample++)
                    writer.WriteSample((float)(Math.Sin(sample * .017) * .7));
            Check(LegacyPeaks(path).SequenceEqual(WaveformPeakReader.Read(path, CancellationToken.None)), "Waveform peaks must match frozen RC1 exactly across formats and partial final blocks");
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { WaveformPeakReader.Read("must-not-open", cancellation.Token); throw new InvalidOperationException("Pre-cancelled decoder opened a file"); }
        catch (OperationCanceledException) { }
        using var duringRead = new CancellationTokenSource();
        var source = new CancelOnRead(duringRead);
        try { WaveformPeakReader.Read(source, TimeSpan.FromMinutes(1), duringRead.Token); throw new InvalidOperationException("Decoder ignored block cancellation"); }
        catch (OperationCanceledException) { }
        Check(source.Reads == 1, "Cancelled source must not read a second block");
    }

    private static async Task CheckSharingAndCancellation()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var reads = 0;
        using var scheduler = new WaveformScheduler((_, token) =>
        {
            Interlocked.Increment(ref reads);
            entered.Set();
            finish.Wait(token);
            return [new(-.5f, .5f)];
        });
        using var firstCancellation = new CancellationTokenSource();
        using var first = await scheduler.AcquireAsync("same", "same", false, firstCancellation.Token);
        await WaitUntil(() => entered.IsSet);
        using var second = await scheduler.AcquireAsync("SAME", "same", true, CancellationToken.None);
        firstCancellation.Cancel();
        await ExpectCancelled(first.Task);
        Check(scheduler.State.Active == 1 && !second.Task.IsCompleted, "One departing view must not cancel another view's shared decoder");
        finish.Set();
        var peaks = await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cached = await scheduler.AcquireAsync("same", "same", false, CancellationToken.None);
        Check(ReferenceEquals(peaks, await cached.Task) && reads == 1, "Shared and cached results must reuse one completed array");

        using var orphanEntered = new ManualResetEventSlim();
        var attempts = 0;
        using var orphanScheduler = new WaveformScheduler((_, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                orphanEntered.Set();
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                token.ThrowIfCancellationRequested();
            }
            return [new(-1, 1)];
        });
        using var orphanCancellation = new CancellationTokenSource();
        using var orphan = await orphanScheduler.AcquireAsync("orphan", "orphan", true, orphanCancellation.Token);
        await WaitUntil(() => orphanEntered.IsSet);
        orphanCancellation.Cancel();
        using var retry = await orphanScheduler.AcquireAsync("orphan", "orphan", true, CancellationToken.None);
        await ExpectCancelled(orphan.Task);
        Check((await retry.Task.WaitAsync(TimeSpan.FromSeconds(5))).Length == 1 && attempts == 2, "New consumer must retry an orphan instead of attaching to its cancelled job");
    }

    private static async Task<(int Maximum, int ObsoleteDecoded)> CheckBoundsAndPriority()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var order = new ConcurrentQueue<string>();
        var concurrent = 0; var maximum = 0;
        using var scheduler = new WaveformScheduler((path, token) =>
        {
            var active = Interlocked.Increment(ref concurrent);
            maximum = Math.Max(maximum, active);
            try
            {
                order.Enqueue(path);
                if (path == "block") { entered.Set(); finish.Wait(token); }
                return [new(-.25f, .25f)];
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }, maximumJobs: 4);
        using var block = await scheduler.AcquireAsync("block", "block", false, CancellationToken.None);
        await WaitUntil(() => entered.IsSet);
        using var cancelled = await scheduler.AcquireAsync("cancelled", "cancelled", false, CancellationToken.None);
        using var background = await scheduler.AcquireAsync("background", "background", false, CancellationToken.None);
        using var visible = await scheduler.AcquireAsync("visible", "visible", true, CancellationToken.None);
        var admission = scheduler.AcquireAsync("fifth", "fifth", true, CancellationToken.None);
        Check(scheduler.State.Active == 1 && scheduler.State.Pending == 3 && scheduler.State.WaitingAdmissions == 1, "Decoder/job admission must remain bounded while full");
        background.SetVisible(true);
        using var sharedVisible = await scheduler.AcquireAsync("visible", "visible", true, CancellationToken.None);
        cancelled.Dispose();
        using var fifth = await admission.WaitAsync(TimeSpan.FromSeconds(5));
        Check(scheduler.State.Pending == 3, "Freed slot admits a waiting live view");
        using var waitingCancellation = new CancellationTokenSource();
        var withdrawnAdmission = scheduler.AcquireAsync("withdrawn", "withdrawn", true, waitingCancellation.Token);
        waitingCancellation.Cancel();
        await ExpectCancelled(withdrawnAdmission);
        Check(scheduler.State.WaitingAdmissions == 0, "Cancelled admission must leave no waiting request");
        finish.Set();
        await Task.WhenAll(block.Task, background.Task, visible.Task, fifth.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Check(maximum == 1 && order.SequenceEqual(new[] { "block", "visible", "background", "fifth" }), "Visibility changes must preserve priority FIFO and never start concurrent decoders");
        await ExpectCancelled(cancelled.Task);

        // Keep one consumer blocked while rapidly replacing another view 500 times.
        finish.Reset(); entered.Reset();
        using var rapidBlock = await scheduler.AcquireAsync("rapid-block", "block", true, CancellationToken.None);
        await WaitUntil(() => entered.IsSet);
        for (var index = 0; index < 500; index++)
        {
            using var replacement = new CancellationTokenSource();
            using var request = await scheduler.AcquireAsync("rapid-" + index, "obsolete", true, replacement.Token);
            replacement.Cancel();
            await ExpectCancelled(request.Task);
            Check(scheduler.State.Active == 1 && scheduler.State.Pending == 0, "Rapid superseded requests must not create a backlog");
        }
        using var current = await scheduler.AcquireAsync("current", "current", true, CancellationToken.None);
        finish.Set();
        await Task.WhenAll(rapidBlock.Task, current.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Check(!order.Contains("obsolete") && maximum == 1, "No superseded queued file should be decoded");
        return (maximum, order.Count(path => path == "obsolete"));
    }

    private static async Task CheckCacheAndFailures()
    {
        var reads = new ConcurrentDictionary<string, int>();
        using var scheduler = new WaveformScheduler((path, _) =>
        {
            reads.AddOrUpdate(path, 1, (_, count) => count + 1);
            if (path == "bad") throw new IOException("fixture");
            return path == "large" ? new WavePeak[8] : new WavePeak[2];
        }, maximumCacheBytes: 32, maximumCacheEntries: 2);
        async Task<WavePeak[]> Load(string key, string? path = null)
        {
            using var request = await scheduler.AcquireAsync(key, path ?? key, false, CancellationToken.None);
            return await request.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await Load("a"); await Load("b"); await Load("a"); await Load("c");
        Check(scheduler.State.CachedBytes == 32 && scheduler.State.CachedEntries == 2, "Retained payload and entry count must honor both limits");
        await Load("b");
        Check(reads["a"] == 1 && reads["b"] == 2, "Cache hits must promote LRU before eviction");
        await Load("large"); await Load("large");
        Check(reads["large"] == 2 && scheduler.State.CachedBytes <= 32, "Oversized waveform must display without being retained in cache");
        for (var attempt = 0; attempt < 2; attempt++)
            try { await Load("bad"); throw new InvalidOperationException("Decoder failure vanished"); }
            catch (IOException) { }
        await Load("after-failure");
        Check(reads["bad"] == 2, "Failed jobs must not poison retries or stall other files");

        using var entered = new ManualResetEventSlim();
        using var disposing = new WaveformScheduler((_, token) =>
        {
            entered.Set(); token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)); token.ThrowIfCancellationRequested();
            return [];
        }, maximumJobs: 2);
        using var active = await disposing.AcquireAsync("active", "active", true, CancellationToken.None);
        await WaitUntil(() => entered.IsSet);
        using var pending = await disposing.AcquireAsync("pending", "pending", true, CancellationToken.None);
        var waiting = disposing.AcquireAsync("waiting", "waiting", true, CancellationToken.None);
        disposing.Dispose();
        await ExpectCancelled(active.Task); await ExpectCancelled(pending.Task);
        try { await waiting; throw new InvalidOperationException("Disposed scheduler admitted a waiter"); }
        catch (ObjectDisposedException) { }
        await disposing.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(disposing.State.Active == 0 && disposing.State.Pending == 0 && disposing.State.CachedBytes == 0, "Shutdown must release decoder/queue/cache ownership");
    }

    private static async Task CheckControlLifecycle(string root)
    {
        var oldPath = Path.Combine(root, "waveform-old.fixture");
        var newPath = Path.Combine(root, "waveform-new.fixture");
        File.WriteAllText(oldPath, "old"); File.WriteAllText(newPath, "new");
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var reads = 0;
        using var scheduler = new WaveformScheduler((path, _) =>
        {
            Interlocked.Increment(ref reads);
            if (path == oldPath) { entered.Set(); finish.Wait(TimeSpan.FromSeconds(5)); }
            return path == oldPath ? [new(-.1f, .1f)] : [new(-.9f, .9f)];
        });
        var control = new WaveformControl(scheduler);
        var obsolete = control.LoadAsync(oldPath);
        await WaitUntil(() => entered.IsSet);
        var current = control.LoadAsync(newPath);
        await obsolete.WaitAsync(TimeSpan.FromSeconds(5));
        finish.Set();
        await current.WaitAsync(TimeSpan.FromSeconds(5));
        WavePeak[] Displayed() => (WavePeak[])typeof(WaveformControl).GetField("_peaks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!;
        Check(Displayed().Single().Maximum == .9f && reads == 2, "Late orphan result must never overwrite the current waveform");
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Check(Displayed().Length == 0, "Unloaded controls must release their peaks/geometry references");
        await control.LoadAsync(oldPath);
        Check(reads == 2, "Unloaded controls must defer new work until reload");
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        await WaitUntil(() => Displayed().Length > 0);
        Check(Displayed().Single().Maximum == .1f && reads == 3, "Reload must acquire the last requested path rather than the dependency property's default");
        var originalKey = WaveformScheduler.CacheKey(newPath);
        File.AppendAllText(newPath, "changed length");
        Check(originalKey != WaveformScheduler.CacheKey(newPath), "Changed file metadata must invalidate the key");
        await control.LoadAsync(newPath);
        Check(reads == 4, "Changed file must not use old cached peaks");
        File.Delete(newPath);
        await control.LoadAsync(newPath);
        Check(Displayed().Length == 0, "Missing current file must clear its waveform");
        await control.LoadAsync("");
        Check(Displayed().Length == 0, "Empty current file must clear its waveform");
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Waveform fixture did not reach the expected state");
            await Task.Delay(5);
        }
    }

    private static async Task ExpectCancelled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Expected cancellation"); }
        catch (OperationCanceledException) { }
    }

    private static WavePeak[] LegacyPeaks(string path)
    {
        using var reader = new AudioFileReader(path);
        var count = Math.Clamp((int)Math.Ceiling(reader.TotalTime.TotalSeconds * 80), 6000, 240000);
        var totalSamples = Math.Max(1L, (long)Math.Ceiling(reader.TotalTime.TotalSeconds * reader.WaveFormat.SampleRate * reader.WaveFormat.Channels));
        var samplesPerPeak = Math.Max(1L, totalSamples / count);
        var result = new List<WavePeak>(count);
        var buffer = new float[65536];
        long accumulated = 0;
        float minimum = 0, maximum = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            for (var index = 0; index < read; index++)
            {
                minimum = Math.Min(minimum, buffer[index]); maximum = Math.Max(maximum, buffer[index]); accumulated++;
                if (accumulated < samplesPerPeak) continue;
                result.Add(new(minimum, maximum)); accumulated = 0; minimum = maximum = 0;
            }
        if (accumulated > 0) result.Add(new(minimum, maximum));
        return [.. result];
    }

    private sealed class CancelOnRead(CancellationTokenSource cancellation) : ISampleProvider
    {
        internal int Reads;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count) { Reads++; cancellation.Cancel(); return count; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
