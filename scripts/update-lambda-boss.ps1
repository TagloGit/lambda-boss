<#
.SYNOPSIS
    Installs the latest Lambda Boss build into Excel's AddIns folder.

.DESCRIPTION
    Downloads the dev-latest pre-release (default) or, with --stable, the latest
    release, to %APPDATA%\Microsoft\AddIns\lambda-boss64.xll, unblocks it and
    prints the version. Run it with Excel closed, through update-lambda-boss.cmd.

.EXAMPLE
    update-lambda-boss.cmd
    update-lambda-boss.cmd --stable
#>

param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Api = "https://api.github.com/repos/TagloGit/lambda-boss/releases"
$AddInName = "lambda-boss64.xll"
$AddInsDir = Join-Path $env:APPDATA "Microsoft\AddIns"
$Target = Join-Path $AddInsDir $AddInName

function Stop-Update([string]$Message) {
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

function Get-NotesField([string]$Notes, [string]$Name) {
    $match = [regex]::Match([string]$Notes, "(?m)^$Name\:\s*(\S+)")
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}

function Save-Asset($Asset, [string]$Path) {
    Invoke-WebRequest -UseBasicParsing -Uri $Asset.browser_download_url -OutFile $Path

    # GitHub reports each asset's digest as "sha256:<hex>" on recent releases.
    $digestProperty = $Asset.PSObject.Properties["digest"]
    if ($digestProperty -and $digestProperty.Value -match "^sha256:(.+)$") {
        $expected = $Matches[1]
        $actual = (Get-FileHash -Algorithm SHA256 -Path $Path).Hash
        if ($actual -ne $expected) {
            Stop-Update "Download of $($Asset.name) is corrupt: sha256 $actual, expected $expected."
        }
    }
}

function Show-RegistrationHints {
    $registered = @()
    foreach ($office in "16.0", "15.0") {
        $key = "HKCU:\Software\Microsoft\Office\$office\Excel\Options"
        if (Test-Path $key) {
            $values = Get-ItemProperty -Path $key
            foreach ($property in $values.PSObject.Properties) {
                if ($property.Name -match "^OPEN\d*$") { $registered += [string]$property.Value }
            }
        }
    }

    # Excel stores an add-in from its AddIns folder by bare name, anything else by full path.
    $boss = @($registered | Where-Object { $_ -match "lambda-boss" })
    $current = @($boss | Where-Object {
        $_ -like "*$Target*" -or ($_ -like "*$AddInName*" -and $_ -notmatch "\\")
    })
    $other = @($boss | Where-Object { $current -notcontains $_ })

    if ($current.Count -eq 0) {
        Write-Host ""
        Write-Host "Excel isn't set to load this file yet. In Excel: File > Options > Add-ins >"
        Write-Host "Manage: Excel Add-ins > Go, then tick lambda-boss64."
    }
    foreach ($entry in $other) {
        Write-Host ""
        Write-Host "WARNING: Excel also loads another Lambda Boss: $entry" -ForegroundColor Yellow
        Write-Host "Untick it in File > Options > Add-ins, or the old build keeps winning."
    }
}

# --- Arguments ---

$stable = $false
foreach ($argument in @($Arguments | Where-Object { $_ })) {
    if ($argument -in @("--stable", "-stable", "/stable")) { $stable = $true }
    else { Stop-Update "Unknown argument: $argument. Usage: update-lambda-boss.cmd [--stable]" }
}

# --- Preflight ---

if (Get-Process -Name EXCEL -ErrorAction SilentlyContinue) {
    Stop-Update "Excel is running. Close it and run this again."
}

# --- Find the release ---

$headers = @{ "User-Agent" = "LambdaBoss-updater"; "Accept" = "application/vnd.github+json" }
$url = if ($stable) { "$Api/latest" } else { "$Api/tags/dev-latest" }

try {
    $release = Invoke-RestMethod -UseBasicParsing -Uri $url -Headers $headers
}
catch {
    Stop-Update "Couldn't read $url : $($_.Exception.Message)"
}

$workDir = Join-Path ([IO.Path]::GetTempPath()) ("lambda-boss-update-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $workDir | Out-Null

try {
    # --- Download ---

    $download = Join-Path $workDir $AddInName

    if ($stable) {
        $asset = @($release.assets | Where-Object { $_.name -like "LambdaBoss-*.zip" }) | Select-Object -First 1
        if (-not $asset) { Stop-Update "Release $($release.tag_name) has no LambdaBoss zip." }

        $zip = Join-Path $workDir $asset.name
        Save-Asset $asset $zip
        Expand-Archive -Path $zip -DestinationPath (Join-Path $workDir "zip")
        Move-Item -Path (Join-Path $workDir "zip\$AddInName") -Destination $download

        $version = $release.tag_name
        $commit = $null
    }
    else {
        $asset = @($release.assets | Where-Object { $_.name -eq $AddInName }) | Select-Object -First 1
        if (-not $asset) { Stop-Update "dev-latest has no $AddInName." }

        Save-Asset $asset $download

        $version = Get-NotesField $release.body "version"
        $commit = Get-NotesField $release.body "commit"
    }

    # --- Install ---

    New-Item -ItemType Directory -Force -Path $AddInsDir | Out-Null
    try {
        Move-Item -Path $download -Destination $Target -Force
    }
    catch {
        Stop-Update "Couldn't write $Target (is Excel still closing?): $($_.Exception.Message)"
    }
    Unblock-File -Path $Target

    # A new build starts from its own library or GitHub's, never a copy cached by the old one (#452).
    $cache = Join-Path $env:LOCALAPPDATA "LambdaBoss\cache"
    if (Test-Path $cache) {
        Remove-Item -Recurse -Force -Path $cache -ErrorAction SilentlyContinue
        Write-Host "Cleared the library cache: $cache"
    }
}
finally {
    Remove-Item -Recurse -Force -Path $workDir -ErrorAction SilentlyContinue
}

# --- Report ---

$size = "{0:N1} MB" -f ((Get-Item $Target).Length / 1MB)
Write-Host ""
Write-Host "Installed Lambda Boss $version" -ForegroundColor Green
if ($commit) { Write-Host "  commit: $commit" }
Write-Host "  file:   $Target ($size)"
Show-RegistrationHints
