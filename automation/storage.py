"""Bounded storage for generated native evidence and diagnostic history."""
import json
import os
from pathlib import Path
import shutil
import threading
import time

from .paths import state_dir


RUN_EVIDENCE_BYTES = 16 * 1024 * 1024
RUN_EVIDENCE_ITEMS = 16
INCIDENT_LIMIT = 3
PERFORMANCE_RUN_LIMIT = 3
ENGINE_LOG_LIMIT = 10
ERROR_LOG_LIMIT = 3
REPAIR_REQUEST_LIMIT = 1
EVENT_LOG_BYTES = 256 * 1024

_LOCK = threading.RLock()
_LAST_MAINTENANCE = 0.0
_ACTIVE_RUNS = set()


def _inside(path, root):
    path, root = os.path.normcase(os.path.abspath(os.fspath(path))), os.path.normcase(os.path.abspath(os.fspath(root)))
    try:
        return path != root and os.path.commonpath((path, root)) == root
    except (OSError, ValueError):
        return False


def _remove_tree(path, root):
    path, root = Path(path), Path(root)
    if path.is_symlink() or path == root or not _inside(path, root):
        raise ValueError('Refusing to remove an unsafe generated-data path.')
    shutil.rmtree(path, ignore_errors=True)


def _remove_file(path, root):
    path, root = Path(path), Path(root)
    if path.is_symlink() or not _inside(path, root):
        raise ValueError('Refusing to remove an unsafe generated-data file.')
    try:
        path.unlink()
    except FileNotFoundError:
        pass


def _newest(paths):
    def modified(path):
        try:
            return path.stat().st_mtime_ns
        except OSError:
            return -1
    return sorted(paths, key=modified, reverse=True)


def _recovery_protection(root):
    run_ids, incidents = set(), set()
    try:
        data = json.loads((root / 'add-recovery.json').read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return run_ids, incidents
    for value in (data.get('session_id'), (data.get('workflow') or {}).get('run_id')):
        if isinstance(value, str) and len(value) == 32:
            run_ids.add(value)
    for value in (data.get('incident'), (data.get('parent_incident') or {}).get('path')):
        if isinstance(value, str) and value:
            incidents.add(Path(value).name)
    return run_ids, incidents


def _prune_children(folder, *, keep_names=(), keep_count=0):
    if not folder.is_dir():
        return
    keep = set(keep_names)
    children = _newest([path for path in folder.iterdir() if path.is_dir() and not path.is_symlink()])
    keep.update(path.name for path in children[:keep_count])
    for path in children:
        if path.name not in keep:
            _remove_tree(path, folder)


def _prune_files(folder, keep_count):
    if not folder.is_dir():
        return
    files = _newest([path for path in folder.iterdir() if path.is_file() and not path.is_symlink()])
    for path in files[keep_count:]:
        _remove_file(path, folder)


def copy_event_tail(source, destination):
    source, destination = Path(source), Path(destination)
    rows, size = [], 0
    for row in reversed(source.read_bytes().splitlines()):
        if len(row) + 1 > EVENT_LOG_BYTES:
            continue
        if rows and size + len(row) + 1 > EVENT_LOG_BYTES:
            break
        rows.append(row)
        size += len(row) + 1
    destination.write_bytes(b'\n'.join(reversed(rows)) + (b'\n' if rows else b''))


def prune_repair_requests(incident):
    requests = Path(incident) / 'repair-requests'
    _prune_children(requests, keep_count=REPAIR_REQUEST_LIMIT)


def prune_incident_evidence(incident):
    """Compact legacy incidents to native screen evidence near the failure."""
    incident = Path(incident)
    manifest_path = incident / 'vision-inputs.json'
    try:
        manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
        record = json.loads((incident / 'incident.json').read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return
    images = [row for row in manifest.get('images', []) if isinstance(row, dict)]
    action_frame = (record.get('last_action') or {}).get('frame_id')
    latest_frame = next((row.get('frame_id') for row in reversed(images)
                         if row.get('role') in ('window_capture', 'window_input')), None)
    frames = {frame for frame in (action_frame, latest_frame) if frame}
    selected = [row for row in images
                if row.get('role') == 'failure_desktop'
                or (row.get('frame_id') in frames
                    and row.get('role') in ('window_capture', 'window_input'))]
    selected_ids = {row.get('image_id') for row in selected}
    for row in reversed(images):
        if len(selected) >= 8:
            break
        if row.get('image_id') not in selected_ids:
            selected.append(row)
            selected_ids.add(row.get('image_id'))
    selected.sort(key=lambda row: row.get('order', 0))
    keep = {'misa-screen.png', 'text-action-screen.png'}
    keep.update(row.get('name') for row in selected if isinstance(row.get('name'), str))
    keep_stems = {Path(name).stem for name in keep}
    for path in incident.iterdir():
        if not path.is_file() or path.is_symlink():
            continue
        legacy_meta = (path.suffix.lower() == '.json' and len(path.stem) == 32
                       and all(character in '0123456789abcdef' for character in path.stem)
                       and path.stem not in keep_stems)
        if (legacy_meta or path.suffix.lower() == '.npy'
                or (path.suffix.lower() in ('.png', '.jpg', '.jpeg', '.bmp')
                    and path.name not in keep and not path.name.startswith('develop-live-'))):
            _remove_file(path, incident)
    for row in selected:
        row.pop('tensor_file', None)
        row.pop('tensor_sha256', None)
    manifest.update(schema=3, images=selected,
                    retention='compacted native screen evidence')
    from source_updates import atomic_json
    atomic_json(manifest_path, manifest)
    events = incident / 'events.jsonl'
    try:
        if events.stat().st_size > 256 * 1024:
            copy_event_tail(events, events)
    except OSError:
        pass


def prune_generated_storage(*, force=False):
    """Keep only active and recent generated diagnostics."""
    global _LAST_MAINTENANCE
    with _LOCK:
        if not force and time.monotonic() - _LAST_MAINTENANCE < 60:
            return
        root = state_dir().resolve()
        root.mkdir(parents=True, exist_ok=True)
        run_ids, incidents = _recovery_protection(root)
        run_ids.update(_ACTIVE_RUNS)
        for run_id in run_ids:
            try:
                prune_run_evidence(root / 'run-evidence' / run_id)
            except (OSError, ValueError):
                pass
        operations = (
            lambda: _prune_children(root / 'run-evidence', keep_names=run_ids),
            lambda: _prune_children(root / 'sales-reports' / 'incidents',
                                    keep_names=incidents, keep_count=INCIDENT_LIMIT),
            lambda: _prune_children(root / 'sales-reports' / 'performance', keep_count=1),
            lambda: _prune_children(root / 'performance-logs', keep_count=PERFORMANCE_RUN_LIMIT),
            lambda: _prune_files(root / 'engine-logs', ENGINE_LOG_LIMIT),
            lambda: _prune_files(root / 'error-logs', ERROR_LOG_LIMIT),
            lambda: _prune_files(root / 'repair-observers', 2),
        )
        for operation in operations:
            try:
                operation()
            except (OSError, ValueError):
                pass
        incident_root = root / 'sales-reports' / 'incidents'
        try:
            incident_folders = list(incident_root.iterdir()) if incident_root.is_dir() else ()
        except OSError:
            incident_folders = ()
        for incident in incident_folders:
            if incident.is_dir() and not incident.is_symlink():
                try:
                    prune_repair_requests(incident)
                    prune_incident_evidence(incident)
                except (OSError, ValueError):
                    pass
        _LAST_MAINTENANCE = time.monotonic()


def register_run(run_id):
    with _LOCK:
        _ACTIVE_RUNS.add(str(run_id))


def unregister_run(run_id):
    with _LOCK:
        _ACTIVE_RUNS.discard(str(run_id))


def prune_run_evidence(folder, *, protect=()):
    """Bound one active run while retaining complete PNG/JSON evidence pairs."""
    folder = Path(folder).resolve()
    root = (state_dir() / 'run-evidence').resolve()
    if not _inside(folder, root):
        raise ValueError('Invalid run evidence directory.')
    images = folder / 'images'
    if not images.is_dir():
        return
    protected = {Path(name).stem for name in protect}
    groups = {}
    for path in images.iterdir():
        if not path.is_file() or path.is_symlink():
            continue
        if path.suffix.lower() == '.npy':
            _remove_file(path, images)
            continue
        groups.setdefault(path.stem, []).append(path)
    rows = []
    for stem, paths in groups.items():
        size = sum(path.stat().st_size for path in paths)
        modified = max(path.stat().st_mtime_ns for path in paths)
        rows.append((modified, stem, size, paths))
    rows.sort(reverse=True)
    total = sum(row[2] for row in rows)
    for index, (_, stem, size, paths) in enumerate(rows):
        if stem in protected:
            continue
        if index < RUN_EVIDENCE_ITEMS and total <= RUN_EVIDENCE_BYTES:
            continue
        for path in paths:
            _remove_file(path, images)
        total -= size


def link_generated_file(source, destination):
    """Create a stable incident reference without duplicating file contents."""
    source, destination = Path(source), Path(destination)
    if destination.exists():
        return
    try:
        os.link(source, destination)
    except OSError:
        shutil.copyfile(source, destination)


def discard_run_evidence(folder):
    root = (state_dir() / 'run-evidence').resolve()
    folder = Path(folder).resolve()
    if folder.exists():
        _remove_tree(folder, root)
