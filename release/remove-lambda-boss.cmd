@echo off
REM Removes Lambda Boss from this Windows user's Excel, for the next person on a shared or
REM competition machine. Run it with Excel closed. It removes only Lambda Boss's own entries:
REM its OPEN values (what loads it at start) and Add-in Manager entries under HKCU, and its
REM settings, cache and log folders. Then it says what is left to delete by hand.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f='%~f0'; iex ((Get-Content -LiteralPath $f -Raw) -split '#PS-START\r?\n',2)[1]"
echo.
pause
exit /b
#PS-START
$ErrorActionPreference = 'Stop'
$pattern = 'lambda-boss[^\\/]*\.xll'
$removed = New-Object System.Collections.Generic.List[string]

if (Get-Process -Name EXCEL -ErrorAction SilentlyContinue) {
    Write-Host 'Excel is running. Close every Excel window, then run this again.' -ForegroundColor Yellow
    return
}

$officeRoot = 'HKCU:\Software\Microsoft\Office'
$versions = @()
if (Test-Path $officeRoot) { $versions = Get-ChildItem $officeRoot | Where-Object { $_.PSChildName -match '^\d+\.\d+$' } }

foreach ($v in $versions) {
    # OPEN, OPEN1, OPEN2 ... load add-ins at start. Excel stops at the first gap, so the others
    # are renumbered in their order after Lambda Boss's are taken out.
    $options = Join-Path $v.PSPath 'Excel\Options'
    if (Test-Path $options) {
        $props = Get-ItemProperty $options
        $open = foreach ($p in $props.PSObject.Properties) {
            if ($p.Name -match '^OPEN(\d*)$') {
                [pscustomobject]@{ Name = $p.Name; Index = $(if ($Matches[1]) { [int]$Matches[1] } else { 0 }); Value = [string]$p.Value }
            }
        }
        $open = @($open | Sort-Object Index)
        $ours = @($open | Where-Object { $_.Value -match $pattern })
        if ($ours.Count -gt 0) {
            $keep = @($open | Where-Object { $_.Value -notmatch $pattern })
            foreach ($o in $open) { Remove-ItemProperty -Path $options -Name $o.Name }
            for ($i = 0; $i -lt $keep.Count; $i++) {
                $name = if ($i -eq 0) { 'OPEN' } else { "OPEN$i" }
                New-ItemProperty -Path $options -Name $name -Value $keep[$i].Value -PropertyType String | Out-Null
            }
            foreach ($o in $ours) { $removed.Add("Excel $($v.PSChildName) start-up entry $($o.Name): $($o.Value)") }
        }
    }

    # Add-in Manager remembers add-ins listed in the Add-Ins dialog but not ticked.
    $manager = Join-Path $v.PSPath 'Excel\Add-in Manager'
    if (Test-Path $manager) {
        foreach ($p in (Get-ItemProperty $manager).PSObject.Properties) {
            if ($p.Name -notlike 'PS*' -and $p.Name -match $pattern) {
                Remove-ItemProperty -Path $manager -Name $p.Name
                $removed.Add("Excel $($v.PSChildName) Add-Ins list entry: $($p.Name)")
            }
        }
    }
}

foreach ($dir in @((Join-Path $env:APPDATA 'LambdaBoss'), (Join-Path $env:LOCALAPPDATA 'LambdaBoss'))) {
    if (Test-Path $dir) {
        Remove-Item -LiteralPath $dir -Recurse -Force
        $removed.Add("Folder: $dir (settings, library cache, log)")
    }
}

Write-Host ''
if ($removed.Count -eq 0) {
    Write-Host 'Nothing of Lambda Boss was registered for this user.'
} else {
    Write-Host 'Removed:' -ForegroundColor Green
    foreach ($r in $removed) { Write-Host "  $r" }
}

$here = Split-Path -Parent $f
$left = @(Get-ChildItem -Path (Join-Path $env:APPDATA 'Microsoft\AddIns'), $here -Filter 'lambda-boss*.xll' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match $pattern } | Select-Object -ExpandProperty DirectoryName -Unique)
Write-Host ''
Write-Host 'Left for you to delete (Excel no longer loads it):'
if ($left.Count -eq 0) { $left = @($here) }
foreach ($l in $left) { Write-Host "  $l" }
