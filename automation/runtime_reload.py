"""Prepare and attest every runtime component before app-controlled recovery."""
import json
import os
from pathlib import Path
import subprocess

from source_updates import DirectSource, atomic_json, digest, manifest_revision, runtime_identity
from .domain import Cancelled
from .managed_process import OwnedProcess
from .paths import state_dir


class WorkflowLease:
    """An OS lock held across reloads, including the interval without a Tk window."""
    def __init__(self):
        import msvcrt
        path = state_dir() / 'add-workflow.lock'
        path.parent.mkdir(parents=True, exist_ok=True)
        self.stream = path.open('a+b')
        if self.stream.tell() == 0:
            self.stream.write(b'0')
            self.stream.flush()
        self.stream.seek(0)
        try:
            msvcrt.locking(self.stream.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError:
            self.stream.close()
            self.stream = None
            raise RuntimeError('Một workflow Thêm khác đang giữ quyền điều khiển MISA.')

    def close(self):
        if self.stream is not None:
            import msvcrt
            self.stream.seek(0)
            msvcrt.locking(self.stream.fileno(), msvcrt.LK_UNLCK, 1)
            self.stream.close()
            self.stream = None


def prepare_engine(source, cancel):
    from .uia_bridge import _verified_engine
    files = {name: value for name, value in source.files.items()
             if name.startswith(('src/', 'locators/')) or name in
             ('build-engine.ps1', 'Directory.Build.props', 'MisaAutomation.sln')}
    revision = manifest_revision(files)
    root = source.root / 'release' / 'engine'
    stamp = root / 'source-attestation.json'
    cached = {}
    try:
        cached = json.loads(stamp.read_text(encoding='utf-8'))
    except (OSError, ValueError):
        pass
    if cached.get('source_hashes') != files:
        log = state_dir() / 'engine-logs' / ('build-' + revision + '.log')
        log.parent.mkdir(parents=True, exist_ok=True)
        with log.open('wb') as output:
            owned = OwnedProcess(['powershell.exe', '-NoProfile', '-NonInteractive',
                '-ExecutionPolicy', 'Bypass', '-File', str(source.root / 'build-engine.ps1'), '-Publish'],
                cwd=source.root, stdin=subprocess.DEVNULL, stdout=output, stderr=subprocess.STDOUT)
            try:
                code = owned.wait(cancel)
            finally:
                owned.close()
        if code:
            raise RuntimeError('Biên dịch engine chưa xong; xem ' + str(log))
        # The compiler must have consumed the same files the handoff approved.
        if DirectSource(source.root).files != source.files:
            raise RuntimeError('Source đổi trong lúc biên dịch engine; không được nạp.')
        executable = _verified_engine(root, root / 'current.json')
        cached = dict(source_hashes=files, source_revision=revision, executable=str(executable),
                      exe_sha256=digest(executable.read_bytes()))
        atomic_json(stamp, cached)
    executable = _verified_engine(root, root / 'current.json')
    if str(executable) != cached.get('executable') or digest(executable.read_bytes()) != cached.get('exe_sha256'):
        raise RuntimeError('Engine không khớp bản biên dịch đã xác nhận source.')
    if cancel.is_set():
        raise Cancelled('Đã dừng chuẩn bị runtime.')
    return cached


def attest_runtime(expected, engine, cancel):
    from .uia_bridge import UiaBridge, _verified_engine
    identity = runtime_identity()
    if (identity['revision'] != expected['source_revision'] or identity['files'] != expected['files']
            or identity['host_sha256'] != expected['files']['source_updates.py']):
        raise RuntimeError('Runtime còn code cũ hoặc module không khớp incident/source handoff.')
    root = Path(engine['executable']).parents[2]
    if str(_verified_engine(root, root / 'current.json')) != engine['executable']:
        raise RuntimeError('Engine pointer đã đổi sau khi chuẩn bị runtime.')
    os.environ['MISA_ENGINE_PATH'] = engine['executable']
    os.environ['MISA_ENGINE_SHA256'] = engine['exe_sha256']
    bridge = UiaBridge(cancel)
    try:
        if digest(Path(engine['executable']).read_bytes()) != engine['exe_sha256']:
            raise RuntimeError('Engine binary đã đổi trước khi sẵn sàng.')
        identity = runtime_identity()
        identity.update(engine=engine, engine_probe_pid=bridge.process.pid, engine_probe_closed=True,
                        engine_capabilities=sorted(bridge.capabilities), ready=True)
    finally:
        bridge.close()
    if cancel.is_set():
        raise Cancelled('Đã dừng xác nhận runtime mới.')
    return identity
