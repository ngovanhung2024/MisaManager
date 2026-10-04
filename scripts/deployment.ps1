Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:DeploymentRoot = Join-Path $env:LOCALAPPDATA 'MisaManager\deployment'
$script:StateRoot = Join-Path $env:LOCALAPPDATA 'MisaManager'
$script:InstallRoot = Join-Path $env:LOCALAPPDATA 'Programs\MisaManager'
. (Join-Path $PSScriptRoot 'payload-runtime.ps1')

function Write-DeploymentJson([string]$Path, $Value) {
    $temp = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 16))
    try {
        $stream = [IO.FileStream]::new($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temp, $Path, [NullString]::Value) }
        else { [IO.File]::Move($temp, $Path) }
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
}

function Get-DeploymentFiles([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Directory))
    while ($pending.Count) {
        $item = Get-Item -LiteralPath $pending.Pop() -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Reparse point blocked: ' + $item.FullName) }
        if ($item.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $item.FullName -Force) { $pending.Push($child.FullName) }
        } else { $item }
    }
}

function Get-StateFiles {
    if (-not (Test-Path -LiteralPath $script:StateRoot)) { return }
    $active = Join-Path $script:StateRoot 'generations\active.json'
    if (Test-Path -LiteralPath $active) {
        Get-Item -LiteralPath $active -Force
        $pointer = Get-Content -LiteralPath $active -Raw -Encoding UTF8 | ConvertFrom-Json
        $generation = [string]$pointer.generationId
        if ($generation -notmatch '^\d{8}-\d{6}-[a-f0-9]{8}$') { throw 'Active generation identity is invalid.' }
        $generationRoot = Join-Path $script:StateRoot ('generations\' + $generation)
        if (-not (Test-Path -LiteralPath $generationRoot -PathType Container)) { throw 'Active generation directory is missing.' }
        Get-DeploymentFiles $generationRoot
        return
    }
    foreach ($item in Get-ChildItem -LiteralPath $script:StateRoot -Force) {
        if ($item.Name -in @('deployment', 'backups', 'logs', 'diagnostics', 'generations')) { continue }
        Get-DeploymentFiles $item.FullName
    }
}

function Assert-MaintenanceSafe {
    $session = Join-Path $script:StateRoot 'session.json'
    $active = Join-Path $script:StateRoot 'generations\active.json'
    if (Test-Path -LiteralPath $active) {
        $pointer = Get-Content -LiteralPath $active -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace([string]$pointer.generationId)) { throw 'Active generation pointer is invalid.' }
        $session = Join-Path $script:StateRoot ('generations\' + [string]$pointer.generationId + '\session.json')
    }
    if (Test-Path -LiteralPath $session) {
        $record = Get-Content -LiteralPath $session -Encoding UTF8 -Raw | ConvertFrom-Json
        if ($record.documentType -ne 'workflow-session' -or $record.schemaVersion -ne 1) { throw 'Unrecognized workflow state; maintenance blocked.' }
        $snapshot = $record.data.snapshot
        if ($snapshot.status -notin @('saved', 'discarded', 'cancelled', 'failed') -or $snapshot.inputPending) {
            throw 'An active, paused, blocked or uncertain workflow prevents binary/state maintenance.'
        }
    }
}

function Open-MaintenanceLease {
    $created = $false
    $lease = [Threading.Mutex]::new($true, 'Local\MisaManager.App', [ref]$created)
    if (-not $created) { $lease.Dispose(); throw 'Close MISA Excel Manager before install or rollback.' }
    try { Assert-MaintenanceSafe; return $lease } catch { $lease.ReleaseMutex(); $lease.Dispose(); throw }
}

function Get-Deployment([string]$Id) {
    if ($Id -notmatch '^\d{8}-\d{6}-[a-f0-9]{8}$') { throw 'Invalid deployment identifier.' }
    $folder = Join-Path $script:DeploymentRoot $Id
    $record = Get-Content -LiteralPath (Join-Path $folder 'deployment.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($record.id -ne $Id -or $record.documentType -ne 'MisaManager.Deployment' -or $record.schemaVersion -ne 1) { throw 'Invalid deployment record.' }
    return $record
}

function Get-DeploymentManifest($Record) {
    $path = Join-Path (Join-Path $script:DeploymentRoot $Record.id) 'release-manifest.json'
    $manifest = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.documentType -ne 'MisaManager.ReleaseManifest' -or $manifest.schemaVersion -ne 1 -or
        $manifest.version -ne $Record.version -or $manifest.installer.sha256 -ne $Record.msiSha256) {
        throw 'Deployment manifest does not match its recorded version/package.'
    }
    return $manifest
}

function Invoke-InstalledHealth([string]$Version, [string]$Folder, $Manifest) {
    if ($Manifest.documentType -ne 'MisaManager.ReleaseManifest' -or $Manifest.version -ne $Version) {
        throw 'Installed health-check requires the matching release manifest.'
    }
    # Older releases may still list the retired MCP bridge/task board; only the app is checked now.
    foreach ($component in @('app')) {
        $name = 'MisaManager.App.exe'
        $entry = $Manifest.files.PSObject.Properties[($component + '/' + $name)]
        if (-not $entry) {
            throw ('Required component is absent from the release manifest: ' + $component)
        }
        $executable = Join-Path $script:InstallRoot ($component + '\' + $name)
        if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) {
            throw ('Installed executable hash mismatch: ' + $component)
        }
        $signatureEntry = $Manifest.signatures.PSObject.Properties[($component + '/' + $name)]
        if ($signatureEntry -and $signatureEntry.Value -eq 'Valid' -and
            (Get-AuthenticodeSignature -LiteralPath $executable).Status -ne 'Valid') {
            throw ('Installed signature validation failed: ' + $component)
        }
        Assert-SelfContainedPayload (Split-Path -Parent $executable) 'App da cai'
        $output = Join-Path $Folder ($component + '-installed-health.json')
        $arguments = @('--health-check')
        $start = @{ FilePath = $executable; PassThru = $true; Wait = $true; WindowStyle = 'Hidden'; RedirectStandardError = ($output + '.stderr') }
        if ($component -eq 'app') { $arguments += @('--output', ('"' + $output + '"')) }
        else { $start.RedirectStandardOutput = $output }
        $start.ArgumentList = $arguments
        $process = Start-Process @start
        try { $process.Refresh(); if ($process.ExitCode -ne 0) { throw 'Installed health-check process failed.' } } finally { $process.Dispose() }
        $health = Get-Content -LiteralPath $output -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($health.status -ne 'healthy' -or $health.inputSent -ne $false -or $health.version -ne $Version) { throw 'Installed binary health/version mismatch.' }
    }
}

function Invoke-Package([string]$Msi, [string]$Operation, [string]$LogPath) {
    if ($Operation -notin @('/i', '/x')) { throw 'Invalid MSI operation.' }
    $process = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList @($Operation, ('"' + $Msi + '"'), '/qn', '/norestart', '/L*v', ('"' + $LogPath + '"')) -WindowStyle Hidden -Wait -PassThru
    try {
        $process.Refresh()
        if ($process.ExitCode -notin @(0, 3010)) { throw ('Windows Installer failed with exit code ' + $process.ExitCode + '; see ' + $LogPath) }
    } finally { $process.Dispose() }
}

function Assert-PackageHash($Record) {
    $package = Join-Path (Join-Path $script:DeploymentRoot $Record.id) 'MisaManager.msi'
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Record.msiSha256) { throw 'Rollback package hash mismatch.' }
    return $package
}

function Get-StateHashes {
    $hashes = [ordered]@{}
    foreach ($file in Get-StateFiles) {
        $relative = $file.FullName.Substring($script:StateRoot.Length + 1)
        $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $hashes
}

function Restore-StateSnapshot($Record) {
    $folder = Join-Path $script:DeploymentRoot $Record.id
    $snapshotRoot = Join-Path $folder 'state-before'
    foreach ($property in @($Record.stateFiles.PSObject.Properties)) {
        $source = Join-Path $snapshotRoot $property.Name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw ('State snapshot is incomplete: ' + $property.Name) }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $property.Value) { throw ('State snapshot hash mismatch: ' + $property.Name) }
    }
    $current = @(Get-StateFiles)
    foreach ($file in $current) { Remove-Item -LiteralPath $file.FullName -Force }
    foreach ($property in @($Record.stateFiles.PSObject.Properties)) {
        $source = Join-Path $snapshotRoot $property.Name
        $destination = Join-Path $script:StateRoot $property.Name
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }
}
