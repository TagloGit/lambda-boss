<#
.SYNOPSIS
    Builds the Lambda Boss release zip: signed packed XLL + README + unblock.cmd.

.DESCRIPTION
    Stages a signed `lambda-boss64.xll` plus the README and `unblock.cmd` from
    `release/` into a temporary folder, then zips it as
    `release/output/LambdaBoss-<version>.zip`.

    The script assumes the build has already happened and the XLL is signed --
    `publish-release.ps1` handles those steps and calls this script after.

.PARAMETER Version
    The version string, e.g. "0.2.0". Used in the zip filename.

.PARAMETER SignedXllPath
    Full path to the signed packed XLL. Defaults to
    `addin/lambda-boss/bin/Release/net48/publish/lambda-boss64-packed.xll`.

.EXAMPLE
    .\scripts\build-release-bundle.ps1 -Version 0.2.0
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$SignedXllPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$ReleaseDir = "$RepoRoot\release"
$OutputDir = "$ReleaseDir\output"
$StagingDir = "$OutputDir\staging-$Version"

if (-not $SignedXllPath) {
    $SignedXllPath = "$RepoRoot\addin\lambda-boss\bin\Release\net48\publish\lambda-boss64-packed.xll"
}

if (-not (Test-Path $SignedXllPath)) {
    Write-Error "Signed XLL not found at: $SignedXllPath"
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
Copy-Item $SignedXllPath "$StagingDir\lambda-boss64.xll"
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
