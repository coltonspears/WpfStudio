#Requires -Version 7.0
param(
    [string]$DotNetRoot = (Join-Path $PSScriptRoot '../artifacts/toolchains/dotnet8'),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$isolatedRoot = [IO.Path]::GetFullPath($DotNetRoot)
if (!(Test-Path (Join-Path $isolatedRoot 'dotnet.exe')) -or
    !(Get-ChildItem (Join-Path $isolatedRoot 'sdk') -Directory -Filter '8.*' -ErrorAction SilentlyContinue) -or
    !(Get-ChildItem (Join-Path $isolatedRoot 'shared/Microsoft.NETCore.App') -Directory -Filter '10.*' -ErrorAction SilentlyContinue)) {
    throw 'Install SDK 8 and the .NET 10 runtime into DotNetRoot using official dotnet-install.ps1 -InstallDir <path> -NoPath.'
}
Push-Location $repository
try {
    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'The development SDK could not be resolved.' }
    $sdkRoot = & dotnet --list-sdks | ForEach-Object { if ($_ -match ('^' + [regex]::Escape($sdkVersion) + '\s+\[(.+)\]$')) { $Matches[1] } } | Select-Object -First 1
    $testRunner = Join-Path $sdkRoot "$sdkVersion/vstest.console.dll"
    if (!$NoBuild) {
        & dotnet build tests/WpfStudio.Workspace.Tests/WpfStudio.Workspace.Tests.csproj --configuration $Configuration --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    }
    $assembly = Join-Path $repository "tests/WpfStudio.Workspace.Tests/bin/$Configuration/net10.0/WpfStudio.Workspace.Tests.dll"
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $isolatedRoot 'dotnet.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $repository
    foreach ($argument in @('exec', $testRunner, $assembly, '/TestCaseFilter:FullyQualifiedName~PinnedSdk8Tests', '/Logger:console;verbosity=normal', '/Logger:trx;LogFileName=sdk8-final.trx', ('/ResultsDirectory:' + (Join-Path $repository 'artifacts/TestResults')))) { $start.ArgumentList.Add($argument) }
    $start.Environment['WPFSTUDIO_DOTNET8_ROOT'] = $isolatedRoot
    $start.Environment['DOTNET_ROOT'] = $isolatedRoot
    $start.Environment['DOTNET_ROOT_X64'] = $isolatedRoot
    $start.Environment['DOTNET_HOST_PATH'] = $start.FileName
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.Environment['PATH'] = $isolatedRoot + [IO.Path]::PathSeparator + $start.Environment['PATH']
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        Write-Output $output.GetAwaiter().GetResult()
        $errorText = $errors.GetAwaiter().GetResult()
        if ($errorText) { Write-Output $errorText }
        if ($process.ExitCode -ne 0) { throw "SDK 8 compatibility test failed with exit code $($process.ExitCode)." }
    }
    finally { $process.Dispose() }
}
finally { Pop-Location }
