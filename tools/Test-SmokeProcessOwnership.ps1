<#
.SYNOPSIS
Checks portable-smoke process ownership without launching or terminating processes.
.DESCRIPTION
Parses the smoke script and evaluates only its ownership helper definitions. CIM
queries are synthetic; the only real process access is a read-only handle to this
test's existing PowerShell process. The smoke script's main body never executes.
#>
[CmdletBinding()]
param([string]$SmokeScript = (Join-Path $PSScriptRoot 'Smoke-Portable.ps1'))

$ErrorActionPreference = 'Stop'
$script:AssertionCount = 0
function Assert-OwnershipTest([bool]$Condition, [string]$Message) {
    $script:AssertionCount++
    if (-not $Condition) { throw "Process ownership regression: $Message" }
}

$parseTokens = $null
$parseErrors = $null
$smokeAst = [Management.Automation.Language.Parser]::ParseFile(
    [IO.Path]::GetFullPath($SmokeScript), [ref]$parseTokens, [ref]$parseErrors)
Assert-OwnershipTest ($parseErrors.Count -eq 0) 'The smoke script must parse successfully.'

$definitions = @{}
foreach ($name in @('Test-OwnedProcessIdentity', 'Get-OwnedDescendants')) {
    $matches = @($smokeAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true))
    Assert-OwnershipTest ($matches.Count -eq 1) "Exactly one $name helper must exist."
    $definitions[$name] = $matches[0]
    # Only function definitions are evaluated, never top-level script statements.
    . ([scriptblock]::Create($matches[0].Extent.Text))
}

$parentStarted = [datetime]::new(2026, 1, 1, 12, 0, 0, [DateTimeKind]::Utc)
$childStarted = $parentStarted.AddSeconds(2)
Assert-OwnershipTest (Test-OwnedProcessIdentity $parentStarted $childStarted $childStarted) 'A matching later child identity should be accepted.'
Assert-OwnershipTest (-not (Test-OwnedProcessIdentity $parentStarted $parentStarted.AddDays(-1) $parentStarted.AddDays(-1))) 'An old child of a reused parent PID must be rejected.'
Assert-OwnershipTest (-not (Test-OwnedProcessIdentity $parentStarted $childStarted $childStarted.AddSeconds(1))) 'A child PID reused between enumeration and handle acquisition must be rejected.'
Assert-OwnershipTest (-not (Test-OwnedProcessIdentity $parentStarted $childStarted $childStarted.AddMilliseconds(1))) 'Identity must not use a millisecond tolerance.'
Assert-OwnershipTest (-not (Test-OwnedProcessIdentity $parentStarted $childStarted $childStarted.AddTicks(10))) 'A full microsecond mismatch must be rejected.'
Assert-OwnershipTest (Test-OwnedProcessIdentity $parentStarted $childStarted $childStarted.AddTicks(9)) 'CIM microsecond truncation should accept the same native creation time.'
Assert-OwnershipTest (Test-OwnedProcessIdentity $parentStarted.ToLocalTime() $childStarted.ToLocalTime() $childStarted.AddTicks(7)) 'Local and UTC representations must identify the same instant.'

$intermediateStarted = $parentStarted.AddSeconds(4)
$oldGrandchildStarted = $parentStarted.AddSeconds(3)
Assert-OwnershipTest (-not (Test-OwnedProcessIdentity $intermediateStarted $oldGrandchildStarted $oldGrandchildStarted)) 'A descendant newer than the root but older than its immediate parent must be rejected.'
$grandchildStarted = $intermediateStarted.AddSeconds(2)
Assert-OwnershipTest (Test-OwnedProcessIdentity $intermediateStarted $grandchildStarted $grandchildStarted.AddTicks(3)) 'A valid immediate-parent relationship should remain accepted at deeper levels.'

# A Process instance alone is not a pinned native process handle. Keep this
# check tied to the safety-critical ordering in the actual production helper.
$enumeratorText = $definitions['Get-OwnedDescendants'].Extent.Text
$pinPosition = $enumeratorText.IndexOf('$ownedProcess.Handle', [StringComparison]::Ordinal)
$timePosition = $enumeratorText.IndexOf('$ownedProcess.StartTime', [StringComparison]::Ordinal)
Assert-OwnershipTest ($pinPosition -ge 0 -and $timePosition -gt $pinPosition) 'The child handle must be pinned before reading its creation time.'
$rootPinPosition = $enumeratorText.IndexOf('$ParentProcess.Handle', [StringComparison]::Ordinal)
$rootTimePosition = $enumeratorText.IndexOf('$ParentProcess.StartTime', [StringComparison]::Ordinal)
Assert-OwnershipTest ($rootPinPosition -ge 0 -and $rootTimePosition -gt $rootPinPosition) 'The immediate parent handle must be pinned before reading its creation time.'

$killCalls = @($smokeAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $node.Member.Value -eq 'Kill'
}, $true))
Assert-OwnershipTest ($killCalls.Count -gt 0) 'The cleanup termination calls must be inspected by this regression.'
foreach ($call in $killCalls) {
    Assert-OwnershipTest ($null -eq $call.Arguments -or $call.Arguments.Count -eq 0) 'Cleanup must terminate only its pinned process; recursive Kill overloads are forbidden.'
}

$script:FakeCimRows = @()
$script:FakeCimCalls = 0
$script:FakeCimFilter = $null
function Get-CimInstance {
    param([string]$ClassName, [string]$Filter)
    if ($ClassName -ne 'Win32_Process') { throw "Unexpected CIM class: $ClassName" }
    $script:FakeCimCalls++
    $script:FakeCimFilter = $Filter
    # Return the prepared snapshot once. Even a broken recursive implementation
    # cannot recurse indefinitely through this intentionally synthetic record.
    if ($script:FakeCimCalls -eq 1) { $script:FakeCimRows }
}

$testProcess = [Diagnostics.Process]::GetCurrentProcess()
try {
    [void]$testProcess.Handle
    $actualStarted = $testProcess.StartTime.ToUniversalTime()
    foreach ($case in @(
        [pscustomobject]@{ Label = 'A stale parent-PID relationship'; CreationDate = $actualStarted.AddDays(-1) },
        [pscustomobject]@{ Label = 'A reused child PID with a different creation identity'; CreationDate = $actualStarted.AddSeconds(1) },
        [pscustomobject]@{ Label = 'A missing CIM creation timestamp'; CreationDate = $null }
    )) {
        $script:FakeCimCalls = 0
        $script:FakeCimRows = @([pscustomobject]@{
            ProcessId = $testProcess.Id
            ParentProcessId = $testProcess.Id
            CreationDate = $case.CreationDate
        })
        $observed = @()
        try {
            $observed = @(Get-OwnedDescendants -ParentProcess $testProcess)
            Assert-OwnershipTest ($observed.Count -eq 0) "$($case.Label) must not yield an owned process."
            Assert-OwnershipTest ($script:FakeCimCalls -eq 1) "$($case.Label) must not become an ancestry root."
            Assert-OwnershipTest ($script:FakeCimFilter -eq "ParentProcessId = $($testProcess.Id)") 'Enumeration must use the actual parent identity.'
        } finally {
            # This only releases handles. It never closes or terminates a process.
            foreach ($entry in $observed) { $entry.Process.Dispose() }
        }
    }
} finally {
    $testProcess.Dispose()
}

Write-Host "Portable-smoke process ownership: $script:AssertionCount assertions passed. No processes were launched or terminated."
