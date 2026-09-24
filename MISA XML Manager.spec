# -*- mode: python ; coding: utf-8 -*-
from pathlib import Path
import sys
from PyInstaller.config import CONF
from PyInstaller.utils.hooks import collect_all

sys.path.insert(0, SPECPATH)
from source_updates import build_bundle

root = Path(SPECPATH)
if sys.version_info < (3, 10, 2):
    raise RuntimeError('Build needs Python 3.10.2 or newer.')
if sys.version_info[:2] != (3, 10):
    raise RuntimeError('Build requires CPython 3.10 x64 for the native Windows runtime.')

baseline = Path(CONF['workpath']) / 'baseline-source.zip'
baseline.write_bytes(build_bundle(root).raw)
fuzz_datas, fuzz_binaries, fuzz_hiddenimports = collect_all(
    'rapidfuzz', include_py_files=False,
    filter_submodules=lambda name: '__pyinstaller' not in name and 'test' not in name.split('.'),
    exclude_datas=['**/test*', '**/tests/**', '**/__pyinstaller/**'])

a = Analysis(
    ['launcher.py'], pathex=[],
    datas=[
        (str(baseline), '.'), ('requirements.txt', '.'),
        (str(Path(sys.base_prefix) / 'LICENSE.txt'), 'licenses/python'),
        ('THIRD_PARTY_NOTICES.md', '.'),
    ] + fuzz_datas,
    binaries=fuzz_binaries,
    hiddenimports=[
        'app', 'invoice_xml', 'automation.domain', 'automation.glass',
        'automation.error_log', 'automation.paths', 'automation.win32',
        'automation.screen_win32', 'automation.incident_evidence',
        'automation.performance', 'automation.verification',
        'automation.uia_bridge', 'automation.uia_readback',
        'automation.dashboard', 'automation.comparison',
        'automation.add_sale', 'automation.sale_items', 'automation.recovery',
        'automation.runtime_reload', 'automation.tab_recovery',
        'automation.inventory_screen', 'automation.inventory_menu',
        'automation.label_matching', 'automation.elements',
        'automation.observation_wait', 'pydantic', 'pydantic_core',
        'defusedxml', 'PIL',
    ] + fuzz_hiddenimports,
    hookspath=[], hooksconfig={}, runtime_hooks=[],
    excludes=[
        'pytest', 'rapidfuzz.__pyinstaller', 'onnxruntime', 'onnx',
        'tensorrt',
    ],
    noarchive=False, optimize=0)
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name='MISA XML Manager',
          debug=False, bootloader_ignore_signals=False, strip=False, upx=False,
          console=False, disable_windowed_traceback=False, runtime_tmpdir=None,
          uac_admin=False, contents_directory='_internal')
coll = COLLECT(exe, a.binaries, a.datas, strip=False, upx=False,
               name='MISA XML Manager')
