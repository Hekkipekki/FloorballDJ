[CmdletBinding()]
param([string]$TraceTool, [string]$BenchmarkExe, [string]$OutputDirectory, [ValidateRange(1,20)][int]$Trials = 6, [switch]$Interactions)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path -Parent $PSScriptRoot
if(-not $BenchmarkExe){
    $packaged=Join-Path $root 'benchmark\FloorballDJ.PerformanceSmoke.exe'
    $BenchmarkExe=if(Test-Path -LiteralPath $packaged){$packaged}else{Join-Path $root 'tests\FloorballDJ.PerformanceSmoke\bin\Release\net10.0-windows\win-x64\FloorballDJ.PerformanceSmoke.exe'}
}
if(-not $TraceTool){
    $localTool=Join-Path $root 'artifacts\performance-tools\dotnet-trace.exe'
    if(Test-Path -LiteralPath $localTool){$TraceTool=$localTool}else{
        $command=Get-Command dotnet-trace -ErrorAction SilentlyContinue
        if(-not $command){throw 'dotnet-trace is required. Supply -TraceTool; this script does not install tools.'}
        $TraceTool=$command.Source
    }
}
if(-not(Test-Path -LiteralPath $BenchmarkExe -PathType Leaf) -or -not(Test-Path -LiteralPath $TraceTool -PathType Leaf)){throw 'Benchmark or trace tool missing.'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root ('captures\main-view-profile-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new output directory to retain earlier traces.'}
New-Item -ItemType Directory -Path $OutputDirectory|Out-Null
$trace=Join-Path $OutputDirectory 'main.nettrace'
$run=Join-Path $OutputDirectory 'run'
Write-Host 'Profiled synthetic main-view run. Do not use its durations as an unprofiled baseline.'
$benchmarkArguments=@('--main-view-analysis','--main-view-trials',[string]$Trials,'--output',$run)
if($Interactions){$benchmarkArguments+='--main-view-interactions'}
& $TraceTool collect --profile dotnet-sampled-thread-time,gc-verbose --providers 'FloorballDJ-Analysis:0xFFFFFFFFFFFFFFFF:5' --format Speedscope -o $trace --show-child-io -- $BenchmarkExe @benchmarkArguments
if($LASTEXITCODE -ne 0){throw "Trace or child process failed: $LASTEXITCODE"}
[ordered]@{version='p4.11-main-view-profile-v1';profiles='dotnet-sampled-thread-time,gc-verbose';markerProvider='FloorballDJ-Analysis';trials=$Trials;interactions=[bool]$Interactions;profiled=$true;timingBaseline=$false;traceFile='main.nettrace';runDirectory='run'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputDirectory 'profile.json') -Encoding UTF8
Write-Host $OutputDirectory
