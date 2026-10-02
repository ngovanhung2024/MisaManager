param([string]$OutputDirectory = 'cleanup')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'cleanup-targets.ps1')
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if (-not $output.StartsWith((Join-Path $root 'cleanup'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Inventory belongs below cleanup.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$lock = [IO.File]::Open((Join-Path $root 'cleanup\inventory.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    $entries = @(foreach ($relative in Get-RetiredProjectPaths) {
        $path = Assert-ProjectCleanupPath (Join-Path $root $relative) $root
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $files = @(Get-CleanupFiles $path)
        $digests = [ordered]@{}
        $bytes = [long]0
        foreach ($file in $files) {
            $isReparse = $null -ne $file.PSObject.Properties['Reparse'] -and [bool]$file.Reparse
            $digests[$file.FullName] = if ($isReparse) { 'reparse-point' } else { (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
            $bytes += $file.Length
        }
        [ordered]@{ path=$path; kind='retired-project-component'; size=$bytes; fileCount=$files.Count;
            inventory='recursive_hashed'; digest=$digests; owner='MisaManager';
            reason='Owner requested removal of unused project legacy before live workflow.';
            replacement='MisaManager.sln and versioned profiles'; phase='G7-owner-authorized'; result='dry_run_only' }
    })
    $document = [ordered]@{ documentType='MisaManager.CleanupInventory'; schemaVersion=1;
        generatedAt=[DateTimeOffset]::UtcNow.ToString('O'); root=$root; mode='dry-run'; scope='project_only'; entries=$entries }
    $path = Join-Path $output 'cleanup-manifest.json'
    Write-CleanupJson $path $document
    [pscustomobject]@{manifest=$path; targets=$entries.Count; files=[long](($entries | ForEach-Object { $_.fileCount } | Measure-Object -Sum).Sum);
        bytes=[long](($entries | ForEach-Object { $_.size } | Measure-Object -Sum).Sum)} | ConvertTo-Json
} finally { $lock.Dispose() }
