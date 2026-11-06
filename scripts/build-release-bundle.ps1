<#
.SYNOPSIS
    Builds the Lambda Boss release zip: packed XLL + README + unblock.cmd.

.DESCRIPTION
    Stages `lambda-boss64.xll` plus the README and `unblock.cmd` from
    `release/` into a temporary folder, then zips it as
    `release/output/LambdaBoss-<version>.zip`.

    The script assumes the build has already happened. The Release workflow
    (.github/workflows/release.yml) calls it after building. Releases are unsigned.

.PARAMETER Version
    The version string, e.g. "0.2.0". Used in the zip filename.

.PARAMETER XllPath
    Full path to the packed XLL. Defaults to
    `addin/lambda-boss/bin/Release/net48/publish/lambda-boss64-packed.xll`.

.EXAMPLE
    .\scripts\build-release-bundle.ps1 -Version 0.2.0
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$XllPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$ReleaseDir = "$RepoRoot\release"
$OutputDir = "$ReleaseDir\output"
$StagingDir = "$OutputDir\staging-$Version"

if (-not $XllPath) {
    $XllPath = "$RepoRoot\addin\lambda-boss\bin\Release\net48\publish\lambda-boss64-packed.xll"
}

if (-not (Test-Path $XllPath)) {
    Write-Error "Packed XLL not found at: $XllPath"
}

$readmePath = "$ReleaseDir\README.txt"
$unblockPath = "$ReleaseDir\unblock.cmd"

if (-not (Test-Path $readmePath)) {
    Write-Error "README.txt not found at: $readmePath"
}
if (-not (Test-Path $unblockPath)) {
    Write-Error "unblock.cmd not found at: $unblockPath"
}

# Prepare a clean staging folder
if (Test-Path $StagingDir) {
    Remove-Item -Recurse -Force $StagingDir
}
New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null

# Copy in the three deliverables. Rename the XLL to drop the "-packed" suffix
# -- end users don't need to see ExcelDNA's internal naming.
Copy-Item $XllPath "$StagingDir\lambda-boss64.xll"
Copy-Item $readmePath "$StagingDir\README.txt"
Copy-Item $unblockPath "$StagingDir\unblock.cmd"

$zipPath = "$OutputDir\LambdaBoss-$Version.zip"
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

Compress-Archive -Path "$StagingDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

# Clean up the staging folder -- the zip is the only thing we keep
Remove-Item -Recurse -Force $StagingDir

$zipSize = (Get-Item $zipPath).Length / 1MB
Write-Host ("Built $zipPath ({0:N1} MB)" -f $zipSize) -ForegroundColor Green
