# Pre-flight checks proving the MCP bridge/app payload runs without a machine-wide .NET runtime.
# Dot-source this file; it only defines functions.

$script:RuntimeFiles = @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll')
$script:RuntimeFixHint = 'Dùng bản release self-contained: chạy scripts\release.ps1 -Stage -Installer rồi scripts\install.ps1 (bản release luôn publish win-x64 self-contained). Tạm thời có thể cài .NET 10 Desktop Runtime x64.'

# Structural proof that a published folder carries its own runtime (framework-dependent output lacks these).
function Get-SelfContainedProblem([string]$Folder) {
    if (-not (Test-Path -LiteralPath $Folder -PathType Container)) { return "Không thấy thư mục: $Folder" }
    $missing = @($script:RuntimeFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Folder $_) -PathType Leaf) })
    if ($missing.Count -gt 0) { return ('Thiếu file runtime đi kèm (' + ($missing -join ', ') + ') trong ' + $Folder) }
    foreach ($config in Get-ChildItem -LiteralPath $Folder -Filter '*.runtimeconfig.json' -File) {
        $json = Get-Content -LiteralPath $config.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        $options = $json.runtimeOptions
        if ($null -ne $options -and $null -ne $options.PSObject.Properties['framework'] -and $null -eq $options.PSObject.Properties['includedFrameworks']) {
            return ('Cấu hình ' + $config.Name + ' phụ thuộc .NET cài sẵn (framework-dependent).')
        }
    }
    return $null
}

function Assert-SelfContainedPayload([string]$Folder, [string]$Name) {
    $problem = Get-SelfContainedProblem $Folder
    if ($problem) { throw ("$Name không phải bản self-contained: $problem. " + $script:RuntimeFixHint) }
}

function ConvertTo-HostExitCode([int]$ExitCode) { return [BitConverter]::ToUInt32([BitConverter]::GetBytes($ExitCode), 0) }

# Launches '<bridge> --health-check' and classifies failures; never touches MISA or the running app.
function Test-BridgeLaunch([string]$BridgeExe, [int]$TimeoutSeconds = 20) {
    $result = [ordered]@{ Ok = $false; Message = ''; ExitCode = $null }
    if (-not (Test-Path -LiteralPath $BridgeExe -PathType Leaf)) {
        $result.Message = "Không tìm thấy bridge: $BridgeExe. Cài lại bằng scripts\install.ps1."
        return [pscustomobject]$result
    }
    $start = [Diagnostics.ProcessStartInfo]::new($BridgeExe)
    $start.ArgumentList.Add('--health-check')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = $null
    try {
        try { $process = [Diagnostics.Process]::Start($start) }
        catch {
            $result.Message = "Không khởi chạy được bridge ($BridgeExe): $($_.Exception.Message). " + $script:RuntimeFixHint
            return [pscustomobject]$result
        }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            $result.Message = "Bridge không phản hồi sau $TimeoutSeconds giây khi chạy --health-check ($BridgeExe)."
            return [pscustomobject]$result
        }
        $process.WaitForExit()
        $result.ExitCode = $process.ExitCode
        $text = ($stdout.GetAwaiter().GetResult() + "`n" + $stderr.GetAwaiter().GetResult()).Trim()
        if ($process.ExitCode -eq 0) {
            try {
                $health = ($stdout.GetAwaiter().GetResult()) | ConvertFrom-Json
                if ($health.status -eq 'healthy') { $result.Ok = $true; $result.Message = 'Bridge chạy bình thường.'; return [pscustomobject]$result }
            } catch { }
            $result.Message = "Bridge chạy nhưng --health-check không trả về trạng thái healthy: $text"
            return [pscustomobject]$result
        }
        $code = ConvertTo-HostExitCode $process.ExitCode
        # 0x800080xx = .NET host failures (runtime/framework missing, bad config); 0xC0000135/0xC000007B = missing/bad DLL.
        $hostFailure = ($code -ge 0x80008080L -and $code -le 0x800080FFL) -or $code -eq 0xC0000135L -or $code -eq 0xC000007BL `
            -or $text -match '(?i)you must install|framework .* not found|\.NET (runtime|location)'
        if ($hostFailure) {
            $reason = Get-SelfContainedProblem (Split-Path -Parent $BridgeExe)
            $result.Message = ('Bridge không chạy được vì thiếu .NET runtime (mã 0x{0:X8}). ' -f $code) + $(if ($reason) { $reason + '. ' } else { '' }) + $script:RuntimeFixHint
        } else {
            $result.Message = ('Bridge thoát với mã {0}: {1}' -f $process.ExitCode, $text)
        }
        return [pscustomobject]$result
    } finally { if ($process) { $process.Dispose() } }
}

function Assert-BridgeLaunch([string]$BridgeExe) {
    $check = Test-BridgeLaunch $BridgeExe
    if (-not $check.Ok) { throw $check.Message }
}
