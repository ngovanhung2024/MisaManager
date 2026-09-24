"""Stable EXE. Application sources reload without starting another process."""
from pathlib import Path
import sys
from source_updates import Bundle, DirectSource, UpdateHost, runtime_requirements_hash


def main():
    import os
    os.environ['MISA_HEADLESS'] = '1' if '--headless' in sys.argv[1:] else '0'
    if getattr(sys, 'frozen', False):
        resources = Path(__file__).resolve().parent
        folder = Path(sys.executable).resolve().parent / 'updates'
        requirements_hash = runtime_requirements_hash((resources / 'requirements.txt').read_bytes())
        baseline = Bundle((resources / 'baseline-source.zip').read_bytes(), requirements_hash=requirements_hash)
    else:
        root = Path(__file__).resolve().parent
        folder = root / 'release' / 'updates'
        requirements_hash = runtime_requirements_hash((root / 'requirements.txt').read_bytes())
        baseline = DirectSource(root)
    return UpdateHost(folder, baseline, requirements_hash).run(
        sys.argv[1:], prefer_baseline=not getattr(sys, 'frozen', False))


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        from automation.glass import show_startup_error

        show_startup_error(error)
        sys.exit(1)
