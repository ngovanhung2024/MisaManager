param(
    [switch]$Publish,
    [string]$OutputRoot = 'out\publish',
    [string]$Version = '0.1.0'
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'release.ps1') -Stage:$Publish -OutputRoot $OutputRoot -Version $Version
