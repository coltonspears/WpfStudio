<#
.SYNOPSIS
Prints the product version from Version.props, such as 0.4.2.
.DESCRIPTION
The release workflow reads the version with this script, and so can anyone who wants to know what the next release will be called.
#>
[CmdletBinding()]
param(
    [string]$Path = (Join-Path $PSScriptRoot '../Version.props')
)
$ErrorActionPreference = 'Stop'
$file = [IO.Path]::GetFullPath($Path)
$found = [regex]::Matches([IO.File]::ReadAllText($file), '<VersionPrefix>\s*(\d+\.\d+\.\d+)\s*</VersionPrefix>')
if ($found.Count -ne 1) { throw "Expected exactly one <VersionPrefix>major.minor.patch</VersionPrefix> in $file, found $($found.Count)." }
Write-Output $found[0].Groups[1].Value
