#requires -Version 5.1
<#
.SYNOPSIS
Builds the ShareX solution in Release|x64 and deploys the output to a local folder.
.DESCRIPTION
The deployment path is machine local and never stored in the repository. It is resolved in this order:
1. -DeployPath parameter
2. SHAREX_DEPLOY_PATH environment variable
3. Scripts/BuildRelease.local.json (gitignored), e.g. { "deployPath": "D:\Programs\ShareX" } (backslashes escaped)
See Scripts/BuildRelease.local.json.example as a starting point.
Without any of them the script only builds and skips deployment.
.EXAMPLE
.\Scripts\BuildRelease.ps1
.EXAMPLE
.\Scripts\BuildRelease.ps1 -DeployPath "D:\Programs\ShareX"
.EXAMPLE
$env:SHAREX_DEPLOY_PATH = "D:\Programs\ShareX"; .\Scripts\BuildRelease.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$DeployPath,
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
robocopy $buildDirectory $resolvedDeployPath /MIR /NFL /NDL /NJH /R:2 /W:1
# Robocopy exit codes 0-7 mean success, 8+ mean errors.
if ($LASTEXITCODE -ge 8) {
    throw "Deployment failed with robocopy exit code $LASTEXITCODE."
}

Write-Host "Deployed: $(Join-Path $resolvedDeployPath 'ShareX.exe')" -ForegroundColor Green
