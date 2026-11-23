# Temporary diagnostics: what the traced debugger sessions did, with the busiest processes around any that failed.
param([string] $Results = 'TestResults', [string] $Samples = 'samples.log')
$ErrorActionPreference = 'Continue'
function Time([string] $text) { [datetime]::ParseExact($text, 'HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture) }
function Cut([string] $text, [int] $length = 300) { if ($text.Length -gt $length) { $text.Substring(0, $length) } else { $text } }

"==== Tests that did not pass, and every debugger integration case"
foreach ($trx in Get-ChildItem $Results -Recurse -Filter *.trx -ErrorAction SilentlyContinue | Sort-Object FullName) {
    try {
        $xml = [xml](Get-Content $trx.FullName -Raw)
        foreach ($result in $xml.TestRun.Results.UnitTestResult) {
            if ($result.outcome -eq 'Passed' -and $result.testName -notlike '*DebuggerIntegrationTests*') { continue }
            $start = ([datetimeoffset]$result.startTime).UtcDateTime.ToString('HH:mm:ss.fff')
            "$($trx.Directory.Name)/$($trx.BaseName) $($result.outcome) $($result.duration) from $start $($result.testName)"
            if ($result.outcome -ne 'Passed') { "    " + (Cut ((($result.Output.ErrorInfo.Message | Out-String).Trim() -split "`n" | Select-Object -First 3) -join ' / ') 600) }
        }
    } catch { "  could not read $($trx.FullName): $_" }
}

$sampleLines = @(if (Test-Path $Samples) { Get-Content $Samples })
"==== $($sampleLines.Count) process samples"

$sessions = [ordered]@{}
foreach ($file in Get-ChildItem tests -Recurse -Filter dap-trace.log -ErrorAction SilentlyContinue) {
    $project = ($file.FullName -split '[\\/]tests[\\/]')[1].Split([char[]]'\/')[0]
    # Transport numbers start again in each test process, and every session starts with initialize.
    $current = @{}
    foreach ($line in Get-Content $file.FullName) {
        if ($line -notmatch '^(\S+) #(\d+) (.*)$') { continue }
        $number = $Matches[2]
        if (-not $current.ContainsKey($number) -or $line -match '-> .*"command":"initialize"') { $current[$number] = "$project#$number@$($line.Substring(0, 12))" }
        $id = $current[$number]
        if (-not $sessions.Contains($id)) { $sessions[$id] = [Collections.Generic.List[string]]::new() }
        $sessions[$id].Add($line)
    }
}
"==== $($sessions.Count) traced DAP sessions; those with a real debuggee:"
$logs = @(Get-ChildItem tests -Recurse -Filter 'netcoredbg-*.log' -ErrorAction SilentlyContinue | Sort-Object Name)
foreach ($id in $sessions.Keys) {
    $lines = $sessions[$id]
    $real = $lines | Where-Object { $_ -match '-> .*"command":"launch".*WpfStudio-DebugTests' -or ($_ -match '<- .*"event":"module"') } | Select-Object -First 1
    if (-not $real) { continue }
    $first = Time $lines[0].Substring(0, 12)
    $mark = @{}
    foreach ($line in $lines) {
        $at = $line.Substring(0, 12)
        foreach ($name in 'launch', 'attach', 'configurationDone', 'setBreakpoints') {
            if ($line -match "-> .*`"command`":`"$name`"" -and -not $mark.ContainsKey("$name->")) { $mark["$name->"] = $at }
            if ($line -match "<- .*`"command`":`"$name`"" -and -not $mark.ContainsKey("$name<-")) { $mark["$name<-"] = $at; $mark["$name ok"] = $line -match '"success":true'; $mark["$name line"] = $line }
        }
        if ($line -match '<- .*"event":"initialized"' -and -not $mark.ContainsKey('initialized')) { $mark['initialized'] = $at }
        if ($line -match '<- .*"event":"process"' -and -not $mark.ContainsKey('process')) { $mark['process'] = $at }
    }
    $kind = if ($mark.ContainsKey('launch->')) { 'launch' } else { 'attach' }
    $done = if ($mark.ContainsKey('configurationDone<-')) { [int]((Time $mark['configurationDone<-']) - (Time $mark['configurationDone->'])).TotalMilliseconds } else { -1 }
    "$id $kind from $($lines[0].Substring(0,12)): $kind sent $($mark["$kind->"]), initialized $($mark['initialized']), configurationDone sent $($mark['configurationDone->']) answered $($mark['configurationDone<-']) after $($done) ms ok=$($mark['configurationDone ok']), $kind answered $($mark["$kind<-"]) ok=$($mark["$kind ok"]), process event $($mark['process'])"
    $failed = ($mark.ContainsKey('configurationDone<-') -and -not $mark['configurationDone ok']) -or ($mark.ContainsKey("$kind<-") -and -not $mark["$kind ok"]) -or $done -lt 0 -or $done -gt 2500
    if (-not $failed) { continue }
    "---- $id in full"
    $lines | Select-Object -First 40 | ForEach-Object { "  " + (Cut $_ 400) }
    $last = if ($mark.ContainsKey('configurationDone<-')) { Time $mark['configurationDone<-'] } else { Time $lines[$lines.Count - 1].Substring(0, 12) }
    "---- busiest processes from 4 s before to 2 s after"
    foreach ($sample in $sampleLines) {
        try { $at = Time $sample.Substring(0, 12) } catch { continue }
        if ($at -ge $first.AddSeconds(-4) -and $at -le $last.AddSeconds(2)) { "  " + (Cut $sample 500) }
    }
    $log = $logs | Where-Object { $_.Name -match '^netcoredbg-(\d{9})-' -and [datetime]::ParseExact($Matches[1], 'HHmmssfff', $null) -le $first.AddSeconds(1) } | Select-Object -Last 1
    if ($log) {
        $text = @(Get-Content $log.FullName)
        "---- $($log.Name): $($text.Count) lines; the last 50"
        $text | Select-Object -Last 50 | ForEach-Object { "  " + (Cut $_ 300) }
    }
}
"==== $($logs.Count) netcoredbg logs"
if ($logs.Count -gt 0) { $sample = @(Get-Content $logs[0].FullName); "first of them, $($sample.Count) lines; its first 15:"; $sample | Select-Object -First 15 | ForEach-Object { "  " + (Cut $_ 300) } }
"==== samples whose four cores were nearly saturated (over 7 s of CPU in 2 s)"
$sampleLines | Where-Object { $_ -match ' cpu (\d+)ms' -and [int]$Matches[1] -gt 7000 } | Select-Object -First 40 | ForEach-Object { "  " + (Cut $_ 400) }
