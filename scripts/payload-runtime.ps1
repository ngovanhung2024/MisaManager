# Pre-flight checks proving a published payload runs without a machine-wide .NET runtime.
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
