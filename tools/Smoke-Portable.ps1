<#
.SYNOPSIS
Starts the published Windows application with isolated settings, measures its first idle shell, and closes it.
.DESCRIPTION
Exercises the real executable, App.OnStartup, DI composition, XAML resources, and shutdown.
No workspace, debugger, terminal session, or SQL connection is opened. Timing includes process startup
but does not assert a cold OS file cache, first-ever .NET initialization, or completed semantic analysis.
#>
[CmdletBinding()]
param(
    [string]$ApplicationDirectory = (Join-Path $PSScriptRoot '../artifacts/WpfStudio'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/performance/portable-smoke.json'),
    [ValidateRange(5, 120)][int]$StartupTimeoutSeconds = 30,
    [ValidateRange(5, 60)][int]$ShutdownTimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'
$applicationRoot = [IO.Path]::GetFullPath($ApplicationDirectory)
$executable = Join-Path $applicationRoot 'WpfStudio.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Published application missing: $executable. Run tools/Publish.ps1 first." }
$reportPath = [IO.Path]::GetFullPath($OutputPath)
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$profileDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('WpfStudio-portable-smoke-' + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $profileDirectory | Out-Null

# EnumWindows includes hidden HWNDs; Process.MainWindowHandle can exclude a hidden launch.
if (-not ('WpfStudio.PortableSmoke.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace WpfStudio.PortableSmoke
{
    public sealed class WindowInfo
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; }
    }
    public static class Native
    {
        private delegate bool Callback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        public static WindowInfo[] Windows(int processId)
        {
            var result = new List<WindowInfo>();
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out uint owner);
                if (owner == processId)
                {
                    var title = new StringBuilder(2048);
                    GetWindowText(window, title, title.Capacity);
                    result.Add(new WindowInfo { Handle = window, Title = title.ToString() });
                }
                return true;
            }, IntPtr.Zero);
            return result.ToArray();
        }
        public static bool RequestClose(IntPtr window) => PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }
}
'@
}

function Get-OwnedDescendants([int]$ParentProcessId) {
    foreach ($child in @(Get-CimInstance -ClassName Win32_Process -Filter "ParentProcessId = $ParentProcessId")) {
        # Keep an actual process handle and creation timestamp to avoid attributing a reused PID later.
        try {
            $ownedProcess = [Diagnostics.Process]::GetProcessById([int]$child.ProcessId)
            [pscustomobject]@{ Process = $ownedProcess; Id = $ownedProcess.Id; Name = $ownedProcess.ProcessName; StartedUtc = $ownedProcess.StartTime.ToUniversalTime() }
            Get-OwnedDescendants -ParentProcessId $ownedProcess.Id
        } catch [ArgumentException] { } catch [InvalidOperationException] { }
    }
}

$appProcess = $null
$trackedChildren = @()
$mainWindow = $null
$failure = $null
try {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.WorkingDirectory = $applicationRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    # Child-only override: no mutation, backup, or restoration of the user's existing profile.
    $start.Environment['WPFSTUDIO_DATA_DIRECTORY'] = $profileDirectory
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $appProcess = [Diagnostics.Process]::Start($start)
    while ($clock.Elapsed.TotalSeconds -lt $StartupTimeoutSeconds) {
        if ($appProcess.HasExited) { throw "WpfStudio exited during startup with code $($appProcess.ExitCode)." }
        $windows = @([WpfStudio.PortableSmoke.Native]::Windows($appProcess.Id))
        if ($windows.Title -contains 'WpfStudio could not start') { throw 'The actual application displayed its startup error dialog.' }
        $mainWindow = $windows | Where-Object Title -EQ 'WpfStudio' | Select-Object -First 1
        if ($mainWindow -and $appProcess.WaitForInputIdle(100)) { break }
        Start-Sleep -Milliseconds 50
    }
    if (-not $mainWindow -or -not $appProcess.WaitForInputIdle(100)) { throw "No idle WpfStudio main window appeared within $StartupTimeoutSeconds seconds." }
    $startupMs = [Math]::Round($clock.Elapsed.TotalMilliseconds)
    # Allow asynchronous empty-profile initialization to settle before checking errors and memory.
    Start-Sleep -Milliseconds 250
    $appProcess.Refresh()
    if ($appProcess.HasExited) { throw 'WpfStudio exited immediately after showing its shell.' }
    $startupError = Join-Path $profileDirectory 'startup-error.log'
    if (Test-Path -LiteralPath $startupError) { throw (Get-Content -LiteralPath $startupError -Raw) }
    $workingSet = $appProcess.WorkingSet64
    $privateBytes = $appProcess.PrivateMemorySize64
    $trackedChildren = @(Get-OwnedDescendants -ParentProcessId $appProcess.Id)
    $shutdown = [Diagnostics.Stopwatch]::StartNew()
    if (-not [WpfStudio.PortableSmoke.Native]::RequestClose($mainWindow.Handle)) { throw 'Could not request graceful closure of the main window.' }
    if (-not $appProcess.WaitForExit($ShutdownTimeoutSeconds * 1000)) { throw "WpfStudio did not close gracefully within $ShutdownTimeoutSeconds seconds." }
    $shutdownMs = [Math]::Round($shutdown.Elapsed.TotalMilliseconds)
    if ($appProcess.ExitCode -ne 0) { throw "WpfStudio exited with code $($appProcess.ExitCode)." }
    $shutdownError = Join-Path $profileDirectory 'shutdown-error.log'
    if (Test-Path -LiteralPath $shutdownError) { throw (Get-Content -LiteralPath $shutdownError -Raw) }
    foreach ($child in $trackedChildren) {
        if (-not $child.Process.WaitForExit(5000)) { throw "Owned child process did not exit: $($child.Name) ($($child.Id))." }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $profileDirectory 'settings.json'))) {
        throw 'The isolated profile was not saved. Confirm this package supports WPFSTUDIO_DATA_DIRECTORY and completed normal shutdown.'
    }
    $report = [ordered]@{
        Label = 'Actual portable executable: process start to first idle main-window HWND; isolated empty profile. OS/.NET caches may be warm; no workspace or optional feature initialized.'
        Executable = $executable
        ProcessId = $appProcess.Id
        MainWindowTitle = $mainWindow.Title
        ProcessStartToIdleShellMs = $startupMs
        ShellWorkingSetBytes = $workingSet
        ShellPrivateBytes = $privateBytes
        GracefulShutdownMs = $shutdownMs
        ExitCode = $appProcess.ExitCode
        OwnedChildrenObserved = @($trackedChildren | ForEach-Object { [ordered]@{ Id = $_.Id; Name = $_.Name; StartedUtc = $_.StartedUtc } })
        OwnedChildrenExited = $true
        IsolatedProfileSaved = $true
        CapturedUtc = [DateTime]::UtcNow
    }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($reportPath)) -Force | Out-Null
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8
    [pscustomobject]$report
    Write-Host "Portable smoke passed. Report: $reportPath"
} catch {
    $failure = $_
} finally {
    # Cleanup is restricted to this invocation's process handles and randomly named temporary profile.
    if ($appProcess) {
        if (-not $appProcess.HasExited) {
            foreach ($ownedWindow in [WpfStudio.PortableSmoke.Native]::Windows($appProcess.Id)) { [void][WpfStudio.PortableSmoke.Native]::RequestClose($ownedWindow.Handle) }
            if (-not $appProcess.WaitForExit(3000)) { $appProcess.Kill($true); [void]$appProcess.WaitForExit(5000) }
        }
        $appProcess.Dispose()
    }
    foreach ($child in $trackedChildren) {
        try { if (-not $child.Process.HasExited) { $child.Process.Kill($true); [void]$child.Process.WaitForExit(5000) } }
        finally { $child.Process.Dispose() }
    }
    $resolvedProfile = [IO.Path]::GetFullPath($profileDirectory)
    $allowedPrefix = $temporaryRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedProfile.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($resolvedProfile)).StartsWith('WpfStudio-portable-smoke-', [StringComparison]::Ordinal)) {
        throw "Refusing cleanup outside the owned smoke profile: $resolvedProfile"
    }
    if (Test-Path -LiteralPath $resolvedProfile) { Remove-Item -LiteralPath $resolvedProfile -Recurse -Force }
}
if ($failure) { throw $failure }
