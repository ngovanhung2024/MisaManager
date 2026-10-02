param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
        '-Configuration', $Configuration)
    if ($Restore) { $arguments += '-Restore' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments | Out-Null
    exit 0
}

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'MisaManager.sln'
$exe = Join-Path $root ("out\bin\MisaManager.App\{0}\net10.0-windows\MisaManager.App.exe" -f $Configuration)
$buildVersion = '0.1.0'
$runtimeRoot = Join-Path $env:LOCALAPPDATA 'MisaManager'
$activePointer = Join-Path $runtimeRoot 'generations\active.json'
if (Test-Path -LiteralPath $activePointer -PathType Leaf) {
    $active = Get-Content -LiteralPath $activePointer -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not [string]::IsNullOrWhiteSpace($active.appVersion)) { $buildVersion = [string]$active.appVersion }
}

Push-Location $root
try {
    # Stop the previous development build so Windows does not keep its files locked.
    $running = @(Get-Process -Name 'MisaManager.App' -ErrorAction SilentlyContinue)
    foreach ($process in $running) {
        if (-not $process.HasExited) { [void]$process.CloseMainWindow() }
    }
    if ($running.Count -gt 0) {
        $running | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
        $remaining = @($running | Where-Object { -not $_.HasExited })
        if ($remaining.Count -gt 0) {
            throw 'Could not close the running app within 5 seconds; current data was preserved.'
        }
    }

    $buildArgs = @('build', $solution, '-c', $Configuration, ('-p:Version=' + $buildVersion), '--nologo')
    if (-not $Restore) { $buildArgs += '--no-restore' }
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit code $LASTEXITCODE)." }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "App executable was not found after build: $exe" }

    # A successful development build starts with fresh runtime state. Move old
    # runtime directories out of the live path immediately; cleanup continues
    # in the background so this launcher can open the app without waiting.
    $runtimeRootFull = [IO.Path]::GetFullPath($runtimeRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $runtimeTargets = @('generations', 'logs', 'diagnostics', 'control', 'import-profiles')
    $cleanupRoot = Join-Path (Split-Path -Parent $runtimeRootFull) ("MisaManager-build-cleanup-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $cleanupRoot -Force | Out-Null
    foreach ($name in $runtimeTargets) {
        $target = [IO.Path]::GetFullPath((Join-Path $runtimeRootFull $name))
        if (-not $target.StartsWith($runtimeRootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a path outside the MisaManager runtime root: $target"
        }
        if (Test-Path -LiteralPath $target) {
            $item = Get-Item -LiteralPath $target -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to clean a reparse-point runtime directory: $target"
            }
            Move-Item -LiteralPath $target -Destination (Join-Path $cleanupRoot $name)
        }
    }
    $cleanupScript = Join-Path $PSScriptRoot 'cleanup-runtime.ps1'
    Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $cleanupScript, '-Path', $cleanupRoot
    ) | Out-Null
    Write-Host "Cleared previous MISA Manager runtime history and import state. Background cleanup started."

    $started = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
    Write-Host ("Built and started MISA Excel Manager ({0}). PID: {1}" -f $Configuration, $started.Id)
}
finally {
    Pop-Location
}
