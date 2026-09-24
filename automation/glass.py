"""Fixed, fully legible floating panel; native changes affect only our own HWND."""
import ctypes as C
from ctypes import wintypes as W
import os
import sys
from pathlib import Path
import tkinter as tk
from tkinter import ttk

from invoice_xml import format_money
from .dashboard import (BG, SURFACE, INK, MUTED, COLORS, ComparisonView,
                        ErrorLogView, PerformanceLogView)
from .comparison import comparison_rows, ScreenComparisonScanner, sample_rows


class WindowChrome:
    """Optional Windows 11 corners for the tool's own window."""

    def __init__(self, widget):
        self.widget = widget
        self.user = C.WinDLL('user32', use_last_error=True)
        self.user.GetAncestor.argtypes, self.user.GetAncestor.restype = [W.HWND, W.UINT], W.HWND
        self.user.GetWindowThreadProcessId.argtypes = [W.HWND, C.POINTER(W.DWORD)]

    def hwnd(self):
        handle = self.user.GetAncestor(self.widget.winfo_id(), 2)
        pid = W.DWORD()
        self.user.GetWindowThreadProcessId(handle, C.byref(pid))
        if not handle or pid.value != os.getpid():
            raise RuntimeError('Chỉ được thay giao diện cửa sổ của tool.')
        return handle

    def decorate(self):
        try:
            api = C.WinDLL('dwmapi')
            api.DwmSetWindowAttribute.argtypes = [W.HWND, W.DWORD, C.c_void_p, W.DWORD]
            handle = self.hwnd()
            for attribute, value in [(33, 2), (34, 0xF5EADF)]:
                data = C.c_int(value)
                api.DwmSetWindowAttribute(handle, attribute, C.byref(data), C.sizeof(data))
        except (OSError, AttributeError, RuntimeError):
            pass  # Older Windows keeps the standard window frame.

def show_startup_error(error):
    from .error_log import ErrorJournal

    previous = getattr(tk, '_default_root', None)
    journal = getattr(previous, 'error_journal', None) or ErrorJournal()
    journal.record(error, operation='Khởi động ứng dụng')
    try:
        if sys.stderr is not None:
            sys.stderr.write(journal.render() + '\n')
    except (OSError, ValueError):
        pass
    # Explicit headless MCP runs must never create a visible Tk error window.
    if os.environ.get('MISA_HEADLESS', '0').strip().casefold() not in ('0', 'false', 'no'):
        if previous is not None:
            recovery = getattr(previous, 'recovery', None)
            if recovery is not None:
                recovery.shutdown()
            try:
                previous.destroy()
            except tk.TclError:
                pass
        return
    if previous is not None:
        recovery = getattr(previous, 'recovery', None)
        if recovery is not None:
            recovery.shutdown()
        try:
            previous.destroy()
        except tk.TclError:
            pass
    window = tk.Tk()
    window.title('MISA Excel Manager — Log lỗi khởi động')
    window.geometry('900x620')
    view = ErrorLogView(window)
    view.pack(fill='both', expand=True)
    view.set_report(journal)
    status = tk.StringVar(value=(
        'Lỗi khởi động đã được ghi cục bộ. Tool không tự mở Codex; '
        'nếu đang test, Codex CLI đã khởi chạy tiến trình sẽ nhận exit/error này.'))
    ttk.Label(window, textvariable=status, wraplength=850).pack(fill='x', padx=12, pady=8)
    ttk.Button(window, text='Đóng', command=window.destroy).pack(pady=(0, 10))
    window.protocol('WM_DELETE_WINDOW', window.destroy)
    window.mainloop()


class GlassPanel(tk.Toplevel):
    BG, CARD, INK, MUTED = BG, SURFACE, INK, MUTED

    def __init__(self, owner):
        super().__init__(owner)
        self.withdraw()
        self.owner = owner
        self.title('MISA · Hóa đơn Excel')
        self.configure(bg=self.BG)
        screen_width = self.winfo_screenwidth()
        screen_height = self.winfo_screenheight()
        panel_width = round(min(460, max(380, screen_width * .22)))
        height = min(980, screen_height - 70)
        self.geometry(f'{panel_width}x{height}+{max(0, screen_width-panel_width-10)}+10')
        self.minsize(360, min(650, height))
        self.protocol('WM_DELETE_WINDOW', owner.close)
        self.chrome = WindowChrome(self)
        self._invoice_identity = None
        self.invoice_title = tk.StringVar(value='Chọn một hóa đơn Excel')
        self.mode = tk.StringVar(value='SẴN SÀNG')

        top = tk.Frame(self, bg=self.BG, padx=16, pady=10)
        top.pack(fill='x')
        tk.Label(top, text='MISA  /  HÓA ĐƠN EXCEL', bg=self.BG, fg=self.MUTED,
                 font=('Segoe UI', 9, 'bold')).pack(side='left')
        self.badge = tk.Label(top, textvariable=self.mode, bg='#d6eee9', fg='#196f5c',
                              padx=9, pady=3, font=('Segoe UI', 8, 'bold'))
        self.badge.pack(side='right')
        tk.Label(self, textvariable=self.invoice_title, bg=self.BG, fg=self.INK,
                 font=('Segoe UI', 16, 'bold'), padx=16, anchor='w').pack(fill='x')
        source = tk.Frame(self, bg=self.BG)
        source.pack(fill='x', padx=14, pady=(2, 5))
        self.choose = ttk.Button(source, text='Chọn Excel…', command=owner.choose)
        self.choose.pack(side='left', padx=(0, 7))
        self.file_choice = ttk.Combobox(source, state='readonly')
        self.file_choice.pack(side='left', fill='x', expand=True)
        self.file_choice.bind('<<ComboboxSelected>>', self.select_file)
        self._file_paths = []
        self._file_labels = []

        self.total_values = {}
        total_frame = tk.Frame(self, bg=self.BG)
        total_frame.pack(fill='x', padx=14, pady=(3, 5))
        for key, title in [('goods', 'TIỀN HÀNG'), ('tax', 'THUẾ GTGT'), ('payment', 'THANH TOÁN')]:
            box = tk.Frame(total_frame, bg=self.CARD, padx=9, pady=7, highlightthickness=1,
                           highlightbackground='white')
            box.pack(side='left', fill='both', expand=True, padx=2)
            tk.Label(box, text=title, bg=self.CARD, fg=self.MUTED,
                     font=('Segoe UI', 8, 'bold')).pack(anchor='w')
            expected = tk.Label(box, text='—', bg=self.CARD, fg=self.INK,
                                font=('Segoe UI', 11, 'bold'))
            expected.pack(anchor='w', pady=(2, 1))
            actual = tk.Label(box, text='MISA: —', bg=self.CARD, fg=self.MUTED,
                              font=('Segoe UI', 8), wraplength=112, anchor='w', justify='left')
            actual.pack(anchor='w')
            self.total_values[key] = (expected, actual)

        self.notebook = ttk.Notebook(self)
        self.notebook.pack(fill='both', expand=True, padx=14, pady=(0, 5))
        self.error_view = ErrorLogView(self.notebook)
        self.notebook.add(self.error_view, text='Log lỗi')
        self.error_view.set_report(owner.error_journal)
        self.performance_view = PerformanceLogView(self.notebook)
        self.notebook.add(self.performance_view, text='Hiệu suất')
        self.performance_view.set_snapshot(owner._performance_snapshot)

        # Comparison is intentionally detached and opt-in.  It is not created
        # or polled with the main tool panel; the user opens a temporary table
        # window when they need to inspect several MISA vouchers.
        self.comparison_window = None
        self.comparison_view = None
        self.comparison_scanner = None
        bottom = tk.Frame(self, bg=self.BG, padx=14, pady=8)
        bottom.pack(side='bottom', fill='x')
        entry_actions = tk.Frame(bottom, bg=self.BG)
        entry_actions.pack(fill='x', pady=(0, 7))
        add_label = 'Chạy test Codex' if getattr(owner, 'run_mode', 'actual') == 'codex_test' else 'Thêm vào MISA'
        self.add_button = ttk.Button(entry_actions, text=add_label, command=owner.start_add, style='Primary.TButton')
        self.add_button.pack(side='left', fill='x', expand=True)
        self.stop_add_button = ttk.Button(entry_actions, text='Dừng', command=owner.stop_add, style='Stop.TButton')
        self.stop_add_button.pack(side='left', padx=(7, 0))
        self.develop_button = ttk.Button(entry_actions, text='Develop · sao chép goal',
                                         command=owner.open_develop_mode)
        self.develop_button.pack(side='left', padx=(7, 0))
        secondary_actions = tk.Frame(bottom, bg=self.BG)
        secondary_actions.pack(fill='x')
        ttk.Button(secondary_actions, text='Mở rộng', command=owner.deiconify).pack(side='left')
        self.compare_button = ttk.Button(secondary_actions, text='Đối chiếu',
                                         command=self.open_comparison)
        self.compare_button.pack(side='left', padx=(8, 0))
        self.hot_reload_button = ttk.Button(secondary_actions, text='Hot reload',
                                            command=owner.request_hot_reload)
        self.hot_reload_button.pack(side='right')
        tk.Label(bottom, textvariable=owner.status, bg=self.BG, fg=self.MUTED,
                 wraplength=panel_width - 28, anchor='w', justify='left', font=('Segoe UI', 9)).pack(fill='x', pady=(6, 0))
        tk.Label(bottom, textvariable=owner.update_status, bg=self.BG, fg=self.MUTED,
                 wraplength=panel_width - 28, anchor='w', justify='left', font=('Segoe UI', 9)).pack(fill='x', pady=(3, 0))
        # Reserve the controls first; the notebook expands only into the remaining space.
        self.notebook.pack_forget()
        bottom.pack_forget()
        bottom.pack(side='bottom', fill='x')
        self.notebook.pack(fill='both', expand=True, padx=14, pady=(0, 5))
        self.after(0, self.chrome.decorate)

    def show(self):
        self.attributes('-alpha', 1.0)
        self.attributes('-topmost', True)
        self.deiconify()
        self.render()

    def open_comparison(self):
        if self.comparison_window is not None and self.comparison_window.winfo_exists():
            self.comparison_window.deiconify()
            self.comparison_window.lift()
            return
        window = tk.Toplevel(self)
        window.title('MISA · Đối chiếu màn hình')
        window.geometry('620x700')
        window.minsize(520, 320)
        window.resizable(True, True)
        window.configure(bg=self.BG)
        WindowChrome(window).decorate()
        view = ComparisonView(window, self.owner)
        view.pack(fill='both', expand=True, padx=10, pady=10)
        view.clear_observation()
        self.comparison_window, self.comparison_view = window, view
        view.set_invoices(self.owner.entries, self.owner.selected_path)
        self.comparison_scanner = ScreenComparisonScanner(interval=.2)

        def invoice_for_observation(observation):
            """Prefer the loaded XML invoice whose number is on the live voucher."""
            fields = observation.get('fields') or {}
            number = str(fields.get('txtInvoiceNo') or fields.get('txtRefNo') or '').strip()
            if number:
                for invoice, _error in self.owner.entries.values():
                    if invoice is not None and str(invoice.number).strip() == number:
                        return invoice
            return self.owner.entries.get(self.owner.selected_path, (None, ''))[0]

        def place_beside_misa():
            """Keep the comparison panel attached to the current MISA voucher."""
            try:
                from . import win32
                from .screen_win32 import ScreenDesktop
                misa = win32.recover_window(restore=False)
                if not ScreenDesktop.matches_window(misa, 'sale'):
                    return
                left, top, right, bottom = win32.window_rect(misa)
                width = max(520, min(760, window.winfo_width() or 620))
                height = max(320, min(bottom - top, window.winfo_screenheight() - top - 8))
                x = max(0, left - width - 8)
                window.geometry(f'{width}x{height}+{x}+{max(0, top)}')
            except Exception:
                pass

        def poll():
            if self.comparison_window is None or not self.comparison_window.winfo_exists():
                return
            place_beside_misa()
            for observation in self.comparison_scanner.drain():
                invoice = invoice_for_observation(observation)
                if observation.get('status') == 'observed':
                    view.set_observation(observation, invoice)
                else:
                    view.clear_observation(invoice)
            window.after(50, poll)

        def close_comparison():
            scanner = self.comparison_scanner
            self.comparison_scanner = None
            if scanner is not None:
                scanner.stop()
            self.comparison_view = None
            self.comparison_window = None
            window.destroy()

        window.protocol('WM_DELETE_WINDOW', close_comparison)
        place_beside_misa()
        self.comparison_scanner.start()
        window.after(250, poll)

    def render_invoice(self):
        # Live comparison data is owned by ScreenComparisonScanner and is only
        # refreshed while the detached window exists.
        return

    def render(self):
        self.render_invoice()
        invoice = self.owner.entries.get(self.owner.selected_path, (None, ''))[0]
        self.invoice_title.set('Hóa đơn ' + invoice.number if invoice else 'Chọn một hóa đơn Excel')
        values = {
            'goods': invoice.before_tax if invoice else None,
            'tax': invoice.tax if invoice else None,
            'payment': invoice.total if invoice else None,
        }
        fields = {'goods': 'creTotalTotalSaleAmountOC', 'tax': 'creTotalVATAmountOC', 'payment': 'creTotalAmountOC'}
        readings = {row['key']: row for row in comparison_rows(invoice, self.owner.comparison_state_for(invoice))}
        for key, (label, actual) in self.total_values.items():
            reading = readings.get(fields[key], {})
            bg, fg, _ = COLORS.get(reading.get('status'), COLORS['unverified'])
            label.configure(text=format_money(values[key]) if invoice else '—', bg=bg, fg=fg)
            actual.configure(text='MISA: ' + str(reading.get('actual') if reading.get('actual') not in (None, '') else '—'), bg=bg, fg=fg)
            label.master.configure(bg=bg)
            for child in label.master.winfo_children():
                child.configure(bg=bg)
        self.refresh_buttons()

    def refresh_buttons(self):
        busy = self.owner.adding or self.owner.importing or self.owner.closing or self.owner.reloading
        if self.comparison_scanner is not None:
            # Pause native comparison reads while an interactive action owns MISA.
            self.comparison_scanner.set_paused(busy)
        invoice = self.owner.entries.get(self.owner.selected_path, (None, ''))[0]
        add_label = ('Chạy test Codex' if getattr(self.owner, 'run_mode', 'actual') == 'codex_test'
                     else 'Thêm vào MISA')
        self.add_button.configure(text=add_label)
        self.choose.configure(state='disabled' if busy else 'normal')
        codex_locked = getattr(self.owner, 'run_mode', 'actual') == 'codex_test'
        self.add_button.configure(state='disabled' if busy or invoice is None or codex_locked else 'normal')
        self.stop_add_button.configure(state='normal' if self.owner.adding else 'disabled')
        state = self.owner.comparison_state_for(invoice) or {}
        phase = getattr(getattr(self.owner, 'recovery', None), 'data', {}).get('phase', '')
        phases = {'running': 'ĐANG THÊM', 'collecting': 'THU THẬP LỖI',
                  'waiting_for_codex': 'CHỜ CODEX TEST', 'manual_attention': 'LỖI · XEM LOG',
                  'sending': 'GỬI CODEX', 'accepted': 'CODEX ĐÃ NHẬN', 'processing': 'CODEX ĐANG SỬA',
                  'console_open': 'CODEX CLI / CMD MỚI',
                  'incomplete': 'CHƯA HOÀN TẤT', 'technical_error': 'CODEX ĐÃ DỪNG',
                  'source_ready': 'SOURCE SẴN SÀNG', 'preparing_runtime': 'CHUẨN BỊ RUNTIME',
                  'preparing_reload': 'CHUẨN BỊ NẠP', 'quiescing': 'THU HỒI WORKER',
                  'reloading': 'ĐANG NẠP SOURCE', 'verifying_runtime': 'KIỂM TRA RUNTIME',
                   'cleaning_tabs': 'ĐÓNG TAB KHÔNG LƯU', 'checking_sales': 'CHUẨN BỊ THÊM',
                   'restarting': 'CHẠY LẠI THÊM', 'resuming': 'TIẾP TỤC CHECKPOINT',
                   'stopping': 'ĐANG DỪNG'}
        if getattr(self.owner, 'run_mode', 'actual') == 'actual':
            phases.update({
                'waiting_for_codex': 'LỖI · XEM LOG',
                'sending': 'LƯU LOG CỤC BỘ',
                'accepted': 'ĐÃ GHI NHẬN LỖI',
                'processing': 'ĐANG DỪNG SAU LỖI',
                'console_open': 'LỖI · XEM LOG',
                'incomplete': 'CHƯA HOÀN TẤT',
                'technical_error': 'LỖI KỸ THUẬT',
                'source_ready': 'SOURCE SẴN SÀNG',
            })
        self.mode.set(('TEST CODEX · ' if getattr(self.owner, 'run_mode', 'actual') == 'codex_test' else '') +
                      (phases.get(phase, 'ĐANG PHỤC HỒI') if self.owner.adding else
                      'CẦN XỬ LÝ' if state.get('state') == 'blocked' else
                      'CHƯA CẤT' if state else 'SẴN SÀNG'))
        self.file_choice.configure(state='disabled' if busy else 'readonly')
        codex_locked = getattr(self.owner, 'run_mode', 'actual') == 'codex_test'
        self.hot_reload_button.configure(
            state='disabled' if busy or codex_locked or self.owner.update_host is None else 'normal')
        self.develop_button.configure(state='disabled' if busy or codex_locked else 'normal')
        self.compare_button.configure(state='disabled' if busy else 'normal')
        paths = list(self.owner.entries)
        labels = [f'{invoice.number if invoice else "Lỗi Excel"} · {Path(path).name}'
                  for path, (invoice, _) in self.owner.entries.items()]
        if paths != self._file_paths or labels != self._file_labels:
            self._file_paths, self._file_labels = paths, labels
            self.file_choice.configure(values=labels)
        if self.owner.selected_path in paths:
            index = paths.index(self.owner.selected_path)
            if self.file_choice.current() != index:
                self.file_choice.current(index)

    def select_file(self, _event=None):
        if self.owner.adding or self.owner.importing or self.owner.closing or self.owner.reloading:
            return
        index = self.file_choice.current()
        if 0 <= index < len(self._file_paths):
            self.owner.files.selection_set(self._file_paths[index])
            self._invoice_identity = None
            self.owner.select_file()
