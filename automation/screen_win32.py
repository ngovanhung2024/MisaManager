"""Guarded native UIA/Win32 automation for MISA surfaces."""
from __future__ import annotations

from contextlib import contextmanager
from copy import deepcopy
import json
import math
import re
import time
import uuid
from dataclasses import dataclass, field, replace
from datetime import datetime, timezone

from .domain import Cancelled, NeedsAttention, normalize, scalar_text
from .verification import Reading, utc_now, normalized, text_key
from .observation_wait import read_until
from .label_matching import (label_key, label_hint_matches, label_candidates, label_is_confirmed,
                             tab_candidates, word_evidence)
from .inventory_menu import menu_caption_words
from .inventory_screen import InventoryScreen
from .performance import span, tracked, record, record_image, current_trace
from . import win32


def _box(left, top, right, bottom):
    return (max(0, round(left)), max(0, round(top)),
            max(1, round(right - left)), max(1, round(bottom - top)))


def _word_overlap(word_box, bounds):
    wx1, wy1, wx2, wy2 = word_box
    bx, by, bw, bh = bounds
    return max(0, min(wx2, bx + bw) - max(wx1, bx)) * max(0, min(wy2, by + bh) - max(wy1, by))


def _same_word_region(left, right):
    x0, y0, x1, y1 = left.box
    a0, b0, a1, b1 = right.box
    intersection = max(0, min(x1, a1) - max(x0, a0)) * max(0, min(y1, b1) - max(y0, b0))
    union = max(1, (x1 - x0) * (y1 - y0)) + max(1, (a1 - a0) * (b1 - b0)) - intersection
    return intersection / union >= .75


@dataclass(frozen=True)
class ScreenWord:
    """A text-bearing UIA element with bounds relative to the current window."""
    text: str
    box: tuple

    @property
    def cx(self):
        return (self.box[0] + self.box[2]) / 2

    @property
    def cy(self):
        return (self.box[1] + self.box[3]) / 2


def merge_frame_words(words, frame_id=''):
    """Deduplicate overlapping native text elements without synthesizing text."""
    result = []
    for word in words:
        if not word.text.strip():
            continue
        duplicate = next((old for old in result
                          if _same_word_region(old, word)), None)
        if duplicate is None:
            result.append(word)
    return tuple(sorted(result, key=lambda item: (item.cy, item.cx)))


def _word_evidence(word):
    return word_evidence(word)


@dataclass
class ScreenControl:
    desktop: object
    id: str = ''
    name: str = ''
    kind: int = 50020
    bounds: tuple = (0, 0, 1, 1)
    enabled: bool = True
    offscreen: bool = False
    selected: bool = False
    toggle: int = 0
    role: str = ''
    parent_key: str = ''
    value_override: object = field(default=None, repr=False)
    observed_window_id: int = 0
    observed_frame_id: str = ''
    target: dict = field(default_factory=dict, repr=False)

    def __post_init__(self):
        self.observed_window_id = getattr(self.desktop.window, 'hwnd', 0)
        self.observed_frame_id = getattr(self.desktop, '_frame_id', '')

    @property
    def visible(self):
        return not self.offscreen and self.bounds[2] > 0 and self.bounds[3] > 0

    @property
    def value(self):
        if self.value_override is not None:
            return str(self.value_override)
        return self.desktop.text_in(self.bounds)

    @property
    def window_id(self):
        return self.observed_window_id


class ScreenDesktop(InventoryScreen):
    """Native UIA/Win32 locator with guarded mouse and keyboard input."""

    LABEL_ALIASES = {
        'Khách hàng': ('khachhang',), 'Người nộp': ('nguoinop',),
        'Địa chỉ': ('diachi',), 'Số phiếu thu': ('sophieuthu', 's6phieuthu'),
        'Lý do nộp': ('lydonop',), 'Ngày hạch toán': ('ngayhachtoan',),
        'Ngày chứng từ': ('ngaychungtu', 'ngaychuingtui'),
        'Ký hiệu HĐ': ('kyhieuhd', 'kyhieuhdon'),
        'Ngày hóa đơn': ('ngayhoadon',), 'Mã số thuế': ('masothue',),
        'Mã (*)': ('ma',), 'Tên (*)': ('ten',), 'Kho ngầm định': ('khongamdinh',),
        'Tài khoản kho': ('taikhoankho',), 'TK doanh thu': ('tkdoanhthu',),
        'Thuế suất GTGT (%)': ('thuesuatgtgt',), 'ĐVT chính': ('dvtchinh',),
        'Tên hàng': ('tenhang',), 'Mã hàng': ('mahang',), 'ĐVT': ('dvt', 'donvitinh'),
        'Số lượng': ('soluong', 'soluigng', 's8ludng'), 'Đơn giá': ('dongia',),
        'Thành tiền': ('thanhtien',), 'TK tiền/chi phí': ('tktienchiphi',),
        'Tiền thuế GTGT': ('tienthuegtgt', 'tienthue'),
        'Tổng tiền hàng': ('tongtienhang',), 'Tổng tiền thanh toán': ('tongtienthanhtoan',),
        'Hình thức thu': ('hinhthucthu',), 'Loại bán hàng': ('loaibanhang',),
        # Keep action aliases narrow: broad variants such as ``tam`` also
        # match unrelated labels like “Tạm tính”.
        'Thêm': ('them', 'tlhem', 'tihem', 'tlem', 'tthem', 'themmoi', '+them'),
        'Bán hàng': ('banhang', 'binhirg', 'banhing'),
        'Số chứng từ': ('sochungtu', 'sochuingtui', 'sotdong', 'sochugtu'),
        'Số hóa đơn': ('sohoadon', 's6hoadon', 's6hbadon', 'soltodoi', 'sohdon'),
        'Phiếu thu': ('phieuthu', 'phieuthiu', 'phieutiu'),
        'Hóa đơn': ('hoadon',),
        '1. Hàng tiền': ('1hangtien', '1hangtin'),
        '2. Thuế': ('2thue', '2thuegtgt'),
    }

    # Only actual caption variants authorize an action. LABEL_ALIASES contains
    # Legacy caption aliases are locate hints only; exact native text is required.
    LABEL_VARIANTS = {
        'Mã (*)': ('Mã',), 'Tên (*)': ('Tên',),
        'Ký hiệu HĐ': ('Ký hiệu hóa đơn',),
        'ĐVT': ('Đơn vị tính',),
        'Thuế suất GTGT (%)': ('Thuế suất GTGT',),
        'Tiền thuế GTGT': ('Tiền thuế',),
        'Thêm': ('Thêm mới',),
        '1. Hàng tiền': ('1. Hàng tền',),
        '2. Thuế': ('2. Thuế GTGT',),
        'Thuế suất GTGT': ('% thuế GTGT', 'Thuế suất GTGT (%)'),
    }

    COLUMN_LABELS = {
        'mahang': ('Mã hàng',), 'tenhang': ('Tên hàng',), 'dvt': ('ĐVT', 'Đơn vị tính'),
        'soluong': ('Số lượng',), 'soluongton': ('Số lượng tồn',), 'dongia': ('Đơn giá',),
        'thanhtien': ('Thành tiền',), 'tktienchiphi': ('TK tiền/chi phí',),
        'tkdoanhthu': ('TK doanh thu',), 'thuesuatgtgt': ('Thuế suất GTGT', 'Thuế GTGT', '% thuế GTGT', 'Thuế suất GTGT (%)'),
        'diengiaithue': ('Diễn giải thuế',), 'tkthuegtgt': ('TK thuế GTGT',),
        'tienthuegtgt': ('Tiền thuế GTGT', 'Tiền thuế'),
    }

    FIELD_LAYOUT = {
        'cboAccountObjectID': ('Khách hàng', 6, -2, 190, 25),
        'txtAccountObjectName': ('Khách hàng', 6, -2, 190, 25),
        'txtAccountObjectTaxCode': ('Mã số thuế', 6, -2, 190, 25),
        'txtCompanyTaxCode': ('Mã số thuế', 6, -2, 190, 25),
        'txtTaxCode': ('Mã số thuế', 6, -2, 190, 25),
        'txtAccountObjectAddress': ('Địa chỉ', 6, -2, 650, 25),
        'txtRefNo': ('Số phiếu thu', 6, -2, 110, 25),
        'txtJournalMemo': ('Lý do nộp', 17, -2, 760, 25),
        'dtePostedDate': ('Ngày hạch toán', 6, -2, 92, 25),
        'dteRefDate': ('Ngày chứng từ', 6, -2, 92, 25),
        'txtInvoiceSeries': ('Ký hiệu HĐ', 6, -2, 130, 25),
        'txtInvoiceNo': ('Số hóa đơn', 6, -2, 110, 25),
        'dteInvoiceDate': ('Ngày hóa đơn', 6, -2, 92, 25),
        'txtInventoryItemCode': ('Mã (*)', 6, -2, 210, 25),
        'txtInventoryItemName': ('Tên (*)', 6, -2, 510, 25),
        'cboStockID': ('Kho ngầm định', 6, -2, 260, 25),
        'cboInventoryAccount': ('Tài khoản kho', 6, -2, 160, 25),
        'cboRevenueAccount': ('TK doanh thu', 6, -2, 160, 25),
        'cboVATRate': ('Thuế suất GTGT (%)', 6, -2, 160, 25),
        'cboUnitID': ('ĐVT chính', 6, -2, 160, 25),
    }

    # MISA revisions have renamed several inventory-form controls while
    # keeping their semantic fields unchanged. Keep workflow IDs stable and
    # resolve the current native AutomationId through this compatibility map.
    FIELD_AUTOMATION_ID_ALIASES = {
        'cboUnitID': ('cboUnit',),
        'cboStockID': ('cboDefaultStock',),
        'cboVATRate': ('cboTaxRate',),
        'cboRevenueAccount': ('cboSaleAccount',),
    }

    CHECKBOXES = {
        'chkIsSaleWithOutward': ('Kiêm phiếu xuất kho',),
        'chkIncludeInvoice': ('Lập kèm hóa đơn',),
    }

    TOTALS = {
        'creTotalTotalSaleAmountOC': 'Tổng tiền hàng',
        'creTotalVATAmountOC': 'Tiền thuế GTGT',
        'creTotalAmountOC': 'Tổng tiền thanh toán',
    }

    # Read-only editors observed on the loaded sale form. These IDs are safe
    # for current UIA readback but must never become writable workflow fields.
    OBSERVED_SALE_READONLY_FIELDS = {
        'txtInvAccountObjectName': 'Khách hàng',
        'txtInvAccountObjectAddress': 'Địa chỉ',
        'txtInvAccountObjectTaxCode': 'Mã số thuế',
        'txtInvBuyer': 'Người mua',
        'txtInvJournalMemo': 'Diễn giải',
        'dteInvDate': 'Ngày hóa đơn',
        'txtInvNo': 'Số hóa đơn',
        'txtInvSeries': 'Ký hiệu HĐ',
        'cboPaymentMethodType': 'Hình thức thu',
    }
    OBSERVED_SALE_READONLY_IDS = frozenset(OBSERVED_SALE_READONLY_FIELDS)

    SALE_COLUMNS = {
        'mahang': ('mahang',), 'tenhang': ('tenhang',), 'dvt': ('dvt', 'donvitinh'),
        'soluong': ('soluong', 'soluigng', 's6luong', 's8ludng'), 'dongia': ('dongia',),
        'thanhtien': ('thanhtien',), 'tktienchiphi': ('tktienchiphi',),
        'tkdoanhthu': ('tkdoanhthu',), 'thuesuatgtgt': ('thuesuatgtgt', 'thuegtgt'),
        'diengiaithue': ('diengiaithue',), 'tkthuegtgt': ('tkthuegtgt',),
        'tienthuegtgt': ('tienthuegtgt', 'tienthue'),
    }

    TAB_LABELS = ('Phiếu thu', 'Phiếu xuất', 'Hóa đơn', '1. Hàng tiền',
                  '2. Thuế', '3. Giá vốn', '4. Thống kê', '5. Khác')
    # Small fixed tabs can lose a Vietnamese tone mark after DPI scaling. The
    # tab label remains a locate-only caption; the active state and content
    # are still required before any input or layout proof is accepted.
    LOW_CONTRAST_TABS = frozenset(('1. Hàng tiền', '2. Thuế', '3. Giá vốn',
                                   '4. Thống kê', '5. Khác'))

    ADD_HEADER_STEPS = frozenset(('add.open', 'add.receipt', 'add.customer',
                                 'add.posted_date', 'add.voucher_date', 'add.invoice'))
    ADD_HEADER_FIELDS = frozenset(('cboAccountObjectID', 'txtAccountObjectName',
                                  'dtePostedDate', 'dteRefDate', 'txtInvoiceNo'))
    HEADER_VALUE_RULES = {
        'cboAccountObjectID': 'exact', 'txtAccountObjectName': 'text',
        'dtePostedDate': 'date', 'dteRefDate': 'date', 'txtInvoiceNo': 'invoice_number',
    }
    INVENTORY_VALUE_RULES = {
        'txtInventoryItemCode': 'exact', 'txtInventoryItemName': 'text',
        'cboUnitID': 'unit', 'cboStockID': 'exact', 'cboVATRate': 'tax_rate',
    }
    FIXED_CAPTIONS = frozenset([spec[0] for spec in FIELD_LAYOUT.values()]
                              + list(TOTALS.values())
                              + [name for names in COLUMN_LABELS.values() for name in names]
                              + [name for names in CHECKBOXES.values() for name in names]
                              + ['Số chứng từ', 'Đồng ý', 'Hủy bỏ', 'Đóng'])
    SALES_LIST_COLUMNS = ('Số chứng từ', 'Số hóa đơn', 'Ký hiệu HĐ',
                          'Ngày chứng từ', 'Ngày hạch toán', 'Khách hàng', 'Tổng tiền hàng')
    SALES_DOCUMENT_COLUMNS = frozenset(('Số chứng từ', 'Số hóa đơn',
                                       'Ngày chứng từ', 'Ngày hạch toán'))

    def __init__(self, cancel, *, ledger=None):
        self.cancel = cancel
        self.ledger = ledger
        self.recovery = None
        if ledger is not None:
            from .tab_recovery import TabRecovery
            self.recovery = TabRecovery(self, ledger)
        self.window = None
        self.pid = None
        # UIA owns semantic text and Win32 supplies a guarded read-only fallback.
        # The screenshot is retained only for HWND/PID/DPI/bounds validation.
        self._frame = None
        self._frame_kind = None
        self._frame_fields = ()
        self._frame_tab = ''
        self._field_scan_hints = {}
        self._tab_scan_hints = []
        self._field_hint_context = None
        self._frame_at = 0.0
        self._frame_id = ''
        self._frame_window = None
        self._frame_context = {}
        self._frame_uia_elements = None
        self._reframing = False
        self._detail_reads = {}
        self._grid_text_reads = {}
        self._uia_find_row_reads = {}
        self._field_observations = []
        self._captured_at = utc_now()
        self.run_token = uuid.uuid4().hex
        self.input_epoch = 0
        self.last_input_at = utc_now()
        self._uia_readback = None
        self._inventory_menu_open = False
        self._item_row_proof = None
        self._popup_tab_proof = None
        self._locating = False
        self._mapping = False
        self.checkpoint = ''
        self.last_action = {}
        self._selected_tabs = set()
        self._surface_kind = None
        # Direct control may use UIA patterns against an iconic MISA window.
        # Coordinate and SendInput paths remain foreground-only.
        self.background_control = False
        self._selected_find = set()
        self._layout_proof = None
        self._metrics = {'frames': 0, 'screen_cache_hits': 0, 'input_clicks': 0,
                         'input_keys': 0, 'input_texts': 0, 'last_ms': 0.0,
                         'total_ms': 0.0,
                         'native_frames': 0,
                         'frame_repositions': 0,
                         'target_reuses': 0,
                         'uia_value_sets': 0, 'uia_value_fallbacks': 0,
                         'uia_query_cache_hits': 0, 'uia_query_cache_misses': 0,
                         'uia_bind_reuses': 0,
                         'uia_option_fallbacks': 0, 'uia_scrolls': 0,
                         'uia_scroll_fallbacks': 0, 'uia_find_row_fallbacks': 0,
                         'uia_sale_row_fallbacks': 0, 'uia_grid_fallbacks': 0}
        for stage in ('capture', 'fingerprint', 'mapping'):
            self._metrics[f'{stage}_last_ms'] = 0.0
            self._metrics[f'{stage}_total_ms'] = 0.0

    def _check(self):
        if self.cancel.is_set():
            raise Cancelled('Đã dừng thao tác; chứng từ hiện tại được giữ lại.')

    def _region_run(self):
        return None

    def record_region_verification(self, result):
        return None

    def confirm_region_step(self):
        return None

    def _record_region_label(self, label):
        return None

    def _native_words(self):
        """Read visible text from the current UIA tree in the pinned frame."""
        self._guard_frame()
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        kinds = (50020, 50000, 50019, 50035, 50007, 50004, 50003, 50029, 50013)
        try:
            # Most MISA surfaces fit in one bounded query. This cuts the
            # per-frame UIA round trips from nine to one.
            elements = self._uia_readback.query(self, kinds=kinds, cache=True)
        except Exception as error:
            if isinstance(error, Cancelled):
                raise
            elements = []
            self.last_action.setdefault('native_query_errors', []).append(
                {'kinds': list(kinds), 'error': str(error)})
            # Preserve the older narrow-query path for unusually large trees.
            for kind in kinds:
                try:
                    elements.extend(self._uia_readback.query(self, kinds=(kind,)))
                except Exception as narrow_error:
                    if isinstance(narrow_error, Cancelled):
                        raise
                    self.last_action.setdefault('native_query_errors', []).append(
                        {'kind': kind, 'error': str(narrow_error)})
        self._frame_uia_elements = tuple(elements)
        left, top, right, bottom = self._frame[0]
        words = []
        seen = set()
        for element in elements:
            if (element.get('windowId') != self.window.hwnd or element.get('offscreen')
                    or not element.get('enabled', True)):
                continue
            box = element.get('bounds') or {}
            values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
            if (not all(isinstance(value, (int, float)) and math.isfinite(value)
                        for value in values) or values[2] <= 0 or values[3] <= 0):
                continue
            x, y, width, height = values
            if x < left or y < top or x + width > right or y + height > bottom:
                continue
            kind = int(element.get('kind') or 0)
            text = element.get('name') or ''
            if kind in (50004, 50003, 50029):
                text = element.get('value') or text
            text = ' '.join(str(text).split())
            if not text or re.fullmatch(r'\{?[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\}?', text, re.I):
                continue
            local = (round(x - left), round(y - top), round(width), round(height))
            key = (kind, str(element.get('id') or ''), text, local)
            if key in seen:
                continue
            seen.add(key)
            words.append(ScreenWord(text=text, box=(local[0], local[1],
                               local[0] + local[2], local[1] + local[3])))
        return merge_frame_words(words, self._frame_id)

    def _native_selected_tabs(self):
        if self._uia_readback is None or not self.is_window('sale'):
            return ''
        selected = []
        try:
            tabs = ([item for item in self._frame_uia_elements
                     if int(item.get('kind') or 0) == 50019]
                    if self._frame_uia_elements is not None
                    else self._uia_readback.query(self, kinds=(50019,)))
            for name in self.TAB_LABELS:
                wanted = normalize(name)
                if any(item.get('selected') and normalize(item.get('name') or '') == wanted
                       for item in tabs):
                    selected.append(name)
        except (Cancelled, NeedsAttention, win32.WindowError):
            # Keep the guarded exact-token fallback for providers that cannot
            # return the complete TabItem set in one query.
            for name in self.TAB_LABELS:
                try:
                    item = self._uia_tab_state(name)
                except (Cancelled, NeedsAttention, win32.WindowError):
                    item = None
                if item and item.get('selected'):
                    selected.append(name)
        return '|'.join(sorted(selected)) or ('unobserved' if self.is_window('sale') else 'not_applicable')

    def _scan_native_frame(self, requested_kind, *, field_ids=(), tab=''):
        self._check()
        self.sync(kind=requested_kind)
        try:
            picture, capture_box, context, captured_at = self._capture_picture('native_locate_or_verify')
            client = win32.client_bounds(self.window, capture_box)
        except win32.WindowError as error:
            raise NeedsAttention(str(error)) from error
        context.update(client_bounds=list(client), scan_scope='native', scan_fields=list(field_ids),
                       scan_tab=tab or '', scan_regions=[], scan_expanded=False,
                       observed_tab='unobserved')
        self._frame = (capture_box, picture.size, context['frame_id'], (), picture)
        self._frame_window = (self.window.hwnd, self.window.pid)
        self._frame_context = context
        self._frame_id = context['frame_id']
        self._captured_at = captured_at
        self._frame_kind = requested_kind
        self._frame_fields = tuple(field_ids)
        self._frame_tab = tab or ''
        self._frame_at = time.monotonic()
        self._metrics['frames'] += 1
        words = self._native_words()
        context['observed_tab'] = self._native_selected_tabs()
        self._frame_context = context
        self._frame = (capture_box, picture.size, context['frame_id'], words, picture)
        self._guard_frame()
        return words

    def attach(self):
        self._check()
        try:
            window = None
            # During reload the MISA main frame may regain foreground while
            # the run-owned voucher/dialog is still open. The ledger is the
            # authoritative surface identity for this workflow, so prefer its
            # latest visible surface before generic window discovery.
            if self.ledger is not None:
                for entry in reversed(self.ledger.open_surfaces()):
                    candidate = win32.MisaWindow(entry['hwnd'], entry['pid'], entry['title'])
                    if win32.readable_window(candidate):
                        window = candidate
                        break
            if window is None:
                window = win32.recover_window(restore=not self.background_control)
            win32.check_permission(window)
            if (not self.background_control
                    and not win32.is_foreground(window)):
                win32.activate(window)
        except (win32.WindowError, win32.MisaNotRunning) as error:
            raise NeedsAttention(str(error)) from error
        self.window, self.pid = window, window.pid
        self._invalidate()
        self.sync()

    def attach_main(self):
        self._check()
        window = win32.find_misa()
        win32.check_permission(window)
        win32.activate(window)
        # A modal child can be canceled just before reload returns here. The
        # main frame remains disabled briefly while MISA transfers activation;
        # wait for that native state to settle before rejecting the switch.
        deadline = time.monotonic() + 5.0
        while not win32.user32.IsWindowEnabled(window.hwnd):
            self._check()
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise NeedsAttention('Trang chính bị hộp thoại có sẵn chặn; không bỏ dữ liệu ngoài lượt chạy.')
            time.sleep(min(.05, remaining))
        self.window, self.pid = window, window.pid
        self._invalidate()
        self.sync(kind='main')

    def sync(self, *, kind=None):
        self._check()
        if self.window is None:
            self.attach()
            return
        if getattr(self, 'direct_control', False):
            # Direct MCP control follows the currently discoverable MISA
            # surface. HWND/PID changes are a new target, not a stale-session
            # error.
            try:
                current = win32.recover_window(restore=not self.background_control)
                win32.check_permission(current)
            except (win32.WindowError, win32.MisaNotRunning) as error:
                raise NeedsAttention(str(error)) from error
            previous_identity = (self.window.hwnd, self.pid)
            self.window, self.pid = current, current.pid
            if (self.window.hwnd, self.pid) != previous_identity:
                self._invalidate()
                self._selected_tabs.clear()
                self._surface_kind = None
            if (not self.background_control
                    and not win32.is_foreground(self.window)):
                win32.activate(self.window)
            if kind and not self.is_window(kind):
                raise NeedsAttention(f'Dang co cua so khac: {self.window.title}. Can {kind}.')
            return
        previous_identity = (self.window.hwnd, self.window.pid)
        try:
            self.window = win32.bound_window(self.window)
        except win32.WindowError as error:
            raise NeedsAttention(str(error)) from error
        if self.window.pid != self.pid:
            raise NeedsAttention('Phiên MISA đã thay đổi.')
        if (self.window.hwnd, self.window.pid) != previous_identity:
            self._selected_tabs.clear()
            self._item_row_proof = None
            self._popup_tab_proof = None
            self._inventory_menu_open = False
        current_surface = next((name for name in ('main', 'sale', 'find', 'new_item')
                                if self.is_window(name)), None)
        if current_surface != self._surface_kind:
            self._selected_tabs.clear()
            self._item_row_proof = None
            self._popup_tab_proof = None
            self._inventory_menu_open = False
            self._surface_kind = current_surface
        elif (self._frame is not None and current_surface == 'sale'
              and not getattr(self, 'direct_control', False)):
            self._refresh_selected_tab_state()
        if self.ledger:
            self.ledger.observe(self)
        if kind and not self.is_window(kind):
            raise NeedsAttention(f'Đang có cửa sổ khác: {self.window.title}. Cần {kind}.')

    def _refresh_selected_tab_state(self):
        """Invalidate a cached frame when the user changes MISA's active tab."""
        expected = str(self._frame_context.get('observed_tab') or '')
        if expected in ('', 'unobserved', 'not_applicable') or self._uia_readback is None:
            return
        try:
            from .uia_bridge import EngineError
            self._uia_readback._bind(self)
            items = self._uia_readback.bridge.call('query', {'kinds': [50019]})
            selected = sorted({name for name in self.TAB_LABELS
                               if any(item.get('windowId') == self.window.hwnd
                                      and item.get('selected')
                                      and normalize(item.get('name') or '') == normalize(name)
                                      for item in items)})
            if '|'.join(selected) != expected:
                self._selected_tabs.clear()
                self._invalidate()
        except (Cancelled, NeedsAttention, win32.WindowError, EngineError):
            # A provider that cannot expose SelectionItem state must not make
            # an otherwise valid workflow fail; the next native frame is fresh.
            return

    def _background_bridge(self):
        """Bind the UIA engine without capturing or activating an iconic window."""
        if self.window is None:
            raise NeedsAttention('Chưa gắn được cửa sổ MISA nền.')
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        self._uia_readback._bind(self)
        return self._uia_readback.bridge

    def _background_element(self, target):
        """Resolve one unique UIA element by semantic identity, including offscreen controls."""
        bridge = self._background_bridge()
        kind = str(target.get('type') or '')
        names, ids, kinds = [], [], []
        if kind == 'field':
            ids = [target.get('id', '')]
            ids.extend(self.FIELD_AUTOMATION_ID_ALIASES.get(target.get('id', ''), ()))
            if target.get('id') == 'txtInvoiceNo':
                ids.append('txtInvNo')
            kinds = [50004, 50003]
        elif kind == 'find_filter':
            names, kinds = [target.get('label', '')], [50004, 50003]
        elif kind in ('tab', 'word', 'fixed_button', 'item_dialog_button', 'sales_add', 'recovery'):
            default_name = 'Thêm' if kind == 'sales_add' else ''
            names, kinds = [target.get('text') or target.get('name') or default_name], [
                50019 if kind == 'tab' else 50000]
            known = {
                ('sales_add', 'Thêm'): 'mnuAdd',
                ('fixed_button', 'Đóng'): 'mnuClose',
                ('item_dialog_button', 'Cất'): 'mnuSave',
                ('item_dialog_button', 'Hủy bỏ'): 'btnCancel',
                ('item_dialog_button', 'Đồng ý'): 'btnOK',
            }
            if (kind, names[0]) in known:
                ids.append(known[(kind, names[0])])
        elif kind == 'checkbox':
            ids, kinds = [target.get('id', '')], [50002]
        elif kind == 'radio':
            names, kinds = [target.get('text') or target.get('name') or ''], [50013]
        elif kind in ('option', 'catalog_option'):
            names, kinds = [target.get('text') or target.get('name') or ''], [50007, 50029]
        elif kind == 'sale_item_cell':
            ids = [{'mahang': '58', 'tenhang': '3', 'dvt': '6', 'soluong': '8',
                    'dongia': '9', 'thanhtien': '11'}.get(target.get('column'), '')]
            kinds = [50003, 50004]
        elif kind == 'native_control':
            if target.get('id'):
                ids = [target['id']]
            if target.get('name'):
                names = [target['name']]
            kinds = [int(target.get('kind') or 0)]
        else:
            raise NeedsAttention('Thao tác nền cần target semantic UIA, không dùng tọa độ.')
        if not any(ids) and not any(names):
            raise NeedsAttention('Target UIA nền thiếu AutomationId hoặc tên control.')
        query = {'kinds': [value for value in kinds if value]}
        if ids:
            query['ids'] = list(dict.fromkeys(value for value in ids if value))
        if names and not ids:
            query['names'] = [value for value in names if value]
        if target.get('parent_token'):
            query['parentToken'] = target['parent_token']
        elements = [item for item in bridge.call('query', query)
                    if item.get('windowId') == self.window.hwnd and item.get('enabled')]
        if names:
            wanted = {normalize(value) for value in names if value}
            elements = [item for item in elements
                        if not wanted or normalize(item.get('name') or '') in wanted]
        if kind == 'field' and target.get('label'):
            wanted = normalize(target['label'])
            elements = [item for item in elements
                        if not item.get('name') or normalize(item['name']) == wanted]
        if target.get('name'):
            elements = [item for item in elements
                        if normalize(item.get('name') or '') == normalize(target['name'])]
        if len(elements) != 1:
            raise NeedsAttention('UIA nền không định vị được đúng một control MISA hiện tại.')
        current = bridge.call('read', {'token': elements[0]['token']})
        if (current.get('windowId') != self.window.hwnd
                or current.get('kind') != elements[0].get('kind')
                or current.get('id') != elements[0].get('id')
                or current.get('name') != elements[0].get('name')
                or not current.get('enabled')):
            raise NeedsAttention('UIA nền thay đổi định danh control trước thao tác.')
        return current

    def background_observe(self):
        """Return semantic UIA state without restoring a minimized MISA window."""
        bridge = self._background_bridge()
        elements = []
        for kind in (50000, 50002, 50003, 50004, 50007, 50013, 50018, 50019,
                     50020, 50025, 50029, 50035):
            elements.extend(bridge.call('query', {'kinds': [kind]}))
        return {'window': {'hwnd': self.window.hwnd, 'pid': self.window.pid,
                           'title': self.window.title, 'minimized': bool(win32.user32.IsIconic(self.window.hwnd))},
                'elements': [item for item in elements
                             if item.get('windowId') == self.window.hwnd],
                'source': 'UIA3.background'}

    def deep_native_observe(self, *, maximum_nodes=5000, query='', maximum_results=120):
        """Collect bounded UIA3 and Win32 trees for live diagnostics only."""
        if self.window is None:
            raise NeedsAttention('Chưa gắn được cửa sổ MISA để inspect.')
        if type(maximum_nodes) is not int or not 1 <= maximum_nodes <= 8000:
            raise NeedsAttention('Giới hạn cây native phải nằm trong khoảng 1..8000.')
        if type(maximum_results) is not int or not 1 <= maximum_results <= 1000:
            raise NeedsAttention('Giới hạn kết quả native phải nằm trong khoảng 1..1000.')
        if not isinstance(query, str) or len(query) > 200:
            raise NeedsAttention('Từ khóa inspect native không hợp lệ.')
        needle = query.casefold().strip()

        def select_nodes(nodes, text_of, identity_of, parent_of):
            by_id = {identity_of(node): node for node in nodes}
            selected = set()
            for node in nodes:
                if needle and needle not in text_of(node).casefold():
                    continue
                current = node
                for _ in range(40):
                    identity = identity_of(current)
                    if not identity or identity in selected:
                        break
                    selected.add(identity)
                    current = by_id.get(parent_of(current))
                    if current is None:
                        break
                if len(selected) >= maximum_results:
                    break
            output = [node for node in nodes if identity_of(node) in selected]
            return output[:maximum_results], len(output) > maximum_results

        result = {'window': {'hwnd': self.window.hwnd, 'pid': self.window.pid,
                             'title': self.window.title,
                             'minimized': bool(win32.user32.IsIconic(self.window.hwnd))},
                  'observed_at': utc_now().isoformat(), 'query': query,
                  'maximum_nodes': maximum_nodes, 'maximum_results': maximum_results}
        try:
            if self._uia_readback is None:
                from .uia_readback import UiaReadback
                self._uia_readback = UiaReadback(self.cancel)
            nodes = self._uia_readback.dump_tree(self, maximum_nodes=maximum_nodes)
            chosen, clipped = select_nodes(
                nodes,
                lambda node: ' '.join(str((node.get('element') or {}).get(key) or '')
                                      for key in ('id', 'name', 'className', 'value')),
                lambda node: (node.get('element') or {}).get('token'),
                lambda node: (node.get('element') or {}).get('parentToken'))
            result['uia'] = {'status': 'observed', 'source': 'UIA3',
                             'node_count': len(nodes), 'returned_count': len(chosen),
                             'truncated': clipped or (not needle and len(nodes) > len(chosen)),
                             'nodes': chosen}
        except Exception as error:
            result['uia'] = {'status': 'unavailable', 'source': 'UIA3',
                             'error': f'{type(error).__name__}: {error}'}
        try:
            tree = win32.child_window_tree(self.window, maximum_nodes=maximum_nodes)
            nodes = tree['nodes']
            chosen, clipped = select_nodes(
                nodes,
                lambda node: ' '.join(str(node.get(key) or '')
                                      for key in ('hwnd', 'control_id', 'class_name', 'text')),
                lambda node: node.get('hwnd'),
                lambda node: node.get('parent_hwnd'))
            tree.update(nodes=chosen, returned_count=len(chosen),
                        truncated=tree['truncated'] or clipped
                        or (not needle and len(nodes) > len(chosen)))
            result['win32'] = tree
        except Exception as error:
            result['win32'] = {'status': 'unavailable', 'source': 'Win32.EnumChildWindows',
                               'error': f'{type(error).__name__}: {error}'}
        return result

    def background_action(self, action, target, value=None):
        """Execute a semantic UIA pattern without activating the MISA window."""
        if not self.background_control or not self.window:
            raise NeedsAttention('Chế độ UIA nền chưa được bật.')
        if action not in ('click', 'set', 'select', 'toggle', 'expand', 'collapse', 'scroll'):
            raise NeedsAttention('Phím và tọa độ không được gửi khi MISA đang thu nhỏ.')
        if action == 'set' and target.get('type') not in ('field', 'find_filter', 'native_control'):
            raise NeedsAttention('Nhập nền cần một trường UIA có định danh rõ ràng.')
        bridge = self._background_bridge()
        element = self._background_element(target)
        patterns = set(element.get('patterns') or ())
        required = {
            'click': {'Invoke', 'SelectionItem', 'Toggle', 'ExpandCollapse'},
            'select': {'SelectionItem'}, 'toggle': {'Toggle'},
            'expand': {'ExpandCollapse'}, 'collapse': {'ExpandCollapse'},
            'scroll': {'Scroll', 'ScrollItem'}, 'set': {'Value'},
        }[action]
        if required and not (patterns & required):
            raise NeedsAttention('Control MISA không có UIA pattern phù hợp cho thao tác nền.')
        before_identity = (self.window.hwnd, self.window.pid)
        result = bridge.execute(action, token=element['token'], value=value,
                                checkpoint=self.checkpoint, allow_background=True)
        try:
            current = bridge.call('read', {'token': element['token']})
        except Exception as error:
            return {'strategy': result.get('strategyUsed'), 'source': 'UIA3.background',
                    'status': 'sent_unverified', 'readback_error': str(error)}
        stable = (before_identity == (self.window.hwnd, self.window.pid)
                  and current.get('windowId') == self.window.hwnd
                  and current.get('kind') == element.get('kind')
                  and current.get('id') == element.get('id'))
        verified = stable and (str(current.get('value')) == str(value) if action == 'set'
                               else bool(current.get('selected')) if action == 'select'
                               else current.get('toggle') != element.get('toggle') if action == 'toggle'
                               else False)
        return {'strategy': result.get('strategyUsed'), 'element': current,
                'source': 'UIA3.background', 'status': 'verified' if verified else 'sent_unverified'}

    def is_window(self, kind):
        return self.matches_window(self.window, kind)

    @staticmethod
    def matches_window(window, kind):
        if kind == 'sale_tabs':
            kind = 'sale'
        if kind == 'main_navigation':
            kind = 'main'
        if kind == 'main' and window and win32.user32.GetWindow(window.hwnd, 4):
            return False
        title = normalize(window.title if window else '')
        tokens = {'sale': ('banhanghoadichvutrongnuoc', 'chungtubanhang'),
                  'find': ('timnhanhvattu', 'timnhanh'), 'new_item': ('themvattu',),
                  'main': ('misasme',)}
        return any(title.startswith(token) for token in tokens.get(kind, ()))

    def _regions(self, kind, width, height, *, expanded=False):
        if kind == 'main':
            dpi = int(win32.user32.GetDpiForWindow(self.window.hwnd)) if hasattr(win32.user32, 'GetDpiForWindow') else 96
            scale = (dpi or 96) / 96
            top = min(height - 1, round(40 * scale))
            bottom = min(height, round((360 if expanded else 230) * scale))
            return [(0, top, width, max(1, bottom - top))]
        if self.checkpoint.startswith('restart.') or kind == 'main_navigation':
            return [(0, 0, width, height)]
        if self.checkpoint in self.ADD_HEADER_STEPS and kind in ('sale', 'sale_tabs'):
            bottom = min(height, 560 if expanded else 410)
            top = min(70, bottom - 1)
            return [(0, top, width, bottom - top)]
        if kind == 'sale_tabs':
            # Header tabs and the Hàng tiền/Thuế strip are enough before the
            # first product row is touched; scan the grid only when needed.
            header_bottom = min(height, max(360, min(410, height - 98)))
            tabs_top = max(70, header_bottom - 40)
            tabs_bottom = min(height, max(tabs_top + 1, 560))
            return [(0, 70, width, max(1, header_bottom - 70)),
                    (0, tabs_top, width, max(1, tabs_bottom - tabs_top))]
        if kind == 'sale':
            # MISA's Phiếu thu / Hóa đơn and Hàng tiền / Thuế tabs sit in the
            # header-to-grid band.  Keep the tiles overlapping; the old
            # 275..350 pixel gap made an on-screen tab look like 0 results.
            header_bottom = min(height, max(360, min(410, height - 98)))
            # Leave context above the column captions for their accents;
            # A tile beginning at the glyphs can create a false geometry conflict.
            grid_top = max(70, header_bottom - 40)
            footer_top = max(grid_top, height - 98)
            return [(0, 70, width, max(1, header_bottom - 70)),
                    (0, grid_top, width, max(1, footer_top - grid_top)),
                    (0, footer_top, width, max(1, height - footer_top)),
                    (400, footer_top, max(1, width - 400), max(1, height - footer_top))]
        return [(0, 40, width, max(1, height - 40))]

    def _observe(self, *, kind=None, force=False, poll=False, field_ids=None, tab=None):
        self._check()
        if self._locating and self._frame is not None:
            self._guard_frame()
            return self._frame[3]
        if self._mapping and self._frame is not None and not force:
            self._guard_frame()
            return self._frame[3]
        # A frame for one layout may only contain crops for that layout.  Do
        # not reuse it for another form or tab request.
        requested_kind = kind if kind is not None else self._frame_kind
        if field_ids is None:
            field_ids = self._frame_fields if kind is None else ()
        fields = tuple(field_ids)
        if tab is None:
            tab = self._frame_tab if kind is None else ''
        if (self._frame is not None and self._frame_kind == requested_kind and not force and not poll
                and self._frame_fields == fields and self._frame_tab == tab
                and self._frame_context.get('checkpoint') == self.checkpoint
                and self._frame_context.get('input_epoch') == self.input_epoch
                and time.monotonic() - self._frame_at < .15):
            self._guard_frame()
            return self._frame[3]
        self._invalidate()
        self.sync(kind=kind)
        return self._scan_frame(requested_kind, field_ids=fields, tab=tab)

    def _capture_picture(self, purpose):
        frame_id = uuid.uuid4().hex
        captured_at = utc_now()
        context = dict(frame_id=frame_id, captured_at=captured_at.isoformat(),
                       hwnd=self.window.hwnd, pid=self.window.pid, window_title=self.window.title,
                       checkpoint=self.checkpoint, input_epoch=self.input_epoch, purpose=purpose,
                       run_token=self.run_token,
                       dpi=int(win32.user32.GetDpiForWindow(self.window.hwnd)) if hasattr(win32.user32, 'GetDpiForWindow') else None)
        # The direct MCP bridge may be called while Codex or another app owns
        # the foreground. Bring the selected MISA surface forward at the last
        # possible moment so the native capture has a readable surface.
        if getattr(self, 'direct_control', False) and not win32.is_foreground(self.window):
            win32.activate(self.window)
        with span('capture', 'Chụp cửa sổ MISA hiện tại'):
            started = time.perf_counter()
            picture, capture_box = win32.capture(self.window, evidence_context=context)
            self._record_time('capture', started)
        context.update(capture_box=list(capture_box),
                       original_image_id=picture.info.get('misa_original_image_id'),
                       masked_regions=picture.info.get('misa_masked_regions', []))
        context['window_size'] = list(picture.size)
        context['frame_image_id'] = record_image(picture, role='window_capture', **context)
        return picture, capture_box, context, captured_at

    def _scan_frame(self, requested_kind, *, field_ids=(), tab=''):
        return self._scan_native_frame(requested_kind, field_ids=field_ids, tab=tab)
    def _record_time(self, stage, started):
        elapsed = (time.perf_counter() - started) * 1000
        self._metrics[f'{stage}_last_ms'] = round(elapsed, 2)
        key = f'{stage}_total_ms'
        self._metrics[key] = round(self._metrics[key] + elapsed, 2)

    def _map_observation(self, kind):
        self.sync()
        if kind is None:
            kind = next((name for name in ('main', 'sale', 'find', 'new_item')
                         if self.is_window(name)), None)
        self._words(kind=kind)
        previous = self._mapping
        self._mapping = True
        started = time.perf_counter()
        try:
            yield
        finally:
            self._mapping = previous
            if not previous:
                self._record_time('mapping', started)

    @contextmanager
    def read_scope(self, kind='sale', *, field_ids=(), tab=''):
        """Pin one fresh frame while resolving related controls and their values."""
        previous = self._mapping
        try:
            # A MISA combo can leave an untitled owned popup foreground after
            # selection (especially across hot reload). Read-only verification
            # belongs to the bound run-owned form; restore it before capture.
            # Dynamic catalog-option resolution uses _native_frame directly and
            # therefore keeps the popup foreground while it is being located.
            if (not getattr(self, 'direct_control', False)
                    and self.window is not None
                    and not win32.is_foreground(self.window)):
                foreground = win32.foreground_for_process(self.pid)
                if win32.is_owned_by(foreground, self.window.hwnd):
                    win32.activate(self.window)
                    self._invalidate()
            self._observe(kind=kind, force=True, field_ids=field_ids, tab=tab)
            self._mapping = True
            yield
            self._guard_frame()
        except Exception as error:
            self._record_read_failure({'type': 'read_scope', 'kind': kind}, error)
            raise
        finally:
            self._mapping = previous

    def _guard_frame(self):
        self._check()
        if getattr(self, 'direct_control', False):
            return
        try:
            current = win32.bound_window(self.window)
            # A user may briefly switch applications while recovery is polling
            # a dialog. Re-activate the bound native window instead of treating
            # that foreground change as a new target.
            if not win32.is_foreground(current):
                win32.activate(current)
            win32.ensure_foreground(current)
        except win32.WindowError as error:
            raise NeedsAttention(str(error)) from error
        rectangle = win32.W.RECT()
        dpi = int(win32.user32.GetDpiForWindow(current.hwnd)) if hasattr(win32.user32, 'GetDpiForWindow') else 0
        if (self._frame is None or self._frame_window != (current.hwnd, current.pid)
                or self._frame_context.get('dpi', 0) != dpi
                or self._frame_context.get('window_title') != current.title
                or self._frame_context.get('input_epoch') != self.input_epoch
                or self._frame_context.get('checkpoint') != self.checkpoint
                or self._captured_at < self.last_input_at
                or (utc_now() - self._captured_at).total_seconds() > 30
                or not win32.user32.GetWindowRect(current.hwnd, win32.C.byref(rectangle))):
            raise NeedsAttention('Khung quan sát native không còn đúng cửa sổ/vị trí/thời điểm; cần đọc lại.')
        current_box = (rectangle.left, rectangle.top, rectangle.right, rectangle.bottom)
        if current_box != self._frame[0]:
            # A user may move or resize MISA between two semantic operations.
            # Rebuild the native frame against the same HWND/PID so all UIA
            # bounds are translated from the new origin before input continues.
            if self._reframing or self._frame_kind is None:
                raise NeedsAttention('MISA đổi vị trí liên tục; cần đọc lại frame hiện tại.')
            kind, fields, tab = self._frame_kind, self._frame_fields, self._frame_tab
            self._reframing = True
            try:
                self._invalidate()
                self._scan_frame(kind, field_ids=fields, tab=tab)
                self._metrics['frame_repositions'] = self._metrics.get('frame_repositions', 0) + 1
                return
            except (Cancelled, NeedsAttention, win32.WindowError) as error:
                raise NeedsAttention('MISA đã đổi vị trí; không dựng lại được frame native: ' + str(error)) from error
            finally:
                self._reframing = False
        if self._frame_context.get('client_bounds') is not None:
            try:
                client = win32.client_bounds(current, self._frame[0])
            except win32.WindowError as error:
                raise NeedsAttention(str(error)) from error
            if list(client) != self._frame_context['client_bounds']:
                raise NeedsAttention('MISA client rectangle changed; recapture before input.')

    @tracked('readback', lambda self, bounds, purpose, **kwargs: 'Đọc native: ' + purpose)
    def _detail_words(self, bounds, purpose, *, single_line=False, contrast_profile='native',
                      left_limit=None, right_limit=None):
        """Read a bounded native text region from the current UIA tree."""
        self._guard_frame()
        x, y, width, height = bounds
        words = [word for word in self._native_words()
                 if x <= word.cx <= x + width and y <= word.cy <= y + height]
        if left_limit is not None:
            words = [word for word in words if word.box[2] >= left_limit]
        if right_limit is not None:
            words = [word for word in words if word.box[0] <= right_limit]
        if single_line and words:
            row = sum(word.cy for word in words) / len(words)
            words = [word for word in words if abs(word.cy - row) <= max(8, height * .6)]
        return tuple(words)
    def _record_read_failure(self, target, error):
        if self._locating:
            self.last_action['field_observations'] = deepcopy(self._field_observations)
            return
        self.last_action = dict(at=utc_now().isoformat(), step=self.checkpoint, target=target,
                                status='cancelled' if isinstance(error, Cancelled) else 'blocked',
                                input_sent=False, error=str(error),
                                field_observations=deepcopy(self._field_observations))
        if self._frame is not None:
            self.last_action.update(window_hwnd=self._frame_window[0], window_pid=self._frame_window[1],
                frame_id=self._frame_id, capture_box=list(self._frame[0]),
                observed_at=self._captured_at.isoformat(),
                words=[dict(text=w.text, box=list(w.box)) for w in self._frame[3]])
        self._remember_action()

    @tracked('readback', lambda self, control, **kwargs: 'Đọc bằng chứng native: ' + (control.name or control.id))
    def read_evidence(self, control, *, document, row, field):
        """Return only current UIA3/Win32 evidence for a native target."""
        self._check()
        context = dict(frame_id=self._frame_id, hwnd=self.window.hwnd, pid=self.window.pid,
                       document=document, row=row, field=field, checkpoint=self.checkpoint,
                       run_token=self.run_token, input_epoch=self.input_epoch,
                       bounds=tuple(control.bounds))
        stale = (control.observed_frame_id != self._frame_id
                 or control.window_id != self.window.hwnd
                 or self._frame_window != (self.window.hwnd, self.window.pid))
        if stale:
            return [Reading(**context, source='UIA3', actual_raw=None,
                            observed_at=self._captured_at, usable=False,
                            reason='Stale native control; read a fresh UIA frame.')]
        coordinate_fallback = bool((control.target or {}).get('coordinate_fallback'))
        readings = []
        if not coordinate_fallback:
            if self._uia_readback is None:
                from .uia_readback import UiaReadback
                self._uia_readback = UiaReadback(self.cancel)
            readings.append(self._uia_readback.read(self, control, context))
        if not readings or not readings[0].usable:
            try:
                readings.append(self._native_field_reading(control, context))
            except (Cancelled, NeedsAttention, win32.WindowError) as error:
                if isinstance(error, Cancelled):
                    raise
                readings.append(Reading(**context, source='Win32', actual_raw=None,
                                        observed_at=utc_now(), usable=False,
                                        reason='unavailable:' + str(error)))
        current = win32.bound_window(self.window)
        win32.ensure_foreground(current)
        rectangle = win32.W.RECT()
        unchanged = ((current.hwnd, current.pid) == (self.window.hwnd, self.window.pid)
                     and bool(win32.user32.GetWindowRect(current.hwnd, win32.C.byref(rectangle)))
                     and (rectangle.left, rectangle.top, rectangle.right, rectangle.bottom) == self._frame[0])
        if not unchanged:
            readings = [reading.model_copy(update={
                'usable': False, 'reason': 'Window changed during native readback.'})
                        for reading in readings]
        self._check()
        return readings
    @tracked('readback', lambda self, control, context: 'Đọc chữ hiển thị: ' + (control.name or control.id))
    def _native_field_reading(self, control, context):
        self._guard_frame()
        observed = win32.read_control_text(self.window, control.bounds, self._frame[0])
        self._guard_frame()
        value = observed['value']
        if value and re.fullmatch(r'\{?[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\}?', value.strip(), re.I):
            observed.update(status='unavailable', reason='Internal GUID is not displayed text.')
        return Reading(**context, source='Win32', source_detail=observed['valueSource'],
                       actual_raw=value, observed_at=datetime.fromisoformat(observed['observedAt']),
                       usable=observed['status'] == 'supported',
                       reason=('unavailable:' if observed['status'] == 'unavailable' else '') + observed['reason'])

    def _input_sent(self, *, surface_unchanged=True):
        # Any input invalidates the captured frame. Every next target is reacquired natively.
        self.input_epoch += 1
        self.last_input_at = utc_now()
        self._layout_proof = None

    @tracked('readback', lambda self, control, expected_box: 'Đọc lại native: ' + (control.name or control.id))
    def _read_input_value(self, control, expected_box):
        """Read the edited value through a fresh native UIA/Win32 frame."""
        self._check()
        self.sync(kind='sale' if self.is_window('sale') else None)
        self.guard(control)
        self._native_frame('sale' if self.is_window('sale') else next(
            (kind for kind in ('find', 'new_item', 'main') if self.is_window(kind)),
            'sale'), fresh=True, )
        # The window may have moved while MISA processed the input. The fresh
        # UIA field above is authoritative; only HWND/PID continuity matters.
        if self._frame_window != (self.window.hwnd, self.window.pid):
            raise NeedsAttention('Cửa sổ MISA đổi tiến trình khi đọc lại ô vừa nhập.')
        fresh = control
        if control.role == 'field' and control.id:
            fresh = self._uia_field_target((control.id,), control.name) or control
        readings = self.read_evidence(fresh, document=self.run_token,
                                      row=fresh.target.get('row') if fresh.target else None,
                                      field=fresh.id or fresh.name)
        usable = next((reading for reading in readings if reading.usable), None)
        actual = usable.actual_raw if usable is not None else None
        self.last_action.setdefault('readbacks', []).append({
            'at': datetime.now(timezone.utc).isoformat(), 'scope': 'native',
            'bounds': list(fresh.bounds), 'value': actual,
            'frame_id': self._frame_id, 'observed_at': self._captured_at.isoformat(),
            'hwnd': self.window.hwnd, 'pid': self.window.pid,
            'checkpoint': self.checkpoint, 'input_epoch': self.input_epoch,
            'sources': [reading.source for reading in readings],
        })
        return actual
    def _invalidate(self):
        self._frame = None
        self._frame_kind = None
        self._frame_fields = ()
        self._frame_tab = ''
        self._frame_at = 0.0
        self._frame_id = ''
        self._frame_window = None
        self._frame_context = {}
        self._frame_uia_elements = None
        self._detail_reads = {}
        self._grid_text_reads = {}
        self._uia_find_row_reads = {}
        self._field_observations = []
        self._inventory_menu_open = False
        self._detail_reads = {}

    def _words(self, kind=None, *, force=False):
        return self._observe(kind=kind, force=force)

    @staticmethod
    def _aliases(text):
        key = normalize(text)
        return {key, *ScreenDesktop.LABEL_ALIASES.get(text, ())}

    def _matches(self, text, word):
        return label_hint_matches(self._aliases(text), word)

    def _label_pool(self, text, pool):
        if not self._frame:
            return []
        key = normalize(text)
        height = self._frame[1][1]
        if self.is_window('sale'):
            if text in self.TOTALS.values():
                return [w for w in pool if w.cy >= height - 110]
            if text in self.TAB_LABELS:
                return [w for w in pool if 70 <= w.cy <= min(height - 80, 560)]
            if text in {spec[0] for spec in self.FIELD_LAYOUT.values()}:
                bottom = 560 if (self.checkpoint in self.ADD_HEADER_STEPS
                                 and (self._frame_context.get('scan_expanded')
                                      or self._frame_context.get('scan_scope') == 'fields')) else 410
                return [w for w in pool if 70 <= w.cy < min(height - 98, bottom)]
            return [w for w in pool if w.cy >= 40]
        if self.is_window('find') or self.is_window('new_item'):
            if key in ('dongy', 'huybo', 'cat', 'dong'):
                return [w for w in pool if w.cy >= height - 100 or 40 <= w.cy < 80]
            return [w for w in pool if 40 <= w.cy < height - 65]
        scale = (self._frame_context.get('dpi') or 96) / 96
        bottom = 360 if self._frame_context.get('scan_expanded') else 230
        return [w for w in pool if 40 * scale <= w.cy <= bottom * scale]

    def _find_words(self, text, words=None, *, scoped=False):
        pool = list(self._words() if words is None else words)
        if not scoped:
            pool = self._label_pool(text, pool)
        wanted = {label_key(value) for value in (text, *self.LABEL_VARIANTS.get(text, ()))}
        exact = [word for word in pool if label_key(word.text) in wanted]
        if exact:
            return exact if all(label_is_confirmed(word) for word in exact) else []
        return []

    def unique(self, controls, label):
        """Require one current native control; ambiguity is never resolved by position."""
        controls = [control for control in controls if control is not None]
        if len(controls) != 1:
            raise NeedsAttention(f'Khong dinh vi duy nhat native control: {label}.')
        return controls[0]

    def guard(self, control=None):
        """Validate the bound window; action paths additionally require a fresh frame."""
        self._check()
        if getattr(self, 'direct_control', False):
            return
        self.sync()
        try:
            if not getattr(self, 'background_control', False) and not win32.is_foreground(self.window):
                # Input is bound to this exact HWND/PID. Restore it at the
                # input boundary instead of failing when the user briefly
                # switches applications.
                win32.activate(self.window)
            win32.ensure_foreground(self.window)
            if not win32.user32.IsWindowEnabled(self.window.hwnd):
                raise win32.WindowError('Cua so MISA hien tai dang bi khoa.')
        except win32.WindowError as error:
            raise NeedsAttention(str(error)) from error
        if control is not None and control.window_id != self.window.hwnd:
            raise NeedsAttention('Native control thuoc cua so MISA khac.')
        if self._frame is not None:
            self._guard_frame()
            if control is not None and control.observed_frame_id != self._frame_id:
                raise NeedsAttention('Control thuộc frame cũ sau khi MISA di chuyển/đổi tab; cần định vị lại.')
            unresolved = {'recovery', 'arrow', 'option'}
            target_type = (control.target or {}).get('type') if control is not None else None
            coordinate_fallback = bool((control.target or {}).get('coordinate_fallback')) if control else False
            if control is not None and target_type not in unresolved and not coordinate_fallback:
                element = (control.target or {}).get('uia_element')
                if (not isinstance(element, dict) or not element.get('token')
                        or element.get('windowId') != self.window.hwnd
                        or element.get('offscreen') or not element.get('enabled')):
                    raise NeedsAttention('Native control thieu UIA identity hien tai.')

    def _target_for(self, control):
        self.guard(control)
        if not (control.target or {}).get('type'):
            raise NeedsAttention('Native control thieu semantic target.')
        return deepcopy(control.target)

    def text_in(self, bounds):
        """Read a bounded value from the current UIA tree only."""
        self._guard_frame()
        left, top = self._frame[0][:2]
        x, y, width, height = bounds
        words = [word for word in self._native_words()
                 if x <= word.cx <= x + width and y <= word.cy <= y + height]
        return ' '.join(word.text for word in words).strip()

    def _label(self, text, words=None, *, scoped=False):
        candidates = self._find_words(text, words, scoped=scoped)
        if len(candidates) != 1:
            return None
        return candidates[0]

    def _observe_label(self, text, words=None, *, scoped=False, purpose='label'):
        word = self._label(text, words, scoped=scoped)
        return word, {'status': 'supported' if word is not None else 'unavailable',
                      'source': 'UIA3', 'purpose': purpose}

    def _control_for_word(self, word, *, kind, name=None):
        """Re-resolve a caption through UIA; a text bounding box is not an action target."""
        if word is None:
            raise NeedsAttention('Khong co native caption hien tai.')
        caption = name or word.text
        window_kind = next((surface for surface in ('main', 'sale', 'find', 'new_item')
                            if self.is_window(surface)), None)
        target_type = 'tab' if kind == 50019 else 'fixed_button'
        control = self._uia_static_target({'type': target_type, 'text': caption,
                                           'name': caption, 'window_kind': window_kind})
        if control is None:
            # MISA can expose a tab caption as native text while omitting the
            # TabItem/SelectionItem provider. Bind a Win32 click to the fresh
            # caption bounds; _click_resolved keeps the frame/window guard.
            if target_type == 'tab' and self._frame is not None:
                left, top, _, _ = self._frame[0]
                x = round(word.cx)
                y = round(word.cy)
                return ScreenControl(
                    self, id='', name=caption, kind=kind,
                    bounds=word.box, enabled=True, role='tab',
                    target={'type': 'tab', 'text': caption,
                            'coordinate_fallback': True,
                            'coordinate': [x, y],
                            'coordinate_space': 'window',
                            'frame_id': self._frame_id})
            raise NeedsAttention('UIA khong cung cap action pattern cho ' + caption + '.')
        return control

    def field(self, ids, label=''):
        control = self._uia_field_target(tuple(ids), label)
        if control is None:
            raise NeedsAttention('UIA/Win32 khong cung cap truong native: ' + (label or str(ids)))
        return control

    def controls(self, row):
        result = list(getattr(row, '_cells', {}).values())
        checkbox = getattr(row, '_uia_checkbox', None)
        if checkbox is not None:
            result.append(checkbox)
        return result

    def _find_grid_controls(self):
        rows = self._uia_find_rows() or ()
        result = []
        for row in rows:
            result.extend(self.controls(row))
        return result

    def find_controls(self, *, kinds=(), ids=()):
        """Return current UIA controls matching semantic kind/id filters."""
        self._guard_frame()
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        elements = self._uia_readback.query(self, kinds=tuple(kinds), ids=tuple(ids))
        left, top = self._frame[0][:2]
        controls = []
        for element in elements:
            box = element.get('bounds') or {}
            values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
            if (element.get('windowId') != self.window.hwnd
                    or element.get('offscreen') or not element.get('enabled')
                    or not element.get('token')
                    or not all(isinstance(value, (int, float)) and math.isfinite(value)
                               for value in values)
                    or values[2] <= 0 or values[3] <= 0):
                continue
            control = ScreenControl(
                self, id=str(element.get('id') or ''), name=str(element.get('name') or ''),
                kind=int(element.get('kind') or 0),
                bounds=(values[0] - left, values[1] - top, values[2], values[3]),
                enabled=True, selected=bool(element.get('selected')),
                toggle=int(element.get('toggle') or 0), role='native')
            control.target = {'type': 'native_control', 'uia': True,
                              'kind': control.kind, 'id': control.id,
                              'name': control.name,
                              'parent_token': element.get('parentToken') or '',
                              'bounds': tuple(control.bounds),
                              'uia_element': element,
                              'uia_automation_id': element.get('id') or ''}
            controls.append(control)
        return controls

    def _grid(self, sale=True):
        if not sale or not self.is_window('sale'):
            raise NeedsAttention('Khong co bang native UIA hien tai.')
        self._native_frame('sale')
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        element = self._uia_readback.locate(self, name='', kind=50025,
                                            window_kind='sale', automation_id='grdDetail')
        box = element['bounds']
        left, top = self._frame[0][:2]
        control = ScreenControl(self, id=element.get('id') or 'grdDetail',
                                name='Bang hang hoa', kind=50025,
                                bounds=(box['x'] - left, box['y'] - top,
                                        box['width'], box['height']), role='grid')
        control.target = {'type': 'grid', 'sale': True, 'uia': True,
                          'uia_element': element}
        return control

    def _rows(self, sale=True):
        if sale:
            raise NeedsAttention('Bang ban hang phai duoc doc qua UIA GridPattern.')
        rows = self._uia_find_rows()
        if rows is None:
            raise NeedsAttention('UIA khong cung cap cac dong native hien tai.')
        return rows

    def _synthetic_named(self, text, kind):
        controls = [control for control in self.find_controls(kinds=(kind,))
                    if label_key(control.name) == label_key(text)]
        return controls

    def _native_frame(self, kind, *, fresh=False):
        """Pin a fresh native frame for UIA/Win32 resolution."""
        self._check()
        self.sync(kind='sale' if kind == 'sale_tabs' else kind)
        current_kind = self._frame_kind
        same_surface = (current_kind == kind or
                        {current_kind, kind} <= {'sale', 'sale_tabs'})
        # A checkpoint or input transition invalidates the prior frame even
        # when the same MISA surface remains open; recapture before mapping.
        if (not fresh and self._frame is not None and same_surface
                and self._frame_context.get('client_bounds')
                and self._frame_context.get('checkpoint') == self.checkpoint
                and self._frame_context.get('input_epoch') == self.input_epoch):
            self._guard_frame()
            return self._frame_context['client_bounds']
        self._invalidate()
        try:
            picture, capture_box, context, captured_at = self._capture_picture('native_locate')
        except win32.WindowError as error:
            raise NeedsAttention(str(error)) from error
        try:
            client = win32.client_bounds(self.window, capture_box)
        except win32.WindowError as error:
            raise NeedsAttention(str(error)) from error
        context.update(client_bounds=list(client),
                       scan_scope='native', scan_fields=[], scan_tab=[], scan_regions=[])
        self._frame = (capture_box, picture.size, context['frame_id'], (), picture)
        self._frame_window = (self.window.hwnd, self.window.pid)
        self._frame_context = context
        self._frame_id = context['frame_id']
        self._captured_at = captured_at
        self._frame_kind = kind
        self._frame_fields = ()
        self._frame_tab = ''
        self._frame_at = time.monotonic()
        self._metrics['native_frames'] += 1
        # Native frames do not scan learned ROIs, but their identity must still
        # be tied to the current memory generation for frame guarding.
        self._guard_frame()
        return client

    def _uia_sale_item_cell(self, row, column, *, code='', new=False,
                            semantic_only=False, allow_reveal=True,
                            revealed_target=False, expected_total=None):
        """Resolve an editor through its current DataItem parent before map fallback."""
        if not self.is_window('sale'):
            return None
        # These IDs are observed on the current grdDetail editors.  Read-only
        # columns use the same semantic path; write policy remains enforced by
        # item_edit(), which only permits quantity and amount.
        automation_id = {'mahang': '58', 'tenhang': '3',
                         'tktienchiphi': '4', 'tkdoanhthu': '5', 'dvt': '6',
                         'soluong': '8', 'dongia': '9', 'thanhtien': '11'}.get(column)
        semantic_names = tuple(self.COLUMN_LABELS.get(column, ()))
        if automation_id is None and not semantic_names:
            return None
        from .uia_bridge import EngineError
        try:
            self._native_frame('sale', )
            if self._uia_readback is None:
                from .uia_readback import UiaReadback
                self._uia_readback = UiaReadback(self.cancel)
            tax_column = column in ('thuesuatgtgt', 'tienthuegtgt')
            # Only promote tab captions observed in the current sale tree.
            # Historical aliases remain locate hints, never UIA targets.
            tab_names = ('2. Thuế',) if tax_column else ('1. Hàng tiền',)
            tab = None
            for candidate in tab_names:
                try:
                    tab = self._uia_readback.locate(
                        self, name=candidate, kind=50019, window_kind='sale')
                    break
                except EngineError:
                    continue
            if tab is None:
                return None
            tab_name = str(tab.get('name') or tab_names[0])
            if ('SelectionItem' not in set(tab.get('patterns') or ())
                    or not tab.get('selected')):
                return None
            expected_parent_id = '-1' if new else str(row - 1)

            def valid_box(element):
                box = element.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                if (element.get('windowId') != self.window.hwnd
                        or element.get('offscreen') or not element.get('enabled')
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in values)
                        or values[2] <= 0 or values[3] <= 0):
                    return False
                frame_left, frame_top, frame_right, frame_bottom = self._frame[0]
                x, y, width, height = values
                return (x >= frame_left and y >= frame_top
                        and x + width <= frame_right and y + height <= frame_bottom)

            def valid_row_box(element):
                box = element.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                if (element.get('windowId') != self.window.hwnd
                        or element.get('offscreen') or not element.get('enabled')
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in values)
                        or values[2] <= 0 or values[3] <= 0):
                    return False
                frame_left, frame_top, frame_right, frame_bottom = self._frame[0]
                x, y, width, height = values
                return (x < frame_right and x + width > frame_left
                        and y >= frame_top and y + height <= frame_bottom)

            grid = self._uia_readback.locate(
                self, name='', kind=50025, window_kind='sale',
                automation_id='grdDetail')
            grid_box = grid.get('bounds') or {}

            # Prefer GridPattern for virtualized/custom grids. Descendant
            # DataItems are still required for editable targets, but a live
            # grid page can provide the same row/cell identity without geometry guesses.
            grid_target = ScreenControl(
                self, id=grid.get('id') or 'grdDetail', name='Bảng hàng hóa',
                kind=50025, bounds=(
                    grid_box.get('x', 0) - self._frame[0][0],
                    grid_box.get('y', 0) - self._frame[0][1],
                    grid_box.get('width', 0), grid_box.get('height', 0)),
                role='grid')
            grid_target.target = {'type': 'grid', 'sale': True,
                                  'uia': True, 'uia_element': grid}
            try:
                grid_cell = self._uia_sale_item_cell_from_grid(
                    grid_target, row, column, code=code, new=new,
                    expected_total=expected_total)
            except (EngineError, NeedsAttention, win32.WindowError):
                grid_cell = None
            if grid_cell is not None:
                expected_count = (int(expected_total) if expected_total is not None
                                  else row - 1 if new else row)
                self._item_row_proof = {
                    'count': expected_count, 'row': row,
                    'frame_id': self._frame_id, 'input_epoch': self.input_epoch,
                    'checkpoint': self.checkpoint,
                    'window': (self.window.hwnd, self.window.pid),
                    'tab': tab_name, 'prompt': new,
                    'source': 'UIA3.GridPattern',
                }
                return grid_cell

            def in_grid(element):
                box = element.get('bounds') or {}
                return (box.get('x', 0) >= grid_box.get('x', 0)
                        and box.get('y', 0) >= grid_box.get('y', 0)
                        and box.get('x', 0) + box.get('width', 0)
                        <= grid_box.get('x', 0) + grid_box.get('width', 0)
                        and box.get('y', 0) + box.get('height', 0)
                        <= grid_box.get('y', 0) + grid_box.get('height', 0))

            def row_in_grid(element):
                box = element.get('bounds') or {}
                x, y, width, height = (box.get(key, 0)
                                       for key in ('x', 'y', 'width', 'height'))
                gx, gy, gw, gh = (grid_box.get(key, 0)
                                  for key in ('x', 'y', 'width', 'height'))
                return (x < gx + gw and x + width > gx
                        and y >= gy and y + height <= gy + gh)

            all_rows = []
            add_rows = 0
            reveal_row = None
            invalid_row = False
            for item in self._uia_readback.query(self, kinds=(50029,)):
                if not self._uia_readback.bridge.has_ancestor(
                        item, grid.get('token'), window_id=self.window.hwnd):
                    continue
                try:
                    item_id = int(item.get('id'))
                except (TypeError, ValueError):
                    continue
                if item_id < -1:
                    continue
                if item_id == -1:
                    # MISA may expose a disabled trailing -1 DataItem in
                    # addition to the enabled add-row prompt. Count only the
                    # current visible prompt as part of the grid proof.
                    if (item.get('windowId') == self.window.hwnd
                            and not item.get('offscreen') and item.get('enabled')
                            and valid_row_box(item) and row_in_grid(item)):
                        add_rows += 1
                        all_rows.append(item)
                    continue
                if (item_id == int(expected_parent_id)
                        and (item.get('offscreen') or not valid_row_box(item)
                             or not row_in_grid(item))
                        and ({'Scroll', 'ScrollItem'} & set(item.get('patterns') or ()))):
                    reveal_row = item
                    continue
                if not valid_row_box(item) or not row_in_grid(item):
                    invalid_row = True
                    continue
                all_rows.append(item)
            if reveal_row is not None and allow_reveal and not semantic_only:
                if self._uia_reveal_sale_row(reveal_row, row, new):
                    return self._uia_sale_item_cell(
                        row, column, code=code, new=new,
                        semantic_only=False, allow_reveal=False,
                        revealed_target=True, expected_total=expected_total)
                return None
            if invalid_row and not revealed_target and not new:
                return None
            rows = [item for item in all_rows if item.get('id') == expected_parent_id]
            if not rows and new:
                # The add-row prompt can be exposed as a transient DataItem
                # whose UIA ancestry is omitted during the first frame after a
                # previous row commits. Rebind one current -1 prompt by the
                # already proven grid geometry instead of losing the row.
                prompts = []
                for item in self._uia_readback.query(self, kinds=(50029,)):
                    try:
                        item_id = int(item.get('id'))
                    except (TypeError, ValueError):
                        continue
                    if item_id == -1 and valid_row_box(item) and row_in_grid(item):
                        prompts.append(item)
                if len(prompts) == 1:
                    expected_parent_id = '-1'
                    rows = prompts
                    add_rows = max(add_rows, 1)
            if add_rows == 0:
                # Existing-row reads also need the trailing prompt in their
                # completeness proof, even when that prompt lacks ancestry.
                prompts = []
                for item in self._uia_readback.query(self, kinds=(50029,)):
                    try:
                        item_id = int(item.get('id'))
                    except (TypeError, ValueError):
                        continue
                    if item_id != -1 or not valid_row_box(item) or not row_in_grid(item):
                        continue
                    prompts.append(item)
                if len(prompts) == 1:
                    add_rows = 1
            activated_new = False
            if not rows and new and row > 1:
                # After F3, MISA can materialize the current input row as the
                # next zero-based DataItem instead of the documented -1 row.
                # Accept it only when all preceding committed IDs are present.
                candidate = [item for item in all_rows
                             if str(item.get('id')) == str(row - 1)
                             and valid_row_box(item) and row_in_grid(item)]
                prior_ids = []
                for item in all_rows:
                    try:
                        prior_ids.append(int(item.get('id')))
                    except (TypeError, ValueError):
                        prior_ids = []
                        break
                if (len(candidate) == 1
                        and sorted(prior_ids) == list(range(row))):
                    expected_parent_id = str(row - 1)
                    rows = candidate
                    activated_new = True
            if not rows and new and row == 1:
                # F3/F9 activates the add-row prompt as logical row 0 before
                # the catalog window opens; retain that current editor target.
                activated = [item for item in all_rows if str(item.get('id')) == '0']
                if len(activated) == 1:
                    expected_parent_id = '0'
                    rows = activated
                    activated_new = True
            row_ids = []
            for item in all_rows:
                if (item.get('windowId') != self.window.hwnd
                        or item.get('offscreen') or not item.get('enabled')):
                    continue
                try:
                    item_id = int(item.get('id'))
                except (TypeError, ValueError):
                    continue
                if item_id >= 0:
                    row_ids.append(item_id)
            expected_count = (int(expected_total) if expected_total is not None
                              else row if activated_new else row - 1 if new else row)
            expected_add_rows = ((0, 1) if tax_column or activated_new else (1,))
            complete_sequence = (sorted(row_ids) == list(range(expected_count))
                                and len(row_ids) == len(set(row_ids))
                                and add_rows in expected_add_rows)
            if not complete_sequence and not revealed_target:
                return None
            if len(rows) != 1:
                return None
            parent = rows[0]
            parent_token = parent.get('token')
            if not parent_token:
                return None

            kinds = (50004, 50003)
            query_ids = (automation_id,) if automation_id is not None else ()
            editors = [item for item in self._uia_readback.query(
                self, kinds=kinds, ids=query_ids, parent_token=parent_token)
                       if item.get('parentToken') == parent_token and valid_box(item)]
            expected_names = {text_key(name) for name in semantic_names}
            editors = [item for item in editors
                       if text_key(item.get('name') or '') in expected_names]
            if len(editors) != 1 and new:
                # The first frame after an add-row prompt appears may omit the
                # transient row parent from editor descendants. Rebind the
                # unique current editor by automation id/name and row geometry.
                pbox = parent.get('bounds') or {}
                px, py, pw, ph = (pbox.get(key) for key in ('x', 'y', 'width', 'height'))
                candidates = []
                for item in self._uia_readback.query(self, kinds=kinds, ids=query_ids):
                    box = item.get('bounds') or {}
                    values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                    id_matches = (not query_ids
                                  or str(item.get('id') or '') in query_ids)
                    name_matches = (not expected_names
                                    or text_key(item.get('name') or '') in expected_names)
                    semantic_match = id_matches if query_ids else name_matches
                    if (item.get('windowId') != self.window.hwnd
                            or item.get('offscreen') or not item.get('enabled')
                            or not semantic_match
                            or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                       for value in values + (px, py, pw, ph))
                            or values[2] <= 0 or values[3] <= 0
                            or values[0] < px or values[1] < py
                            or values[0] + values[2] > px + pw
                            or values[1] + values[3] > py + ph):
                        continue
                    candidates.append(item)
                if len(candidates) == 1:
                    editors = candidates
            if len(editors) != 1:
                return None
            element = editors[0]
            if automation_id is None:
                patterns = set(element.get('patterns') or ())
                if (('Value' not in patterns and 'Text' not in patterns)
                        or element.get('value') is None):
                    return None
                current = self._uia_readback.bridge.call('read', {'token': element['token']})
                if (current.get('windowId') != self.window.hwnd
                        or current.get('id') != element.get('id')
                        or text_key(current.get('name') or '') not in expected_names
                        or current.get('parentToken') != parent_token
                        or current.get('bounds') != element.get('bounds')
                        or not current.get('enabled') or current.get('offscreen')
                        or current.get('value') is None
                        or not ({'Value', 'Text'} & set(current.get('patterns') or ()))):
                    return None
                element = current

            if code and column in ('soluong', 'thanhtien'):
                code_editors = [item for item in self._uia_readback.query(
                    self, kinds=(50004, 50003), ids=('58',))
                                if item.get('parentToken') == parent_token and valid_box(item)]
                if (len(code_editors) != 1
                        or str(code_editors[0].get('value') or '').strip() != str(code).strip()):
                    return None

            control = self._uia_sale_item_cell_control(
                element, parent, row, column, code, new)
            if control is None:
                return None
            self._item_row_proof = {
                'count': expected_count, 'row': row, 'frame_id': self._frame_id,
                'input_epoch': self.input_epoch, 'checkpoint': self.checkpoint,
                'window': (self.window.hwnd, self.window.pid),
                'tab': tab_name, 'prompt': new,
                'source': 'UIA3.DataItem',
            }
            return control
        except Cancelled:
            raise
        except (EngineError, NeedsAttention, win32.WindowError):
            self._metrics['uia_field_fallbacks'] = self._metrics.get('uia_field_fallbacks', 0) + 1
            return None

    def _uia_sale_item_cell_from_grid(self, grid_control, row, column, *,
                                      code='', new=False, expected_total=None):
        """Resolve one existing row through the engine's GridPattern page."""
        if new or not self._uia_readback or not grid_control.target.get('uia_element'):
            return None
        page = self._uia_readback.grid(
            self, grid_control, start_row=0,
            maximum_rows=min(100, max(row, int(expected_total or 0))))
        row_count = int(page.get('rowCount', -1))
        if expected_total is not None and row_count != int(expected_total):
            return None
        if row_count < row or row_count > 100:
            return None
        rows = [item for item in page.get('rows') or ()
                if int(item.get('index', -1)) == row - 1]
        if len(rows) != 1:
            return None
        row_data = rows[0]
        parent = row_data.get('element')
        if not isinstance(parent, dict) or not parent.get('token'):
            return None
        if (parent.get('windowId') != self.window.hwnd or parent.get('offscreen')
                or not parent.get('enabled')):
            return None
        cells = [cell for cell in row_data.get('cells') or ()
                 if isinstance(cell, dict) and cell.get('windowId') == self.window.hwnd
                 and not cell.get('offscreen') and cell.get('enabled')]
        wanted_names = {text_key(name) for name in self.COLUMN_LABELS.get(column, ())}
        automation_id = {'mahang': '58', 'tenhang': '3',
                         'tktienchiphi': '4', 'tkdoanhthu': '5', 'dvt': '6',
                         'soluong': '8', 'dongia': '9', 'thanhtien': '11'}.get(column)
        matches = [cell for cell in cells
                   if (automation_id and str(cell.get('id') or '') == automation_id)
                   or (not automation_id and text_key(cell.get('name') or '') in wanted_names)]
        matches = [cell for cell in matches
                   if not wanted_names or text_key(cell.get('name') or '') in wanted_names]
        if len(matches) != 1:
            return None
        element = matches[0]
        if code and column in ('soluong', 'thanhtien'):
            code_cells = [cell for cell in cells if str(cell.get('id') or '') == '58'
                          and str(cell.get('value') or '').strip() == str(code).strip()]
            if len(code_cells) != 1:
                return None
        patterns = set(element.get('patterns') or ())
        if not ({'Value', 'Text'} & patterns) or element.get('value') is None:
            return None
        box = element.get('bounds') or {}
        pbox = parent.get('bounds') or {}
        values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
        parent_values = tuple(pbox.get(key) for key in ('x', 'y', 'width', 'height'))
        if (not all(isinstance(value, (int, float)) and math.isfinite(value)
                    for value in (*values, *parent_values))
                or values[2] <= 0 or values[3] <= 0
                or parent_values[2] <= 0 or parent_values[3] <= 0):
            return None
        if (values[0] < parent_values[0] or values[1] < parent_values[1]
                or values[0] + values[2] > parent_values[0] + parent_values[2]
                or values[1] + values[3] > parent_values[1] + parent_values[3]):
            return None
        return self._uia_sale_item_cell_control(
            element, parent, row, column, code, new)

    def _uia_reveal_sale_row(self, element, row, new):
        """Scroll one current UIA DataItem into view, then reacquire it."""
        from .uia_bridge import EngineError
        if self._uia_readback is None or not element.get('token'):
            return False
        patterns = set(element.get('patterns') or ())
        if not ({'Scroll', 'ScrollItem'} & patterns):
            return False
        box = element.get('bounds') or {}
        values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
        if not all(isinstance(value, (int, float)) and math.isfinite(value) for value in values):
            return False
        left, top = self._frame[0][:2]
        control = ScreenControl(
            self, id=str(element.get('id') or ''), name=f'row:{row}', kind=50029,
            bounds=(values[0] - left, values[1] - top, values[2], values[3]),
            enabled=bool(element.get('enabled')), role='row')
        control.target = {
            'type': 'sale_row', 'row': row, 'new': new,
            'uia': True, 'uia_element': element,
        }
        try:
            self._uia_readback.invoke(self, control, action='scroll')
        except (EngineError, NeedsAttention, win32.WindowError):
            return False
        self._input_sent()
        self._invalidate()
        return True

    def _uia_sale_item_count(self, *, required_tab='1. Hàng tiền'):
        """Return a complete current sale-row count, or None when virtualized."""
        if (not self.is_window('sale')
                or required_tab not in ('1. Hàng tiền', '2. Thuế')):
            return None
        from .uia_bridge import EngineError
        try:
            self._native_frame('sale', )
            if self._uia_readback is None:
                from .uia_readback import UiaReadback
                self._uia_readback = UiaReadback(self.cancel)
            tab = self._uia_readback.locate(
                self, name=required_tab, kind=50019, window_kind='sale')
            if ('SelectionItem' not in set(tab.get('patterns') or ())
                    or not tab.get('selected')):
                return None
            grid = self._uia_readback.locate(
                self, name='', kind=50025, window_kind='sale',
                automation_id='grdDetail')
            grid_box = grid.get('bounds') or {}
            grid_target = ScreenControl(
                self, id=grid.get('id') or 'grdDetail', name='Bảng hàng hóa',
                kind=50025, bounds=(
                    grid_box.get('x', 0) - self._frame[0][0],
                    grid_box.get('y', 0) - self._frame[0][1],
                    grid_box.get('width', 0), grid_box.get('height', 0)),
                role='grid')
            grid_target.target = {'type': 'grid', 'sale': True,
                                  'uia': True, 'uia_element': grid}
            try:
                page = self._uia_readback.grid(
                    self, grid_target, start_row=0, maximum_rows=100)
                current = self._uia_readback.bridge.call(
                    'read', {'token': grid.get('token')})
                row_count = int(page.get('rowCount', -1))
                rows = page.get('rows')
                columns = page.get('columns')
                row_ids = []
                for item in rows or ():
                    element = item.get('element') if isinstance(item, dict) else None
                    try:
                        row_ids.append(int((element or {}).get('id')))
                    except (TypeError, ValueError):
                        row_ids.append(None)
                if (current.get('windowId') == self.window.hwnd
                        and current.get('id') == grid.get('id')
                        and current.get('kind') == grid.get('kind')
                        and current.get('bounds') == grid.get('bounds')
                        and current.get('enabled') and not current.get('offscreen')
                        and 0 <= row_count <= 100
                        and isinstance(columns, list) and columns
                        and len(columns) == len(set(map(str, columns)))
                        and isinstance(rows, list) and len(rows) == row_count
                        and {int(item.get('index', -1)) for item in rows}
                            == set(range(row_count))
                        and all(row_id is not None for row_id in row_ids)):
                    visible_prompt = False
                    gx, gy, gw, gh = (grid_box.get(key) for key in ('x', 'y', 'width', 'height'))
                    for prompt in self._uia_readback.query(self, kinds=(50029,)):
                        try:
                            prompt_id = int(prompt.get('id'))
                        except (TypeError, ValueError):
                            continue
                        box = prompt.get('bounds') or {}
                        values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                        if (prompt_id == -1 and prompt.get('windowId') == self.window.hwnd
                                and not prompt.get('offscreen') and prompt.get('enabled')
                                and all(isinstance(value, (int, float)) and math.isfinite(value)
                                        for value in values + (gx, gy, gw, gh))
                                and values[2] > 0 and values[3] > 0
                                and values[0] < gx + gw and values[0] + values[2] > gx
                                and values[1] >= gy and values[1] + values[3] <= gy + gh):
                            visible_prompt = True
                            break
                    if not visible_prompt and row_ids and all(row_id == -1 for row_id in row_ids):
                        return 0
                    if (not visible_prompt and row_ids == list(range(row_count))
                            and str(required_tab).startswith('2.')):
                        return row_count
            except (EngineError, TypeError, ValueError):
                pass
            gx, gy, gw, gh = (grid_box.get(key) for key in ('x', 'y', 'width', 'height'))
            if not all(isinstance(value, (int, float)) and math.isfinite(value)
                       for value in (gx, gy, gw, gh)) or gw <= 0 or gh <= 0:
                return None

            rows = []
            add_rows = 0
            for item in self._uia_readback.query(self, kinds=(50029,)):
                try:
                    item_id = int(item.get('id'))
                except (TypeError, ValueError):
                    continue
                if item_id < -1 or not self._uia_readback.bridge.has_ancestor(
                        item, grid.get('token'), window_id=self.window.hwnd):
                    continue
                if item_id == -1:
                    box = item.get('bounds') or {}
                    values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                    if (item.get('windowId') == self.window.hwnd
                            and not item.get('offscreen') and item.get('enabled')
                            and all(isinstance(value, (int, float)) and math.isfinite(value)
                                    for value in values)
                            and values[2] > 0 and values[3] > 0
                            and values[0] < gx + gw and values[0] + values[2] > gx
                            and values[1] >= gy
                            and values[1] + values[3] <= gy + gh):
                        add_rows += 1
                    continue
                box = item.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                if (item.get('windowId') != self.window.hwnd
                        or item.get('offscreen') or not item.get('enabled')
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in values)
                        or values[2] <= 0 or values[3] <= 0
                        or values[0] < gx or values[1] < gy
                        or values[0] + values[2] > gx + gw
                        or values[1] + values[3] > gy + gh):
                    # A hidden or partially observed row means the UIA tree is
                    # virtualized; do not mistake the visible subset for count.
                    return None
                rows.append(item_id)
            if add_rows == 0:
                # The visible add-row prompt can temporarily omit its grid
                # ancestry after a previous row commits. Rebind it by the
                # current grid geometry when exactly one prompt is visible.
                prompts = []
                for item in self._uia_readback.query(self, kinds=(50029,)):
                    try:
                        item_id = int(item.get('id'))
                    except (TypeError, ValueError):
                        continue
                    if item_id != -1:
                        continue
                    box = item.get('bounds') or {}
                    values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                    if (item.get('windowId') == self.window.hwnd
                            and not item.get('offscreen') and item.get('enabled')
                            and all(isinstance(value, (int, float)) and math.isfinite(value)
                                    for value in values)
                            and values[2] > 0 and values[3] > 0
                            and values[0] < gx + gw and values[0] + values[2] > gx
                            and values[1] >= gy
                            and values[1] + values[3] <= gy + gh):
                        prompts.append(item)
                if len(prompts) == 1:
                    add_rows = 1
            expected_add_rows = (0, 1) if required_tab == '2. Thuế' else (1,)
            if (add_rows not in expected_add_rows or len(rows) != len(set(rows))
                    or sorted(rows) != list(range(len(rows)))):
                return None
            return len(rows)
        except Cancelled:
            raise
        except (EngineError, NeedsAttention, win32.WindowError):
            self._metrics['uia_grid_count_fallbacks'] = (
                self._metrics.get('uia_grid_count_fallbacks', 0) + 1)
            return None

    def _uia_sale_item_cell_control(self, element, parent, row, column, code, new):
        """Create a guarded sale-cell control from a current UIA element pair."""
        left, top = self._frame[0][:2]
        pbox = parent.get('bounds') or {}
        ex, ey = element['bounds']['x'], element['bounds']['y']
        ew, eh = element['bounds']['width'], element['bounds']['height']
        if not (pbox.get('x', 0) <= ex and pbox.get('y', 0) <= ey
                and ex + ew <= pbox.get('x', 0) + pbox.get('width', 0)
                and ey + eh <= pbox.get('y', 0) + pbox.get('height', 0)):
            return None
        box = element['bounds']
        control = ScreenControl(
            self, id=element.get('id') or '', name=column,
            kind=int(element.get('kind') or 50004), role='cell',
            bounds=(box['x'] - left, box['y'] - top, box['width'], box['height']),
            enabled=bool(element.get('enabled')), value_override=element.get('value'))
        control.target = {'type': 'sale_item_cell', 'row': row, 'column': column,
                          'code': code, 'new': new, 'uia': True,
                          'uia_element': element, 'uia_row_parent': parent,
                          'uia_automation_id': element.get('id') or ''}
        return control

    def _uia_catalog_option(self, text, bounds, dropdown_bounds, field_bounds):
        """Use a current visible ListItem only when its parent proves this dropdown."""
        if not self.is_window('new_item') or not self._frame:
            return None
        from .uia_bridge import EngineError
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        try:
            element = self._uia_readback.locate_near(
                self, bounds=bounds, kind=50007, vertical_only=True)
            actual_text = str(element.get('name') or element.get('value') or '')
            if text_key(actual_text) != text_key(text):
                raise EngineError('UIA_TARGET_MISMATCH',
                                  'Catalog ListItem text disagrees with current native value.')
            if 'SelectionItem' not in set(element.get('patterns') or ()):
                raise EngineError('PATTERN_UNSUPPORTED',
                                  'Catalog ListItem has no SelectionItem pattern.')
            parent_token = element.get('parentToken')
            if not parent_token:
                raise EngineError('UIA_TARGET_UNAVAILABLE',
                                  'Catalog option has no current UIA parent proof.')
            parent = self._uia_readback.bridge.call('read', {'token': parent_token})
            if (parent.get('windowId') != self.window.hwnd
                    or parent.get('offscreen') or not parent.get('enabled')):
                raise EngineError('UIA_TARGET_UNAVAILABLE',
                                  'Catalog option parent is not visible and enabled.')
            left, top = self._frame[0][:2]
            dx, dy, dw, dh = (float(value) for value in dropdown_bounds)
            dropdown = (dx + left, dy + top, dw, dh)
            fx, fy, fw, fh = (float(value) for value in field_bounds)
            field = (fx + left, fy + top, fw, fh)
            pbox = parent.get('bounds') or {}
            px, py, pw, ph = (pbox.get(key) for key in ('x', 'y', 'width', 'height'))
            ebox = element.get('bounds') or {}
            ex, ey, ew, eh = (ebox.get(key) for key in ('x', 'y', 'width', 'height'))
            if not all(isinstance(value, (int, float)) and math.isfinite(value)
                       for value in (px, py, pw, ph, ex, ey, ew, eh)):
                raise EngineError('UIA_TARGET_MISMATCH',
                                  'Catalog option geometry is not finite.')
            parent_matches_field = (
                abs((px + pw / 2) - (field[0] + field[2] / 2)) <= max(12, field[2] * .15)
                and abs((py + ph / 2) - (field[1] + field[3] / 2)) <= max(12, field[3] * .75)
                and abs(pw - field[2]) <= max(24, field[2] * .25)
                and abs(ph - field[3]) <= max(12, field[3] * .5))
            option_in_dropdown = (
                ex >= dropdown[0] - 3 and ey >= dropdown[1] - 3
                and ex + ew <= dropdown[0] + dropdown[2] + 3
                and ey + eh <= dropdown[1] + dropdown[3] + 3)
            if not parent_matches_field or not option_in_dropdown:
                raise EngineError('UIA_TARGET_MISMATCH',
                                  'Catalog option parent does not match the current dropdown.')
            element = dict(element, catalogParent=parent)
            box = element['bounds']
            control = ScreenControl(
                self, id=element.get('id') or '', name=text, kind=50007,
                bounds=(box['x'] - left, box['y'] - top, box['width'], box['height']),
                enabled=bool(element.get('enabled')), selected=bool(element.get('selected')),
                role='option')
            control.target = {'type': 'catalog_option', 'text': text, 'uia': True,
                              'uia_element': element,
                              'uia_automation_id': element.get('id') or ''}
            return control
        except Cancelled:
            raise
        except (EngineError, NeedsAttention, win32.WindowError):
            self._metrics['uia_option_fallbacks'] = self._metrics.get('uia_option_fallbacks', 0) + 1
            return None

    def _uia_catalog_option_by_parent(self, text, dropdown_bounds, parent):
        """Find one current ListItem by semantic text under the open ComboBox."""
        if not (self.is_window('new_item') or self.is_window('sale')) or parent is None:
            return None
        saved_parent = (parent.target or {}).get('uia_element')
        parent_token = saved_parent.get('token') if isinstance(saved_parent, dict) else None
        if not parent_token:
            return None
        from .uia_bridge import EngineError
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        try:
            current_parent = self._uia_readback.bridge.call('read', {'token': parent_token})
            if (current_parent.get('windowId') != self.window.hwnd
                    or current_parent.get('offscreen') or not current_parent.get('enabled')
                    or current_parent.get('bounds') != saved_parent.get('bounds')):
                raise EngineError('STALE_ACTION_TARGET',
                                  'Catalog ComboBox changed before option lookup.')
            # The popup provider reparents visible items under a transient
            # ValueListDropDown; editor descendants under the combo keep the
            # same text but have zero-sized bounds. Query current ListItems
            # globally, then bind the visible popup back to this combo by
            # geometry and its transient parent identity.
            # Unit lists expose ListItem; warehouse/account lists can expose
            # the same choice as a DataItem inside a transient grid.
            options = self._uia_readback.query(self, kinds=(50007, 50029, 50008))
            pbox = current_parent.get('bounds') or {}
            px, py = pbox.get('x'), pbox.get('y')
            pw, ph = pbox.get('width'), pbox.get('height')
            left, top = self._frame[0][:2]
            if dropdown_bounds is not None:
                x, y, width, height = (float(value) for value in dropdown_bounds)
                dx, dy, dw, dh = x + left, y + top, width, height
            found = []
            tax_option = (self.is_window('sale')
                          and (parent.target or {}).get('type') == 'sale_item_cell'
                          and (parent.target or {}).get('column') == 'thuesuatgtgt')
            wanted_text = text_key(text)
            wanted_tax = normalized(text, 'tax_rate') if tax_option else None
            for option in options:
                actual = str(option.get('name') or option.get('value') or '')
                box = option.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                patterns = set(option.get('patterns') or ())
                text_matches = (text_key(actual) == wanted_text)
                if tax_option and not text_matches:
                    try:
                        text_matches = normalized(actual, 'tax_rate') == wanted_tax
                    except (ValueError, NeedsAttention):
                        text_matches = False
                if (not text_matches
                        or not ({'SelectionItem', 'Invoke'} & patterns)
                        or option.get('windowId') != self.window.hwnd
                        or option.get('offscreen') or not option.get('enabled')
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in values)
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in (px, py, pw, ph))
                        or values[2] <= 0 or values[3] <= 0
                        or values[0] < px - 24 or values[0] + values[2] > px + pw + 80
                        or values[1] < py + ph - 3 or values[1] > py + ph + 240):
                    continue
                ex, ey, ew, eh = values
                in_dropdown = (dropdown_bounds is None or
                               (ex >= dx - 3 and ey >= dy - 3
                                and ex + ew <= dx + dw + 3 and ey + eh <= dy + dh + 3))
                if in_dropdown:
                    option_parent = self._uia_readback.bridge.call(
                        'read', {'token': option.get('parentToken')}) if option.get('parentToken') else {}
                    option_parent_box = option_parent.get('bounds') or {}
                    opx, opy = option_parent_box.get('x'), option_parent_box.get('y')
                    opw, oph = option_parent_box.get('width'), option_parent_box.get('height')
                    parent_matches = (option_parent.get('windowId') == self.window.hwnd
                            and option_parent.get('enabled')
                            and not option_parent.get('offscreen')
                            and option_parent.get('kind') in (50008, 50010, 50018, 50025, 50029, 50033)
                            and all(isinstance(value, (int, float)) and math.isfinite(value)
                                    for value in (opx, opy, opw, oph))
                            and opx <= ex and opy <= ey
                            and ex + ew <= opx + opw and ey + eh <= opy + oph
                            and abs((opx + opw / 2) - (px + pw / 2)) <= max(32, pw * .5)
                            and opy >= py + ph - 3)
                    grid_row_matches = (int(option.get('kind') or 0) == 50029
                                        and not option.get('parentToken')
                                        and ey >= py + ph - 3
                                        and ey <= py + ph + 240)
                    if parent_matches or grid_row_matches:
                        found.append(dict(option, catalogPopupParent=option_parent))
            if len(found) != 1:
                return None
            option = found[0]
            box = option['bounds']
            control = ScreenControl(
                self, id=option.get('id') or '', name=text,
                kind=int(option.get('kind') or 50007),
                bounds=(box['x'] - left, box['y'] - top, box['width'], box['height']),
                enabled=bool(option.get('enabled')), selected=bool(option.get('selected')),
                role='option')
            option = dict(option, catalogParent=current_parent)
            control.target = {'type': 'catalog_option', 'text': text, 'uia': True,
                              'uia_element': option,
                              'uia_automation_id': option.get('id') or ''}
            return control
        except Cancelled:
            raise
        except (EngineError, NeedsAttention, win32.WindowError):
            self._metrics['uia_option_fallbacks'] = self._metrics.get('uia_option_fallbacks', 0) + 1
            return None

    def _uia_find_filter(self, label):
        """Resolve the active custom-grid filter editor from current UIA state."""
        automation_id = {'Mã hàng': '3', 'Tên hàng': '4', 'ĐVT': '14'}.get(label)
        if not self.is_window('find') or label not in ('Mã hàng', 'Tên hàng', 'ĐVT'):
            return None
        try:
            self._native_frame('find', )
            if self._uia_readback is None:
                from .uia_readback import UiaReadback
                self._uia_readback = UiaReadback(self.cancel)
            left, top, right, bottom = self._frame[0]

            def is_result_editor(item):
                token = item.get('parentToken')
                seen = set()
                for _ in range(8):
                    if not token or token in seen:
                        return False
                    seen.add(token)
                    parent = self._uia_readback.bridge.call('read', {'token': token})
                    if parent.get('windowId') != self.window.hwnd:
                        return False
                    if parent.get('kind') == 50029:
                        # The filter row, when exposed as a DataItem, uses the
                        # sentinel -1; numbered DataItems are result rows.
                        return str(parent.get('id') or '') not in ('', '-1')
                    token = parent.get('parentToken')
                return False

            candidates = []
            for item in self._uia_readback.query(
                    self, kinds=(50004, 50003), ids=(automation_id,)):
                box = item.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                if (item.get('windowId') != self.window.hwnd
                        or not item.get('enabled') or item.get('offscreen')
                        or is_result_editor(item)
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in values)
                        or values[2] <= 0 or values[3] <= 0
                        or values[0] < left or values[1] < top
                        or values[0] + values[2] > right
                        or values[1] + values[3] > bottom
                        or not ({'Value', 'Text'} & set(item.get('patterns') or ()))):
                    continue
                candidates.append(item)
            if len(candidates) != 1:
                return None
            element = candidates[0]
            left, top = self._frame[0][:2]
            box = element['bounds']
            control = ScreenControl(
                self, id='find-filter:' + normalize(label), name=label,
                kind=int(element.get('kind') or 50004), bounds=(box['x'] - left,
                box['y'] - top, box['width'], box['height']), enabled=bool(element.get('enabled')),
                role='find_filter')
            control.target = {'type': 'find_filter', 'id': control.id,
                              'label': label, 'uia': True,
                              'uia_element': element,
                              'uia_automation_id': element.get('id') or ''}
            return control
        except Cancelled:
            raise
        except (NeedsAttention, win32.WindowError):
            self._metrics['uia_field_fallbacks'] = self._metrics.get('uia_field_fallbacks', 0) + 1
            return None

    def _uia_find_rows(self):
        """Resolve the visible Find result rows from the current UIA grid."""
        if not self.is_window('find'):
            return None
        from .uia_bridge import EngineError
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        try:
            self._native_frame('find', )
            grid = self._uia_readback.locate(
                self, name='', kind=50025, window_kind='find',
                automation_id='grdData')
            grid_box = grid.get('bounds') or {}
            def valid(element, *, read_only=False):
                box = element.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                if (element.get('windowId') != self.window.hwnd
                        or (not read_only and not element.get('enabled'))
                        or element.get('offscreen')
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in values)
                        or values[2] <= 0 or values[3] <= 0):
                    return False
                x, y, width, height = values
                # A horizontally clipped trailing column can extend past the
                # viewport while still being a current, readable grid cell.
                gx, gy = grid_box.get('x', 0), grid_box.get('y', 0)
                gw, gh = grid_box.get('width', 0), grid_box.get('height', 0)
                return (x < gx + gw and x + width > gx
                        and y < gy + gh and y + height > gy)

            def valid_row(element):
                box = element.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                if (element.get('windowId') != self.window.hwnd
                        or not element.get('enabled')
                        or element.get('offscreen')
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in values)
                        or values[2] <= 0 or values[3] <= 0):
                    return False
                x, y, width, height = values
                gx, gy = grid_box.get('x', 0), grid_box.get('y', 0)
                gw, gh = grid_box.get('width', 0), grid_box.get('height', 0)
                return (x < gx + gw and x + width > gx
                        and y >= gy and y + height <= gy + gh)

            all_rows = self._uia_readback.query(
                self, kinds=(50029,), parent_token=grid.get('token'))
            rows = [item for item in all_rows
                    if valid_row(item) and item.get('id') not in ('', '-1')]
            # The custom grid always exposes an enabled sentinel DataItem for
            # its filter row. When that is the only row, the current search is
            # a confirmed empty result set rather than incomplete UIA data.
            if not rows:
                filter_rows = [item for item in all_rows
                               if valid_row(item) and str(item.get('id')) == '-1']
                if filter_rows:
                    return []
            by_id = {}
            for item in rows:
                try:
                    index = int(item.get('id'))
                except (TypeError, ValueError):
                    continue
                if index < 0 or index in by_id:
                    return None
                by_id[index] = item
            if not by_id or sorted(by_id) != list(range(len(by_id))):
                return None

            rows_out = []
            cell_ids = {'mahang': '3', 'tenhang': '4',
                        'soluongton': '53', 'dvt': '14'}
            for index in sorted(by_id):
                row_parent = by_id[index]
                token = row_parent.get('token')
                if not token:
                    return None
                editors = self._uia_readback.query(self, kinds=(50004,),
                                                   parent_token=token)
                cells = {}
                for column, automation_id in cell_ids.items():
                    matched = [item for item in editors
                               if item.get('id') == automation_id
                               and item.get('parentToken') == token
                               and item.get('value') is not None
                               and valid(item, read_only=True)]
                    if len(matched) != 1:
                        return None
                    item = matched[0]
                    box = item['bounds']
                    left, top = self._frame[0][:2]
                    cell = ScreenControl(
                        self, id=automation_id, name=column, kind=50004,
                        bounds=(box['x'] - left, box['y'] - top,
                                box['width'], box['height']),
                        enabled=bool(item.get('enabled')), role='cell',
                        parent_key=f'row:{index}', value_override=item.get('value'))
                    cell.target = {'type': 'cell',
                                   'row': {'type': 'row', 'sale': False,
                                           'index': index, 'add': False,
                                           'code': str(cells.get('mahang').value_override)
                                           if cells.get('mahang') else str(item.get('value') or '')},
                                   'column': column, 'uia': True,
                                   'uia_element': item,
                                   'uia_automation_id': item.get('id') or ''}
                    cells[column] = cell
                row_target = {'type': 'row', 'sale': False, 'index': index,
                              'add': False,
                              'code': str(cells['mahang'].value_override or '')}
                for cell in cells.values():
                    cell.target['row'] = row_target
                checkbox_items = [item for item in self._uia_readback.query(
                    self, kinds=(50002,), parent_token=token)
                                  if item.get('parentToken') == token
                                  and item.get('windowId') == self.window.hwnd
                                  and not item.get('offscreen') and item.get('enabled')
                                  and 'Toggle' in set(item.get('patterns') or ())]
                if len(checkbox_items) != 1:
                    return None
                checkbox_item = checkbox_items[0]
                box = checkbox_item['bounds']
                checkbox = ScreenControl(
                    self, id=f'find:{index}', name='Chọn', kind=50002,
                    bounds=(box['x'] - left, box['y'] - top,
                            box['width'], box['height']),
                    enabled=True, selected=bool(checkbox_item.get('toggle')),
                    toggle=int(checkbox_item.get('toggle') or 0), role='find_checkbox')
                checkbox.target = {'type': 'find_checkbox', 'row': row_target,
                                   'uia': True, 'uia_element': checkbox_item,
                                   'uia_automation_id': checkbox_item.get('id') or ''}
                box = row_parent['bounds']
                row = ScreenControl(
                    self, id=str(index), name=str(index + 1), kind=50029,
                    bounds=(box['x'] - left, box['y'] - top,
                            box['width'], box['height']), enabled=True, role='row',
                    parent_key='find', target=row_target)
                row._cells = cells
                row._uia_checkbox = checkbox
                rows_out.append(row)
            return rows_out
        except Cancelled:
            raise
        except (EngineError, NeedsAttention, win32.WindowError):
            self._metrics['uia_find_row_fallbacks'] = self._metrics.get('uia_find_row_fallbacks', 0) + 1
            return None

    def _uia_find_count(self):
        """Read the current Find result count from one visible UIA Text node."""
        if not self.is_window('find'):
            return None
        from .uia_bridge import EngineError
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        try:
            self._native_frame('find', )
            left, top, right, bottom = self._frame[0]
            candidates = []
            for item in self._uia_readback.query(self, kinds=(50020,)):
                value = str(item.get('value') or '')
                match = re.search(r'(\d+)\s*$', value)
                box = item.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                if (not match or item.get('windowId') != self.window.hwnd
                        or item.get('offscreen')
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in values)
                        or values[2] <= 0 or values[3] <= 0
                        or values[0] < left or values[1] < top
                        or values[0] + values[2] > right
                        or values[1] + values[3] > bottom
                        or values[1] < top + (bottom - top) * .55):
                    continue
                candidates.append(int(match.group(1)))
            unique = sorted(set(candidates))
            if len(unique) != 1:
                return None
            return unique[0]
        except Cancelled:
            raise
        except (EngineError, NeedsAttention, win32.WindowError):
            self._metrics['uia_find_count_fallbacks'] = self._metrics.get('uia_find_count_fallbacks', 0) + 1
            return None

    def _uia_find_row_cell(self, row_index, column, bounds):
        """Read-only UIA corroboration for one visible find-result editor."""
        if not self.is_window('find'):
            return None
        cache_key = (self._frame_id, row_index, column, tuple(bounds))
        if cache_key in self._uia_find_row_reads:
            return self._uia_find_row_reads[cache_key]
        automation_id = {'mahang': '3', 'tenhang': '4',
                         'soluongton': '53', 'dvt': '14'}.get(column)
        if automation_id is None:
            return None
        from .uia_bridge import EngineError
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        try:
            element = self._uia_readback.locate_near(
                self, bounds=bounds, kind=50004, automation_id=automation_id,
                vertical_only=True)
            if ('Value' not in set(element.get('patterns') or ())
                    and 'Text' not in set(element.get('patterns') or ())):
                raise EngineError('PATTERN_UNSUPPORTED',
                                  'Find result editor has no readable UIA pattern.')
            if element.get('value') is None:
                raise EngineError('UIA_TARGET_UNAVAILABLE',
                                  'Find result editor returned no current value.')
            parent_token = element.get('parentToken')
            if not parent_token:
                raise EngineError('UIA_TARGET_UNAVAILABLE',
                                  'Find result editor has no current row parent.')
            parent = self._uia_readback.bridge.call('read', {'token': parent_token})
            if (parent.get('windowId') != self.window.hwnd or parent.get('kind') != 50029
                    or parent.get('id') != str(row_index)
                    or parent.get('offscreen') or not parent.get('enabled')):
                raise EngineError('UIA_TARGET_MISMATCH',
                                  'Find result editor belongs to another current row.')
            result = dict(element, rowParent=parent)
            self._uia_find_row_reads[cache_key] = result
            return result
        except Cancelled:
            raise
        except (EngineError, NeedsAttention, win32.WindowError):
            self._metrics['uia_find_row_fallbacks'] = self._metrics.get('uia_find_row_fallbacks', 0) + 1
            return None

    def _uia_static_target(self, target):
        """Resolve fixed semantic controls through current UIA identity."""
        kind = target.get('type')
        if kind == 'sales_add':
            name, control_type, window_kind = 'Thêm', 50000, 'main'
        elif kind == 'word' and target.get('kind') == 50000:
            # A native text element may expose a button caption without a button pattern. Reuse the
            # current caption/kind proof before requiring a UIA action target.
            name, control_type = target.get('text', ''), 50000
            window_kind = next((surface for surface in ('main', 'sale', 'find', 'new_item')
                                if self.is_window(surface)), None)
        elif kind == 'tab':
            name, control_type = target.get('text', ''), 50019
            window_kind = 'sale' if self.is_window('sale') else 'new_item'
        elif kind == 'item_dialog_button':
            name, control_type, window_kind = target.get('text', ''), 50000, target.get('window_kind')
        elif kind == 'fixed_button':
            name, control_type, window_kind = target.get('name', ''), 50000, target.get('window_kind')
        else:
            return None
        if not name or not self.window or not self._frame:
            return None
        if window_kind and not self.is_window(window_kind):
            return None
        known_ids = {
            ('sales_add', 'Thêm', 'main'): 'mnuAdd',
            ('fixed_button', '\u0110\u00f3ng', 'sale'): 'mnuClose',
            ('fixed_button', '\u0110\u00f3ng', 'find'): 'mnuClose',
            ('fixed_button', '\u0110\u00f3ng', 'new_item'): 'mnuClose',
            ('fixed_button', 'C\u1ea5t', 'new_item'): 'mnuSave',
            ('item_dialog_button', 'C\u1ea5t', 'new_item'): 'mnuSave',
            ('item_dialog_button', 'H\u1ee7y b\u1ecf', 'find'): 'btnCancel',
            ('item_dialog_button', 'H\u1ee7y b\u1ecf', 'new_item'): 'btnCancel',
            ('item_dialog_button', '\u0110\u1ed3ng \u00fd', 'find'): 'btnOK',
            ('item_dialog_button', '\u0110\u1ed3ng \u00fd', 'new_item'): 'btnOK',
            ('item_dialog_button', 'Gi\u00fap', 'find'): 'btnHelp',
            ('item_dialog_button', 'Gi\u00fap', 'new_item'): 'btnHelp',
        }
        automation_id = (target.get('uia_automation_id')
                          or target.get('automation_id')
                          or known_ids.get((kind, name, window_kind)))
        from .uia_bridge import EngineError
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        try:
            element = self._uia_readback.locate(
                self, name=name, kind=control_type, window_kind=window_kind,
                automation_id=automation_id)
            if (element.get('name') and normalize(element.get('name')) != normalize(name)):
                raise EngineError('UIA_TARGET_MISMATCH',
                                  'UIA control name disagrees with the requested semantic target.')
            if kind == 'tab' and 'SelectionItem' not in set(element.get('patterns') or ()):
                raise EngineError('PATTERN_UNSUPPORTED',
                                  'The current tab has no SelectionItem pattern.')
        except Cancelled:
            raise
        except EngineError as error:
            # Some MISA builds expose descriptive AutomationIds such as
            # ``[Toolbar ...] Tool : mnuAdd`` instead of the stable suffix.
            # Resolve that suffix only when one current enabled element matches
            # the requested kind and caption; ambiguity stays fail-closed.
            candidates = []
            if automation_id:
                try:
                    id_tokens = [str(automation_id)]
                    elements = self._uia_readback.query(self, kinds=(control_type,))
                    candidates = [item for item in elements
                                  if item.get('windowId') == self.window.hwnd
                                  and item.get('enabled') and not item.get('offscreen')
                                  and normalize(item.get('name') or '') == normalize(name)
                                  and any(token.casefold() in str(item.get('id') or '').casefold()
                                          for token in id_tokens)]
                except (EngineError, NeedsAttention, win32.WindowError):
                    candidates = []
            if len(candidates) == 1:
                element = candidates[0]
            else:
                # Some dialog buttons keep the exact caption and button type
                # but expose a revision-specific AutomationId. The current
                # window and an unambiguous caption still bind the action.
                if kind == 'item_dialog_button':
                    try:
                        elements = self._uia_readback.query(self, kinds=(control_type,))
                        candidates = [item for item in elements
                                      if item.get('windowId') == self.window.hwnd
                                      and item.get('enabled') and not item.get('offscreen')
                                      and normalize(item.get('name') or '') == normalize(name)]
                    except (EngineError, NeedsAttention, win32.WindowError):
                        candidates = []
                if len(candidates) == 1:
                    element = candidates[0]
                else:
                    self.last_action['uia_target_fallback'] = error.code + ': ' + str(error)
                    return None
        left, top = self._frame[0][:2]
        box = element['bounds']
        control = ScreenControl(self, id=element.get('id') or '', name=element.get('name') or name,
                                kind=int(element.get('kind') or control_type),
                                bounds=(box['x'] - left, box['y'] - top, box['width'], box['height']),
                                enabled=bool(element.get('enabled')), selected=bool(element.get('selected')),
                                role='tab' if kind == 'tab' else 'button')
        control.target = dict(target, uia=True, uia_element=element,
                              uia_automation_id=element.get('id') or '')
        return control

    def _uia_tab_state(self, name):
        """Read the current TabItem selection from native state."""
        if not self.window or not self._frame:
            return None
        window_kind = 'sale' if self.is_window('sale') else 'new_item' if self.is_window('new_item') else None
        if window_kind is None:
            return None
        from .uia_bridge import EngineError
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        try:
            element = self._uia_readback.locate(
                self, name=name, kind=50019, window_kind=window_kind)
            patterns = set(element.get('patterns') or ())
            if 'SelectionItem' not in patterns:
                raise EngineError('PATTERN_UNSUPPORTED', 'The current tab has no SelectionItem pattern.')
            current = self._uia_readback.bridge.call('read', {'token': element['token']})
            if (current.get('windowId') != self.window.hwnd
                    or current.get('kind') != 50019
                    or current.get('name') != element.get('name')
                    or current.get('id') != element.get('id')
                    or current.get('bounds') != element.get('bounds')
                    or current.get('offscreen') or not current.get('enabled')
                    or 'SelectionItem' not in set(current.get('patterns') or ())):
                raise EngineError('STALE_ACTION_TARGET', 'Tab identity changed during UIA selection readback.')
            return current
        except Cancelled:
            raise
        except (EngineError, NeedsAttention, win32.WindowError) as error:
            self.last_action['uia_tab_fallback'] = str(error)
            return None

    def _uia_field_target(self, ids, label=''):
        """Resolve a known editor by AutomationId on the current native frame."""
        candidates = tuple(item_id for item_id in ids if item_id)
        if not candidates or not self.window:
            return None
        if not any(item_id in self.FIELD_LAYOUT or item_id in self.TOTALS
                   or item_id in self.INVENTORY_VALUE_RULES
                   or item_id in self.OBSERVED_SALE_READONLY_IDS for item_id in candidates):
            return None
        window_kind = next((kind for kind in ('sale', 'find', 'new_item')
                            if self.is_window(kind)), None)
        if window_kind is None:
            return None
        try:
            self._native_frame(window_kind, )
        except (NeedsAttention, win32.WindowError):
            return None
        from .uia_bridge import EngineError
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        lookups = list(candidates)
        # The invoice tab uses the observed-form alias for this same editor on
        # some MISA builds; retain the canonical workflow id in the control.
        if 'txtInvoiceNo' in candidates and 'txtInvNo' not in lookups:
            lookups.append('txtInvNo')
        for item_id in candidates:
            for alias in self.FIELD_AUTOMATION_ID_ALIASES.get(item_id, ()):
                if alias not in lookups:
                    lookups.append(alias)
        for item_id in lookups:
            control_type = 50003 if item_id.startswith('cbo') else 50004
            try:
                element = self._uia_readback.locate(
                self, name=(label or self.FIELD_LAYOUT.get(item_id, ('',))[0]
                            or self.OBSERVED_SALE_READONLY_FIELDS.get(item_id, '')),
                    kind=control_type, window_kind=window_kind, automation_id=item_id)
            except Cancelled:
                raise
            except EngineError as error:
                self.last_action['uia_field_fallback'] = error.code + ': ' + str(error)
                continue
            if not element.get('token'):
                self.last_action['uia_field_fallback'] = (
                    'UIA_TARGET_UNAVAILABLE: field has no current token.')
                continue
            if (element.get('name') and label
                    and normalize(element.get('name')) != normalize(label)):
                self.last_action['uia_field_fallback'] = (
                    'UIA_TARGET_MISMATCH: UIA field name disagrees with the requested field.')
                continue
            left, top = self._frame[0][:2]
            box = element['bounds']
            canonical_id = candidates[0]
            control = ScreenControl(
                self, id=canonical_id,
                name=(label or self.FIELD_LAYOUT.get(canonical_id, ('',))[0]
                      or self.OBSERVED_SALE_READONLY_FIELDS.get(canonical_id, '')),
                kind=control_type,
                bounds=(box['x'] - left, box['y'] - top, box['width'], box['height']),
                enabled=bool(element.get('enabled')), role='field')
            control.target = {'type': 'field', 'id': canonical_id,
                              'label': control.name, 'uia': True,
                              'uia_element': element,
                              'uia_automation_id': element.get('id') or ''}
            return control
        if window_kind == 'sale' and self.checkpoint.startswith('add.') and self._frame is not None:
            # The voucher form can expose editable WinForms children without
            # a usable UIA Value pattern. Bind known header editors to this
            # fresh frame so guarded Win32 typing can continue.
            width, height = self._frame[1]
            layouts = {
                'cboAccountObjectID': (.098, .253, .188, .032),
                'txtAccountObjectName': (.291, .253, .454, .032),
                'dtePostedDate': (.416, .253, .118, .032),
                'dteRefDate': (.416, .284, .118, .032),
                'txtInvoiceNo': (.416, .315, .118, .032),
            }
            layout = layouts.get(candidates[0])
            if layout:
                x, y, w, h = (round(width * layout[0]), round(height * layout[1]),
                              round(width * layout[2]), round(height * layout[3]))
                control = ScreenControl(
                    self, id=candidates[0], name=(label or candidates[0]),
                    kind=50003 if candidates[0].startswith('cbo') else 50004,
                    bounds=(x, y, w, h), enabled=True, role='field',
                    target={'type': 'field', 'id': candidates[0],
                            'label': label or candidates[0],
                            'coordinate_fallback': True,
                            'coordinate': [x + w // 2, y + h // 2],
                            'coordinate_space': 'window',
                            'frame_id': self._frame_id})
                return control
        return None

    def _uia_inventory_menu_current(self, action, row):
        """Resolve the two live inline catalog buttons from the proven row anchor."""
        if action not in ('new', 'search') or not self.is_window('sale'):
            return None
        proof = getattr(self, '_popup_tab_proof', None) or {}
        anchor = proof.get('anchor_bounds')
        if (proof.get('row') != row
                or proof.get('checkpoint') != self.checkpoint
                or proof.get('window') != (self.window.hwnd, self.window.pid)
                or proof.get('input_epoch') not in (self.input_epoch, self.input_epoch - 1)
                or not isinstance(anchor, (list, tuple)) or len(anchor) != 4
                or not all(isinstance(value, (int, float)) and math.isfinite(value)
                           for value in anchor)):
            return None
        from .uia_bridge import EngineError
        try:
            self._native_frame('sale', fresh=True, )
            if tuple(proof.get('anchor_capture_box') or ()) != tuple(self._frame[0]):
                raise EngineError('STALE_ACTION_TARGET',
                                  'The inventory row anchor moved before menu lookup.')
            tab = self._uia_tab_state('1. Hàng tiền')
            if not tab or not tab.get('selected'):
                raise EngineError('UIA_TARGET_MISMATCH',
                                  'The inventory menu is not on the selected item tab.')
            if self._uia_readback is None:
                from .uia_readback import UiaReadback
                self._uia_readback = UiaReadback(self.cancel)
            row_token = proof.get('anchor_row_token')
            if row_token:
                expected_row_id = '-1' if proof.get('prompt') else str(row - 1)
                expected_row_box = proof.get('anchor_row_bounds') or {}
                try:
                    current_row = self._uia_readback.bridge.call('read', {'token': row_token})
                except EngineError as error:
                    if 'expired' not in str(error).casefold():
                        raise
                    # Expanding the editor can recreate its provider. Rebind only
                    # the same visible row at the proven bounds in this window.
                    rows = [item for item in self._uia_readback.query(self, kinds=(50029,))
                            if item.get('windowId') == self.window.hwnd
                            and str(item.get('id')) == expected_row_id
                            and item.get('bounds') == expected_row_box
                            and not item.get('offscreen') and item.get('enabled')]
                    if len(rows) != 1:
                        raise EngineError('STALE_ACTION_TARGET',
                                          'The proven inventory row cannot be rebound.') from error
                    current_row = rows[0]
                    proof['anchor_row_token'] = current_row.get('token')
                row_box = current_row.get('bounds') or {}
                if (current_row.get('windowId') != self.window.hwnd
                        or current_row.get('kind') != 50029
                        or str(current_row.get('id')) != expected_row_id
                        or current_row.get('offscreen') or not current_row.get('enabled')
                        or (expected_row_box and row_box != expected_row_box)):
                    raise EngineError('STALE_ACTION_TARGET',
                                      'The proven inventory row changed before menu lookup.')
                if not all(isinstance(row_box.get(key), (int, float))
                           and math.isfinite(row_box.get(key))
                           for key in ('x', 'y', 'width', 'height')):
                    raise EngineError('UIA_TARGET_MISMATCH',
                                      'The current inventory row has invalid bounds.')
            left, top = self._frame[0][:2]
            x, y, width, height = (float(value) for value in anchor)
            ax, ay = x + left, y + top
            right = ax + width
            candidates = []
            for element in self._uia_readback.query(self, kinds=(50000,)):
                box = element.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                if (element.get('windowId') != self.window.hwnd
                        or element.get('offscreen') or not element.get('enabled')
                        or 'Invoke' not in set(element.get('patterns') or ())
                        or not element.get('token')
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in values)):
                    continue
                ex, ey, ew, eh = values
                center_y = ey + eh / 2
                if (right - max(8, width * .08) <= ex
                        and ex + ew <= right + max(72, width * .65)
                        and ay - 3 <= center_y <= ay + height + 3
                        and 4 <= ew <= max(36, width * .35)
                        and 4 <= eh <= max(36, height * 1.8)):
                    candidates.append(element)
            candidates.sort(key=lambda item: (item['bounds']['x'], item['bounds']['y']))
            if len(candidates) != 2:
                raise EngineError('UIA_TARGET_AMBIGUOUS',
                                  'The current row does not expose exactly two inline catalog buttons.')
            element = candidates[0 if action == 'new' else 1]
            box = element['bounds']
            name = 'Thêm vật tư' if action == 'new' else 'Tìm vật tư'
            control = ScreenControl(
                self, id=element.get('id') or '', name=name, kind=50000,
                bounds=(box['x'] - left, box['y'] - top, box['width'], box['height']),
                enabled=True, role='button')
            control.target = {
                'type': 'inventory_menu', 'action': action, 'uia': True,
                'uia_element': element,
                'uia_automation_id': element.get('id') or '',
                'uia_inventory_menu_order': 'left_new_right_search',
            }
            return control
        except Cancelled:
            raise
        except (EngineError, NeedsAttention, win32.WindowError) as error:
            self.last_action['uia_inventory_menu_fallback'] = (
                getattr(error, 'code', type(error).__name__) + ': ' + str(error))
            return None

    def _uia_scroll_target(self, bounds, target):
        """Resolve the visible custom-grid container when UIA exposes Scroll."""
        if target.get('type') != 'item_grid_scroll' or not self.is_window('sale'):
            return None
        from .uia_bridge import EngineError
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        try:
            element = self._uia_readback.locate(
                self, name='', kind=50018, window_kind='sale',
                automation_id='tabDetail')
            if 'Scroll' not in set(element.get('patterns') or ()):
                raise EngineError('PATTERN_UNSUPPORTED',
                                  'The current grid container has no Scroll pattern.')
            left, top = self._frame[0][:2]
            box = element['bounds']
            if bounds is not None:
                x, y, width, height = bounds
                target_center = (x + left + width / 2, y + top + height / 2)
                if not (box['x'] <= target_center[0] <= box['x'] + box['width']
                        and box['y'] <= target_center[1] <= box['y'] + box['height']):
                    raise EngineError('UIA_TARGET_MISMATCH',
                                      'The Scroll container does not cover the current grid.')
            control = ScreenControl(
                self, id=element.get('id') or '', name='Cuộn bảng hàng', kind=50018,
                bounds=(box['x'] - left, box['y'] - top, box['width'], box['height']),
                enabled=bool(element.get('enabled')), role='scroll')
            control.target = dict(target, uia=True, uia_element=element,
                                  uia_automation_id=element.get('id') or '')
            return control
        except Cancelled:
            raise
        except (EngineError, NeedsAttention, win32.WindowError):
            self._metrics['uia_scroll_fallbacks'] = self._metrics.get('uia_scroll_fallbacks', 0) + 1
            return None

    def _uia_sale_scroll_target_current(self, target):
        """Resolve sale-grid scrolling from current native state."""
        if target.get('type') != 'item_grid_scroll' or not self.is_window('sale'):
            return None
        from .uia_bridge import EngineError
        try:
            self._native_frame('sale', fresh=True, )
            if self._uia_readback is None:
                from .uia_readback import UiaReadback
                self._uia_readback = UiaReadback(self.cancel)
            tab = self._uia_readback.locate(
                self, name='1. Hàng tiền', kind=50019, window_kind='sale')
            if ('SelectionItem' not in set(tab.get('patterns') or ())
                    or not tab.get('selected')):
                return None
            grid = self._uia_readback.locate(
                self, name='', kind=50025, window_kind='sale',
                automation_id='grdDetail')
            box = grid.get('bounds') or {}
            left, top = self._frame[0][:2]
            values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
            if (not all(isinstance(value, (int, float)) and math.isfinite(value)
                        for value in values)
                    or values[2] <= 0 or values[3] <= 0):
                return None
            return self._uia_scroll_target(
                (values[0] - left, values[1] - top, values[2], values[3]), target)
        except Cancelled:
            raise
        except (EngineError, NeedsAttention, win32.WindowError):
            self._metrics['uia_scroll_fallbacks'] = self._metrics.get('uia_scroll_fallbacks', 0) + 1
            return None

    def _uia_catalog_scroll_target(self, bounds, target, parent):
        """Use a visible popup ListItem's ScrollItem pattern for virtualized lists."""
        if target.get('type') != 'catalog_scroll' or not self.is_window('new_item'):
            return None
        saved_parent = (parent.target or {}).get('uia_element') if parent is not None else None
        parent_token = saved_parent.get('token') if isinstance(saved_parent, dict) else None
        if not parent_token:
            return None
        from .uia_bridge import EngineError
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        try:
            # The popup exposes ScrollItem on its visible ListItems rather than
            # a Scroll pattern on the dropdown container.
            current_parent = self._uia_readback.bridge.call('read', {'token': parent_token})
            pbox = current_parent.get('bounds') or {}
            px, py, pw, ph = (pbox.get(key) for key in ('x', 'y', 'width', 'height'))
            popups = self._uia_readback.query(self, kinds=(50033,))
            popup_items = []
            for popup in popups:
                box = popup.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                if (popup.get('windowId') != self.window.hwnd
                        or popup.get('offscreen') or not popup.get('enabled')
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in values)
                        or values[2] <= 0 or values[3] <= 0
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in (px, py, pw, ph))
                        or abs((values[0] + values[2] / 2) - (px + pw / 2)) > max(32, pw * .5)
                        or values[1] < py + ph - 3):
                    continue
                for item in self._uia_readback.query(
                        self, kinds=(50007, 50029, 50008), parent_token=popup.get('token')):
                    ibox = item.get('bounds') or {}
                    ivals = tuple(ibox.get(key) for key in ('x', 'y', 'width', 'height'))
                    if (item.get('windowId') == self.window.hwnd
                            and not item.get('offscreen') and item.get('enabled')
                            and 'ScrollItem' in set(item.get('patterns') or ())
                            and all(isinstance(value, (int, float)) and math.isfinite(value)
                                    for value in ivals)
                            and ivals[2] > 0 and ivals[3] > 0):
                        popup_items.append(item)
            if popup_items:
                # ScrollIntoView on the first visible row is a no-op for a
                # virtualized popup. Target the lowest visible row so the
                # provider advances the list when more items are clipped.
                item = max(popup_items, key=lambda candidate: (
                    (candidate.get('bounds') or {}).get('y', 0)
                    + (candidate.get('bounds') or {}).get('height', 0),
                    (candidate.get('bounds') or {}).get('x', 0)))
                box = item['bounds']
                control = ScreenControl(
                    self, id=item.get('id') or '', name='Cuộn danh sách hiện tại',
                    kind=50007,
                    bounds=(box['x'] - self._frame[0][0], box['y'] - self._frame[0][1],
                            box['width'], box['height']),
                    enabled=bool(item.get('enabled')), role='scroll')
                control.target = dict(target, uia=True, uia_element=item,
                                      uia_automation_id=item.get('id') or '')
                return control
            # Some MISA combo popups omit the ValueListDropDown container
            # from the UIA query even though their visible ListItems remain
            # queryable.  Bind those rows by geometry to this combo instead
            # of abandoning the guarded scroll operation.
            visible_items = []
            for item in self._uia_readback.query(self, kinds=(50007, 50029, 50008)):
                box = item.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                if (item.get('windowId') != self.window.hwnd
                        or item.get('offscreen') or not item.get('enabled')
                        or 'ScrollItem' not in set(item.get('patterns') or ())
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in values)
                        or values[2] <= 0 or values[3] <= 0
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in (px, py, pw, ph))
                        or values[0] < px - 24 or values[0] + values[2] > px + pw + 80
                        or values[1] < py + ph - 3 or values[1] > py + ph + 240):
                    continue
                visible_items.append(item)
            if visible_items:
                item = max(visible_items, key=lambda candidate: (
                    (candidate.get('bounds') or {}).get('y', 0)
                    + (candidate.get('bounds') or {}).get('height', 0),
                    (candidate.get('bounds') or {}).get('x', 0)))
                box = item['bounds']
                control = ScreenControl(
                    self, id=item.get('id') or '', name='Cuộn danh sách hiện tại',
                    kind=50007,
                    bounds=(box['x'] - self._frame[0][0], box['y'] - self._frame[0][1],
                            box['width'], box['height']),
                    enabled=bool(item.get('enabled')), role='scroll')
                control.target = dict(target, uia=True, uia_element=item,
                                      uia_automation_id=item.get('id') or '')
                return control
            candidates = self._uia_readback.query(
                self, kinds=(50008, 50025, 50033, 50003), parent_token=parent_token)
            if not candidates:
                # A second MISA popup shape is not exposed below the combo
                # token, but its scroll provider is still a current-window
                # element.  Keep the same geometry guard when broadening the
                # query so unrelated controls cannot become scroll targets.
                candidates = self._uia_readback.query(
                    self, kinds=(50008, 50025, 50033, 50003))
            left, top = self._frame[0][:2]
            if bounds is not None:
                x, y, width, height = (float(value) for value in bounds)
                tx, ty, tw, th = x + left, y + top, width, height

            def matches(item):
                box = item.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                if (item.get('windowId') != self.window.hwnd
                        or item.get('offscreen') or not item.get('enabled')
                        or 'Scroll' not in set(item.get('patterns') or ())
                        or not all(isinstance(value, (int, float)) and math.isfinite(value)
                                   for value in values)):
                    return False
                ex, ey, ew, eh = values
                near_parent = (ex <= px + pw + 80 and ex + ew >= px - 24
                               and ey >= py + ph - 3 and ey <= py + ph + 240)
                return (near_parent and
                        (bounds is None or
                         (ex <= tx + 8 and ey <= ty + 8
                          and ex + ew >= tx + tw - 8 and ey + eh >= ty + th - 8)))

            found = [item for item in candidates if matches(item)]
            if len(found) != 1:
                return None
            item = found[0]
            box = item['bounds']
            control = ScreenControl(
                self, id=item.get('id') or '', name='Cuộn danh sách hiện tại',
                kind=int(item.get('kind') or 50025),
                bounds=(box['x'] - left, box['y'] - top, box['width'], box['height']),
                enabled=bool(item.get('enabled')), role='scroll')
            control.target = dict(target, uia=True, uia_element=item,
                                  uia_automation_id=item.get('id') or '')
            return control
        except Cancelled:
            raise
        except (EngineError, NeedsAttention, win32.WindowError):
            self._metrics['uia_scroll_fallbacks'] = self._metrics.get('uia_scroll_fallbacks', 0) + 1
            return None

    def _native_target(self, target):
        """Resolve an action only from a current native control identity."""
        kind = target.get('type')
        if kind == 'sales_add':
            self._native_frame('main', fresh=True)
            return self._uia_static_target(target)
        if kind in ('fixed_button', 'item_dialog_button', 'word', 'tab'):
            surface = target.get('window_kind') or next(
                (name for name in ('main', 'sale', 'find', 'new_item') if self.is_window(name)), None)
            if surface:
                self._native_frame(surface, fresh=True)
            return self._uia_static_target(dict(target, window_kind=surface))
        if kind == 'field':
            return self._uia_field_target((target.get('id'),), target.get('label', ''))
        if kind == 'sale_item_cell':
            return self._uia_sale_item_cell(target['row'], target['column'],
                                            code=target.get('code', ''), new=target.get('new', False),
                                            semantic_only=True)
        if kind == 'inventory_menu':
            return self._uia_inventory_menu_current(target['action'], getattr(self, '_active_item_row', 1))
        if kind == 'find_filter':
            return self._uia_find_filter(target['label'])
        if kind == 'arrow':
            parent = self._resolve_target(target['parent'])
            element = parent.target.get('uia_element') if parent.target else None
            if not isinstance(element, dict) or 'ExpandCollapse' not in set(element.get('patterns') or ()):
                return None
            control = ScreenControl(self, id=element.get('id') or parent.id, name='mũi tên',
                                    kind=int(element.get('kind') or 50003), bounds=parent.bounds,
                                    enabled=bool(element.get('enabled')), role='button')
            control.target = dict(target, uia=True, uia_element=element,
                                  uia_automation_id=element.get('id') or '')
            return control
        if kind == 'native_control':
            control_kind = int(target.get('kind') or 0)
            if not control_kind or not (target.get('id') or target.get('name')):
                raise NeedsAttention('Native target thieu control type va identity.')
            candidates = self.find_controls(
                kinds=(control_kind,),
                ids=(target['id'],) if target.get('id') else ())
            candidates = [control for control in candidates
                          if (not target.get('name') or control.name == target['name'])
                          and ((control.target['uia_element'].get('parentToken') or '')
                               == (target.get('parent_token') or ''))
                          and (target.get('id') or tuple(control.bounds) == tuple(target.get('bounds') or ()))]
            return self.unique(candidates, target.get('id') or target.get('name'))
        return None

    def _resolve_target(self, target):
        if self._locating:
            self.last_action.setdefault('resolution_path', []).append(deepcopy(target))
        kind = target['type']
        if kind == 'recovery' and self.recovery is not None:
            return self.recovery.resolve(target)
        if kind in ('sale_item_cell', 'inventory_menu', 'catalog_option', 'catalog_scroll',
                    'item_grid_scroll', 'item_dialog_button'):
            return self.resolve_inventory_target(target)
        if kind == 'sales_add':
            return self.sales_add_button()
        if kind == 'field':
            ids = (target['id'],) if target['id'] else ()
            return self.field(ids, target['label'])
        if kind == 'find_filter':
            if not self.is_window('find'):
                raise NeedsAttention('Ô lọc chỉ được nhập trong Tìm nhanh vật tư.')
            uia = self._uia_find_filter(target['label'])
            if uia is not None:
                return uia
            self.unique(self._find_words(target['label']), target['label'])
            return self.unique([c for c in self._find_grid_controls() if c.id == target['id']],
                               target['label'])
        if kind == 'grid':
            return self._grid(target['sale'])
        if kind == 'row':
            rows = None if target['sale'] else self._uia_find_rows()
            if rows is None:
                rows = self._rows(target['sale'])
            if target['sale']:
                rows = [r for r in rows if r.target['index'] == target['index']
                        and r.target['code'] == target['code']
                        and (r.target['add'] == target['add'] or (target['add'] and not target['code']))]
            else:
                rows = [r for r in rows if target['code'] and r.target['code'] == target['code']]
            return self.unique(rows, 'dòng hàng ' + (target['code'] or str(target['index'] + 1)))
        if kind in ('cell', 'find_checkbox'):
            row = self._resolve_target(target['row'])
            if kind == 'cell':
                if target['column'] not in row._cells:
                    raise NeedsAttention('Không scan được cột ' + target['column'])
                return row._cells[target['column']]
            return self.unique([c for c in self.controls(row) if c.role == 'find_checkbox'], 'Chọn hàng')
        if kind == 'arrow':
            parent = self._resolve_target(target['parent'])
            x, y, width, height = parent.bounds
            return ScreenControl(self, name='mũi tên', kind=50000, target=target,
                                 bounds=(max(x, x + width - 22), y, min(22, width), height))
        if kind == 'option':
            parent = self._resolve_target(target['parent'])
            left, top, width, height = parent.bounds
            choices = [c for c in self.find_controls(kinds=(50007,))
                       if text_key(c.name) == text_key(target['text'])
                       and left - 18 <= c.bounds[0] <= left + width + 18
                       and c.bounds[1] >= top + height]
            return self.unique(choices, target['text'])
        if kind == 'checkbox':
            surface = next((name for name in ('main', 'sale', 'find', 'new_item')
                            if self.is_window(name)), None)
            if surface:
                self._native_frame(surface, fresh=True)
            return self.unique(self.find_controls(ids=(target['id'],)), target['id'])
        if kind == 'radio':
            surface = next((name for name in ('main', 'sale', 'find', 'new_item')
                            if self.is_window(name)), None)
            if surface:
                self._native_frame(surface, fresh=True)
            return self.unique([c for c in self.find_controls(kinds=(50013,))
                                if normalize(c.name) == normalize(target['text'])], target['text'])
        if kind == 'tab':
            return self._tab_control(target['text'])
        if kind == 'word':
            surface = next((name for name in ('main', 'sale', 'find', 'new_item')
                            if self.is_window(name)), None)
            if surface:
                self._native_frame(surface, fresh=True)
            return self.unique(self._synthetic_named(target['text'], target['kind']), target['text'])
        raise NeedsAttention('Chưa có bộ định vị native cho ' + kind)

    @tracked('locate', lambda self, control: 'Định vị native: ' + (control.name or control.id))
    def _prepare_target(self, control):
        # A semantic recovery target may be unresolved here; resolve it below
        # and require its UIA identity before any input is sent.
        if (not getattr(self, 'direct_control', False)
                and not win32.is_foreground(self.window)):
            win32.activate(self.window)
        self._guard_frame()
        if control.window_id != self.window.hwnd:
            raise NeedsAttention('Native control thuoc cua so MISA khac.')
        # A semantic control can legitimately outlive one frame: focusing an
        # editor invalidates the frame before the write is bound. Preserve the
        # semantic target and re-resolve it against the current UIA/Win32
        # frame instead of rejecting the old token before resolution.
        stale_frame = (control.observed_frame_id != self._frame_id
                       or self._frame_window != (self.window.hwnd, self.window.pid))
        target = (deepcopy(control.target) if stale_frame
                  else self._target_for(control))
        if not target.get('type'):
            raise NeedsAttention('Native control thieu semantic target.')
        expected_window = (self.window.hwnd, self.window.pid)
        self.last_action = {'at': datetime.now(timezone.utc).isoformat(), 'step': self.checkpoint,
                            'target': target, 'status': 'scanning', 'input_sent': False,
                            'scan_attempts': []}
        for attempt in range(3):
            # Never attribute the previous frame's evidence to a failed capture.
            for key in ('window_hwnd', 'window_pid', 'capture_box', 'words', 'observed_at', 'frame_id',
                        'resolution_path', 'column_scans', 'row_scans', 'field_observations',
                        'scan_scope', 'scan_regions', 'bounds', 'value', 'error'):
                self.last_action.pop(key, None)
            try:
                # sales_add_button() already resolved this exact UIA element from
                # the current frame. Reusing it avoids a second full native frame
                # capture and UIA tree scan immediately before the click.
                reusable = (target.get('type') == 'sales_add'
                            and control.observed_frame_id == self._frame_id
                            and self._frame_window == expected_window
                            and isinstance((control.target or {}).get('uia_element'), dict)
                            and (control.target or {}).get('uia_element', {}).get('token'))
                coordinate_reuse = (target.get('coordinate_fallback')
                                    and target.get('frame_id') == self._frame_id)
                if reusable or coordinate_reuse:
                    fresh = control
                    self._metrics['target_reuses'] += 1
                else:
                    fresh = self._native_target(target)
                if fresh is not None:
                    scope = 'native'
                    self.last_action.update(window_hwnd=self.window.hwnd, window_pid=self.window.pid,
                        frame_id=self._frame_id, scan_scope=scope,
                        scan_regions=self._frame_context.get('scan_regions'),
                        capture_box=list(self._frame[0]),
                        observed_at=datetime.now(timezone.utc).isoformat(),
                        words=[{'text': w.text, 'box': list(w.box)}
                               for w in self._frame[3]])
                else:
                    kind = next((name for name in ('find', 'new_item', 'sale', 'main')
                                 if self.is_window(name)), None)
                    if target['type'] == 'sales_module' or target['type'] == 'recovery':
                        kind = 'main_navigation' if self.is_window('main') else None
                    field_ids = (target['id'],) if target['type'] == 'field' and target.get('id') else ()
                    tab = target.get('text', '') if target['type'] == 'tab' else ''
                    if kind == 'sale' and tab in ('Phiếu thu', 'Hóa đơn') and self.checkpoint in self.ADD_HEADER_STEPS:
                        kind = 'sale_tabs'
                    self._observe(kind=kind, force=True, field_ids=field_ids, tab=tab)
                    self.last_action.update(window_hwnd=self.window.hwnd, window_pid=self.window.pid,
                        frame_id=self._frame_id,
                        scan_scope=self._frame_context.get('scan_scope'),
                        scan_regions=self._frame_context.get('scan_regions'),
                        capture_box=list(self._frame[0]),
                        observed_at=datetime.now(timezone.utc).isoformat(),
                        words=[{'text': w.text, 'box': list(w.box)} for w in self._frame[3]])
                if (self.window.hwnd, self.window.pid) != expected_window:
                    raise NeedsAttention('Cửa sổ thao tác đã đổi; cần xác nhận checkpoint hiện tại.')
                # Dynamic targets are resolved from this one fresh observation.
                if fresh is None:
                    if target['type'] in {'fixed_button', 'sales_add', 'tab',
                                          'field', 'find_filter', 'item_dialog_button'}:
                        raise NeedsAttention('Control native chưa cung cấp UIA identity hiện tại.')
                    self._locating = True
                    fresh = self._resolve_target(target)
                self.guard(fresh)
                self._guard_frame()
                if not fresh.visible or not fresh.enabled:
                    raise NeedsAttention('Mục tiêu scan được chưa sẵn sàng: ' + control.name)
                if fresh.target.get('coordinate_fallback'):
                    self.last_action.update(status='located', attempt=attempt + 1,
                        bounds=list(fresh.bounds), value=fresh.name,
                        input_epoch=self.input_epoch, run_token=self.run_token,
                        field_observations=deepcopy(self._field_observations))
                    return fresh
                element = (fresh.target or {}).get('uia_element')
                if not isinstance(element, dict) or not element.get('token'):
                    raise NeedsAttention('Mục tiêu thiếu UIA token native hiện tại.')
                if (element.get('windowId') != self.window.hwnd
                        or element.get('offscreen') or not element.get('enabled')
                        or fresh.observed_frame_id != self._frame_id):
                    raise NeedsAttention('UIA target identity không khớp frame hiện tại.')
                # Fixed toolbar buttons already have their semantic name and
                # UIA token. Reading ``fresh.value`` here would trigger a full
                # native-word scan (including slow grid queries) for a value
                # that is not needed to authorize the click.
                observed_value = (fresh.name if target.get('type') in {
                    'sales_add', 'fixed_button', 'item_dialog_button', 'tab'
                } else fresh.value)
                self.last_action.update(status='located', attempt=attempt + 1,
                    bounds=list(fresh.bounds), value=observed_value,
                    input_epoch=self.input_epoch, run_token=self.run_token,
                    field_observations=deepcopy(self._field_observations))
                corroborated = fresh.target.get('uia_element') if fresh.target else None
                uia_automation_id = (fresh.target.get('uia_automation_id')
                                     if fresh.target else None)
                uia_row_parent = (fresh.target.get('uia_row_parent')
                                  if fresh.target else None)
                fresh.target = dict(target)
                if corroborated is not None:
                    fresh.target.update(uia=True, uia_element=corroborated)
                if uia_automation_id:
                    fresh.target['uia_automation_id'] = uia_automation_id
                if uia_row_parent:
                    fresh.target['uia_row_parent'] = uia_row_parent
                return fresh
            except Cancelled:
                raise
            except (NeedsAttention, win32.WindowError) as error:
                self.last_action.update(status='blocked', error=str(error), attempt=attempt + 1,
                                        field_observations=deepcopy(self._field_observations))
                self.last_action['scan_attempts'].append(deepcopy({
                    key: value for key, value in self.last_action.items() if key != 'scan_attempts'}))
                if attempt == 2 or (self.window.hwnd, self.window.pid) != expected_window:
                    self._remember_action()
                    raise NeedsAttention('Chưa scan được mục tiêu cho bước hiện tại: ' + str(error)) from error
            finally:
                self._locating = False
            # The next native capture is itself the readiness wait.
            # Do not add a blind delay after a failed current-frame lookup.

    def _remember_action(self):
        record('text_action', action=deepcopy(self.last_action))

    @tracked('input', lambda self, control, **kwargs: 'Bấm: ' + (control.name or control.id))
    def _click_resolved(self, control, *, inventory_menu=False, settle=0):
        if (not getattr(self, 'direct_control', False)
                and not win32.is_foreground(self.window)):
            win32.activate(self.window)
        self.guard(control)
        label = normalize(control.name)
        if (not getattr(self, 'direct_control', False)
                and not self.is_window('new_item') and control.kind in (50000, 50007)
                and (label.startswith(('cat', 'luu', 'ghiso')) or 'save' in control.id.lower())):
            raise NeedsAttention('Cất/ghi sổ chứng từ chỉ do người dùng thực hiện.')
        menu = control.target.get('type') in ('inventory_menu', 'catalog_option')
        if inventory_menu and not menu:
            raise NeedsAttention('Chưa scan được mục tiêu thuộc menu vật tư.')
        closing_before = None
        opening = None
        try:
            if self.ledger:
                if control.target.get('type') == 'sales_add':
                    opening = 'pending'
                    self.ledger.before_open(self, 'sale')
                elif control.target.get('type') == 'inventory_menu':
                    action = control.target.get('action')
                    if action in ('new', 'search'):
                        opening = 'pending'
                        self.ledger.before_open(self, 'new_item' if action == 'new' else 'find')
                if control.target.get('type') == 'recovery':
                    intent = control.target.get('intent')
                    if intent in ('close', 'discard', 'confirm_discard'):
                        closing_before = deepcopy(self.ledger.data['closing'])
                        if intent == 'close':
                            close_action = control.target.get('close_action')
                            if not close_action:
                                raise NeedsAttention('Thao tác đóng thiếu định danh phương thức input.')
                            self.ledger.data['closing'].update(
                                stage='requested', request_frame=self._frame_id,
                                close_action=close_action)
                        else:
                            self.ledger.data['closing'].update(
                                stage='discard_sent', request_frame=self._frame_id)
                        if intent != 'close':
                            self.ledger.data['closing']['discard_dialog'] = dict(
                                hwnd=self.window.hwnd, pid=self.window.pid, intent=intent,
                                text=control.name, frame_id=self._frame_id,
                                captured_at=self._captured_at.isoformat())
                        self.ledger.save()
            # Ledger persistence may take time; validate the frame again at the input boundary.
            self.guard(control)
            self._guard_frame()
            ui_target = control.target.get('type')
            uia_strategy = None
            if control.target.get('coordinate_fallback'):
                frame_ok = control.target.get('frame_id') == self._frame_id
                add_button_ok = (ui_target == 'sales_add' and self.is_window('main')
                                 and self.checkpoint in ('add.open', 'restart.ready', 'add.prepare'))
                tab_ok = (ui_target == 'tab' and (self.is_window('sale') or self.is_window('new_item'))
                          and self.checkpoint.startswith('add.'))
                field_ok = (ui_target == 'field' and self.is_window('sale')
                            and self.checkpoint.startswith('add.')
                            and control.target.get('id') in self.ADD_HEADER_FIELDS)
                recovery_ok = (ui_target == 'recovery' and self.checkpoint == 'restart.discard'
                               and control.target.get('intent') in ('discard', 'confirm_discard'))
                if not frame_ok or not (add_button_ok or tab_ok or field_ok or recovery_ok):
                    raise NeedsAttention('Fallback native hien tai khong con khop frame MISA.')
                native_button_hwnd = control.target.get('native_button_hwnd')
                if (native_button_hwnd and ui_target == 'recovery'
                        and control.target.get('intent') in ('discard', 'confirm_discard')):
                    win32.click_dialog_button(self.window, native_button_hwnd, control.name)
                    uia_strategy = 'Win32.BM_CLICK'
                else:
                    win32.direct_click(self.window, control.target['coordinate'],
                                       coordinate_space=control.target.get('coordinate_space', 'window'))
                    uia_strategy = 'Win32.direct_click'
            if not control.target.get('coordinate_fallback') and ui_target in ('sales_add', 'fixed_button', 'word', 'tab', 'item_dialog_button',
                             'catalog_option', 'find_checkbox', 'inventory_menu', 'arrow',
                             'sale_item_cell', 'field', 'find_filter', 'native_control', 'recovery'):
                from .uia_bridge import EngineError
                if self._uia_readback is None:
                    from .uia_readback import UiaReadback
                    self._uia_readback = UiaReadback(self.cancel)
                element = control.target.get('uia_element') or {}
                action = ('focus' if ui_target in ('field', 'find_filter')
                          or (ui_target == 'sale_item_cell'
                              and control.target.get('column') != 'mahang')
                          else 'select' if (ui_target == 'catalog_option'
                                             and 'SelectionItem' in set(element.get('patterns') or ()))
                          else 'click' if ui_target == 'catalog_option'
                          else 'expand' if (ui_target == 'arrow'
                              and 'ExpandCollapse' in set(element.get('patterns') or ()))
                          else 'expand' if (ui_target == 'sale_item_cell'
                              and control.target.get('column') == 'mahang'
                              and 'ExpandCollapse' in set(element.get('patterns') or ()))
                          else 'click')
                try:
                    uia_strategy = self._uia_readback.invoke(self, control, action=action)
                except Cancelled:
                    raise
                except EngineError as error:
                    if error.uncertain:
                        self._input_sent(surface_unchanged=False)
                        self.last_action.update(status='uncertain', input_sent=None,
                                                input_method='UIA3.' + action, error=str(error))
                        raise NeedsAttention(str(error)) from error
                    optional = error.code in {
                        'ENGINE_MISSING', 'ENGINE_VERSION', 'ENGINE_UNAVAILABLE', 'ENGINE_CLOSED',
                        'NOT_FOUND', 'AMBIGUOUS_ELEMENT', 'PATTERN_UNSUPPORTED',
                        'ELEMENT_UNAVAILABLE', 'UIA_TARGET_MISMATCH', 'STALE_ACTION_TARGET',
                        'UIA_TARGET_UNAVAILABLE', 'UIA_TARGET_AMBIGUOUS',
                    }
                    if not optional:
                        raise NeedsAttention(str(error)) from error
                    self.last_action['uia_action_fallback'] = error.code + ': ' + str(error)
            if uia_strategy is None:
                raise NeedsAttention('UIA không cung cấp pattern cho thao tác hiện tại.')
        except (win32.WindowError, NeedsAttention, Cancelled, OSError) as error:
            uncertain = (isinstance(error, win32.InputUncertain)
                         or bool(getattr(getattr(error, '__cause__', None), 'uncertain', False)))
            if uncertain:
                self._input_sent()
            else:
                if opening:
                    self.ledger.data[opening] = None
                if closing_before is not None:
                    self.ledger.data['closing'] = closing_before
                if opening or closing_before is not None:
                    self.ledger.save()
            self.last_action.update(status='uncertain' if uncertain else 'blocked',
                                    input_sent=None if uncertain else False, error=str(error))
            self._remember_action()
            self._invalidate()
            if isinstance(error, Cancelled):
                raise
            raise NeedsAttention(str(error)) from error
        target_type = control.target.get('type')
        self._input_sent(surface_unchanged=target_type not in {
            'tab', 'inventory_menu', 'item_dialog_button', 'catalog_option', 'sales_add'})
        if control.target.get('type') == 'tab':
            self._field_scan_hints.clear()
        self._metrics['input_clicks'] += 1
        self.last_action.update(status='clicked', input_sent=True,
                                input_method=uia_strategy or 'Win32.SendInput')
        self._remember_action()
        self._invalidate()
        if settle > 0:
            self.pause(settle)

    @tracked('action', lambda self, control, **kwargs: 'Bấm: ' + (control.name or control.id))
    def click(self, control, *, inventory_menu=False):
        fresh = self._prepare_target(control)
        self._click_resolved(fresh, inventory_menu=inventory_menu)
        return fresh

    @tracked('wait', 'Chờ giao diện MISA phản hồi')
    def pause(self, seconds=.08):
        self._check()
        if self.cancel.wait(seconds):
            self._check()

    @tracked('input', lambda self, vk, **kwargs: 'Gửi phím ' + ('Ctrl+' if kwargs.get('ctrl') else '')
             + {0x0D: 'Enter', 0x09: 'Tab', 0x1B: 'Esc', 0x41: 'A'}.get(vk, hex(vk)))
    def key(self, vk, *, ctrl=False, settle=0, focus_guard=None):
        allowed = {0x0D, 0x09, 0x1B, 0x28, 0x26, 0x22, 0x21, 0x24, 0x23,
                   0x72, 0x78, 0x20, 0x41}
        shortcut_allowed = (self.is_window('new_item')
                            or (self.is_window('sale')
                                and self.checkpoint.startswith('add.items.')))
        if (vk not in allowed or (ctrl and vk != 0x41)
                or (vk in (0x72, 0x78) and not shortcut_allowed)):
            raise NeedsAttention('Phím không thuộc luồng nhập bán hàng.')
        self.guard()
        try:
            win32.keypress(self.window, vk, ctrl=ctrl, focus_guard=focus_guard)
        except win32.WindowError as error:
            if isinstance(error, win32.InputUncertain):
                self._input_sent()
            self._invalidate()
            raise NeedsAttention(str(error)) from error
        self._input_sent()
        self._metrics['input_keys'] += 1
        self._invalidate()
        if settle > 0:
            self.pause(settle)

    def _require_input_focus(self, control, capture_box, *, label=None):
        """Wait briefly for the native editor created by the guarded click."""
        latest = {}

        def observe():
            try:
                self.guard(control)
                value = win32.input_focus(self.window, control.bounds, capture_box)
            except Cancelled:
                raise
            except NeedsAttention as error:
                value = dict(matched=False, reason=str(error))
            except win32.WindowError as error:
                value = dict(matched=False, reason=str(error))
            latest.clear()
            latest.update(value)
            self.last_action['input_focus'] = dict(value)
            return value

        try:
            return read_until(observe, lambda value: bool(value and value.get('matched')),
                              self.cancel, timeout=2.0, interval=.016,
                              label=label or control.name or control.id or 'ô nhập')
        except Cancelled:
            raise
        except NeedsAttention as error:
            reason = latest.get('reason') or str(error)
            self.last_action.update(status='blocked', focus_error=reason)
            self._remember_action()
            raise NeedsAttention('Ô ' + (label or control.name or control.id or 'nhập')
                                 + ' chưa nhận bàn phím; ' + reason) from error

    def focus(self, control):
        self.click(control)

    def close_document_key(self, entry):
        if self.recovery is None or self.checkpoint != 'restart.close':
            raise NeedsAttention('Đóng tab cần ngữ cảnh phục hồi đã được cho phép.')
        if entry['kind'] not in ('sale', 'find', 'new_item') or entry['hwnd'] != self.window.hwnd:
            raise NeedsAttention('Chỉ đóng cửa sổ phụ của lượt chạy; giữ nguyên trang chính MISA.')
        self._observe(kind='main_navigation' if self.is_window('main') else None, force=True)
        self.recovery.require_owned()
        self.guard()
        self._guard_frame()
        self.last_action = dict(target={'type': 'close_document', 'entry': deepcopy(entry)},
            frame_id=self._frame_id, input_epoch=self.input_epoch, run_token=self.run_token,
            window_hwnd=self.window.hwnd, window_pid=self.pid, at=utc_now().isoformat(),
            status='requested', input_sent=None, key='Ctrl+F4')
        self._remember_action()
        self.ledger.data['closing'].update(stage='requested', request_frame=self._frame_id,
                                           close_action='ctrl_f4')
        self.ledger.save()
        try:
            self.guard()
            self._guard_frame()
            win32.keypress(self.window, 0x73, ctrl=True)
            self._input_sent()
            self.last_action.update(status='sent', input_sent=True)
        except (win32.WindowError, NeedsAttention, Cancelled) as error:
            uncertain = isinstance(error, win32.InputUncertain)
            if uncertain:
                self._input_sent()
            else:
                self.ledger.data['closing']['stage'] = 'planned'
                self.ledger.data['closing'].pop('close_action', None)
                self.ledger.save()
            self.last_action.update(status='uncertain' if uncertain else 'blocked',
                                    input_sent=None if uncertain else False, error=str(error))
            raise
        finally:
            self._remember_action()
            self._invalidate()

    def _input_value_rule(self, control, readback_rule=None):
        if readback_rule:
            return readback_rule
        if control.role == 'field':
            return (self.HEADER_VALUE_RULES.get(control.id)
                    or self.INVENTORY_VALUE_RULES.get(control.id, 'exact'))
        if control.target.get('column') == 'thuesuatgtgt':
            return 'tax_rate'
        return 'decimal' if control.target.get('column') in (
            'soluong', 'thanhtien', 'dongia', 'tienthuegtgt') else 'exact'

    @staticmethod
    def _uia_value_purpose(control):
        if control.role == 'field' and control.id in (
                'cboAccountObjectID', 'txtInventoryItemCode', 'txtInventoryItemName',
                'txtInvoiceNo'):
            return 'field:' + control.id
        if (control.target.get('type') == 'sale_item_cell'
                and control.target.get('column') in ('soluong', 'thanhtien',
                                                      'thuesuatgtgt')):
            return 'sale_item_cell:' + control.target['column']
        if control.role == 'find_filter':
            return 'find_filter:' + (control.name or control.id)
        return None

    def _existing_value_matches(self, control, text, rule):
        """Use the same evidence policy before and after a scalar replacement."""
        from .verification import verify
        self._guard_frame()
        row = control.target.get('row')
        field = control.id or control.target.get('column') or control.name
        readings = self.read_evidence(control, document=self.run_token, row=row, field=field)
        result = verify(text, readings, rule=rule if text else 'empty',
                        hwnd=self.window.hwnd, pid=self.window.pid, document=self.run_token,
                        row=row, field=field, checkpoint=self.checkpoint, run_token=self.run_token,
                        input_epoch=self.input_epoch, not_before=self.last_input_at)
        self._guard_frame()
        self.last_action['before_write'] = result.model_dump(mode='json')
        self.last_action['field_observations'] = deepcopy(self._field_observations)
        return result.status == 'matched'

    def _type_date_value(self, control, text, capture_box):
        """Replace a masked date through its scalar pattern, never its caret segment."""
        from .uia_bridge import EngineError
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        before_write = self.last_action.get('before_write')
        self._click_resolved(control)
        self._require_input_focus(control, capture_box, label=control.name)
        original_bounds = control.bounds
        try:
            # Clicking invalidates the frame; bind the write to a fresh native frame.
            self._native_frame('sale', fresh=True)
            control = self._prepare_target(control)
        finally:
            self.last_action.update(before_write=before_write, input_sent=True)
        if self._frame_window != (self.window.hwnd, self.window.pid):
            raise NeedsAttention('Cửa sổ MISA đổi tiến trình sau khi nhận bàn phím.')
        try:
            result = self._uia_readback.write_date(self, control, text)
        except (EngineError, Cancelled) as error:
            uncertain = isinstance(error, Cancelled) or error.uncertain
            if uncertain:
                self._input_sent()
            self.last_action.update(status='uncertain' if uncertain else 'blocked',
                                    input_method='UIA3.Value.Date',
                                    date_input_may_have_been_sent=uncertain, error=str(error))
            raise
        finally:
            self._invalidate()
        self._input_sent()
        self._metrics['input_texts'] += 1
        self.last_action.update(status='typed', typed_value=text,
                                input_method=result['strategyUsed'], input_sent=True,
                                date_input_may_have_been_sent=True)
        return control, capture_box

    def _type_value(self, control, text, *, readback_rule=None):
        """Send text once; this does not verify the value accepted by MISA."""
        text = scalar_text(text)
        if not getattr(self, 'direct_control', False) and control.id in self.OBSERVED_SALE_READONLY_IDS:
            raise NeedsAttention('Ô sale quan sát chỉ đọc; không được ghi dữ liệu vào trường này.')
        if (not getattr(self, 'direct_control', False)
                and (control.id == 'txtJournalMemo' or normalize(control.name) in ('lydonop', 'lydoxuat'))):
            raise NeedsAttention('Không được ghi đè Lý do nộp/xuất do MISA sinh.')
        rule = self._input_value_rule(control, readback_rule)
        normalized(text, rule if text else 'empty')
        control = self._prepare_target(control)
        capture_box = self._frame[0]
        if rule == 'date' and self._existing_value_matches(control, text, rule):
            self.last_action['status'] = 'already_matches'
            self.last_action['value'] = self.last_action['before_write']['actual_raw']
            self._remember_action()
            return control, capture_box
        if rule == 'date':
            return self._type_date_value(control, text, capture_box)
        # Coordinate-bound fallbacks intentionally bypass the UIA bridge;
        # another automation engine may own the provider while Win32 input is
        # still safe on this guarded field frame.
        purpose = (None if control.target.get('coordinate_fallback')
                   else self._uia_value_purpose(control))
        if purpose is not None:
            from .uia_bridge import EngineError
            if self._uia_readback is None:
                from .uia_readback import UiaReadback
                self._uia_readback = UiaReadback(self.cancel)
            try:
                strategy = self._uia_readback.write_value(self, control, text, purpose)
            except Cancelled:
                raise
            except EngineError as error:
                if error.uncertain:
                    self._input_sent()
                    self.last_action.update(status='uncertain', input_sent=None,
                                            input_method='UIA3.Value.Observed', error=str(error))
                    self._invalidate()
                    raise
                optional = error.code in {
                    'ENGINE_MISSING', 'ENGINE_VERSION', 'ENGINE_UNAVAILABLE', 'ENGINE_CLOSED',
                    'PATTERN_UNSUPPORTED', 'VALUE_TARGET_UNAVAILABLE', 'ELEMENT_UNAVAILABLE',
                    'FOCUS_FAILED',
                }
                if not optional:
                    self.last_action.update(status='blocked', input_sent=False,
                                            input_method='UIA3.Value.Observed', error=str(error))
                    self._invalidate()
                    raise
                self._metrics['uia_value_fallbacks'] += 1
                self.last_action['uia_value_fallback'] = error.code + ': ' + str(error)
            else:
                self._input_sent()
                self._metrics['input_texts'] += 1
                self._metrics['uia_value_sets'] += 1
                self.last_action.update(status='typed', typed_value=str(text),
                                        input_method=strategy, input_sent=True)
                self._invalidate()
                return control, capture_box
        self._click_resolved(control)
        focus_guard = lambda: self._require_input_focus(control, capture_box,
                                                         label=control.name)
        self._require_input_focus(control, capture_box, label=control.name)
        try:
            # Text fields receive a whole scalar; date editors use their value pattern above.
            with win32.prepared_clipboard_text(text) as sequence:
                if text:
                    # Keep the foreground/focus guard at the boundary, then
                    # submit Ctrl+A/Ctrl+V as one SendInput batch.
                    win32.replace_text(self.window, sequence, focus_guard=focus_guard)
                else:
                    self.key(0x2E, settle=0, focus_guard=focus_guard)
        except win32.WindowError as error:
            if isinstance(error, win32.InputUncertain):
                self._input_sent()
            self._invalidate()
            raise NeedsAttention(str(error)) from error
        self._input_sent()
        self._metrics['input_texts'] += 1
        self.last_action.update(status='typed', typed_value=str(text),
                                input_method='clipboard' if text else 'delete')
        self._invalidate()
        return control, capture_box

    @tracked('action', lambda self, control, text, **kwargs: 'Nhập và xác nhận ô: ' + (control.name or control.id))
    def enter_field(self, control, text, *, commit):
        """Commit a header edit once; AddSale verifies fresh values afterwards."""
        allowed = {
            'cboAccountObjectID': ('add.customer', 0x0D),
            'dtePostedDate': ('add.posted_date', 0x09),
            'dteRefDate': ('add.voucher_date', 0x09),
            'txtInvoiceNo': ('add.invoice', 0x09),
        }
        if (not self.is_window('sale') or control.role != 'field'
                or allowed.get(control.id) != (self.checkpoint, commit)):
            raise NeedsAttention('Ô hoặc phím xác nhận không thuộc bước Thêm hiện tại.')
        try:
            control, capture_box = self._type_value(control, text)
            if (self.HEADER_VALUE_RULES.get(control.id) == 'date'
                    and self.last_action.get('before_write', {}).get('status') == 'matched'):
                return
            if self.last_action.get('status') == 'already_matches':
                self._click_resolved(control)
            self.sync(kind='sale')
            self.guard(control)
            # Read after Enter/Tab: the active editor can expose a caret as
            # The native text has not yet committed the customer selection.
            self.key(commit, focus_guard=lambda: self._require_input_focus(
                control, capture_box, label=control.name))
            self.last_action.update(status='committed_unverified', commit_key=commit,
                                    committed_at=self.last_input_at.isoformat(),
                                    input_epoch=self.input_epoch)
        finally:
            self._remember_action()
            self._invalidate()

    @tracked('action', lambda self, control, text, **kwargs: 'Nhập và đọc lại: ' + (control.name or control.id))
    def set(self, control, text, *, readback_rule=None):
        text = scalar_text(text)
        control, capture_box = self._type_value(control, text, readback_rule=readback_rule)
        if self.last_action.get('status') == 'already_matches':
            return self.last_action['value']
        try:
            rule = self._input_value_rule(control, readback_rule)
            def accepted(actual):
                if actual is None:
                    return False
                try:
                    return normalized(actual, rule) == normalized(str(text), rule)
                except (ValueError, NeedsAttention):
                    return False
            return read_until(lambda: self._read_input_value(control, capture_box),
                              accepted, self.cancel,
                              timeout=8, label=control.name)
        finally:
            self._remember_action()
            self._invalidate()

    def sales_add_button(self):
        """Scope Thêm to the sales list again immediately before the click."""
        if self.checkpoint != 'add.open' and not getattr(self, 'direct_control', False):
            raise NeedsAttention('Thêm chỉ được bấm ở bước mở chứng từ mới.')
        return self.sales_list_add_button()

    def sales_list_add_button(self):
        """Resolve the fixed Thêm button from one fresh native frame.

        The user selects the Bán hàng page.  Native UIA/Win32 evidence locates
        and qualifies this fixed toolbar control; window/checkpoint/hit-test
        guards remain mandatory at the input boundary.
        """
        self.sync()
        if not self.is_window('main') or not win32.user32.IsWindowEnabled(self.window.hwnd):
            raise NeedsAttention('Danh sách Bán hàng chưa sẵn sàng; đang có cửa sổ MISA khác mở.')
        main = self.ledger.data.get('main') if self.ledger else None
        if main and self.window.hwnd != main['hwnd']:
            raise NeedsAttention('Trang chính MISA đã thay đổi; chưa được mở chứng từ mới.')
        self._native_frame('main', )
        try:
            uia = self._uia_static_target({'type': 'sales_add'})
        except NeedsAttention:
            uia = None
        if uia is not None:
            uia.target = dict(uia.target, type='sales_add', uia=True)
            return uia
        # Some MISA builds expose the toolbar button only through Win32 while
        # the UIA provider is busy or omits the toolbar tree. The coordinate is
        # bound to this fresh main-window frame and is used only for this fixed
        # toolbar target.
        width, height = self._frame[1]
        point = (min(215, max(120, round(width * .12))), 112)
        return ScreenControl(self, id='mnuAdd', name='Thêm', kind=50000,
                             bounds=(point[0] - 18, point[1] - 18, 36, 36),
                             enabled=True, role='button',
                             target={'type': 'sales_add', 'coordinate_fallback': True,
                                     'coordinate': list(point), 'coordinate_space': 'window',
                                     'frame_id': self._frame_id})

    def _tab_control(self, name):
        allowed = self.TAB_LABELS if self.is_window('sale') else ('1. Ngầm định',)
        if name not in allowed:
            raise NeedsAttention('Tab không thuộc luồng chứng từ: ' + name)
        kind = 'sale_tabs' if self.is_window('sale') else 'new_item'
        words = self._label_pool(name, self._words(kind=kind))
        candidates = (tab_candidates(name, words, self.TAB_LABELS, aliases=self._aliases(name))
                      if self.is_window('sale') else self._find_words(name, words, scoped=True))
        word = self._label(name, candidates)
        if word is None and self.is_window('sale') and self._frame is not None:
            # Header tab locations are fixed in this voucher layout. MISA
            # sometimes paints their captions without exposing a UIA element.
            width, height = self._frame[1]
            points = {'Phiếu thu': (.050, .198),
                      'Phiếu xuất': (.125, .198),
                      'Hóa đơn': (.190, .198)}
            point = points.get(name)
            if point:
                x, y = round(width * point[0]), round(height * point[1])
                tab = ScreenControl(
                    self, id='', name=name, kind=50019,
                    bounds=(max(0, x - 40), max(0, y - 14), 80, 28),
                    enabled=True, role='tab',
                    target={'type': 'tab', 'text': name,
                            'coordinate_fallback': True,
                            'coordinate': [x, y],
                            'coordinate_space': 'window',
                            'frame_id': self._frame_id})
            else:
                tab = self._control_for_word(word, kind=50019, name=name)
        else:
            tab = self._control_for_word(word, kind=50019, name=name)
        semantic = self._uia_tab_state(name)
        tab.selected = bool(semantic and semantic.get('selected'))
        tab.target = dict(tab.target, type='tab', text=name)
        return tab

    def tab(self, name):
        return self.ensure_tab(name)

    def _tab_content_visible(self, name, words):
        """Confirm the tab's own content, including local caption rereads."""
        evidence = {
            'Phiếu thu': ('Số phiếu thu', 'Khách hàng', 'Lý do nộp'),
            'Hóa đơn': ('Số hóa đơn', 'Ký hiệu HĐ', 'Ngày hóa đơn'),
            '1. Hàng tiền': ('Mã hàng', 'Tên hàng', 'Số lượng', 'Đơn giá', 'Thành tiền'),
            '2. Thuế': ('Diễn giải thuế', 'Tiền thuế GTGT', '% thuế GTGT'),
        }.get(name, ())
        found = {}
        try:
            native_captions = {normalize(value) for value in win32.child_window_texts(self.window)}
        except (win32.WindowError, OSError):
            native_captions = set()
        for label in evidence:
            pool = self._label_pool(label, words)
            if name in ('1. Hàng tiền', '2. Thuế'):
                # Footer totals also exist on the other tab.
                height = self._frame[1][1] if self._frame else 0
                pool = [word for word in words if 70 <= word.cy < height - 110]
            word, _ = self._observe_label(label, pool, scoped=True, purpose='tab_content')
            if word is not None or normalize(label) in native_captions:
                found[label] = word
        if name == 'Phiếu thu':
            return 'Số phiếu thu' in found and bool({'Khách hàng', 'Lý do nộp'}.intersection(found))
        if name == 'Hóa đơn':
            return len(found) >= 2
        if name == '1. Hàng tiền':
            return (bool({'Mã hàng', 'Tên hàng'}.intersection(found))
                    and bool({'Số lượng', 'Đơn giá', 'Thành tiền'}.intersection(found)))
        if name == '2. Thuế':
            return bool({'Diễn giải thuế', '% thuế GTGT'}.intersection(found))
        return False

    def _wait_tab(self, name, *, selected_only=False):
        """Re-evaluate caption and content on each fresh frame; never send input."""
        surface_kind = 'sale' if self.is_window('sale') else 'new_item'
        self.sync(kind=surface_kind)
        expected_window = (self.window.hwnd, self.window.pid)
        attempts = []
        latest = {}

        def observe():
            nonlocal latest
            latest = {'at': datetime.now(timezone.utc).isoformat(), 'step': self.checkpoint,
                      'target': {'type': 'tab', 'text': name}, 'attempt': len(attempts) + 1,
                      'phase': 'observe_selected' if selected_only else 'observe_ready',
                      'status': 'scanning', 'input_sent': False}
            try:
                # A current UIA TabItem with SelectionItem is authoritative for
                # Active state after a semantic tab click comes from UIA SelectionItem.
                if selected_only:
                    self._native_frame(surface_kind, fresh=True, )
                    semantic = self._uia_tab_state(name)
                    if semantic is not None:
                        active = bool(semantic.get('selected'))
                        latest.update(window_hwnd=self.window.hwnd, window_pid=self.window.pid,
                                      frame_id=self._frame_id, capture_box=list(self._frame[0]),
                                      observed_at=self._captured_at.isoformat(),
                                      source='UIA3.SelectionItem', selected=active,
                                      content_visible='uia_selection')
                        self.guard()
                        self._guard_frame()
                        if active:
                            latest['status'] = 'selected'
                            return semantic, True
                # A titled modeless HWND can still show the journal behind it.
                # Pin one new observation for both caption and content checks.
                kind = ('sale_tabs' if name in ('Phiếu thu', 'Hóa đơn')
                        and self.checkpoint in self.ADD_HEADER_STEPS else surface_kind)
                with self.read_scope(kind, tab=name):
                    self.guard()
                    if (self.window.hwnd, self.window.pid) != expected_window:
                        raise NeedsAttention('Cửa sổ đã đổi khi chờ tab ' + name)
                    words = self._frame[3]
                    latest.update(window_hwnd=self.window.hwnd, window_pid=self.window.pid,
                                  frame_id=self._frame_id, capture_box=list(self._frame[0]),
                                  observed_at=self._captured_at.isoformat(),
                                  words=[{'text': w.text, 'box': list(w.box)} for w in words])
                    tab = None
                    try:
                        tab = self._tab_control(name)
                    except Cancelled:
                        raise
                    except NeedsAttention as error:
                        latest['caption_error'] = str(error)
                    content_visible = self._tab_content_visible(name, words)
                    supported_content = (surface_kind == 'sale'
                                         and name in ('Phiếu thu', 'Hóa đơn', '1. Hàng tiền', '2. Thuế'))
                    # Grid captions can remain visible while MISA is still on a
                    # sibling tab (for example, Hóa đơn leaves the Hàng tiền
                    # grid painted below it).  Content alone therefore cannot
                    # authorize a tab transition or any fixed-layout input.
                    observed_tabs = set(filter(None,
                        self._frame_context.get('observed_tab', '').split('|')))
                    coordinate_selected = bool(
                        tab and tab.target.get('coordinate_fallback')
                        and content_visible)
                    active = ((bool(tab and tab.selected) and name in observed_tabs)
                              or coordinate_selected)
                    active = active and (content_visible if supported_content else True)
                    latest['content_visible'] = content_visible
                    latest['observed_tabs'] = sorted(observed_tabs)
                    if tab is not None:
                        latest.update(bounds=list(tab.bounds), value=tab.value, selected=tab.selected)
                    self.guard()
                    self._guard_frame()
                    if active and (name in set(filter(None,
                            self._frame_context.get('observed_tab', '').split('|')))
                                   or tab.target.get('coordinate_fallback')):
                        latest['status'] = 'selected'
                        return (tab, True)
                    ready = active or (tab is not None and not tab.selected and not selected_only)
                    latest['status'] = 'located' if ready else 'waiting'
                    return (tab, active) if ready else None
            except Cancelled as error:
                latest.update(status='cancelled', error=str(error))
                raise
            except NeedsAttention as error:
                latest.update(status='waiting', error=str(error))
                # Only retry observation while the same foreground form is still owned.
                self.sync(kind=surface_kind)
                self.guard()
                if (self.window.hwnd, self.window.pid) != expected_window:
                    raise NeedsAttention('Cửa sổ đã đổi khi chờ tab ' + name) from error
                return None
            except Exception as error:
                latest.update(status='blocked', error=str(error))
                raise
            finally:
                attempts.append(deepcopy(latest))
                record('tab_readiness', observation=deepcopy(latest))

        try:
            return read_until(observe, bool, self.cancel, timeout=20, label='tab ' + name)
        except Exception as error:
            # Preserve the failing tab observation, including native diagnostics.
            last_read_error = latest.get('error') or latest.get('caption_error')
            self.last_action = dict(latest, target={'type': 'tab', 'text': name},
                                    step=self.checkpoint, input_sent=False,
                                    status='cancelled' if isinstance(error, Cancelled) else 'blocked',
                                    error=str(error), last_read_error=last_read_error, scan_attempts=attempts)
            self._remember_action()
            if isinstance(error, NeedsAttention) and not isinstance(error, Cancelled) and last_read_error:
                raise NeedsAttention(str(error) + '; lần đọc tab cuối: ' + last_read_error) from error
            raise

    def ensure_tab(self, name):
        self._field_scan_hints.clear()
        if self._frame is not None:
            # A successful tab observation is enough until the next input or
            # checkpoint/window change. Do not click the already active tab,
            # even when the caller has not yet populated _selected_tabs.
            try:
                self._guard_frame()
                semantic = self._uia_tab_state(name)
                if semantic is not None and semantic.get('selected'):
                    self._selected_tabs = {name}
                    return
                observed = set(filter(None, self._frame_context.get('observed_tab', '').split('|')))
                if name in observed:
                    self._selected_tabs = {name}
                    return
            except NeedsAttention:
                pass

        if (not self._locating and not self._mapping
                and (self.is_window('sale') or self.is_window('new_item'))):
            self._native_frame('sale' if self.is_window('sale') else 'new_item',
                               fresh=True)
            self.last_action = {
                'at': datetime.now(timezone.utc).isoformat(),
                'step': self.checkpoint,
                'target': {'type': 'tab', 'text': name},
                'status': 'locating', 'input_sent': False,
            }
            target = None
            for attempt in range(3):
                if attempt:
                    self._native_frame('sale' if self.is_window('sale') else 'new_item',
                                       fresh=True)
                target = self._uia_static_target({'type': 'tab', 'text': name})
                if target is None:
                    # Keep the current native caption as the source of a
                    # guarded Win32 fallback when UIA omits TabItem identity.
                    try:
                        target = self._tab_control(name)
                    except (Cancelled, NeedsAttention):
                        target = None
                if target is not None:
                    break
                self.pause(.08)
            if target is None:
                raise NeedsAttention('UIA không cung cấp tab native hiện tại.')
            self.last_action['status'] = 'uia_select'
            self._click_resolved(target)
            self._wait_tab(name, selected_only=True)
            self._selected_tabs = {name}
            return

        raise NeedsAttention('Không thể định vị tab cố định bằng map native hiện tại.')

    def wait_window(self, kind, seconds=5):
        started = time.monotonic()
        latest = {'wait_id': uuid.uuid4().hex, 'checkpoint': self.checkpoint,
                  'target_kind': kind, 'timeout_seconds': seconds, 'expected_pid': self.pid,
                  'attempts': 0, 'phase': 'window_lookup', 'candidate_status': 'not_observed',
                  'last_candidate': None, 'last_candidate_attempt': None,
                  'last_window_error': '', 'last_window_error_attempt': None,
                  'frame_id': '', 'captured_at': None}
        recorded_state = None

        def report(status='waiting', error=''):
            nonlocal recorded_state
            try:
                foreground = win32.foreground_identity()
            except Cancelled:
                raise
            except Exception as foreground_error:
                foreground = {'error': f'{type(foreground_error).__name__}: {foreground_error}'}
            latest.update(status=status, error=error, foreground=foreground,
                          elapsed_ms=round((time.monotonic() - started) * 1000, 2))
            # Repeated polling only updates the summary; log actual state changes.
            state = {key: latest[key] for key in ('phase', 'candidate_status', 'last_candidate',
                                                 'foreground', 'last_window_error')}
            if status != 'waiting' or state != recorded_state:
                record('window_wait', observation=deepcopy(latest))
                recorded_state = deepcopy(state)

        def observe():
            self._check()
            latest['attempts'] += 1
            latest['phase'] = 'window_lookup'
            try:
                candidate = win32.recover_window()
            except win32.WindowError as error:
                latest.update(candidate_status='selection_error',
                              last_window_error=f'{type(error).__name__}: {error}',
                              last_window_error_attempt=latest['attempts'])
                report()
                return False
            latest.update(last_candidate={'hwnd': candidate.hwnd, 'pid': candidate.pid,
                                          'title': candidate.title},
                          last_candidate_attempt=latest['attempts'])
            if candidate.pid != self.pid:
                latest['candidate_status'] = 'pid_mismatch'
                raise NeedsAttention('MISA process changed while waiting for a window.')
            self.window = candidate
            if self.ledger:
                self.ledger.observe(self)
            if not self.is_window(kind):
                latest['candidate_status'] = 'kind_mismatch'
                report()
                return False
            if self.ledger and kind in ('sale', 'find', 'new_item'):
                try:
                    self.ledger.require_surface(self, kind)
                except NeedsAttention:
                    latest['candidate_status'] = 'not_run_owned'
                    report()
                    return False
            state = win32.window_lifecycle(candidate)
            latest['native_window'] = state
            if not state['visible'] or not state['enabled']:
                # A matching title behind a modal/closing surface is not ready for input.
                latest['candidate_status'] = 'transition_pending'
                self._invalidate()
                report()
                return False
            if not state['foreground']:
                # The ledger has proved this newly opened surface belongs to this run.
                # Restore its focus before the guarded native read; never trust the title alone.
                if self.ledger and kind in ('sale', 'find', 'new_item'):
                    win32.activate(candidate)
                    state = win32.window_lifecycle(candidate)
                    latest['native_window'] = state
                if not state['foreground']:
                    latest['candidate_status'] = 'transition_pending'
                    self._invalidate()
                    report()
                    return False
            latest.update(phase='native_observe', candidate_status='matched')
            self._invalidate()
            report()
            try:
                self._observe(kind='sale_tabs' if kind == 'sale' else kind, force=True)
            except Cancelled:
                raise
            except NeedsAttention as error:
                self.guard()
                latest.update(candidate_status='native_pending', last_native_error=str(error))
                report()
                return False
            self._guard_frame()
            latest.pop('last_native_error', None)
            latest.update(frame_id=self._frame_id, captured_at=self._captured_at.isoformat())
            return True

        try:
            read_until(observe, bool, self.cancel, timeout=seconds, label=kind)
        except Cancelled as error:
            report('cancelled', str(error))
            raise
        except Exception as error:
            report('unverified', f'{type(error).__name__}: {error}')
            summary = json.dumps({key: value for key, value in latest.items() if key != 'error'},
                                 ensure_ascii=False, separators=(',', ':'))
            # Workflow preserves native evidence by walking this cause chain.
            raise NeedsAttention(f'{error}; window_wait={summary}') from error
        report('observed')

    def close(self):
        try:
            if self._uia_readback is not None:
                self._uia_readback.close()
        finally:
            self._uia_readback = None
            self._invalidate()
            self.window = None
            self.pid = None

    def metrics(self):
        return deepcopy(self._metrics)











