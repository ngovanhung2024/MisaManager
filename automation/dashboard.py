"""Shared, responsive comparison and diagnostics views for the desktop tool."""
from datetime import datetime
import re
import tkinter as tk
from tkinter import ttk

from .comparison import comparison_rows, compatible_state, observation_rows, sample_rows
from invoice_xml import format_money


BG, SURFACE, INK, MUTED = '#edf2f8', '#ffffff', '#182c49', '#566981'
ACCENT, BORDER = '#245de5', '#d7e1ef'
COLORS = {
    'matched': ('#e7f4ed', '#196747', 'Khớp'),
    'mismatched': ('#fff0ef', '#af302e', 'Sai lệch'),
    'unverified': (SURFACE, MUTED, 'Chưa có dữ liệu'),
    'mapped': (SURFACE, MUTED, 'Đã ghép mã'),
}


def configure_styles(root):
    style = ttk.Style(root)
    style.theme_use('clam')
    style.configure('.', font=('Segoe UI', 10), background=BG, foreground=INK)
    style.configure('TButton', padding=(10, 7), borderwidth=1, relief='flat',
                    background=SURFACE, bordercolor=BORDER)
    style.map('TButton', background=[('active', '#e0eafb'), ('disabled', '#e9edf3')],
              foreground=[('disabled', '#8995a5')])
    style.configure('Primary.TButton', background=ACCENT, foreground='white', bordercolor=ACCENT)
    style.map('Primary.TButton', background=[('disabled', '#dbe3ef'), ('active', '#174bc5')],
              foreground=[('disabled', '#8995a5'), ('!disabled', 'white')])
    style.configure('Stop.TButton', foreground='#af302e')
    style.configure('TNotebook', background=BG, borderwidth=0, tabmargins=(0, 0, 0, 5))
    style.configure('TNotebook.Tab', padding=(10, 8), background=BG, foreground=MUTED)
    style.map('TNotebook.Tab', background=[('selected', SURFACE)],
              foreground=[('selected', ACCENT)])
    style.configure('TCombobox', padding=4, fieldbackground=SURFACE, arrowsize=14)
    style.configure('Treeview', rowheight=30, font=('Segoe UI', 10), background=SURFACE,
                    fieldbackground=SURFACE, foreground=INK, borderwidth=0)
    style.configure('Treeview.Heading', font=('Segoe UI', 9, 'bold'), padding=(7, 8),
                    background='#e8eef7', foreground=MUTED, relief='flat')
    style.map('Treeview', background=[('selected', '#dce9ff')], foreground=[('selected', '#173e77')])
    style.configure('Glass.Treeview', rowheight=29, font=('Segoe UI', 9))
    style.configure('TProgressbar', background=ACCENT, troughcolor='#e3ebf5', borderwidth=0)
    return style


def local_time(value):
    if not value:
        return ''
    try:
        return datetime.fromisoformat(value.replace('Z', '+00:00')).astimezone().strftime('%H:%M:%S')
    except (TypeError, ValueError):
        return str(value)


class ComparisonView(tk.Frame):
    def __init__(self, parent, owner=None):
        super().__init__(parent, bg=SURFACE)
        self.owner = owner
        self._invoice = self._state = None
        self._observation = None
        self._rows, self._signature = [], None
        self.summary = tk.Label(self, bg=SURFACE, fg=INK, font=('Segoe UI', 10, 'bold'),
                                anchor='w', justify='left', padx=12, pady=9)
        self.summary.pack(fill='x')
        controls = tk.Frame(self, bg=SURFACE)
        controls.pack(fill='x', padx=12)
        self.invoice_choice = ttk.Combobox(controls, state='readonly', width=34)
        self.invoice_choice.pack(side='left', pady=(0, 7))
        self.invoice_choice.bind('<<ComboboxSelected>>', self._select_invoice)
        # Kept as a state flag for workflow compatibility; the detached
        # comparison window is driven by explicit screen observations.
        self.follow = tk.BooleanVar(value=False)
        self.filter = ttk.Combobox(controls, state='readonly', width=18,
                                   values=('Tất cả', 'Khớp', 'Sai lệch', 'Chưa xác minh'))
        self.filter.set('Tất cả')
        self.filter.pack(side='right', pady=(0, 7))
        self.filter.bind('<<ComboboxSelected>>', lambda event: self.render_cards())
        self.freshness = tk.Label(self, bg=SURFACE, fg=MUTED, anchor='w', justify='left',
                                  padx=12, pady=7, font=('Segoe UI', 9))
        self.freshness.pack(side='bottom', fill='x')
        body = tk.Frame(self, bg=SURFACE)
        body.pack(fill='both', expand=True, padx=10)
        columns = ('status', 'group', 'label', 'expected', 'actual', 'source', 'time')
        self.table = ttk.Treeview(body, columns=columns, show='headings', selectmode='browse')
        headings = {'status': 'Trạng thái', 'group': 'Nhóm', 'label': 'Trường',
                    'expected': 'Dữ liệu Excel', 'actual': 'Giá trị MISA',
                    'source': 'Nguồn', 'time': 'Đọc lúc'}
        widths = {'status': 105, 'group': 125, 'label': 155, 'expected': 180,
                  'actual': 180, 'source': 115, 'time': 76}
        for column in columns:
            self.table.heading(column, text=headings[column])
            self.table.column(column, width=widths[column], minwidth=70,
                               anchor='w', stretch=column in {'label', 'expected', 'actual'})
        yscroll = ttk.Scrollbar(body, orient='vertical', command=self.table.yview)
        xscroll = ttk.Scrollbar(body, orient='horizontal', command=self.table.xview)
        self.table.configure(yscrollcommand=yscroll.set, xscrollcommand=xscroll.set)
        self.table.grid(row=0, column=0, sticky='nsew')
        yscroll.grid(row=0, column=1, sticky='ns')
        xscroll.grid(row=1, column=0, sticky='ew')
        body.rowconfigure(0, weight=1)
        body.columnconfigure(0, weight=1)
        self.table.tag_configure('matched', background='#e7f4ed', foreground='#196747')
        self.table.tag_configure('mismatched', background='#fff0ef', foreground='#af302e')
        self.table.tag_configure('unverified', background=SURFACE, foreground=MUTED)
        self.table.tag_configure('mapped', background='#eef3fb', foreground='#365887')
        self.table.bind('<Double-1>', self.copy_selected)
        legend = tk.Label(self, text='Xanh: khớp  ·  Đỏ: không khớp  ·  Không màu: MISA chưa có dữ liệu',
                          bg=SURFACE, fg=MUTED, anchor='w', padx=12, pady=4,
                          font=('Segoe UI', 9))
        legend.pack(before=self.freshness, fill='x')
        self.set_state(None, None)

    def set_invoices(self, entries, selected_path=None):
        self._invoice_paths = []
        labels = []
        for path, value in entries.items():
            invoice = value[0] if isinstance(value, tuple) else None
            if invoice is None:
                continue
            self._invoice_paths.append(path)
            labels.append(f'{invoice.number} · {len(invoice.lines)} dòng · {format_money(invoice.total)}')
        self.invoice_choice.configure(values=labels)
        if self._invoice_paths:
            index = self._invoice_paths.index(selected_path) if selected_path in self._invoice_paths else 0
            self.invoice_choice.current(index)
        invoice = self.selected_invoice()
        if self._observation is not None:
            self.set_rows(observation_rows(self._observation, invoice),
                          message=('Nguồn: ' + str(self._observation.get('source', 'màn hình'))
                                   + ' · frame ' + str(self._observation.get('frame_id', '—'))))

    def _select_invoice(self, _event=None):
        if not self.owner or not self._invoice_paths:
            return
        path = self._invoice_paths[self.invoice_choice.current()]
        self.owner.files.selection_set(path)
        self.owner.select_file()

    def selected_invoice(self):
        if self.owner:
            return self.owner.entries.get(self.owner.selected_path, (None, ''))[0]
        return None

    def wheel(self, event):
        self.table.yview_scroll(-int(event.delta / 120), 'units')
        return 'break'

    def resize(self, event):
        self.freshness.configure(wraplength=max(220, self.winfo_width() - 28))
        self.summary.configure(wraplength=max(220, self.winfo_width() - 28))

    def set_state(self, invoice, state, *, force=False):
        state = state if compatible_state(invoice, state) else None
        identity = (id(invoice), state.get('run_id') if state else None,
                    state.get('updated_at') if state else None, state.get('state') if state else None)
        if identity == self._signature and not force:
            return
        self._signature, self._invoice, self._state = identity, invoice, state
        self.set_rows(comparison_rows(invoice, state) if invoice else sample_rows(),
                      message=('Theo dõi dữ liệu đã đọc trong workflow.' if state
                               else 'Dữ liệu mẫu minh họa; chưa có dữ liệu MISA/Excel.'))

    def set_observation(self, observation, invoice=None):
        self._observation = observation
        rows = observation_rows(observation, invoice)
        self._invoice = invoice
        self.set_rows(rows, message=('Nguồn: ' + str(observation.get('source', 'màn hình'))
                                     + ' · frame ' + str(observation.get('frame_id', '—')))
                      if observation else 'Dữ liệu mẫu minh họa.')

    def clear_observation(self, invoice=None):
        """Clear live rows when the MISA voucher disappears."""
        self._observation = None
        self._invoice = invoice
        self.set_rows([dict(key='live:observation', group='Màn hình MISA',
                            field='Đọc native', label='Đọc native', expected='—',
                            actual=None, status='unverified', source='live_misa_win32',
                            observed_at='', note='Chưa mở phiếu MISA hiện tại.')],
                      message='Chưa mở phiếu MISA hiện tại.')

    def set_rows(self, rows, *, message=''):
        self._rows = list(rows or sample_rows())
        count = {status: sum(row['status'] == status for row in self._rows) for status in COLORS}
        summary = (f"{count['matched']} khớp  ·  {count['mismatched']} sai lệch  ·  "
                   f"{count['unverified']} chưa có dữ liệu"
                   + (f"  ·  {count['mapped']} mã đã ghép" if count['mapped'] else ''))
        self.summary.configure(text=summary if self._rows else 'Chưa có dữ liệu')
        self.freshness.configure(text=message or 'Dữ liệu được chụp từ một frame hiện tại; không dùng ảnh lịch sử làm live truth.')
        self.render_cards()

    def render_cards(self):
        self.table.delete(*self.table.get_children())
        wanted = {'Khớp': 'matched', 'Sai lệch': 'mismatched',
                  'Chưa xác minh': 'unverified'}.get(self.filter.get())
        if wanted:
            rows = [row for row in self._rows if row.get('status') == wanted]
        else:
            rows = list(self._rows)
        captions = {'matched': 'Khớp', 'mismatched': 'Sai lệch',
                    'unverified': 'Chưa xác minh', 'mapped': 'Đã ghép mã'}
        for index, row in enumerate(rows, 1):
            status = row.get('status', 'unverified')
            source = row.get('source', 'Excel')
            observed = local_time(row.get('observed_at')) or '—'
            self.table.insert('', 'end', iid=f'row-{index}', values=(
                captions.get(status, status), row.get('group', '—'), row.get('label', '—'),
                row.get('expected', '—'), row.get('actual') or '—', source, observed),
                tags=(status,))

    def copy_selected(self, _event=None):
        selected = self.table.selection()
        if not selected:
            return
        values = self.table.item(selected[0], 'values')
        self.clipboard_clear()
        self.clipboard_append('\t'.join(values))


class ReportView(tk.Frame):
    def __init__(self, parent):
        super().__init__(parent, bg=SURFACE)
        self._content = ''
        self.summary = tk.Label(self, bg=SURFACE, fg=INK, anchor='w', justify='left',
                                padx=12, pady=9, font=('Segoe UI', 10, 'bold'))
        self.summary.pack(fill='x')
        self.actions = tk.Frame(self, bg=SURFACE)
        self.actions.pack(fill='x', padx=10, pady=(0, 6))
        self.copy_button = ttk.Button(self.actions, text='Sao chép', command=self.copy, state='disabled')
        self.copy_button.pack(side='left')
        self.feedback = tk.StringVar(value='')
        self.hint = tk.Label(self, textvariable=self.feedback, bg=SURFACE, fg=MUTED,
                             anchor='w', justify='left', padx=12, pady=7, font=('Segoe UI', 9))
        self.hint.pack(side='bottom', fill='x')
        self.body = tk.Frame(self, bg=SURFACE)
        self.body.pack(fill='both', expand=True)
        self.text = tk.Text(self.body, wrap='none', state='disabled', relief='flat',
                            bg='#f6f8fc', fg=INK, font=('Cascadia Mono', 9), padx=10, pady=9, height=5)
        sy = ttk.Scrollbar(self.body, command=self.text.yview)
        sx = ttk.Scrollbar(self.body, orient='horizontal', command=self.text.xview)
        self.text.configure(yscrollcommand=sy.set, xscrollcommand=sx.set)
        self.text.grid(row=0, column=0, sticky='nsew')
        sy.grid(row=0, column=1, sticky='ns')
        sx.grid(row=1, column=0, sticky='ew')
        self.body.rowconfigure(0, weight=1)
        self.body.columnconfigure(0, weight=1)
        self.text.bind('<Control-a>', self.select_all)
        self.text.tag_configure('error', foreground='#af302e')
        self.text.tag_configure('heading', foreground=ACCENT, font=('Cascadia Mono', 9, 'bold'))
        self.bind('<Configure>', self.resize)

    def resize(self, event):
        if event.widget is self:
            self.summary.configure(wraplength=max(220, event.width - 24))
            self.hint.configure(wraplength=max(220, event.width - 24))

    def set_text(self, content):
        position = self.text.yview()
        self.text.configure(state='normal')
        self.text.delete('1.0', 'end')
        self.text.insert('1.0', content)
        for line, value in enumerate(content.splitlines(), 1):
            tag = 'error' if value.startswith('Exception:') else 'heading' if value.startswith(
                ('Operation:', 'Occurred at:', 'Full traceback:', 'Source revision:')) else None
            if tag:
                self.text.tag_add(tag, f'{line}.0', f'{line}.end')
        self.text.configure(state='disabled')
        if position:
            self.text.yview_moveto(position[0])

    def select_all(self, _event=None):
        self.text.tag_add('sel', '1.0', 'end-1c')
        self.text.focus_set()
        return 'break'

    def copy(self, content=None):
        try:
            self.clipboard_clear()
            self.clipboard_append(self._content if content is None else content)
            self.feedback.set('Đã sao chép. Bạn có thể dán nội dung để phân tích.')
        except tk.TclError:
            self.select_all()
            self.feedback.set('Chưa ghi được clipboard. Nhấn Ctrl+C trên phần đã chọn.')


class ErrorLogView(ReportView):
    def __init__(self, parent):
        super().__init__(parent)
        self._entries, self._report = [], ''
        self.copy_button.configure(text='Sao chép lỗi')
        self.copy_all = ttk.Button(self.actions, text='Toàn bộ log', command=lambda: self.copy(self._report), state='disabled')
        self.copy_all.pack(side='left', padx=6)
        filters = tk.Frame(self, bg=SURFACE)
        filters.pack(before=self.body, fill='x', padx=10, pady=(0, 6))
        self.search = tk.StringVar()
        tk.Label(filters, text='Tìm lỗi / bước', bg=SURFACE, fg=MUTED, font=('Segoe UI', 9)).pack(anchor='w')
        ttk.Entry(filters, textvariable=self.search).pack(fill='x', pady=(3, 5))
        self.search.trace_add('write', lambda *args: self.render_list())
        self.events = ttk.Treeview(filters, columns=('error',), show='headings', height=3, selectmode='browse')
        self.events.heading('error', text='THỜI ĐIỂM · THAO TÁC')
        self.events.column('error', width=230)
        self.events.pack(fill='x')
        self.events.bind('<<TreeviewSelect>>', self.select_error)
        self.events.tag_configure('error', foreground='#af302e')

    @staticmethod
    def entry_value(entry, name):
        match = re.search(r'^' + re.escape(name) + r': (.*)$', entry, re.M)
        return match[1] if match else ''

    def set_report(self, journal):
        report = journal.render()
        if report == self._report:
            return
        self._report, self._entries = report, list(journal.entries)
        count = len(self._entries)
        self.summary.configure(text=f'{count} lỗi trong phiên hiện tại' if count else 'Phiên hiện tại chưa ghi nhận lỗi',
                               fg='#af302e' if count else INK)
        self.copy_all.configure(state='normal' if count else 'disabled')
        self.feedback.set('Chưa lưu được file: ' + journal.storage_error if journal.storage_error else
                          'Chọn một lỗi để đọc nguyên nhân và traceback. Log được giữ cục bộ.')
        self.render_list(newest=True)

    def render_list(self, *, newest=False):
        previous = self.events.selection()
        self.events.delete(*self.events.get_children())
        query = self.search.get().casefold().strip()
        for index in reversed(range(len(self._entries))):
            entry = self._entries[index]
            if query and query not in entry.casefold():
                continue
            title = local_time(self.entry_value(entry, 'Occurred at')) + ' · ' + self.entry_value(entry, 'Operation')
            self.events.insert('', 'end', iid=str(index), values=(title,), tags=('error',))
        ids = self.events.get_children()
        if ids:
            selected = previous[0] if previous and previous[0] in ids and not newest else ids[0]
            self.events.selection_set(selected)
            self.select_error()
        else:
            self._content = ''
            self.copy_button.configure(state='disabled')
            self.set_text('Không có lỗi phù hợp.' if query else 'Chưa có lỗi. Chi tiết sẽ hiển thị tại đây khi cần.')

    def select_error(self, _event=None):
        selected = self.events.selection()
        if not selected:
            return
        entry = self._entries[int(selected[0])]
        self._content = self._report.split('\n\n', 1)[0] + '\n\n' + entry
        self.copy_button.configure(state='normal')
        self.set_text(entry)


class PerformanceLogView(ReportView):
    """Show native UIA/Win32 timing and readback activity."""

    def __init__(self, parent):
        super().__init__(parent)
        self._snapshot = None
        self._displayed_snapshot = None
        self._render_identity = None
        self.live = tk.BooleanVar(value=True)
        self.raw = tk.BooleanVar(value=False)
        self.copy_button.configure(text='Sao chép báo cáo')
        ttk.Checkbutton(self.actions, text='Trực tiếp', variable=self.live,
                        command=self.resume).pack(side='right')
        self.dashboard = tk.Frame(self, bg=SURFACE)
        self.dashboard.pack(before=self.body, fill='both', expand=True, padx=10)
        self.stats = tk.Label(self.dashboard, bg='#edf3fc', fg=INK, justify='left', anchor='w',
                              padx=10, pady=9, font=('Segoe UI', 10, 'bold'))
        self.stats.pack(fill='x')
        self.dashboard.bind('<Configure>', lambda event: self.stats.configure(
            wraplength=max(200, event.width - 24)))
        self.chart = tk.Canvas(self.dashboard, bg=SURFACE, highlightthickness=0,
                               height=116, width=220)
        self.chart.pack(fill='x', pady=(3, 0))
        self.chart.bind('<Configure>', lambda event: self.draw_chart())
        self.steps = ttk.Treeview(self.dashboard, columns=('step', 'time'),
                                  show='headings', height=5)
        self.steps.heading('step', text='BƯỚC / TRẠNG THÁI')
        self.steps.heading('time', text='GIÂY')
        self.steps.column('step', width=260, minwidth=100)
        self.steps.column('time', width=70, minwidth=55, stretch=False, anchor='e')
        self.steps.pack(fill='both', expand=True)
        self.steps.tag_configure('error', foreground='#af302e')
        self.steps.tag_configure('active', foreground=ACCENT)
        self.steps.bind('<<TreeviewSelect>>', self.select_step)
        self.body_toggle = ttk.Checkbutton(self, text='Chi tiết báo cáo', variable=self.raw,
                                           command=self.toggle_raw)
        self.body_toggle.pack(before=self.body, anchor='w', padx=10, pady=4)
        self.body.pack_forget()
        self.set_snapshot(None)

    def resume(self):
        self._render_identity = None
        if self.live.get():
            self.text.tag_remove('sel', '1.0', 'end')
            self.set_snapshot(self._snapshot)

    def toggle_raw(self):
        if self.raw.get():
            self.dashboard.pack_forget()
            self.body.pack(before=self.hint, fill='both', expand=True)
            self.set_text(self._content)
        else:
            self.body.pack_forget()
            self.dashboard.pack(before=self.body_toggle, fill='both', expand=True, padx=10)

    def set_snapshot(self, snapshot):
        from .performance import render_performance
        self._snapshot = snapshot
        if not self.live.get() or (self.raw.get() and self.text.tag_ranges('sel')):
            return
        identity = (snapshot.get('session_id'), snapshot.get('elapsed_ms'),
                    snapshot.get('pending_writes'), snapshot.get('log_error'),
                    snapshot.get('state'), snapshot.get('dropped_events')) if snapshot else ('empty',)
        if identity == self._render_identity:
            return
        self._render_identity = identity
        self._displayed_snapshot = snapshot
        self._content = render_performance(snapshot)
        number = (snapshot or {}).get('context', {}).get('invoice_number', '')
        state = (snapshot or {}).get('state', '')
        caption = {'running': 'Đang chạy', 'complete': 'Đã kết thúc',
                   'paused': 'Đã dừng', 'blocked': 'Cần xử lý'}.get(state, state)
        if (snapshot or {}).get('historical'):
            caption = 'Số đo đã lưu từ phiên trước'
        self.summary.configure(text=(f'Hóa đơn {number} · {caption}\nBắt đầu lúc '
                                     + local_time(snapshot.get('started_at')))
                               if snapshot else 'Chưa có lượt nhập trong phiên này')
        elapsed = (snapshot or {}).get('elapsed_ms', 0) / 1000
        slowest = (snapshot or {}).get('slowest_step') or max(
            (snapshot or {}).get('steps', []), key=lambda s: s['elapsed_ms'], default=None)
        slow_text = (f"\nLâu nhất: {slowest.get('checkpoint', '')} · "
                     f"{slowest['elapsed_ms'] / 1000:.1f} s" if slowest else '')
        self.stats.configure(text=f'{elapsed:.1f} s  tổng thời gian' + slow_text)
        self.copy_button.configure(state='normal' if snapshot else 'disabled')
        warnings = []
        if snapshot:
            if snapshot.get('log_error'):
                warnings.append('Lỗi ghi log: ' + snapshot['log_error'])
            if snapshot.get('dropped_events'):
                warnings.append(f"Bỏ lọc {snapshot['dropped_events']} sự kiện")
        self.feedback.set(' · '.join(warnings) or
                          'Biểu đồ dùng thời gian riêng, tránh cộng trùng thao tác con. Chọn bước để xem đầy đủ.')
        steps = list((snapshot or {}).get('steps', []))
        active = [step for step in (snapshot or {}).get('active', []) if step.get('kind') == 'step']
        self._steps = {}
        for index, step in enumerate(active + list(reversed(steps))):
            running = index < len(active)
            status = step.get('status', 'active' if running else '')
            label = (('Đang chạy · ' if running else 'Lỗi · ' if status == 'error' else
                      'Đã dừng · ' if status == 'cancelled' else '') + step['label'])
            key = step.get('operation_id') or str(index)
            self._steps[key] = step
            values = (label, f"{step['elapsed_ms'] / 1000:.2f}")
            tags = ('active' if running else 'error' if status == 'error' else '',)
            if self.steps.exists(key):
                self.steps.item(key, values=values, tags=tags)
                self.steps.move(key, '', index)
            else:
                self.steps.insert('', index, iid=key, values=values, tags=tags)
        for key in self.steps.get_children():
            if key not in self._steps:
                self.steps.delete(key)
        self.draw_chart()
        if self.steps.selection() and not warnings:
            self.select_step()
        if self.raw.get():
            self.set_text(self._content)

    def select_step(self, _event=None):
        selected = self.steps.selection()
        if selected and selected[0] in self._steps:
            step = self._steps[selected[0]]
            self.feedback.set(step['label'] + ('. ' + step['error'] if step.get('error') else ''))

    def draw_chart(self):
        self.chart.delete('all')
        totals = (self._displayed_snapshot or {}).get('totals', {})
        ranked = sorted(totals.items(), key=lambda item: item[1].get('self_ms', 0), reverse=True)[:4]
        if not ranked:
            self.chart.create_text(8, 28, text='Thời gian từng nhóm sẽ xuất hiện khi chạy.',
                                   anchor='w', fill=MUTED, font=('Segoe UI', 9))
            return
        labels = {'input': 'Click / gõ', 'wait': 'Chờ MISA', 'verification': 'Đối chiếu',
                  'readback': 'Đọc giá trị', 'locate': 'Định vị', 'observation': 'Quan sát',
                  'state_io': 'Ghi tiến độ', 'step': 'Bước nhập', 'workflow': 'Quy trình',
                  'action': 'Thao tác', 'capture': 'Chụp cửa sổ', 'evidence': 'Bằng chứng'}
        available = max(70, self.chart.winfo_width() - 162)
        maximum = max(value.get('self_ms', 0) for _, value in ranked) or 1
        for index, (key, value) in enumerate(ranked):
            y = 15 + index * 25
            ms = value.get('self_ms', 0)
            self.chart.create_text(2, y, text=labels.get(key, key), anchor='w',
                                   fill=MUTED, font=('Segoe UI', 8))
            self.chart.create_rectangle(88, y - 5, 88 + available, y + 5,
                                        fill='#edf2f8', outline='')
            self.chart.create_rectangle(88, y - 5, 88 + max(1, available * ms / maximum),
                                        y + 5, fill=ACCENT, outline='')
            self.chart.create_text(98 + available, y, text=f'{ms/1000:.1f}s',
                                   anchor='w', fill=INK, font=('Segoe UI', 8))
