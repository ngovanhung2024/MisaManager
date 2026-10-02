param([string]$ManifestPath = 'cleanup\cleanup-manifest.json', [switch]$Execute)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'cleanup-targets.ps1')
. (Join-Path $PSScriptRoot 'deployment.ps1')
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$manifestPath = [IO.Path]::GetFullPath((Join-Path $root $ManifestPath))
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.documentType -ne 'MisaManager.CleanupInventory' -or $manifest.schemaVersion -ne 1 -or
    $manifest.mode -ne 'dry-run' -or $manifest.scope -ne 'project_only' -or $manifest.root -ne $root) {
    throw 'Expected this project-only inventory; user/runtime state is out of scope.'
}
$lease = $null
$lock = [IO.File]::Open((Join-Path $root 'cleanup\inventory.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    if ($Execute) { $lease = Open-MaintenanceLease }
    $config = Get-Content -LiteralPath (Join-Path $root '.codex\config.toml') -Raw
    $processes = @(Get-CimInstance Win32_Process)
    $verified = @()
    $skipped = @()
    foreach ($entry in $manifest.entries) {
        $path = Assert-ProjectCleanupPath $entry.path $root
        if (-not (Test-Path -LiteralPath $path)) { $skipped += $path; continue }
        if ($entry.inventory -ne 'recursive_hashed') { throw ('Recursive inventory required: ' + $path) }
        if ($config.IndexOf($path, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw ('Project MCP config still uses: ' + $path) }
        foreach ($process in $processes) {
            if (($process.ExecutablePath -and ($process.ExecutablePath -eq $path -or $process.ExecutablePath.StartsWith($path+'\', [StringComparison]::OrdinalIgnoreCase))) -or
                ($process.CommandLine -and $process.CommandLine.IndexOf($path, [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
                throw ('Process still uses target: ' + $process.ProcessId + ' ' + $path)
            }
        }
        $files = @(Get-CleanupFiles $path)
        if ($files.Count -ne $entry.fileCount) { throw ('File count changed: ' + $path) }
        $digests = @{}
        foreach ($property in $entry.digest.PSObject.Properties) { $digests[$property.Name] = $property.Value }
        $links = @()
        $bytes = [long]0
        foreach ($file in $files) {
            $expected = $digests[$file.FullName]
            $isReparse = $null -ne $file.PSObject.Properties['Reparse'] -and [bool]$file.Reparse
            if ($isReparse) {
                $link = Get-Item -LiteralPath $file.FullName -Force
                if ($link.FullName -ne (Join-Path $root 'vendor\python-build\cpython-3.10-windows-x86_64-none') -or
                    $link.LinkType -ne 'Junction' -or @($link.Target).Count -ne 1 -or
                    $link.Target[0] -ne (Join-Path $root 'vendor\python-build\cpython-3.10.20-windows-x86_64-none')) {
                    throw ('Unreviewed reparse point: ' + $link.FullName)
                }
                $links += $link.FullName
            }
            $actual = if ($isReparse) { 'reparse-point' } else { (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
            if (-not $expected -or $actual -ne $expected) {
                throw ('File changed since inventory: ' + $file.FullName)
            }
            $bytes += $file.Length
        }
        $verified += [pscustomobject]@{path=$path; bytes=$bytes; files=$files.Count; links=$links}
        Write-Output ('Verified: ' + $path)
    }
    if (-not $Execute) {
        [pscustomobject]@{status='dry_run'; targets=$verified.Count; bytes=($verified | Measure-Object bytes -Sum).Sum} | ConvertTo-Json
        return
    }
    $reportPath = Join-Path (Split-Path -Parent $manifestPath) 'cleanup-report.json'
    $report = [ordered]@{documentType='MisaManager.CleanupReport'; schemaVersion=1; scope='project_only';
        status='running'; startedAt=[DateTimeOffset]::UtcNow.ToString('O'); manifest=$manifestPath;
        manifestSha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();
        removed=@(); alreadyAbsent=$skipped; filesRemoved=0L; bytesRemoved=0L;
        retained=@('.git', 'current C# source', 'native releases 0.1.21/0.1.22', 'external runtime state and deployment backups', 'MISA and original workbooks')}
    Write-CleanupJson $reportPath $report
    foreach ($item in $verified) {
        $path = Assert-ProjectCleanupPath $item.path $root
        # Unlink the reviewed junction itself; never recurse through its target.
        foreach ($link in $item.links) { [IO.Directory]::Delete($link, $false) }
        Remove-Item -LiteralPath $path -Recurse -Force
        if (Test-Path -LiteralPath $path) { throw ('Removal incomplete: ' + $path) }
        $report.removed += $path
        $report.filesRemoved += $item.files
        $report.bytesRemoved += $item.bytes
        Write-CleanupJson $reportPath $report
    }
    $report.status = 'cleaned'
    $report['inventoryEntriesNowAbsent'] = 0L
    $report['inventoryBytesNowAbsent'] = 0L
    foreach ($entry in $manifest.entries) {
        $path = Assert-ProjectCleanupPath $entry.path $root
        if (Test-Path -LiteralPath $path) { throw ('Target remains after cleanup: ' + $path) }
        $report.inventoryEntriesNowAbsent += $entry.fileCount
        $report.inventoryBytesNowAbsent += $entry.size
    }
    $report['completedAt'] = [DateTimeOffset]::UtcNow.ToString('O')
    Write-CleanupJson $reportPath $report
    [pscustomobject]@{report=$reportPath; targets=$report.removed.Count; files=$report.filesRemoved; bytes=$report.bytesRemoved; status=$report.status} | ConvertTo-Json
} finally {
    $lock.Dispose()
    if ($lease) { $lease.ReleaseMutex(); $lease.Dispose() }
}
