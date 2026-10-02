$ErrorActionPreference = 'Stop'

try {
    if (-not ('MisaManagerWindow' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MisaManagerWindow {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
}
'@
    }

    function Wait-ForManagerWindow([Diagnostics.Process]$Process, [int]$TimeoutSeconds = 120) {
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        do {
            $Process.Refresh()
            if ($Process.HasExited) { throw "MISA Excel Manager da thoat khi khoi dong (ma $($Process.ExitCode))." }
            if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
                [void][MisaManagerWindow]::ShowWindow($Process.MainWindowHandle, 9)
                [void][MisaManagerWindow]::SetForegroundWindow($Process.MainWindowHandle)
                return $true
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        throw 'MISA Excel Manager chay nhung khong tao cua so trong 120 giay.'
    }

    $releaseRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'out\releases'
    if (-not (Test-Path -LiteralPath $releaseRoot -PathType Container)) {
        throw "Chua co ban release da stage. Hay chay .\release.ps1 -Stage -Version 0.1.0 trong thu muc scripts, roi chay lai start-manager."
    }
    $candidates = foreach ($directory in Get-ChildItem -LiteralPath $releaseRoot -Directory) {
        $manifestPath = Join-Path $directory.FullName 'release-manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { continue }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($manifest.documentType -ne 'MisaManager.ReleaseManifest' -or
            $manifest.verification -ne 'build_verified' -or
            $manifest.revision -ne $directory.Name) { continue }
        $version = $null
        if (-not [version]::TryParse($manifest.version, [ref]$version)) { continue }
        [pscustomobject]@{ Directory = $directory.FullName; Manifest = $manifest; Version = $version; Revision = $directory.Name }
    }
    $latest = $candidates | Sort-Object Version, Revision -Descending | Select-Object -First 1
    if ($null -eq $latest) { throw 'Khong tim thay ban MISA Excel Manager da build.' }

    $executable = Join-Path $latest.Directory 'payload\app\MisaManager.App.exe'
    $expectedHash = $latest.Manifest.files.'app/MisaManager.App.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or
        [string]::IsNullOrWhiteSpace($expectedHash) -or
        (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $expectedHash) {
        throw "Ban $($latest.Version) khong khop hash release; khong mo app."
    }

    $running = Get-CimInstance Win32_Process -Filter "Name = 'MisaManager.App.exe'" |
        Select-Object -First 1
    if ($null -ne $running) {
        $process = Get-Process -Id $running.ProcessId
        Wait-ForManagerWindow $process | Out-Null
        if (-not [string]::IsNullOrWhiteSpace($running.ExecutablePath) -and
            -not [string]::Equals($running.ExecutablePath, $executable, [StringComparison]::OrdinalIgnoreCase)) {
            Add-Type -AssemblyName PresentationFramework
            [void][System.Windows.MessageBox]::Show(
                "MISA Excel Manager dang chay tu ban khac. Dong app dang mo, roi nhap dup shortcut de mo ban $($latest.Version).",
                'MISA Excel Manager')
        }
        return
    }

    $started = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable -Parent) -PassThru
    Wait-ForManagerWindow $started | Out-Null
}
catch {
    Add-Type -AssemblyName PresentationFramework
    [void][System.Windows.MessageBox]::Show($_.Exception.Message, 'MISA Excel Manager')
    exit 1
}
