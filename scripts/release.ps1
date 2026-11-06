param(
    [switch]$Stage,
    [switch]$Installer,
    [string]$Version = '0.1.0',
    [string]$OutputRoot = 'out\releases',
    [string]$DotnetPath = '',
    [string]$CertificateThumbprint = '',
    # PFX file for CI signing; its password is read from MISAMANAGER_SIGNING_PASSWORD, never from the command line.
    [string]$CertificatePath = '',
    [string]$SignToolPath = '',
    [string]$TimestampUrl = '',
    # CI speed-ups (release.yml): a faster cabinet codec and no ICE validation when the installer authoring is unchanged.
    # Local publishing keeps the WiX defaults and full validation.
    [ValidateSet('', 'none', 'low', 'medium', 'high', 'mszip')][string]$InstallerCompression = '',
    [switch]$SkipInstallerValidation
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'payload-runtime.ps1')
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
} else {
    $dotnet = [IO.Path]::GetFullPath($DotnetPath)
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw ('Specified .NET host was not found: ' + $dotnet) }
}
$releaseVersion = $null
if (-not [Version]::TryParse($Version, [ref]$releaseVersion) -or $releaseVersion.Build -lt 0 -or
    $releaseVersion.Revision -gt 0 -or $releaseVersion.Major -gt 255 -or $releaseVersion.Minor -gt 255 -or
    $releaseVersion.Build -gt 65534) { throw 'Use an MSI-compatible major.minor.build version; a fourth nonzero part is not supported by MSI upgrades.' }
$Version = $releaseVersion.ToString(3)
$assemblyVersion = $Version + '.0'
$sourceRevision = (& git -C $root rev-parse HEAD 2>$null | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($sourceRevision)) { $sourceRevision = 'uncommitted' }
$sourceDirty = -not [string]::IsNullOrWhiteSpace((& git -C $root status --porcelain 2>$null | Out-String).Trim())
$components = @('Core', 'Import', 'Automation', 'App')

function Get-SourceFiles {
    foreach ($name in @('global.json', 'Directory.Build.props', 'Directory.Packages.props', 'MisaManager.sln', 'README.md', 'AGENTS.md', 'APP_WORKFLOW.md', 'RECOVERY.md', 'MIGRATION_REPORT.md', 'THIRD_PARTY_NOTICES.md')) {
        $path = Join-Path $root $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { Get-Item -LiteralPath $path }
    }
$folders = @('installer', 'scripts', 'docs', 'licenses') + @($components | ForEach-Object { 'src\MisaManager.' + $_ })
    if (Test-Path -LiteralPath (Join-Path $root 'profiles')) { $folders += 'profiles' }
    $pending = [Collections.Generic.Stack[string]]::new()
    # Optional folders (docs) may not exist in every revision.
    foreach ($folder in $folders) {
        $path = Join-Path $root $folder
        if (Test-Path -LiteralPath $path -PathType Container) { $pending.Push($path) }
    }
    while ($pending.Count -gt 0) {
        $directory = Get-Item -LiteralPath $pending.Pop()
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Source reparse point is not supported: ' + $directory.FullName) }
        foreach ($item in Get-ChildItem -LiteralPath $directory.FullName -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Source reparse point is not supported: ' + $item.FullName) }
            if ($item.PSIsContainer) {
                if ($item.Name -notin @('bin', 'obj', 'artifacts')) { $pending.Push($item.FullName) }
            } else { $item }
        }
    }
}

function Invoke-ArtifactHealth([string]$Executable, [string]$OutputFile, [switch]$Gui) {
    $arguments = @('--health-check')
    if ($Gui) { $arguments += @('--output', ('"' + $OutputFile + '"')) }
    $start = @{ FilePath = $Executable; ArgumentList = $arguments; PassThru = $true; WindowStyle = 'Hidden' }
    if (-not $Gui) {
        $start.RedirectStandardError = $OutputFile + '.stderr'
        $start.RedirectStandardOutput = $OutputFile
    }
    $healthProcess = Start-Process @start -Wait
    try {
        $healthProcess.Refresh()
        if (-not $healthProcess.HasExited) { throw ('Health-check did not exit: ' + $Executable) }
        $exitCode = $healthProcess.ExitCode
        if ($exitCode -ne 0) { throw ('Health-check exited with ' + $exitCode + ': ' + $Executable) }
    } finally { $healthProcess.Dispose() }
    $health = Get-Content -LiteralPath $OutputFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($health.status -ne 'healthy' -or $health.inputSent -ne $false -or $health.version -ne $assemblyVersion -or $health.architecture -ne 'X64') {
        throw ('Unexpected health or binary version: ' + $Executable)
    }
    return $health
}

function Invoke-ReleaseCleanup([string]$Releases, [int]$Keep = 2) {
    $rootPath = [IO.Path]::GetFullPath($Releases)
    $workspaceOut = [IO.Path]::GetFullPath((Join-Path $root 'out')) + '\'
    if (-not $rootPath.StartsWith($workspaceOut, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release cleanup path must be below workspace out.' }
    if (-not (Test-Path -LiteralPath $rootPath)) { New-Item -ItemType Directory -Path $rootPath -Force | Out-Null; return }
    if ((Get-Item -LiteralPath $rootPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release cleanup root is a reparse point.' }

    $protected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $deploymentRoot = Join-Path $env:LOCALAPPDATA 'MisaManager\deployment'
    if (Test-Path -LiteralPath $deploymentRoot) {
        foreach ($recordPath in Get-ChildItem -LiteralPath $deploymentRoot -Filter deployment.json -File -Recurse) {
            $record = Get-Content -LiteralPath $recordPath.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($record.PSObject.Properties['releaseDirectory'] -and $record.releaseDirectory) {
                [void]$protected.Add((Split-Path -Leaf ([string]$record.releaseDirectory)))
            }
        }
    }

    # Limited query access also resolves elevated app paths hidden by WMI.
    if (-not ('MisaReleaseProcessPath' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class MisaReleaseProcessPath {
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
    public static string Read(uint id) {
        var handle = OpenProcess(0x1000, false, id);
        if (handle == IntPtr.Zero) return null;
        try {
            var path = new StringBuilder(32768);
            uint size = (uint)path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
        } finally { CloseHandle(handle); }
    }
}
'@
    }
    foreach ($process in Get-CimInstance Win32_Process -Filter "Name LIKE 'MisaManager.%'") {
        $path = [MisaReleaseProcessPath]::Read($process.ProcessId)
        if (-not $path) { Write-Warning 'Release cleanup skipped: a running MISA component path is unavailable.'; return }
        if ($path.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
            [void]$protected.Add($path.Substring($rootPath.Length + 1).Split('\')[0])
        }
    }

    $directories = @(Get-ChildItem -LiteralPath $rootPath -Directory -Force)
    foreach ($directory in $directories) {
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw ('Refusing to clean release reparse point: ' + $directory.FullName)
        }
    }
    foreach ($directory in $directories) {
        $manifestPath = Join-Path $directory.FullName 'release-manifest.json'
        if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($manifest.documentType -ne 'MisaManager.ReleaseManifest' -or $manifest.revision -ne $directory.Name) {
                throw ('Invalid release manifest: ' + $manifestPath)
            }
            if ($manifest.verification -ne 'build_verified' -or $manifest.liveVerification -eq 'live_pending') {
                [void]$protected.Add($directory.Name)
            }
            continue
        }
        if ($protected.Contains($directory.Name)) { continue }
        if (@(Get-ChildItem -LiteralPath $directory.FullName -Recurse -Force -Attributes ReparsePoint).Count -gt 0) {
            throw ('Refusing to clean release containing a reparse point: ' + $directory.FullName)
        }
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    }

    $complete = @(Get-ChildItem -LiteralPath $rootPath -Directory -Force | Where-Object {
        Test-Path -LiteralPath (Join-Path $_.FullName 'release-manifest.json')
    } | Sort-Object LastWriteTimeUtc -Descending)
    $keepNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($directory in ($complete | Select-Object -First ([Math]::Max(1, $Keep)))) { [void]$keepNames.Add($directory.Name) }
    $candidates = @($complete | Where-Object { -not $keepNames.Contains($_.Name) -and -not $protected.Contains($_.Name) })
    foreach ($directory in $candidates) {
        if (@(Get-ChildItem -LiteralPath $directory.FullName -Recurse -Force -Attributes ReparsePoint).Count -gt 0) {
            throw ('Refusing to clean release containing a reparse point: ' + $directory.FullName)
        }
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    }
}

$buildRoot = $root
$releaseRoot = $null
$sourceFiles = [ordered]@{}
if ($Stage -or $Installer) {
    $output = [IO.Path]::GetFullPath((Join-Path $root $OutputRoot))
    $allowedOutput = (Join-Path $root 'out') + '\'
    if (-not $output.StartsWith($allowedOutput, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release output must be below the workspace out directory.' }
    $ancestor = $output
    while ($ancestor -and $ancestor.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw ('Release path contains a reparse point: ' + $ancestor)
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    Invoke-ReleaseCleanup $output
    $revision = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $releaseRoot = Join-Path $output $revision
    $buildRoot = Join-Path $releaseRoot 'source'
    New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    # Compile the captured source, so concurrent workspace edits cannot change this release.
    foreach ($file in Get-SourceFiles | Sort-Object FullName -Unique) {
        $relative = $file.FullName.Substring($root.Length + 1)
        $destination = Join-Path $buildRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
        $sourceFiles[$relative.Replace('\', '/')] = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

Push-Location $buildRoot
try {
    # Tests are not part of the captured release source; the app project pulls in every shipped library.
    $appProject = 'src\MisaManager.App\MisaManager.App.csproj'
    $sdk = (& $dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Pinned .NET SDK is unavailable.' }
    # Generate/refresh RID-specific lock entries inside the build snapshot,
    # then prove the exact snapshot is reproducible with locked mode.
    & $dotnet restore $appProject -r win-x64 --force-evaluate --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Runtime restore failed.' }
    & $dotnet restore $appProject -r win-x64 --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    # Staging publishes (and so compiles) the same project below; a separate build only doubled the compile time.
    if (-not $releaseRoot) {
        & $dotnet build $appProject -c Release --no-restore -p:Version=$Version --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
        return
    }

    $publish = Join-Path $releaseRoot 'payload'
    foreach ($component in @('App')) {
        $folder = 'app'
        & $dotnet publish "src\MisaManager.$component\MisaManager.$component.csproj" -c Release -r win-x64 --self-contained true --no-restore -p:Version=$Version -o (Join-Path $publish $folder) --nologo
        if ($LASTEXITCODE -ne 0) { throw ('Publish failed: ' + $component) }
        # The build host has .NET installed, so health checks alone cannot catch a framework-dependent payload.
        Assert-SelfContainedPayload (Join-Path $publish $folder) $component
    }
    $metadata = Join-Path $releaseRoot 'metadata'
    New-Item -ItemType Directory -Path $metadata | Out-Null
    $appHealth = Invoke-ArtifactHealth (Join-Path $publish 'app\MisaManager.App.exe') (Join-Path $metadata 'app-health.json') -Gui
    if (-not $appHealth.native.initialized -or $appHealth.native.backend -ne 'UIA3/Win32') { throw 'Native backend health-check failed.' }

    # Restore the captured source files after RID restore. Lock files can gain
    # generated runtime entries during restore; the release source manifest must
    # still describe the exact workspace source checked by repair handoff.
    foreach ($entry in $sourceFiles.GetEnumerator()) {
        $workspaceFile = Join-Path $root $entry.Key
        $captured = Join-Path $buildRoot $entry.Key
        if ((Get-FileHash -LiteralPath $workspaceFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) {
            throw ('Workspace source changed during release: ' + $entry.Key)
        }
        if ((Get-FileHash -LiteralPath $captured -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) {
            Copy-Item -LiteralPath $workspaceFile -Destination $captured -Force
        }
    }

    $signingState = 'unsigned_internal'
    $signArgs = @()
    if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
        if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) { throw 'CertificatePath does not exist.' }
        $signArgs = @('sign', '/f', [IO.Path]::GetFullPath($CertificatePath), '/fd', 'SHA256')
        if (-not [string]::IsNullOrEmpty($env:MISAMANAGER_SIGNING_PASSWORD)) { $signArgs += @('/p', $env:MISAMANAGER_SIGNING_PASSWORD) }
    } elseif (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        $signArgs = @('sign', '/sha1', $CertificateThumbprint, '/fd', 'SHA256', '/a')
    }
    if ($signArgs.Count -gt 0) {
        if ([string]::IsNullOrWhiteSpace($SignToolPath)) {
            $SignToolPath = Get-ChildItem -Path (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\*\x64\signtool.exe') -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
        if ([string]::IsNullOrWhiteSpace($SignToolPath) -or -not (Test-Path -LiteralPath $SignToolPath)) { throw 'signtool.exe was not found; pass -SignToolPath.' }
        if ([string]::IsNullOrWhiteSpace($TimestampUrl)) { $TimestampUrl = 'http://timestamp.digicert.com' }
        $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256')
        foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $publish 'app') -File | Where-Object { $_.Name -like 'MisaManager.*' -and $_.Extension -in '.exe', '.dll' } | ForEach-Object { 'app\' + $_.Name })) {
            & $SignToolPath @signArgs (Join-Path $publish $file)
            if ($LASTEXITCODE -ne 0 -or (Get-AuthenticodeSignature -LiteralPath (Join-Path $publish $file)).Status -ne 'Valid') { throw ('Signing failed: ' + $file) }
        }
        $signingState = 'signed'
    }

    if (Test-Path -LiteralPath (Join-Path $buildRoot 'licenses')) {
        $noticeRoot = Join-Path $releaseRoot 'notices'
        New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $buildRoot 'licenses') -File -Recurse | Sort-Object FullName) {
            $relative = $file.FullName.Substring((Join-Path $buildRoot 'licenses').Length + 1).Replace('\', '__').Replace('/', '__')
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $noticeRoot $relative) -Force
        }
        $thirdPartyNotices = Join-Path $buildRoot 'THIRD_PARTY_NOTICES.md'
        if (Test-Path -LiteralPath $thirdPartyNotices -PathType Leaf) {
            Copy-Item -LiteralPath $thirdPartyNotices -Destination $noticeRoot -Force
        }
        $payloadNoticeRoot = Join-Path $publish 'notices'
        New-Item -ItemType Directory -Path $payloadNoticeRoot -Force | Out-Null
        Copy-Item -Path (Join-Path $noticeRoot '*') -Destination $payloadNoticeRoot -Force
    }

    $installerArtifact = $null
    if ($Installer) {
        & $dotnet restore installer\MisaManager.wixproj --locked-mode --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Locked installer restore failed.' }
    $installerOutput = Join-Path $releaseRoot 'installer'
        New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null
        $wixArguments = @('build', 'installer\MisaManager.wixproj', '-c', 'Release', '--no-restore', "-p:PackageVersion=$Version",
            "-p:PublishRoot=$publish", "-p:OutputPath=$installerOutput", '--nologo')
        if ($InstallerCompression) { $wixArguments += "-p:DefaultCompressionLevel=$InstallerCompression" }
        if ($SkipInstallerValidation) { $wixArguments += '-p:SuppressValidation=true' }
        & $dotnet @wixArguments
        if ($LASTEXITCODE -ne 0) { throw 'WiX installer build failed.' }
        $msi = Join-Path $installerOutput 'MisaManager.msi'
        # WiX 4 writes the package to its intermediate directory when a custom
        # OutputPath is supplied. Normalize that SDK behavior into the release root.
        if (-not (Test-Path -LiteralPath $msi -PathType Leaf)) {
            $candidates = @(Get-ChildItem -LiteralPath (Join-Path $buildRoot 'installer') -Recurse -File -Filter 'MisaManager.msi' |
                Where-Object { $_.FullName -notlike ($installerOutput + '\*') } | Sort-Object LastWriteTimeUtc -Descending)
            if ($candidates.Count -ne 1) { throw 'WiX build did not produce a unique MSI artifact.' }
            Copy-Item -LiteralPath $candidates[0].FullName -Destination $msi
        }
        if ($signingState -eq 'signed') {
            & $SignToolPath @signArgs $msi
            if ($LASTEXITCODE -ne 0 -or (Get-AuthenticodeSignature -LiteralPath $msi).Status -ne 'Valid') { throw 'MSI signature verification failed.' }
        }
        $installerArtifact = [ordered]@{ path = 'installer/MisaManager.msi'; sha256 = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant(); signature = (Get-AuthenticodeSignature -LiteralPath $msi).Status.ToString() }
    }

    $manifest = [ordered]@{
        documentType = 'MisaManager.ReleaseManifest'; schemaVersion = 1; revision = $revision
        version = $appHealth.version; target = 'win-x64'; sdk = $sdk; generatedAt = [DateTimeOffset]::UtcNow
        verification = 'build_verified'; signing = $signingState; liveVerification = 'not_run'
        sourceRevision = $sourceRevision; sourceDirty = $sourceDirty
        stateSchemaVersion = $appHealth.schemaVersion; workbookSchemaVersion = $appHealth.workbookSchemaVersion
        locatorProfileVersion = $appHealth.locatorProfileVersion
        source = [ordered]@{ directory = 'source'; files = $sourceFiles }
        nativeProfileStatus = 'pending_live_verify'; payload = 'payload'; files = [ordered]@{}
        signatures = [ordered]@{}; installer = $installerArtifact
    }
    if (Test-Path -LiteralPath (Join-Path $buildRoot 'licenses')) {
        $noticeRoot = Join-Path $releaseRoot 'notices'
        $manifest['notices'] = [ordered]@{ directory = 'notices'; files = [ordered]@{} }
        foreach ($file in Get-ChildItem -LiteralPath $noticeRoot -File -Recurse | Sort-Object FullName) {
            $relative = $file.FullName.Substring($noticeRoot.Length + 1).Replace('\\', '/')
            $manifest.notices.files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName) {
        $relative = $file.FullName.Substring($publish.Length + 1).Replace('\', '/')
        $manifest.files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    foreach ($entry in @('app/MisaManager.App.exe')) {
        $manifest.signatures[$entry] = (Get-AuthenticodeSignature -LiteralPath (Join-Path $publish $entry)).Status.ToString()
    }
    # Recheck source inputs after restore/build, including lockfiles.
    foreach ($entry in $sourceFiles.GetEnumerator()) {
        if ((Get-FileHash -LiteralPath (Join-Path $buildRoot $entry.Key) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) {
            throw ('Build changed a captured source input: ' + $entry.Key)
        }
    }
    $manifestPath = Join-Path $releaseRoot 'release-manifest.json'
    [IO.File]::WriteAllText($manifestPath + '.tmp', ($manifest | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($manifestPath + '.tmp', $manifestPath)
    Invoke-ReleaseCleanup $output
    Write-Output $manifestPath
} finally { Pop-Location }

