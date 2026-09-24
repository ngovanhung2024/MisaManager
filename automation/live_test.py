"""Local control plane for an explicit Codex-assisted MISA test session.

The session is intentionally file-backed: the GUI remains the only process that
owns MISA input, while a Codex CLI process can request start/stop/reload and
observe the resulting state through the incident MCP server.
"""
from copy import deepcopy
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import hmac
import json
import os
from pathlib import Path
import secrets
import signal
import subprocess
import sys
import threading
import time
import uuid
import re

from source_updates import atomic_json
from .paths import state_dir


SESSION_SCHEMA = 1
LIVE_MODE = 'codex_test'
COMMANDS = frozenset(('start', 'stop', 'handoff'))


def now():
    return datetime.now(timezone.utc).isoformat()


def _token_hash(token):
    return hashlib.sha256(str(token).encode('utf-8')).hexdigest()


def _command_auth(control_hash, command):
    body = {key: command[key] for key in
            ('schema', 'session_id', 'sequence', 'action', 'payload', 'issued_at')
            if key in command}
    if 'supersedes_sequence' in command:
        body['supersedes_sequence'] = command['supersedes_sequence']
    raw = json.dumps(body, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode('utf-8')
    return hmac.new(str(control_hash).encode('ascii'), raw, hashlib.sha256).hexdigest()


def _session_root():
    folder = state_dir() / 'live-test'
    folder.mkdir(parents=True, exist_ok=True)
    return folder


def session_path():
    return _session_root() / 'session.json'


def command_path():
    return _session_root() / 'command.json'


def control_path():
    """Authorization lives apart from the app-published session state."""
    return _session_root() / 'control.json'


@contextmanager
def _control_mutex():
    """Serialize claims/commands across the app and separate MCP processes."""
    path = _session_root() / 'control.lock'
    handle = os.fdopen(os.open(str(path), os.O_CREAT | os.O_RDWR), 'r+b')
    locked = False
    try:
        if handle.seek(0, os.SEEK_END) == 0:
            handle.seek(0)
            handle.write(b'0')
            handle.flush()
        handle.seek(0)
        if os.name == 'nt':
            import msvcrt
            deadline = time.monotonic() + 10
            while True:
                try:
                    msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
                    locked = True
                    break
                except OSError:
                    if time.monotonic() >= deadline:
                        raise RuntimeError('Timed out waiting for the live-test control lock.')
                    time.sleep(0.02)
        else:
            import fcntl
            fcntl.flock(handle.fileno(), fcntl.LOCK_EX)
            locked = True
        yield
    finally:
        try:
            if locked:
                handle.seek(0)
                if os.name == 'nt':
                    import msvcrt
                    msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(handle.fileno(), fcntl.LOCK_UN)
        finally:
            handle.close()


def _process_birth(pid):
    try:
        from .win32 import process_birth
        return process_birth(pid)
    except Exception:
        return None


def _process_executable(pid):
    """Return the native image path used for process identity checks."""
    try:
        from .win32 import process_path
        value = process_path(pid)
        return os.path.normcase(os.path.abspath(value)) if value else None
    except Exception:
        return None


def process_identity(pid=None, executable=None):
    pid = os.getpid() if pid is None else pid
    configured = os.path.abspath(executable or os.sys.executable)
    return {
        'manager_pid': pid,
        'manager_executable': _process_executable(pid) or configured,
        'manager_birth': _process_birth(pid),
    }


def _default_state(owner, snapshot=None):
    snapshot = snapshot if isinstance(snapshot, dict) else {}
    pid = os.getpid()
    session_id = snapshot.get('session_id')
    if not isinstance(session_id, str) or not session_id:
        session_id = uuid.uuid4().hex
    return {
        'schema': SESSION_SCHEMA,
        'session_id': session_id,
        'mode': LIVE_MODE,
        **process_identity(pid),
        'source_revision': getattr(owner, 'source_revision', ''),
        'selected_path': getattr(owner, 'selected_path', None),
        'state': snapshot.get('state', 'ready'),
        'phase': snapshot.get('phase', 'ready'),
        'incident_id': snapshot.get('incident_id'),
        'incident_path': snapshot.get('incident_path'),
        'error': snapshot.get('error', ''),
        'message': snapshot.get('message', 'Codex test session ready.'),
        'event_seq': int(snapshot.get('event_seq', 0) or 0),
        'command_seq': int(snapshot.get('command_seq', 0) or 0),
        'handled_command_seq': int(snapshot.get('handled_command_seq', 0) or 0),
        'updated_at': now(),
    }


def _read_json(path):
    try:
        value = json.loads(Path(path).read_text(encoding='utf-8'))
    except FileNotFoundError:
        return None
    except (OSError, ValueError):
        return None
    return value if isinstance(value, dict) else None


def _ensure_control(session_id, *, legacy=None):
    """Create or migrate the private control record for one session."""
    if not isinstance(session_id, str) or not session_id:
        raise RuntimeError('Live-test session ID is missing.')
    current = _read_json(control_path())
    if (isinstance(current, dict) and current.get('schema') == SESSION_SCHEMA
            and current.get('session_id') == session_id
            and isinstance(current.get('control_hash'), str)
            and re.fullmatch(r'[a-f0-9]{64}', current['control_hash'])):
        return current
    legacy = legacy if isinstance(legacy, dict) else {}
    control_hash = legacy.get('control_hash')
    if (not isinstance(control_hash, str)
            or not re.fullmatch(r'[a-f0-9]{64}', control_hash)):
        # The plaintext token is returned only by the MCP claim operation.
        control_hash = _token_hash(secrets.token_hex(32))
    control = {
        'schema': SESSION_SCHEMA,
        'session_id': session_id,
        'control_hash': control_hash,
        'claimed_at': legacy.get('claimed_at'),
        'claimed_thread': legacy.get('claimed_thread'),
        'command_seq': int(legacy.get('command_seq', 0) or 0),
        'closed': False,
        'updated_at': now(),
    }
    atomic_json(control_path(), control)
    return control


def _write_control(value):
    value = deepcopy(value)
    value['updated_at'] = now()
    atomic_json(control_path(), value)


def _public_control(control):
    return {key: control.get(key) for key in
            ('claimed_at', 'claimed_thread', 'command_seq')}


class LiveTestSession:
    """Own the app side of a single Codex test session."""

    def __init__(self, owner, snapshot=None):
        self.owner = owner
        self.lock = threading.RLock()
        self.data = _default_state(owner, snapshot)
        self.control = _ensure_control(self.data['session_id'], legacy=snapshot)
        self._closed = False
        self._write()

    @property
    def active(self):
        return not self._closed and self.data.get('state') != 'closed'

    @property
    def session_id(self):
        return self.data['session_id']

    def snapshot(self):
        with self.lock:
            return deepcopy(self.data)

    def _write(self, *, event=False):
        with self.lock:
            # Never write authorization fields from an old in-memory snapshot.
            control = _read_json(control_path()) or self.control
            if control.get('session_id') == self.session_id:
                self.control = control
                self.data.update(_public_control(control))
            self.data['updated_at'] = now()
            if event:
                self.data['event_seq'] = int(self.data.get('event_seq', 0) or 0) + 1
            atomic_json(session_path(), self.data)

    def publish(self, *, state=None, phase=None, incident=None, message=None,
                error=None, **details):
        if self._closed:
            return
        with self.lock:
            if state is not None:
                self.data['state'] = state
            if phase is not None:
                self.data['phase'] = phase
            if incident is not None:
                self.data['incident_path'] = str(incident)
                self.data['incident_id'] = Path(incident).name
            if message is not None:
                self.data['message'] = str(message)
            if error is not None:
                self.data['error'] = str(error)
            for key, value in details.items():
                if key not in ('control_hash', 'token', 'claimed_at', 'claimed_thread'):
                    self.data[key] = deepcopy(value)
            self.data['event_seq'] = int(self.data.get('event_seq', 0) or 0) + 1
            self._write()

    def selected(self, path):
        if self._closed:
            return
        with self.lock:
            self.data['selected_path'] = str(path) if path else None
            self.data['source_revision'] = getattr(self.owner, 'source_revision', '')
            self._write()

    def begin_workflow(self, path=None):
        """Clear the previous incident before a newly authenticated start."""
        if self._closed:
            return
        with self.lock:
            recovery = getattr(self.owner, 'recovery', None)
            recovery_data = recovery.snapshot() if recovery is not None else {}
            self.data.update(state='running',
                             phase=recovery_data.get('phase', self.data.get('phase', 'starting')),
                             selected_path=str(path) if path else self.data.get('selected_path'),
                             incident_id=None, incident_path=None, error='',
                             command_error=False,
                             recovery={key: recovery_data.get(key) for key in
                                       ('session_id', 'mode', 'phase', 'active', 'attempt',
                                        'source_revision', 'incident', 'parent_incident')}
                             if recovery_data else None,
                             last_command_payload=None)
            self.data['event_seq'] = int(self.data.get('event_seq', 0) or 0) + 1
            self._write()

    def _read_command(self):
        path = command_path()
        try:
            command = json.loads(path.read_text(encoding='utf-8'))
        except FileNotFoundError:
            return None
        except (OSError, ValueError) as error:
            self.publish(error=str(error), command_error=True)
            return None
        if not isinstance(command, dict) or command.get('session_id') != self.session_id:
            return None
        sequence = command.get('sequence')
        if type(sequence) is not int or sequence <= int(self.data.get('handled_command_seq', 0) or 0):
            return None
        control = _read_json(control_path())
        if (not isinstance(control, dict) or control.get('schema') != SESSION_SCHEMA
                or control.get('session_id') != self.session_id
                or sequence != int(control.get('command_seq', 0) or 0)):
            return None
        if not isinstance(control.get('control_hash'), str):
            return None
        if (not isinstance(command.get('auth'), str)
                or not hmac.compare_digest(command['auth'],
                                           _command_auth(control.get('control_hash'), command))):
            self.publish(error='Invalid live-test command authentication.', command_error=True)
            self.data['handled_command_seq'] = sequence
            self._write()
            return None
        if command.get('action') not in COMMANDS:
            self.publish(error='Unknown live-test command.', command_error=True)
            self.data['handled_command_seq'] = sequence
            self._write()
            return None
        return command

    def poll(self):
        """Consume at most one command on the Tk/main thread."""
        if self._closed:
            return
        command = self._read_command()
        if command is None:
            return
        sequence = command['sequence']
        action = command['action']
        payload = command.get('payload') or {}
        try:
            if action == 'start':
                if (getattr(self.owner, 'adding', False)
                        or bool(getattr(getattr(self.owner, 'recovery', None), 'active', False))
                        or self.data.get('phase') not in ('ready', 'stopped', 'complete', 'manual_attention')):
                    raise RuntimeError('Codex test session is not ready for a new workflow.')
                self.owner.start_add(from_live_test=True)
                if (not getattr(self.owner, 'adding', False)
                        and not getattr(getattr(self.owner, 'recovery', None), 'active', False)):
                    raise RuntimeError('Không thể bắt đầu workflow Codex test với lựa chọn hiện tại.')
                self.begin_workflow(getattr(self.owner, 'selected_path', None))
            elif action == 'stop':
                self.owner.stop_add(from_live_test=True)
            elif action == 'handoff':
                recovery = getattr(self.owner, 'recovery', None)
                if recovery is None:
                    raise RuntimeError('Recovery controller is not ready.')
                recovery.accept_external_handoff(payload)
        except Exception as error:
            if action == 'handoff':
                # Keep waiting_for_codex visible after a rejected handoff.
                self.publish(error=str(error), command_error=True, last_command=action)
            elif action == 'start':
                self.publish(state='ready', phase='ready', error=str(error),
                             command_error=True, last_command=action)
            else:
                self.publish(state='blocked', phase='command_error', error=str(error),
                             last_command=action)
        finally:
            self._mark_handled(sequence, action, payload)

    def _mark_handled(self, sequence, action, payload):
        """Record command consumption without clobbering recovery's newer phase."""
        with self.lock:
            latest = _read_json(session_path())
            if isinstance(latest, dict) and latest.get('session_id') == self.session_id:
                self.data.update(latest)
            self.data['handled_command_seq'] = sequence
            self.data['last_command'] = action
            self.data['last_command_payload'] = deepcopy(payload)
            self._write(event=True)

    def close(self, *, preserve=False):
        if self._closed and not preserve:
            return
        if preserve:
            self.publish(state='reloading', phase='reloading', message='Giữ phiên Codex qua hot reload.')
            return
        with _control_mutex():
            self._closed = True
            with self.lock:
                self.data['state'] = 'closed'
                self.data['phase'] = 'closed'
                self.data['message'] = 'Codex test session closed.'
                control = _read_json(control_path()) or self.control
                control.update(closed=True, claimed_at=None, claimed_thread=None)
                _write_control(control)
                self._write()


def claim_session(thread_id=None, expected_session_id=None):
    """Claim a ready session and return a one-time control token."""
    with _control_mutex():
        path = session_path()
        try:
            data = json.loads(path.read_text(encoding='utf-8'))
        except FileNotFoundError:
            raise RuntimeError('No MISA Codex test session is running.') from None
        except (OSError, ValueError) as error:
            raise RuntimeError('Cannot read the MISA Codex test session: ' + str(error)) from None
        if (not isinstance(data, dict) or data.get('schema') != SESSION_SCHEMA
                or data.get('mode') != LIVE_MODE or data.get('state') == 'closed'):
            raise RuntimeError('MISA is not running in Codex test mode.')
        if expected_session_id is not None and data.get('session_id') != expected_session_id:
            raise ValueError('The MISA Codex test session has changed; do not claim another run.')
        control = _ensure_control(data.get('session_id'), legacy=data)
        if control.get('closed'):
            raise RuntimeError('The MISA Codex test session is closed.')
        if not _manager_alive(data):
            raise RuntimeError('The recorded MISA test process is no longer alive.')
        requested_thread = thread_id or os.environ.get('CODEX_THREAD_ID') or f'mcp:{os.getpid()}'
        if control.get('claimed_at'):
            owner = control.get('claimed_thread') or 'another Codex thread'
            raise RuntimeError('This MISA test session is already claimed by ' + str(owner) + '.')
        token = secrets.token_urlsafe(32)
        control.update(control_hash=_token_hash(token), claimed_at=now(),
                       claimed_thread=requested_thread, closed=False)
        _write_control(control)
        return {
            'session_id': data['session_id'],
            'control_token': token,
            'mode': data['mode'],
            'state': data.get('state'),
            'phase': data.get('phase'),
            'source_revision': data.get('source_revision'),
            'selected_path': data.get('selected_path'),
            'manager_pid': data.get('manager_pid'),
            'event_seq': data.get('event_seq'),
            'claimed_thread': requested_thread,
        }


def read_session(expected_id=None):
    try:
        data = json.loads(session_path().read_text(encoding='utf-8'))
    except FileNotFoundError:
        raise RuntimeError('No MISA Codex test session is running.') from None
    except (OSError, ValueError) as error:
        raise RuntimeError('Cannot read the MISA Codex test session: ' + str(error)) from None
    if (not isinstance(data, dict) or data.get('schema') != SESSION_SCHEMA
            or not isinstance(data.get('session_id'), str) or not data.get('session_id')):
        raise RuntimeError('MISA Codex test session metadata is invalid.')
    if expected_id and data.get('session_id') != expected_id:
        raise ValueError('Live-test session ID does not match.')
    control = _read_json(control_path())
    if isinstance(control, dict) and control.get('session_id') == data.get('session_id'):
        data.update(_public_control(control))
        if control.get('closed'):
            data['state'] = 'closed'
    return data


def verify_token(data, token):
    session_id = data.get('session_id') if isinstance(data, dict) else None
    control = (_read_json(control_path()) or {}) if session_id else {}
    current_thread = os.environ.get('CODEX_THREAD_ID')
    thread_ok = (not current_thread or not control.get('claimed_thread')
                 or current_thread == control.get('claimed_thread'))
    if (not isinstance(data, dict) or data.get('mode') != LIVE_MODE
            or not control.get('claimed_at') or control.get('session_id') != session_id
            or control.get('closed') or not thread_ok
            or not isinstance(token, str) or not token \
            or _token_hash(token) != control.get('control_hash')):
        raise PermissionError('Invalid MISA Codex test control token.')


def issue_command(session_id, token, action, payload=None):
    """Issue one authenticated command from the external Codex process."""
    with _control_mutex():
        if action not in COMMANDS:
            raise ValueError('Unsupported live-test command.')
        data = read_session(session_id)
        verify_token(data, token)
        if not _manager_alive(data):
            raise RuntimeError('The MISA Codex test process is no longer alive.')
        pending = _read_json(command_path())
        handled = int(data.get('handled_command_seq', 0) or 0)
        pending_current = (isinstance(pending, dict) and pending.get('session_id') == session_id
                           and type(pending.get('sequence')) is int
                           and pending['sequence'] > handled)
        if pending_current and action != 'stop':
            raise RuntimeError('A live-test command is already waiting for the manager.')
        control = _read_json(control_path())
        if not isinstance(control, dict) or control.get('session_id') != session_id:
            raise RuntimeError('Live-test control metadata is unavailable.')
        sequence = max(int(control.get('command_seq', 0) or 0), handled) + 1
        command = {
            'schema': SESSION_SCHEMA,
            'session_id': session_id,
            'sequence': sequence,
            'action': action,
            'payload': deepcopy(payload or {}),
            'issued_at': now(),
        }
        if pending_current:
            command['supersedes_sequence'] = pending['sequence']
        command['auth'] = _command_auth(control.get('control_hash'), command)
        control['command_seq'] = sequence
        _write_control(control)
        atomic_json(command_path(), command)
        return {'status': 'accepted', 'session_id': session_id,
                'sequence': sequence, 'action': action}


def launch_live_test_process(xml_paths=None):
    """Launch one source manager process; the caller must claim it afterwards."""
    import subprocess

    try:
        current = read_session()
    except RuntimeError:
        current = None
    if current is not None and current.get('mode') == LIVE_MODE and _manager_alive(current):
        raise RuntimeError('A MISA manager is already running in Codex test mode.')
    from .paths import project_root
    root = project_root() or Path(__file__).resolve().parent.parent
    candidates = [root / '.venv' / 'Scripts' / 'pythonw.exe',
                  Path(sys.executable).with_name('pythonw.exe')]
    pythonw = next((path for path in candidates if path.is_file()), None)
    if pythonw is None:
        raise RuntimeError('Cannot find a Python window executable for the MISA manager.')
    app = root / 'app.py'
    if not app.is_file():
        raise RuntimeError('Cannot find the MISA XML Manager source entry point.')
    flags = getattr(subprocess, 'DETACHED_PROCESS', 0)
    if xml_paths is not None and not isinstance(xml_paths, (list, tuple)):
        raise ValueError('xml_paths must be a list of regular .xml files.')
    arguments = [str(pythonw), str(app), '--codex-test']
    checked_paths = []
    for value in (xml_paths or []):
        path = Path(value).expanduser().resolve()
        if path.is_symlink() or not path.is_file() or path.suffix.lower() != '.xml':
            raise ValueError('Each live-test XML path must be a regular .xml file: ' + str(path))
        checked_paths.append(str(path))
    if checked_paths:
        arguments.extend(('--xml', *checked_paths))
    child_env = os.environ.copy()
    for key in ('MISA_REPAIR_INCIDENT', 'MISA_REPAIR_REQUEST',
                'MISA_CODEX_SESSION_FILE', 'MISA_CODEX_SESSION_NONCE',
                'CODEX_THREAD_ID'):
        child_env.pop(key, None)
    child_env['MISA_SOURCE_ROOT'] = str(root)
    process = subprocess.Popen(arguments, cwd=str(root),
                               close_fds=True, creationflags=flags, env=child_env)
    deadline = time.monotonic() + 30
    last_error = None
    while time.monotonic() < deadline:
        code = process.poll()
        if code is not None:
            raise RuntimeError(f'MISA Codex test manager exited during startup (exit={code}).')
        try:
            session = read_session()
            if session.get('manager_pid') == process.pid and _manager_alive(session):
                return {key: session.get(key) for key in
                        ('session_id', 'mode', 'state', 'phase', 'manager_pid',
                         'source_revision', 'selected_path', 'updated_at')}
        except RuntimeError as error:
            last_error = error
        time.sleep(0.1)
    if process.poll() is None:
        try:
            process.terminate()
            process.wait(timeout=5)
        except Exception:
            try:
                process.kill()
            except Exception:
                pass
    raise RuntimeError('Timed out waiting for the MISA Codex test manager to publish its session.'
                       + ((' ' + str(last_error)) if last_error else ''))


def codex_controller_prompt(session_id):
    """Build the goal for a user-started Codex CLI session."""
    goal = (
        'Drive the explicit MISA Codex live-test session already running for this project. '
        f'The session id is {session_id}. Do not launch another manager. '
        f'First call claim_live_test(expected_session_id="{session_id}"), then '
        'get_live_test_state for a fresh observation, and call '
        'start_live_test to run the selected XML workflow. Route all MISA input through the app control plane; '
        'use observe, click, type, key, scroll, tab, workflow, save/discard or XML tools when requested. '
        'When an incident appears, read '
        'the bounded evidence requested by its packet, fix the bug at its shared root, and inspect the '
        'fresh performance evidence for a safe, targeted optimization related to the same path. Apply '
        'that optimization in the same source change when the evidence supports it; preserve behavior '
        'and avoid speculative or unrelated refactors. Run only the permitted source syntax/build checks, '
        'call submit_live_test_handoff, and keep this same conversation open. '
        'After every handoff, repeatedly call get_live_test_state while the manager reloads; when it returns '
        'running, continue the workflow from the reported checkpoint. If another incident appears, repair it '
        'in this same conversation and hand it off again. Never close the Codex tab, launch a second manager, '
        'or call start_live_test again merely because source was reloaded. Do not run native input from the '
        'MCP process; route it through the app control plane. Do not run unittest/pytest/other test suites.'
    )
    # Make the first chat entry a persistent Codex goal instead of a plain prompt.
    return '/goal ' + goal


def terminate_live_test_process(session_id):
    """Stop only the manager created for a failed Develop launch."""
    try:
        session = read_session(session_id)
    except RuntimeError:
        return
    if session.get('mode') != LIVE_MODE or not _manager_alive(session):
        return
    try:
        os.kill(int(session['manager_pid']), signal.SIGTERM)
    except (OSError, ValueError, TypeError):
        pass


def _manager_alive(data):
    pid = data.get('manager_pid')
    birth = _process_birth(pid) if type(pid) is int and pid > 0 else None
    if (type(pid) is not int or pid <= 0 or birth is None
            or data.get('manager_birth') is None or birth != data.get('manager_birth')):
        return False
    try:
        return _process_executable(pid) == os.path.normcase(data.get('manager_executable', ''))
    except Exception:
        return False

