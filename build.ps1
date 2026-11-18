<#
.SYNOPSIS
  Builds Live Captions for Windows 10 into .\dist (framework-dependent, needs the .NET 8 Desktop Runtime).

.DESCRIPTION
  Everything the build touches (SDK, NuGet packages, caches, temp files) stays inside this folder's
  .tools directory, so nothing is written to the system drive.

.EXAMPLE
  .\build.ps1                 # build into .\dist
  .\build.ps1 -Run            # build and start it
  .\build.ps1 -InstallSdk     # first install a private .NET 8 SDK into .tools\dotnet (no admin needed)
  .\build.ps1 -SelfContained  # bundle the .NET runtime too (bigger, runs without installing .NET)
#>
param(
    [switch]$Run,
    [switch]$InstallSdk,
    [switch]$SelfContained,
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$tools = Join-Path $root ".tools"

# Keep every cache and temp file inside .tools
$env:DOTNET_CLI_HOME = Join-Path $tools "home"
$env:NUGET_PACKAGES = Join-Path $tools "nuget\packages"
$env:NUGET_HTTP_CACHE_PATH = Join-Path $tools "nuget\http-cache"
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $tools "nuget\plugins-cache"
$env:TEMP = $env:TMP = Join-Path $tools "tmp"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:MSBUILDDISABLENODEREUSE = "1"     # no build servers left running afterwards
$env:DOTNET_CLI_USE_MSBUILD_SERVER = "0"
foreach ($dir in $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH, $env:TEMP) {
    New-Item -ItemType Directory -Force $dir | Out-Null
}

$localDotnet = Join-Path $tools "dotnet\dotnet.exe"
if ($InstallSdk -and -not (Test-Path $localDotnet)) {
    $installer = Join-Path $env:TEMP "dotnet-install.ps1"
    Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer -UseBasicParsing
    if ((Get-AuthenticodeSignature $installer).Status -ne "Valid") { throw "dotnet-install.ps1 signature check failed" }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Channel 8.0 -InstallDir (Join-Path $tools "dotnet") -NoPath
}
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { "dotnet" }
if ($dotnet -eq $localDotnet) { $env:DOTNET_ROOT = Split-Path $localDotnet }

$project = Join-Path $root "src\LiveCaptions10\LiveCaptions10.csproj"
$dist = Join-Path $root "dist"
$buildArgs = @("publish", $project, "-c", $Configuration, "-o", $dist, "-p:UseSharedCompilation=false", "--nologo")
if ($SelfContained) { $buildArgs += @("--self-contained", "true") }

$running = Get-Process LiveCaptions10 -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dist*" }
if ($running) {
    Write-Host "Closing the running copy so it can be replaced..."
    Start-Process (Join-Path $dist "LiveCaptions10.exe") -ArgumentList "--exit" -Wait
    Start-Sleep -Seconds 2
}

& $dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host ""
Write-Host "Built: $dist\LiveCaptions10.exe  (models and settings live in $dist\data)"
if ($Run) { Start-Process (Join-Path $dist "LiveCaptions10.exe") }
