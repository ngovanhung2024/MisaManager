"""Publish source atomically; no administrator prompt and no MISA operations."""
import argparse
from pathlib import Path
from source_updates import publish


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    bundle = publish(root, args.output or root / 'release' / 'updates')
    print('Source published: ' + bundle.revision)
