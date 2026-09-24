param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$dotnetBinary = Join-Path $projectRoot 'vendor/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetBinary)) { $dotnetBinary = 'dotnet' }
Push-Location $projectRoot
try {
    & $dotnetBinary restore MisaAutomation.sln -r win-x64 --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $dotnetBinary build MisaAutomation.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($Publish) {
        $revision = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        $publications = @()
        foreach ($component in @(@('App', 'engine'), @('Inspector', 'inspector'))) {
            $projectName = $component[0]
            $componentRoot = Join-Path $projectRoot ('release/' + $component[1])
            $destination = Join-Path $componentRoot ('versions/' + $revision)
            & $dotnetBinary publish ('src/MisaAutomation.' + $projectName + '/MisaAutomation.' + $projectName + '.csproj') -c Release -r win-x64 --self-contained true --no-restore -o $destination
            if ($LASTEXITCODE -ne 0) { throw ('Publish failed: ' + $projectName) }
            Copy-Item -LiteralPath (Join-Path $projectRoot 'locators') -Destination $destination -Recurse
            $files = @{}
            $destinationPrefix = [IO.Path]::GetFullPath($destination).TrimEnd('\') + '\'
            foreach ($file in Get-ChildItem -LiteralPath $destination -File -Recurse) {
                if (-not $file.FullName.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Published file is outside the component directory.'
                }
                $relative = $file.FullName.Substring($destinationPrefix.Length).Replace('\', '/')
                $files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            $manifest = @{ protocol = 1; revision = $revision; verification = 'pending_live'; files = $files }
            [IO.File]::WriteAllText((Join-Path $destination 'engine-manifest.json'), ($manifest | ConvertTo-Json -Depth 5))
            $pointer = @{ protocol = 1; revision = $revision; executable = 'versions/' + $revision + '/MisaAutomation.' + $projectName + '.exe'; verification = 'pending_live' }
            $temporary = Join-Path $componentRoot ('current-' + $revision + '.tmp')
            [IO.File]::WriteAllText($temporary, ($pointer | ConvertTo-Json))
            $publications += @{ Temporary = $temporary; Current = (Join-Path $componentRoot 'current.json') }
            Write-Output ('Published ' + $projectName + ': ' + $destination)
        }
        # Keep both current versions unchanged if either component fails to build.
        foreach ($publication in $publications) {
            if ([IO.File]::Exists($publication.Current)) {
                # PowerShell coerces $null to an empty string; skip backups with a true null.
                [IO.File]::Replace($publication.Temporary, $publication.Current, [NullString]::Value)
            } else {
                [IO.File]::Move($publication.Temporary, $publication.Current)
            }
        }
    }
} finally { Pop-Location }
