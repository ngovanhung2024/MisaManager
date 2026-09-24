"""Local exception reports for manual copying; no network or repair execution."""
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import platform
import re
import sys
import threading
import traceback
import uuid

from .paths import state_dir


MAX_ENTRIES = 50
_SECRETS = (
    (re.compile(r'(?i)(authorization\s*[:=]\s*bearer\s+)\S+'), r'\1<redacted>'),
    (re.compile(r'''(?i)(["']?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|password|secret)["']?\s*[:=]\s*["']?)[^\s"',;}]+'''), r'\1<redacted>'),
    (re.compile(r'\bsk-[A-Za-z0-9_-]{12,}\b'), '<redacted>'),
)


def redact(text):
    for pattern, replacement in _SECRETS:
        text = pattern.sub(replacement, str(text))
    return text


class ErrorJournal:
    def __init__(self, source_revision='', *, session=None):
        self.source_revision = source_revision
        self.session_id = uuid.uuid4().hex
        self.started_at = datetime.now(timezone.utc).isoformat()
        self.entries = []
        self.omitted = 0
        self.storage_error = ''
        self._lock = threading.RLock()
        # Only an in-process reload supplies a journal; XML input state does not.
        if isinstance(session, dict) and re.fullmatch(r'[a-f0-9]{32}', str(session.get('id', ''))):
            entries = session.get('entries', [])
            if isinstance(entries, list) and all(isinstance(item, str) for item in entries):
                self.session_id = session['id']
                self.started_at = str(session.get('started_at', self.started_at))
                self.entries = [redact(item) for item in entries[-MAX_ENTRIES:]]
                omitted = session.get('omitted', 0)
                self.omitted = max(0, omitted if type(omitted) is int else 0) + max(0, len(entries) - MAX_ENTRIES)
                self.storage_error = redact(str(session.get('storage_error', '')))
        self.path = state_dir() / 'error-logs' / ('errors-' + self.session_id + '.txt')

    def record(self, error, *, operation, context=None, tb=None, trace_text=None,
               error_type=None, occurred_at=None, source_revision=None):
        now = datetime.now(timezone.utc)
        trace_text = trace_text if trace_text is not None else ''.join(
            traceback.format_exception(type(error), error, tb if tb is not None else error.__traceback__))
        details = dict(context or {})
        for name in ('code', 'uncertain'):
            if hasattr(error, name):
                details['error_' + name] = getattr(error, name)
        result = getattr(error, 'result', None)
        if result is not None:
            details['verification'] = result.model_dump(mode='json') if hasattr(result, 'model_dump') else str(result)
        entry = '\n'.join([
            '=' * 72,
            'Error ID: ' + uuid.uuid4().hex,
            'Occurred at: ' + (occurred_at or now.isoformat()),
            'Recorded at (local): ' + now.astimezone().isoformat(),
            'Operation: ' + str(operation),
            'Exception: ' + (error_type or type(error).__name__) + ': ' + str(error),
            'Source revision: ' + (source_revision or self.source_revision or 'unavailable'),
            'Process/thread: ' + str(os.getpid()) + ' / ' + threading.current_thread().name,
            'Python: ' + sys.version.replace('\n', ' '),
            'Executable: ' + sys.executable,
            'Platform: ' + platform.platform(),
            'Working directory: ' + str(Path.cwd()),
            'Context recorded at failure:',
            json.dumps(details, ensure_ascii=False, indent=2, default=str),
            '', 'Full traceback:', trace_text or '(not supplied by the error source)', '',
        ])
        with self._lock:
            self.entries.append(redact(entry))
            if len(self.entries) > MAX_ENTRIES:
                self.omitted += len(self.entries) - MAX_ENTRIES
                self.entries = self.entries[-MAX_ENTRIES:]
            self.storage_error = ''
            try:
                self.path.parent.mkdir(parents=True, exist_ok=True)
                self.path.write_text(self.render(), encoding='utf-8', errors='backslashreplace')
            except OSError as failure:
                # Keep the full report in memory even when disk logging fails.
                self.storage_error = redact(str(failure))

    def render(self):
        with self._lock:
            header = [
                'MISA XML Manager - local error report',
                'Session: ' + self.session_id,
                'Session started at: ' + self.started_at,
                'Log file: ' + str(self.path),
                'Scope: errors from this application session, including manual source reloads.',
                'MISA observation: this logger does not capture or inspect the MISA screen.',
                'Invoice context is read from XML; it is not a MISA readback.',
            ]
            if self.omitted:
                header.append(f'Earlier errors omitted: {self.omitted}; showing the last {MAX_ENTRIES}.')
            if self.storage_error:
                header.append('Could not save the log: ' + self.storage_error + '; copy this report from the UI.')
            return '\n'.join(header) + '\n\n' + '\n'.join(self.entries)

    def snapshot(self):
        with self._lock:
            return {'id': self.session_id, 'started_at': self.started_at,
                    'entries': list(self.entries), 'omitted': self.omitted,
                    'storage_error': self.storage_error}
