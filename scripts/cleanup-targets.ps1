# Project-only retirement catalog. User/runtime state is intentionally excluded.
function Get-RetiredProjectPaths {
    @(
        'automation', 'app.py', 'invoice_xml.py', 'launcher.py', 'source_updates.py',
        'publish_update.py', 'incident_mcp.py', 'requirements.txt', 'requirements-observer.txt',
        'MISA XML Manager.spec', 'MISA XML Manager - Source.lnk', 'MisaAutomation.sln',
        'build-engine.ps1', '.venv', 'vendor', 'models', 'training', 'locators',
        '__pycache__', 'build', 'dist', 'release', 'artifacts',
        'src/MisaAutomation.App', 'src/MisaAutomation.Core', 'src/MisaAutomation.Input',
        'src/MisaAutomation.Inspector', 'src/MisaAutomation.UIA',
        'installer/bin', 'installer/obj', 'installer/artifacts', 'out/bin', 'out/obj',
        'out/releases/20260925-164505-d14957c5',
        'out/releases/20260925-172602-dc18b6c3', 'out/releases/20260925-172830-97b7428f',
        'licenses/altgraph-LICENSE',
        'licenses/annotated-types-LICENSE',
        'licenses/cffi-LICENSE',
        'licenses/coloredlogs-LICENSE.txt',
        'licenses/CRAFT-LICENSE.txt',
        'licenses/defusedxml-LICENSE',
        'licenses/EasyOCR-LICENSE.txt',
        'licenses/humanfriendly-LICENSE.txt',
        'licenses/mpmath-LICENSE',
        'licenses/numpy-LICENSE',
        'licenses/numpy-LICENSE.md',
        'licenses/numpy-LICENSE.txt',
        'licenses/numpy-numpy__core_include_numpy_random_LICENSE.txt',
        'licenses/opencv-python-LICENSE-3RD-PARTY.txt',
        'licenses/opencv-python-LICENSE.txt',
        'licenses/PaddleOCR-LICENSE.txt',
        'licenses/PaddlePaddle-LICENSE.txt',
        'licenses/PaddleX-LICENSE.txt',
        'licenses/pefile-LICENSE',
        'licenses/pillow-LICENSE',
        'licenses/protobuf-LICENSE',
        'licenses/pyclipper-LICENSE',
        'licenses/pycparser-LICENSE',
        'licenses/pydantic-LICENSE',
        'licenses/pydantic_core-LICENSE',
        'licenses/pyinstaller-COPYING.txt',
        'licenses/pyinstaller-hooks-contrib-LICENSE',
        'licenses/pyreadline3-LICENSE.md',
        'licenses/PyTorch-LICENSE.txt',
        'licenses/pywin32-ctypes-LICENSE.txt',
        'licenses/pywin32-license.txt',
        'licenses/pywin32-MAPIStubLibrary-License.txt',
        'licenses/pywin32-NOTICE.md',
        'licenses/pywin32-pythonwin_License.txt',
        'licenses/pywin32-pythonwin_pywin_idle_LICENSE.txt',
        'licenses/pywin32-Scintilla-License.txt',
        'licenses/pywin32-win32com_License.txt',
        'licenses/PyYAML-LICENSE',
        'licenses/rapidfuzz-LICENSE',
        'licenses/setuptools-LICENSE',
        'licenses/shapely-LICENSE.txt',
        'licenses/shapely-LICENSE_GEOS',
        'licenses/shapely-LICENSE_win32',
        'licenses/six-LICENSE',
        'licenses/sympy-LICENSE',
        'licenses/sympy-LICENSE.txt',
        'licenses/tenacity-LICENSE',
        'licenses/torchvision-LICENSE.txt',
        'licenses/typing-inspection-LICENSE',
        'licenses/typing_extensions-LICENSE',
        'licenses/VietOCR-LICENSE.txt',
        'CdnData.txt', 'CheckUpdateFile.txt', 'UseSituationLog.txt'
    )
}

function Assert-ProjectCleanupPath([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Cleanup path escapes project: ' + $full)
    }
    $allowed = @(Get-RetiredProjectPaths | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $Root $_)) })
    if ($full -notin $allowed) { throw ('Path is not in the reviewed retirement catalog: ' + $full) }
    $cursor = $full
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw ('Reparse point blocked: ' + $cursor)
        }
        $next = Split-Path -Parent $cursor
        if ($next -eq $cursor) { break }
        $cursor = $next
    }
    return $full
}

function Get-CleanupFiles([string]$Path) {
    $stack = [Collections.Generic.Stack[string]]::new()
    $stack.Push($Path)
    while ($stack.Count) {
        $item = Get-Item -LiteralPath $stack.Pop() -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            [pscustomobject]@{ FullName = $item.FullName; Length = [long]0; Reparse = $true }
            continue
        }
        if ($item.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $item.FullName -Force) { $stack.Push($child.FullName) }
        } else { $item }
    }
}

function Write-CleanupJson([string]$Path, $Value) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) } else { [IO.File]::Move($temporary, $Path) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
