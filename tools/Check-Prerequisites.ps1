[CmdletBinding()]
param(
    [string]$ApplicationDirectory,
    [switch]$AsJson
)
$ErrorActionPreference = 'Stop'
$checks = [Collections.Generic.List[object]]::new()

function Add-Check([string]$Name, [bool]$Required, [bool]$Succeeded, [string]$Message) {
    $checks.Add([pscustomobject]@{
        Name = $Name
        Status = if ($Succeeded) { 'OK' } elseif ($Required) { 'MISSING' } else { 'OPTIONAL' }
        Required = $Required
        Succeeded = $Succeeded
        Message = $Message
    })
}

if ([string]::IsNullOrWhiteSpace($ApplicationDirectory)) {
    $ApplicationDirectory = $PSScriptRoot
    # The same script works from the repository or from the portable package root.
    if (-not (Test-Path -LiteralPath (Join-Path $ApplicationDirectory 'WpfStudio.exe'))) {
        foreach ($candidate in @('../artifacts/WpfStudio', '../src/WpfStudio.App/bin/Debug/net10.0-windows', '../src/WpfStudio.App/bin/Release/net10.0-windows')) {
            $path = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $candidate))
            if (Test-Path -LiteralPath (Join-Path $path 'WpfStudio.exe')) { $ApplicationDirectory = $path; break }
        }
    }
}
$ApplicationDirectory = [IO.Path]::GetFullPath($ApplicationDirectory)
$isWindowsPlatform = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$windowsBuild = [Environment]::OSVersion.Version.Build
$supportedOs = $isWindowsPlatform -and $windowsBuild -ge 22000 -and $architecture -eq 'X64'
Add-Check 'Windows 11 x64' $true $supportedOs $(if ($supportedOs) { "Windows build $windowsBuild, architecture $architecture." } else { "This package targets Windows 11 on x64. Detected $([Environment]::OSVersion), architecture $architecture." })

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { $null }
if ($isWindowsPlatform) {
    $programFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { [Environment]::GetFolderPath('ProgramFiles') }
    $standardDotnet = Join-Path $programFiles64 'dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $standardDotnet) { $dotnet = $standardDotnet }
}
$sdks = @()
$runtimes = @()
if ($dotnet) {
    $sdks = @(& $dotnet --list-sdks 2>$null)
    $runtimes = @(& $dotnet --list-runtimes 2>$null)
}
$hasSdk = @($sdks | Where-Object { $_ -match '^10\.\d+\.\d+\s' }).Count -gt 0
Add-Check '.NET 10 SDK' $true $hasSdk $(if ($hasSdk) { ($sdks -join '; ') } else { 'Install the .NET 10 x64 SDK from https://dotnet.microsoft.com/download/dotnet/10.0. Building and language services require an SDK even for a self-contained IDE.' })

$selfContained = Test-Path -LiteralPath (Join-Path $ApplicationDirectory 'coreclr.dll')
$hasDesktop = @($runtimes | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 10\.' }).Count -gt 0
Add-Check '.NET 10 Desktop runtime' $true ($selfContained -or $hasDesktop) $(if ($selfContained) { 'This package includes its own runtime (coreclr.dll).' } elseif ($hasDesktop) { ($runtimes | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 10\.' }) -join '; ' } else { 'Install the .NET 10 Windows Desktop Runtime (x64) from https://dotnet.microsoft.com/download/dotnet/10.0, or use a package published with -SelfContained.' })

$appPresent = Test-Path -LiteralPath (Join-Path $ApplicationDirectory 'WpfStudio.exe')
$workerPresent = (Test-Path -LiteralPath (Join-Path $ApplicationDirectory 'WorkspaceHost/WpfStudio.WorkspaceHost.dll')) -and (Test-Path -LiteralPath (Join-Path $ApplicationDirectory 'WorkspaceHost/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll'))
Add-Check 'Application package' $true ($appPresent -and $workerPresent) $(if ($appPresent -and $workerPresent) { "App and Roslyn workspace host found in $ApplicationDirectory." } else { 'Copy the complete published folder, including WorkspaceHost and BuildHost-netcore. From source, run tools/Publish.ps1, or pass -ApplicationDirectory with the complete build output.' })

$previewFiles = @('WpfStudio.PreviewHost.exe', 'WpfStudio.PreviewHost.dll', 'WpfStudio.PreviewHost.runtimeconfig.json', 'WpfStudio.PreviewHost.deps.json', 'WpfStudio.Wpf.PropertyEditing.dll', 'WpfStudio.Wpf.Diagnostics.dll', 'WpfStudio.Inspection.Protocol.dll')
$previewPresent = @($previewFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $ApplicationDirectory "PreviewHost/$_") -PathType Leaf) }).Count -eq 0
Add-Check 'XAML Designer: preview host' $false $previewPresent $(if ($previewPresent) { 'Isolated WPF preview host found in PreviewHost/.' } else { 'Restore the complete PreviewHost/ directory from the portable package, or rebuild/republish. Source editing and diagnostics remain available.' })

$inspectionFiles = @('WpfStudio.Inspection.StartupHook.dll', 'WpfStudio.Inspection.Agent.dll', 'WpfStudio.Inspection.Protocol.dll', 'WpfStudio.Wpf.PropertyEditing.dll', 'WpfStudio.Wpf.Diagnostics.dll')
$inspectionPresent = @($inspectionFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $ApplicationDirectory "Inspection/$_") -PathType Leaf) }).Count -eq 0
Add-Check 'Live XAML: inspection agent' $false $inspectionPresent $(if ($inspectionPresent) { 'Launch-time inspection components found in Inspection/. The target application requires its own compatible .NET Desktop runtime.' } else { 'Restore the complete Inspection/ directory from the portable package, or rebuild/republish. Source editing and preview remain available.' })

$webViewVersion = $null
if ($isWindowsPlatform) {
    # Official Evergreen detection locations:
    # https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution#detect-if-a-webview2-runtime-is-already-installed
    foreach ($key in @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    )) {
        $value = Get-ItemProperty -LiteralPath $key -Name pv -ErrorAction SilentlyContinue
        if ($value -and $value.pv -and $value.pv -ne '0.0.0.0') { $webViewVersion = $value.pv; break }
    }
}
$fixedWebView = $env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER
if ($fixedWebView -and (Test-Path -LiteralPath (Join-Path $fixedWebView 'msedgewebview2.exe'))) { $webViewVersion = 'Fixed runtime: ' + $fixedWebView }
Add-Check 'Terminal: WebView2' $false ([bool]$webViewVersion) $(if ($webViewVersion) { "WebView2 runtime $webViewVersion." } else { 'The terminal requires the free Evergreen WebView2 Runtime: https://developer.microsoft.com/microsoft-edge/webview2/. Editing, builds, and SQL tools remain usable without it.' })

$debuggerPresent = Test-Path -LiteralPath (Join-Path $ApplicationDirectory 'debugger/netcoredbg.exe')
Add-Check 'Debugger: netcoredbg' $false $debuggerPresent $(if ($debuggerPresent) { 'Pinned debugger executable found in debugger/.' } else { 'Restore the debugger/ directory from the portable package. For source builds, run tools/Get-Debugger.ps1 and rebuild or republish.' })
$terminalAssets = Test-Path -LiteralPath (Join-Path $ApplicationDirectory 'Assets/Terminal/xterm.js')
Add-Check 'Terminal: bundled assets' $false $terminalAssets $(if ($terminalAssets) { 'Local xterm renderer found; Node.js is not required.' } else { 'Restore Assets/Terminal from the portable package or rebuild WpfStudio.Runtime.' })

$passed = @($checks | Where-Object { $_.Required -and -not $_.Succeeded }).Count -eq 0
if ($AsJson) {
    [pscustomobject]@{ ApplicationDirectory = $ApplicationDirectory; Ready = $passed; Checks = @($checks) } | ConvertTo-Json -Depth 5
} else {
    $checks | Format-Table Name, Status, Message -Wrap -AutoSize
    if ($passed) { Write-Output 'Required checks passed. Launch WpfStudio.exe. Install additional SDK/runtime versions selected by your project global.json or target framework.' }
    else { Write-Output 'Resolve the MISSING checks above, then run this script again. This check installs nothing.' }
}
if (-not $passed) { exit 1 }
