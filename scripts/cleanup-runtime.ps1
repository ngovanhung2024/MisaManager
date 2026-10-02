param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'SilentlyContinue'
$full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
$parent = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ((-not ([IO.Path]::GetFileName($full)).StartsWith('MisaManager-build-cleanup-', [StringComparison]::Ordinal)) -or (-not [string]::Equals([IO.Path]::GetDirectoryName($full), $parent, [StringComparison]::OrdinalIgnoreCase))) { exit 2 }
if (-not (Test-Path -LiteralPath $full -PathType Container)) { exit 0 }
Remove-Item -LiteralPath $full -Recurse -Force
