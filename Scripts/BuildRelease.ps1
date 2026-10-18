#requires -Version 5.1
<#
.SYNOPSIS
Builds the ShareX solution in Release|x64, provides ffmpeg.exe next to ShareX.exe and deploys the output to a local folder.
.DESCRIPTION
FFmpeg is neither stored in the repository nor referenced as a NuGet package. ShareX always looks for ffmpeg.exe
next to ShareX.exe, so the CI workflow copies it into ShareX\bin\<Configuration>\win-<platform> after the build
(see the "Download FFmpeg" step in .github/workflows/build.yml). This script does the same, otherwise the deployed
build cannot record or convert video. Use -FFmpegVersion to pin another release, -SkipFFmpeg to leave the build
output untouched.
The deployment path is machine local and never stored in the repository. It is resolved in this order:
1. -DeployPath parameter
2. SHAREX_DEPLOY_PATH environment variable
3. Scripts/BuildRelease.local.json (gitignored), e.g. { "deployPath": "D:\Programs\ShareX" } (backslashes escaped)
See Scripts/BuildRelease.local.json.example as a starting point.
Without any of them the script only builds and skips deployment.
Deployment mirrors the build output with robocopy /MIR, so files that the build does not produce are deleted from the
deployment folder. Files ShareX creates at runtime (Portable, PersonalPath.cfg, the local ShareX folder) are excluded
from that cleanup.
.EXAMPLE
.\Scripts\BuildRelease.ps1
.EXAMPLE
.\Scripts\BuildRelease.ps1 -DeployPath "D:\Programs\ShareX"
.EXAMPLE
.\Scripts\BuildRelease.ps1 -FFmpegVersion 8.1
.EXAMPLE
.\Scripts\BuildRelease.ps1 -SkipFFmpeg -SkipDeploy
.EXAMPLE
$env:SHAREX_DEPLOY_PATH = "D:\Programs\ShareX"; .\Scripts\BuildRelease.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$DeployPath,
    [string]$FFmpegVersion = '8.1',
    [switch]$SkipFFmpeg,
    [switch]$SkipDeploy
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'ShareX.sln'

function Resolve-DeployPath([string]$explicitPath)
{
    if (-not [string]::IsNullOrWhiteSpace($explicitPath)) { return $explicitPath }
    if (-not [string]::IsNullOrWhiteSpace($env:SHAREX_DEPLOY_PATH)) { return $env:SHAREX_DEPLOY_PATH }

    $configFile = Join-Path $PSScriptRoot 'BuildRelease.local.json'
    if (Test-Path -LiteralPath $configFile) {
        $config = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
        if ($config.PSObject.Properties['deployPath'] -and -not [string]::IsNullOrWhiteSpace($config.deployPath)) {
            return $config.deployPath
        }
    }

    return $null
}

function Ensure-FFmpeg([string]$buildDirectory, [string]$platform, [string]$ffmpegVersion)
{
    $ffmpegPath = Join-Path $buildDirectory 'ffmpeg.exe'
    if (Test-Path -LiteralPath $ffmpegPath) {
        Write-Host "FFmpeg already present: $ffmpegPath" -ForegroundColor DarkGray
        return
    }

    # Same source as the "Download FFmpeg" step of .github/workflows/build.yml.
    $architecture = $platform.ToLowerInvariant()
    $url = "https://github.com/ShareX/FFmpeg/releases/download/v$ffmpegVersion/ffmpeg-$ffmpegVersion-win-$architecture.zip"
    $tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "ShareX-BuildRelease-ffmpeg-$([System.Guid]::NewGuid().ToString('N'))"

    Write-Host "Downloading FFmpeg $ffmpegVersion ($architecture) from $url..." -ForegroundColor Cyan
    # Invoke-WebRequest is extremely slow in Windows PowerShell when the progress bar is enabled.
    $progressPreference = 'SilentlyContinue'
    New-Item -ItemType Directory -Path $tempDirectory -Force | Out-Null

    try {
        $archivePath = Join-Path $tempDirectory 'ffmpeg.zip'
        Invoke-WebRequest -Uri $url -OutFile $archivePath -UseBasicParsing
        Expand-Archive -Path $archivePath -DestinationPath $tempDirectory -Force

        $downloadedExecutable = Get-ChildItem -Path $tempDirectory -Filter 'ffmpeg.exe' -Recurse -File | Select-Object -First 1
        if ($null -eq $downloadedExecutable) {
            throw "ffmpeg.exe was not found in the downloaded archive: $url"
        }

        Copy-Item -LiteralPath $downloadedExecutable.FullName -Destination $ffmpegPath -Force
    }
    finally {
        Remove-Item -LiteralPath $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path -LiteralPath $ffmpegPath)) {
        throw "FFmpeg was not extracted to: $ffmpegPath"
    }

    Write-Host "FFmpeg ready: $ffmpegPath" -ForegroundColor Green
}

Write-Host "Building $solution ($Configuration|$Platform)..." -ForegroundColor Cyan
dotnet build $solution -c $Configuration -p:Platform=$Platform --nologo -v m
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

$buildDirectory = Join-Path $repositoryRoot "ShareX\bin\$Configuration\win-$Platform"
$executable = Join-Path $buildDirectory 'ShareX.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Build output not found: $executable"
}

Write-Host "Build succeeded: $executable" -ForegroundColor Green

if ($SkipFFmpeg) {
    Write-Host 'FFmpeg step skipped.' -ForegroundColor Yellow
}
else {
    Ensure-FFmpeg $buildDirectory $Platform $FFmpegVersion
}

if ($SkipDeploy) {
    Write-Host 'Deployment skipped.' -ForegroundColor Yellow
    return
}

$resolvedDeployPath = Resolve-DeployPath $DeployPath
if ([string]::IsNullOrWhiteSpace($resolvedDeployPath)) {
    Write-Host 'No deployment path configured (use -DeployPath, SHAREX_DEPLOY_PATH or Scripts/BuildRelease.local.json). Build only.' -ForegroundColor Yellow
    return
}

if (-not (Test-Path -LiteralPath $resolvedDeployPath)) {
    New-Item -ItemType Directory -Path $resolvedDeployPath -Force | Out-Null
}

Write-Host "Mirroring build to $resolvedDeployPath..." -ForegroundColor Cyan
# /MIR also deletes files that the build does not produce, so keep the files ShareX creates at runtime.
$robocopyArgs = @(
    $buildDirectory,
    $resolvedDeployPath,
    '/MIR', '/NFL', '/NDL', '/NJH', '/R:2', '/W:1',
    '/XF', 'Portable', 'PersonalPath.cfg',
    '/XD', (Join-Path $resolvedDeployPath 'ShareX')
)
robocopy @robocopyArgs
# Robocopy exit codes 0-7 mean success, 8+ mean errors.
if ($LASTEXITCODE -ge 8) {
    throw "Deployment failed with robocopy exit code $LASTEXITCODE."
}

Write-Host "Deployed: $(Join-Path $resolvedDeployPath 'ShareX.exe')" -ForegroundColor Green
