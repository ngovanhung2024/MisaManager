param([Parameter(Mandatory=$true)][string]$ReleaseDirectory)
. (Join-Path $PSScriptRoot 'deployment.ps1')
$release = [IO.Path]::GetFullPath($ReleaseDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $release 'release-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.documentType -ne 'MisaManager.ReleaseManifest' -or $manifest.schemaVersion -ne 1 -or $manifest.stateSchemaVersion -ne 1) { throw 'Unsupported release/state schema.' }
if ($manifest.verification -ne 'build_verified' -or $manifest.target -ne 'win-x64') { throw 'Release has not passed build checks.' }
$package = Join-Path $release 'installer\MisaManager.msi'
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.installer.sha256) { throw 'Installer hash mismatch.' }
if ($manifest.signing -eq 'signed' -and (Get-AuthenticodeSignature -LiteralPath $package).Status -ne 'Valid') { throw 'Signed release MSI signature is invalid.' }
foreach ($entry in $manifest.files.PSObject.Properties) {
    $payloadRoot = [IO.Path]::GetFullPath((Join-Path $release 'payload')) + '\'
    $file = [IO.Path]::GetFullPath((Join-Path $payloadRoot $entry.Name))
    if (-not $file.StartsWith($payloadRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload path escapes release.' }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) { throw ('Payload hash mismatch: ' + $entry.Name) }
    $signature = $manifest.signatures.PSObject.Properties[$entry.Name]
    if ($signature -and $signature.Value -eq 'Valid' -and (Get-AuthenticodeSignature -LiteralPath $file).Status -ne 'Valid') { throw ('Payload signature mismatch: ' + $entry.Name) }
}
# Prove the staged bridge carries its runtime before touching the installed generation.
Assert-SelfContainedPayload (Join-Path $release 'payload\bridge') 'Bridge trong release'
Assert-BridgeLaunch (Join-Path $release 'payload\bridge\MisaManager.McpBridge.exe')
$lease = Open-MaintenanceLease
try {
    New-Item -ItemType Directory -Path $script:DeploymentRoot -Force | Out-Null
    $activePath = Join-Path $script:DeploymentRoot 'active.json'
    $previous = $null
    if (Test-Path -LiteralPath $activePath) {
        $pointer = Get-Content -LiteralPath $activePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $previous = Get-Deployment $pointer.id
        if ([Version]$manifest.version -le [Version]$previous.version) { throw 'Use rollback for an earlier release; installs must increase version.' }
    }
    $id = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $folder = Join-Path $script:DeploymentRoot $id
    New-Item -ItemType Directory -Path $folder | Out-Null
    Copy-Item -LiteralPath $package -Destination (Join-Path $folder 'MisaManager.msi')
    Copy-Item -LiteralPath (Join-Path $release 'release-manifest.json') -Destination (Join-Path $folder 'release-manifest.json')
    $hashes = [ordered]@{}
    foreach ($file in Get-StateFiles) {
        $relative = $file.FullName.Substring($script:StateRoot.Length + 1)
        $target = Join-Path (Join-Path $folder 'state-before') $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
        $hashes[$relative] = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $record = [ordered]@{ documentType='MisaManager.Deployment'; schemaVersion=1; id=$id; version=$manifest.version; stateSchemaVersion=1; previousId=$(if($previous){$previous.id}else{$null}); status='prepared'; createdAt=[DateTimeOffset]::UtcNow.ToString('O'); msiSha256=$manifest.installer.sha256; stateFiles=$hashes; signing=$manifest.signing }
    Write-DeploymentJson (Join-Path $folder 'deployment.json') $record
    try {
        Invoke-Package (Join-Path $folder 'MisaManager.msi') '/i' (Join-Path $folder 'install.log')
        Invoke-InstalledHealth $manifest.version $folder $manifest
        $record.stateAfterFiles = Get-StateHashes
        $record.status = 'healthy'
        Write-DeploymentJson (Join-Path $folder 'deployment.json') $record
        Write-DeploymentJson $activePath ([ordered]@{ id=$id; version=$manifest.version; updatedAt=[DateTimeOffset]::UtcNow.ToString('O') })
    } catch {
        $failure = $_
        $record.status = 'failed'
        Write-DeploymentJson (Join-Path $folder 'deployment.json') $record
        if ($previous) {
            $oldPackage = Assert-PackageHash $previous
            $oldManifest = Get-DeploymentManifest $previous
            Invoke-Package (Join-Path $folder 'MisaManager.msi') '/x' (Join-Path $folder 'failed-uninstall.log')
            Invoke-Package $oldPackage '/i' (Join-Path $folder 'automatic-rollback.log')
            Invoke-InstalledHealth $previous.version $folder $oldManifest
        }
        throw $failure
    }
    Write-Output ('Installed and health-verified ' + $record.version + '; deployment ' + $id)
} finally { $lease.ReleaseMutex(); $lease.Dispose() }
