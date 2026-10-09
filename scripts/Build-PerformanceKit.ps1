[CmdletBinding()]
param([string]$OutputRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $root 'artifacts\performance-kit' }
$name = 'FloorballDJ-RC1-Performance-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$directory = Join-Path ([System.IO.Path]::GetFullPath($OutputRoot)) $name
New-Item -ItemType Directory -Path $directory | Out-Null
foreach ($project in @(
    @{ path = 'src\FloorballDJ\FloorballDJ.csproj'; output = 'app' },
    @{ path = 'tests\FloorballDJ.PerformanceSmoke\FloorballDJ.PerformanceSmoke.csproj'; output = 'benchmark' }
)) {
    & dotnet publish (Join-Path $root $project.path) --configuration Release --runtime win-x64 --self-contained true --output (Join-Path $directory $project.output) -p:PerformanceSymbols=true -p:PublishSingleFile=false -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw ('Publish failed: ' + $project.path) }
}
$scriptDirectory = Join-Path $directory 'scripts'
New-Item -ItemType Directory -Path $scriptDirectory | Out-Null
foreach ($script in @('Start-PerformanceCapture.ps1', 'Summarize-PerformanceCapture.ps1', 'Start-MainViewProfile.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $scriptDirectory
}
Copy-Item -LiteralPath (Join-Path $root 'docs\PERFORMANCE-CAPTURE.md') -Destination (Join-Path $directory 'READ-ME.md')
foreach ($document in @('PERFORMANCE-CAPTURE.md', 'PERFORMANCE-ROADMAP-RC1-TO-1.0.md', 'PERFORMANCE-PHASE0-RESULTS.md', 'PERFORMANCE-PHASE1-HOTKEYS.md', 'PERFORMANCE-PHASE1-WAVEFORMS.md', 'PERFORMANCE-PHASE2-OUTPUT-REUSE.md', 'PERFORMANCE-PHASE1-SAVES.md', 'PERFORMANCE-PHASE1-METADATA.md', 'PERFORMANCE-PHASE1-AUTOPLAY.md', 'PERFORMANCE-PHASE1-UI-WORK.md', 'PERFORMANCE-PHASE4-RANDOM-SETTINGS.md', 'PERFORMANCE-PHASE4-LAYOUT-ANALYSIS.md', 'PERFORMANCE-PHASE4-RANDOM-LAYOUT.md', 'PERFORMANCE-PHASE4-SHOW-ANALYSIS.md', 'PERFORMANCE-PHASE4-SEARCH-REFRESH.md', 'PERFORMANCE-PHASE4-PLACEMENT-ANALYSIS.md', 'PERFORMANCE-PHASE4-FIT-BOUNDS.md', 'PERFORMANCE-PHASE4-MAIN-VIEW.md', 'PERFORMANCE-PHASE4-RENDER-PROFILE.md', 'PERFORMANCE-PHASE4-INTRINSIC-MEASURE.md', 'PERFORMANCE-PHASE4-TEMPLATE-ANALYSIS.md', 'PERFORMANCE-PHASE4-PERSISTENT-DECK.md', 'PERFORMANCE-PHASE4-CARD-REUSE.md', 'PERFORMANCE-PROBOOK-20261006.md', 'PERFORMANCE-PHASE1-RANDOM-VALIDATION.md', 'UI-VOLUME-FIXES-RC1.md', 'RC-WORKFLOW-FIXES.md', 'AUDIO-KEEP-ALIVE.md')) {
    Copy-Item -LiteralPath (Join-Path $root ('docs\' + $document)) -Destination $directory
}
@'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-PerformanceCapture.ps1" -AudioOutputs reuse -Scenario manual-profile-reuse
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Capture.cmd') -Encoding ASCII
@'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-PerformanceCapture.ps1" -Benchmark -AudioOutputs reuse -Scenario silent-keyboard-reuse
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Silent-Benchmark.cmd') -Encoding ASCII
@'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-PerformanceCapture.ps1" -AudioOutputs legacy -Scenario manual-profile-legacy
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Legacy-Capture.cmd') -Encoding ASCII
@'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-PerformanceCapture.ps1" -Benchmark -AudioOutputs legacy -Scenario silent-keyboard-legacy
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Legacy-Silent-Benchmark.cmd') -Encoding ASCII
@'
@echo off
"%~dp0benchmark\FloorballDJ.PerformanceSmoke.exe" --save-checks --output "%~dp0checks\save-%RANDOM%-%RANDOM%"
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Save-Checks.cmd') -Encoding ASCII
@'
@echo off
"%~dp0benchmark\FloorballDJ.PerformanceSmoke.exe" --metadata-checks --output "%~dp0checks\metadata-%RANDOM%-%RANDOM%"
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Metadata-Checks.cmd') -Encoding ASCII
@'
@echo off
"%~dp0benchmark\FloorballDJ.PerformanceSmoke.exe" --autoplay-checks --output "%~dp0checks\autoplay-%RANDOM%-%RANDOM%"
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Autoplay-Checks.cmd') -Encoding ASCII
@'
@echo off
"%~dp0benchmark\FloorballDJ.PerformanceSmoke.exe" --ui-work-checks --output "%~dp0checks\ui-work-%RANDOM%-%RANDOM%"
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'UI-Work-Checks.cmd') -Encoding ASCII
@'
@echo off
"%~dp0benchmark\FloorballDJ.PerformanceSmoke.exe" --random-settings-checks --output "%~dp0checks\random-settings-%RANDOM%-%RANDOM%"
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Random-Settings-Checks.cmd') -Encoding ASCII
@'
@echo off
"%~dp0benchmark\FloorballDJ.PerformanceSmoke.exe" --layout-analysis --output "%~dp0checks\layout-analysis-%RANDOM%-%RANDOM%"
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Layout-Analysis.cmd') -Encoding ASCII
@'
@echo off
"%~dp0benchmark\FloorballDJ.PerformanceSmoke.exe" --show-analysis --output "%~dp0checks\show-analysis-%RANDOM%-%RANDOM%"
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Show-Analysis.cmd') -Encoding ASCII
@'
@echo off
"%~dp0benchmark\FloorballDJ.PerformanceSmoke.exe" --placement-analysis --output "%~dp0checks\placement-analysis-%RANDOM%-%RANDOM%"
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Placement-Analysis.cmd') -Encoding ASCII
@'
@echo off
"%~dp0benchmark\FloorballDJ.PerformanceSmoke.exe" --fit-checks --output "%~dp0checks\fit-%RANDOM%-%RANDOM%"
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Fit-Checks.cmd') -Encoding ASCII
@'
@echo off
"%~dp0benchmark\FloorballDJ.PerformanceSmoke.exe" --main-view-analysis --output "%~dp0checks\main-view-%RANDOM%-%RANDOM%"
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Main-View-Analysis.cmd') -Encoding ASCII
@'
@echo off
"%~dp0benchmark\FloorballDJ.PerformanceSmoke.exe" --main-startup-checks
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Main-Startup-Checks.cmd') -Encoding ASCII
@'
@echo off
"%~dp0benchmark\FloorballDJ.PerformanceSmoke.exe" --main-view-analysis --main-view-interactions --output "%~dp0checks\template-analysis-%RANDOM%-%RANDOM%"
pause
'@ | Set-Content -LiteralPath (Join-Path $directory 'Template-Analysis.cmd') -Encoding ASCII
$commit = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot record source commit.' }
$changed = @(& git -C $root status --porcelain -- src tests scripts docs)
if ($LASTEXITCODE -ne 0) { throw 'Cannot record source status.' }
$hashes = @(Get-ChildItem -LiteralPath (Join-Path $directory 'app') -File | Where-Object { $_.Name -in @('FloorballDJ.exe', 'FloorballDJ.dll', 'FloorballDJ.pdb') } | ForEach-Object {
    [pscustomobject]@{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[ordered]@{ schemaVersion = 1; diagnosticRevision = 'primary-output-keep-alive-v1'; sourceCommit = [string]$commit
    primaryOutputKeepAlive = 'opt-in-shared-digital-silence-v1'; keepAliveDefaultEnabled = $false; keepAliveRetryIntervalMs = 5000
    rcWorkflowFixes = 'dark-pills-autoplay-exit-portable-playlists-local-random-keys-short-tail-fade-team-close-v1'
    presentationFixes = 'dark-random-deck-dropdown-live-primary-limiter-v1'; newProfileAutoMixHeadroomEnabled = $false
    savePipeline = 'owned-snapshot-ordered-v1'
    metadataPipeline = 'bounded-reader-snapshot-apply-v1'; metadataCacheEntryLimit = 256; metadataCacheLifetimeSeconds = 30; metadataApplyBatchLimit = 16
    autoplayPipeline = 'playlist-before-library-v1'; playlistCacheEntryLimit = 32; playlistCacheEstimatedByteLimit = 4194304; playlistCacheLifetimeSeconds = 30; nextFilePrefixByteLimit = 262144
    uiWorkPipeline = 'changed-snapshots-volume-state-v1'; brushCacheEntriesPerKind = 256; fontCacheEntries = 128; viewsPerSource = 4; playbackPollingIntervalMs = 50
    randomSettingsPipeline = 'owned-fresh-library-lazy-groups-v1'; randomSettingsActiveWorkers = 1; randomSettingsFileStatusLifetime = 'one-operation'
    randomSettingsVisibleSelection = 'binding-first-deck-localized-template-v1'; layoutAnalysis = 'p4.3-layout-analysis-v1'; finiteOverviewImplemented = $true; virtualizedSongListImplemented = $true
    randomSettingsCards = 'measured-uniform-wrap-recycling-v1'; randomSettingsOverscanRowsPerSide = 1; randomSettingsRetainedFocusedCards = 1; randomSettingsSorting = 'single-permutation-reset-v1'
    showAnalysis = 'p4.4-show-analysis-v1'; windowPlacementDiagnostics = $true
    randomSettingsSearch = 'changed-membership-refresh-v1'; unchangedSearchPreservesContainers = $true
    placementAnalysis = 'p4.6-placement-analysis-v1'; productionWindowPlacement = 'source-initialized-fit-envelope-v1'; placementAlternativesEnabled = $false
    fitChecks = 'p4.7-fit-checks-v1'; fitGeometry = 'margin-aware-minimum-size-v1'; fitMarginPixelsPerSide = 12
    mainViewAnalysis = 'p4.8-main-view-analysis-v1'; sessionToggleSaves = 'changed-user-state-v1'; startupSessionBindingSaveFixed = $true
    openingTraceMarkers = 'FloorballDJ-Analysis-v1'; renderProfiling = 'p4.11-phase-trace-v1'; interactionAnalysis = 'p4.11-template-phases-v1'
    intrinsicCardMeasurement = 'wpf-stable-sample-constraint-v1'; intrinsicCardChecks = 'p4.10-intrinsic-checks-v1'; randomSettingsDeckView = 'stable-explicit-template-host-v1'; randomSettingsCardReuse = 'owned-viewport-presenter-pool-v1'; randomPoolAvailability = 'candidate-on-demand-per-command-v1'; randomPoolAvailabilityChecks = 'p1.7-random-pool-availability-v1'
    defaultAudioOutputs = 'legacy'; captureLauncherAudioOutputs = 'reuse'; idleOutputLimit = 2; idleOutputLifetimeSeconds = 30
    includesUncommittedChanges = ($changed.Count -gt 0); configuration = 'Release'; runtime = 'win-x64'
    selfContained = $true; builtUtc = [DateTime]::UtcNow.ToString('o'); applicationHashes = $hashes
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $directory 'build.json') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = $directory + '.zip'
[System.IO.Compression.ZipFile]::CreateFromDirectory($directory, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
Write-Host $zip
