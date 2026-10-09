[CmdletBinding()]
param([Parameter(Mandatory)][string]$CaptureDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$capture = (Resolve-Path -LiteralPath $CaptureDirectory).Path
$warnings = [System.Collections.Generic.List[string]]::new()
$durations = @{}
$runtime = @{}
$commands = @{}
$operations = @{}
$footer = $null
$malformed = 0
$eventCount = 0
$stageCounts = @{}
$volumeTargetCount = 0.0
$randomSettingsFileChecks = 0.0
$randomSettingsJingleEditors = 0.0
$randomPoolFileProbes = 0.0
$randomPoolValidatedSelections = 0.0
$metadata = $null
$benchmark = $null
function Read-OptionalJson([string]$Name) {
    $path = Join-Path $capture $Name
    if (Test-Path -LiteralPath $path) { return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) }
    return $null
}
function Add-Number($Map, [string]$Name, [double]$Value) {
    if (-not $Map.ContainsKey($Name)) { $Map[$Name] = [System.Collections.Generic.List[double]]::new() }
    $Map[$Name].Add($Value)
}
function Get-Statistics($Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    # Nearest-rank percentiles; retain the sample count rather than imply tail confidence.
    return [pscustomobject]@{
        count = $sorted.Count
        p50Ms = [math]::Round($sorted[[math]::Max(0, [math]::Ceiling(.50 * $sorted.Count) - 1)], 3)
        p95Ms = [math]::Round($sorted[[math]::Max(0, [math]::Ceiling(.95 * $sorted.Count) - 1)], 3)
        p99Ms = [math]::Round($sorted[[math]::Max(0, [math]::Ceiling(.99 * $sorted.Count) - 1)], 3)
        maximumMs = [math]::Round($sorted[-1], 3)
    }
}
$metadata = Read-OptionalJson 'session.json'
$benchmark = Read-OptionalJson 'benchmark.json'
$run = Read-OptionalJson 'run.json'
$hardware = Read-OptionalJson 'hardware.json'
$build = Read-OptionalJson 'build.json'
if ($null -ne $run) {
    if ($null -eq $run.exitCode) { $warnings.Add('Launcher has no completed process result; this run may still be active or interrupted.') }
    elseif ($run.exitCode -ne 0) { $warnings.Add('Application/runner failed with exit code ' + $run.exitCode + '; this is a partial run.') }
}
if ($null -ne $metadata -and $metadata.schemaVersion -ne 1) { throw 'Unsupported capture schema.' }
$eventPath = Join-Path $capture 'events.jsonl'
if (Test-Path -LiteralPath $eventPath) {
    # Read one line at a time; do not retain raw audio-read events in memory.
    $reader = [System.IO.File]::OpenText($eventPath)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            try { $item = $line | ConvertFrom-Json } catch { $malformed++; continue }
            if ($item.type -eq 'summary') { $footer = $item; continue }
            if ($item.type -ne 'event') { continue }
            $eventCount++
            $stage = [string]$item.stage
            if (-not $stageCounts.ContainsKey($stage)) { $stageCounts[$stage] = 0 }
            $stageCounts[$stage]++
            if ($stage -eq 'VolumeTargetCount' -and $null -ne $item.value) { $volumeTargetCount += [double]$item.value }
            if ($stage -eq 'RandomSettingsFileChecks' -and $null -ne $item.value) { $randomSettingsFileChecks += [double]$item.value }
            if ($stage -eq 'RandomSettingsJingleEditors' -and $null -ne $item.value) { $randomSettingsJingleEditors += [double]$item.value }
            if ($stage -eq 'RandomPoolFileProbes' -and $null -ne $item.value) { $randomPoolFileProbes += [double]$item.value }
            if ($stage -eq 'RandomPoolSelectedValidCount' -and $null -ne $item.value) { $randomPoolValidatedSelections += [double]$item.value }
            if ($null -ne $item.durationMs) { Add-Number $durations $stage $item.durationMs }
            if ($item.commandId -gt 0 -and -not $commands.ContainsKey([string]$item.commandId)) {
                $commands[[string]$item.commandId] = @{ received = $null; completed = $null; source = 'unknown'; route = 'unresolved' }
            }
            if ($item.commandId -gt 0) {
                $command = $commands[[string]$item.commandId]
                switch ($stage) {
                    'CommandReceived' { $command.received = [double]$item.elapsedMs; $command.source = [string]$item.detail }
                    'CommandHandlerCompleted' { $command.completed = [double]$item.elapsedMs }
                    'RouteResolved' { $command.route = [string]$item.detail }
                }
            }
            if ($item.operationId -gt 0) {
                $key = [string]$item.operationId
                if (-not $operations.ContainsKey($key)) {
                    $operations[$key] = @{ commandId = [string]$item.commandId; times = @{}; details = @{}; values = @{} }
                }
                $operation = $operations[$key]
                if (-not $operation.times.ContainsKey($stage) -or $operation.times[$stage] -gt $item.elapsedMs) {
                    $operation.times[$stage] = [double]$item.elapsedMs
                }
                if ($null -ne $item.detail) { $operation.details[$stage] = [string]$item.detail }
                if ($null -ne $item.value -and -not $operation.values.ContainsKey($stage)) { $operation.values[$stage] = [double]$item.value }
            }
            if ((($item.operationId -eq 0 -and $item.commandId -eq 0) -or
                    $stage -in @('WaveformActiveDecoders', 'WaveformPendingJobs', 'WaveformWaitingAdmissions', 'WaveformCachedBytes')) -and
                    $null -ne $item.value) {
                if (-not $runtime.ContainsKey($stage)) {
                    $runtime[$stage] = @{ samples = 0; first = [double]$item.value; last = [double]$item.value; maximum = [double]$item.value }
                }
                $sample = $runtime[$stage]
                $sample.samples++; $sample.last = [double]$item.value
                $sample.maximum = [math]::Max($sample.maximum, [double]$item.value)
            }
        }
    } finally { $reader.Dispose() }
    if ($null -eq $footer) { $warnings.Add('No closing summary: capture may be incomplete (close the app normally before reporting).') }
    else {
        if ($footer.droppedEvents -gt 0 -or $footer.discardedEvents -gt 0) { $warnings.Add('Events were lost; timing distributions and command coverage may be biased.') }
        if ($footer.stopReason -ne 'CaptureClosed') { $warnings.Add('Capture stopped early: ' + $footer.stopReason) }
        if ($footer.writtenEvents -ne $eventCount) { $warnings.Add('Event count differs from closing summary.') }
    }
    if ($null -eq $metadata) { $warnings.Add('Session metadata is missing.') }
} elseif ($null -eq $benchmark -or $benchmark.diagnostics) { throw 'No events.jsonl or diagnostics-off benchmark.json found.' }
if ($malformed -gt 0) { $warnings.Add("Unreadable/truncated event lines: $malformed") }
$chains = @{}
$groups = @{}
$seenJingles = @{}
$playback = @{ requested = 0; started = 0; fadingOut = 0; polyphonyLimitReached = 0; failed = 0; firstBuffers = 0; firstSignals = 0; startedWithoutBuffer = 0 }
$saves = @{ requested = 0; captured = 0; serialized = 0; completed = 0; superseded = 0; failed = 0 }
foreach ($save in @($operations.Values | Where-Object { $_.times.ContainsKey('SaveRequested') })) {
    $saves.requested++
    if ($save.times.ContainsKey('SaveSnapshotReady') -or $save.times.ContainsKey('SaveSnapshotBytes')) { $saves.captured++ }
    if ($save.times.ContainsKey('SaveSerialization')) { $saves.serialized++ }
    if ($save.times.ContainsKey('SaveCompleted')) { $saves.completed++ }
    if ($save.times.ContainsKey('SaveSuperseded')) { $saves.superseded++ }
    if ($save.times.ContainsKey('SaveFailed')) { $saves.failed++ }
}
$voices = @($operations.Values | Where-Object { $_.times.ContainsKey('PlaybackRequested') } | Sort-Object { $_.times['PlaybackRequested'] })
foreach ($voice in $voices) {
    $playback.requested++
    $times = $voice.times
    $details = $voice.details
    $action = if ($details.ContainsKey('PlaybackAction')) { $details['PlaybackAction'] } else { 'unknown' }
    switch ($action) {
        'Started' { $playback.started++ }
        'FadingOut' { $playback.fadingOut++ }
        'PolyphonyLimitReached' { $playback.polyphonyLimitReached++ }
    }
    if ($times.ContainsKey('PlaybackFailed') -or ($details.ContainsKey('PlaybackStopped') -and $details['PlaybackStopped'] -eq 'Failed')) { $playback.failed++ }
    if ($times.ContainsKey('FirstBufferReturned')) { $playback.firstBuffers++ }
    if ($times.ContainsKey('FirstSignalReturned')) { $playback.firstSignals++ }
    if ($action -eq 'Started' -and -not $times.ContainsKey('FirstBufferReturned')) { $playback.startedWithoutBuffer++ }
    if ($action -ne 'Started') { continue }
    $jingle = if ($details.ContainsKey('JingleIdentity')) { $details['JingleIdentity'] } else { 'unknown' }
    $use = if ($seenJingles.ContainsKey($jingle)) { 'repeatObserved' } else { 'firstObserved' }
    $seenJingles[$jingle] = $true
    $command = if ($commands.ContainsKey($voice.commandId)) { $commands[$voice.commandId] } else { $null }
    $source = if ($null -ne $command) { $command.source } else { 'background' }
    $route = if ($null -ne $command) { $command.route } else { 'background' }
    $extension = if ($details.ContainsKey('SourceExtension')) { $details['SourceExtension'] } else { 'unknown' }
    $format = if ($details.ContainsKey('SourceFormat')) { $details['SourceFormat'] } else { 'unknown' }
    $output = if ($details.ContainsKey('OutputRoute')) { $details['OutputRoute'] } else { 'unknown' }
    $mode = if ($details.ContainsKey('PlaybackMode')) { $details['PlaybackMode'] } else { 'unknownMode' }
    $start = if ($voice.values.ContainsKey('SeekRequested')) { $voice.values['SeekRequested'].ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '?' }
    $fade = if ($voice.values.ContainsKey('EffectiveFadeInSeconds')) { $voice.values['EffectiveFadeInSeconds'].ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '?' }
    $lifecycle = if ($details.ContainsKey('OutputLifecycle')) { $details['OutputLifecycle'] } else { 'unspecifiedOutput' }
    $reuse = if ($times.ContainsKey('OutputReused')) { 'reusedOutput' } elseif ($times.ContainsKey('OutputCreated')) { 'newOutput' } else { 'perVoiceOutput' }
    $group = "$source/$route/$extension/$format/$output/$mode/start=$start/fade=$fade/$lifecycle/$reuse/$use"
    foreach ($marker in @('FirstDecodedFrames', 'FirstBufferReturned', 'FirstSignalReturned')) {
        if (-not $times.ContainsKey($marker)) { continue }
        Add-Number $chains ("PlaybackRequestedTo" + $marker) ($times[$marker] - $times['PlaybackRequested'])
        if ($null -ne $command -and $null -ne $command.received) {
            $value = $times[$marker] - $command.received
            Add-Number $chains ("HandlerReceivedTo" + $marker) $value
            Add-Number $groups ("$group/$marker") $value
        }
    }
}
foreach ($command in $commands.Values) {
    if ($null -ne $command.received -and $null -ne $command.completed) {
        Add-Number $durations 'ManagedCommandHandler' ($command.completed - $command.received)
    }
}
if ($playback.startedWithoutBuffer -gt 0) { $warnings.Add('Started voices without first-buffer markers: inspect stops, short clips, errors, and capture completeness. This is not proof of missed input.') }
if ($null -ne $run -and $run.mode -eq 'benchmark' -and $run.diagnostics -and $playback.started -ne $run.expectedIterations) {
    $warnings.Add('Successful starts differ from the requested benchmark iteration count.')
}
$repeatBuffers = 0
foreach ($key in $groups.Keys) { if ($key -like '*/repeatObserved/FirstBufferReturned') { $repeatBuffers += $groups[$key].Count } }
if ($eventCount -gt 0 -and $repeatBuffers -lt 1000) { $warnings.Add("Only $repeatBuffers repeated first-buffer observations in total; this does not satisfy the 1,000-warm-trigger gate per important scenario.") }
foreach ($key in $groups.Keys) {
    if ($key -like '*/repeatObserved/FirstBufferReturned' -and $groups[$key].Count -lt 1000 -and $repeatBuffers -ge 1000) {
        $warnings.Add("Scenario '$key' has fewer than 1,000 repeats.")
    }
}
if ($runtime.ContainsKey('AllocatedBytes') -and $runtime['AllocatedBytes'].samples -lt 2) { $warnings.Add('Too few runtime samples for allocation/GC deltas.') }
function Convert-StatisticsMap($Map) {
    return @($Map.Keys | Sort-Object | ForEach-Object { [pscustomobject]@{ metric = $_; statistics = (Get-Statistics $Map[$_]) } })
}
function Get-StageCount([string]$Name) {
    if ($stageCounts.ContainsKey($Name)) { return $stageCounts[$Name] }
    return 0
}
$metadataWork = [ordered]@{
    requests = Get-StageCount 'MetadataRequested'; fileProbes = Get-StageCount 'MetadataFileProbe'
    durationReadAttempts = Get-StageCount 'MetadataDurationRead'; cacheHits = Get-StageCount 'MetadataCacheHit'
    missing = Get-StageCount 'MetadataMissing'; failed = Get-StageCount 'MetadataFailed'
    libraryInspections = Get-StageCount 'LibraryInspection'; relinkSearches = Get-StageCount 'LibraryRelinkSearch'
}
$autoplayWork = [ordered]@{
    playlistRequests = Get-StageCount 'PlaylistRequested'; playlistReads = Get-StageCount 'PlaylistRead'
    playlistParses = Get-StageCount 'PlaylistParse'; playlistCacheHits = Get-StageCount 'PlaylistCacheHit'
    mediaProbes = Get-StageCount 'PlaylistMediaProbe'; targetedDirectories = Get-StageCount 'PlaylistTargetDirectory'
    queueApplies = Get-StageCount 'PlaylistQueueApply'; fullLibraryScans = Get-StageCount 'AutoplayLibraryScan'
    prefixReads = Get-StageCount 'QueuePrefetchRead'
}
$uiWork = [ordered]@{
    volumeStateRebuilds = Get-StageCount 'VolumeStateRebuild'; calculatedVolumeTargets = $volumeTargetCount
}
$randomSettingsWork = [ordered]@{
    opens = Get-StageCount 'RandomSettingsOpenRequested'; ready = Get-StageCount 'RandomSettingsReady'
    fileChecks = $randomSettingsFileChecks; realizedJingleEditors = $randomSettingsJingleEditors
    groupRealizations = Get-StageCount 'RandomSettingsGroupRealization'; overviews = Get-StageCount 'RandomSettingsOverview'
    cancelled = Get-StageCount 'RandomSettingsCancelled'; stale = Get-StageCount 'RandomSettingsStale'; failed = Get-StageCount 'RandomSettingsFailed'
}
$randomPoolWork = [ordered]@{
    preparations = Get-StageCount 'RandomPoolPreparationRequested'
    fileProbes = if ($stageCounts.ContainsKey('RandomPoolFileProbes')) { $randomPoolFileProbes } else { $null }
    validatedSelections = if ($stageCounts.ContainsKey('RandomPoolSelectedValidCount')) { $randomPoolValidatedSelections } else { $null }
    scope = 'Command-owned candidate/follow-up probes; not the total number of available pool members. Null means this capture revision did not record the count.'
}
$report = [ordered]@{
    schemaVersion = 1; percentileMethod = 'nearest rank'; generatedUtc = [DateTime]::UtcNow.ToString('o')
    session = $metadata; benchmark = $benchmark; run = $run; hardware = $hardware; build = $build
    eventCount = $eventCount; closingSummary = $footer
    warnings = @($warnings.ToArray()); playback = $playback; saves = $saves; metadataWork = $metadataWork; autoplayWork = $autoplayWork; uiWork = $uiWork; randomSettingsWork = $randomSettingsWork; commandsObserved = $commands.Count
    randomPoolWork = $randomPoolWork
    onsetChains = @(Convert-StatisticsMap $chains); scenarioChains = @(Convert-StatisticsMap $groups)
    stages = @(Convert-StatisticsMap $durations); runtime = $runtime
    synchronousBenchmarkDispatch = if ($null -ne $benchmark) { Get-Statistics $benchmark.synchronousDispatchMs } else { $null }
    limits = @(
        'Managed handler timestamps omit prior Windows/WPF input-queue wait. Dispatcher delay is a separate periodic proxy.'
        'FirstBufferReturned is the source-provider boundary before output consumption/resampling, not speaker onset or an actual submitted hardware frame.'
        'FirstSignalReturned means a returned post-fader buffer contains a sample above 0.0001; includes leading silence/fades and buffer quantization.'
        'FirstObserved is the first seen play per jingle in this capture; neither it nor repeats proves OS/disk cache state.'
        'Provider read durations exclude consumer work, logging/scanning overhead, hardware buffer negotiation and underrun detection.'
        'Runtime samples include diagnostic overhead. GC collection counts are not GC pause durations. No CPU stacks or physical loopback are collected.'
    )
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $capture 'report.json') -Encoding UTF8
$markdown = [System.Collections.Generic.List[string]]::new()
$markdown.Add('# FloorballDJ performance capture')
$markdown.Add('')
$markdown.Add('Managed timings; these results do not measure physical hotkey-to-speaker latency.')
$markdown.Add('')
foreach ($warning in $warnings) { $markdown.Add('- **Coverage warning:** ' + $warning) }
$markdown.Add('')
$markdown.Add(('Playback requests: {0}; started: {1}; fade-out actions: {2}; polyphony rejections: {3}; failures: {4}; first buffers: {5}; first signal buffers: {6}.' -f $playback.requested, $playback.started, $playback.fadingOut, $playback.polyphonyLimitReached, $playback.failed, $playback.firstBuffers, $playback.firstSignals))
$markdown.Add('')
$markdown.Add(('Save requests: {0}; captured snapshots: {1}; serialization attempts: {2}; completed: {3}; superseded before submission: {4}; failed: {5}.' -f $saves.requested, $saves.captured, $saves.serialized, $saves.completed, $saves.superseded, $saves.failed))
$markdown.Add('')
$markdown.Add(('Metadata requests: {0}; file probes: {1}; duration read attempts: {2}; cache hits: {3}; missing: {4}; failed: {5}; library inspections: {6}; relink searches: {7}.' -f $metadataWork.requests, $metadataWork.fileProbes, $metadataWork.durationReadAttempts, $metadataWork.cacheHits, $metadataWork.missing, $metadataWork.failed, $metadataWork.libraryInspections, $metadataWork.relinkSearches))
$markdown.Add(('Autoplay playlist requests: {0}; reads/parses/cache hits: {1}/{2}/{3}; media probes: {4}; targeted directory listings: {5}; queue applies: {6}; full library scans: {7}; bounded next-file prefix reads: {8}.' -f $autoplayWork.playlistRequests, $autoplayWork.playlistReads, $autoplayWork.playlistParses, $autoplayWork.playlistCacheHits, $autoplayWork.mediaProbes, $autoplayWork.targetedDirectories, $autoplayWork.queueApplies, $autoplayWork.fullLibraryScans, $autoplayWork.prefixReads))
$markdown.Add(('Changed volume-state rebuilds: {0}; calculated voice targets: {1}. Stable polling is not recorded as a rebuild.' -f $uiWork.volumeStateRebuilds, $uiWork.calculatedVolumeTargets))
$randomPoolProbeLabel = if ($null -eq $randomPoolWork.fileProbes) { 'not recorded by this revision' } else { [string]$randomPoolWork.fileProbes }
$markdown.Add(('Random pool preparations: {0}; candidate/follow-up file probes: {1}. This is not a full available-member count.' -f $randomPoolWork.preparations, $randomPoolProbeLabel))
$markdown.Add(('Random-player settings opens/ready: {0}/{1}; file checks: {2}; realized jingle editors: {3}; group realizations: {4}; overview refreshes: {5}; cancelled/stale/failed: {6}/{7}/{8}. Ready is dispatcher-idle preparation, not compositor presentation.' -f $randomSettingsWork.opens, $randomSettingsWork.ready, $randomSettingsWork.fileChecks, $randomSettingsWork.realizedJingleEditors, $randomSettingsWork.groupRealizations, $randomSettingsWork.overviews, $randomSettingsWork.cancelled, $randomSettingsWork.stale, $randomSettingsWork.failed))
if ($null -ne $benchmark) { $markdown.Add(''); $markdown.Add('Benchmark fixture: ' + $benchmark.fixture + '; route: ' + $benchmark.route + '; diagnostics: ' + $benchmark.diagnostics + '. ' + $benchmark.limitation) }
function Add-Table([string]$Title, $Rows) {
    $markdown.Add(''); $markdown.Add('## ' + $Title); $markdown.Add('')
    $markdown.Add('| Metric | Samples | p50 ms | p95 ms | p99 ms | Max ms |')
    $markdown.Add('| --- | ---: | ---: | ---: | ---: | ---: |')
    foreach ($row in $Rows) {
        $s = $row.statistics
        $markdown.Add(('| {0} | {1} | {2} | {3} | {4} | {5} |' -f $row.metric, $s.count, $s.p50Ms, $s.p95Ms, $s.p99Ms, $s.maximumMs))
    }
}
Add-Table 'Playback timing chains' $report.onsetChains
Add-Table 'Scenarios (first observed versus repeated use)' $report.scenarioChains
Add-Table 'Individual stages (overlapping; do not add all rows)' $report.stages
if ($null -ne $report.synchronousBenchmarkDispatch) {
    Add-Table 'Synthetic dispatch including invocation overhead' @([pscustomobject]@{ metric = 'SynchronousBenchmarkDispatch'; statistics = $report.synchronousBenchmarkDispatch })
}
$markdown.Add(''); $markdown.Add('## Runtime samples'); $markdown.Add('')
$markdown.Add('| Metric | Samples | First | Last | Maximum |'); $markdown.Add('| --- | ---: | ---: | ---: | ---: |')
foreach ($key in ($runtime.Keys | Sort-Object)) {
    $s = $runtime[$key]
    $markdown.Add(('| {0} | {1} | {2} | {3} | {4} |' -f $key, $s.samples, $s.first, $s.last, $s.maximum))
}
$markdown.Add(''); $markdown.Add('CPU uses core equivalents (1 = one logical processor fully busy). Memory/allocation values use bytes; collections are cumulative counters. Subtract first from last only within this run.');
$markdown.Add('Waveform scheduler rows record state changes, rather than one-second process samples. Cached bytes count retained peak payload; visible controls and the active decoder hold additional memory.');
$markdown.Add(''); $markdown.Add('## Measurement limits'); $markdown.Add('')
foreach ($limit in $report.limits) { $markdown.Add('- ' + $limit) }
$markdown | Set-Content -LiteralPath (Join-Path $capture 'report.md') -Encoding UTF8
Write-Host (Join-Path $capture 'report.md')
