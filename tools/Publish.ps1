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
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Export-PackageNotices {
    $noticeDirectory = Join-Path $staging 'licenses'
    New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/AvalonEdit-LICENSE.txt') -Destination $noticeDirectory
    $inventory = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($project in @('WpfStudio.App', 'WpfStudio.WorkspaceHost')) {
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

    $required = @(
        'WpfStudio.exe',
        'WorkspaceHost/WpfStudio.WorkspaceHost.dll',
        'WorkspaceHost/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll',
        'Assets/Terminal/index.html',
        'Assets/Terminal/xterm.js',
        'debugger/netcoredbg.exe'
    )
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $relative))) { throw "Published package is incomplete: $relative is missing." }
    }
    Copy-Item -LiteralPath (Join-Path $repository 'README.md'), (Join-Path $repository 'VALIDATION.md'), (Join-Path $repository 'THIRD-PARTY-NOTICES.md'), (Join-Path $PSScriptRoot 'DEBUGGER-NOTICES.md') -Destination $staging
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
