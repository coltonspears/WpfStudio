[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$version = '3.2.0-1092'
$expectedHash = '3c410a45fa502415203a94fcb88654af65bf8e3dac158a5527a722e7a6b9274a'
$target = Join-Path $PSScriptRoot 'netcoredbg'
$archive = Join-Path ([IO.Path]::GetTempPath()) ('wpfstudio-netcoredbg-' + [Guid]::NewGuid().ToString('N') + '.zip')
$staging = Join-Path ([IO.Path]::GetTempPath()) ('wpfstudio-netcoredbg-' + [Guid]::NewGuid().ToString('N'))
try {
    Invoke-WebRequest -Uri "https://github.com/Samsung/netcoredbg/releases/download/$version/netcoredbg-win64.zip" -OutFile $archive
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) { throw "Debugger download checksum mismatch: $actualHash" }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $staging -Force
    $executable = Get-ChildItem -LiteralPath $staging -Filter netcoredbg.exe -Recurse | Select-Object -First 1
    if (-not $executable) { throw 'The pinned debugger archive contains no executable.' }
    Get-ChildItem -LiteralPath $executable.DirectoryName | Copy-Item -Destination $target -Recurse -Force
    Invoke-WebRequest 'https://raw.githubusercontent.com/Samsung/netcoredbg/3.2.0-1092/LICENSE' -OutFile (Join-Path $target 'LICENSE')
    Invoke-WebRequest 'https://raw.githubusercontent.com/Samsung/netcoredbg/3.2.0-1092/third_party/json/LICENSE.MIT' -OutFile (Join-Path $target 'LICENSE-Json.txt')
    Invoke-WebRequest 'https://raw.githubusercontent.com/Samsung/netcoredbg/3.2.0-1092/third_party/linenoise-ng/LICENSE' -OutFile (Join-Path $target 'LICENSE-Linenoise.txt')
    Invoke-WebRequest 'https://raw.githubusercontent.com/dotnet/roslyn/main/License.txt' -OutFile (Join-Path $target 'LICENSE-Roslyn.txt')
    Invoke-WebRequest 'https://raw.githubusercontent.com/dotnet/diagnostics/main/LICENSE.TXT' -OutFile (Join-Path $target 'LICENSE-Diagnostics.txt')
    Invoke-WebRequest 'https://raw.githubusercontent.com/dotnet/runtime/main/LICENSE.TXT' -OutFile (Join-Path $target 'LICENSE-Runtime.txt')
    Write-Output "Installed netcoredbg $version at $target"
} finally {
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
    # Only remove the unique staging directory under the OS temporary directory.
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedStaging.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
