# Reporting assertions run under Windows PowerShell 5, the laptop kit's runtime.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('FloorballDJ-report-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$reportScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\Summarize-PerformanceCapture.ps1'
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
@{ schemaVersion = 1 } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'session.json') -Encoding UTF8
@{ schemaVersion = 1; mode = 'benchmark'; diagnostics = $true; expectedIterations = 1001; exitCode = 0 } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'run.json') -Encoding UTF8
$lines = [System.Collections.Generic.List[string]]::new()
function Add-Event([long]$CommandId, [long]$OperationId, [string]$Stage, [double]$Time, $Detail = $null, $Duration = $null, $Value = $null) {
    $lines.Add((@{ type = 'event'; commandId = $CommandId; operationId = $OperationId; stage = $Stage; elapsedMs = $Time
        threadId = 1; detail = $Detail; durationMs = $Duration; value = $Value } | ConvertTo-Json -Compress))
}
for ($index = 0; $index -le 1000; $index++) {
    $command = $index + 1; $operation = $index + 10001; $time = $index * 2000
    Add-Event $command 0 'CommandReceived' $time 'keyboard'
    Add-Event $command 0 'RouteResolved' ($time + .1) 'jingle'
    Add-Event $command $operation 'PlaybackRequested' ($time + .2)
    Add-Event $command $operation 'JingleIdentity' ($time + .3) 'same-jingle'
    Add-Event $command $operation 'SourceExtension' ($time + .3) '.wav'
    Add-Event $command $operation 'SourceFormat' ($time + .3) '48000Hz/2ch'
    Add-Event $command $operation 'OutputRoute' ($time + .3) 'primary'
    Add-Event $command $operation 'PlaybackAction' ($time + .4) 'Started'
    $latency = if ($index -eq 0) { 20 } else { $index }
    Add-Event $command $operation 'FirstBufferReturned' ($time + $latency)
    Add-Event $command 0 'CommandHandlerCompleted' ($time + .5)
}
Add-Event 0 1 'EngineLockWait' 0 $null 0
Add-Event 0 2 'PlaybackRequested' 9999999
Add-Event 0 2 'PlaybackAction' 9999999 'FadingOut'
foreach ($state in @(@{ active = 1; pending = 3; bytes = 16 }, @{ active = 0; pending = 0; bytes = 32 })) {
    foreach ($metric in @(@{ name = 'WaveformActiveDecoders'; value = $state.active },
            @{ name = 'WaveformPendingJobs'; value = $state.pending }, @{ name = 'WaveformCachedBytes'; value = $state.bytes })) {
        $lines.Add((@{ type = 'event'; commandId = 42; operationId = 77; stage = $metric.name; elapsedMs = 9999999
            threadId = 1; detail = $null; durationMs = $null; value = $metric.value } | ConvertTo-Json -Compress))
    }
}
$footer = @{ type = 'summary'; writtenEvents = $lines.Count; droppedEvents = 0; discardedEvents = 0; stopReason = 'CaptureClosed'; elapsedMs = 10000000 }
Add-Event 0 30001 'SaveRequested' 9999999
Add-Event 0 30001 'SaveSnapshotReady' 9999999
Add-Event 0 30001 'SaveSerialization' 9999999 $null 2
Add-Event 0 30001 'SaveCompleted' 9999999
Add-Event 0 30002 'SaveRequested' 9999999
Add-Event 0 30002 'SaveSnapshotReady' 9999999
Add-Event 0 30002 'SaveSuperseded' 9999999
Add-Event 0 30003 'SaveRequested' 9999999
Add-Event 0 30003 'SaveFailed' 9999999
Add-Event 0 40001 'MetadataRequested' 9999999
Add-Event 0 40001 'MetadataDurationRead' 9999999 $null 2
for ($taskHit = 0; $taskHit -lt 3; $taskHit++) { Add-Event 0 40001 'MetadataCacheHit' 9999999 }
Add-Event 0 40001 'MetadataMissing' 9999999
Add-Event 0 40001 'MetadataFailed' 9999999
Add-Event 0 40002 'LibraryInspection' 9999999 $null 3
Add-Event 0 50001 'PlaylistRequested' 9999999
Add-Event 0 50001 'PlaylistCacheHit' 9999999
for ($probe = 0; $probe -lt 3; $probe++) { Add-Event 0 50001 'PlaylistMediaProbe' 9999999 }
Add-Event 0 50001 'PlaylistQueueApply' 9999999 $null 2
Add-Event 0 50002 'AutoplayLibraryScan' 9999999 $null 3
Add-Event 0 50003 'QueuePrefetchRead' 9999999 $null 1
Add-Event 0 60001 'VolumeStateRebuild' 9999999 $null 1
Add-Event 0 60001 'VolumeTargetCount' 9999999 $null $null 3
Add-Event 0 60002 'VolumeStateRebuild' 9999999 $null 2
Add-Event 0 60002 'VolumeTargetCount' 9999999 $null $null 5
Add-Event 0 70001 'RandomSettingsOpenRequested' 9999999
Add-Event 0 70001 'RandomSettingsReady' 9999999 $null 4
Add-Event 0 70002 'RandomSettingsFileChecks' 9999999 $null $null 553
Add-Event 0 70003 'RandomSettingsFileChecks' 9999999 $null $null 17
Add-Event 0 70004 'RandomSettingsJingleEditors' 9999999 $null $null 550
Add-Event 0 70005 'RandomSettingsJingleEditors' 9999999 $null $null 550
Add-Event 0 70004 'RandomSettingsGroupRealization' 9999999 $null 2
Add-Event 0 70006 'RandomSettingsOverview' 9999999 $null 1
Add-Event 0 70007 'RandomSettingsStale' 9999999
Add-Event 0 70008 'RandomSettingsCancelled' 9999999
Add-Event 0 70009 'RandomSettingsFailed' 9999999
Add-Event 80001 80001 'RandomPoolPreparationRequested' 9999999
Add-Event 80001 80001 'RandomPoolFileProbes' 9999999 $null $null 3
Add-Event 80001 80001 'RandomPoolSelectedValidCount' 9999999 $null $null 1
Add-Event 80002 80002 'RandomPoolPreparationRequested' 9999999
Add-Event 80002 80002 'RandomPoolFileProbes' 9999999 $null $null 7
Add-Event 80002 80002 'RandomPoolSelectedValidCount' 9999999 $null $null 0
$footer.writtenEvents = $lines.Count
$lines.Add(($footer | ConvertTo-Json -Compress))
$eventPath = Join-Path $root 'events.jsonl'
[System.IO.File]::WriteAllLines($eventPath, $lines)
& $reportScript -CaptureDirectory $root
$report = Get-Content -LiteralPath (Join-Path $root 'report.json') -Raw | ConvertFrom-Json
$repeat = @($report.scenarioChains | Where-Object { $_.metric -like '*/repeatObserved/FirstBufferReturned' })
Assert ($repeat.Count -eq 1) 'Expected one repeated-use scenario.'
$statistics = $repeat[0].statistics
Assert ($statistics.count -eq 1000 -and $statistics.p50Ms -eq 500 -and $statistics.p95Ms -eq 950 -and $statistics.p99Ms -eq 990 -and $statistics.maximumMs -eq 1000) 'Nearest-rank warm percentiles must match independent fixture values.'
Assert ($report.playback.started -eq 1001 -and $report.playback.firstBuffers -eq 1001 -and $report.playback.fadingOut -eq 1 -and $report.playback.startedWithoutBuffer -eq 0) 'Fade actions must not count as missed starts.'
$zero = @($report.stages | Where-Object { $_.metric -eq 'EngineLockWait' })
Assert ($zero.Count -eq 1 -and $zero[0].statistics.p50Ms -eq 0) 'Zero-duration stages must survive reporting.'
Assert ($report.runtime.WaveformActiveDecoders.maximum -eq 1 -and $report.runtime.WaveformPendingJobs.maximum -eq 3 -and
    $report.runtime.WaveformCachedBytes.last -eq 32) 'Operation-scoped waveform state changes must appear in the resource table.'
Assert ($report.saves.requested -eq 3 -and $report.saves.captured -eq 2 -and $report.saves.serialized -eq 1 -and
    $report.saves.completed -eq 1 -and $report.saves.superseded -eq 1 -and $report.saves.failed -eq 1) 'Superseded autosaves must remain distinct from completed and failed requests.'
Assert ($report.metadataWork.requests -eq 1 -and $report.metadataWork.durationReadAttempts -eq 1 -and
    $report.metadataWork.cacheHits -eq 3 -and $report.metadataWork.missing -eq 1 -and $report.metadataWork.failed -eq 1 -and
    $report.metadataWork.libraryInspections -eq 1) 'Metadata work counts must retain every cache hit within one batch, not only one marker per operation.'
Assert ($report.autoplayWork.playlistRequests -eq 1 -and $report.autoplayWork.playlistReads -eq 0 -and
    $report.autoplayWork.playlistCacheHits -eq 1 -and $report.autoplayWork.mediaProbes -eq 3 -and
    $report.autoplayWork.queueApplies -eq 1 -and $report.autoplayWork.fullLibraryScans -eq 1 -and $report.autoplayWork.prefixReads -eq 1) 'Autoplay work must stay distinct from playback and count all media probes.'
Assert ($report.uiWork.volumeStateRebuilds -eq 2 -and $report.uiWork.calculatedVolumeTargets -eq 8) 'Volume target work must sum voice counts across changed states, not count polling ticks.'
Assert ($report.randomPoolWork.preparations -eq 2 -and $report.randomPoolWork.fileProbes -eq 10 -and $report.randomPoolWork.validatedSelections -eq 1) 'Random validation sums probes and reports found memberships without inventing a full available count.'
Assert ($report.randomSettingsWork.opens -eq 1 -and $report.randomSettingsWork.ready -eq 1 -and
    $report.randomSettingsWork.fileChecks -eq 570 -and $report.randomSettingsWork.realizedJingleEditors -eq 1100 -and
    $report.randomSettingsWork.groupRealizations -eq 1 -and $report.randomSettingsWork.overviews -eq 1 -and
    $report.randomSettingsWork.stale -eq 1 -and $report.randomSettingsWork.cancelled -eq 1 -and $report.randomSettingsWork.failed -eq 1) 'Dialog work must sum batch counts and remain separate from playback failures.'
Assert (@($report.warnings | Where-Object { $_ -match '1,000|differ|lost|failed' }).Count -eq 0) 'Complete warm fixture must not generate false loss/coverage warnings.'
# A capture cap, event loss, and trailing partial JSON must remain prominently visible.
$footer.droppedEvents = 3; $footer.discardedEvents = 2; $footer.stopReason = 'EventByteLimit'
$lines[$lines.Count - 1] = $footer | ConvertTo-Json -Compress
$lines.Add('{"type":"event",')
[System.IO.File]::WriteAllLines($eventPath, $lines)
& $reportScript -CaptureDirectory $root
$report = Get-Content -LiteralPath (Join-Path $root 'report.json') -Raw | ConvertFrom-Json
Assert (@($report.warnings | Where-Object { $_ -match 'lost' }).Count -eq 1) 'Dropped events must produce a warning.'
Assert (@($report.warnings | Where-Object { $_ -match 'EventByteLimit' }).Count -eq 1) 'Capture cap must produce a warning.'
Assert (@($report.warnings | Where-Object { $_ -match 'truncated' }).Count -eq 1) 'Partial JSON must produce a warning.'
$control = Join-Path $root 'control'
New-Item -ItemType Directory -Path $control | Out-Null
@{ schemaVersion = 1; diagnostics = $false; fixture = 'control'; route = 'keyboard'; limitation = 'synthetic'; synchronousDispatchMs = @(1,2,3,4) } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $control 'benchmark.json') -Encoding UTF8
& $reportScript -CaptureDirectory $control
$report = Get-Content -LiteralPath (Join-Path $control 'report.json') -Raw | ConvertFrom-Json
Assert ($report.eventCount -eq 0 -and $report.synchronousBenchmarkDispatch.p50Ms -eq 2 -and $report.synchronousBenchmarkDispatch.p95Ms -eq 4) 'Diagnostics-off controls must work without an event file.'
$mixed = Join-Path $root 'mixed-outputs'
New-Item -ItemType Directory -Path $mixed | Out-Null
@{ schemaVersion = 1 } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $mixed 'session.json') -Encoding UTF8
$lines.Clear()
for ($index = 0; $index -lt 3; $index++) {
    $command = $index + 1; $operation = $index + 100; $time = $index * 100
    Add-Event $command 0 'CommandReceived' $time 'keyboard'
    Add-Event $command 0 'RouteResolved' $time 'jingle'
    Add-Event $command $operation 'PlaybackRequested' $time
    Add-Event $command $operation 'JingleIdentity' $time 'same-jingle'
    Add-Event $command $operation 'OutputLifecycle' $time 'reusePrototype'
    Add-Event $command $operation $(if ($index -eq 2) { 'OutputReused' } else { 'OutputCreated' }) $time
    Add-Event $command $operation 'PlaybackAction' $time 'Started'
    Add-Event $command $operation 'FirstBufferReturned' ($time + 10)
}
$lines.Add((@{ type = 'summary'; writtenEvents = $lines.Count; droppedEvents = 0; discardedEvents = 0; stopReason = 'CaptureClosed' } | ConvertTo-Json -Compress))
[System.IO.File]::WriteAllLines((Join-Path $mixed 'events.jsonl'), $lines)
& $reportScript -CaptureDirectory $mixed
$report = Get-Content -LiteralPath (Join-Path $mixed 'report.json') -Raw | ConvertFrom-Json
$newRepeat = @($report.scenarioChains | Where-Object { $_.metric -like '*/reusePrototype/newOutput/repeatObserved/FirstBufferReturned' })
$reusedRepeat = @($report.scenarioChains | Where-Object { $_.metric -like '*/reusePrototype/reusedOutput/repeatObserved/FirstBufferReturned' })
Assert ($newRepeat.Count -eq 1 -and $reusedRepeat.Count -eq 1 -and $newRepeat[0].statistics.count -eq 1 -and
    $reusedRepeat[0].statistics.count -eq 1) 'Repeated jingle use must distinguish initialization misses from reused outputs.'
Assert ($null -eq $report.randomPoolWork.fileProbes -and $null -eq $report.randomPoolWork.validatedSelections) 'Older captures have unknown random-probe counts, not a false zero.'
Write-Host 'PASS: report percentiles, first/repeat/output-reuse separation, save completion/superseding/failure counts, fade classification, zero durations, resource states, loss/cap/truncation warnings and diagnostics-off controls.'
