"""Persistent, serialized JSON-lines transport to the .NET UIA owner process."""
import json
import hashlib
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import uuid

from .domain import Cancelled, NeedsAttention


MAX_RESPONSE = 8 * 1024 * 1024


class EngineError(NeedsAttention):
    def __init__(self, code, message, uncertain=False):
        super().__init__(message)
        self.code, self.uncertain = code, uncertain


def engine_path():
    explicit = os.environ.get('MISA_ENGINE_PATH')
    if explicit:
        return Path(explicit).resolve()
    if getattr(sys, 'frozen', False):
        root = Path(sys.executable).resolve().parent / 'engine'
    else:
        from .paths import project_root
        source = project_root() or Path(__file__).resolve().parents[1]
        root = source / 'release' / 'engine'
    pointer = root / 'current.json'
    if not pointer.is_file():
        return root / 'MisaAutomation.App.exe'
    if pointer.stat().st_size > 8192:
        raise EngineError('ENGINE_MANIFEST', 'Engine version pointer is too large.')
    try:
        return _verified_engine(root, pointer)
    except EngineError:
        raise
    except (OSError, ValueError, KeyError, TypeError, AttributeError) as error:
        raise EngineError('ENGINE_MANIFEST', 'Cannot validate the engine package: ' + str(error)) from error


def _verified_engine(root, pointer):
    data = pointer if isinstance(pointer, dict) else json.loads(pointer.read_text(encoding='utf-8-sig'))
    executable = (root / data['executable']).resolve()
    if data.get('protocol') != 1 or not executable.is_relative_to(root.resolve()):
        raise EngineError('ENGINE_MANIFEST', 'Invalid engine version pointer.')
    manifest_path = executable.parent / 'engine-manifest.json'
    if manifest_path.stat().st_size > 1024 * 1024:
        raise EngineError('ENGINE_MANIFEST', 'Engine manifest is too large.')
    manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    if manifest.get('revision') != data.get('revision') or manifest.get('protocol') != 1:
        raise EngineError('ENGINE_MANIFEST', 'Engine revision does not match the published pointer.')
    files = manifest['files']
    if not isinstance(files, dict) or executable.name not in files or not files:
        raise EngineError('ENGINE_MANIFEST', 'Engine executable is missing from the integrity manifest.')
    actual = {p.relative_to(executable.parent).as_posix() for p in executable.parent.rglob('*') if p.is_file()}
    if actual != set(files) | {'engine-manifest.json'}:
        raise EngineError('ENGINE_MANIFEST', 'Engine package contains missing or unlisted files.')
    for name, expected in files.items():
        path = (executable.parent / name).resolve()
        if not path.is_relative_to(executable.parent) or path == executable.parent:
            raise EngineError('ENGINE_MANIFEST', 'Engine file is outside its version directory.')
        checksum = hashlib.sha256()
        with path.open('rb') as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                checksum.update(chunk)
        if checksum.hexdigest() != expected:
            raise EngineError('ENGINE_MANIFEST', 'Engine file failed integrity validation: ' + name)
    return executable


class UiaBridge:
    def __init__(self, cancel):
        self.cancel = cancel
        self.lock = threading.Lock()
        self.responses = queue.Queue(maxsize=2)
        self.closed = False
        self.cleanup_error = ''
        self.metrics = {'uia_requests': 0, 'uia_total_ms': 0.0}
        executable = engine_path()
        if not executable.is_file():
            raise EngineError('ENGINE_MISSING', 'Build/publish the MISA .NET engine first: ' + str(executable))
        attested = os.environ.get('MISA_ENGINE_SHA256')
        if attested and hashlib.sha256(executable.read_bytes()).hexdigest() != attested:
            raise EngineError('ENGINE_VERSION', 'Engine differs from the attested recovery runtime.')
        log_folder = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'MisaInvoiceTool' / 'engine-logs'
        log_folder.mkdir(parents=True, exist_ok=True)
        self.stderr = (log_folder / ('engine-stderr-' + uuid.uuid4().hex + '.log')).open('wb')
        try:
            self.process = subprocess.Popen([str(executable)], cwd=executable.parent,
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.stderr,
                encoding='utf-8', errors='strict', bufsize=1,
                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        except Exception:
            self.stderr.close()
            raise
        self.reader = threading.Thread(target=self._receive, daemon=True, name='misa-uia-response')
        self.reader.start()
        try:
            hello = self.call('hello')
            self.capabilities = set(hello.get('capabilities', []))
            if hello.get('protocol') != 1 or not {'inline_wait', 'grid_rows', 'wait_window'}.issubset(hello.get('capabilities', [])):
                raise EngineError('ENGINE_VERSION', 'Unsupported MISA engine protocol.')
        except Exception:
            try:
                self.close()
            except Exception:
                pass
            raise

    def _receive(self):
        try:
            while True:
                line = self.process.stdout.readline(MAX_RESPONSE + 1)
                if not line:
                    break
                if len(line) > MAX_RESPONSE or not line.endswith('\n'):
                    raise ValueError('MISA engine response is oversized or incomplete.')
                response = json.loads(line)
                if not isinstance(response, dict):
                    raise ValueError('MISA engine response must be an object.')
                if not response.get('eventName'):
                    self.responses.put_nowait(response)
        except Exception as error:
            try:
                self.responses.put_nowait(error)
            except queue.Full:
                pass
        finally:
            try:
                self.responses.put_nowait(EOFError('MISA engine closed its response stream.'))
            except queue.Full:
                pass

    def call(self, method, parameters=None, timeout=10):
        with self.lock:
            if self.closed:
                raise EngineError('ENGINE_CLOSED', 'MISA engine connection is closed.')
            if self.cancel.is_set():
                raise Cancelled('Da dung thao tac MISA.')
            request_id = uuid.uuid4().hex
            started = time.monotonic()
            action = (parameters or {}).get('action', '')
            uncertain = (method == 'setObservedValue'
                         or (method == 'execute' and action != 'get'
                             and not action.startswith('wait:')))
            try:
                self.process.stdin.write(json.dumps({'id': request_id, 'method': method,
                    'parameters': parameters or {}}, ensure_ascii=False) + '\n')
                self.process.stdin.flush()
                while True:
                    remaining = timeout - (time.monotonic() - started)
                    if remaining <= 0:
                        raise TimeoutError('MISA engine response deadline exceeded.')
                    if self.cancel.is_set():
                        raise Cancelled('Da dung; can doc lai thao tac dang cho MISA.')
                    try:
                        response = self.responses.get(timeout=min(.1, remaining))
                        break
                    except queue.Empty:
                        continue
                if isinstance(response, Exception):
                    raise response
                if response.get('id') != request_id:
                    raise RuntimeError('MISA response correlation mismatch.')
                if type(response.get('success')) is not bool or (response['success'] and 'data' not in response):
                    raise ValueError('Invalid MISA response envelope.')
                if method == 'execute' or method.startswith('voucher.'):
                    result = response.get('data')
                    if response['success'] and (not isinstance(result, dict) or type(result.get('success')) is not bool):
                        raise ValueError('Invalid MISA operation result.')
            except Exception as error:
                # Once transport state is uncertain it must never carry another action.
                self._terminate()
                if isinstance(error, Cancelled):
                    raise
                raise EngineError('ENGINE_UNCERTAIN' if uncertain else 'ENGINE_UNAVAILABLE', str(error), uncertain) from error
            self.metrics['uia_requests'] += 1
            self.metrics['uia_total_ms'] += (time.monotonic() - started) * 1000
            if not response.get('success'):
                raise EngineError(response.get('errorCode', 'ENGINE_ERROR'), response.get('message', ''),
                                  response.get('inputMayHaveBeenSent', uncertain))
            return response.get('data')

    def has_ancestor(self, item, ancestor_token, *, window_id):
        """Read current parents within one window, rejecting cycles or deep chains."""
        token = item.get('parentToken')
        seen = set()
        for _ in range(12):
            if not token or token in seen:
                return False
            if token == ancestor_token:
                return True
            seen.add(token)
            parent = self.call('read', {'token': token})
            if parent.get('windowId') != window_id:
                return False
            token = parent.get('parentToken')
        return False

    def execute(self, action, *, token=None, value=None, checkpoint='', allow_background=False, **extra):
        result = self.call('execute', dict(action=action, token=token, value=value,
                                          workflow=checkpoint, allowBackground=allow_background, **extra),
                           timeout=max(10, extra.get('timeoutMs', 5000) / 1000 + 5))
        if not result['success']:
            raise EngineError(result['errorCode'], result['message'], result.get('inputMayHaveBeenSent', False))
        return result

    def _terminate(self):
        self.closed = True
        try:
            if self.process.poll() is None:
                self.process.kill()
            self.process.wait(timeout=5)
        except (OSError, subprocess.TimeoutExpired) as error:
            self.cleanup_error = str(error)

    def close(self):
        with self.lock:
            if not self.closed:
                self.closed = True
                try:
                    self.process.stdin.close()
                    self.process.wait(timeout=5)
                except (OSError, subprocess.TimeoutExpired):
                    self._terminate()
            elif self.process.poll() is None:
                self._terminate()
            self.reader.join(timeout=1)
            if self.reader.is_alive():
                raise EngineError('ENGINE_CLEANUP', 'Engine response worker has not stopped: ' + self.cleanup_error)
            try:
                self.process.stdin.close()
            finally:
                try:
                    self.process.stdout.close()
                finally:
                    self.stderr.close()
