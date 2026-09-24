"""Excel invoice entry, live readback comparison and local diagnostics."""
import argparse
import json
import os
import tempfile
from pathlib import Path
import sys
import uuid
import tkinter as tk
from tkinter import ttk, filedialog

from invoice_xml import read_invoice, read_excel, format_money
from source_updates import atomic_json
from automation.glass import ErrorLogView, GlassPanel, PerformanceLogView, WindowChrome
from automation.dashboard import configure_styles
from automation.error_log import ErrorJournal
from automation.performance import source_fingerprint
from automation.storage import prune_generated_storage

BG = '#f3f6fc'
NAVY = '#152644'


def restored_inputs(value):
    if not isinstance(value, dict):
        return {}
    paths = value.get('paths', [])
    return {
        'paths': [p for p in paths if isinstance(p, str) and p] if isinstance(paths, list) else [],
        'selected_path': value.get('selected_path') if isinstance(value.get('selected_path'), str) else None,
    }


class MisaTool(tk.Tk):
    def __init__(self, paths=None, *, host=None, session=None, run_mode='actual', show_ui=True):
        super().__init__()
        self.withdraw()
        prune_generated_storage(force=True)
        self.title('MISA Excel Manager — Nhập hàng & đối chiếu')
        self.geometry('1220x850')
        self.minsize(850, 650)
        self.configure(bg=BG)
        self.update_host = host
        self.run_mode = run_mode if run_mode in ('actual', 'codex_test') else 'actual'
        self.show_ui = bool(show_ui)
        self.source_revision = host.current.revision if host else source_fingerprint()
        self.error_journal = ErrorJournal(self.source_revision, session=(session or {}).get('error_log'))
        self._pending_recovery_errors = []
        self._last_host_error = None
        self.importing = False
        self.closing = False
        self.reloading = False
        self.adding = False
        self._add_task = None
        self._comparison_state = None
        self._comparison_invoice = None
        self._close_after_add = False
        self.control_plane = None
        self._control_desktop = None
        saved_performance = (session or {}).get('performance')
        self._performance_snapshot = dict(saved_performance, historical=True) if isinstance(saved_performance, dict) else None
        self.entries = {}
        self.input_files = []
        self.glass_panel = None
        self._available_bundle = None
        self.selected_path = None
        self.state_file = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'MisaInvoiceTool' / 'excel-input.json'
        self.status = tk.StringVar(value='Chọn file Excel để xem danh sách hóa đơn.')
        self.summary = tk.StringVar(value='Chưa chọn hóa đơn')
        self.parties = tk.StringVar(value='')
        self.source_path = tk.StringVar(value='')
        mode_label = 'CHẾ ĐỘ TEST CODEX' if self.run_mode == 'codex_test' else 'CHẾ ĐỘ THỰC TẾ'
        self.update_status = tk.StringVar(value=mode_label + ' · Source ' + self.source_revision[:12])
        configure_styles(self)
        header = tk.Frame(self, bg=NAVY, padx=22, pady=16)
        header.pack(fill='x')
        tk.Label(header, text='HÓA ĐƠN EXCEL', bg=NAVY, fg='#a9c7ff',
                 font=('Segoe UI', 10, 'bold')).pack(anchor='w')
        tk.Label(header, text='Nhập hàng & đối chiếu MISA', bg=NAVY, fg='white',
                 font=('Segoe UI', 22, 'bold')).pack(anchor='w')
        body = tk.Frame(self, bg=BG, padx=18, pady=14)
        body.pack(fill='both', expand=True)
        footer = tk.Frame(body, bg=BG)
        footer.pack(side='bottom', fill='x')
        controls = tk.Frame(body, bg=BG)
        controls.pack(fill='x', pady=(0, 12))
        self.choose_button = tk.Button(controls, text='Chọn file Excel…', command=self.choose,
                                      bg='#245de5', fg='white', relief='flat', padx=20,
                                      pady=10, font=('Segoe UI', 11, 'bold'))
        self.choose_button.pack(side='left')
        add_label = 'Chạy test Codex' if self.run_mode == 'codex_test' else 'Thêm vào MISA'
        self.add_button = ttk.Button(controls, text=add_label, command=self.start_add, style='Primary.TButton')
        self.add_button.pack(side='left', padx=(10, 0))
        self.stop_add_button = ttk.Button(controls, text='Dừng', command=self.stop_add, style='Stop.TButton')
        self.stop_add_button.pack(side='left', padx=(6, 0))
        self.develop_button = ttk.Button(controls, text='Develop · sao chép goal',
                                         command=self.open_develop_mode)
        self.develop_button.pack(side='left', padx=(6, 0))
        self.hot_reload_button = ttk.Button(controls, text='Hot reload', command=self.request_hot_reload)
        self.hot_reload_button.pack(side='left', padx=10)
        self.files = ttk.Treeview(body, columns=('file', 'number', 'total'),
                                  show='headings', height=3, selectmode='browse')
        for key, label, width in (('file', 'File Excel / nhóm', 620), ('number', 'Số hóa đơn', 150),
                                  ('total', 'Tổng thanh toán', 230)):
            self.files.heading(key, text=label)
            self.files.column(key, width=width, minwidth=80, anchor='e' if key == 'total' else 'w')
        self.files.pack(fill='x')
        self.files.bind('<<TreeviewSelect>>', self.select_file)
        tk.Label(body, textvariable=self.source_path, bg=BG, fg='#637491',
                 anchor='w', wraplength=1060, font=('Segoe UI', 9)).pack(fill='x', pady=(7, 4))
        card = tk.Frame(body, bg='white', padx=12, pady=10)
        card.pack(fill='x')
        tk.Label(card, textvariable=self.summary, bg='white', fg=NAVY, anchor='w',
                 font=('Segoe UI', 13, 'bold')).pack(fill='x')
        tk.Label(card, textvariable=self.parties, bg='white', fg='#52617a', anchor='w',
                 justify='left', wraplength=1040, font=('Segoe UI', 10)).pack(fill='x', pady=(6, 0))
        tabs = ttk.Notebook(body)
        self.tabs = tabs
        tabs.pack(fill='both', expand=True, pady=12)
        # Comparison is opened explicitly as a detached window.
        self.comparison_view = None
        rows_tab, detail_tab = tk.Frame(tabs), tk.Frame(tabs)
        tabs.add(rows_tab, text='Chi tiết hóa đơn trong Excel')
        tabs.add(detail_tab, text='Thông tin hóa đơn')
        self.error_view = ErrorLogView(tabs)
        tabs.add(self.error_view, text='Log lỗi')
        self.performance_view = PerformanceLogView(tabs)
        tabs.add(self.performance_view, text='Hiệu suất')
        self.performance_view.set_snapshot(self._performance_snapshot)
        self.compare_button = ttk.Button(controls, text='Đối chiếu', command=self.open_comparison)
        self.compare_button.pack(side='left', padx=(10, 0))
        columns = ('code', 'name', 'unit', 'quantity', 'price', 'amount', 'tax_rate', 'tax', 'total')
        self.rows = ttk.Treeview(rows_tab, columns=columns, show='headings', height=5)
        for key, label, width in zip(columns,
                ('Mã hàng', 'Tên hàng hóa', 'ĐVT', 'Số lượng', 'Đơn giá', 'Chưa thuế', 'Thuế hàng hóa', 'Tiền thuế', 'Có thuế'),
                (85, 300, 70, 85, 110, 115, 105, 110, 115)):
            self.rows.heading(key, text=label)
            self.rows.column(key, width=width, minwidth=60,
                             anchor='e' if key in ('quantity', 'price', 'amount', 'tax', 'total') else 'w')
        self.rows.grid(row=0, column=0, sticky='nsew')
        ys = ttk.Scrollbar(rows_tab, orient='vertical', command=self.rows.yview)
        xs = ttk.Scrollbar(rows_tab, orient='horizontal', command=self.rows.xview)
        ys.grid(row=0, column=1, sticky='ns')
        xs.grid(row=1, column=0, sticky='ew')
        self.rows.configure(yscrollcommand=ys.set, xscrollcommand=xs.set)
        rows_tab.rowconfigure(0, weight=1)
        rows_tab.columnconfigure(0, weight=1)
        self.details = tk.Text(detail_tab, state='disabled', wrap='word', relief='flat',
                               font=('Segoe UI', 10), padx=12, pady=10)
        self.details.pack(fill='both', expand=True)
        tk.Label(footer, textvariable=self.status, bg=BG, fg=NAVY, anchor='w',
                 font=('Segoe UI', 10)).pack(fill='x')
        tk.Label(footer, textvariable=self.update_status, bg=BG, fg='#637491', anchor='w',
                 font=('Segoe UI', 9)).pack(fill='x', pady=(5, 0))
        self.protocol('WM_DELETE_WINDOW', self.withdraw)
        restored = session
        if restored is None and not paths:
            try:
                restored = json.loads(self.state_file.read_text(encoding='utf-8'))
            except FileNotFoundError:
                restored = {}
            except (OSError, ValueError) as error:
                restored = {}
                self.log_error(error, operation='Đọc danh sách Excel đã lưu',
                               context={'state_file': str(self.state_file)})
        restored = restored_inputs(restored)
        if restored:
            paths = restored.get('paths', [])
            self.selected_path = restored.get('selected_path')
        if paths:
            self.add_files(paths)
        if host:
            self.after(2000, self.check_source_update)
        self.after(100, lambda: WindowChrome(self).decorate())
        if self.show_ui:
            self.show_glass_view()
        self.refresh_controls()
        from automation.recovery import RecoveryController
        from automation.control_plane import ControlPlane
        self.control_plane = ControlPlane()
        self.control_session_id = uuid.uuid4().hex
        self.control_plane.register(self.control_session_id, pid=os.getpid(), mode=self.run_mode)
        self.recovery = RecoveryController(self, (session or {}).get('recovery'))
        self.live_test = None
        if self.run_mode == 'codex_test':
            from automation.live_test import LiveTestSession
            self.live_test = LiveTestSession(self, (session or {}).get('live_test'))
        self.adding = self.recovery.active
        self.record_host_error()
        self.render_error_log(show=bool(self.error_journal.entries))
        self.after(250, self.refresh_performance)
        self.after(80, self.poll_add)
        self.refresh_controls()

    def choose(self):
        if self.adding or self.closing or self.reloading:
            return
        paths = filedialog.askopenfilenames(parent=self.glass_panel, title='Chọn file Excel hóa đơn',
                                           filetypes=[('Excel', '*.xlsx'), ('Excel cũ', '*.xls'), ('Tất cả', '*.*')])
        if paths:
            self.add_files(paths)

    def add_files(self, paths):
        if self.adding or self.importing or self.closing or self.reloading:
            return
        self.importing = True
        self.input_files = list(dict.fromkeys([*self.input_files,
                                               *(str(Path(path).resolve()) for path in paths)]))
        self.refresh_controls()
        pending = iter(paths)
        first = None

        def read_next():
            nonlocal first
            if self.closing:
                return
            path = next(pending, None)
            if path is None:
                self.importing = False
                self.refresh_controls()
                selected = self.selected_path if self.selected_path in self.entries else first
                if selected:
                    self.files.selection_set(selected)
                    self.select_file()
                self.save_inputs()
                return
            path = str(Path(path).resolve())
            try:
                if Path(path).suffix.lower() != '.xlsx':
                    raise ValueError('Chỉ hỗ trợ Excel .xlsx; hãy lưu file .xls thành .xlsx trước khi nhập.')
                invoices = read_excel(path)
                for invoice in invoices:
                    entry_path = invoice.source
                    first = first or entry_path
                    self.entries[entry_path] = (invoice, '')
                    values = (Path(path).name, invoice.number, f'{format_money(invoice.total)} {invoice.currency}')
                    if self.files.exists(entry_path):
                        self.files.item(entry_path, values=values)
                    else:
                        self.files.insert('', 'end', iid=entry_path, values=values)
                self.after(1, read_next)
                return
            except Exception as error:
                self.entries[path] = (None, str(error))
                values = (Path(path).name, 'Không đọc được', '')
                self.log_error(error, operation='Đọc hóa đơn Excel', path=path)
            if self.files.exists(path):
                self.files.item(path, values=values)
            else:
                self.files.insert('', 'end', iid=path, values=values)
            self.after(1, read_next)
        read_next()

    def select_file(self, _event=None):
        selected = self.files.selection()
        if self.adding:
            if selected != (self.selected_path,) and self.selected_path:
                self.files.selection_set(self.selected_path)
            return
        if not selected:
            return
        self.selected_path = selected[0]
        if getattr(self, 'live_test', None) is not None:
            self.live_test.selected(self.selected_path)
        invoice, error = self.entries[self.selected_path]
        self.source_path.set(self.selected_path)
        self.rows.delete(*self.rows.get_children())
        if invoice:
            day = '/'.join(reversed(invoice.issued.split('-'))) if invoice.issued else ''
            self.summary.set(f'HĐ {invoice.number} · {day or "Excel"} · {format_money(invoice.total)} {invoice.currency}')
            self.parties.set(f'Người mua: {invoice.buyer.name or "—"} | MST: {invoice.buyer.tax_code or "—"}\n'
                             'Nguồn: Excel · các dòng cùng số hóa đơn được gom thành một hóa đơn')
            for line in invoice.lines:
                self.rows.insert('', 'end', values=(line.code, line.name, line.unit,
                    format_money(line.quantity), format_money(line.unit_price),
                    format_money(line.amount), line.tax_rate or '—', format_money(line.tax),
                    format_money((line.amount or 0) + (line.tax or 0))))
            detail = '\n'.join([
                f'Loại hóa đơn: {invoice.invoice_type}', 'Nguồn dữ liệu: Excel',
                f'MST người mua: {invoice.buyer.tax_code or "—"}',
                f'Tổng trước thuế: {format_money(invoice.before_tax)}',
                f'Thuế GTGT: {format_money(invoice.tax)}',
                f'Tổng thanh toán: {format_money(invoice.total)} {invoice.currency}',
                '', 'Ghi chú trong Excel:', *(invoice.notes or ('Không có.',)),
                '', 'Đối chiếu dữ liệu:', *(invoice.warnings or ('Không có cảnh báo.',))])
            self.status.set(f'Đã chọn hóa đơn {invoice.number} · {len(invoice.lines)} dòng hàng · {len(self.entries)} hóa đơn')
        else:
            self.summary.set('Không đọc được Excel')
            self.parties.set('')
            detail = error
            self.status.set(error)
        self.details.configure(state='normal')
        self.details.delete('1.0', 'end')
        self.details.insert('1.0', detail)
        self.details.configure(state='disabled')
        self.render_invoice()
        if self.glass_panel and self.glass_panel.comparison_view is not None:
            self.glass_panel.comparison_view.set_invoices(self.entries, self.selected_path)
        self.save_inputs()

    def input_state(self):
        return {'paths': list(self.input_files), 'selected_path': self.selected_path}

    def start_add(self, *, from_live_test=False):
        if self.run_mode == 'codex_test' and not from_live_test:
            self.status.set('Phiên test Codex chỉ nhận lệnh start đã xác thực từ MCP.')
            return
        if self.adding or self.importing or self.closing or self.reloading:
            return
        invoice = self.entries.get(self.selected_path, (None, ''))[0]
        if invoice is None:
            self.status.set('Chọn hóa đơn Excel hợp lệ trước khi bấm Thêm.')
            return
        self._comparison_invoice = invoice
        self._comparison_state = None
        self._performance_snapshot = None
        self.performance_view.live.set(True)
        self.performance_view.text.tag_remove('sel', '1.0', 'end')
        self.performance_view.set_snapshot(None)
        if self.glass_panel:
            self.glass_panel.performance_view.live.set(True)
            self.glass_panel.performance_view.text.tag_remove('sel', '1.0', 'end')
            self.glass_panel.performance_view.set_snapshot(None)
        try:
            self.recovery.start(invoice)
        except Exception as error:
            self.log_error(error, operation='Khởi động Thêm', path=invoice.source)
            self.recovery.callback_failed(error)
            self.status.set(str(error))
        self.refresh_controls()

    def stop_add(self, *, from_live_test=False):
        self._pending_recovery_errors.clear()
        was_active = self.adding or bool(getattr(getattr(self, 'recovery', None), 'active', False))
        if hasattr(self, 'recovery'):
            self.recovery.stop()
            self.status.set('Đang dừng workflow…' if was_active else 'Không có workflow đang chạy.')
        if getattr(self, 'live_test', None) is not None:
            if was_active:
                self.live_test.publish(state='stopping', phase='stopping',
                                       message='Đang dừng Codex test workflow.')
            else:
                self.live_test.publish(state='ready', phase='ready',
                                       message='Codex test session ready.')

    def open_develop_mode(self):
        """Attach Codex control to this manager after an explicit user action."""
        if self.run_mode == 'codex_test' or self.adding or self.importing or self.closing or self.reloading:
            return
        if self.update_host is None:
            self.status.set('Develop cần chạy manager từ launcher có hot reload.')
            return
        recovery_entered = False
        try:
            from automation.live_test import (LiveTestSession, _manager_alive,
                                              codex_controller_prompt, read_session)
            from automation.paths import project_root
            root = project_root()
            if root is None:
                raise RuntimeError('Không tìm thấy source root để mở Codex test.')
            try:
                current = read_session()
            except RuntimeError:
                current = None
            if (current and current.get('mode') == 'codex_test'
                    and current.get('manager_pid') != os.getpid()
                    and _manager_alive(current)):
                raise RuntimeError('Một phiên MISA Codex test khác đang chạy.')
            self.save_inputs()
            self.recovery.enter_external_live_test()
            recovery_entered = True
            self.run_mode = 'codex_test'
            self.update_host.codex_test_authorized = True
            self.live_test = LiveTestSession(self)
            self.live_test.selected(self.selected_path)
            prompt = codex_controller_prompt(self.live_test.session_id)
            self.clipboard_clear()
            self.clipboard_append(prompt)
            self.update()
            self.status.set('Đã sao chép goal; mở CMD tại C:\\misa, chạy Codex rồi nhấn Ctrl+V.')
            self.update_status.set('TEST CODEX · Session ' + self.live_test.session_id[:12])
        except Exception as error:
            if self.live_test is not None:
                self.live_test.close()
                self.live_test = None
            if recovery_entered:
                self.recovery.leave_external_live_test()
            self.update_host.codex_test_authorized = False
            self.run_mode = 'actual'
            self.log_error(error, operation='Mở chế độ Develop', dispatch_codex=False)
            self.status.set('Không mở được chế độ Develop: ' + str(error))
        self.refresh_controls()

    def refresh_performance(self):
        if self.closing:
            return
        try:
            if self._add_task and self._add_task.performance:
                self._performance_snapshot = self._add_task.performance.snapshot()
            self.performance_view.set_snapshot(self._performance_snapshot)
            if self.glass_panel:
                self.glass_panel.performance_view.set_snapshot(self._performance_snapshot)
        finally:
            self.after(250, self.refresh_performance)

    def poll_add(self):
        try:
            if self.control_plane is not None:
                self.control_plane.poll(self)
            if getattr(self, 'live_test', None) is not None:
                self.live_test.poll()
            if self._pending_recovery_errors and not (self.importing or self.reloading or self.closing):
                error, operation = self._pending_recovery_errors[0]
                if self.recovery.active:
                    self.recovery.callback_failed(error)
                else:
                    self.recovery.start(None, error=error, operation=operation)
                self._pending_recovery_errors.clear()
            self.recovery.poll()
            if not self.closing:
                self.refresh_controls()
        finally:
            if not self.closing:
                self.after(80, self.poll_add)

    def dispatch_control(self, request):
        """Execute one MCP control request on the Tk/main thread."""
        from automation import win32
        from automation.screen_win32 import ScreenDesktop

        payload = request.get('payload') or {}
        action = request.get('action')
        def response(*, status='completed', target=None, value=None, readback=None,
                     workflow_state=None, error=None, **extra):
            state = workflow_state if workflow_state is not None else self.recovery.snapshot()
            try:
                json.dumps(state, ensure_ascii=False)
            except (TypeError, ValueError):
                state = {key: state.get(key) for key in
                         ('active', 'phase', 'mode', 'incident', 'incident_id',
                          'source_revision', 'workflow', 'error')
                         if isinstance(state, dict) and key in state}
            result = {'status': status, 'action': action, 'target': target,
                      'value': value, 'readback': readback,
                      'workflow_state': state,
                      'error': error}
            result.update(extra)
            return result

        if action == 'execute_workflow':
            command = payload.get('command')
            if command == 'start':
                self.start_add(from_live_test=True)
            elif command == 'stop':
                self.stop_add(from_live_test=True)
            elif command == 'reload':
                self.request_hot_reload()
            elif command == 'resume':
                if self.recovery.pending_error is not None:
                    self.recovery.resume_after_callback()
                elif self.recovery.active:
                    self.recovery.poll()
                else:
                    raise RuntimeError('Không có workflow đang chờ tiếp tục.')
            elif command == 'reload_source':
                self.request_hot_reload()
            else:
                raise ValueError('Unsupported workflow command: ' + str(command))
            return response(target=command, value=command,
                            workflow_state=self.recovery.snapshot())

        if action == 'focus':
            desktop = self._control_desktop
            if desktop is None:
                desktop = ScreenDesktop(__import__('threading').Event())
                desktop.direct_control = True
                self._control_desktop = desktop
            desktop.direct_control = True
            desktop.attach()
            return response(target='misa-window', readback={
                'hwnd': desktop.window.hwnd, 'pid': desktop.window.pid,
                'title': desktop.window.title, 'foreground': win32.is_foreground(desktop.window)},
                workflow_state=self.recovery.snapshot())

        if action == 'edit_xml':
            path = Path(payload.get('path') or self.selected_path or '').resolve()
            if not path.is_file() or path not in {Path(value) for value in self.entries}:
                raise ValueError('Excel path is not one of the files loaded by this manager.')
            old = payload.get('old_text')
            new = payload.get('new_text', '')
            if not isinstance(old, str) or not old:
                raise ValueError('edit_xml requires non-empty old_text.')
            if not isinstance(new, str) or len(new) > 10 * 1024 * 1024:
                raise ValueError('edit_xml replacement is invalid or too large.')
            raw = path.read_text(encoding='utf-8')
            count = raw.count(old)
            if count != 1:
                raise ValueError(f'edit_xml expected one exact match, found {count}.')
            updated = raw.replace(old, new, 1)
            fd, temporary = tempfile.mkstemp(prefix=path.name + '.', suffix='.tmp',
                                             dir=str(path.parent))
            try:
                with os.fdopen(fd, 'w', encoding='utf-8', newline='') as stream:
                    stream.write(updated)
                    stream.flush()
                    os.fsync(stream.fileno())
                os.replace(temporary, path)
            finally:
                if os.path.exists(temporary):
                    os.unlink(temporary)
            invoice = read_invoice(str(path))
            self.entries[str(path)] = (invoice, '')
            if self.selected_path == str(path):
                self.select_file()
            return response(target=str(path), value=new,
                            readback={'replacement_count': 1, 'path': str(path)},
                            workflow_state=self.recovery.snapshot())

        if action == 'observe':
            desktop = self._control_desktop
            if desktop is None or desktop.window is None:
                desktop = ScreenDesktop(__import__('threading').Event())
                desktop.direct_control = True
                desktop.background_control = True
                desktop.attach()
                self._control_desktop = desktop
            desktop.direct_control = True
            desktop.background_control = True
            desktop.sync()
            if (desktop.background_control
                    and (win32.user32.IsIconic(desktop.window.hwnd)
                         or not win32.is_foreground(desktop.window))
                    and not payload.get('capture', False)):
                return response(status='completed', target='screen',
                                readback={**desktop.background_observe(),
                                          'native_inspection': desktop.deep_native_observe()},
                                workflow_state=self.recovery.snapshot(),
                                last_action=dict(desktop.last_action))
            kind = next((name for name in ('main', 'sale', 'find', 'new_item')
                         if desktop.is_window(name)), None)
            desktop._native_frame(kind, fresh=True)
            elements = []
            try:
                if desktop._uia_readback is not None:
                    elements = desktop._uia_readback.query(desktop)
            except Exception as error:
                desktop.last_action['native_query_error'] = str(error)
            return response(status='completed', target='screen', readback={
                'window': {
                    'hwnd': desktop.window.hwnd, 'pid': desktop.window.pid,
                    'title': desktop.window.title,
                }, 'frame_id': desktop._frame_id,
                'captured_at': desktop._captured_at.isoformat(),
                'image_size': list(desktop._frame[1]),
                'words': [{'text': word.text, 'box': list(word.box)}
                          for word in desktop._frame[3]],
                'elements': elements,
                'native_inspection': desktop.deep_native_observe(),
                'last_action': dict(desktop.last_action),
            })

        allow_minimized = bool(payload.get('allow_minimized', True))
        desktop = self._control_desktop
        if desktop is None or desktop.window is None:
            desktop = ScreenDesktop(__import__('threading').Event())
            desktop.direct_control = True
            desktop.background_control = allow_minimized
            desktop.attach()
            self._control_desktop = desktop
        desktop.direct_control = True
        desktop.background_control = allow_minimized
        desktop.sync()
        minimized = bool(win32.user32.IsIconic(desktop.window.hwnd))
        semantic_background = bool(desktop.background_control)
        background_readback = None
        background_action_used = False

        target = payload.get('target')
        if isinstance(target, dict) and 'type' not in target:
            kind = str(target.get('kind') or '').lower()
            if kind == 'tab':
                target = {'type': 'tab', 'text': target.get('name') or target.get('text')}
            elif kind in ('edit', 'combo', 'field') or target.get('id'):
                if kind in ('button', 'checkbox', 'native_control'):
                    target = {'type': 'native_control', 'id': target.get('id', ''),
                              'name': target.get('name') or target.get('label', ''),
                              'kind': int(target.get('controlType') or target.get('kind') or 50000)}
                else:
                    target = {'type': 'field', 'id': target.get('id', ''),
                              'label': target.get('name') or target.get('label', '')}
            else:
                target = {'type': 'word', 'text': target.get('name') or target.get('text', ''),
                          'kind': int(target.get('controlType') or 50000)}

        if action == 'click':
            coordinates = payload.get('coordinates')
            if coordinates is not None:
                if semantic_background and not win32.is_foreground(desktop.window):
                    raise ValueError('Click tọa độ cần MISA foreground; dùng target semantic UIA để chạy nền.')
                win32.direct_click(desktop.window, coordinates,
                                   coordinate_space=payload.get('coordinate_space', 'window'))
            else:
                if not isinstance(target, dict):
                    raise ValueError('click requires a semantic native target or coordinates.')
                if semantic_background:
                    background_readback = desktop.background_action('click', target)
                    background_action_used = True
                else:
                    control = desktop._resolve_target(target)
                    desktop.click(control)
        elif action == 'type':
            coordinates = payload.get('coordinates')
            if coordinates is not None:
                if semantic_background and not win32.is_foreground(desktop.window):
                    raise ValueError('Nhập theo tọa độ cần MISA foreground; dùng target field UIA để chạy nền.')
                win32.direct_click(desktop.window, coordinates,
                                   coordinate_space=payload.get('coordinate_space', 'window'))
                win32.direct_text(desktop.window, str(payload.get('value', '')))
            else:
                if not isinstance(target, dict):
                    raise ValueError('type requires a semantic native target or coordinates.')
                if semantic_background:
                    background_readback = desktop.background_action('set', target, str(payload.get('value', '')))
                    background_action_used = True
                else:
                    control = desktop._resolve_target(target)
                    desktop.set(control, str(payload.get('value', '')))
        elif action == 'press_key':
            if minimized:
                raise ValueError('MISA đang thu nhỏ; press_key cần cửa sổ foreground.')
            win32.direct_keypress(desktop.window, int(payload['vk']),
                                  ctrl=bool(payload.get('ctrl')),
                                  shift=bool(payload.get('shift')),
                                  alt=bool(payload.get('alt')))
        elif action == 'scroll':
            ticks = int(payload.get('ticks', 0))
            coordinates = payload.get('coordinates')
            if coordinates is not None:
                if semantic_background and not win32.is_foreground(desktop.window):
                    raise ValueError('Scroll tọa độ cần MISA foreground; dùng target semantic UIA để chạy nền.')
                point = coordinates
                if payload.get('coordinate_space', 'screen') != 'screen':
                    rect = win32.window_rect(desktop.window)
                    point = [rect[0] + int(coordinates[0]), rect[1] + int(coordinates[1])]
                win32.direct_scroll(desktop.window, point, ticks)
            else:
                if not isinstance(target, dict):
                    raise ValueError('scroll requires a target or coordinates.')
                if semantic_background:
                    if ticks == 0:
                        raise ValueError('scroll requires nonzero ticks.')
                    direction = 'down' if ticks < 0 else 'up'
                    background_readback = desktop.background_action('scroll', target,
                                                                     direction + ':' + str(min(20, abs(ticks))))
                    background_action_used = True
                else:
                    desktop.scroll_inventory(target, ticks)
        elif action == 'select_tab':
            if semantic_background:
                background_readback = desktop.background_action('select',
                                                                 {'type': 'tab', 'text': str(payload['name'])})
                background_action_used = True
            else:
                desktop.tab(str(payload['name']))
        elif action == 'save_or_discard':
            intent = str(payload.get('intent', ''))
            coordinates = payload.get('coordinates')
            if coordinates is not None:
                if minimized:
                    raise ValueError('MISA đang thu nhỏ; save/discard theo tọa độ không thể chạy nền.')
                win32.direct_click(desktop.window, coordinates,
                                   coordinate_space=payload.get('coordinate_space', 'window'))
            else:
                captions = {
                    'save': 'Cất', 'discard': 'Bỏ qua', 'cancel': 'Hủy bỏ',
                    'close': 'Đóng', 'confirm': 'Đồng ý',
                }
                target = {'type': 'word', 'text': captions.get(intent, intent), 'kind': 50000}
                if semantic_background:
                    background_readback = desktop.background_action('click', target)
                    background_action_used = True
                else:
                    control = desktop._resolve_target(target)
                    desktop.click(control)
        else:
            raise ValueError('Unsupported MISA control action: ' + str(action))
        # Capture a fresh native frame after input. This is diagnostic readback;
        # the direct path does not infer success from SendInput alone.
        readback = None
        try:
            if background_action_used:
                return response(target=target, value=payload.get('value'), readback=background_readback,
                                workflow_state=self.recovery.snapshot(),
                                last_action=dict(desktop.last_action))
            desktop.sync()
            kind = next((name for name in ('main', 'sale', 'find', 'new_item')
                         if desktop.is_window(name)), None)
            if kind:
                desktop._native_frame(kind, fresh=True)
                readback = {'frame_id': desktop._frame_id,
                            'words': [{'text': word.text, 'box': list(word.box)}
                                      for word in desktop._frame[3]],
                            'last_action': dict(desktop.last_action)}
        except Exception as error:
            readback = {'error': f'{type(error).__name__}: {error}'}
        return response(target=target, value=payload.get('value'), readback=readback,
                        workflow_state=self.recovery.snapshot(),
                        last_action=dict(desktop.last_action))

    def log_error(self, error, *, operation, path=None, context=None,
                  dispatch_codex=None, stop_active=False, **details):
        path = self.selected_path if path is None else path
        invoice = self.entries.get(path, (None, ''))[0]
        data = {'excel_source': path, 'application_source': __file__}
        if invoice:
            data['invoice_from_excel'] = {
                'number': invoice.number, 'series': invoice.series, 'issued': invoice.issued,
                'seller': invoice.seller.name, 'buyer': invoice.buyer.name,
                'line_count': len(invoice.lines), 'before_tax': invoice.before_tax,
                'tax': invoice.tax, 'total': invoice.total, 'currency': invoice.currency,
            }
        data.update(context or {})
        self.error_journal.record(error, operation=operation, context=data, **details)
        # Actual mode only records and displays the failure. Recovery dispatch is
        # reserved for an explicitly launched and externally claimed Codex test.
        if dispatch_codex is None:
            dispatch_codex = self.run_mode == 'codex_test'
        if self.run_mode != 'codex_test':
            dispatch_codex = False
        recovery = getattr(self, 'recovery', None)
        if stop_active and recovery is not None and recovery.active:
            # An explicit UI callback failure stops the active workflow without
            # opening Codex in actual mode.
            recovery.callback_failed(error)
        elif dispatch_codex and self.run_mode == 'codex_test':
            self._pending_recovery_errors.append((error, operation))
        self.render_error_log(show=True)

    def render_error_log(self, *, show=False):
        view = getattr(self, 'error_view', None)
        if view:
            view.set_report(self.error_journal)
            if show:
                self.tabs.select(view)
        if self.glass_panel:
            self.glass_panel.error_view.set_report(self.error_journal)
            if show:
                self.glass_panel.notebook.select(self.glass_panel.error_view)

    def report_callback_exception(self, exc_type, error, tb):
        self.importing = False
        self.log_error(error, operation='Callback giao diện', stop_active=True, tb=tb)
        self.status.set('Có lỗi giao diện; xem Log lỗi.' if self.run_mode == 'actual' else
                        'Có lỗi giao diện; phiên Codex test đang thu thập sự cố.')
        self.refresh_controls()

    def record_host_error(self):
        if self.update_host is None or not self.update_host.error:
            self._last_host_error = None
            return
        details = getattr(self.update_host, 'last_error', None) or {}
        identity = (self.update_host.error, details.get('at'))
        if identity == self._last_host_error:
            return
        self._last_host_error = identity
        self.log_error(RuntimeError(self.update_host.error), operation=details.get('operation', 'Nạp source'),
                       error_type=details.get('type'), occurred_at=details.get('at'),
                       trace_text=details.get('traceback', 'Host cũ không cung cấp traceback.'),
                       source_revision=details.get('source_revision'))

    def refresh_controls(self):
        busy = self.adding or self.importing or self.closing or self.reloading
        invoice = self.entries.get(self.selected_path, (None, ''))[0]
        add_label = 'Chạy test Codex' if self.run_mode == 'codex_test' else 'Thêm vào MISA'
        self.add_button.configure(text=add_label)
        self.choose_button.configure(state='disabled' if busy else 'normal')
        codex_locked = self.run_mode == 'codex_test'
        self.add_button.configure(state='disabled' if busy or invoice is None or codex_locked else 'normal')
        self.stop_add_button.configure(state='normal' if self.adding else 'disabled')
        self.hot_reload_button.configure(state='disabled' if busy or codex_locked or self.update_host is None else 'normal')
        if hasattr(self, 'develop_button'):
            self.develop_button.configure(state='disabled' if busy or codex_locked else 'normal')
        if self.glass_panel:
            self.glass_panel.refresh_buttons()
        if hasattr(self, 'compare_button'):
            self.compare_button.configure(state='disabled' if busy else 'normal')

    def render_invoice(self):
        self.render_comparison()
        if self.glass_panel:
            self.glass_panel.render()
        self.refresh_controls()

    def comparison_state_for(self, invoice):
        return self._comparison_state if invoice is not None and invoice is self._comparison_invoice else None

    def render_comparison(self):
        # The detached comparison window owns its own current-screen scanner.
        # Workflow state must not overwrite a live observation or make the
        # table update as a side effect of unrelated tool UI refreshes.
        if self.glass_panel and self.glass_panel.comparison_view is not None:
            self.glass_panel.comparison_view.set_invoices(self.entries, self.selected_path)
            self.glass_panel.render()

    def open_comparison(self):
        if self.glass_panel is None:
            self.show_glass_view()
        if self.glass_panel is not None:
            self.glass_panel.open_comparison()

    def show_glass_view(self):
        if self.closing:
            return
        self.show_ui = True
        if self.glass_panel is None:
            self.glass_panel = GlassPanel(self)
        self.glass_panel.show()
        self.withdraw()

    def save_inputs(self):
        try:
            atomic_json(self.state_file, self.input_state())
            return True
        except OSError as error:
            self.status.set('Đã đọc Excel; chưa lưu được danh sách file trên máy.')
            self.log_error(error, operation='Lưu danh sách Excel',
                           context={'state_file': str(self.state_file)})
            return False

    def check_source_update(self):
        if self.closing:
            return
        self.after(750, self.check_source_update)
        if not self.adding and not self.importing and not self.reloading:
            bundle = self.update_host.candidate()
            self._available_bundle = bundle
            mode_label = 'TEST CODEX' if self.run_mode == 'codex_test' else 'THỰC TẾ'
            if bundle:
                suffix = (' · chỉ nạp qua handoff Codex đã xác thực.' if self.run_mode == 'codex_test'
                          else ' · bấm Hot reload để áp dụng.')
                self.update_status.set(mode_label + ' · Có source mới ' + bundle.revision[:12] + suffix)
            elif not self.update_host.error:
                suffix = (' · source chỉ đổi qua handoff Codex.' if self.run_mode == 'codex_test'
                          else ' · Hot reload để nạp lại toàn bộ source.')
                self.update_status.set(mode_label + ' · Source ' + self.update_host.current.revision[:12] + suffix)
            if self.glass_panel:
                self.glass_panel.refresh_buttons()
            if self.update_host.error:
                self.update_status.set('Chưa nhận cập nhật: ' + self.update_host.error)
            self.record_host_error()

    def request_hot_reload(self):
        if self.run_mode == 'codex_test':
            self.status.set('Phiên test Codex chỉ nạp source qua handoff đã xác thực.')
            return
        if self.adding or self.closing or self.reloading or self.update_host is None:
            return
        self.reloading = True
        self.update_status.set('Đang chờ đọc Excel xong để nạp lại source…')
        self.refresh_controls()
        self.after(0, self.prepare_hot_reload)

    def prepare_hot_reload(self):
        if self.closing:
            return
        if self.adding or self.importing:
            self.after(50, self.prepare_hot_reload)
            return
        try:
            import source_updates
            from automation.paths import project_root

            configured = os.environ.get('MISA_SOURCE_ROOT')
            if configured:
                root = Path(configured).expanduser().resolve()
            elif not getattr(sys, 'frozen', False):
                root = Path(source_updates.__file__).resolve().parent
            else:
                root = project_root()
            if root is not None:
                bundle = source_updates.DirectSource(root)
            else:
                bundle = self.update_host.candidate(force=True)
                if bundle is None and self.update_host.error:
                    raise ValueError(self.update_host.error)
                bundle = bundle or self.update_host.current
            if hasattr(bundle, 'raw'):
                bundle = source_updates.Bundle(bundle.raw,
                    requirements_hash=self.update_host.requirements_hash, expected=bundle.revision)
            if (hasattr(bundle, 'requirements_hash')
                    and bundle.requirements_hash != self.update_host.requirements_hash):
                raise ValueError('Dependency/runtime đã đổi. Cài requirements.txt và khởi động lại app; '
                                 'EXE cần đóng gói lại. Hot reload chỉ nạp source.')
        except Exception as error:
            self.reloading = False
            self.log_error(error, operation='Hot reload source')
            self.update_status.set('Không nạp được source: ' + str(error))
            self.status.set('Hot reload chưa thành công: ' + str(error))
            self.refresh_controls()
            return
        if not self.save_inputs():
            self.reloading = False
            self.refresh_controls()
            return
        self.update_host.request(bundle, dict(self.input_state(), error_log=self.error_journal.snapshot(),
                                             performance=self._performance_snapshot,
                                             run_mode=self.run_mode,
                                             live_test=self.live_test.snapshot() if self.live_test else None))
        if self.live_test is not None:
            self.live_test.close(preserve=True)
        self.closing = True
        self.destroy()

    def close(self):
        if self.closing:
            return
        if self.adding:
            self._close_after_add = True
            self.stop_add()
            return
        if self.reloading:
            return
        if not self.save_inputs():
            return
        if self.live_test is not None:
            self.live_test.close()
        self.closing = True
        self.destroy()

    def destroy(self):
        for timer in self.tk.call('after', 'info'):
            self.after_cancel(timer)
        super().destroy()


def main(argv=None, *, host=None, session=None):
    # Windows may launch the standalone tool with a legacy cp1252 stream;
    # argparse help contains Vietnamese text.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding='utf-8')
        except (AttributeError, ValueError):
            pass
    parser = argparse.ArgumentParser(description='Đọc và đối chiếu dữ liệu hóa đơn Excel.')
    parser.add_argument('--xml', nargs='+', help=argparse.SUPPRESS)
    parser.add_argument('--excel', nargs='+')
    parser.add_argument('--codex-test', action='store_true',
                        help='Mở phiên test do Codex CLI điều phối; lỗi được giữ để agent sửa và tiếp tục')
    visibility = parser.add_mutually_exclusive_group()
    visibility.add_argument('--headless', action='store_true',
                            help='Chạy ẩn qua MCP khi không cần giao diện tương tác')
    visibility.add_argument('--show-ui', action='store_true',
                            help='Hiển thị panel MISA XML Manager (mặc định)')
    args = parser.parse_args(argv)
    os.environ['MISA_HEADLESS'] = '1' if args.headless else '0'
    if host is None:
        import source_updates

        root = Path(source_updates.__file__).resolve().parent
        baseline = source_updates.DirectSource(root)
        requirements_hash = source_updates.runtime_requirements_hash((root / 'requirements.txt').read_bytes())
        return source_updates.UpdateHost(root / 'release' / 'updates', baseline, requirements_hash).run(
            sys.argv[1:] if argv is None else argv, prefer_baseline=True)
    from automation import win32
    win32.enable_dpi_awareness()
    # A saved reload session is data, not authorization to enter developer mode.
    # The explicit flag or an in-process Develop transition enables the control plane.
    run_mode = ('codex_test' if args.codex_test or
                getattr(host, 'codex_test_authorized', False) else 'actual')
    application = MisaTool(args.excel or args.xml, host=host, session=session,
                           run_mode=run_mode, show_ui=not args.headless)
    if host:
        host.ready()
    try:
        application.mainloop()
    finally:
        application.recovery.shutdown()
        if application.live_test is not None:
            application.live_test.close(preserve=application.reloading)
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        from automation.glass import show_startup_error

        show_startup_error(error)
        sys.exit(1)

