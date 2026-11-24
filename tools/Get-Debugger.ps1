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
    function Save-PinnedFile([string]$Uri, [string]$Destination, [string]$ExpectedSha256) {
        Invoke-WebRequest -Uri $Uri -OutFile $Destination
        $actual = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
        if ($actual -ine $ExpectedSha256) {
            Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
            throw "License download checksum mismatch for ${Uri}: expected $ExpectedSha256, got $actual"
        }
    }
    Invoke-WebRequest 'https://raw.githubusercontent.com/Samsung/netcoredbg/3.2.0-1092/LICENSE' -OutFile (Join-Path $target 'LICENSE')
    Invoke-WebRequest 'https://raw.githubusercontent.com/Samsung/netcoredbg/3.2.0-1092/third_party/json/LICENSE.MIT' -OutFile (Join-Path $target 'LICENSE-Json.txt')
    Invoke-WebRequest 'https://raw.githubusercontent.com/Samsung/netcoredbg/3.2.0-1092/third_party/linenoise-ng/LICENSE' -OutFile (Join-Path $target 'LICENSE-Linenoise.txt')
    Save-PinnedFile 'https://raw.githubusercontent.com/dotnet/roslyn/0d04884c243978cdd609e219fdb63cb58ebec69d/License.txt' (Join-Path $target 'LICENSE-Roslyn.txt') 'ae48df11a335dc1a615f4f938b69cba73bcf4485c4f97af49b38efb0f216353b'
    Save-PinnedFile 'https://raw.githubusercontent.com/dotnet/diagnostics/a2ce91d9165946b0b6be5466c61f091c7b295c6e/LICENSE.TXT' (Join-Path $target 'LICENSE-Diagnostics.txt') 'cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310'
    Save-PinnedFile 'https://raw.githubusercontent.com/dotnet/runtime/ed3d384706c03937e4eac3c377779f0048fa4c3e/LICENSE.TXT' (Join-Path $target 'LICENSE-Runtime.txt') 'cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310'
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
