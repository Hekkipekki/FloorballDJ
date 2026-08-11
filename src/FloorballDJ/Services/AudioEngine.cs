using FloorballDJ.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FloorballDJ.Services;

public enum PlaybackAction { Started, FadingOut, PolyphonyLimitReached }

public sealed class AudioEngine : IDisposable
{
    private const int MaxConcurrentInstancesPerJingle = 8;
    private sealed class Voice : IDisposable
    {
        private readonly object _fadeGate = new();
        private CancellationTokenSource _fadeCancellation = new();
        private volatile bool _isVolumeTransitioning;

        public required Jingle Jingle { get; init; }
        public required AudioFileReader Reader { get; init; }
        public required WasapiOut Output { get; init; }
        public required VolumeSampleProvider Volume { get; init; }
        public required DjEffectsSampleProvider Effects { get; init; }
        public required SmoothGainSampleProvider TalkGain { get; init; }
        public bool Paused { get; set; }
        public bool PauseRequested { get; set; }
        public float PeakLeft { get; set; }
        public float PeakRight { get; set; }
        public bool IsDisposed { get; private set; }
        public bool StopRequested { get; set; }
        public bool NaturalEndRequested { get; set; }
        public bool LoopEnabled { get; init; }
        public bool UsesSecondaryDevice { get; init; }
        public double PolyphonyHeadroomDb { get; set; }
        public bool IsVolumeTransitioning => _isVolumeTransitioning;
        public CancellationToken BeginFade()
        {
            lock (_fadeGate)
            {
                _fadeCancellation.Cancel();
                _fadeCancellation.Dispose();
                _fadeCancellation = new CancellationTokenSource();
                _isVolumeTransitioning = true;
                return _fadeCancellation.Token;
            }
        }

        public void EndFade(CancellationToken token)
        {
            lock (_fadeGate)
            {
                if (IsDisposed)
                {
                    _isVolumeTransitioning = false;
                    return;
                }
                if (_fadeCancellation.Token == token)
                    _isVolumeTransitioning = false;
            }
        }

        public void CancelFade()
        {
            lock (_fadeGate)
            {
                _fadeCancellation.Cancel();
                _isVolumeTransitioning = false;
            }
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            lock (_fadeGate)
            {
                _fadeCancellation.Cancel();
                _fadeCancellation.Dispose();
                _isVolumeTransitioning = false;
            }
            Output.Dispose();
            Reader.Dispose();
        }
    }

    private readonly object _gate = new();
    private readonly List<Voice> _voices = [];
    private readonly MMDeviceEnumerator _enumerator = new();
    private Voice? _primary;
    private string? _deviceId;
    private string? _secondaryDeviceId;
    private bool _useSecondaryDevice;
    private double _masterDb;
    private double _duckDb = -12;
    private double _fadeInSeconds;
    private double _fadeOutSeconds = 0.45;
    private bool _masterLimiterEnabled = true;
    private double _masterLimiterCeilingDbtp = -1;
    private bool _autoMixHeadroomEnabled = true;
    private double _talkDuckDb = -15;
    private double _talkGainDb;
    private bool _talkDuckingEnabled;

    public event EventHandler<PlaybackSnapshot>? SnapshotChanged;
    public event EventHandler<Jingle>? PlaybackCompleted;
    public event EventHandler<string>? PlaybackFailed;

    public IReadOnlyList<OutputDevice> GetOutputDevices()
    {
        var devices = new List<OutputDevice> { new("", "Windows standardenhet") };
        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    devices.Add(new OutputDevice(device.ID, device.FriendlyName));
                }
                catch { }
            }
        }
        catch { }
        return devices
            .Skip(1)
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .Prepend(devices[0])
            .ToList();
    }

    public void Configure(string? deviceId, string? secondaryDeviceId, double masterDb, double duckDb, double fadeInSeconds, double fadeOutSeconds,
        bool masterLimiterEnabled = true, double masterLimiterCeilingDbtp = -1, bool autoMixHeadroomEnabled = true,
        double talkDuckDb = -15)
    {
        _deviceId = deviceId;
        _secondaryDeviceId = secondaryDeviceId;
        _masterDb = masterDb;
        _duckDb = duckDb;
        _fadeInSeconds = Math.Max(0, fadeInSeconds);
        _fadeOutSeconds = Math.Clamp(fadeOutSeconds, 0, 30);
        _masterLimiterEnabled = masterLimiterEnabled;
        _masterLimiterCeilingDbtp = Math.Clamp(masterLimiterCeilingDbtp, -12, 0);
        _autoMixHeadroomEnabled = autoMixHeadroomEnabled;
        _talkDuckDb = Math.Clamp(talkDuckDb, -60, 0);
        lock (_gate)
        {
            if (_talkDuckingEnabled) _talkGainDb = _talkDuckDb;
            foreach (var voice in _voices)
            {
                ApplyVolume(voice);
                voice.TalkGain.SetTarget(DbToLinear(voice.UsesSecondaryDevice ? 0 : _talkGainDb), 0);
            }
        }
    }

    public Task SetTalkDuckingAsync(bool enabled, double seconds)
    {
        lock (_gate)
        {
            _talkDuckingEnabled = enabled;
            _talkGainDb = enabled ? _talkDuckDb : 0;
            var targetGain = DbToLinear(_talkGainDb);
            foreach (var voice in _voices.Where(voice => !voice.IsDisposed && !voice.StopRequested && !voice.UsesSecondaryDevice))
                voice.TalkGain.SetTarget(targetGain, Math.Clamp(seconds, 0, 10));
        }
        return Task.CompletedTask;
    }

    public PlaybackAction Play(Jingle jingle, bool honorJingleLoop = true, double? fadeInSecondsOverride = null,
        double? fadeOutPreviousSecondsOverride = null, bool releaseTalkDucking = true)
    {
        if (!File.Exists(jingle.FilePath))
            throw new FileNotFoundException("Ljudfilen kunde inte hittas.", jingle.FilePath);

        lock (_gate)
        {
            CleanupStopped();
            if (jingle.AllowMultipleClicks)
            {
                var activeInstanceCount = _voices.Count(voice => !voice.IsDisposed && !voice.StopRequested &&
                    voice.UsesSecondaryDevice == _useSecondaryDevice &&
                    (voice.Jingle.Id == jingle.Id || string.Equals(voice.Jingle.FilePath, jingle.FilePath, StringComparison.OrdinalIgnoreCase)));
                if (activeInstanceCount >= MaxConcurrentInstancesPerJingle)
                    return PlaybackAction.PolyphonyLimitReached;
            }
            var matching = jingle.AllowMultipleClicks
                ? Array.Empty<Voice>()
                : _voices.Where(voice => voice.UsesSecondaryDevice == _useSecondaryDevice &&
                    (voice.Jingle.Id == jingle.Id || string.Equals(voice.Jingle.FilePath, jingle.FilePath, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matching.Length > 0)
            {
                foreach (var active in matching)
                    _ = FadeOutVoiceAsync(active, fadeOutPreviousSecondsOverride ?? active.Jingle.FadeOutOverrideSeconds ?? _fadeOutSeconds);
                return PlaybackAction.FadingOut;
            }

            var previous = jingle.PlayMode is JinglePlayMode.Mix or JinglePlayMode.Duck
                ? Array.Empty<Voice>()
                : _voices.Where(voice => voice.UsesSecondaryDevice == _useSecondaryDevice && voice.Jingle.PlayMode != JinglePlayMode.Mix &&
                    !(jingle.AllowMultipleClicks && (voice.Jingle.Id == jingle.Id ||
                        string.Equals(voice.Jingle.FilePath, jingle.FilePath, StringComparison.OrdinalIgnoreCase)))).ToArray();
            var resetTalkForNewPrimary = releaseTalkDucking && !_useSecondaryDevice && jingle.PlayMode == JinglePlayMode.Solo;

            AudioFileReader? reader = null;
            WasapiOut? output = null;
            Voice? createdVoice = null;
            try
            {
                reader = new AudioFileReader(jingle.FilePath);
                reader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, jingle.StartSeconds));
                ISampleProvider source = reader;
                if (Math.Abs(jingle.PitchSemitones) >= .01)
                    source = new SmbPitchShiftingSampleProvider(source) { PitchFactor = (float)Math.Pow(2, jingle.PitchSemitones / 12) };
                var volume = new VolumeSampleProvider(source);
                var effects = new DjEffectsSampleProvider(volume, jingle, _masterLimiterCeilingDbtp, _masterLimiterEnabled);
                var talkGain = new SmoothGainSampleProvider(effects,
                    DbToLinear(_useSecondaryDevice || resetTalkForNewPrimary ? 0 : _talkGainDb));
                var meter = new MeteringSampleProvider(talkGain);
                output = new WasapiOut(ResolveDevice(), AudioClientShareMode.Shared, true, 50);
                var voice = new Voice
                {
                    Jingle = jingle,
                    Reader = reader,
                    Output = output,
                    Volume = volume,
                    Effects = effects,
                    TalkGain = talkGain,
                    LoopEnabled = honorJingleLoop && jingle.Loop,
                    UsesSecondaryDevice = _useSecondaryDevice
                };
                createdVoice = voice;
                meter.StreamVolume += (_, e) =>
                {
                    voice.PeakLeft = e.MaxSampleValues.ElementAtOrDefault(0);
                    voice.PeakRight = e.MaxSampleValues.ElementAtOrDefault(1);
                };
                output.Init(meter.ToWaveProvider());
                if (resetTalkForNewPrimary && _talkDuckingEnabled)
                {
                    _talkDuckingEnabled = false;
                    _talkGainDb = 0;
                    foreach (var activeVoice in _voices.Where(candidate => !candidate.IsDisposed && !candidate.StopRequested && !candidate.UsesSecondaryDevice))
                        activeVoice.TalkGain.SetTarget(1, _fadeInSeconds);
                }
                output.PlaybackStopped += (_, e) => OnStopped(voice, e.Exception);
                _voices.Add(voice);
                if (jingle.AllowMultipleClicks)
                {
                    var polyphonyGroup = _voices.Where(candidate => !candidate.IsDisposed && !candidate.StopRequested &&
                        candidate.UsesSecondaryDevice == voice.UsesSecondaryDevice && candidate.Jingle.AllowMultipleClicks &&
                        (candidate.Jingle.Id == jingle.Id || string.Equals(candidate.Jingle.FilePath, jingle.FilePath, StringComparison.OrdinalIgnoreCase))).ToArray();
                    // Samma signal kan summeras nästan helt i fas. Reservera därför
                    // 20*log10(n) dB i stället för vanlig mix-headroom. Behåll den
                    // mest konservativa nivån tills varje instans är klar, så att en
                    // avslutad kopia inte orsakar ett plötsligt volymhopp i de andra.
                    var groupHeadroomDb = -20 * Math.Log10(Math.Max(1, polyphonyGroup.Length));
                    foreach (var instance in polyphonyGroup)
                        instance.PolyphonyHeadroomDb = Math.Min(instance.PolyphonyHeadroomDb, groupHeadroomDb);
                }
                _primary = voice;
                var fadeInSeconds = fadeInSecondsOverride ?? jingle.FadeInOverrideSeconds ?? _fadeInSeconds;
                voice.Volume.Volume = fadeInSeconds > 0 ? 0 : TargetVolume(voice);
                output.Play();
                // Markera de utgående rösterna innan den nya fade-in-kurvan beräknas.
                // En vanlig crossfade ska inte tolkas som två avsiktligt samtidiga ljud,
                // annars släpps mix-headroomet när den gamla rösten försvinner och den
                // nya låten får ett hörbart nivåhopp i slutet av fade-in.
                foreach (var oldVoice in previous)
                    _ = FadeOutVoiceAsync(oldVoice, fadeOutPreviousSecondsOverride ?? oldVoice.Jingle.FadeOutOverrideSeconds ?? _fadeOutSeconds);
                if (fadeInSeconds > 0) _ = FadeInAsync(voice, fadeInSeconds);
                RefreshActiveVolumes();
                return PlaybackAction.Started;
            }
            catch
            {
                if (createdVoice is not null)
                {
                    _voices.Remove(createdVoice);
                    if (_primary == createdVoice) _primary = _voices.LastOrDefault();
                }
                output?.Dispose();
                reader?.Dispose();
                throw;
            }
        }
    }

    public void SetSecondaryOutput(bool enabled)
    {
        lock (_gate) _useSecondaryDevice = enabled;
    }

    public void StopSecondaryOutput()
    {
        Voice[] voices;
        lock (_gate) voices = _voices.Where(voice => voice.UsesSecondaryDevice).ToArray();
        foreach (var voice in voices)
        {
            voice.StopRequested = true;
            try { voice.Output.Stop(); } catch { }
        }
        PublishSnapshot();
    }

    public async Task PauseOrResumeAsync()
    {
        Voice? voice;
        var resume = false;
        lock (_gate)
        {
            voice = _primary;
            if (voice is null) return;
            if (voice.StopRequested) return;
            if (voice.Paused || voice.PauseRequested)
            {
                voice.CancelFade();
                voice.Volume.Volume = 0;
                voice.Output.Play();
                voice.Paused = false;
                voice.PauseRequested = false;
                resume = true;
            }
            else voice.PauseRequested = true;
        }
        if (resume)
        {
            await FadeInAsync(voice, _fadeInSeconds);
            return;
        }

        await FadeToPauseAsync(voice, _fadeOutSeconds);
    }

    private async Task FadeToPauseAsync(Voice voice, double seconds)
    {
        if (voice.IsDisposed) return;
        var cancellationToken = voice.BeginFade();
        try
        {
            float startVolume;
            try { startVolume = voice.Volume.Volume; } catch (ObjectDisposedException) { return; }
            var steps = Math.Max(1, (int)(seconds * 100));
            for (var step = steps - 1; step >= 0; step--)
            {
                if (voice.IsDisposed || cancellationToken.IsCancellationRequested) return;
                try { voice.Volume.Volume = startVolume * step / steps; } catch (ObjectDisposedException) { return; }
                if (!await DelayFadeStepAsync(cancellationToken)) return;
            }
            lock (_gate)
            {
                if (voice.IsDisposed || cancellationToken.IsCancellationRequested || !_voices.Contains(voice)) return;
                voice.Output.Pause();
                voice.Paused = true;
                voice.PauseRequested = false;
            }
            PublishSnapshot();
        }
        finally { voice.EndFade(cancellationToken); }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
            if (_primary is not null)
            {
                var start = TimeSpan.FromSeconds(Math.Max(0, _primary.Jingle.StartSeconds));
                var end = _primary.Jingle.EndSeconds is double seconds
                    ? TimeSpan.FromSeconds(seconds) : _primary.Reader.TotalTime;
                _primary.Reader.CurrentTime = start + TimeSpan.FromTicks(
                    Math.Clamp(position.Ticks, 0, Math.Max(0, (end - start).Ticks)));
            }
    }

    public TimeSpan? GetCurrentPosition()
    {
        lock (_gate)
        {
            if (_primary is null) return null;
            var start = TimeSpan.FromSeconds(Math.Max(0, _primary.Jingle.StartSeconds));
            return _primary.Reader.CurrentTime > start ? _primary.Reader.CurrentTime - start : TimeSpan.Zero;
        }
    }

    public async Task FadeOutAllAsync(double seconds)
    {
        List<Voice> voices;
        lock (_gate) voices = [.. _voices];
        await Task.WhenAll(voices.Select(voice => FadeOutVoiceAsync(voice, seconds)));
        PublishSnapshot();
    }

    public async Task FadeOutPrimaryOutputAsync(double seconds)
    {
        List<Voice> voices;
        lock (_gate) voices = _voices.Where(voice => !voice.UsesSecondaryDevice).ToList();
        await Task.WhenAll(voices.Select(voice => FadeOutVoiceAsync(voice, seconds)));
        PublishSnapshot();
    }

    public void StopAll(bool notify = true)
    {
        Voice[] voices;
        lock (_gate)
        {
            voices = _voices.ToArray();
            foreach (var voice in voices) voice.StopRequested = true;
            _voices.Clear();
            _primary = null;
        }
        foreach (var voice in voices)
        {
            voice.CancelFade();
            try { voice.Output.Stop(); } catch { }
            voice.Dispose();
        }
        if (notify) PublishSnapshot();
    }

    public void PublishSnapshot()
    {
        List<Voice> reachedEnd = [];
        PlaybackSnapshot snapshot;
        lock (_gate)
        {
            CleanupStopped();
            foreach (var voice in _voices.Where(candidate => !candidate.IsDisposed && !candidate.StopRequested && !candidate.NaturalEndRequested).ToArray())
            {
                if (voice.Jingle.EndSeconds is not double endSeconds || voice.Reader.CurrentTime.TotalSeconds < endSeconds) continue;
                if (voice.LoopEnabled)
                    voice.Reader.CurrentTime = TimeSpan.FromSeconds(voice.Jingle.StartSeconds);
                else
                {
                    voice.NaturalEndRequested = true;
                    reachedEnd.Add(voice);
                }
            }
            if (_primary is null)
                snapshot = new(null, "Redo för nästa jingle", "", TimeSpan.Zero, TimeSpan.Zero, -60, -60, false, false);
            else
            {
                var start = TimeSpan.FromSeconds(Math.Max(0, _primary.Jingle.StartSeconds));
                var end = _primary.Jingle.EndSeconds is double seconds
                    ? TimeSpan.FromSeconds(seconds) : _primary.Reader.TotalTime;
                var duration = end > start ? end - start : TimeSpan.Zero;
                var position = _primary.Reader.CurrentTime > start ? _primary.Reader.CurrentTime - start : TimeSpan.Zero;
                snapshot = new(_primary.Jingle.Id, _primary.Jingle.Title, _primary.Jingle.FilePath, position, duration,
                    LinearToDb(_primary.PeakLeft), LinearToDb(_primary.PeakRight),
                    _primary.Output.PlaybackState == PlaybackState.Playing, _primary.Paused);
            }
        }
        foreach (var voice in reachedEnd)
            try { voice.Output.Stop(); } catch { }
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private MMDevice ResolveDevice()
    {
        var selectedId = _useSecondaryDevice ? _secondaryDeviceId : _deviceId;
        if (!string.IsNullOrWhiteSpace(selectedId))
            try { return _enumerator.GetDevice(selectedId); } catch { }
        return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private void OnStopped(Voice voice, Exception? error)
    {
        var completed = false;
        lock (_gate)
        {
            if (error is null && !voice.StopRequested && voice.LoopEnabled && !voice.IsDisposed)
            {
                voice.Reader.CurrentTime = TimeSpan.FromSeconds(voice.Jingle.StartSeconds);
                voice.Output.Play();
                return;
            }
            if (!_voices.Remove(voice)) return;
            if (_primary == voice) _primary = _voices.LastOrDefault();
            var sameJingleStillPlaying = voice.Jingle.AllowMultipleClicks && _voices.Any(candidate =>
                !candidate.IsDisposed && !candidate.StopRequested && !candidate.NaturalEndRequested && candidate.UsesSecondaryDevice == voice.UsesSecondaryDevice &&
                (candidate.Jingle.Id == voice.Jingle.Id || string.Equals(candidate.Jingle.FilePath, voice.Jingle.FilePath, StringComparison.OrdinalIgnoreCase)));
            completed = error is null && !voice.StopRequested && !sameJingleStillPlaying;
            RefreshActiveVolumes();
        }
        voice.Dispose();
        PublishSnapshot();
        if (error is not null) PlaybackFailed?.Invoke(this, $"{voice.Jingle.Title}: {error.Message}");
        if (completed) PlaybackCompleted?.Invoke(this, voice.Jingle);
    }

    private void CleanupStopped()
    {
        foreach (var voice in _voices.Where(x => x.Output.PlaybackState == PlaybackState.Stopped).ToArray())
        {
            _voices.Remove(voice);
            voice.Dispose();
        }
        if (_primary is not null && !_voices.Contains(_primary)) _primary = _voices.LastOrDefault();
        RefreshActiveVolumes();
    }

    private async Task FadeInAsync(Voice voice, double seconds)
    {
        if (voice.IsDisposed) return;
        var cancellationToken = voice.BeginFade();
        try
        {
            var steps = Math.Max(1, (int)(seconds * 100));
            try { voice.Volume.Volume = 0; }
            catch (ObjectDisposedException) { return; }
            for (var i = 1; i <= steps && !voice.IsDisposed; i++)
            {
                if (cancellationToken.IsCancellationRequested) return;
                try { voice.Volume.Volume = TargetVolume(voice) * i / steps; }
                catch (ObjectDisposedException) { return; }
                if (!await DelayFadeStepAsync(cancellationToken)) return;
            }
        }
        finally { voice.EndFade(cancellationToken); }
    }

    private async Task FadeOutVoiceAsync(Voice voice, double seconds)
    {
        if (voice.IsDisposed) return;
        var cancellationToken = voice.BeginFade();
        try
        {
            voice.PauseRequested = false;
            // Markera avsiktlig toning direkt. Annars kan filen nå sitt naturliga slut
            // under fade-jobbet och felaktigt utlösa PlaybackCompleted en extra gång.
            voice.StopRequested = true;
            float startVolume;
            try { startVolume = voice.Volume.Volume; }
            catch (ObjectDisposedException) { return; }
            var steps = Math.Max(1, (int)(seconds * 100));
            for (var step = steps - 1; step >= 0; step--)
            {
                if (voice.IsDisposed || cancellationToken.IsCancellationRequested) return;
                try { voice.Volume.Volume = startVolume * step / steps; }
                catch (ObjectDisposedException) { return; }
                if (!await DelayFadeStepAsync(cancellationToken)) return;
            }

            lock (_gate)
            {
                if (cancellationToken.IsCancellationRequested || !_voices.Remove(voice)) return;
                if (_primary == voice) _primary = _voices.LastOrDefault();
                RefreshActiveVolumes();
            }
            try { voice.Output.Stop(); } catch { }
            voice.Dispose();
        }
        finally { voice.EndFade(cancellationToken); }
    }

    private static async Task<bool> DelayFadeStepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(10, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    private void ApplyVolume(Voice voice) => voice.Volume.Volume = TargetVolume(voice);
    private float TargetVolume(Voice voice)
    {
        var gainDb = voice.Jingle.GainDb + (voice.Jingle.NormalizationEnabled ? voice.Jingle.NormalizationGainDb : 0) +
            _masterDb + voice.PolyphonyHeadroomDb;
        // Röster som redan tonas ut är en del av en crossfade, inte en bestående mix.
        // Genom att utesluta dem hålls den nya röstens gain konstant under hela starten.
        var activeOnDevice = _voices.Where(candidate => !candidate.IsDisposed && !candidate.StopRequested && !candidate.NaturalEndRequested &&
            candidate.UsesSecondaryDevice == voice.UsesSecondaryDevice).ToArray();
        if (activeOnDevice.Any(candidate => candidate != voice && candidate.Jingle.PlayMode == JinglePlayMode.Duck) && voice.Jingle.PlayMode != JinglePlayMode.Duck)
            gainDb += _duckDb;
        var containsMixVoice = activeOnDevice.Any(candidate => candidate.Jingle.PlayMode == JinglePlayMode.Mix);
        var logicalLayerCount = activeOnDevice.Count(candidate => !candidate.Jingle.AllowMultipleClicks) +
            activeOnDevice.Where(candidate => candidate.Jingle.AllowMultipleClicks).Select(candidate => candidate.Jingle.Id).Distinct().Count();
        if (_autoMixHeadroomEnabled && logicalLayerCount > 1 && !containsMixVoice)
        {
            // Mix is deliberately additive and must not alter the sound already playing.
            // Its own level is controlled by the gain on the Mix jingle.
            gainDb -= 3.0103 * Math.Log(logicalLayerCount, 2);
        }
        return Math.Clamp(DbToLinear(gainDb), 0, 4);
    }

    private void RefreshActiveVolumes()
    {
        foreach (var active in _voices.Where(voice => !voice.IsDisposed && !voice.StopRequested && !voice.NaturalEndRequested && !voice.IsVolumeTransitioning))
            ApplyVolume(active);
    }
    public void RefreshVolumes()
    {
        lock (_gate) RefreshActiveVolumes();
    }
    private static float DbToLinear(double db) => (float)Math.Pow(10, db / 20);
    private static float LinearToDb(float value) => value <= 0.0001f ? -60 : Math.Max(-60, 20f * MathF.Log10(value));
    public void Dispose() { StopAll(false); _enumerator.Dispose(); }
}
