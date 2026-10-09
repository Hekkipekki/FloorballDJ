using FloorballDJ.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FloorballDJ.Services;

public enum PlaybackAction { Started, FadingOut, PolyphonyLimitReached }

public sealed class AudioEngine : IDisposable
{
    private const int MaxConcurrentInstancesPerJingle = 8;
    private const double MinimumStartRampSeconds = 0.03;
    private static readonly PlaybackSnapshot EmptyPrimarySnapshot = new(null, "Redo för nästa jingle", "", TimeSpan.Zero, TimeSpan.Zero, -60, -60, false, false);
    private static readonly PlaybackSnapshot EmptySecondarySnapshot = new(null, "Ingen förlyssning", "", TimeSpan.Zero, TimeSpan.Zero, -60, -60, false, false);
    private readonly record struct VolumeInputs(Guid Id, bool Active, bool Multiple, JinglePlayMode Mode,
        double GainDb, bool Normalized, double NormalizationGainDb, double PolyphonyHeadroomDb);
    private sealed class Voice : IDisposable
    {
        private readonly object _fadeGate = new();
        private CancellationTokenSource _fadeCancellation = new();
        private volatile bool _isVolumeTransitioning;

        public required Jingle Jingle { get; init; }
        public required AudioFileSeekSampleProvider SeekProvider { get; init; }
        public required AudioFileReader Reader { get; init; }
        public required IVoiceOutput Output { get; init; }
        // Statisk nivå: manuell gain, LUFS-normalisering och mastervolym.
        public required VolumeSampleProvider Volume { get; init; }
        public required DjEffectsSampleProvider Effects { get; init; }
        // Uppspelningsfadern ligger efter effekterna. Då kan kompressorn inte
        // hålla kvar signalen precis innan rösten ska nå fullständig tystnad.
        public required SmoothGainSampleProvider FadeVolume { get; init; }
        public required SmoothGainSampleProvider TalkGain { get; init; }
        public PerformanceOperation Performance { get; init; }
        public double PlaybackStartSeconds { get; init; }
        public bool Paused { get; set; }
        public bool PauseRequested { get; set; }
        public bool CanResumeSpaceFade { get; set; }
        public float PeakLeft { get; set; }
        public float PeakRight { get; set; }
        public bool IsDisposed { get; private set; }
        public bool StopRequested { get; set; }
        public bool NaturalEndRequested { get; set; }
        public bool LoopEnabled { get; init; }
        public bool UsesSecondaryDevice { get; init; }
        public double PolyphonyHeadroomDb { get; set; }
        public double PlaybackGainOffsetDb { get; init; }
        public VolumeInputs VolumeInputs { get; set; }
        public bool HasVolumeInputs { get; set; }
        public float TargetVolume { get; set; }
        public bool VolumeApplied { get; set; }
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
    private readonly AudioOutputPool? _outputPool;
    private readonly OutputNotifications _outputNotifications;
    private readonly PrimaryOutputKeepAlive _keepAlive = new();
    private bool _notificationsRegistered;
    internal PrimaryOutputKeepAlive KeepAlive => _keepAlive;
    private int _disposed;
    private Voice? _primary;
    private string? _deviceId;
    private string? _secondaryDeviceId;
    private bool _useSecondaryDevice;
    private double _masterDb;
    private double _duckDb = -12;
    private double _fadeInSeconds;
    private double _fadeOutSeconds = 0.45;
    private bool _masterLimiterEnabled = true;
    private bool _primaryLimiterBypassed;
    private double _masterLimiterCeilingDbtp = -1;
    private bool _autoMixHeadroomEnabled = true;
    private double _talkDuckDb = -15;
    private double _talkGainDb;
    private bool _talkDuckingEnabled;
    private double _secondaryMonitorDb;
    private bool _volumeTargetsDirty = true;
    private int _volumeVoiceCount = -1;
    private long _volumeStateRebuilds;
    private long _volumeTargetCalculations;
    private sealed class VolumeGroup
    {
        internal readonly HashSet<Guid> MultipleIds = [];
        internal int SingleLayers;
        internal bool ContainsDuck;
        internal bool ContainsMix;
        internal double HeadroomDb;
        internal void Clear()
        {
            MultipleIds.Clear();
            SingleLayers = 0;
            ContainsDuck = ContainsMix = false;
            HeadroomDb = 0;
        }
    }
    private readonly VolumeGroup _primaryVolumeGroup = new();
    private readonly VolumeGroup _secondaryVolumeGroup = new();
    internal (long Rebuilds, long Targets) VolumeWork { get { lock (_gate) return (_volumeStateRebuilds, _volumeTargetCalculations); } }

    public event EventHandler<PlaybackSnapshot>? SnapshotChanged;
    public event EventHandler<Jingle>? PlaybackCompleted;
    public event EventHandler<string>? PlaybackFailed;

    public AudioEngine() : this(AudioOutputPool.Requested) { }
    internal AudioEngine(bool reuseAudioOutputs)
    {
        if (reuseAudioOutputs) _outputPool = new AudioOutputPool();
        _outputNotifications = new OutputNotifications(_outputPool, _keepAlive);
        if (reuseAudioOutputs) RegisterNotifications();
    }

    private void RegisterNotifications()
    {
        if (_notificationsRegistered) return;
        try { _enumerator.RegisterEndpointNotificationCallback(_outputNotifications); } catch { }
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _notificationsRegistered = true;
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs args)
    {
        _outputPool?.Invalidate(disposeAsynchronously: true);
        _keepAlive.Invalidate();
    }

    private sealed class OutputNotifications(AudioOutputPool? pool, PrimaryOutputKeepAlive keepAlive) : IMMNotificationClient
    {
        private void Changed() { pool?.Invalidate(true); keepAlive.Invalidate(); }
        public void OnDeviceStateChanged(string deviceId, DeviceState state) => Changed();
        public void OnDeviceAdded(string deviceId) => Changed();
        public void OnDeviceRemoved(string deviceId) => Changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        { if (flow == DataFlow.Render && role == Role.Multimedia) Changed(); }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) => pool?.Invalidate(true);
    }

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
        double talkDuckDb = -15, bool keepPrimaryOutputActive = false)
    {
        lock (_gate)
        {
            if (keepPrimaryOutputActive) RegisterNotifications();
            _keepAlive.Configure(keepPrimaryOutputActive, deviceId);
            if (!string.Equals(_deviceId, deviceId, StringComparison.Ordinal) ||
                !string.Equals(_secondaryDeviceId, secondaryDeviceId, StringComparison.Ordinal)) _outputPool?.Invalidate();
            _volumeTargetsDirty |= !_masterDb.Equals(masterDb) || !_duckDb.Equals(duckDb) || _autoMixHeadroomEnabled != autoMixHeadroomEnabled;
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
            if (_talkDuckingEnabled) _talkGainDb = _talkDuckDb;
            EnsureVolumeTargets();
            foreach (var voice in _voices)
            {
                voice.Effects.SetLimiterEnabled(_masterLimiterEnabled && (voice.UsesSecondaryDevice || !_primaryLimiterBypassed));
                ApplyPreparedVolume(voice);
                voice.TalkGain.SetTarget(DbToLinear(voice.UsesSecondaryDevice ? 0 : _talkGainDb), 0);
            }
        }
    }

    // Session-only override. Settings/normalization and secondary protection are
    // unchanged; active primary voices update at the next provider buffer.
    public void SetPrimaryLimiterBypassed(bool bypassed)
    {
        lock (_gate)
        {
            _primaryLimiterBypassed = bypassed;
            foreach (var voice in _voices.Where(voice => !voice.UsesSecondaryDevice))
                voice.Effects.SetLimiterEnabled(_masterLimiterEnabled && !bypassed);
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
        double? fadeOutPreviousSecondsOverride = null, bool releaseTalkDucking = true,
        TimeSpan? initialClipPosition = null, double? playbackStartSecondsOverride = null,
        double playbackGainOffsetDb = 0)
    {
        var performance = PerformanceDiagnostics.BeginOperation("PlaybackRequested");
        using var preparation = performance.Measure("PlaybackPreparation");
        if (performance.Enabled)
        {
            performance.Mark("OutputLifecycle", detail: _outputPool is null ? "perVoice" : "reusePrototype");
            performance.Mark("JingleIdentity", detail: jingle.Id.ToString("N"));
            performance.Mark("SourceExtension", detail: Path.GetExtension(jingle.FilePath).ToLowerInvariant());
            performance.Mark("PlaybackMode", detail: jingle.PlayMode.ToString());
            performance.Mark("OutputRoute", detail: _useSecondaryDevice ? "secondary" : "primary");
        }
        if (!File.Exists(jingle.FilePath))
        {
            performance.Mark("PlaybackFailed", detail: "FileNotFoundException");
            throw new FileNotFoundException("Ljudfilen kunde inte hittas.", jingle.FilePath);
        }

        var lockRequested = performance.Timestamp;
        lock (_gate)
        {
            performance.Duration("EngineLockWait", lockRequested);
            CleanupStopped();
            if (jingle.AllowMultipleClicks)
            {
                var activeInstanceCount = _voices.Count(voice => !voice.IsDisposed && !voice.StopRequested &&
                    voice.UsesSecondaryDevice == _useSecondaryDevice &&
                    (voice.Jingle.Id == jingle.Id || string.Equals(voice.Jingle.FilePath, jingle.FilePath, StringComparison.OrdinalIgnoreCase)));
                if (activeInstanceCount >= MaxConcurrentInstancesPerJingle)
                {
                    performance.Mark("PlaybackAction", detail: "PolyphonyLimitReached");
                    return PlaybackAction.PolyphonyLimitReached;
                }
            }
            var matching = jingle.AllowMultipleClicks
                ? Array.Empty<Voice>()
                : _voices.Where(voice => voice.UsesSecondaryDevice == _useSecondaryDevice &&
                    (voice.Jingle.Id == jingle.Id || string.Equals(voice.Jingle.FilePath, jingle.FilePath, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matching.Length > 0)
            {
                foreach (var active in matching)
                    _ = FadeOutVoiceAsync(active, fadeOutPreviousSecondsOverride ?? active.Jingle.FadeOutOverrideSeconds ?? _fadeOutSeconds);
                performance.Mark("PlaybackAction", detail: "FadingOut");
                return PlaybackAction.FadingOut;
            }

            var previous = jingle.PlayMode is JinglePlayMode.Mix or JinglePlayMode.Duck
                ? Array.Empty<Voice>()
                : _voices.Where(voice => voice.UsesSecondaryDevice == _useSecondaryDevice && voice.Jingle.PlayMode != JinglePlayMode.Mix &&
                    !(jingle.AllowMultipleClicks && (voice.Jingle.Id == jingle.Id ||
                        string.Equals(voice.Jingle.FilePath, jingle.FilePath, StringComparison.OrdinalIgnoreCase)))).ToArray();
            var resetTalkForNewPrimary = releaseTalkDucking && !_useSecondaryDevice && jingle.PlayMode == JinglePlayMode.Solo;

            AudioFileReader? reader = null;
            IVoiceOutput? output = null;
            Voice? createdVoice = null;
            try
            {
                using (performance.Measure("FileOpen")) reader = new AudioFileReader(jingle.FilePath);
                if (performance.Enabled)
                    performance.Mark("SourceFormat", detail: $"{reader.WaveFormat.SampleRate}Hz/{reader.WaveFormat.Channels}ch");
                var providersStarted = performance.Timestamp;
                var playbackStartSeconds = Math.Max(0, playbackStartSecondsOverride ?? jingle.StartSeconds);
                var clipStart = TimeSpan.FromSeconds(playbackStartSeconds);
                var clipEnd = jingle.EndSeconds is double endSeconds
                    ? TimeSpan.FromSeconds(Math.Max(playbackStartSeconds, endSeconds))
                    : reader.TotalTime;
                if (clipStart > reader.TotalTime) clipStart = reader.TotalTime;
                var requestedOffset = initialClipPosition ?? TimeSpan.Zero;
                var seekProvider = new AudioFileSeekSampleProvider(reader, jingle.FilePath, performance);
                seekProvider.Seek(clipStart + TimeSpan.FromTicks(
                    Math.Clamp(requestedOffset.Ticks, 0, Math.Max(0, (clipEnd - clipStart).Ticks))));
                ISampleProvider source = seekProvider;
                if (Math.Abs(jingle.PitchSemitones) >= .01)
                    source = new SmbPitchShiftingSampleProvider(source) { PitchFactor = (float)Math.Pow(2, jingle.PitchSemitones / 12) };
                var volume = new VolumeSampleProvider(source);
                var effects = new DjEffectsSampleProvider(volume, jingle, _masterLimiterCeilingDbtp,
                    _masterLimiterEnabled && (_useSecondaryDevice || !_primaryLimiterBypassed));
                var fadeVolume = new SmoothGainSampleProvider(effects, 0);
                var talkGain = new SmoothGainSampleProvider(fadeVolume,
                    DbToLinear(_useSecondaryDevice || resetTalkForNewPrimary ? 0 : _talkGainDb));
                var meter = new MeteringSampleProvider(talkGain);
                performance.Duration("ProviderConstruction", providersStarted);
                MMDevice device;
                string? endpointId = null;
                using (performance.Measure("EndpointResolution"))
                {
                    device = ResolveDevice();
                    if (_outputPool is not null) endpointId = device.ID;
                }
                if (performance.Enabled)
                {
                    // Avoid diagnostic COM/property-store queries on the playback path.
                    performance.Mark("EndpointSelection", detail: string.IsNullOrWhiteSpace(
                        _useSecondaryDevice ? _secondaryDeviceId : _deviceId) ? "windowsDefault" : "explicit");
                    performance.Mark("RequestedBufferMs", 50);
                }
                ISampleProvider outputSource = performance.Enabled ? new PerformanceSampleProvider(meter, performance) : meter;
                var waveSource = outputSource.ToWaveProvider();
                if (_outputPool is not null)
                {
                    try
                    {
                        output = _outputPool.Acquire(endpointId!, _useSecondaryDevice, waveSource, () =>
                        {
                            // Snapshot stop events on the audio thread before a lease can
                            // be handed off; engine handlers run outside that callback.
                            return CreateOutput(device);
                        }, performance);
                    }
                    finally { device.Dispose(); }
                }
                else
                {
                    WasapiOut direct;
                    using (performance.Measure("OutputConstruction")) direct = CreateOutput(device);
                    output = new DirectVoiceOutput(direct);
                    using (performance.Measure("OutputInitialization")) direct.Init(waveSource);
                }
                var voice = new Voice
                {
                    Jingle = jingle,
                    Reader = reader,
                    Output = output,
                    Volume = volume,
                    SeekProvider = seekProvider,
                    Effects = effects,
                    FadeVolume = fadeVolume,
                    TalkGain = talkGain,
                    Performance = performance,
                    PlaybackStartSeconds = clipStart.TotalSeconds,
                    LoopEnabled = honorJingleLoop && jingle.Loop,
                    UsesSecondaryDevice = _useSecondaryDevice,
                    PlaybackGainOffsetDb = Math.Clamp(playbackGainOffsetDb, -60, 12)
                };
                createdVoice = voice;
                meter.StreamVolume += (_, e) =>
                {
                    voice.PeakLeft = e.MaxSampleValues.ElementAtOrDefault(0);
                    voice.PeakRight = e.MaxSampleValues.ElementAtOrDefault(1);
                };
                if (resetTalkForNewPrimary && _talkDuckingEnabled)
                {
                    _talkDuckingEnabled = false;
                    _talkGainDb = 0;
                    foreach (var activeVoice in _voices.Where(candidate => !candidate.IsDisposed && !candidate.StopRequested && !candidate.UsesSecondaryDevice))
                        activeVoice.TalkGain.SetTarget(1, _fadeInSeconds);
                }
                output.PlaybackStopped += (_, e) => OnStopped(voice, e.Exception);
                _voices.Add(voice);
                _volumeTargetsDirty = true;
                if (jingle.AllowMultipleClicks)
                {
                    var polyphonyGroup = _voices.Where(candidate => !candidate.IsDisposed && !candidate.StopRequested &&
                        candidate.UsesSecondaryDevice == voice.UsesSecondaryDevice && candidate.Jingle.AllowMultipleClicks &&
                        (candidate.Jingle.Id == jingle.Id || string.Equals(candidate.Jingle.FilePath, jingle.FilePath, StringComparison.OrdinalIgnoreCase))).ToArray();
                    // En ny kopia får egen säkerhetsmarginal, men redan spelande
                    // kopior ändras aldrig i efterhand. Tidigare sänktes hela gruppen
                    // på nytt vid varje klick, vilket lät som att effekten blev svagare
                    // och svagare. Masterlimitern fångar fortfarande extrema toppar.
                    voice.PolyphonyHeadroomDb = _autoMixHeadroomEnabled
                        ? -20 * Math.Log10(Math.Max(1, polyphonyGroup.Length))
                        : 0;
                }
                _primary = voice;
                var requestedFadeInSeconds = fadeInSecondsOverride ?? jingle.FadeInOverrideSeconds ?? _fadeInSeconds;
                // Även ett uttryckligt värde på 0 får en ohörbart kort de-click-ramp.
                // Att öppna en signal mitt i en vågform på full nivå kan annars låta
                // som ett kort främmande klick från den föregående jinglen.
                var fadeInSeconds = Math.Max(MinimumStartRampSeconds, requestedFadeInSeconds);
                performance.Mark("EffectiveFadeInSeconds", fadeInSeconds);
                performance.Mark("OutputPlayRequested");
                using (performance.Measure("OutputPlayCall")) output.Play();
                // Markera de utgående rösterna innan den nya fade-in-kurvan beräknas.
                // En vanlig crossfade ska inte tolkas som två avsiktligt samtidiga ljud,
                // annars släpps mix-headroomet när den gamla rösten försvinner och den
                // nya låten får ett hörbart nivåhopp i slutet av fade-in.
                foreach (var oldVoice in previous)
                    _ = FadeOutVoiceAsync(oldVoice, fadeOutPreviousSecondsOverride ?? oldVoice.Jingle.FadeOutOverrideSeconds ?? _fadeOutSeconds);
                // Den statiska nivån ska vara färdig innan den separata
                // uppspelningsfadern börjar röra sig från 0 till 1.
                ApplyVolume(voice);
                _ = FadeInAsync(voice, fadeInSeconds);
                RefreshActiveVolumes();
                performance.Mark("PlaybackAction", detail: "Started");
                return PlaybackAction.Started;
            }
            catch (Exception exception)
            {
                performance.Mark("PlaybackFailed", detail: exception.GetType().Name);
                if (createdVoice is not null)
                {
                    _voices.Remove(createdVoice);
                    if (_primary == createdVoice) _primary = _voices.LastOrDefault();
                }
                output?.Discard();
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

    public void SetSecondaryMonitorVolumeDb(double db)
    {
        lock (_gate)
        {
            var normalized = Math.Clamp(db, -60, 12);
            _volumeTargetsDirty |= !_secondaryMonitorDb.Equals(normalized);
            _secondaryMonitorDb = normalized;
            RefreshActiveVolumes();
        }
    }

    public void SeekSecondary(TimeSpan position)
    {
        lock (_gate)
        {
            var voice = _voices.LastOrDefault(candidate => candidate.UsesSecondaryDevice && !candidate.IsDisposed);
            if (voice is null) return;
            var start = TimeSpan.FromSeconds(voice.PlaybackStartSeconds);
            var end = voice.Jingle.EndSeconds is double seconds ? TimeSpan.FromSeconds(seconds) : voice.Reader.TotalTime;
            voice.SeekProvider.Seek(start + TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Math.Max(0, (end - start).Ticks))));
        }
    }

    public PlaybackSnapshot GetSecondarySnapshot()
    {
        lock (_gate)
        {
            var voice = _voices.LastOrDefault(candidate => candidate.UsesSecondaryDevice && !candidate.IsDisposed && !candidate.StopRequested);
            return CreateSnapshot(voice, EmptySecondarySnapshot);
        }
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
            voice.FadeVolume.SetTarget(0, FadeSecondsRemaining(voice, seconds));
            if (!await WaitForRenderedFadeAsync(voice, cancellationToken)) return;
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
                var start = TimeSpan.FromSeconds(_primary.PlaybackStartSeconds);
                var end = _primary.Jingle.EndSeconds is double seconds
                    ? TimeSpan.FromSeconds(seconds) : _primary.Reader.TotalTime;
                _primary.SeekProvider.Seek(start + TimeSpan.FromTicks(
                    Math.Clamp(position.Ticks, 0, Math.Max(0, (end - start).Ticks))));
            }
    }

    public TimeSpan? GetCurrentPosition()
    {
        lock (_gate)
        {
            if (_primary is null) return null;
            var start = TimeSpan.FromSeconds(_primary.PlaybackStartSeconds);
            return _primary.Reader.CurrentTime > start ? _primary.Reader.CurrentTime - start : TimeSpan.Zero;
        }
    }

    public bool TryResumeSpaceFade()
    {
        var resumed = false;
        lock (_gate)
        {
            foreach (var voice in _voices.Where(v => !v.IsDisposed && v.StopRequested && v.CanResumeSpaceFade))
            {
                voice.CancelFade();
                voice.StopRequested = false;
                voice.CanResumeSpaceFade = false;
                // Reverse from the current gain, never jump to silence or seek.
                voice.FadeVolume.SetTarget(1, .08);
                resumed = true;
            }
            if (resumed) RefreshActiveVolumes();
        }
        if (resumed) PublishSnapshot();
        return resumed;
    }

    public async Task FadeOutAllAsync(double seconds, bool allowSpaceResume = false)
    {
        List<Voice> voices;
        lock (_gate) voices = [.. _voices];
        await Task.WhenAll(voices.Select(voice => FadeOutVoiceAsync(voice, seconds, allowSpaceResume)));
        PublishSnapshot();
    }

    public async Task FadeOutPrimaryOutputAsync(double seconds, bool allowSpaceResume = false)
    {
        List<Voice> voices;
        lock (_gate) voices = _voices.Where(voice => !voice.UsesSecondaryDevice).ToList();
        await Task.WhenAll(voices.Select(voice => FadeOutVoiceAsync(voice, seconds, allowSpaceResume)));
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

    public Task StopAllDeClickedAsync() => FadeOutAllAsync(MinimumStartRampSeconds);

    public void PublishSnapshot()
    {
        List<Voice>? reachedEnd = null;
        PlaybackSnapshot snapshot;
        lock (_gate)
        {
            CleanupStopped();
            foreach (var voice in _voices)
            {
                if (voice.IsDisposed || voice.StopRequested || voice.NaturalEndRequested) continue;
                if (voice.Jingle.EndSeconds is not double endSeconds || voice.Reader.CurrentTime.TotalSeconds < endSeconds) continue;
                if (voice.LoopEnabled)
                    voice.SeekProvider.Seek(TimeSpan.FromSeconds(voice.PlaybackStartSeconds));
                else
                {
                    voice.NaturalEndRequested = true;
                    (reachedEnd ??= []).Add(voice);
                }
            }
            var primaryOutputVoice = _voices.LastOrDefault(candidate => !candidate.UsesSecondaryDevice && !candidate.IsDisposed);
            snapshot = CreateSnapshot(primaryOutputVoice, EmptyPrimarySnapshot);
        }
        if (reachedEnd is not null)
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

    private static WasapiOut CreateOutput(MMDevice device)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return new WasapiOut(device, AudioClientShareMode.Shared, true, 50); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    private void OnStopped(Voice voice, Exception? error, bool publishSnapshot = true)
    {
        var completed = false;
        lock (_gate)
        {
            if (!voice.Output.TryConsumeStopped(out error)) return;
            voice.Performance.Mark("PlaybackStopped", detail: error is not null ? "Failed" : voice.StopRequested ? "Manual" : "Natural");
            if (error is null && !voice.StopRequested && voice.LoopEnabled && !voice.IsDisposed)
            {
                voice.SeekProvider.Seek(TimeSpan.FromSeconds(voice.PlaybackStartSeconds));
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
        if (publishSnapshot) PublishSnapshot();
        if (error is not null) PlaybackFailed?.Invoke(this, $"{voice.Jingle.Title}: {error.Message}");
        if (completed) PlaybackCompleted?.Invoke(this, voice.Jingle);
    }

    private void CleanupStopped()
    {
        List<Voice>? stopped = null;
        foreach (var voice in _voices)
            if (voice.Output.PlaybackState == PlaybackState.Stopped && voice.Output.StopConfirmed)
                (stopped ??= []).Add(voice);
        if (stopped is not null)
            foreach (var voice in stopped)
            {
                // The timer/new command can beat delivery of the callback. Process
                // its terminal event once, including natural completion/loop rules.
                if (voice.Output.StopConfirmed) OnStopped(voice, null, publishSnapshot: false);
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
            voice.FadeVolume.SetTarget(1, Math.Clamp(seconds, 0, 30));
            await WaitForFadeAsync(seconds, cancellationToken, drainOutputBuffer: false);
        }
        finally { voice.EndFade(cancellationToken); }
    }

    private async Task FadeOutVoiceAsync(Voice voice, double seconds, bool allowSpaceResume = false)
    {
        if (voice.IsDisposed) return;
        var cancellationToken = voice.BeginFade();
        try
        {
            voice.PauseRequested = false;
            voice.CanResumeSpaceFade = allowSpaceResume;
            // Markera avsiktlig toning direkt. Annars kan filen nå sitt naturliga slut
            // under fade-jobbet och felaktigt utlösa PlaybackCompleted en extra gång.
            voice.StopRequested = true;
            voice.FadeVolume.SetTarget(0, voice.Paused ? 0 : FadeSecondsRemaining(voice, seconds));
            if (!await WaitForRenderedFadeAsync(voice, cancellationToken)) return;

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

    private static async Task<bool> WaitForRenderedFadeAsync(Voice voice, CancellationToken cancellationToken)
    {
        try
        {
            // The audio callback, not wall-clock time, decides when the ramp is
            // complete. Then allow the 50 ms WASAPI buffer to drain.
            await voice.FadeVolume.TransitionCompleted.WaitAsync(cancellationToken);
            await Task.Delay(60, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    private static double FadeSecondsRemaining(Voice voice, double requestedSeconds)
    {
        // Finish at silence before the source/clip runs out, even if the requested
        // fade is longer than the unread tail. The reader position includes audio
        // already handed to WASAPI; its queued buffers still drain below.
        var end = Math.Min(voice.Reader.TotalTime.TotalSeconds, voice.Jingle.EndSeconds ?? double.MaxValue);
        return Math.Min(Math.Clamp(requestedSeconds, 0, 30), Math.Max(0, end - voice.Reader.CurrentTime.TotalSeconds));
    }

    private static async Task<bool> WaitForFadeAsync(double seconds, CancellationToken cancellationToken, bool drainOutputBuffer)
    {
        try
        {
            // WASAPI använder en 50 ms buffert. Vid fade-out låter vi därför en
            // helt tyst buffert passera innan Output.Stop, så stoppet aldrig sker
            // medan den sista hörbara delen fortfarande väntar i enheten.
            var milliseconds = Math.Clamp(seconds, 0, 30) * 1000 + (drainOutputBuffer ? 60 : 0);
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    private void EnsureVolumeTargets()
    {
        var changed = _volumeTargetsDirty || _volumeVoiceCount != _voices.Count;
        foreach (var voice in _voices)
        {
            var jingle = voice.Jingle;
            var inputs = new VolumeInputs(jingle.Id, !voice.IsDisposed && !voice.StopRequested && !voice.NaturalEndRequested,
                jingle.AllowMultipleClicks, jingle.PlayMode, jingle.GainDb, jingle.NormalizationEnabled, jingle.NormalizationGainDb, voice.PolyphonyHeadroomDb);
            if (voice.HasVolumeInputs && voice.VolumeInputs == inputs) continue;
            voice.VolumeInputs = inputs;
            voice.HasVolumeInputs = true;
            changed = true;
        }
        if (!changed) return;
        var performance = PerformanceDiagnostics.BeginOperation("VolumeStateChanged");
        using var duration = performance.Measure("VolumeStateRebuild");
        _primaryVolumeGroup.Clear();
        _secondaryVolumeGroup.Clear();
        foreach (var voice in _voices)
        {
            var inputs = voice.VolumeInputs;
            if (!inputs.Active) continue;
            var group = voice.UsesSecondaryDevice ? _secondaryVolumeGroup : _primaryVolumeGroup;
            if (inputs.Multiple) group.MultipleIds.Add(inputs.Id); else group.SingleLayers++;
            group.ContainsDuck |= inputs.Mode == JinglePlayMode.Duck;
            group.ContainsMix |= inputs.Mode == JinglePlayMode.Mix;
        }
        SetGroupHeadroom(_primaryVolumeGroup);
        SetGroupHeadroom(_secondaryVolumeGroup);
        foreach (var voice in _voices)
        {
            var inputs = voice.VolumeInputs;
            var group = voice.UsesSecondaryDevice ? _secondaryVolumeGroup : _primaryVolumeGroup;
            var gainDb = inputs.GainDb + (inputs.Normalized ? inputs.NormalizationGainDb : 0) +
                _masterDb + inputs.PolyphonyHeadroomDb + voice.PlaybackGainOffsetDb + (voice.UsesSecondaryDevice ? _secondaryMonitorDb : 0);
            if (group.ContainsDuck && inputs.Mode != JinglePlayMode.Duck) gainDb += _duckDb;
            // Preserve the original arithmetic order and Mix exception.
            gainDb -= group.HeadroomDb;
            var target = Math.Clamp(DbToLinear(gainDb), 0, 4);
            if (!voice.TargetVolume.Equals(target)) voice.VolumeApplied = false;
            voice.TargetVolume = target;
        }
        _volumeStateRebuilds++;
        _volumeTargetCalculations += _voices.Count;
        performance.Mark("VolumeTargetCount", _voices.Count);
        _volumeVoiceCount = _voices.Count;
        _volumeTargetsDirty = false;
    }

    private void SetGroupHeadroom(VolumeGroup group)
    {
        var count = group.SingleLayers + group.MultipleIds.Count;
        if (_autoMixHeadroomEnabled && count > 1 && !group.ContainsMix)
            group.HeadroomDb = 3.0103 * Math.Log(count, 2);
    }

    private void ApplyVolume(Voice voice)
    {
        EnsureVolumeTargets();
        ApplyPreparedVolume(voice);
    }
    private static void ApplyPreparedVolume(Voice voice)
    {
        if (voice.VolumeApplied) return;
        voice.Volume.Volume = voice.TargetVolume;
        voice.VolumeApplied = true;
    }
    private void RefreshActiveVolumes()
    {
        EnsureVolumeTargets();
        foreach (var active in _voices)
            if (!active.IsDisposed && !active.StopRequested && !active.NaturalEndRequested && !active.IsVolumeTransitioning)
                ApplyPreparedVolume(active);
    }
    public void RefreshVolumes()
    {
        lock (_gate) RefreshActiveVolumes();
    }
    private static PlaybackSnapshot CreateSnapshot(Voice? voice, PlaybackSnapshot empty)
    {
        if (voice is null)
            return empty;
        var start = TimeSpan.FromSeconds(voice.PlaybackStartSeconds);
        var end = voice.Jingle.EndSeconds is double seconds ? TimeSpan.FromSeconds(seconds) : voice.Reader.TotalTime;
        var duration = end > start ? end - start : TimeSpan.Zero;
        var currentTime = voice.Reader.CurrentTime;
        var position = currentTime > start ? currentTime - start : TimeSpan.Zero;
        return new(voice.Jingle.Id, voice.Jingle.Title, voice.Jingle.FilePath, position, duration,
            LinearToDb(voice.PeakLeft), LinearToDb(voice.PeakRight), voice.Output.PlaybackState == PlaybackState.Playing, voice.Paused,
            voice.StopRequested || voice.PauseRequested);
    }
    private static float DbToLinear(double db) => (float)Math.Pow(10, db / 20);
    private static float LinearToDb(float value) => value <= 0.0001f ? -60 : Math.Max(-60, 20f * MathF.Log10(value));
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_notificationsRegistered)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_outputNotifications); } catch { }
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        StopAll(false);
        _keepAlive.Dispose();
        _outputPool?.Dispose();
        _enumerator.Dispose();
    }
}
