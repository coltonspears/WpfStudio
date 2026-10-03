[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SelfContained,
    [switch]$SkipTests,
    [switch]$SkipDebuggerDownload,
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$destination = [IO.Path]::GetFullPath((Join-Path $artifacts 'WpfStudio'))
$staging = [IO.Path]::GetFullPath((Join-Path $artifacts ('publish-' + [Guid]::NewGuid().ToString('N'))))

function Assert-ArtifactPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $allowedPrefix = $artifacts.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to change a path outside the repository artifacts directory: $absolute"
    }
}

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments '-m:2'
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Export-PackageNotices {
    $noticeDirectory = Join-Path $staging 'licenses'
    New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/AvalonEdit-LICENSE.txt') -Destination $noticeDirectory
    $inventory = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($project in @('WpfStudio.App', 'WpfStudio.WorkspaceHost', 'WpfStudio.PreviewHost', 'WpfStudio.ProfilingHost')) {
        $assetsPath = Join-Path $repository "src/$project/obj/project.assets.json"
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
        foreach ($library in $assets.libraries.PSObject.Properties) {
            if ($library.Value.type -ne 'package' -or $inventory.ContainsKey($library.Name)) { continue }
            $packageDirectory = $null
            foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
                $candidate = Join-Path $folder $library.Value.path
                if (Test-Path -LiteralPath $candidate) { $packageDirectory = $candidate; break }
            }
            if (-not $packageDirectory) { throw "Package cache directory missing for $($library.Name)" }
            $nuspecFile = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' | Select-Object -First 1
            [xml]$nuspec = Get-Content -LiteralPath $nuspecFile.FullName -Raw
            $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
            $license = $metadata.SelectSingleNode("*[local-name()='license']")
            $licenseUrl = $metadata.SelectSingleNode("*[local-name()='licenseUrl']")
            $projectUrl = $metadata.SelectSingleNode("*[local-name()='projectUrl']")
            $safeName = $library.Name.Replace('/', '-')
            $packageNotices = Join-Path $noticeDirectory $safeName
            New-Item -ItemType Directory -Path $packageNotices -Force | Out-Null
            Copy-Item -LiteralPath $nuspecFile.FullName -Destination $packageNotices
            $licenseFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -match '^(LICENSE|LICENCE|NOTICE|THIRD[-.]PARTY)' })
            if ($license -and $license.type -eq 'file') {
                $declaredFile = Join-Path $packageDirectory $license.InnerText
                if (Test-Path -LiteralPath $declaredFile) { $licenseFiles += Get-Item -LiteralPath $declaredFile }
            }
            if ($licenseFiles.Count -gt 0) {
                $licenseFiles | Sort-Object FullName -Unique | Copy-Item -Destination $packageNotices -Force
            }
            $inventory.Add($library.Name, [ordered]@{
                package = $library.Name
                licenseType = if ($license) { $license.type } else { $null }
                license = if ($license) { $license.InnerText } else { $null }
                licenseUrl = if ($licenseUrl) { $licenseUrl.InnerText } else { $null }
                projectUrl = if ($projectUrl) { $projectUrl.InnerText } else { $null }
            })
        }
    }
    @($inventory.Values | Sort-Object { $_.package }) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $staging 'THIRD-PARTY-PACKAGES.json') -Encoding utf8
}

Push-Location $repository
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK before packaging WpfStudio.' }
    if (-not ((& dotnet --list-sdks) -match '^10\.')) { throw 'The .NET 10 SDK is required.' }
    $debugger = Join-Path $PSScriptRoot 'netcoredbg/netcoredbg.exe'
    if (-not (Test-Path -LiteralPath $debugger)) {
        if ($SkipDebuggerDownload) { throw 'Pinned debugger is missing. Run tools/Get-Debugger.ps1 or omit -SkipDebuggerDownload.' }
        & (Join-Path $PSScriptRoot 'Get-Debugger.ps1')
    }
    if (-not $SkipTests) { Invoke-DotNet @('test', (Join-Path $repository 'WpfStudio.sln'), '-c', $Configuration, '--verbosity', 'minimal') }
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    $selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
    Invoke-DotNet @('publish', 'src/WpfStudio.App/WpfStudio.App.csproj', '-c', $Configuration, '-r', 'win-x64', '--self-contained', $selfContainedValue, '-p:PublishSingleFile=false', '-o', $staging)
    # Preserve the entire worker publication, including Roslyn BuildHost-netcore and its dependency files.
    Invoke-DotNet @('publish', 'src/WpfStudio.WorkspaceHost/WpfStudio.WorkspaceHost.csproj', '-c', $Configuration, '-r', 'win-x64', '--self-contained', $selfContainedValue, '-p:PublishSingleFile=false', '-o', (Join-Path $staging 'WorkspaceHost'))
    Invoke-DotNet @('publish', 'src/WpfStudio.PreviewHost/WpfStudio.PreviewHost.csproj', '-c', $Configuration, '-r', 'win-x64', '--self-contained', $selfContainedValue, '-p:PublishSingleFile=false', '-o', (Join-Path $staging 'PreviewHost'))
    Invoke-DotNet @('publish', 'src/WpfStudio.ProfilingHost/WpfStudio.ProfilingHost.csproj', '-c', $Configuration, '-r', 'win-x64', '--self-contained', $selfContainedValue, '-p:PublishSingleFile=false', '-o', (Join-Path $staging 'ProfilingHost'))
    # A self-contained x86 worker supports legacy 32-bit Framework dumps even when
    # the machine has only the x64 .NET runtime installed.
    Invoke-DotNet @('publish', 'src/WpfStudio.ProfilingHost/WpfStudio.ProfilingHost.csproj', '-c', $Configuration, '-r', 'win-x86', '--self-contained', 'true', '-p:PublishSingleFile=false', '-o', (Join-Path $staging 'ProfilingHost/x86'))
    $inspectionDirectory = Join-Path $staging 'Inspection'
    New-Item -ItemType Directory -Path $inspectionDirectory -Force | Out-Null
    # Injected libraries run on the target's .NET/WPF runtime. Publish them
    # independently so the IDE's RID/self-contained settings cannot leak in.
    Invoke-DotNet @('publish', 'src/WpfStudio.Inspection.StartupHook/WpfStudio.Inspection.StartupHook.csproj', '-c', $Configuration, '--self-contained', 'false', '-o', $inspectionDirectory)
    Invoke-DotNet @('publish', 'src/WpfStudio.Inspection.Agent/WpfStudio.Inspection.Agent.csproj', '-c', $Configuration, '--self-contained', 'false', '-o', $inspectionDirectory)

    $required = @(
        'WpfStudio.exe',
        'WorkspaceHost/WpfStudio.WorkspaceHost.dll',
        'PreviewHost/WpfStudio.PreviewHost.exe',
        'PreviewHost/WpfStudio.PreviewHost.dll',
        'PreviewHost/WpfStudio.PreviewHost.deps.json',
        'ProfilingHost/WpfStudio.ProfilingHost.dll',
        'ProfilingHost/x86/WpfStudio.ProfilingHost.exe',
        'PreviewHost/WpfStudio.PreviewHost.runtimeconfig.json',
        'PreviewHost/WpfStudio.Wpf.PropertyEditing.dll',
        'PreviewHost/WpfStudio.Wpf.Diagnostics.dll',
        'PreviewHost/WpfStudio.Inspection.Protocol.dll',
        'Inspection/WpfStudio.Inspection.StartupHook.dll',
        'Inspection/WpfStudio.Inspection.Agent.dll',
        'Inspection/WpfStudio.Inspection.Protocol.dll',
        'Inspection/WpfStudio.Wpf.PropertyEditing.dll',
        'Inspection/WpfStudio.Wpf.Diagnostics.dll',
        'WorkspaceHost/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll',
        'Assets/Terminal/index.html',
        'Assets/Terminal/xterm.js',
        'debugger/netcoredbg.exe'
    )
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $relative))) { throw "Published package is incomplete: $relative is missing." }
    }
    Copy-Item -LiteralPath (Join-Path $repository 'README.md'), (Join-Path $repository 'VALIDATION.md'), (Join-Path $repository 'THIRD-PARTY-NOTICES.md'), (Join-Path $PSScriptRoot 'DEBUGGER-NOTICES.md') -Destination $staging
    # Repository notice links must resolve inside the portable folder too.
    $packageNoticesText = [IO.File]::ReadAllText((Join-Path $staging 'THIRD-PARTY-NOTICES.md')).Replace(
        '(tools/DEBUGGER-NOTICES.md)', '(DEBUGGER-NOTICES.md)').Replace(
        '(src/WpfStudio.Runtime/Assets/Terminal/THIRD-PARTY-NOTICES.md)', '(Assets/Terminal/THIRD-PARTY-NOTICES.md)')
    [IO.File]::WriteAllText((Join-Path $staging 'THIRD-PARTY-NOTICES.md'), $packageNoticesText)
    $documentationDirectory = Join-Path $staging 'docs'
    New-Item -ItemType Directory -Path $documentationDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'docs/xaml-devtools-design.md'), (Join-Path $repository 'docs/xaml-runtime-bootstrap.md'), (Join-Path $repository 'docs/xaml-preview-scenarios.md'), (Join-Path $repository 'docs/xaml-appearance.md'), (Join-Path $repository 'docs/xaml-binding-navigation.md'), (Join-Path $repository 'docs/xaml-binding-diagnostics.md'), (Join-Path $repository 'docs/xaml-named-elements.md'), (Join-Path $repository 'docs/xaml-preview-interaction.md'), (Join-Path $repository 'docs/xaml-resource-resolution-plan.md'), (Join-Path $repository 'docs/xaml-feature-tour.md'), (Join-Path $repository 'docs/xaml-language-performance.md') -Destination $documentationDirectory
    Copy-Item -LiteralPath (Join-Path $repository 'docs/memory-profiler.md'), (Join-Path $repository 'docs/profiling-suite-design.md') -Destination $documentationDirectory
    $imageDirectory = Join-Path $documentationDirectory 'images'
    New-Item -ItemType Directory -Path $imageDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'docs/images/xaml') -Destination $imageDirectory -Recurse
    $performanceDocumentation = Join-Path $documentationDirectory 'performance'
    New-Item -ItemType Directory -Path $performanceDocumentation -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'docs/performance/xaml-resource-scale.json') -Destination $performanceDocumentation
    $benchmarkDocumentation = Join-Path $staging 'tools/XamlLanguageBench'
    New-Item -ItemType Directory -Path $benchmarkDocumentation -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'tools/XamlLanguageBench/README.md') -Destination $benchmarkDocumentation
    $sampleRoot = Join-Path $repository 'samples/CounterApp'
    foreach ($sampleFile in Get-ChildItem -LiteralPath $sampleRoot -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
        $sampleTarget = Join-Path $staging ('samples/CounterApp/' + [IO.Path]::GetRelativePath($sampleRoot, $sampleFile.FullName))
        New-Item -ItemType Directory -Path (Split-Path $sampleTarget -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $sampleFile.FullName -Destination $sampleTarget
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Check-Prerequisites.ps1') -Destination $staging
    $measurements = Join-Path $artifacts 'performance'
    if (Test-Path -LiteralPath $measurements) {
        $validationDirectory = Join-Path $staging 'validation'
        New-Item -ItemType Directory -Path $validationDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath $measurements -Filter '*.json' -File | Copy-Item -Destination $validationDirectory
    }
    Export-PackageNotices
    Assert-ArtifactPath $destination
    Assert-ArtifactPath $staging
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    Move-Item -LiteralPath $staging -Destination $destination
    if ($Zip) {
        $zipPath = Join-Path $artifacts 'WpfStudio-win-x64.zip'
        Assert-ArtifactPath $zipPath
        Compress-Archive -LiteralPath $destination -DestinationPath $zipPath -Force
        Write-Output "Archive: $zipPath"
    }
    Write-Output "Portable build: $destination"
    Write-Output "Launch: $(Join-Path $destination 'WpfStudio.exe')"
} finally {
    Pop-Location
    Assert-ArtifactPath $staging
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
