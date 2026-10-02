param([string]$ExpectedAppVersion)
. (Join-Path $PSScriptRoot 'deployment.ps1')
$lease = Open-MaintenanceLease
try {
    $activePath = Join-Path $script:DeploymentRoot 'active.json'
    $pointer = Get-Content -LiteralPath $activePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $active = Get-Deployment $pointer.id
    if (-not $active.previousId) { throw 'No previous installed generation is available.' }
    $previous = Get-Deployment $active.previousId
    if ($previous.status -ne 'healthy' -or $previous.stateSchemaVersion -ne $active.stateSchemaVersion) { throw 'No healthy compatible previous binary/state pair.' }
    if ($ExpectedAppVersion -and $previous.version -ne $ExpectedAppVersion) { throw 'Rollback target version mismatch.' }
    $currentPackage = Assert-PackageHash $active
    $previousPackage = Assert-PackageHash $previous
    $previousManifest = Get-DeploymentManifest $previous
    if (-not $active.stateAfterFiles) { throw 'Deployment lacks a post-upgrade state snapshot; rollback is blocked.' }
    $currentFiles = @(Get-StateFiles)
    if ($currentFiles.Count -ne @($active.stateAfterFiles.PSObject.Properties).Count) { throw 'State changed since upgrade; rollback needs state reconciliation.' }
    foreach ($file in $currentFiles) {
        $relative = $file.FullName.Substring($script:StateRoot.Length + 1)
        $entry = $active.stateAfterFiles.PSObject.Properties[$relative]
        if (-not $entry -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) { throw ('State changed since upgrade; refusing to lose data: ' + $relative) }
    }
    $folder = Join-Path $script:DeploymentRoot $active.id
    Invoke-Package $currentPackage '/x' (Join-Path $folder 'rollback-uninstall.log')
    try {
        Restore-StateSnapshot $active
        Invoke-Package $previousPackage '/i' (Join-Path $folder 'rollback-install.log')
        Invoke-InstalledHealth $previous.version $folder $previousManifest
    } catch {
        Invoke-Package $currentPackage '/i' (Join-Path $folder 'rollback-recovery.log')
        throw
    }
    # State is byte-identical to the pre-upgrade snapshot; retain it without rewriting.
    Write-DeploymentJson $activePath ([ordered]@{ id=$previous.id; version=$previous.version; updatedAt=[DateTimeOffset]::UtcNow.ToString('O') })
    Write-Output ('Restored installed app/bridge ' + $previous.version + '; state unchanged and verified.')
} finally { $lease.ReleaseMutex(); $lease.Dispose() }
