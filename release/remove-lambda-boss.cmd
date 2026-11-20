@echo off
REM Removes Lambda Boss from this Windows user's Excel, for the next person on a shared or
REM competition machine. Run it with Excel closed. It removes only Lambda Boss's own entries:
REM its OPEN values (what loads it at start) and Add-in Manager entries under HKCU, and its
REM settings, cache and log folders. Then it says what is left to delete by hand.
set "LB_SELF=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f=$env:LB_SELF; iex ((Get-Content -LiteralPath $f -Raw) -split '#PS-START\r?\n',2)[1]"
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

# Registry through .NET: value names are literal (paths may hold [ ] or other wildcard characters).
$hkcu = [Microsoft.Win32.Registry]::CurrentUser
$office = $hkcu.OpenSubKey('Software\Microsoft\Office')
$versions = @()
if ($office) { $versions = @($office.GetSubKeyNames() | Where-Object { $_ -match '^\d+\.\d+$' }) }

foreach ($ver in $versions) {
    # OPEN, OPEN1, OPEN2 ... load add-ins at start; Excel reads them until the first gap. Ours
    # come out of that run and the rest move up in order. Each kept value is written to its new
    # slot before any slot is deleted, so a failure part-way leaves a duplicate, never a loss.
    $options = $hkcu.OpenSubKey("Software\Microsoft\Office\$ver\Excel\Options", $true)
    if ($options) {
        $run = New-Object System.Collections.Generic.List[object]
        for ($i = 0; ; $i++) {
            $name = if ($i -eq 0) { 'OPEN' } else { "OPEN$i" }
            $value = $options.GetValue($name, $null, 'DoNotExpandEnvironmentNames')
            if ($null -eq $value) { break }
            $run.Add([pscustomobject]@{ Name = $name; Value = [string]$value; Kind = $options.GetValueKind($name) })
        }
        $ours = @($run | Where-Object { $_.Value -match $pattern })
        if ($ours.Count -gt 0) {
            $keep = @($run | Where-Object { $_.Value -notmatch $pattern })
            for ($i = 0; $i -lt $keep.Count; $i++) {
                $name = if ($i -eq 0) { 'OPEN' } else { "OPEN$i" }
                $options.SetValue($name, $keep[$i].Value, $keep[$i].Kind)
            }
            for ($i = $keep.Count; $i -lt $run.Count; $i++) {
                $options.DeleteValue($run[$i].Name)
            }
            foreach ($o in $ours) { $removed.Add("Excel $ver start-up entry $($o.Name): $($o.Value)") }
        }
        # Any of ours past a gap Excel never reads; just delete it.
        foreach ($name in @($options.GetValueNames())) {
            if ($name -match '^OPEN\d*$' -and ([string]$options.GetValue($name)) -match $pattern) {
                $options.DeleteValue($name)
                $removed.Add("Excel $ver unused start-up entry $name")
            }
        }
        $options.Close()
    }

    # Add-in Manager remembers add-ins listed in the Add-Ins dialog but not ticked.
    $manager = $hkcu.OpenSubKey("Software\Microsoft\Office\$ver\Excel\Add-in Manager", $true)
    if ($manager) {
        foreach ($name in @($manager.GetValueNames())) {
            if ($name -match $pattern) {
                $manager.DeleteValue($name)
                $removed.Add("Excel $ver Add-Ins list entry: $name")
            }
        }
        $manager.Close()
    }
}

foreach ($dir in @((Join-Path $env:APPDATA 'LambdaBoss'), (Join-Path $env:LOCALAPPDATA 'LambdaBoss'))) {
    if (Test-Path -LiteralPath $dir) {
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
$left = @(@((Join-Path $env:APPDATA 'Microsoft\AddIns'), $here) |
    Where-Object { Test-Path -LiteralPath $_ } |
    ForEach-Object { Get-ChildItem -LiteralPath $_ -ErrorAction SilentlyContinue } |
    Where-Object { $_.Name -match $pattern } | Select-Object -ExpandProperty DirectoryName -Unique)
Write-Host ''
if ($left.Count -gt 0) {
    Write-Host 'Left for you to delete (Excel no longer loads it): the add-in folder, with the XLL and its hints file'
    foreach ($l in $left) { Write-Host "  $l" }
} else {
    Write-Host 'Left for you to delete: the folder you copied Lambda Boss into.'
}
