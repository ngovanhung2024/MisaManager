"""One user-started Add session: run, repair, reload, recover, restart, or stop."""
from copy import deepcopy
from dataclasses import asdict
from decimal import Decimal
import json
import os
from pathlib import Path
import queue
import sys
import threading
import uuid

from source_updates import DirectSource, atomic_json, loaded_source, runtime_identity
from .domain import Cancelled
from .incident_evidence import EvidenceJournal, now
from .paths import state_dir
from .runtime_reload import WorkflowLease, prepare_engine, attest_runtime
from .storage import prune_generated_storage
from .tab_recovery import TabLedger


PHASES = {
    'preparing_runtime': 'Đang chuẩn bị runtime cho Thêm…',
    'running': 'Đang chạy Thêm theo checkpoint…',
    'collecting': 'Đã dừng thao tác; đang thu thập sự cố…',
    'sending': 'Đang gửi log và ảnh sự cố cho Codex…',
    'accepted': 'Codex đã tiếp nhận yêu cầu sửa.',
    'processing': 'Codex đang phân tích và sửa source…',
    'console_open': 'Đã mở hội thoại Codex CLI mới; đang chờ bàn giao source…',
    'waiting_for_codex': 'Workflow đã dừng ở lỗi; đang chờ Codex CLI test sửa source và bàn giao…',
    'manual_attention': 'Workflow đã dừng ở lỗi. Chế độ thực tế chỉ ghi lỗi; không tự gọi Codex.',
    'incomplete': 'Codex chưa sửa xong; đã dừng, không tự mở model mới.',
    'technical_error': 'Kênh sửa gặp lỗi kỹ thuật; đã dừng, không tự kết nối lại.',
    'source_ready': 'Codex đã sửa source; đang chuẩn bị nạp…',
    'preparing_reload': 'Đang kiểm tra source và chuẩn bị các module mới…',
    'quiescing': 'Đang hủy và thu hồi worker cũ trước reload…',
    'reloading': 'Đang hot reload source đã xác nhận…',
    'verifying_runtime': 'Đang xác nhận phiên bản code thực sự được nạp…',
    'cleaning_tabs': 'Đang đóng tab của lượt chạy và chọn không lưu…',
    'checking_sales': 'Đang kiểm tra frame và map nút Thêm trên trang Bán hàng bạn đã chọn…',
    'restarting': 'Danh sách Bán hàng đã sẵn sàng; đang bắt đầu lượt Thêm mới…',
    'resuming': 'Source mới đã nạp; đang tiếp tục checkpoint bị lỗi trên cửa sổ MISA hiện tại…',
    'stopping': 'Đang dừng toàn bộ chu trình…',
    'stopped': 'Đã dừng. Phản hồi sửa đến sau không tự chạy lại.',
    'complete': 'Workflow đã hoàn tất; chứng từ bán hàng chưa Cất.',
    'source_updated': 'Codex đã sửa source. Bấm Hot reload để nạp; chưa xác minh trên MISA.',
    'no_change': 'Codex đã phân tích lỗi; không cần sửa source.',
}


def restore_invoice(data):
    from invoice_xml import Invoice, InvoiceLine, Party, TaxSubtotal
    data = deepcopy(data)
    data['seller'], data['buyer'] = Party(**data['seller']), Party(**data['buyer'])
    for row in data['lines']:
        for key in ('quantity', 'unit_price', 'amount', 'discount', 'tax', 'discount_rate'):
            if row.get(key) is not None:
                row[key] = Decimal(row[key])
    data['lines'] = tuple(InvoiceLine(**row) for row in data['lines'])
    for row in data['tax_subtotals']:
        for key in ('amount', 'tax'):
            if row.get(key) is not None:
                row[key] = Decimal(row[key])
    data['tax_subtotals'] = tuple(TaxSubtotal(**row) for row in data['tax_subtotals'])
    data['notes'], data['warnings'] = tuple(data['notes']), tuple(data['warnings'])
    for key in ('total', 'before_tax', 'tax', 'exchange_rate', 'total_discount'):
        if data.get(key) is not None:
            data[key] = Decimal(data[key])
    return Invoice(**data)


class RecoveryController:
    def __init__(self, owner, session=None):
        self.owner = owner
        self.host = owner.update_host
        self.lock = threading.RLock()
        self.events = queue.Queue()
        self.worker = None
        self.result = None
        self.stage = None
        self.task = None
        self.pending_source = None
        self.pending_error = None
        self.failure_journal = None
        self.data = deepcopy(session or {})
        self.run_mode = getattr(owner, 'run_mode', self.data.get('run_mode', 'actual'))
        self.external_live_test = self.run_mode == 'codex_test'
        if self.data.get('active') and not self.external_live_test:
            # Actual mode must never resume a workflow handed over by an older
            # host/session; leave it for manual attention instead.
            lease = getattr(self.host, 'workflow_lease', None)
            if lease is not None:
                lease.close()
                self.host.workflow_lease = None
            self.data = {}
        self.invoice = restore_invoice(self.data['invoice']) if self.data.get('active') and self.data.get('invoice') else None
        self.cancel = getattr(self.host, 'recovery_cancel', None) or threading.Event()
        self.host.recovery_cancel = self.cancel
        self.resume_pending = bool(self.data.get('active')) and self.external_live_test

    @property
    def active(self):
        return bool(self.data.get('active'))

    def enter_external_live_test(self):
        """Attach Codex control to this idle manager without replacing its UI."""
        if self.active or self.worker is not None:
            raise RuntimeError('Không thể chuyển sang Develop khi workflow đang chạy.')
        self.run_mode = 'codex_test'
        self.external_live_test = True
        self.resume_pending = False

    def leave_external_live_test(self):
        """Roll back an in-place Develop transition that failed to launch."""
        if self.active or self.worker is not None:
            return
        self.run_mode = 'actual'
        self.external_live_test = False
        self.resume_pending = False

    def snapshot(self):
        with self.lock:
            return deepcopy(self.data)

    def phase_message(self, phase):
        if self.run_mode == 'actual':
            local = {
                'collecting': 'Đang lưu log và bằng chứng sự cố cục bộ…',
                'manual_attention': 'Workflow đã dừng ở lỗi. Xem Log lỗi để xử lý.',
            }
            if phase in local:
                return local[phase]
        return PHASES.get(phase, phase)

    def publish(self, phase=None, **updates):
        with self.lock:
            if phase is not None:
                updates['phase'] = phase
                updates.setdefault('message', self.phase_message(phase))
            self.data.update(updates, updated_at=now(), source_revision=self.owner.source_revision)
            try:
                atomic_json(state_dir() / 'add-recovery.json', self.data)
            except OSError as error:
                if getattr(error, 'errno', None) != 28:
                    raise
                try:
                    prune_generated_storage(force=True)
                    atomic_json(state_dir() / 'add-recovery.json', self.data)
                except OSError as retry_error:
                    self.data['status_write_error'] = str(retry_error)
            payload = deepcopy(self.data)
        live_test = getattr(self.owner, 'live_test', None)
        if live_test is not None:
            live_state = ('blocked' if payload.get('phase') in ('collecting', 'waiting_for_codex',
                          'incomplete', 'technical_error') else
                          'complete' if payload.get('phase') == 'complete' else
                          'stopped' if payload.get('phase') == 'stopped' else 'running')
            live_test.publish(state=live_state, phase=payload.get('phase'),
                              incident=payload.get('incident'),
                              message=payload.get('message'),
                              error=(payload.get('workflow') or {}).get('error', ''),
                              baseline_source_revision=payload.get('source_revision'),
                              baseline_source_files=payload.get('source_files'),
                              recovery={key: payload.get(key) for key in
                                        ('session_id', 'mode', 'phase', 'active', 'attempt',
                                         'source_revision', 'incident', 'parent_incident')})
        self.events.put(('status', payload))

    def start(self, invoice, *, error=None, operation=None):
        if self.active or self.worker:
            return
        if getattr(self.host, 'workflow_lease', None) is not None:
            raise RuntimeError('Workflow trước chưa trả quyền điều khiển MISA.')
        self.host.workflow_lease = WorkflowLease()
        self.cancel = threading.Event()
        self.host.recovery_cancel = self.cancel
        self.host.recovery_user_stop.clear()
        self.invoice = invoice
        self.task = None
        self.pending_error = None
        self.failure_journal = None
        self.owner._add_task = None
        from .win32 import process_birth
        session_id = uuid.uuid4().hex
        mode = 'tool_error' if error is not None else 'add_sale'
        source = loaded_source()
        self.data = dict(schema=1, session_id=session_id, active=True, mode=mode,
            run_mode=self.run_mode, external_live_test=self.external_live_test,
            user_started_at=now(), manager_pid=os.getpid(), manager_executable=sys.executable,
            manager_birth=process_birth(os.getpid()), source_revision=self.owner.source_revision,
            source_files=deepcopy(getattr(source, 'files', {})),
            invoice=json.loads(json.dumps(asdict(invoice), default=str)) if invoice else None,
            phase='preparing_runtime', attempt=0, ledger={}, effects={},
            incident=None, parent_incident=None, handoff=None, workflow=None, stop_version=0)
        self.owner.adding = True
        self.owner._comparison_invoice = invoice
        self.owner._comparison_state = None
        self.owner.refresh_controls()
        if error is not None:
            self.data['workflow'] = dict(workflow=mode, function=operation, run_id=session_id,
                state='blocked', active_step='tool.error', error=str(error), completed=[])
            self.publish('collecting')
            self.launch('incident', lambda: self.collect_failure('tool.error', error))
            return
        self.publish('preparing_runtime')
        self.launch('initial_runtime', self.initial_runtime)

    def initial_runtime(self):
        source = loaded_source()
        if not isinstance(source, DirectSource):
            raise RuntimeError('Chu trình tự phục hồi cần khởi động từ source trực tiếp.')
        engine = prepare_engine(source, self.cancel)
        expected = dict(source_revision=source.revision, files=source.files)
        runtime = attest_runtime(expected, engine, self.cancel)
        return dict(engine=engine, runtime=runtime)

    def launch(self, stage, function):
        if self.cancel.is_set() or not self.active:
            return
        if self.worker is not None:
            raise RuntimeError('Chỉ một worker được phép chạy trong chu trình Thêm.')
        self.stage = stage
        self.result = None
        events = self.events

        def work():
            try:
                value = function()
                events.put(('result', (stage, value, None)))
            except BaseException as error:
                events.put(('result', (stage, None, error)))

        self.worker = threading.Thread(target=work, name='misa-recovery-' + stage, daemon=True)
        try:
            self.worker.start()
        except Exception:
            self.worker = None
            raise

    def workflow_state(self, state):
        with self.lock:
            self.data['workflow'] = deepcopy(state)
            self.data['effects'] = deepcopy(state.get('effects', self.data['effects']))
        self.events.put(('workflow', state))

    def save_ledger(self, value):
        self.publish(ledger=value)

    def save_effects(self, value):
        self.publish(effects=value)

    def run_workflow(self):
        from .add_sale import AddSale
        self.publish('running', attempt=self.data['attempt'] + 1)
        ledger = TabLedger(self.data['ledger'], self.save_ledger)
        task = AddSale(self.invoice, self.cancel, self.workflow_state,
            source_revision=self.owner.source_revision,
            ledger=ledger, effects=self.data['effects'], save_effects=self.save_effects,
            runtime=self.data.get('runtime'), parent_incident=self.data.get('parent_incident'),
            run_mode=self.run_mode,
            resume_state=self.data.get('workflow') if self.data.get('resume_context') else None,
            resume_window_kind=(self.data.get('resume_context') or {}).get('window_kind', ''))
        self.task = task
        self.owner._add_task = task
        task.run()
        return deepcopy(task.state)

    def stop(self):
        # This event survives module reload and outranks any later completion message.
        self.host.recovery_user_stop.set()
        self.cancel.set()
        if self.active:
            updates = dict(phase='stopping', message=PHASES['stopping'], stop_requested=True,
                           stop_version=self.data.get('stop_version', 0) + 1,
                           updated_at=now(), source_revision=self.owner.source_revision)
            try:
                self.publish(**updates)
            except (OSError, ValueError) as error:
                # Cancellation must remain usable even when diagnostic storage is unavailable.
                with self.lock:
                    self.data.update(updates, status_write_error=str(error))
                    payload = deepcopy(self.data)
                self.events.put(('status', payload))
        elif getattr(self.host, 'codex_console', None) is not None:
            self.host.codex_console.close()

    def callback_failed(self, error):
        if self.active and self.pending_error is None:
            self.pending_error = error
            self.cancel.set()

    def resume_after_callback(self):
        error, self.pending_error = self.pending_error, None
        self.cancel = threading.Event()
        self.host.recovery_cancel = self.cancel
        self.publish('collecting')
        self.launch('incident', lambda: self.collect_failure('callback', error))

    def finish(self, phase, message=None):
        self.host.close_reload_stop()
        if phase == 'stopped' and getattr(self.host, 'codex_console', None) is not None:
            self.host.codex_console.close()
        try:
            self.publish(phase, active=False, message=message or self.phase_message(phase))
        except OSError as error:
            self.data['status_write_error'] = str(error)
        self.owner.adding = False
        self.owner.reloading = False
        lease = getattr(self.host, 'workflow_lease', None)
        if lease:
            lease.close()
            self.host.workflow_lease = None
        self.owner.status.set(message or self.phase_message(phase))
        live_test = getattr(self.owner, 'live_test', None)
        if live_test is not None:
            live_test.publish(state='complete' if phase == 'complete' else 'stopped',
                              phase=phase, message=message or PHASES[phase])
        self.owner.refresh_controls()
        if self.owner._close_after_add:
            self.owner._close_after_add = False
            self.owner.close()

    def capture_failure(self, stage, error):
        if stage == 'workflow' and self.task and self.task.state.get('incident_path'):
            return self.task.state['incident_path']
        if stage == 'workflow' and self.task:
            return self.task.evidence.incident(self.task.state, error, runtime=self.data.get('runtime'),
                ledger=self.data.get('ledger'), parent=self.data.get('parent_incident'), cancel=self.cancel)
        state = deepcopy(getattr(error, 'recovery_state', None) or self.data.get('workflow') or {})
        state.update(active_step=stage, error=str(error), source_revision=self.owner.source_revision,
                     invoice_number=self.invoice.number if self.invoice else None,
                     invoice_source=self.invoice.source if self.invoice else None,
                     next_step=PHASES.get(self.data.get('phase'), stage))
        if getattr(error, 'recovery_state', None):
            state['active_step'] = error.recovery_state['active_step']
        # Keep the persisted workflow identity aligned with recovery evidence;
        # external handoff validation must compare against this incident run.
        with self.lock:
            self.data['workflow'] = deepcopy(state)
        state['recovery'] = self.snapshot()
        if self.data.get('mode') == 'tool_error':
            state.update({key: self.data[key] for key in
                          ('manager_pid', 'manager_executable', 'manager_birth', 'updated_at')})
        state['reload_error'] = getattr(self.host, 'reload_error', None)
        runtime = dict(previous_attestation=self.data.get('runtime'), ready=False)
        try:
            runtime.update(runtime_identity(strict=False))
        except Exception as identity_error:
            runtime['identity_error'] = str(identity_error)
        if self.failure_journal is None:
            run_id = self.data['session_id'] if self.data.get('mode') == 'tool_error' else uuid.uuid4().hex
            self.failure_journal = EvidenceJournal(run_id, self.owner.source_revision)
        return self.failure_journal.incident(state, error, runtime=runtime,
            ledger=self.data.get('ledger'), parent=self.data.get('parent_incident'), phase=stage, cancel=self.cancel)

    def collect_failure(self, stage, error):
        self.failure_journal = getattr(error, 'evidence_journal', None)
        attempt = 0
        while not self.cancel.is_set():
            try:
                return getattr(error, 'incident_path', None) or self.capture_failure(stage, error)
            except Cancelled:
                raise
            except Exception as collection_error:
                # Retry the same evidence package; never replace the original error/frame.
                attempt += 1
                if isinstance(collection_error, OSError) and getattr(collection_error, 'errno', None) == 28:
                    prune_generated_storage(force=True)
                details = dict(error=str(collection_error), original_error=str(error),
                               stage=stage, attempt=attempt, at=now())
                try:
                    self.publish('collecting', collection_error=details)
                except (OSError, ValueError):
                    with self.lock:
                        self.data.update(phase='collecting', collection_error=details)
                    self.events.put(('status', self.snapshot()))
                self.cancel.wait(min(60, 2 ** min(attempt, 6)))
        raise Cancelled('Đã dừng thu thập sự cố; không gửi yêu cầu mới.')

    def wait_for_external_repair(self, folder):
        """Pause the app-owned workflow until the claimed Codex session hands off source."""
        self.cancel = threading.Event()
        self.host.recovery_cancel = self.cancel
        self.publish('waiting_for_codex', incident=folder, external_live_test=True)

    def accept_external_handoff(self, request):
        """Validate a live-test handoff and enter the existing reload/recovery path."""
        if not self.active or not self.external_live_test:
            raise RuntimeError('Không có Codex test workflow đang chờ source.')
        if self.worker is not None or self.data.get('phase') != 'waiting_for_codex':
            raise RuntimeError('Workflow chưa ở trạng thái chờ Codex bàn giao.')
        if not isinstance(request, dict) or request.get('status') != 'source_ready':
            raise ValueError('Live-test handoff phải có status=source_ready.')
        incident_path = Path(self.data.get('incident') or '')
        baseline_revision = self.data.get('source_revision')
        baseline_files = self.data.get('source_files') or {}
        if (not isinstance(baseline_revision, str)
                or not isinstance(baseline_files, dict) or not baseline_files):
            raise ValueError('Incident recovery has no complete source baseline.')
        if (not incident_path.is_dir() or request.get('incident_id') != incident_path.name
                or request.get('session_id') != getattr(self.owner.live_test, 'session_id', None)):
            raise ValueError('Live-test handoff không khớp phiên/incident hiện tại.')
        if request.get('base_revision') != baseline_revision:
            raise ValueError('Live-test handoff không khớp source baseline của incident.')
        if request.get('base_files') != baseline_files:
            raise ValueError('Live-test handoff không khớp file map baseline của incident.')
        try:
            incident = json.loads((incident_path / 'incident.json').read_text(encoding='utf-8'))
        except (OSError, ValueError) as error:
            raise ValueError('Không đọc được incident hiện tại: ' + str(error)) from None
        if (incident.get('id') != incident_path.name
                or incident.get('source_revision') != baseline_revision):
            raise ValueError('Live-test handoff không khớp identity/baseline của incident.')
        root_cause = request.get('root_cause')
        affected = request.get('affected_callers')
        message = request.get('message')
        if (not isinstance(root_cause, str) or not root_cause.strip()
                or not isinstance(message, str) or not message.strip()
                or not isinstance(affected, list) or not affected
                or not all(isinstance(value, str) and value.strip() for value in affected)):
            raise ValueError('Live-test handoff thiếu root_cause, affected_callers hoặc message.')
        from .paths import project_root
        root = project_root()
        if root is None:
            raise RuntimeError('Không tìm thấy source root để xác thực live-test handoff.')
        source = DirectSource(root)
        changed = sorted(name for name in set(baseline_files) | set(source.files)
                         if baseline_files.get(name) != source.files.get(name))
        if not changed or source.revision == baseline_revision:
            raise ValueError('Codex chưa thay đổi source của incident hiện tại.')
        declared_changed = request.get('changed_files')
        declared_files = request.get('files')
        if (not isinstance(declared_changed, list)
                or declared_changed != changed
                or not isinstance(declared_files, dict)
                or declared_files != source.files):
            raise ValueError('Live-test handoff không khớp diff/file map source thực tế.')
        if request.get('source_revision') != source.revision:
            raise ValueError('Source đã đổi sau khi Codex gửi live-test handoff.')
        if request.get('verification') != 'unverified':
            raise ValueError('Live-test handoff phải giữ verification=unverified.')
        request_id = request.get('request_id')
        if not isinstance(request_id, str) or not request_id.strip():
            raise ValueError('Live-test handoff thiếu request_id.')
        handoff = dict(status='source_ready', request_id=request.get('request_id') or uuid.uuid4().hex,
                       incident_id=incident_path.name, base_revision=baseline_revision,
                       base_files=baseline_files,
                       source_revision=source.revision, root_cause=root_cause.strip(),
                       message=message.strip(), changed_files=changed,
                       affected_callers=affected, verification='unverified', files=source.files,
                       validated_at=now(), external_live_test=True,
                       runtime_before=baseline_revision)
        atomic_json(incident_path / 'repair-handoff.json', handoff)
        self.cancel = threading.Event()
        self.host.recovery_cancel = self.cancel
        self.publish('preparing_reload', handoff=handoff,
                     parent_incident=dict(path=str(incident_path), request_id=handoff['request_id'],
                                          source_revision=handoff['source_revision']))
        self.launch('prepare_reload', self.prepare_reload)

    def prepare_reload(self):
        from .paths import project_root
        handoff = self.data['handoff']
        root = project_root()
        if root is None:
            raise RuntimeError('Không tìm thấy source root để chuẩn bị reload.')
        source = DirectSource(root, expected=handoff['source_revision'])
        if source.files != handoff['files']:
            raise RuntimeError('Danh sách source không khớp thông báo sửa hoàn tất.')
        engine = prepare_engine(source, self.cancel)
        return source, engine

    def quiesce(self):
        if self.task and self.task.performance:
            self.task.performance._writer.join()
        if self.cancel.is_set():
            raise Cancelled('Đã dừng trước khi thay source.')
        self.host.keep_stop_available(self.cancel)

    def recover(self):
        from .screen_win32 import ScreenDesktop
        from .performance import PerformanceTrace
        journal = EvidenceJournal(uuid.uuid4().hex, self.owner.source_revision)
        ledger = TabLedger(self.data['ledger'], self.save_ledger)
        trace = PerformanceTrace(state_dir() / 'performance-logs', run_id=journal.run_id,
                                 source_revision=self.owner.source_revision, capture_images=False,
                                 context={'workflow': 'add_recovery'})
        desktop = None
        try:
            with journal.bind(), trace.bind():
                desktop = ScreenDesktop(self.cancel, ledger=ledger)
                workflow = self.data.get('workflow') or {}
                checkpoint = str(workflow.get('active_step') or '')
                # A recovery failure can persist restart.close even though the
                # original item dialog and its active query are still owned by
                # this run. Preserve that live item checkpoint for the next
                # handoff instead of entering cleanup and closing the voucher.
                if (checkpoint == 'restart.close'
                        and (workflow.get('active_query')
                             or workflow.get('active_row') is not None)
                        and any(entry.get('kind') == 'new_item'
                                and entry.get('state') != 'closed'
                                for entry in ledger.open_surfaces())):
                    checkpoint = f"add.items.{int(workflow.get('active_row') or 1)}.search"
                    # Keep the persisted workflow aligned with the live item
                    # dialog.  Otherwise AddSale receives the stale
                    # restart.close checkpoint and starts a fresh add.prepare,
                    # where attach_main() rejects the owned dialog as a
                    # pre-existing blocker.
                    workflow['active_step'] = checkpoint
                    with self.lock:
                        self.data['workflow'] = deepcopy(workflow)
                # Keep the run-owned voucher open for every add checkpoint.  A
                # reload must continue the failed header step just like an
                # item step; closing the voucher here loses the user's place
                # and forces AddSale to start over.
                can_resume = (checkpoint in ScreenDesktop.ADD_HEADER_STEPS
                              or checkpoint.startswith('add.items.')) and ledger.data
                if can_resume:
                    desktop.checkpoint = checkpoint
                    desktop.attach()
                    kind = ('find' if desktop.is_window('find') else
                            'new_item' if desktop.is_window('new_item') else
                            'sale' if desktop.is_window('sale') else '')
                    expected_kinds = ('sale',) if checkpoint in ScreenDesktop.ADD_HEADER_STEPS else (
                        'find', 'new_item', 'sale')
                    if kind not in expected_kinds:
                        can_resume = False
                    else:
                        if kind in ('find', 'new_item'):
                            # A dialog opened by the failed shortcut may remain
                            # visible across reload before its pending ledger entry
                            # was persisted; claim only a non-baseline native surface.
                            ledger.claim_current_surface(desktop, kind)
                        ledger.require_surface(desktop, kind)
                if not can_resume and self.data.get('mode') == 'tool_error' and not ledger.data:
                    # A source-update error has no run-owned tabs. Keep the
                    # existing MISA surface for the user instead of treating it
                    # as an Add checkpoint that must be cleaned and restarted.
                    return dict(ledger=ledger.data, resume_context=None, tool_error_idle=True)
                if not can_resume:
                    desktop.checkpoint = 'restart.close'
                    if not ledger.data:
                        desktop.attach_main()
                        ledger.baseline(desktop)
                    else:
                        desktop.attach()
                    desktop.recovery.notify = lambda phase, message: self.publish(phase, message=message)
                    desktop.recovery.prepare_restart()
                    return dict(ledger=ledger.data, resume_context=None)
                return dict(ledger=ledger.data,
                            resume_context={'checkpoint': checkpoint, 'window_kind': kind})
        except Cancelled:
            raise
        except Exception as error:
            state = deepcopy(self.data.get('workflow') or {})
            # Recovery evidence gets a fresh journal/run identity; keep the
            # workflow snapshot bound to that incident for handoff validation.
            state.update(run_id=journal.run_id,
                         active_step=desktop.checkpoint if desktop else 'restart.attach',
                         last_action=deepcopy(desktop.last_action) if desktop else {},
                         invoice_number=self.invoice.number, invoice_source=self.invoice.source)
            with self.lock:
                self.data['workflow'] = deepcopy(state)
            error.evidence_journal = journal
            error.recovery_state = state
            try:
                error.incident_path = journal.incident(state, error, runtime=self.data.get('runtime'),
                    ledger=ledger.data, parent=self.data.get('parent_incident'), phase='recovery', cancel=self.cancel)
            except Cancelled:
                raise
            except Exception as collection_error:
                state['incident_collection_error'] = str(collection_error)
            raise
        finally:
            if desktop:
                desktop.close()
            trace.finish('stopped' if self.cancel.is_set() else 'finished')
            trace._writer.join()
            journal.discard()

    def poll(self):
        if self.resume_pending:
            # The replacement main UI now has an active event loop and its own Stop button.
            self.host.close_reload_stop()
        changed = False
        try:
            while True:
                kind, payload = self.events.get_nowait()
                if kind == 'result':
                    self.result = payload
                elif kind == 'workflow':
                    self.owner._comparison_state = payload
                    changed = True
                    if not self.cancel.is_set():
                        self.owner.status.set(payload.get('next_step', PHASES['running']))
                elif not self.cancel.is_set():
                    phase = payload.get('phase')
                    self.owner.status.set(payload.get('message') or self.phase_message(phase))
                    self.owner.update_status.set('Source ' + self.owner.source_revision[:12] +
                        ' · vòng ' + str(payload.get('attempt', 0)) + ' · ' + str(phase))
        except queue.Empty:
            pass
        if changed:
            self.owner.render_comparison()
        if self.worker is not None:
            if self.worker.is_alive() or self.result is None:
                return
            self.worker.join()
            self.worker = None
            stage, value, error = self.result
            self.result = None
            if self.pending_error is not None and not self.host.recovery_user_stop.is_set():
                self.resume_after_callback()
                return
            if self.cancel.is_set() or isinstance(error, Cancelled):
                self.finish('stopped')
                return
            if error is not None:
                try:
                    self.owner.log_error(error, operation='Tự phục hồi: ' + stage,
                                         path=self.invoice.source if self.invoice else '',
                                         context={'recovery': self.snapshot()}, dispatch_codex=False)
                except Exception as log_error:
                    self.data['error_log_failure'] = str(log_error)
                if stage == 'repair':
                    phase = getattr(error, 'status', 'technical_error')
                    if phase not in ('incomplete', 'technical_error'):
                        phase = 'technical_error'
                    self.finish(phase, PHASES[phase] + ' ' + str(error))
                    return
                self.publish('collecting')
                self.launch('incident', lambda: self.collect_failure(stage, error))
                return
            if stage == 'incident':
                if self.external_live_test:
                    self.wait_for_external_repair(value)
                else:
                    self.publish('manual_attention', incident=value)
                    self.finish('manual_attention')
            elif stage == 'initial_runtime':
                self.publish(runtime=value['runtime'], engine=value['engine'])
                self.launch('workflow', self.run_workflow)
            elif stage == 'workflow':
                if value.get('state') != 'complete':
                    raise RuntimeError('Workflow returned without a verified completion condition.')
                self.finish('complete', value['next_step'])
            elif stage == 'repair':
                if self.data.get('mode') == 'tool_error':
                    phase = 'no_change' if value.get('status') == 'no_change' else 'source_updated'
                    self.finish(phase, PHASES[phase] + ' ' + value.get('message', ''))
                    return
                self.publish('preparing_reload', handoff=value,
                             parent_incident=dict(path=self.data['incident'], request_id=value['request_id'],
                                                  source_revision=value['source_revision']))
                self.launch('prepare_reload', self.prepare_reload)
            elif stage == 'prepare_reload':
                self.pending_source, engine = value
                self.publish('quiescing', engine=engine)
                self.owner.reloading = True
                self.launch('quiesce', self.quiesce)
            elif stage == 'quiesce':
                self.publish('reloading')
                session = dict(self.owner.input_state(), recovery=self.snapshot(),
                               error_log=self.owner.error_journal.snapshot(),
                               performance=self.owner._performance_snapshot,
                               run_mode=self.run_mode,
                               live_test=self.owner.live_test.snapshot()
                                         if getattr(self.owner, 'live_test', None) else None)
                if getattr(self.owner, 'live_test', None) is not None:
                    self.owner.live_test.close(preserve=True)
                self.host.request(self.pending_source, session)
                self.owner.closing = True
                self.owner.destroy()
            elif stage == 'attest':
                from .screen_win32 import ScreenDesktop
                checkpoint = str((self.data.get('workflow') or {}).get('active_step') or '')
                resume_candidate = ((checkpoint in ScreenDesktop.ADD_HEADER_STEPS
                                     or checkpoint.startswith('add.items.'))
                                    and bool(self.data.get('ledger')))
                self.publish('resuming' if resume_candidate else 'cleaning_tabs', runtime=value)
                atomic_json(Path(self.data['incident']) / 'reload-ack.json',
                    dict(incident_id=self.data['handoff']['incident_id'],
                         request_id=self.data['handoff']['request_id'],
                         runtime=value, at=now(), verified_misa_behavior=False))
                self.launch('recover', self.recover)
            elif stage == 'recover':
                if value.get('tool_error_idle'):
                    self.finish('source_updated', PHASES['source_updated'])
                    return
                self.data['resume_context'] = value.get('resume_context')
                self.publish('restarting' if not value.get('resume_context') else 'resuming',
                             ledger=value.get('ledger', {}), resume_context=self.data['resume_context'])
                self.launch('workflow', self.run_workflow)
            return
        if self.active and self.cancel.is_set():
            if self.pending_error is not None and not self.host.recovery_user_stop.is_set():
                self.resume_after_callback()
                return
            self.finish('stopped')
            return
        if self.resume_pending and self.active:
            self.resume_pending = False
            self.owner.adding = True
            # A tool-error recovery can be created before the async XML import
            # finishes, leaving the serialized recovery invoice empty. Rebind
            # it from the selected entry before resuming the live workflow.
            if self.invoice is None:
                selected = getattr(self.owner, 'selected_path', None)
                candidate = self.owner.entries.get(selected, (None, ''))[0]
                if candidate is None:
                    raise RuntimeError('Không khôi phục được hóa đơn XML đang chọn để tiếp tục workflow.')
                self.invoice = candidate
            self.owner._comparison_invoice = self.invoice
            self.owner.entries[self.invoice.source] = (self.invoice, '')
            self.owner.refresh_controls()
            self.publish('verifying_runtime')
            self.launch('attest', lambda: attest_runtime(self.data['handoff'], self.data['engine'], self.cancel))

    def shutdown(self):
        if self.data.get('phase') == 'reloading' and not self.cancel.is_set():
            return
        self.cancel.set()
        if self.worker:
            self.worker.join()
        lease = getattr(self.host, 'workflow_lease', None)
        if lease:
            lease.close()
            self.host.workflow_lease = None
