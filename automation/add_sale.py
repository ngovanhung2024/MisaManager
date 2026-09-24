"""User-started sale entry and reconciliation; the sales voucher stays unsaved."""
from copy import deepcopy
from contextlib import contextmanager
from datetime import date, datetime, timezone
import os
import sys
import uuid

from source_updates import atomic_json
from .domain import Cancelled, NeedsAttention, normalize, scalar_text
from .paths import state_dir
from .performance import PerformanceTrace, span, tracked


CONSUMER_NAME = 'BÁN CHO NGƯỜI TIÊU DÙNG'


def add_sale_state_path():
    return state_dir() / 'add-sale-state.json'


class AddSale:
    def __init__(self, invoice, cancel, notify, *, source_revision='',
                 ledger=None, effects=None, save_effects=None, runtime=None, parent_incident=None,
                 run_mode='actual', resume_state=None, resume_window_kind=''):
        self.invoice = invoice
        self.cancel = cancel
        self.notify = notify
        self.desktop = None
        self.performance = None
        from .tab_recovery import TabLedger
        from .incident_evidence import EvidenceJournal
        self.ledger = ledger or TabLedger()
        self.effects = deepcopy(effects or {})
        self.save_effects = save_effects or (lambda value: None)
        self.runtime = deepcopy(runtime or {})
        self.parent_incident = parent_incident
        self.run_mode = run_mode if run_mode in ('actual', 'codex_test') else 'actual'
        self.state = {
            'workflow': 'add_sale', 'run_id': uuid.uuid4().hex,
            'manager_pid': os.getpid(), 'manager_executable': sys.executable,
            'source_revision': source_revision,
            'invoice_source': invoice.source, 'invoice_number': invoice.number,
            'invoice_series': invoice.series, 'invoice_issued': invoice.issued,
            'seller': invoice.seller.name, 'buyer': invoice.buyer.name,
            'state': 'running', 'active_step': 'add.prepare', 'next_step': '',
            'completed': [], 'readbacks': {}, 'error': '',
            'effects': self.effects,
        }
        if resume_state:
            # Carry only control-flow data across reload. Readbacks belong to
            # the old source/frame and must be reacquired before use.
            for key in ('active_step', 'next_step', 'completed', 'expected', 'sale_hwnd',
                         'customer_note', 'item_mappings', 'line_total', 'active_row',
                         'lines_completed', 'active_query'):
                if key in resume_state:
                    self.state[key] = deepcopy(resume_state[key])
        self.resume_state = deepcopy(resume_state or {})
        self.resume_window_kind = resume_window_kind
        if resume_state:
            self.resume_state['item_mappings'] = deepcopy(self.state.get('item_mappings') or {})
        self.evidence = EvidenceJournal(self.state['run_id'], source_revision)

    def effect(self, key, **changes):
        entry = self.effects.setdefault(key, {})
        entry.update(changes, at=datetime.now(timezone.utc).isoformat(), run_id=self.state['run_id'])
        self.save_effects(deepcopy(self.effects))
        self.publish()
        return entry

    def publish(self, *, persist=True, **changes):
        self.state.update(changes, updated_at=datetime.now(timezone.utc).isoformat())
        if self.desktop and self.desktop.window:
            window = self.desktop.window
            self.state['window'] = dict(hwnd=window.hwnd, pid=window.pid, title=window.title)
        if self.performance and persist:
            self.state['performance'] = self.performance.snapshot()
        if persist:
            with span('state_io', 'Lưu tiến độ Thêm'):
                atomic_json(add_sale_state_path(), self.state)
        self.notify_state()

    def notify_state(self):
        # Full evidence remains in the run state/log; the Tk queue only needs display values.
        payload = {key: value for key, value in self.state.items()
                   if key not in ('readbacks', 'performance', 'last_action')}
        payload['readbacks'] = {
            key: {name: value for name, value in reading.items() if name != 'evidence'}
            if isinstance(reading, dict) else reading
            for key, reading in self.state['readbacks'].items()
        }
        self.notify(deepcopy(payload))

    @contextmanager
    def step(self, checkpoint, message):
        if self.cancel.is_set():
            raise Cancelled('Đã dừng thao tác Thêm; giữ nguyên cửa sổ MISA hiện tại.')
        if self.desktop:
            self.desktop.checkpoint = checkpoint
        with span('step', message, checkpoint=checkpoint):
            self.publish(active_step=checkpoint, next_step=message)
            proof = {}
            yield proof
            if self.cancel.is_set():
                raise Cancelled('Đã dừng sau bước hiện tại; không ghi nhận tối ưu thành công.')
            if proof.get('verified') and self.desktop:
                self.desktop.confirm_region_step()
            self.state['completed'].append(checkpoint)

    def require_sale(self):
        self.desktop.sync(kind='sale')
        if not getattr(self.desktop, 'direct_control', False):
            # A checkpoint transition invalidates the prior frame; refresh it before guarding input.
            self.desktop._native_frame('sale', fresh=True)
        self.desktop.guard()
        seller = normalize(self.invoice.seller.name)
        # Excel exports intentionally omit seller data; only validate the
        # MISA company when the source contains a seller name.
        if seller and normalize(self.desktop.window.title.split(' - ')[-1]) != seller:
            raise NeedsAttention('Đơn vị trên cửa sổ bán hàng không khớp người bán trong XML.')
        if self.desktop.window.hwnd != self.state.get('sale_hwnd'):
            raise NeedsAttention('Cửa sổ bán hàng đã thay đổi trong lượt Thêm.')

    @tracked('verification', lambda self, fields: 'Đọc lại: ' + ', '.join(label for _, label, *_ in fields))
    def verify_fields(self, fields):
        from .observation_wait import read_until
        from .verification import verify

        desktop = self.desktop
        latest = {}

        def observe():
            latest.clear()
            self.require_sale()
            try:
                with desktop.read_scope('sale', field_ids=tuple(item_id for item_id, *_ in fields)):
                    for item_id, label, expected, rule in fields:
                        control = desktop.field((item_id,), label)
                        readings = desktop.read_evidence(
                            control, document=self.state['run_id'], row=None, field=item_id)
                        result = verify(expected, readings, rule=rule,
                            hwnd=desktop.window.hwnd, pid=desktop.window.pid,
                            document=self.state['run_id'], row=None, field=item_id,
                            checkpoint=desktop.checkpoint, run_token=desktop.run_token,
                            input_epoch=desktop.input_epoch, not_before=desktop.last_input_at)
                        desktop.record_region_verification(result)
                        latest[item_id] = result.model_dump(mode='json')
                        self.state['readbacks'][item_id] = latest[item_id]
                        self.publish(persist=False)
                    self.require_sale()
                return all(latest[item_id]['status'] == 'matched' for item_id, *_ in fields)
            except Cancelled:
                raise
            except NeedsAttention as error:
                latest['observation_error'] = str(error)
                return False

        try:
            read_until(observe, bool, self.cancel, timeout=12,
                       label=', '.join(label for _, label, *_ in fields))
        except NeedsAttention as error:
            details = []
            for item_id, label, expected, _ in fields:
                reading = latest.get(item_id)
                if reading and reading['status'] != 'matched':
                    details.append(label + ': ' + reading['reason']
                                   + '; đọc=' + repr(reading.get('actual_raw'))
                                   + '; cần=' + repr(expected))
            if latest.get('observation_error'):
                details.append(latest['observation_error'])
            if details:
                raise NeedsAttention('Không xác minh được thông tin sau Enter/Tab. '
                                     + ' | '.join(details)) from error
            raise
        finally:
            self.state['readbacks'].update(latest)

    def fill(self, item_id, label, value, *, commit=0x09):
        self.require_sale()
        control = self.desktop.field((item_id,), label)
        self.desktop.enter_field(control, value, commit=commit)

    def run(self):
        self.performance = PerformanceTrace(state_dir() / 'performance-logs',
            run_id=self.state['run_id'], source_revision=self.state['source_revision'],
            capture_images=False, context=dict({key: self.state[key] for key in
                ('workflow', 'invoice_source', 'invoice_number', 'invoice_series', 'invoice_issued')},
                line_count=len(self.invoice.lines)))
        try:
            with self.evidence.bind(), self.performance.bind(), span('workflow', 'Thêm thông tin chứng từ'):
                self.evidence.record('workflow_start', state=deepcopy(self.state), runtime=self.runtime)
                self._run()
        except Exception as error:
            if self.state['state'] not in ('paused', 'blocked'):
                self.state.update(state='paused' if isinstance(error, Cancelled) else 'blocked', error=str(error))
            raise
        finally:
            try:
                if self.desktop:
                    self.performance.record('driver_metrics', metrics=self.desktop.metrics())
            except Exception as error:
                self.performance.record('metrics_error', error=f'{type(error).__name__}: {error}')
            self.performance.finish(self.state['state'])
            self.performance._writer.join()
            self.state['performance'] = self.performance.snapshot()
            # A performance report failure must not change the MISA outcome.
            try:
                atomic_json(add_sale_state_path(), self.state)
            except OSError as error:
                self.state['performance']['state_write_error'] = str(error)
            self.notify_state()
            self.evidence.finalize_incident(self.state)

    def _run(self):
        try:
            resume_step = str(self.resume_state.get('active_step') or '')
            resume_header = resume_step in {
                'add.open', 'add.receipt', 'add.customer', 'add.posted_date',
                'add.voucher_date', 'add.invoice'}
            if self.resume_state and (resume_header or resume_step.startswith('add.items.')):
                from .screen_win32 import ScreenDesktop
                self.desktop = ScreenDesktop(self.cancel, ledger=self.ledger)
                self.desktop.checkpoint = self.state['active_step']
                # A resumed workflow may be paused while another app owns the
                # foreground; subsequent guarded actions may restore this exact
                # run-owned sale window before reading or sending input.
                self.desktop.resume_auto_focus = True
                self.desktop.attach()
                current_kind = ('find' if self.desktop.is_window('find') else
                                'new_item' if self.desktop.is_window('new_item') else
                                'sale' if self.desktop.is_window('sale') else '')
                item_resume_to_sale = (
                    self.state['active_step'].startswith('add.items.')
                    and current_kind == 'sale'
                    and self.resume_window_kind in ('find', 'new_item'))
                if current_kind != self.resume_window_kind and not item_resume_to_sale:
                    raise NeedsAttention('Cửa sổ MISA sau hot reload không khớp checkpoint đang tiếp tục.')
                self.ledger.require_surface(self.desktop, current_kind)
                if current_kind == 'sale':
                    # The resumed sale surface is already ledger-owned; bind its
                    # current HWND before require_sale validates the identity.
                    self.state['sale_hwnd'] = self.desktop.window.hwnd
                    self.require_sale()
                if resume_header:
                    # The existing voucher is the continuation point.  It has
                    # already passed the native open check before the reload.
                    if 'add.open' not in self.state['completed']:
                        self.state['completed'].append('add.open')
                    day = date.fromisoformat(self.invoice.issued).strftime('%d/%m/%Y')
                    number = scalar_text(self.invoice.number)
                    consumer = normalize(self.invoice.buyer.name) == normalize(CONSUMER_NAME)
                    expected = self.state.setdefault('expected', {})
                    expected.setdefault('customer_code', 'NTD' if consumer else None)
                    expected.setdefault('posted_date', day)
                    expected.setdefault('voucher_date', day)
                    expected.setdefault('invoice_number', number)
                    if resume_step == 'add.receipt' or 'add.receipt' not in self.state['completed']:
                        with self.step('add.receipt', 'Mở Phiếu thu và đọc thông tin chung…') as proof:
                            self.desktop.ensure_tab('Phiếu thu')
                            proof['verified'] = True
                    if consumer and 'add.customer' not in self.state['completed']:
                        with self.step('add.customer', 'Nhập khách hàng NTD và nhấn Enter…') as proof:
                            self.fill('cboAccountObjectID', 'Khách hàng', 'NTD', commit=0x0D)
                            self.verify_fields((
                                ('cboAccountObjectID', 'Khách hàng', 'NTD', 'exact'),
                                ('txtAccountObjectName', 'Tên khách hàng', CONSUMER_NAME, 'text'),
                            ))
                            proof['verified'] = True
                    elif not consumer:
                        self.state['customer_note'] = 'Người mua khác NTD; khách hàng do người dùng chọn.'
                    if 'add.posted_date' not in self.state['completed']:
                        with self.step('add.posted_date', 'Nhập ngày hạch toán ' + day + '…') as proof:
                            self.fill('dtePostedDate', 'Ngày hạch toán', day)
                            self.verify_fields((('dtePostedDate', 'Ngày hạch toán', day, 'date'),))
                            proof['verified'] = True
                    if 'add.voucher_date' not in self.state['completed']:
                        with self.step('add.voucher_date', 'Nhập ngày chứng từ ' + day + '…') as proof:
                            self.fill('dteRefDate', 'Ngày chứng từ', day)
                            self.verify_fields((
                                ('dtePostedDate', 'Ngày hạch toán', day, 'date'),
                                ('dteRefDate', 'Ngày chứng từ', day, 'date'),
                            ))
                            proof['verified'] = True
                    if 'add.invoice' not in self.state['completed']:
                        with self.step('add.invoice', 'Mở Hóa đơn và nhập số ' + number + '…') as proof:
                            self.desktop.ensure_tab('Hóa đơn')
                            self.fill('txtInvoiceNo', 'Số hóa đơn', number)
                            self.verify_fields((('txtInvoiceNo', 'Số hóa đơn', number, 'invoice_number'),))
                            proof['verified'] = True
                    from .sale_items import SaleItems
                    SaleItems(self).run()
                    message = (f'Đã nhập {len(self.invoice.lines)} dòng hàng của hóa đơn {number}; '
                               'tổng tiền và thuế GTGT đã đọc khớp XML. Chứng từ chưa Cất.')
                    self.publish(state='complete', active_step=None, next_step=message)
                    return
                from .sale_items import SaleItems
                SaleItems(self).run(resume=self.resume_state)
                message = (f'Đã nhập {len(self.invoice.lines)} dòng hàng của hóa đơn {self.invoice.number}; '
                           'tổng tiền và thuế GTGT đã đọc khớp XML. Chứng từ chưa Cất.')
                self.publish(state='complete', active_step=None, next_step=message)
                return
            with self.step('add.prepare', 'Chuẩn bị Thêm từ hóa đơn XML đang chọn…'):
                from .sale_items import SaleItems
                SaleItems.validate(self.invoice)
                day = date.fromisoformat(self.invoice.issued).strftime('%d/%m/%Y')
                number = scalar_text(self.invoice.number)
                if not number.isascii() or not number.isdecimal():
                    raise NeedsAttention('XML không có số hóa đơn hợp lệ.')
                consumer = normalize(self.invoice.buyer.name) == normalize(CONSUMER_NAME)
                self.state['expected'] = {
                    'customer_code': 'NTD' if consumer else None,
                    'posted_date': day, 'voucher_date': day, 'invoice_number': number,
                }
                from .screen_win32 import ScreenDesktop

                self.desktop = ScreenDesktop(self.cancel, ledger=self.ledger)
                self.desktop.checkpoint = self.state['active_step']
                self.desktop.attach_main()
                self.ledger.baseline(self.desktop)
                self.ledger.data['run_id'] = self.state['run_id']
                self.ledger.save()
            with self.step('add.open', 'Bấm Thêm trên danh sách Bán hàng…') as proof:
                self.desktop.click(self.desktop.recovery.prepare_sales())
                self.desktop.wait_window('sale', seconds=15)
                self.state['sale_hwnd'] = self.desktop.window.hwnd
                self.require_sale()
                proof['verified'] = True

            with self.step('add.receipt', 'Mở Phiếu thu và đọc thông tin chung…') as proof:
                self.desktop.ensure_tab('Phiếu thu')
                proof['verified'] = True
            if consumer:
                with self.step('add.customer', 'Nhập khách hàng NTD và nhấn Enter…') as proof:
                    self.fill('cboAccountObjectID', 'Khách hàng', 'NTD', commit=0x0D)
                    self.verify_fields((
                        ('cboAccountObjectID', 'Khách hàng', 'NTD', 'exact'),
                        ('txtAccountObjectName', 'Tên khách hàng', CONSUMER_NAME, 'text'),
                    ))
                    proof['verified'] = True
            else:
                self.state['customer_note'] = 'Người mua khác NTD; khách hàng do người dùng chọn.'

            with self.step('add.posted_date', 'Nhập ngày hạch toán ' + day + '…') as proof:
                self.fill('dtePostedDate', 'Ngày hạch toán', day)
                self.verify_fields((('dtePostedDate', 'Ngày hạch toán', day, 'date'),))
                proof['verified'] = True
            with self.step('add.voucher_date', 'Nhập ngày chứng từ ' + day + '…') as proof:
                self.fill('dteRefDate', 'Ngày chứng từ', day)
                self.verify_fields((
                    ('dtePostedDate', 'Ngày hạch toán', day, 'date'),
                    ('dteRefDate', 'Ngày chứng từ', day, 'date'),
                ))
                proof['verified'] = True

            with self.step('add.invoice', 'Mở Hóa đơn và nhập số ' + number + '…') as proof:
                self.desktop.ensure_tab('Hóa đơn')
                self.fill('txtInvoiceNo', 'Số hóa đơn', number)
                self.verify_fields((('txtInvoiceNo', 'Số hóa đơn', number, 'invoice_number'),))
                proof['verified'] = True
            SaleItems(self).run()
            message = (f'Đã nhập {len(self.invoice.lines)} dòng hàng của hóa đơn {number}; '
                       'tổng tiền và thuế GTGT đã đọc khớp XML. Chứng từ chưa Cất.')
            if not consumer:
                message += ' Khách hàng do bạn chọn.'
            self.publish(state='complete', active_step=None, next_step=message)
        except Exception as error:
            stopped = isinstance(error, Cancelled)
            self.state.update(state='paused' if stopped else 'blocked', error=str(error))
            if self.desktop:
                self.state['last_action'] = deepcopy(self.desktop.last_action)
            if not stopped:
                self.state['next_step'] = 'Đã dừng thao tác; đang lưu log và ảnh sự cố…'
                self.notify_state()
                try:
                    self.state['incident_path'] = self.evidence.incident(self.state, error,
                        runtime=self.runtime, ledger=self.ledger.data, parent=self.parent_incident, cancel=self.cancel)
                except Exception as evidence_error:
                    self.state['incident_collection_error'] = str(evidence_error)
            try:
                failure_next_step = ('Đã dừng thao tác; app đã lưu sự cố cục bộ, giữ nguyên MISA.'
                                     if self.run_mode == 'actual' else
                                     'Đã dừng thao tác; app đang gửi sự cố và chờ Codex sửa source.')
                self.publish(next_step=('Đã dừng; giữ nguyên MISA.' if stopped else
                    failure_next_step))
            except Exception as state_error:
                self.state['state_write_error'] = str(state_error)
            raise
        finally:
            if self.desktop:
                with span('cleanup', 'Đóng bộ đọc sau lượt Thêm'):
                    self.desktop.close()
