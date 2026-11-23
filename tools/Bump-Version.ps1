<#
.SYNOPSIS
Raises the product version in Version.props and prints the new version.
.DESCRIPTION
A patch raise turns 0.4.2 into 0.4.3, a minor raise into 0.5.0 and a major raise into 1.0.0. Only the version itself is
rewritten, so the rest of the file keeps its layout and line endings.

The version workflow (.github/workflows/version.yml) runs this for every pull request merged into main, with the part
chosen by the pull request's labels. Run it by hand to raise the version outside a pull request.
.EXAMPLE
./tools/Bump-Version.ps1 -Part minor
#>
[CmdletBinding()]
param(
    [ValidateSet('major', 'minor', 'patch')][string]$Part = 'patch',
    [string]$Path = (Join-Path $PSScriptRoot '../Version.props')
)
$ErrorActionPreference = 'Stop'
$file = [IO.Path]::GetFullPath($Path)
$text = [IO.File]::ReadAllText($file)
$found = [regex]::Matches($text, '<VersionPrefix>\s*(\d+)\.(\d+)\.(\d+)\s*</VersionPrefix>')
if ($found.Count -ne 1) { throw "Expected exactly one <VersionPrefix>major.minor.patch</VersionPrefix> in $file, found $($found.Count)." }
$match = $found[0]
[int]$major = $match.Groups[1].Value; [int]$minor = $match.Groups[2].Value; [int]$patch = $match.Groups[3].Value
$next = switch ($Part) {
    'major' { "$($major + 1).0.0" }
    'minor' { "$major.$($minor + 1).0" }
    'patch' { "$major.$minor.$($patch + 1)" }
}
$updated = $text.Substring(0, $match.Index) + "<VersionPrefix>$next</VersionPrefix>" + $text.Substring($match.Index + $match.Length)
# No byte order mark: the file has none, and MSBuild and Git read it the same either way.
[IO.File]::WriteAllText($file, $updated, [Text.UTF8Encoding]::new($false))
Write-Output $next
