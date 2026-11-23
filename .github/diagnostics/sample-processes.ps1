# Temporary diagnostics: every two seconds, the processes that used the most CPU since the last sample, one line each.
param([Parameter(Mandatory)] [string] $Path)
$previous = @{}
while ($true) {
    $now = Get-Date
    $rows = foreach ($process in Get-Process) {
        try { $cpu = $process.TotalProcessorTime.TotalMilliseconds } catch { continue }
        $key = "$($process.Id):$($process.ProcessName)"
        $delta = if ($previous.ContainsKey($key)) { $cpu - $previous[$key] } else { 0 }
        $previous[$key] = $cpu
        [pscustomobject]@{ Name = $process.ProcessName; Id = $process.Id; Delta = $delta; WorkingSet = $process.WorkingSet64 }
    }
    $total = ($rows | Measure-Object Delta -Sum).Sum
    $top = $rows | Sort-Object Delta -Descending | Select-Object -First 6 | ForEach-Object { "$($_.Name)/$($_.Id) $([int]$_.Delta)ms $([int]($_.WorkingSet / 1MB))MB" }
    $free = try { [int]((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1024) } catch { -1 }
    Add-Content -Path $Path -Value "$($now.ToUniversalTime().ToString('HH:mm:ss.fff')) cpu $([int]$total)ms free $($free)MB | $($top -join ' | ')"
    Start-Sleep -Milliseconds 2000
}
