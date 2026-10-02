param(
    [string]$ReleaseRoot = 'out\releases',
    [int]$Keep = 2,
    [int]$MinimumAgeDays = 30,
    [long]$MaximumBytes = 0,
    [long]$MinimumFreeBytes = 2GB,
    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releases = [IO.Path]::GetFullPath((Join-Path $root $ReleaseRoot))
if (-not (Test-Path -LiteralPath $releases)) { throw 'Release root does not exist.' }
if ((Get-Item -LiteralPath $releases -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release root is a reparse point.' }
$protectedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$deploymentRoot = Join-Path $env:LOCALAPPDATA 'MisaManager\deployment'
if (Test-Path -LiteralPath $deploymentRoot) {
    foreach ($recordPath in Get-ChildItem -LiteralPath $deploymentRoot -Filter deployment.json -File -Recurse -ErrorAction SilentlyContinue) {
        try { $record = Get-Content -LiteralPath $recordPath.FullName -Raw -Encoding UTF8 | ConvertFrom-Json; if ($record.releaseDirectory) { [void]$protectedNames.Add((Split-Path -Leaf ([string]$record.releaseDirectory))) } } catch { }
    }
}
$items = @(Get-ChildItem -LiteralPath $releases -Directory | ForEach-Object {
    $manifestPath = Join-Path $_.FullName 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { return }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.documentType -ne 'MisaManager.ReleaseManifest' -or $manifest.schemaVersion -ne 1) { throw ('Invalid release manifest: ' + $_.FullName) }
    [pscustomobject]@{ Path = $_.FullName; Name = $_.Name; LastWrite = $_.LastWriteTimeUtc; Version = [string]$manifest.version; Verification = [string]$manifest.verification; Live = [string]$manifest.liveVerification; Signing = [string]$manifest.signing }
} | Sort-Object LastWrite -Descending)
if (-not $items) { throw 'No release manifests found.' }
$keepItems = @($items | Select-Object -First ([Math]::Max(1, $Keep)))
$cutoff = [DateTime]::UtcNow.AddDays(-[Math]::Abs($MinimumAgeDays))
$candidates = @($items | Where-Object { $_ -notin $keepItems -and $_.LastWrite -lt $cutoff -and -not $protectedNames.Contains($_.Name) })
$totalBytes = [long]0
foreach ($item in $items) { $totalBytes += (Get-ChildItem -LiteralPath $item.Path -File -Recurse -Force | Measure-Object -Property Length -Sum).Sum }
$report = [ordered]@{ documentType = 'MisaManager.ReleaseRetention'; schemaVersion = 1; generatedAt = [DateTimeOffset]::UtcNow; execute = [bool]$Execute; root = $releases; totalBytes = $totalBytes; maximumBytes = $MaximumBytes; minimumFreeBytes = $MinimumFreeBytes; kept = @($keepItems | Select-Object Name,Version,Verification,Live,Signing); protected = @($protectedNames); candidates = @($candidates | Select-Object Name,Version,Verification,Live,Signing); removed = @() }
if ($Execute) {
    $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($releases).TrimEnd('\').TrimEnd(':'))
    if ($drive.Free -lt $MinimumFreeBytes) { throw "Insufficient free space for safe retention: $($drive.Free) bytes available." }
    foreach ($item in $candidates) {
        if ($item.Live -eq 'live_pending' -or $item.Verification -ne 'build_verified') { continue }
        if ($MaximumBytes -gt 0 -and $totalBytes -le $MaximumBytes) { break }
        $itemBytes = (Get-ChildItem -LiteralPath $item.Path -File -Recurse -Force | Measure-Object -Property Length -Sum).Sum
        Remove-Item -LiteralPath $item.Path -Recurse -Force
        $report.removed += $item.Name
        $totalBytes -= [long]$itemBytes
    }
}
$report | ConvertTo-Json -Depth 8
