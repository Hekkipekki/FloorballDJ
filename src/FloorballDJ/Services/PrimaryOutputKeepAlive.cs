using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FloorballDJ.Services;

// A separate, opt-in shared-mode silence stream. It owns no playback voices,
// cannot advance queues, and does not depend on UI timers or media files.
internal sealed class PrimaryOutputKeepAlive : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<string?, IWavePlayer> _open;
    private readonly System.Threading.Timer _timer;
    private Task _worker = Task.CompletedTask;
    private IWavePlayer? _output;
    private bool _enabled, _disposed, _requested, _running;
    private string? _deviceId;
    private long _generation, _openedGeneration = -1;
    internal Task Settled { get { lock (_gate) return _worker; } }
    internal bool Active { get { lock (_gate) return _output is not null; } }

    internal PrimaryOutputKeepAlive(Func<string?, IWavePlayer>? open = null)
    {
        _open = open ?? Open;
        _timer = new(_ => RequestWork(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal void Configure(bool enabled, string? deviceId)
    {
        lock (_gate)
        {
            if (_disposed || (_enabled == enabled && string.Equals(_deviceId, deviceId, StringComparison.Ordinal))) return;
            var wasEnabled = _enabled;
            _enabled = enabled; _deviceId = deviceId; _generation++;
            if (!enabled && !wasEnabled && _output is null && !_running) return;
            _timer.Change(enabled ? 5000 : Timeout.Infinite, enabled ? 5000 : Timeout.Infinite);
            QueueWork();
        }
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            if (_disposed || !_enabled) return;
            _generation++; QueueWork();
        }
    }

    private void RequestWork()
    {
        lock (_gate) { if (!_disposed && _enabled) QueueWork(); }
    }
    private void QueueWork()
    {
        _requested = true;
        if (_running) return;
        _running = true; _worker = Task.Run(Work);
    }
    private void Work()
    {
        while (true)
        {
            bool enabled; string? id; long generation; IWavePlayer? previous;
            lock (_gate)
            {
                if (!_requested) { _running = false; return; }
                _requested = false; enabled = _enabled && !_disposed; id = _deviceId; generation = _generation;
                if (enabled && _openedGeneration == generation && _output?.PlaybackState == PlaybackState.Playing) continue;
                previous = _output; _output = null;
            }
            Release(previous);
            if (!enabled) continue;
            IWavePlayer? candidate = null;
            try
            {
                candidate = _open(id);
                lock (_gate)
                {
                    if (!_disposed && _enabled && generation == _generation)
                    {
                        _output = candidate; _openedGeneration = generation; candidate = null;
                        PerformanceDiagnostics.BeginOperation("PrimaryOutputKeepAlive").Mark("KeepAliveStarted");
                    }
                }
            }
            catch (Exception exception)
            {
                // Retry at the next bounded timer/device notification, not in a busy loop.
                PerformanceDiagnostics.BeginOperation("PrimaryOutputKeepAlive").Mark("KeepAliveFailed", detail: exception.GetType().Name);
            }
            finally { Release(candidate); }
        }
    }

    private static IWavePlayer Open(string? id)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try { device = string.IsNullOrWhiteSpace(id) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(id); }
        catch { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        using (device)
        {
            var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
            try { output.Init(new SilenceProvider(new WaveFormat(48000, 16, 2))); output.Play(); return output; }
            catch { output.Dispose(); throw; }
        }
    }
    private static void Release(IWavePlayer? output)
    {
        if (output is null) return;
        try { output.Stop(); } catch { }
        try { output.Dispose(); } catch { }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _enabled = false; _generation++; _timer.Dispose();
            if (_output is not null || _running) QueueWork();
        }
    }
}
