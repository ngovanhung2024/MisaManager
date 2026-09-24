"""Interactive Codex CLI repair with an incident/source-bound completion contract."""
from contextlib import contextmanager
import json
import os
from pathlib import Path
import re
import sys
import uuid

from source_updates import DirectSource, atomic_json, manifest_revision, source_files, digest, loaded_source
from .domain import Cancelled
from .incident_evidence import now
from .codex_console import CodexConsoleSession, read_json_object
from .paths import project_root, state_dir
from .storage import prune_repair_requests


RESULT_SCHEMA = {
    'type': 'object', 'additionalProperties': False,
    'properties': {
        'status': {'type': 'string', 'enum': ['source_ready', 'no_change', 'incomplete', 'technical_error']},
        **{name: {'type': 'string'} for name in ('request_id', 'incident_id', 'base_revision',
             'source_revision', 'root_cause', 'message')},
        'changed_files': {'type': 'array', 'items': {'type': 'string'}},
        'affected_callers': {'type': 'array', 'items': {'type': 'string'}},
        'verification': {'type': 'string', 'enum': ['unverified', 'verified']},
    },
}
RESULT_SCHEMA['required'] = list(RESULT_SCHEMA['properties'])

class RepairPending(RuntimeError):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


def repair_reasoning_effort(incident):
    """Use extra reasoning only for concrete escalation signals."""
    configured = os.environ.get('MISA_CODEX_REPAIR_REASONING', 'high')
    if configured not in ('minimal', 'low', 'medium', 'high', 'xhigh'):
        configured = 'high'
    if configured == 'xhigh':
        return configured
    text = ' '.join(str(incident.get(key, '')) for key in
                    ('workflow', 'step', 'error', 'failure_condition', 'source_revision'))
    if isinstance(incident.get('parent'), dict):
        text += ' ' + json.dumps(incident['parent'], ensure_ascii=False)
        return 'xhigh'
    terms = ('reload', 'contradict', 'mismatch', 'conflict', 'c#', 'csharp',
             'dotnet', 'misaautomations.sln', 'syntax', 'build failed', 'parent incident')
    return 'xhigh' if any(term in text.casefold() for term in terms) else configured


@contextmanager
def repair_lock(cancel):
    import msvcrt
    folder = state_dir()
    folder.mkdir(parents=True, exist_ok=True)
    with (folder / 'source-repair.lock').open('a+b') as stream:
        if stream.tell() == 0:
            stream.write(b'0')
            stream.flush()
        while True:
            if cancel.is_set():
                raise Cancelled('Đã dừng khi chờ lượt sửa source.')
            stream.seek(0)
            try:
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                break
            except OSError:
                cancel.wait(.2)
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


class CodexRepair:
    def __init__(self, folder, cancel, notify, *, console=None):
        self.folder, self.cancel, self.notify = Path(folder), cancel, notify
        self.incident = json.loads((self.folder / 'incident.json').read_text(encoding='utf-8'))
        self.request = None
        self.console = console if console is not None else CodexConsoleSession()

    def status(self, state, **details):
        data = dict(state=state, at=now(), incident_id=self.incident['id'],
                    request_id=self.request['request_id'] if self.request else None, **details)
        atomic_json(self.folder / 'repair-status.json', data)
        self.notify(state, data)

    def repair(self):
        try:
            with repair_lock(self.cancel):
                return self._attempt(1)
        except Cancelled:
            self.console.close()
            raise
        except Exception as error:
            # A bad/late handoff must not spend another model turn on the same failure.
            self.console.close()
            self.status(getattr(error, 'status', 'technical_error'),
                        error=f'{type(error).__name__}: {error}', attempt=1)
            raise

    def repair_images(self, images):
        """Attach one failure screen and at most one current action frame."""
        action_frame = (self.incident.get('last_action') or {}).get('frame_id')
        latest_frame = next((entry.get('frame_id') for entry in reversed(images)
                             if entry.get('role') in ('window_input', 'window_capture')), None)
        selected = []
        failure = next((entry for entry in reversed(images)
                        if entry.get('role') == 'failure_desktop'), None)
        if failure:
            selected.append(failure)
        for frame in dict.fromkeys((action_frame, latest_frame)):
            if not frame:
                continue
            detail = next((entry for role in ('window_input', 'window_capture')
                           for entry in reversed(images)
                           if entry.get('frame_id') == frame and entry.get('role') == role), None)
            if detail:
                selected.append(detail)
                break
        output, hashes = [], set()
        for entry in selected:
            path = self.folder / entry.get('name', '')
            if not path.is_file():
                continue
            value = digest(path.read_bytes())
            if value not in hashes:
                hashes.add(value)
                output.append(entry)
        return output

    def _attempt(self, attempt):
        root = project_root()
        if root is None:
            raise RuntimeError('Không xác định được source root để sửa trực tiếp.')
        before = {name: digest(data) for name, data in source_files(root).items()}
        self.request = dict(schema=1, request_id=uuid.uuid4().hex, incident_id=self.incident['id'],
            run_id=self.incident['run_id'], runtime_revision=self.incident['source_revision'],
            base_revision=manifest_revision(before), before_files=before, created_at=now(), attempt=attempt)
        request = self.request
        requests = self.folder / 'repair-requests' / request['request_id']
        requests.mkdir(parents=True, exist_ok=False)
        prune_repair_requests(self.folder)
        atomic_json(requests / 'request.json', request)
        atomic_json(self.folder / 'repair-request.json', request)
        atomic_json(requests / 'result-schema.json', RESULT_SCHEMA)
        atomic_json(self.folder / 'source-index.json', before)
        manifest = json.loads((self.folder / 'vision-inputs.json').read_text(encoding='utf-8'))
        images = manifest.get('images', [])
        primary = self.repair_images(images)
        command = ['--sandbox', 'workspace-write', '--ask-for-approval', 'never',
                   '--cd', str(root), '--add-dir', str(state_dir()), '--no-alt-screen']
        observer_python = root / '.venv' / 'Scripts' / 'python.exe'
        observer_python = str(observer_python) if observer_python.is_file() else sys.executable
        observer_source = loaded_source()
        observer_path = state_dir() / 'repair-observers' / ((observer_source.revision if observer_source else 'source') + '.json')
        pinned_observer = observer_source is not None and hasattr(observer_source, 'export')
        if pinned_observer:
            observer_path.parent.mkdir(parents=True, exist_ok=True)
            if not observer_path.is_file():
                observer_source.export(observer_path)
            else:
                observer_path.touch()
        observer_bootstrap = (
            "import base64,json,sys,types; "
            "p=json.load(open(sys.argv[1],encoding='utf-8')); "
            "m=types.ModuleType('source_updates'); m.__file__=p['root']+'/source_updates.py'; "
            "sys.path.insert(0,p['root']); sys.modules['source_updates']=m; "
            "exec(compile(base64.b64decode(p['files']['source_updates.py']),m.__file__,'exec'),m.__dict__); "
            "s=m.DirectSource.read(sys.argv[1],p['revision']); m.install_bundle(s); "
            "f=p['root']+'/incident_mcp.py'; sys.argv=[f]; "
            "exec(compile(s.contents['incident_mcp.py'],f,'exec'),{'__name__':'__main__','__file__':f})"
        )
        observer_args = ['-c', observer_bootstrap, str(observer_path)] if pinned_observer else [str(root / 'incident_mcp.py')]
        for key, value in {'command': observer_python, 'args': observer_args,
                           'cwd': str(root), 'enabled': True, 'required': pinned_observer,
                           'enabled_tools': [
                               'begin_repair', 'read_incident_file', 'complete_repair',
                               'observe_misa', 'click_misa', 'type_misa', 'press_key',
                               'scroll_misa', 'select_tab', 'execute_workflow',
                               'save_or_discard', 'edit_xml',
                           ],
                           'default_tools_approval_mode': 'auto',
                           'tool_timeout_sec': 24 * 60 * 60}.items():
            command.extend(['-c', 'mcp_servers.misa_incidents.' + key + '=' + json.dumps(value, ensure_ascii=False)])
        # These two request-scoped writes are the repair protocol itself. Without
        # explicit approval, approval=never rejects them before the MCP server runs.
        for tool in ('begin_repair', 'complete_repair'):
            command.extend(['-c', f'mcp_servers.misa_incidents.tools.{tool}.approval_mode="approve"'])
        message = (
            'Start a NEW, isolated MISA repair conversation. Your first tool call must be '
            f'`begin_repair(incident_id={self.incident["id"]})`. '
            'It atomically binds this fresh thread and returns the compact repair packet, contract and plan. '
            'Follow that packet: edit source directly, use the bounded evidence order and one targeted rg pass. '
            'Fix the reported bug and, in the same change, review the available performance evidence for a '
            'safe targeted optimization on the affected path. Apply it only when evidence supports the gain, '
            'preserve behavior, and avoid speculative or unrelated refactors. '
            'After source checks, you may use observe_misa and the direct control tools when the user has '
            'requested live MISA control; call `complete_repair` for the source handoff and continue if needed. '
            'Do not run a registration helper, read broad incident logs, or reuse an older conversation.'
        )
        effort = repair_reasoning_effort(self.incident)
        self.status('sending', attempt=attempt, image_count=len(images),
                    attached_image_count=len(primary), reasoning_effort=effort)
        if self.cancel.is_set():
            raise Cancelled('Đã dừng trước khi mở Codex CLI.')
        # The host may retain this object across reloads, but every submit creates a new chat.
        mode = CodexConsoleSession.submit(self.console, requests, root, command, message,
                                          [self.folder / entry['name'] for entry in primary], self.cancel,
                                          reasoning_effort=effort)
        self.status('console_open', attempt=attempt,
                    pid=self.console.owned.process.pid, console_mode=mode,
                    thread_id=self.console.thread_id, reasoning_effort=effort,
                    packet_path=str(requests / 'begin-packet.json'))
        self.console.wait_result(requests, self.cancel)
        if self.cancel.is_set():
            raise Cancelled('Đã dừng trước khi nhận source sửa xong.')
        result = read_json_object(requests / 'result.json', 'Codex repair result')
        if (not isinstance(result, dict) or set(result) != set(RESULT_SCHEMA['properties'])
                or not isinstance(result.get('changed_files'), list)
                or not all(isinstance(name, str) for name in result['changed_files'])
                or not isinstance(result.get('affected_callers'), list)
                or not all(isinstance(name, str) for name in result['affected_callers'])
                or result.get('status') not in RESULT_SCHEMA['properties']['status']['enum']
                or not all(isinstance(result.get(key), str) for key in ('root_cause', 'message', 'source_revision'))):
            raise ValueError('Codex trả handoff không đúng schema/hash source.')
        for key in ('request_id', 'incident_id', 'base_revision'):
            if result.get(key) != request[key]:
                raise ValueError('Thông báo sửa không khớp yêu cầu: ' + key)
        no_change = result.get('status') == 'no_change' and self.incident.get('workflow') == 'tool_error'
        if result.get('status') != 'source_ready' and not no_change:
            raise RepairPending(result.get('status', 'incomplete'),
                                'Codex chưa bàn giao source: ' + result.get('message', ''))
        if not re.fullmatch('[a-f0-9]{64}', result['source_revision']):
            raise ValueError('Handoff chưa có hash source hợp lệ.')
        candidate = DirectSource(root, expected=result.get('source_revision'))
        changed = sorted(name for name in set(before) | set(candidate.files)
                         if before.get(name) != candidate.files.get(name))
        if (sorted(result.get('changed_files', [])) != changed or not result.get('root_cause')
                or result.get('verification') not in ('unverified', 'verified')
                or (no_change and (changed or candidate.revision != request['base_revision']))
                or (not no_change and not changed and candidate.revision == request['runtime_revision'])):
            raise ValueError('Handoff thiếu nguyên nhân hoặc changed_files không khớp source thực tế.')
        handoff = dict(result, files=candidate.files, validated_at=now(),
                       codex_thread_id=self.console.thread_id,
                       runtime_before=request['runtime_revision'])
        atomic_json(self.folder / 'repair-handoff.json', handoff)
        atomic_json(requests / 'validated-handoff.json', handoff)
        self.status(result['status'], source_revision=candidate.revision, changed_files=changed,
                    handoff_validated=True, verification=result.get('verification', 'unverified'),
                    thread_id=self.console.thread_id,
                    console_retained=False)
        # A completed repair never becomes context for a later incident.
        self.console.close()
        return handoff
