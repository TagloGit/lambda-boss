$ErrorActionPreference = 'Stop'
$dir = '\\Mac\serve'
$out = Join-Path $dir 'lb386-results.tsv'
$filter = $env:LB386_FILTER
$reps = 7
if ($env:LB386_REPS) { $reps = [int]$env:LB386_REPS }
if ($env:LB386_OUT) { $out = Join-Path $dir $env:LB386_OUT }
$names = Get-Content (Join-Path $dir 'lb386-names.tsv') -Encoding UTF8
$cases = Get-Content (Join-Path $dir 'lb386-cases.tsv') -Encoding UTF8
$xl = New-Object -ComObject Excel.Application
try {
    $xl.Visible = $false
    $xl.DisplayAlerts = $false
    $wb = $xl.Workbooks.Add()
    $xl.Calculation = -4135   # manual
    $ws = $wb.Worksheets.Item(1)
    foreach ($line in $names) {
        $p = $line.Split("`t", 2)
        [void]$wb.Names.Add($p[0], $p[1])
    }
    "# Excel $($xl.Version) build $($xl.Build) $($xl.OperatingSystem) threads=$($xl.MultiThreadedCalculation.ThreadCount)" | Out-File $out -Encoding UTF8
    "id`tvalue`tms_runs`tms_median" | Out-File $out -Append -Encoding UTF8
    $row = 1
    foreach ($line in $cases) {
        $p = $line.Split("`t", 2)
        if ($filter -and ($p[0] -notmatch $filter)) { continue }
        $cell = $ws.Cells.Item($row, 1)
        $row++
        $cell.Formula2 = $p[1]
        $runs = @()
        for ($i = 0; $i -lt $reps; $i++) {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $cell.Calculate()
            $sw.Stop()
            $runs += [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
            if ($sw.Elapsed.TotalSeconds -gt 40) { break }
        }
        $med = ($runs | Sort-Object)[[int][math]::Floor(($runs.Count - 1) / 2)]
        $v = $cell.Text
        $min = ($runs | Measure-Object -Minimum).Minimum
        "$($p[0])`t$v`t$($runs -join ',')`t$med`t$min" | Out-File $out -Append -Encoding UTF8
        Write-Output "$($p[0]) $v $med"
    }
} finally {
    if ($wb) { $wb.Close($false) }
    $xl.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($xl)
}
