"""Invoice expectations and readback-only comparison state shared by both views."""
from datetime import date
from datetime import datetime, timezone
import math
import queue
import re
import threading
import time
import uuid
from invoice_xml import format_money
from .verification import text_key


TOTAL_FIELDS = (
    ('creTotalTotalSaleAmountOC', 'Tiền hàng', 'before_tax'),
    ('creTotalVATAmountOC', 'Thuế GTGT', 'tax'),
    ('creTotalAmountOC', 'Thanh toán', 'total'),
)
LINE_FIELDS = (
    ('tenhang', 'Tên hàng', 'name', 'text'),
    ('dvt', 'Đơn vị tính', 'unit', 'unit'),
    ('soluong', 'Số lượng', 'quantity', 'decimal'),
    ('dongia', 'Đơn giá MISA tự tính', 'unit_price', 'decimal'),
    ('thanhtien', 'Thành tiền', 'amount', 'decimal'),
    ('thuesuatgtgt', 'Thuế suất GTGT', 'tax_rate', 'tax_rate'),
    ('tienthuegtgt', 'Tiền thuế', 'tax', 'decimal'),
)

COMPARISON_SCHEMA_VERSION = 1
EXCEL_IMPORT_SCHEMA = {
    'version': 1,
    'columns': (
        'group', 'field', 'label', 'expected', 'actual', 'status',
        'observed_at', 'source', 'frame_id', 'hwnd', 'pid', 'note'),
    'status_values': ('matched', 'mismatched', 'mapped', 'unverified'),
    'date_format': 'ISO-8601',
    'description': 'Một dòng kỳ vọng/đọc lại cho mỗi trường hoặc dòng hàng; '
                   'không chứa lệnh thao tác MISA.',
}


def now_iso():
    return datetime.now(timezone.utc).isoformat()


def sample_rows():
    """Small, explicit screen sample used before a live MISA observation."""
    observed = '2026-09-21T00:04:00+00:00'
    source = 'sample_screen'
    rows = [
        ('Thông tin chung', 'Khách hàng', 'NTD', 'NTD', 'matched'),
        ('Thông tin chung', 'Tên khách hàng', 'BÁN CHO NGƯỜI TIÊU DÙNG',
         'BÁN CHO NGƯỜI TIÊU DÙNG', 'matched'),
        ('Thông tin chung', 'Ngày hạch toán', '08/12/2026', '08/12/2026', 'matched'),
        ('Thông tin chung', 'Ngày chứng từ', '08/12/2026', '08/12/2026', 'matched'),
        ('Thông tin chung', 'Số phiếu thu', 'PT0203', 'PT0203', 'matched'),
        ('Dòng 1', 'Mã hàng', 'HH_CHAI', 'HH_CHAI', 'matched'),
        ('Dòng 1', 'Tên hàng', 'Rộng đồ 200SL - Chai 100ml',
         'Rộng đồ 200SL - Chai 100ml', 'matched'),
        ('Dòng 1', 'ĐVT', 'Chai', 'Chai', 'matched'),
        ('Dòng 1', 'Số lượng', '22,000', '22,000', 'matched'),
        ('Dòng 1', 'Đơn giá', '10.000,00', '10.000,00', 'matched'),
        ('Dòng 1', 'Thành tiền', '220.000', '220.000', 'matched'),
        ('Dòng 2', 'Mã hàng', 'HH_GOI', 'HH_GOI', 'matched'),
        ('Dòng 2', 'Tên hàng', 'Hesman 60W 18g', 'Hesman 60W 18g', 'matched'),
        ('Dòng 2', 'ĐVT', 'Gói', 'Gói', 'matched'),
        ('Dòng 2', 'Số lượng', '38,000', '38,000', 'matched'),
        ('Dòng 2', 'Đơn giá', '16.000,00', '16.000,00', 'matched'),
        ('Dòng 2', 'Thành tiền', '608.000', '608.000', 'matched'),
        ('Tổng hóa đơn', 'Tổng tiền hàng', '5.220.857', '5.220.857', 'matched'),
        ('Tổng hóa đơn', 'Tiền thuế GTGT', '261.043', '261.043', 'matched'),
        ('Tổng hóa đơn', 'Tổng thanh toán', '5.481.900', '5.481.900', 'matched'),
    ]
    return [dict(key=f'sample:{index}', group=group, field=field, label=field,
                 expected=expected, actual=actual, status=status,
                 observed_at=observed, source=source, frame_id='sample-frame',
                 hwnd=None, pid=None, note='Dữ liệu mẫu từ ảnh tham chiếu.')
            for index, (group, field, expected, actual, status) in enumerate(rows, 1)]


def screen_observation(*, status='unverified', hwnd=None, pid=None, title='',
                       process_birth=None, frame_id='', client_bounds=None,
                       dpi=None, captured_at=None, fields=None, rows=None,
                       totals=None, source='live_misa_win32', reason=''):
    """Stable, Excel-friendly container for one fresh native observation."""
    return {
        'schema': COMPARISON_SCHEMA_VERSION,
        'observation_id': f'compare-{now_iso()}',
        'status': status,
        'source': source,
        'reason': reason,
        'hwnd': hwnd,
        'pid': pid,
        'process_birth': process_birth,
        'window_title': title,
        'frame_id': frame_id,
        'client_bounds': list(client_bounds) if client_bounds else None,
        'dpi': dpi,
        'captured_at': captured_at or now_iso(),
        'fields': dict(fields or {}),
        'rows': list(rows or []),
        'totals': dict(totals or {}),
    }


def observation_rows(observation, invoice=None):
    """Convert a fresh MISA observation into Excel-vs-MISA comparison rows."""
    if not observation or observation.get('source') == 'sample_screen':
        return sample_rows()
    actual = observation.get('fields') or {}
    expected = {row['field']: row for row in expectations(invoice)} if invoice else {}

    def same(expected_value, actual_value, field):
        if expected_value in (None, '', '—') or actual_value in (None, ''):
            return False
        if field in {'txtInvoiceNo', 'dtePostedDate', 'dteRefDate'}:
            return text_key(expected_value) == text_key(actual_value)
        if field.startswith('cre') or field in {'soluong', 'dongia', 'thanhtien', 'tienthuegtgt'}:
            try:
                from decimal import Decimal
                def number(value):
                    raw = str(value).strip().replace(' ', '')
                    if ',' in raw and '.' in raw:
                        raw = raw.replace('.', '').replace(',', '.')
                    elif ',' in raw:
                        raw = raw.replace(',', '.')
                    return Decimal(raw)
                return number(expected_value) == number(actual_value)
            except (ValueError, TypeError, ArithmeticError):
                return False
        return text_key(expected_value) == text_key(actual_value)
    result = []
    line_field_names = {'mahang', 'tenhang', 'dvt', 'soluong', 'dongia', 'thanhtien', 'thuesuatgtgt', 'tienthuegtgt'}
    for field in dict.fromkeys([*expected, *actual]):
        if field in line_field_names:
            continue
        spec = expected.get(field, {})
        value = actual.get(field)
        expected_value = spec.get('expected', '—')
        status = ('matched' if same(expected_value, value, field)
                  else ('mismatched' if value not in (None, '') and spec else 'unverified'))
        result.append(dict(key=field, group=spec.get('group', 'Thông tin MISA'),
                           field=field, label=spec.get('label', field),
                           expected=expected_value, actual=value,
                           status=status, observed_at=observation.get('captured_at', ''),
                           source=observation.get('source', 'live_misa_win32'),
                           frame_id=observation.get('frame_id', ''),
                           hwnd=observation.get('hwnd'), pid=observation.get('pid'),
                           note=observation.get('reason', '')))
    for row_number, row in enumerate(observation.get('rows') or (), 1):
        expected_line = invoice.lines[row_number - 1] if invoice and row_number <= len(invoice.lines) else None
        line_expected = {}
        if expected_line:
            line_expected = {
                'tenhang': expected_line.name, 'dvt': expected_line.unit,
                'soluong': format_money(expected_line.quantity),
                'dongia': format_money(expected_line.unit_price),
                'thanhtien': format_money(expected_line.amount),
                'thuesuatgtgt': expected_line.tax_rate or '—',
                'tienthuegtgt': format_money(expected_line.tax),
            }
        for field, value in row.items():
            if field in ('row', 'group'):
                continue
            expected_value = line_expected.get(field, '—')
            status = 'matched' if same(expected_value, value, field) else ('mismatched' if expected_line else 'unverified')
            result.append(dict(key=f'live:line:{row_number}:{field}',
                               group=f'Dòng {row_number} · {expected_line.name if expected_line else "MISA"}', field=field,
                               label={'tenhang': 'Tên hàng hóa', 'dvt': 'Đơn vị tính', 'soluong': 'Số lượng',
                                      'dongia': 'Đơn giá', 'thanhtien': 'Thành tiền', 'thuesuatgtgt': 'Thuế hàng hóa',
                                      'tienthuegtgt': 'Tiền thuế'}.get(field, field), expected=expected_value, actual=value,
                               status=status, observed_at=observation.get('captured_at', ''),
                               source=observation.get('source', 'live_misa_win32'),
                               frame_id=observation.get('frame_id', ''),
                               hwnd=observation.get('hwnd'), pid=observation.get('pid'),
                                note='Đọc trực tiếp từ control native MISA; chưa có dữ liệu Excel/XML để đối chiếu.'))
    if invoice:
        observed_rows = observation.get('rows') or ()
        for row_number, line in enumerate(invoice.lines, 1):
            row_values = observed_rows[row_number - 1] if row_number <= len(observed_rows) else {}
            expected_line = {
                'tenhang': line.name, 'dvt': line.unit,
                'soluong': format_money(line.quantity), 'dongia': format_money(line.unit_price),
                'thanhtien': format_money(line.amount), 'thuesuatgtgt': line.tax_rate or '—',
                'tienthuegtgt': format_money(line.tax),
            }
            for field, expected_value in expected_line.items():
                if field in row_values:
                    continue
                result.append(dict(key=f'excel:line:{row_number}:{field}',
                                   group=f'Dòng {row_number} · {line.name}', field=field,
                                   label={'tenhang': 'Tên hàng hóa', 'dvt': 'Đơn vị tính', 'soluong': 'Số lượng',
                                          'dongia': 'Đơn giá', 'thanhtien': 'Thành tiền',
                                          'thuesuatgtgt': 'Thuế hàng hóa', 'tienthuegtgt': 'Tiền thuế'}.get(field, field),
                                   expected=expected_value, actual=None, status='unverified',
                                   observed_at=observation.get('captured_at', ''),
                                   source=observation.get('source', 'live_misa_win32'),
                                   frame_id=observation.get('frame_id', ''), hwnd=observation.get('hwnd'),
                                   pid=observation.get('pid'), note=observation.get('reason', '')))
    for field, value in (observation.get('totals') or {}).items():
        spec = expected.get(field, {})
        expected_value = spec.get('expected', '—')
        status = 'matched' if same(expected_value, value, field) else ('mismatched' if spec else 'unverified')
        result.append(dict(key=f'live:total:{field}', group='Tổng hóa đơn', field=field,
                           label=spec.get('label', field), expected=expected_value, actual=value, status=status,
                           observed_at=observation.get('captured_at', ''),
                           source=observation.get('source', 'live_misa_win32'),
                           frame_id=observation.get('frame_id', ''),
                           hwnd=observation.get('hwnd'), pid=observation.get('pid'),
                           note='Đọc trực tiếp từ control native MISA.'))
    if result:
        return result
    return [dict(
        key='live:observation', group='Màn hình MISA', field='Đọc native',
        label='Đọc native', expected='—', actual=None, status='unverified',
        observed_at=observation.get('captured_at', ''),
        source=observation.get('source', 'live_misa_win32'),
        frame_id=observation.get('frame_id', ''), hwnd=observation.get('hwnd'),
        pid=observation.get('pid'),
        note=observation.get('reason') or 'Chưa có dữ liệu native hiện tại.'
    )]


class ScreenComparisonScanner:
    """Read visible voucher data through current Windows-native controls.

    This scanner is observation-only. It never locates or clicks a control;
    fixed controls are resolved by the current UIA3 semantic driver.
    Every result carries the current window identity and frame identity so the
    table cannot present a historical observation as current MISA state.
    """

    def __init__(self, *, interval=.8):
        # Keep the detached panel responsive when the active MISA voucher changes.
        self.interval = max(.1, float(interval))
        self.cancel = threading.Event()
        self.paused = threading.Event()
        self.wake = threading.Event()
        self.events = queue.Queue()
        self.thread = None
        self._uia = None
        self._uia_identity = None
        self._uia_detail_tab = ''
        self._uia_grid_bounds = None
        self._uia_row_bounds = {}

    def start(self):
        if self.thread is not None and self.thread.is_alive():
            return
        self.cancel.clear()
        self.thread = threading.Thread(target=self._run, name='misa-comparison-scan', daemon=True)
        self.thread.start()

    def stop(self):
        self.cancel.set()
        self.wake.set()
        if self.thread is not None and self.thread.ident is not None:
            self.thread.join(timeout=2.0)
        self.thread = None
        if self._uia is not None:
            try:
                self._uia.close()
            except Exception:
                pass
            self._uia = None
            self._uia_identity = None

    def set_paused(self, paused):
        """Suspend the optional native comparison scan during interaction."""
        paused = bool(paused)
        changed = paused != self.paused.is_set()
        if paused:
            self.paused.set()
            while True:
                try:
                    self.events.get_nowait()
                except queue.Empty:
                    break
        else:
            self.paused.clear()
        if changed:
            self.wake.set()

    def drain(self):
        values = []
        while True:
            try:
                values.append(self.events.get_nowait())
            except queue.Empty:
                return values

    def _run(self):
        while not self.cancel.is_set():
            if self.paused.is_set():
                self.wake.clear()
                if self.paused.is_set() and not self.cancel.is_set():
                    self.wake.wait()
                continue
            started = time.monotonic()
            try:
                observation = self.observe()
            except Exception as error:
                observation = (None if type(error).__name__ == 'Cancelled' else
                    screen_observation(status='unverified', source='live_misa_win32',
                        reason=f'{type(error).__name__}: {error}'))
            if observation is not None and not self.paused.is_set() and not self.cancel.is_set():
                self.events.put(observation)
            remaining = self.interval - (time.monotonic() - started)
            if remaining > 0:
                self.wake.clear()
                if not self.paused.is_set() and not self.cancel.is_set():
                    self.wake.wait(remaining)

    @staticmethod
    def _display_value(value):
        if value is None:
            return None
        value = str(value)
        if re.fullmatch(r'\{?[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\}?',
                        value.strip(), re.I):
            return None
        return value

    def _parse_uia_grid_page(self, page, grid, capture_box, fields):
        """Convert one current GridPattern page into canonical scalar rows."""
        if not isinstance(page, dict) or not isinstance(grid, dict):
            return None
        try:
            row_count = int(page.get('rowCount'))
            column_count = int(page.get('columnCount'))
        except (TypeError, ValueError):
            return None
        if row_count < 1 or row_count > 100 or column_count < 1 or column_count > 100:
            return None
        columns = page.get('columns')
        rows = page.get('rows')
        if (not isinstance(columns, list) or len(columns) != column_count
                or any(not str(name).strip() for name in columns)
                or not isinstance(rows, list) or len(rows) != row_count):
            return None
        grid_box = grid.get('bounds') or {}
        grid_values = tuple(grid_box.get(key) for key in ('x', 'y', 'width', 'height'))
        if (not all(isinstance(value, (int, float)) and math.isfinite(value)
                    for value in grid_values)
                or grid_values[2] <= 0 or grid_values[3] <= 0):
            return None
        indices = {}
        for field, (_, expected_names) in fields.items():
            names = {text_key(name) for name in expected_names}
            matches = [index for index, name in enumerate(columns)
                       if text_key(name) in names]
            if len(matches) == 1:
                indices[field] = matches[0]
        if not indices:
            return None

        result = {}
        semantic_bounds = {}
        seen_rows = set()
        for row in rows:
            try:
                row_id = int(row.get('index'))
            except (AttributeError, TypeError, ValueError):
                return None
            if row_id < 0 or row_id >= row_count or row_id in seen_rows:
                return None
            seen_rows.add(row_id)
            cells = row.get('cells')
            if not isinstance(cells, list) or len(cells) != column_count:
                return None
            values = {}
            bounds_by_field = {}
            for field, index in indices.items():
                cell = cells[index]
                if not isinstance(cell, dict):
                    return None
                if (cell.get('windowId') is not None
                        and cell.get('windowId') != self._uia_identity[0]):
                    return None
                if not cell.get('enabled'):
                    return None
                expected_names = {text_key(name) for name in fields[field][1]}
                if text_key(cell.get('name') or '') not in expected_names:
                    return None
                patterns = set(cell.get('patterns') or ())
                value = (self._display_value(cell.get('value'))
                         if {'Value', 'Text'} & patterns and cell.get('value') is not None
                         else None)
                if value is not None:
                    values[field] = value
                if cell.get('offscreen'):
                    if value is None:
                        return None
                    continue
                bounds = self._capture_bounds(cell.get('bounds'), capture_box)
                if bounds is None:
                    return None
                desktop_box = cell.get('bounds') or {}
                if (desktop_box.get('x', 0) < grid_values[0]
                        or desktop_box.get('y', 0) < grid_values[1]
                        or desktop_box.get('x', 0) + desktop_box.get('width', 0)
                        > grid_values[0] + grid_values[2]
                        or desktop_box.get('y', 0) + desktop_box.get('height', 0)
                        > grid_values[1] + grid_values[3]):
                    return None
                bounds_by_field[field] = bounds
            semantic_bounds[row_id] = bounds_by_field
            if values:
                result[row_id] = values
        if seen_rows != set(range(row_count)):
            return None
        self._uia_row_bounds = semantic_bounds
        return result

    @staticmethod
    def _capture_bounds(bounds, capture_box):
        """Convert one current desktop-space UIA rectangle to capture pixels."""
        box = bounds or {}
        values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
        if (not all(isinstance(value, (int, float)) and math.isfinite(value)
                    for value in values)
                or values[2] <= 0 or values[3] <= 0):
            return None
        capture_left, capture_top, capture_right, capture_bottom = capture_box
        left = math.floor(values[0])
        top = math.floor(values[1])
        right = math.ceil(values[0] + values[2])
        bottom = math.ceil(values[1] + values[3])
        if (left < capture_left or top < capture_top
                or right > capture_right or bottom > capture_bottom
                or right <= left or bottom <= top):
            return None
        return (left - capture_left, top - capture_top, right - left, bottom - top)

    @staticmethod
    def _native_text(window, bounds, capture_box):
        """Read one current native control without interpreting pixels."""
        if bounds is None:
            return None
        from . import win32
        try:
            result = win32.read_control_text(window, bounds, capture_box,
                                             allow_background=True)
        except (win32.WindowError, OSError, ValueError, TypeError):
            return None
        if result.get('status') != 'supported':
            return None
        return ScreenComparisonScanner._display_value(result.get('value'))

    def _uia_sale_readonly(self, window, capture_box):
        """Read observed sale values through current UIA, never send input."""
        from .uia_bridge import EngineError, UiaBridge

        ids = (
            'cboAccountObjectID', 'txtAccountObjectName', 'dtePostedDate',
            'dteRefDate', 'txtRefNo', 'txtInvoiceSeries', 'txtInvoiceNo',
            'dteInvoiceDate', 'txtAccountObjectAddress',
            'txtAccountObjectTaxCode', 'txtBuyer', 'txtJournalMemo',
            'txtInvAccountObjectName', 'txtInvAccountObjectAddress',
            'txtInvAccountObjectTaxCode', 'txtInvBuyer', 'txtInvJournalMemo',
            'dteInvDate', 'txtInvNo', 'txtInvSeries', 'cboPaymentMethodType',
            'creTotalTotalSaleAmountOC', 'creTotalVATAmountOC',
            'creTotalAmountOC',
        )
        identity = (window.hwnd, window.pid)
        try:
            if self._uia is None or self._uia.closed:
                self._uia = UiaBridge(self.cancel)
                self._uia_identity = None
            if self._uia_identity != identity:
                bound = self._uia.call('connect', {
                    'windowHandle': identity[0], 'processId': identity[1]})
                if (bound.get('handle'), bound.get('processId')) != identity:
                    raise EngineError('WRONG_WINDOW', 'UIA connected to another MISA window.')
                self._uia_identity = identity
            else:
                bound = self._uia.call('bind', {'windowHandle': identity[0]})
                if (bound.get('handle'), bound.get('processId')) != identity:
                    raise EngineError('WRONG_WINDOW', 'UIA bound to another MISA window.')

            values = self._uia.call('query', {
                'ids': list(ids), 'kinds': [50003, 50004]})
            def valid(item):
                return (item.get('windowId') == window.hwnd and item.get('enabled')
                        and not item.get('offscreen')
                        and self._capture_bounds(item.get('bounds'), capture_box) is not None)

            result = {}
            for item_id in ids:
                matches = [item for item in values
                           if item.get('id') == item_id and valid(item)]
                if len(matches) != 1:
                    continue
                observed = self._uia.call('read', {'token': matches[0]['token']})
                if (observed.get('windowId') != window.hwnd
                        or observed.get('id') != item_id
                        or observed.get('kind') != matches[0].get('kind')
                        or observed.get('name') != matches[0].get('name')
                        or observed.get('parentToken') != matches[0].get('parentToken')
                        or observed.get('bounds') != matches[0].get('bounds')
                        or observed.get('offscreen') or not observed.get('enabled')):
                    continue
                bounds = self._capture_bounds(observed.get('bounds'), capture_box)
                if bounds is None:
                    continue
                patterns = set(observed.get('patterns') or ())
                value = observed.get('value')
                value = (self._display_value(value)
                         if value is not None and {'Value', 'Text'} & patterns
                         else None)
                if value is None:
                    value = self._native_text(window, bounds, capture_box)
                result[item_id] = {'bounds': bounds, 'value': value}
            return result
        except (EngineError, OSError, ValueError, TypeError):
            return {}

    def _uia_sale_row_scalars(self, window, capture_box):
        """Read visible scalar grid editors when the current UIA tree is complete."""
        from .uia_bridge import EngineError

        self._uia_detail_tab = ''
        self._uia_grid_bounds = None
        self._uia_row_bounds = {}
        if self._uia is None or self._uia.closed:
            return {}
        fields = {
            'mahang': ('58', ('Mã hàng',)),
            'tenhang': ('3', ('Tên hàng',)),
            'dvt': ('6', ('ĐVT', 'Đơn vị tính')),
            'soluong': ('8', ('Số lượng',)),
            'dongia': ('9', ('Đơn giá',)),
            'thanhtien': ('11', ('Thành tiền',)),
            'thuesuatgtgt': (None, ('Thuế suất GTGT', 'Thuế GTGT',
                                    '% thuế GTGT', 'Thuế suất GTGT (%)')),
            'tienthuegtgt': (None, ('Tiền thuế GTGT', 'Tiền thuế')),
        }
        try:
            tabs = self._uia.call('query', {
                'names': ['1. Hàng tiền', '2. Thuế'], 'kinds': [50019]})
            tabs = [tab for tab in tabs
                    if tab.get('windowId') == window.hwnd
                    and tab.get('enabled') and not tab.get('offscreen')
                    and tab.get('selected')]
            if len(tabs) != 1:
                return {}
            tab = self._uia.call('read', {'token': tabs[0]['token']})
            if (tab.get('windowId') != window.hwnd
                    or tab.get('kind') != 50019
                    or tab.get('name') != tabs[0].get('name')
                    or tab.get('id') != tabs[0].get('id')
                    or tab.get('bounds') != tabs[0].get('bounds')
                    or tab.get('offscreen') or not tab.get('enabled')
                    or not tab.get('selected')
                    or 'SelectionItem' not in set(tab.get('patterns') or ())):
                return {}
            self._uia_detail_tab = str(tab.get('name') or '')
            grids = self._uia.call('query', {'ids': ['grdDetail'], 'kinds': [50025]})
            grids = [grid for grid in grids
                     if grid.get('windowId') == window.hwnd
                     and grid.get('enabled') and not grid.get('offscreen')]
            if len(grids) != 1:
                return {}
            grid = self._uia.call('read', {'token': grids[0]['token']})
            if (grid.get('windowId') != window.hwnd
                    or grid.get('id') != 'grdDetail'
                    or grid.get('kind') != 50025
                    or grid.get('bounds') != grids[0].get('bounds')
                    or grid.get('offscreen') or not grid.get('enabled')):
                return {}
            gbox = grid.get('bounds') or {}
            gx, gy, gw, gh = (gbox.get(key) for key in ('x', 'y', 'width', 'height'))
            left, top, right, bottom = capture_box
            if (not all(isinstance(value, (int, float)) for value in (gx, gy, gw, gh))
                    or gw <= 0 or gh <= 0
                    or gx < left or gy < top or gx + gw > right or gy + gh > bottom):
                return {}
            self._uia_grid_bounds = self._capture_bounds(gbox, capture_box)
            if self._uia_grid_bounds is None:
                return {}
            try:
                page = self._uia.call('grid', {
                    'token': grid['token'], 'startRow': 0, 'maximumRows': 100})
                current_grid = self._uia.call('read', {'token': grid['token']})
                if (current_grid.get('windowId') == window.hwnd
                        and current_grid.get('id') == grid.get('id')
                        and current_grid.get('kind') == grid.get('kind')
                        and current_grid.get('bounds') == grid.get('bounds')
                        and current_grid.get('enabled') and not current_grid.get('offscreen')):
                    semantic = self._parse_uia_grid_page(
                        page, current_grid, capture_box, fields)
                    if semantic is not None:
                        return semantic
            except EngineError:
                pass

            def belongs_to_grid(item):
                return self._uia.has_ancestor(item, grid.get('token'), window_id=window.hwnd)

            raw_rows = self._uia.call('query', {'kinds': [50029]})
            rows = []
            for row in raw_rows:
                try:
                    row_id = int(row.get('id'))
                except (TypeError, ValueError):
                    continue
                if row_id < 0 or not belongs_to_grid(row):
                    continue
                box = row.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                if (row.get('windowId') != window.hwnd
                        or not row.get('enabled') or row.get('offscreen')
                        or not all(isinstance(value, (int, float)) for value in values)
                        or values[2] <= 0 or values[3] <= 0
                        or values[0] < gx or values[1] < gy
                        or values[0] + values[2] > gx + gw
                        or values[1] + values[3] > gy + gh):
                    return {}
                rows.append((row_id, row))
            if (not rows or len({row_id for row_id, _ in rows}) != len(rows)
                    or sorted(row_id for row_id, _ in rows) != list(range(len(rows)))):
                return {}

            column_bounds = {}
            headers = self._uia.call('query', {'kinds': [50035]})
            for field, (_, expected_names) in fields.items():
                names = {text_key(name) for name in expected_names}
                matches = [header for header in headers
                           if header.get('windowId') == window.hwnd
                           and text_key(header.get('name') or '') in names
                           and header.get('enabled') and not header.get('offscreen')
                           and belongs_to_grid(header)]
                if len(matches) != 1:
                    continue
                current = self._uia.call('read', {'token': matches[0]['token']})
                if (current.get('windowId') != window.hwnd
                        or current.get('kind') != 50035
                        or text_key(current.get('name') or '') not in names
                        or current.get('parentToken') != matches[0].get('parentToken')
                        or current.get('bounds') != matches[0].get('bounds')
                        or not current.get('enabled') or current.get('offscreen')
                        or not belongs_to_grid(current)):
                    continue
                box = current.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                if (not all(isinstance(value, (int, float)) and math.isfinite(value)
                            for value in values)
                        or values[2] <= 0 or values[3] <= 0
                        or values[0] < gx or values[0] + values[2] > gx + gw
                        or values[1] < gy or values[1] + values[3] > gy + gh):
                    continue
                bounds = self._capture_bounds(box, capture_box)
                if bounds is not None:
                    column_bounds[field] = bounds

            result = {}
            semantic_row_bounds = {}
            for row_id, row in rows:
                current_row = self._uia.call('read', {'token': row['token']})
                if (current_row.get('windowId') != window.hwnd
                        or current_row.get('kind') != 50029
                        or current_row.get('id') != row.get('id')
                        or current_row.get('parentToken') != row.get('parentToken')
                        or current_row.get('bounds') != row.get('bounds')
                        or not current_row.get('enabled') or current_row.get('offscreen')
                        or not belongs_to_grid(current_row)):
                    return {}
                current_row_bounds = self._capture_bounds(
                    current_row.get('bounds'), capture_box)
                if current_row_bounds is None:
                    return {}
                editors = self._uia.call('query', {
                    'kinds': [50004, 50003], 'parentToken': current_row.get('token')})
                row_values = {}
                row_bounds = {}
                row_left, row_top, row_width, row_height = current_row_bounds
                row_right = row_left + row_width
                for field, header in column_bounds.items():
                    header_left, _, header_width, _ = header
                    left = max(row_left, header_left)
                    right = min(row_right, header_left + header_width)
                    if right > left:
                        row_bounds[field] = (left, row_top, right - left, row_height)
                for field, (automation_id, expected_names) in fields.items():
                    expected_names = {text_key(name) for name in expected_names}
                    matches = [item for item in editors
                               if ((automation_id is not None and item.get('id') == automation_id)
                                   or (automation_id is None and
                                       text_key(item.get('name') or '') in expected_names))
                               and text_key(item.get('name') or '') in expected_names
                               and item.get('windowId') == window.hwnd
                               and item.get('parentToken') == current_row.get('token')
                               and item.get('enabled') and not item.get('offscreen')]
                    if len(matches) != 1:
                        continue
                    current = self._uia.call('read', {'token': matches[0]['token']})
                    if (current.get('windowId') != window.hwnd
                            or (automation_id is not None and current.get('id') != automation_id)
                            or text_key(current.get('name') or '') not in expected_names
                            or current.get('parentToken') != current_row.get('token')
                            or current.get('bounds') != matches[0].get('bounds')
                            or not current.get('enabled') or current.get('offscreen')):
                        continue
                    bounds = self._capture_bounds(current.get('bounds'), capture_box)
                    if bounds is None:
                        continue
                    row_bounds[field] = bounds
                    patterns = set(current.get('patterns') or ())
                    value = (self._display_value(current.get('value'))
                             if {'Value', 'Text'} & patterns
                             and current.get('value') is not None else None)
                    if value is None:
                        value = self._native_text(window, bounds, capture_box)
                    if value is not None:
                        row_values[field] = value
                if row_bounds:
                    semantic_row_bounds[row_id] = row_bounds
                if row_values:
                    result[row_id] = row_values
            self._uia_row_bounds = semantic_row_bounds
            return result
        except (EngineError, OSError, ValueError, TypeError):
            return {}

    def observe(self):
        from . import win32
        from .screen_win32 import ScreenDesktop

        if self.paused.is_set() or self.cancel.is_set():
            return None
        window = win32.recover_window(restore=False)
        if not ScreenDesktop.matches_window(window, 'sale'):
            return screen_observation(status='unverified', hwnd=window.hwnd, pid=window.pid,
                title=window.title, source='live_misa_win32',
                reason='Cửa sổ hiện tại không phải hóa đơn bán hàng.')

        process_birth = win32.process_birth(window.pid)
        current = win32.bound_window(window)
        capture_box = win32.window_rect(current)
        client = win32.client_bounds(current, capture_box)
        if self.paused.is_set() or self.cancel.is_set():
            return None

        frame_id = uuid.uuid4().hex
        dpi = (int(win32.user32.GetDpiForWindow(window.hwnd))
               if hasattr(win32.user32, 'GetDpiForWindow') else None)
        uia_values = self._uia_sale_readonly(current, capture_box)
        uia_rows = self._uia_sale_row_scalars(current, capture_box)

        # Keep the comparison schema's canonical keys while resolving the
        # currently observed sale-form AutomationIds through native UIA.
        field_ids = {
            'cboAccountObjectID': 'cboAccountObjectID',
            'txtAccountObjectName': 'txtAccountObjectName',
            'txtAccountObjectAddress': 'txtAccountObjectAddress',
            'txtAccountObjectTaxCode': 'txtAccountObjectTaxCode',
            'txtBuyer': 'txtBuyer',
            'txtJournalMemo': 'txtJournalMemo',
            'dtePostedDate': 'dtePostedDate',
            'dteRefDate': 'dteRefDate',
            'txtRefNo': 'txtRefNo',
            'txtInvoiceSeries': 'txtInvoiceSeries',
            'txtInvoiceNo': 'txtInvoiceNo',
            'dteInvoiceDate': 'dteInvoiceDate',
            'txtInvAccountObjectName': 'txtAccountObjectName',
            'txtInvAccountObjectAddress': 'txtAccountObjectAddress',
            'txtInvAccountObjectTaxCode': 'txtAccountObjectTaxCode',
            'txtInvBuyer': 'txtBuyer',
            'txtInvJournalMemo': 'txtJournalMemo',
            'dteInvDate': 'dteInvoiceDate',
            'txtInvNo': 'txtInvoiceNo',
            'txtInvSeries': 'txtInvoiceSeries',
            'cboPaymentMethodType': 'cboPaymentMethodType',
        }
        fields = {}
        semantic_bounds = {}
        candidates = {}
        for observed, canonical in field_ids.items():
            item = uia_values.get(observed)
            if item:
                candidates.setdefault(canonical, []).append(item)
        for canonical, items in candidates.items():
            values = {item['value'] for item in items if item.get('value') is not None}
            if len(values) == 1:
                fields[canonical] = values.pop()
            if len(items) == 1 and items[0].get('bounds') is not None:
                semantic_bounds[canonical] = items[0]['bounds']

        header_fields = (
            'cboAccountObjectID', 'txtAccountObjectName', 'txtAccountObjectAddress',
            'txtAccountObjectTaxCode', 'txtBuyer', 'txtJournalMemo', 'dteInvoiceDate',
            'txtInvoiceNo', 'txtInvoiceSeries', 'cboPaymentMethodType',
            'dtePostedDate', 'dteRefDate', 'txtRefNo')
        total_fields = (
            'creTotalTotalSaleAmountOC', 'creTotalVATAmountOC', 'creTotalAmountOC')
        expected_row_fields = (
            {'mahang', 'tenhang', 'dvt', 'soluong', 'dongia', 'thanhtien'}
            if text_key(self._uia_detail_tab) == text_key('1. Hàng tiền') else
            {'thuesuatgtgt', 'tienthuegtgt'}
            if text_key(self._uia_detail_tab) == text_key('2. Thuế') else set())
        reasons = []
        if not uia_values:
            reasons.append('Không đọc được control native của phiếu.')
        if not expected_row_fields:
            reasons.append('Không xác định được tab chi tiết qua UIA.')

        for field in header_fields:
            if field not in fields:
                value = self._native_text(current, semantic_bounds.get(field), capture_box)
                if value is not None:
                    fields[field] = value

        lines = []
        if uia_rows or self._uia_row_bounds:
            row_ids = set(uia_rows) | set(self._uia_row_bounds)
            lines = [{} for _ in range(max(12, max(row_ids) + 1))]
            for row_index, values in uia_rows.items():
                lines[row_index].update(values)
            for row_index, bounds_by_field in self._uia_row_bounds.items():
                if row_index >= len(lines):
                    continue
                for field in expected_row_fields:
                    if field in lines[row_index]:
                        continue
                    value = self._native_text(current, bounds_by_field.get(field), capture_box)
                    if value is not None:
                        lines[row_index][field] = value
        lines = [row for row in lines if row]
        if expected_row_fields and not uia_rows:
            reasons.append('Bảng chi tiết không cung cấp dữ liệu native.')
        elif expected_row_fields and any(
                not expected_row_fields <= set(row) for row in uia_rows.values()):
            reasons.append('Bảng chi tiết native chưa đủ cột.')

        totals = {}
        for field in total_fields:
            item = uia_values.get(field)
            if not item:
                continue
            value = item.get('value')
            if value is None:
                value = self._native_text(current, item.get('bounds'), capture_box)
            if value is not None:
                totals[field] = value

        try:
            latest = win32.bound_window(current)
            if (win32.process_birth(latest.pid) != process_birth
                    or win32.window_rect(latest) != capture_box):
                return screen_observation(
                    status='unverified', hwnd=latest.hwnd, pid=latest.pid,
                    title=latest.title, process_birth=process_birth,
                    frame_id=frame_id, client_bounds=client, dpi=dpi,
                    source='live_misa_win32',
                    reason='Cửa sổ hoặc tiến trình đổi trong lúc đọc native.')
        except win32.WindowError as error:
            return screen_observation(
                status='unverified', hwnd=current.hwnd, pid=current.pid,
                title=current.title, process_birth=process_birth,
                frame_id=frame_id, client_bounds=client, dpi=dpi,
                source='live_misa_win32', reason=str(error))

        native_data = bool(fields or lines or totals)
        if not native_data:
            reasons.append('MISA không cung cấp dữ liệu qua UIA/Win32 hiện tại.')
        return screen_observation(
            status='observed' if native_data else 'unverified', hwnd=current.hwnd,
            pid=current.pid, title=current.title, process_birth=process_birth,
            frame_id=frame_id, client_bounds=client, dpi=dpi, fields=fields,
            rows=lines, totals=totals, source='live_misa_win32',
            reason='; '.join(dict.fromkeys(reasons)))


def comparison_key(field, row=None):
    return f'line:{row}:{field}' if row is not None else field


def expectations(invoice):
    if invoice is None:
        return []
    fields = []

    def add(field, label, expected, *, group, row=None, note=''):
        fields.append(dict(key=comparison_key(field, row), field=field, label=label,
                           expected=expected, group=group, row=row, note=note))

    for field, label, attr in TOTAL_FIELDS:
        add(field, label, format_money(getattr(invoice, attr)), group='Tổng hóa đơn')
    day = (date.fromisoformat(invoice.issued).strftime('%d/%m/%Y')
           if invoice.issued else '—')
    for field, label, value in (
            ('txtAccountObjectName', 'Khách hàng', invoice.buyer.name),
            ('dtePostedDate', 'Ngày hạch toán', day), ('dteRefDate', 'Ngày chứng từ', day),
            ('txtInvoiceNo', 'Số hóa đơn', invoice.number)):
        add(field, label, value, group='Thông tin chung')
    for row, line in enumerate(invoice.lines, 1):
        group = f'Dòng {row} · {line.name}'
        add('mahang', 'Mã hàng', line.code or '—', group=group, row=row,
            note='Mã XML và mã danh mục có thể khác nhau; mã ghép được ghi riêng.')
        for field, label, attr, rule in LINE_FIELDS:
            value = getattr(line, attr)
            add(field, label, format_money(value) if rule == 'decimal' else value or '—',
                group=group, row=row,
                note='XML không có giá trị này.' if value is None or value == '' else '')
    return fields


def compatible_state(invoice, state):
    return bool(invoice and state and state.get('invoice_source') == invoice.source
                and state.get('invoice_number') == invoice.number
                and state.get('invoice_series') == invoice.series
                and state.get('invoice_issued') == invoice.issued)


def comparison_rows(invoice, state):
    state = state if compatible_state(invoice, state) else {}
    readbacks = state.get('readbacks', {})
    mapping = state.get('item_mappings', {})
    result = []
    for spec in expectations(invoice):
        reading = readbacks.get(spec['key'], {})
        status = reading.get('status', 'unverified')
        actual = reading.get('actual_raw')
        if actual is None or not str(actual).strip() or reading.get('invalidated'):
            status = 'unverified'
        if spec['expected'] == '—':
            status = 'unverified'
        note = spec['note']
        if spec['field'] == 'mahang':
            chosen = mapping.get(str(spec['row']), {})
            if chosen:
                note = 'Mã danh mục đã ghép: ' + chosen.get('code', '') + '. ' + note
            # A catalog mapping is not literal equality to the supplier's XML code.
            if actual and status == 'matched' and actual != spec['expected']:
                status = 'mapped'
        result.append(dict(spec, actual=actual, status=status,
                           observed_at=reading.get('evaluated_at', ''),
                           reason=reading.get('reason', ''), note=note))
    return result


