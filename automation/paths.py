"""Local paths shared by the source host, observer and error reports."""
import os
from pathlib import Path
import sys


def state_dir():
    return Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'MisaInvoiceTool'


def project_root():
    import source_updates

    configured = os.environ.get('MISA_SOURCE_ROOT')
    candidates = ([Path(configured).expanduser()] if configured else [])
    candidates += [Path(source_updates.__file__).resolve().parent,
                   Path(sys.executable).resolve().parent.parent, Path.cwd()]
    for candidate in candidates:
        root = candidate.resolve()
        if all((root / name).is_file() for name in
               ('app.py', 'source_updates.py', 'automation/__init__.py')):
            return root
    return None
