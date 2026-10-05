param(
    [switch]$Publish,
    [string]$DotnetPath = '',
    [string]$CertificateThumbprint = '',
    [string]$CertificatePath = '',
    [string]$SignToolPath = '',
    [string]$TimestampUrl = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($DotnetPath)) { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }

function Assert-Exit([string]$Operation) {
    if ($LASTEXITCODE -ne 0) { throw "$Operation failed (exit $LASTEXITCODE)." }
}

Push-Location $root
try {
    $dirty = (& git status --porcelain | Out-String).Trim()
    Assert-Exit 'Git status'
    if ($dirty) { throw 'Commit or preserve local changes before preparing a release.' }
    $revision = (& git rev-parse HEAD | Out-String).Trim()
    Assert-Exit 'Read source revision'
    $properties = [xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)
    $baseVersion = [Version](@($properties.Project.PropertyGroup.Version | Where-Object { $_ })[0])
    $count = (& git rev-list --count HEAD | Out-String).Trim()
    Assert-Exit 'Count commits'
    $version = "$($baseVersion.Major).$($baseVersion.Minor).$count"
    $repositoryNode = @($properties.Project.PropertyGroup.UpdateRepository | Where-Object { $_ })[0]
    $repository = if ($repositoryNode -is [System.Xml.XmlElement]) { $repositoryNode.InnerText } else { [string]$repositoryNode }
    if ([string]::IsNullOrWhiteSpace($repository)) { $repository = 'ngovanhung2024/MisaManager' }

    if ($Publish) {
        & git fetch origin master
        Assert-Exit 'Fetch master'
        $master = (& git rev-parse origin/master | Out-String).Trim()
        Assert-Exit 'Read master revision'
        if ($revision -ne $master) { throw 'Publish only the exact merged revision of origin/master.' }
        & gh auth status
        Assert-Exit 'GitHub authentication'
        $releasesJson = & gh release list --repo $repository --limit 100 --json tagName,isDraft,isPrerelease
        Assert-Exit 'Read published versions'
        foreach ($release in ($releasesJson | ConvertFrom-Json)) {
            if ($release.isDraft -or $release.isPrerelease) { continue }
            $publishedVersion = $null
            if ([Version]::TryParse($release.tagName.TrimStart('v', 'V'), [ref]$publishedVersion) -and
                $publishedVersion -ge [Version]$version) { throw "Version $version is not newer than $($release.tagName)." }
        }
    }

    & $DotnetPath build MisaManager.sln -c Release
    Assert-Exit 'Build solution'
    & $DotnetPath test tests/MisaManager.Core.Tests -c Release --no-build
    Assert-Exit 'Unit tests'
    $previousUiTests = [Environment]::GetEnvironmentVariable('MISAMANAGER_UI_TESTS')
    try {
        $env:MISAMANAGER_UI_TESTS = '1'
        & $DotnetPath test tests/MisaManager.Integration.Tests -c Release --no-build
        Assert-Exit 'Fake MISA integration tests'
    } finally { [Environment]::SetEnvironmentVariable('MISAMANAGER_UI_TESTS', $previousUiTests) }

    $arguments = @{ Stage = $true; Installer = $true; Version = $version; DotnetPath = $DotnetPath }
    foreach ($name in @('CertificateThumbprint', 'CertificatePath', 'SignToolPath', 'TimestampUrl')) {
        $value = Get-Variable -Name $name -ValueOnly
        if ($value) { $arguments[$name] = $value }
    }
    & (Join-Path $PSScriptRoot 'release.ps1') @arguments | Tee-Object -Variable releaseOutput | Out-Host
    $manifestPath = [string]($releaseOutput | Select-Object -Last 1)
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.sourceRevision -ne $revision -or $manifest.sourceDirty -or
        [Version]$manifest.version -ne [Version]($version + '.0') -or $manifest.verification -ne 'build_verified') {
        throw 'Release manifest does not match the tested source revision and version.'
    }
    $releaseRoot = Split-Path -Parent $manifestPath
    $installer = Join-Path $releaseRoot $manifest.installer.path
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $manifest.installer.sha256) { throw 'Installer does not match its manifest hash.' }
    $distribution = Join-Path $releaseRoot 'dist'
    New-Item -ItemType Directory -Path $distribution | Out-Null
    $msi = Join-Path $distribution "MisaManager-$version.msi"
    $sums = Join-Path $distribution 'SHA256SUMS.txt'
    $distributionManifest = Join-Path $distribution 'release-manifest.json'
    Copy-Item -LiteralPath $installer -Destination $msi
    Copy-Item -LiteralPath $manifestPath -Destination $distributionManifest
    "$hash  MisaManager-$version.msi" | Set-Content -LiteralPath $sums -Encoding ascii
    if (-not $Publish) { Write-Output $distribution; return }

    # Keep the release hidden from the updater until all three assets have uploaded.
    & gh release create "v$version" $msi $sums $distributionManifest --repo $repository --draft --title "MisaManager $version" --generate-notes --target $revision
    Assert-Exit 'Upload draft release'
    & gh release edit "v$version" --repo $repository --draft=false --latest
    Assert-Exit 'Publish release'
} finally { Pop-Location }
