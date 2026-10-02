param([string]$ConfigPath = '', [switch]$RequireRunning)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $ConfigPath) { $ConfigPath = Join-Path $root '.codex\config.toml' }
$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8
if ($config -match '(?m)^\s*\[mcp_servers\.misa_incidents(?:\]|\.)') { throw 'Retired legacy connector is still configured.' }
$section = [regex]::Match($config, '(?ms)^\[mcp_servers\.misa_manager\]\s*\r?\n(?<body>.*?)(?=^\[|\z)')
if (-not $section.Success -or $section.Groups['body'].Value -notmatch '(?m)^enabled\s*=\s*true\s*$') { throw 'Native connector is not enabled.' }
$stable = Join-Path $env:LOCALAPPDATA 'Programs\MisaManager\bridge\MisaManager.McpBridge.exe'
if ($section.Groups['body'].Value -notmatch [regex]::Escape("command = '$stable'")) { throw 'Native connector does not use the stable installed bridge.' }
if (-not (Test-Path -LiteralPath $stable -PathType Leaf)) { throw 'Installed bridge is missing.' }
$processes = @(Get-CimInstance Win32_Process)
$legacy = @($processes | Where-Object { $_.Name -match '^python(w)?\.exe$' -and $_.CommandLine -match '(incident_mcp|[\\/]misa[\\/](app|launcher))\.py' })
if ($legacy.Count) { throw 'A retired legacy control process is still running.' }
$apps = @($processes | Where-Object { $_.Name -eq 'MisaManager.App.exe' })
if ($apps.Count -gt 1) { throw 'Multiple native app processes need review.' }
$mutex = $null
$held = $false
try {
    try { $mutex = [Threading.Mutex]::OpenExisting('Local\MisaManager.App') }
    catch [Threading.WaitHandleCannotBeOpenedException] { }
    if ($mutex) {
        try { $acquired = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if ($acquired) { $mutex.ReleaseMutex() } else { $held = $true }
    }
} finally { if ($mutex) { $mutex.Dispose() } }
if ($RequireRunning -and ($apps.Count -ne 1 -or -not $held)) { throw 'The native app does not hold its lifetime single-writer mutex.' }
$runtimeAppVersion = $null
if ($RequireRunning) {
    $runtimeJson = (& $stable --call runtime_status 2>$null | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($runtimeJson)) { throw 'Installed bridge runtime status returned no response.' }
    try { $runtime = $runtimeJson | ConvertFrom-Json } catch { throw 'Installed bridge runtime status was not valid JSON.' }
    if ($runtime.status -ne 'completed' -or -not $runtime.data.runtime.appVersion) {
        throw ('Installed bridge could not read the running app runtime: ' + [string]$runtime.message)
    }
    $runtimeAppVersion = [string]$runtime.data.runtime.appVersion
    $bridgeVersion = [string](Get-Item -LiteralPath $stable).VersionInfo.FileVersion
    if ($runtimeAppVersion -ne $bridgeVersion) {
        throw ('Bridge/app version mismatch: installed bridge ' + $bridgeVersion + ', running app ' + $runtimeAppVersion + '.')
    }
}
[ordered]@{
    status = $(if ($RequireRunning) { 'verified' } else { 'config_verified' }); owner = 'MisaManager.App'; mutex = 'Local\MisaManager.App';
    mutexHeld = $held; lifetimeMutexHeld = ($held -and $apps.Count -eq 1); appProcessIds = @($apps | ForEach-Object { $_.ProcessId });
    bridge = $stable; bridgeVersion = (Get-Item -LiteralPath $stable).VersionInfo.FileVersion; runtimeAppVersion = $runtimeAppVersion;
    legacyEnabled = $false; inputSent = $false
} | ConvertTo-Json
