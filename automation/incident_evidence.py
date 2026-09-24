"""Durable evidence captured before inference/input, separate from optional timings."""
from contextlib import contextmanager
from copy import deepcopy
from datetime import datetime, timezone
import hashlib
import io
import json
from pathlib import Path
import shutil
import threading
import time
import traceback
import uuid

from source_updates import atomic_json
from .paths import state_dir, project_root
from .storage import (copy_event_tail, discard_run_evidence, link_generated_file, prune_generated_storage,
                      prune_run_evidence, register_run, unregister_run)

_LOCAL = threading.local()


def write_png(path, picture):
    """Hash the exact encoded bytes being written; never re-read a new PNG."""
    encoded = io.BytesIO()
    picture.save(encoded, format='PNG', compress_level=1)
    data = encoded.getvalue()
    checksum = hashlib.sha256(data).hexdigest()
    path.write_bytes(data)
    return checksum


def now():
    return datetime.now(timezone.utc).isoformat()


def current_evidence():
    return getattr(_LOCAL, 'journal', None)


def model_context(frame, input_id):
    journal = current_evidence()
    return dict(folder=str(journal.folder), run_id=journal.run_id,
                source_revision=journal.revision, parent_image_id=input_id, **frame) if journal else None


class EvidenceJournal:
    def __init__(self, run_id, revision):
        self.run_id, self.revision = run_id, revision
        register_run(run_id)
        try:
            prune_generated_storage()
            self.folder = state_dir() / 'run-evidence' / run_id
            (self.folder / 'images').mkdir(parents=True, exist_ok=True)
        except Exception:
            unregister_run(run_id)
            raise
        self.sequence = 0
        self.incident_path = None
        self.pending_incident = None
        self._closed = False

    @contextmanager
    def bind(self):
        previous = current_evidence()
        _LOCAL.journal = self
        try:
            yield self
        finally:
            _LOCAL.journal = previous

    def record(self, event, **details):
        if self._closed:
            return
        self.sequence += 1
        row = dict(details, event=event, at=now(), sequence=self.sequence, run_id=self.run_id,
                   source_revision=self.revision)
        with (self.folder / 'events.jsonl').open('a', encoding='utf-8') as stream:
            stream.write(json.dumps(row, ensure_ascii=False, default=str) + '\n')

    def image(self, picture, *, frame_id, role, **details):
        image_id = uuid.uuid4().hex
        path = self.folder / 'images' / (image_id + '.png')
        # The image is on disk before the model/input receives it. Failure blocks the action.
        checksum = write_png(path, picture)
        meta = dict(details, image_id=image_id, name=path.name, role=role,
                    frame_id=frame_id, recorded_at=now(), order=time.time_ns(),
                    run_id=self.run_id, source_revision=self.revision,
                    size=list(picture.size), mode=picture.mode,
                    sha256=checksum, is_current_screen=False)
        atomic_json(path.with_suffix('.json'), meta)
        prune_run_evidence(self.folder, protect=(path.name, path.with_suffix('.json').name))
        return image_id

    def incident(self, state, error, *, runtime=None, ledger=None, parent=None, phase='running', cancel=None):
        if self.incident_path:
            return self.incident_path
        if self.pending_incident is None:
            incident_id = uuid.uuid4().hex
            folder = state_dir() / 'sales-reports' / 'incidents' / incident_id
            folder.mkdir(parents=True, exist_ok=False)
            self.pending_incident = dict(id=incident_id, folder=str(folder), detected_at=now(),
                trace=''.join(traceback.format_exception(type(error), error, error.__traceback__)))
            screen, capture_error = self.failure_screen(folder, self.pending_incident['detected_at'])
            self.pending_incident.update(screen=screen, capture_error=capture_error)
        pending = self.pending_incident
        incident_id, folder, detected_at = pending['id'], Path(pending['folder']), pending['detected_at']
        screen_entry, screenshot_error = pending['screen'], pending['capture_error']
        self.record('incident_detected', error=f'{type(error).__name__}: {error}',
                    checkpoint=state.get('active_step'), last_action=state.get('last_action'))
        trace = pending['trace']
        (folder / 'traceback.txt').write_text(trace, encoding='utf-8')
        chain, seen = [], set()
        failure = error
        while failure is not None and id(failure) not in seen:
            seen.add(id(failure))
            details = dict(type=type(failure).__name__, message=str(failure))
            for key in ('details', 'diagnostic'):
                value = getattr(failure, key, None)
                if value is not None:
                    details[key] = value
            chain.append(details)
            failure = failure.__cause__ or failure.__context__
        atomic_json(folder / 'error-chain.json', json.loads(json.dumps(chain, default=str)))
        action = deepcopy(state.get('last_action', {}))
        manifest = []
        for path in (self.folder / 'images').glob('*.json'):
            meta = json.loads(path.read_text(encoding='utf-8'))
            manifest.append(meta)
        manifest.sort(key=lambda row: row['order'])
        action_frame = action.get('frame_id')
        latest_frame = next((row.get('frame_id') for row in reversed(manifest)
                             if row.get('role') in ('window_capture', 'window_input')), None)
        frames = {frame for frame in (action_frame, latest_frame) if frame}
        selected, selected_ids = [], set()
        priority_roles = ('window_capture', 'window_input')
        for frame in frames:
            for role in priority_roles:
                row = next((item for item in reversed(manifest)
                            if item.get('frame_id') == frame and item.get('role') == role), None)
                if row and row.get('image_id') not in selected_ids:
                    selected.append(row)
                    selected_ids.add(row.get('image_id'))
        for row in reversed(manifest):
            if len(selected) >= 8:
                break
            if row.get('image_id') not in selected_ids:
                selected.append(row)
                selected_ids.add(row.get('image_id'))
        selected.sort(key=lambda row: row['order'])
        # Incidents retain only the latest/action evidence and hard-link it when possible.
        images = [screen_entry] if screen_entry else []
        for meta in selected:
            if cancel is not None and cancel.is_set():
                from .domain import Cancelled
                raise Cancelled('Đã dừng thu thập sự cố; không gửi yêu cầu sửa mới.')
            source = self.folder / 'images' / meta['name']
            destination = folder / meta['name']
            link_generated_file(source, destination)
            images.append(meta)
        events = self.folder / 'events.jsonl'
        if events.exists():
            copy_event_tail(events, folder / 'events.jsonl')
        atomic_json(folder / 'vision-inputs.json', dict(schema=3, images=images,
            retention='failure screen plus latest/action native window evidence',
            coordinate_rule='screenshots are evidence only; native UIA3/Win32 identities are authoritative',
            historical=True))
        atomic_json(folder / 'checklist.json', state)
        atomic_json(folder / 'text-action.json', action)
        atomic_json(folder / 'tab-ledger.json', ledger or {})
        atomic_json(folder / 'runtime-evidence.json', runtime or {})
        root = project_root()
        if root and (root / 'APP_WORKFLOW.md').is_file():
            shutil.copyfile(root / 'APP_WORKFLOW.md', folder / 'app-workflow.md')
        incident = dict(schema=4, id=incident_id, run_id=self.run_id, created_at=detected_at,
            state='blocked', phase=phase, function=state.get('function', 'Thêm'),
            workflow=state.get('workflow', 'add_sale'),
            step=state.get('active_step'), next_step=state.get('next_step'),
            expected=state.get('expected', {}), observed=state.get('readbacks', {}),
            error=f'{type(error).__name__}: {error}', failure_condition=str(error),
            source_revision=self.revision, invoice_number=state.get('invoice_number'),
            invoice_source=state.get('invoice_source'), parent=parent,
            screenshot_error=screenshot_error, last_action=action,
            capture_status='captured' if screen_entry else 'unavailable',
            evidence_status='historical', files=sorted(p.name for p in folder.iterdir()))
        atomic_json(folder / 'incident.json', incident)
        self.incident_path = str(folder)
        self.discard()
        return str(folder)

    def failure_screen(self, folder, detected_at):
        picture = None
        try:
            from PIL import ImageGrab
            from . import win32
            picture = ImageGrab.grab(all_screens=True).convert('RGB')
            checksum = write_png(folder / 'misa-screen.png', picture)
            return dict(name='misa-screen.png', role='failure_desktop',
                frame_id=uuid.uuid4().hex, captured_at=detected_at,
                order=time.time_ns(), size=list(picture.size),
                desktop_origin=[win32.user32.GetSystemMetrics(76), win32.user32.GetSystemMetrics(77)],
                sha256=checksum,
                source_revision=self.revision, is_current_screen=False), ''
        except Exception as error:
            return None, f'{type(error).__name__}: {error}'
        finally:
            if picture is not None:
                picture.close()

    def finalize_incident(self, state):
        try:
            if self.incident_path:
                atomic_json(Path(self.incident_path) / 'checklist.json', state)
        finally:
            self.discard()

    def discard(self):
        if self._closed:
            return
        self._closed = True
        try:
            discard_run_evidence(self.folder)
        finally:
            unregister_run(self.run_id)


