"""File-backed MCP control plane for the running MISA manager.

Commands are consumed by the Tk/main thread so UIA and Win32 calls never run
from the MCP server process.  The protocol is intentionally small: one request
directory contains the command and its response.
"""
from __future__ import annotations

import json
import time
import uuid
from pathlib import Path

from source_updates import atomic_json
from .paths import state_dir


class ControlPlane:
    def __init__(self, root=None):
        self.root = Path(root or state_dir() / 'misa-control')
        self.requests = self.root / 'requests'
        self.requests.mkdir(parents=True, exist_ok=True)

    def register(self, session_id, *, pid=None, mode='actual'):
        # A new app process owns a new session. Old requests remain as
        # historical evidence and are rejected by the session check in poll().
        cutoff = time.time() - 3600
        for folder in self.requests.iterdir():
            if folder.is_dir() and (folder / 'response.json').is_file():
                try:
                    if folder.stat().st_mtime < cutoff:
                        for path in folder.iterdir():
                            path.unlink()
                        folder.rmdir()
                except OSError:
                    pass
        atomic_json(self.root / 'session.json', {
            'schema': 1, 'session_id': str(session_id), 'pid': pid,
            'mode': mode, 'updated_at': time.time(),
        })

    def issue(self, action, *, session_id=None, payload=None, timeout=30.0):
        request_id = uuid.uuid4().hex
        folder = self.requests / request_id
        folder.mkdir(parents=False, exist_ok=False)
        atomic_json(folder / 'request.json', {
            'schema': 1, 'request_id': request_id, 'session_id': session_id,
            'action': str(action), 'payload': payload or {},
            'issued_at': time.time(),
        })
        deadline = time.monotonic() + max(0.1, float(timeout))
        while time.monotonic() < deadline:
            path = folder / 'response.json'
            if path.is_file():
                try:
                    return json.loads(path.read_text(encoding='utf-8'))
                except (OSError, ValueError):
                    pass
            time.sleep(0.05)
        raise TimeoutError('MISA control command timed out: ' + str(action))

    def poll(self, owner):
        session_id = getattr(owner, 'control_session_id', None)
        for folder in sorted(self.requests.iterdir(), key=lambda item: item.name):
            if not folder.is_dir() or (folder / 'response.json').exists():
                continue
            request_path = folder / 'request.json'
            request = None
            try:
                request = json.loads(request_path.read_text(encoding='utf-8'))
                if request.get('session_id') != session_id:
                    raise PermissionError('MISA control request belongs to another app session.')
                try:
                    result = owner.dispatch_control(request)
                except Exception as first_error:
                    # An explicit retry is allowed after Windows reports an
                    # uncertain direct input. The second attempt is visible in
                    # the response and never happens implicitly.
                    payload = request.get('payload') or {}
                    uncertain = (type(first_error).__name__ == 'InputUncertain'
                                 or type(getattr(first_error, '__cause__', None)).__name__ == 'InputUncertain'
                                 or 'không xác nhận' in str(first_error).casefold())
                    if (payload.get('retry') and uncertain
                            and not request.get('_retried')):
                        retry_request = dict(request, _retried=True)
                        result = owner.dispatch_control(retry_request)
                    else:
                        raise
                response = dict(status='completed', request_id=request.get('request_id'),
                                action=request.get('action'), result=result, error=None)
            except Exception as error:
                response = dict(status='error', request_id=request.get('request_id') if isinstance(request, dict) else None,
                                 action=request.get('action') if isinstance(request, dict) else None,
                                 result=None, error=f'{type(error).__name__}: {error}')
            try:
                atomic_json(folder / 'response.json', response)
            except OSError:
                pass
