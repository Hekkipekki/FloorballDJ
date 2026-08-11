[CmdletBinding()]
param(
    [Parameter()][ValidateNotNullOrEmpty()][string]$Version = "0.40.0-beta.14",
    [Parameter()][ValidateSet("Debug", "Release")][string]$Configuration = "Release",
    [Parameter()][ValidateNotNullOrEmpty()][string]$Runtime = "win-x64",
    [Parameter()][switch]$Sign,
    [Parameter()][string]$PfxPath = $env:FLOORBALLDJ_SIGN_PFX,
    [Parameter()][string]$PfxPassword = $env:FLOORBALLDJ_SIGN_PASSWORD,
    [Parameter()][ValidateNotNullOrEmpty()][string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Find-InnoCompiler {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7\ISCC.exe")
    )
    $compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $compiler) { throw "Inno Setup 7 hittades inte. Installera det från https://jrsoftware.org/isdl.php och kör skriptet igen." }
    return $compiler
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kitsRoot) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory | Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if ($candidate) { return $candidate }
    }
    throw "SignTool hittades inte. Installera Windows SDK-komponenten Signing Tools for Desktop Apps."
}

function Invoke-SignFile {
    param([Parameter(Mandatory)][string]$SignToolPath, [Parameter(Mandatory)][string]$FilePath)
    & $SignToolPath sign /fd SHA256 /f $PfxPath /p $PfxPassword /tr $TimestampUrl /td SHA256 /d "FloorballDJ" /du "https://floorballdj.netlify.app" $FilePath
    if ($LASTEXITCODE -ne 0) { throw "Kodsignering misslyckades för $FilePath." }
    & $SignToolPath verify /pa /v $FilePath
    if ($LASTEXITCODE -ne 0) { throw "Signaturverifiering misslyckades för $FilePath." }
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Versionen måste exempelvis vara 0.40.0 eller 0.40.0-beta." }
$numericVersionText = ($Version -split '-', 2)[0]
$numericVersion = [version]$numericVersionText
$versionInfoVersion = "{0}.{1}.{2}.0" -f $numericVersion.Major, $numericVersion.Minor, $numericVersion.Build
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot "src\FloorballDJ\FloorballDJ.csproj"
$installerScript = Join-Path $projectRoot "installer\FloorballDJ.iss"
$installerRoot = Join-Path $projectRoot "artifacts\installer"
$outputRoot = Join-Path $installerRoot $Version
$publishDir = Join-Path $outputRoot "app"
$resolvedInstallerRoot = [System.IO.Path]::GetFullPath($installerRoot).TrimEnd('\') + '\'
$resolvedOutputRoot = [System.IO.Path]::GetFullPath($outputRoot).TrimEnd('\') + '\'
if (-not $resolvedOutputRoot.StartsWith($resolvedInstallerRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Ogiltig utdatakatalog: $outputRoot" }
if (Test-Path -LiteralPath $outputRoot) { Remove-Item -LiteralPath $outputRoot -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Host "Publicerar FloorballDJ $Version..."
& dotnet publish $projectFile --configuration $Configuration --runtime $Runtime --self-contained true --output $publishDir -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version=$Version -p:AssemblyVersion=$versionInfoVersion -p:FileVersion=$versionInfoVersion -p:InformationalVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "Publiceringen misslyckades." }

$signTool = $null
if ($Sign) {
    if (-not $PfxPath -or -not (Test-Path -LiteralPath $PfxPath)) { throw "En giltig PFX-fil krävs vid kodsignering. Ange -PfxPath eller FLOORBALLDJ_SIGN_PFX." }
    if ([string]::IsNullOrWhiteSpace($PfxPassword)) { throw "PFX-lösenord saknas. Ange -PfxPassword eller FLOORBALLDJ_SIGN_PASSWORD." }
    $signTool = Find-SignTool
    Invoke-SignFile -SignToolPath $signTool -FilePath (Join-Path $publishDir "FloorballDJ.exe")
}

$iscc = Find-InnoCompiler
$isccArguments = @(
    "/DAppVersion=$Version",
    "/DVersionInfoVersion=$versionInfoVersion",
    "/DPublishDir=$publishDir",
    "/DInstallerOutputDir=$outputRoot",
    "/DSignEnabled=$([int][bool]$Sign)"
)
if ($Sign) {
    $signCommand = '"{0}" sign /fd SHA256 /f "{1}" /p "{2}" /tr "{3}" /td SHA256 /d "FloorballDJ" /du "https://floorballdj.netlify.app" $f' -f $signTool, $PfxPath, $PfxPassword, $TimestampUrl
    $isccArguments += "/SFloorballDJSign=$signCommand"
}
$isccArguments += $installerScript
Write-Host "Bygger installationsprogram..."
& $iscc @isccArguments
if ($LASTEXITCODE -ne 0) { throw "Installerarbygget misslyckades." }

$setupPath = Join-Path $outputRoot "FloorballDJ-Setup.exe"
if (-not (Test-Path -LiteralPath $setupPath)) { throw "Installationsfilen skapades inte: $setupPath" }
if ($Sign) {
    & $signTool verify /pa /v $setupPath
    if ($LASTEXITCODE -ne 0) { throw "Installationsfilens signatur kunde inte verifieras." }
}
$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = "$setupPath.sha256"
Set-Content -LiteralPath $checksumPath -Value "$hash  FloorballDJ-Setup.exe" -Encoding ascii
Write-Host ""
Write-Host "Klart: $setupPath"
Write-Host "SHA256: $hash"
Write-Output $setupPath
