[CmdletBinding()]
param(
    [string]$Executable,
    [ValidatePattern('^[A-Za-z0-9_-]{1,60}$')][string]$Scenario = 'manual-profile',
    [string]$OutputRoot,
    [ValidateSet('AC', 'battery', 'unknown')][string]$PowerSource = 'unknown',
    [string]$Notes = '',
    [switch]$Benchmark,
    [ValidateRange(1,10000)][int]$Iterations = 1001,
    [ValidateSet('keyboard', 'engine')][string]$Route = 'keyboard',
    [ValidateSet('legacy', 'reuse')][string]$AudioOutputs = 'legacy',
    [ValidateRange(100,10000)][int]$SpacingMs = 250,
    [string]$MediaFile,
    [ValidateRange(0,86400)][double]$StartSeconds = 0,
    [switch]$WithoutDiagnostics
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $root 'captures' }
if (-not $Executable) {
    if ($Benchmark) { $Executable = Join-Path $root 'benchmark\FloorballDJ.PerformanceSmoke.exe' }
    else { $Executable = Join-Path $root 'app\FloorballDJ.exe' }
    if (-not (Test-Path -LiteralPath $Executable)) {
        if ($Benchmark) { $Executable = Join-Path $root 'tests\FloorballDJ.PerformanceSmoke\bin\Release\net10.0-windows\win-x64\FloorballDJ.PerformanceSmoke.exe' }
        else { $Executable = Join-Path $root 'src\FloorballDJ\bin\Release\net10.0-windows\win-x64\FloorballDJ.exe' }
        if ($OutputRoot -eq (Join-Path $root 'captures')) { $OutputRoot = Join-Path $root 'artifacts\performance' }
    }
}
$Executable = (Resolve-Path -LiteralPath $Executable).Path
if (-not $Benchmark -and $WithoutDiagnostics) { throw 'WithoutDiagnostics is supported only by the benchmark runner.' }
if (-not $Benchmark -and (Get-Process -Name FloorballDJ -ErrorAction SilentlyContinue)) { throw 'Close the running FloorballDJ application before starting this capture.' }
$directory = Join-Path ([System.IO.Path]::GetFullPath($OutputRoot)) (([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) + '-' + $Scenario + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $directory | Out-Null
# Collect inventory before launch so querying drivers/devices does not perturb the playback path.
$inventory = [ordered]@{ schemaVersion = 1; scenario = $Scenario; powerSource = $PowerSource; notes = $Notes; capturedUtc = [DateTime]::UtcNow.ToString('o') }
foreach ($query in @(
    @{ name = 'cpu'; class = 'Win32_Processor'; fields = @('Name', 'NumberOfCores', 'NumberOfLogicalProcessors') },
    @{ name = 'computer'; class = 'Win32_ComputerSystem'; fields = @('Manufacturer', 'Model', 'TotalPhysicalMemory') },
    @{ name = 'os'; class = 'Win32_OperatingSystem'; fields = @('Caption', 'Version', 'BuildNumber') },
    @{ name = 'graphics'; class = 'Win32_VideoController'; fields = @('Name', 'DriverVersion', 'CurrentHorizontalResolution', 'CurrentVerticalResolution') },
    @{ name = 'storage'; class = 'Win32_DiskDrive'; fields = @('Model', 'InterfaceType', 'Size') },
    @{ name = 'audio'; class = 'Win32_SoundDevice'; fields = @('Name', 'Manufacturer', 'Status') }
)) {
    try { $inventory[$query.name] = @(Get-CimInstance -ClassName $query.class | Select-Object -Property $query.fields) }
    catch { $inventory[$query.name] = @{ unavailable = $_.Exception.GetType().Name } }
}
try { $inventory['powerPlan'] = @(& powercfg.exe /getactivescheme) } catch { $inventory['powerPlan'] = @('unavailable') }
$inventory | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $directory 'hardware.json') -Encoding UTF8
if (Test-Path -LiteralPath (Join-Path $root 'build.json')) {
    Copy-Item -LiteralPath (Join-Path $root 'build.json') -Destination (Join-Path $directory 'build.json')
}
function Quote-Argument([string]$Value) {
    # ProcessStartInfo on Windows PowerShell 5 has no ArgumentList; preserve spaces and trailing slashes.
    return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
$arguments = [System.Collections.Generic.List[string]]::new()
if ($Benchmark) {
    foreach ($value in @('--benchmark', '--iterations', [string]$Iterations, '--route', $Route, '--output', $directory,
            '--spacing-ms', [string]$SpacingMs, '--start-seconds', $StartSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture))) { $arguments.Add($value) }
    if ($MediaFile) { $arguments.Add('--file'); $arguments.Add((Resolve-Path -LiteralPath $MediaFile).Path) }
    if ($WithoutDiagnostics) { $arguments.Add('--without-diagnostics') }
    Write-Host 'Running isolated benchmark; your normal profile is not loaded. Supplied media plays at -60 dB, not muted.'
} else {
    $arguments.Add('--performance-diagnostics'); $arguments.Add('--performance-dir'); $arguments.Add($directory)
    Write-Host 'Use the app normally with the test profile. Close the app normally when finished to generate the report.'
}
if ($AudioOutputs -eq 'reuse') { $arguments.Add('--reuse-audio-outputs') }
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = $Executable
$start.WorkingDirectory = Split-Path -Parent $Executable
$start.UseShellExecute = $false
$start.Arguments = ($arguments | ForEach-Object { Quote-Argument $_ }) -join ' '
Write-Host ('Capture folder: ' + $directory)
$run = [ordered]@{ schemaVersion = 1; mode = $(if ($Benchmark) { 'benchmark' } else { 'manual' })
    audioOutputs = $AudioOutputs; spacingMs = $(if ($Benchmark) { $SpacingMs } else { $null })
    diagnostics = (-not [bool]$WithoutDiagnostics); expectedIterations = $(if ($Benchmark) { $Iterations } else { $null })
    route = $(if ($Benchmark) { $Route } else { 'interactive' }); startedUtc = [DateTime]::UtcNow.ToString('o')
    endedUtc = $null; exitCode = $null }
$run | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'run.json') -Encoding UTF8
$process = [System.Diagnostics.Process]::Start($start)
try { $process.WaitForExit(); $exitCode = $process.ExitCode } finally { $process.Dispose() }
$run.endedUtc = [DateTime]::UtcNow.ToString('o'); $run.exitCode = $exitCode
$run | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'run.json') -Encoding UTF8
if (Test-Path -LiteralPath (Join-Path $directory 'events.jsonl')) {
    & (Join-Path $PSScriptRoot 'Summarize-PerformanceCapture.ps1') -CaptureDirectory $directory
} elseif (Test-Path -LiteralPath (Join-Path $directory 'benchmark.json')) {
    & (Join-Path $PSScriptRoot 'Summarize-PerformanceCapture.ps1') -CaptureDirectory $directory
} else { throw 'No capture was produced. Check application startup and write access to the capture folder.' }
if ($exitCode -ne 0) { throw "Application/runner exited with code $exitCode. Preserve the partial capture for review." }
