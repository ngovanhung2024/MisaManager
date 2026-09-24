"""Sequential item search/create, entry, and observed XML/MISA reconciliation."""
import re
import unicodedata

from invoice_xml import format_money
from .comparison import TOTAL_FIELDS, comparison_key
from .domain import Cancelled, NeedsAttention, scalar_text
from .observation_wait import read_until
from .verification import normalized, utc_now, verify


def _tail_trim_queries(value):
    """Return the full value plus bounded 20% and 25% trailing trims."""
    value = ' '.join(unicodedata.normalize('NFC', scalar_text(value)).split())
    if not value:
        return []
    queries = [value]
    for fraction in (.20, .25):
        removed = min(max(1, round(len(value) * fraction)), len(value) - 1)
        shortened = value[:-removed].rstrip()
        if shortened and shortened not in queries:
            queries.append(shortened)
    return queries


def search_queries(name):
    return _tail_trim_queries(name)


def code_queries(code):
    return _tail_trim_queries(code)


def catalog_name_matches(expected, actual, query):
    wanted = ' '.join(unicodedata.normalize('NFC', scalar_text(expected)).split())
    found = ' '.join(unicodedata.normalize('NFC', scalar_text(actual)).split())
    # A shortened query is only a lookup aid. Never accept a prefix,
    # packaging suffix, or numeric variation as the requested item name.
    return wanted == found


def item_code_prefix(name):
    # Strip combining marks before tokenizing Vietnamese words.
    plain = ''.join(c for c in unicodedata.normalize('NFD', name.replace('đ', 'd').replace('Đ', 'D'))
                    if unicodedata.category(c) != 'Mn')
    words = re.findall(r'[A-Za-z0-9]+', plain)
    if not words:
        raise NeedsAttention('Tên hàng không có ký tự để tạo mã.')
    # Keep generated catalog codes compact; the numeric suffix provides the ID.
    prefix = ''.join(words)[:3].upper()
    if len(prefix) < 2:
        raise NeedsAttention('Tên hàng phải có ít nhất 2 ký tự để tạo mã.')
    return prefix


class SaleItems:
    def __init__(self, task):
        self.task = task
        self.desktop = task.desktop
        self.invoice = task.invoice
        self.state = task.state

    @staticmethod
    def validate(invoice):
        if invoice.total_discount:
            raise NeedsAttention('XML có chiết khấu toàn hóa đơn; cần xử lý riêng trước khi nhập.')
        for index, line in enumerate(invoice.lines, 1):
            if line.kind != '1' or (line.discount or 0) != 0 or (line.discount_rate or 0) != 0:
                raise NeedsAttention(f'Dòng {index} có khuyến mại/chiết khấu; cần xử lý riêng trước khi nhập.')
            for value in (line.name, line.unit, line.tax_rate):
                scalar_text(value)
            if not line.unit or line.quantity is None or line.quantity <= 0 or line.amount is None or line.amount < 0:
                raise NeedsAttention(f'Dòng {index} thiếu ĐVT/số lượng/thành tiền hợp lệ.')
            try:
                rate = normalized(line.tax_rate, 'tax_rate')
            except ValueError as error:
                raise NeedsAttention(f'Dòng {index} thiếu thuế suất hợp lệ.') from error
            if rate not in ('0%', '5%', '8%', '10%', 'KCT', 'KKKNT'):
                raise NeedsAttention(f'Dòng {index} có thuế suất chưa có lựa chọn MISA tương ứng.')

    def invalidate(self, fields, *, row=None):
        for field in fields:
            key = comparison_key(field, row)
            old = self.state['readbacks'].get(key, {})
            self.state['readbacks'][key] = dict(old, status='unverified', invalidated=True,
                                               reason='Đang thay đổi dữ liệu; chờ đọc lại sau Tab.')
        self.task.publish(persist=False)

    def read(self, control, expected, rule, *, row=None, field=None, publish=True):
        desktop = self.desktop
        field = field or control.id or control.name
        readings = desktop.read_evidence(control, document=self.state['run_id'], row=row, field=field)
        result = verify(expected, readings, rule=rule, hwnd=desktop.window.hwnd, pid=desktop.window.pid,
                        document=self.state['run_id'], row=row, field=field,
                        checkpoint=desktop.checkpoint, run_token=desktop.run_token,
                        input_epoch=desktop.input_epoch, not_before=desktop.last_input_at)
        desktop.record_region_verification(result)
        if publish:
            self.state['readbacks'][comparison_key(field, row)] = result.model_dump(mode='json')
            self.task.publish(persist=False)
        return result

    def unavailable(self, field, error, *, row=None):
        self.state['readbacks'][comparison_key(field, row)] = dict(status='unverified', actual_raw=None,
            reason=str(error), evaluated_at=utc_now().isoformat())
        self.task.publish(persist=False)

    def require_value(self, locate, expected, rule, *, kind, row=None, field=None, publish=True):
        latest = None

        def observe():
            nonlocal latest
            try:
                # All scalar/cell readbacks bind a native frame first. The
                # driver performs a bounded native read only when semantic data
                # is unavailable.
                field_ids = (field,) if field else ()
                with self.desktop.read_scope(kind, field_ids=field_ids):
                    latest = self.read(locate(), expected, rule, row=row, field=field, publish=publish)
                return latest.status == 'matched'
            except Cancelled:
                raise
            except NeedsAttention as error:
                if field and publish:
                    self.unavailable(field, error, row=row)
                return False
        try:
            read_until(observe, bool, self.task.cancel, timeout=12, label=field or str(expected))
        except NeedsAttention as error:
            if latest:
                raise NeedsAttention(f'{field or "Giá trị"}: đọc {latest.actual_raw!r}; '
                                     f'cần {expected!r}. {latest.reason}') from error
            raise
        return latest

    def query(self, label, text, *, purpose='', reuse_current=False):
        active_query = {'label': label, 'text': text, 'purpose': purpose}
        self.task.publish(active_query=active_query)
        other_label = 'Mã hàng' if label == 'Tên hàng' else 'Tên hàng'
        with self.desktop.read_scope('find'):
            other_filter = self.read(self.desktop.find_filter(other_label), '', 'empty',
                                     field='find_filter', publish=False)
        if other_filter.status == 'mismatched':
            self.desktop.item_edit(self.desktop.find_filter(other_label), '')
        elif other_filter.status != 'matched':
            raise NeedsAttention(f'Chưa xác minh được ô lọc {other_label}; không thể tìm riêng {label}.')
        with self.desktop.read_scope('find'):
            current = self.read(self.desktop.find_filter(label), text,
                'empty' if text == '' else 'text', field='find_filter', publish=False)
        if current.status == 'mismatched':
            self.desktop.item_edit(self.desktop.find_filter(label), text)
        elif current.status != 'matched':
            if not reuse_current:
                self.desktop.item_edit(self.desktop.find_filter(label), text)
            else:
                raise NeedsAttention(f'Chưa xác minh được ô lọc {label} sau hot reload; '
                                     'không tự nhập lại truy vấn.')
        last = None
        latest_filter = None

        def observe():
            nonlocal last, latest_filter
            with self.desktop.read_scope('find'):
                latest_filter = self.read(self.desktop.find_filter(label), text,
                    'empty' if text == '' else 'text', field='find_filter', publish=False)
                if latest_filter.status != 'matched':
                    last = None
                    return None
                count = self.desktop.item_count(False)
                rows = self.desktop.find_candidates(fresh=False) if count <= 12 else []
                signature = (count, tuple((r.target['code'], r._cells['tenhang'].value) for r in rows))
            same = signature == last
            last = signature
            return (count, rows) if same else None
        try:
            return read_until(observe, lambda result: result is not None, self.task.cancel,
                              timeout=12, interval=.016, label='kết quả tìm ' + label)
        except NeedsAttention as error:
            if latest_filter is not None and latest_filter.status != 'matched':
                raise NeedsAttention(f'Ô lọc {label}: đọc {latest_filter.actual_raw!r}; '
                                     f'cần {text!r}. {latest_filter.reason}') from error
            raise

    @staticmethod
    def _resume_query(active, label, text, purpose):
        return bool(active and active.get('label') == label and active.get('text') == text
                    and active.get('purpose') == purpose)

    def choose_existing(self, line, *, resume_query=None):
        queries = [('Tên hàng', query, 'existing_name')
                   for query in search_queries(line.name)]
        # Keep the dialog open and try the supplier code when the name query
        # does not resolve the catalog row. The shortened variants remove only
        # the trailing 20% and 25% as lookup aids.
        queries.extend(('Mã hàng', query, 'existing_code')
                       for query in code_queries(line.code))
        start = 0
        if resume_query and resume_query.get('purpose') in ('existing_name', 'existing_code'):
            try:
                start = queries.index((resume_query.get('label'), resume_query.get('text'),
                                      resume_query.get('purpose')))
            except ValueError as error:
                raise NeedsAttention('Truy vấn hàng trước hot reload không còn hợp lệ.') from error
        too_many = None
        for index, (label, query, purpose) in enumerate(queries[start:], start):
            self.task.publish(next_step=f'Tìm {line.name} · {label}: {query}')
            count, rows = self.query(label, query, purpose=purpose,
                reuse_current=(index == start and self._resume_query(
                    resume_query, label, query, purpose)))
            if count == 0:
                continue
            if count > 12:
                too_many = (count, label)
                continue
            result = self.select_result(line, query, query_field=label, rows=rows)
            if result is not None:
                return result
        if too_many:
            count, label = too_many
            raise NeedsAttention(f'Tìm thấy {count} hàng với {label}; cần giá trị đầy đủ hơn.')
        return None

    @staticmethod
    def effect_key(line):
        import hashlib
        return 'catalog:' + hashlib.sha256((line.name + '\0' + line.unit + '\0' + line.tax_rate).encode()).hexdigest()

    def select_result(self, line, query, *, query_field='Tên hàng', exact_code=None, rows=None):
        if rows is None:
            with self.desktop.read_scope('find'):
                rows = self.desktop.find_candidates(fresh=False)
        else:
            # query() returns controls from the second matching live frame.
            # Reuse that pinned observation for candidate verification instead
            # of paying for a third identical full-layout scan.
            self.desktop._guard_frame()
        matches = []
        for row in rows:
            name = self.read(row._cells['tenhang'], 'present', 'nonempty',
                             row=row.target.get('index', 0) + 1,
                             field='candidate_name', publish=False)
            if name.status != 'matched':
                raise NeedsAttention('Chưa đọc rõ tên kết quả; không coi là không tìm thấy.')
            code_matches = True
            if query_field == 'Mã hàng':
                expected_code = ' '.join(unicodedata.normalize('NFC', scalar_text(query)).split()).casefold()
                actual_code = ' '.join(unicodedata.normalize('NFC', scalar_text(row.target['code'])).split()).casefold()
                code_matches = bool(expected_code) and expected_code in actual_code
            if (code_matches and catalog_name_matches(line.name, name.actual_raw, query)
                    and (exact_code is None or row.target['code'] == exact_code)):
                matches.append((row, name.actual_raw))
        if not matches:
            # A shortened query can return rows whose names are not exact.
            # Leave the dialog open so the caller can try the next query.
            return None
        unit_matches = []
        for row, name in matches:
            candidate_unit = self.read(
                row._cells['dvt'], line.unit, 'unit',
                row=row.target.get('index', 0) + 1,
                field='candidate_unit', publish=False)
            if candidate_unit.status == 'matched':
                unit_matches.append((row, name, candidate_unit))
            elif candidate_unit.status != 'mismatched':
                raise NeedsAttention(
                    f'ĐVT kết quả {row.target["code"]}: đọc {candidate_unit.actual_raw!r}; '
                    f'cần {line.unit!r}.')
        if not unit_matches:
            raise NeedsAttention('Có vật tư cùng tên nhưng không có dòng hiện hữu đúng ĐVT; không tạo mã mới.')
        # Prefer a preexisting MISA code over the code pattern this workflow
        # generates, then keep the native result order stable.
        # Treat every three-digit suffixed code as workflow-generated. Older
        # runs used longer prefixes, so limiting this to the current prefix
        # could still prefer a stale generated row over a native MISA code.
        generated = re.compile(r'^[A-Z0-9]+_[0-9]{3}$', re.IGNORECASE)
        row, name, candidate_unit = next(
            (match for match in unit_matches if not generated.fullmatch(match[0].target['code'])),
            unit_matches[0])
        code = row.target['code']
        checkbox = self.desktop.unique([c for c in self.desktop.controls(row)
                                        if c.role == 'find_checkbox'], 'ô lựa chọn vật tư')
        if any(c.toggle for r in rows if r.target['code'] != code
               for c in self.desktop.controls(r) if c.role == 'find_checkbox'):
            raise NeedsAttention('Đang có mặt hàng khác được tích chọn trong Tìm nhanh.')
        if not checkbox.toggle:
            self.desktop.click(checkbox)

        def selected():
            with self.desktop.read_scope('find'):
                rows = self.desktop.find_candidates()
                picked = [r.target['code'] for r in rows
                          if any(c.toggle for c in self.desktop.controls(r) if c.role == 'find_checkbox')]
                return picked == [code]
        read_until(selected, bool, self.task.cancel, timeout=5, label='tích chọn ' + code)
        self.task.effect(self.effect_key(line), state='select_pending', code=code,
                         name=line.name, unit=line.unit, tax_rate=line.tax_rate,
                         query=query, created=False, before_select_frame=self.desktop._frame_id,
                         catalog_unit_proof=candidate_unit.model_dump(mode='json'))
        self.desktop.click(self.desktop.item_button('Đồng ý', 'find'))
        self.desktop.wait_window('sale', seconds=12)
        self.task.require_sale()
        self.task.effect(self.effect_key(line), state='returned_to_sale', code=code,
                         name=line.name, unit=line.unit, tax_rate=line.tax_rate,
                         query=query, created=False, after_select_frame=self.desktop._frame_id,
                         catalog_unit_proof=candidate_unit.model_dump(mode='json'))
        return dict(code=code, name=name, query=query, created=False)

    def reserve_code(self, line, *, resume_query=None):
        effect = self.task.effects.get(self.effect_key(line))
        if effect and effect.get('code'):
            if effect.get('state') in ('observed_absent', 'reserved'):
                return effect['code']
            raise NeedsAttention('Mã vật tư đã có trạng thái không thể tiếp tục an toàn.')
        base = item_code_prefix(line.name)
        start_suffix = 1
        if resume_query and resume_query.get('purpose') == 'reserve_code':
            match = re.fullmatch(re.escape(base) + r'_([0-9]{3})', str(resume_query.get('text') or ''))
            if match is None:
                raise NeedsAttention('Mã vật tư đang kiểm tra trước hot reload không hợp lệ.')
            start_suffix = int(match.group(1))
        # Name and code lookup are completed before the caller cancels the
        # lookup and creates a new catalog item with this deterministic code.
        return f'{base}_{start_suffix:03d}'

    def current_value(self, locate, expected, rule, *, kind, row=None, field=None):
        field_ids = (field,) if field else ()
        with self.desktop.read_scope(kind, field_ids=field_ids):
            control = locate()
            result = self.read(control, expected, rule, row=row, field=field, publish=False)
            if result.status in ('matched', 'mismatched'):
                return result.status
            empty = self.read(control, '', 'empty', row=row, field=field, publish=False)
            if empty.status == 'matched':
                return 'mismatched'
        raise NeedsAttention(f'{field or "Giá trị"}: chưa đọc chắc chắn giá trị hiện tại; không tự nhập lại.')

    def populate_new_item(self, code, line, *, resume=False):
        # The dialog opens on this tab. Prove it once before the fixed field
        # map is used for code/name entry.
        self.desktop.ensure_tab('1. Ngầm định')
        text_fields = (
                ('txtInventoryItemCode', 'Mã (*)', code, 'exact'),
                ('txtInventoryItemName', 'Tên (*)', line.name, 'text'))
        catalog_fields = (
                ('cboUnitID', line.unit, 'unit'), ('cboStockID', 'KHH', 'exact'),
                ('cboVATRate', normalized(line.tax_rate, 'tax_rate'), 'tax_rate'))
        for field, label, value, rule in text_fields:
            if (not resume or self.current_value(
                    lambda f=field, label=label: self.desktop.field((f,), label),
                    value, rule, kind='new_item', field=field) != 'matched'):
                self.task.effect(self.effect_key(line), state='reserved', code=code,
                                 active_field=field, field_state='write_pending')
                self.desktop.item_edit(self.desktop.field((field,), label), value)
            self.require_value(lambda f=field, label=label: self.desktop.field((f,), label),
                               value, rule, kind='new_item', field=field, publish=False)
            self.task.effect(self.effect_key(line), state='reserved', code=code,
                             active_field=field, field_state='observed')
        for field, value, rule in catalog_fields:
            if (not resume or self.current_value(
                    lambda f=field: self.desktop.field((f,), self.desktop.FIELD_LAYOUT[f][0]),
                    value, rule, kind='new_item', field=field) != 'matched'):
                self.task.effect(self.effect_key(line), state='reserved', code=code,
                                 active_field=field, field_state='write_pending')
                self.desktop.select_catalog(field, value)
            self.require_value(lambda f=field: self.desktop.field((f,), self.desktop.FIELD_LAYOUT[f][0]),
                               value, rule, kind='new_item', field=field, publish=False)
            self.task.effect(self.effect_key(line), state='reserved', code=code,
                             active_field=field, field_state='observed')
        # All required catalog values are reread together before the only permitted save.
        for field, value, rule in (
                ('txtInventoryItemCode', code, 'exact'), ('txtInventoryItemName', line.name, 'text'),
                ('cboUnitID', line.unit, 'unit'), ('cboStockID', 'KHH', 'exact'),
                ('cboVATRate', normalized(line.tax_rate, 'tax_rate'), 'tax_rate')):
            result = self.require_value(
                lambda f=field: self.desktop.field((f,), self.desktop.FIELD_LAYOUT[f][0]),
                value, rule, kind='new_item', field=field, publish=False)
            if field == 'cboUnitID' and result is not None and result.status == 'matched':
                self.task.effect(
                    self.effect_key(line),
                    catalog_unit_proof=result.model_dump(mode='json'))
        # Resolve the current dialog button before recording a pending save.
        # A lookup failure leaves the dialog safely resumable; once the target
        # is bound, the pending marker protects against duplicate Cất input.
        save_button = self.desktop.item_button('Cất', 'new_item')
        self.task.effect(self.effect_key(line), state='save_pending',
                         before_save_frame=self.desktop._frame_id,
                         save_target_resolved=True)
        self.desktop.click(save_button)
        self.desktop.wait_window('sale', seconds=12)
        self.task.require_sale()
        self.task.effect(self.effect_key(line), state='returned_to_sale',
                         code=code, name=line.name, unit=line.unit, tax_rate=line.tax_rate,
                         query=line.name, created=True, after_save_frame=self.desktop._frame_id)
        return dict(code=code, name=line.name, query=line.name, created=True)

    def create_item(self, row, line, *, resume_query=None):
        code = self.reserve_code(line, resume_query=resume_query)
        self.task.effect(self.effect_key(line), state='reserved', code=code, name=line.name,
                         unit=line.unit, tax_rate=line.tax_rate)
        self.desktop.click(self.desktop.item_button('Hủy bỏ', 'find'))
        self.desktop.wait_window('sale', seconds=12)
        self.task.require_sale()
        # Returning from the find dialog leaves the same empty add-new row in
        # the voucher. Creation must target the plus button, never the lens.
        self.desktop.open_inventory_menu(row, new=True, action='new')
        self.desktop.inventory_action('new')
        return self.populate_new_item(code, line)

    def resume_new_item(self, line):
        effect = self.task.effects.get(self.effect_key(line)) or {}
        if effect.get('state') == 'save_pending' and effect.get('save_target_resolved'):
            raise NeedsAttention('Cất vật tư đã được gửi trước hot reload; không tự gửi Cất lần nữa.')
        if effect.get('state') == 'save_pending':
            # Legacy runs marked pending before resolving Cất. That marker is
            # not evidence that input was sent, so resume the open dialog.
            self.task.effect(self.effect_key(line), state='reserved',
                             code=effect.get('code'), name=line.name,
                             unit=line.unit, tax_rate=line.tax_rate,
                             recovery='save_target_unresolved')
            effect = dict(effect, state='reserved')
        if effect.get('state') != 'reserved' or not effect.get('code'):
            raise NeedsAttention('Thiếu mã vật tư đã giữ; không suy diễn dữ liệu để tiếp tục Thêm vật tư.')
        return self.populate_new_item(effect['code'], line, resume=True)

    def resume_sale_mapping(self, row, line):
        effect = self.task.effects.get(self.effect_key(line)) or {}
        if effect.get('state') not in ('select_pending', 'save_pending', 'returned_to_sale'):
            return None
        code = effect.get('code')
        if not code:
            raise NeedsAttention('Thiếu mã vật tư sau khi cửa sổ đã trở về chứng từ.')
        self.task.require_sale()
        self.desktop.ensure_tab('1. Hàng tiền')
        self.read_line(row, line, code, (
            ('mahang', code, 'exact'), ('tenhang', line.name, 'text')),
            required=('mahang', 'tenhang'))
        try:
            self.read_line(row, line, code, (('dvt', line.unit, 'unit'),), required=('dvt',))
        except NeedsAttention:
            # Older checkpoints did not persist the native Find-row unit proof.
            # Reopen the existing row's catalog search so the unit is verified
            # from the current result before accepting the sale mapping.
            self.desktop.open_inventory_menu(row, new=False, action='search')
            self.desktop.inventory_action('search')
            refreshed = self.choose_existing(line)
            if refreshed is None:
                return None
            effect = self.task.effects.get(self.effect_key(line)) or {}
            code = effect.get('code') or refreshed.get('code') or code
        created = bool(effect.get('created', effect.get('state') == 'save_pending'))
        self.task.effect(self.effect_key(line), state='returned_to_sale', code=code,
                         name=line.name, unit=line.unit, tax_rate=line.tax_rate,
                         query=effect.get('query') or line.name, created=created,
                         verification='fresh_sale_row')
        return dict(code=code, name=line.name, query=effect.get('query') or line.name,
                    created=created)

    def reverify_existing_mapping(self, row, line, mapping):
        """Reopen the existing row's catalog search when no unit proof survived reload."""
        self.task.require_sale()
        self.desktop.ensure_tab('1. Hàng tiền')
        self.desktop.open_inventory_menu(row, new=False, action='search')
        self.desktop.inventory_action('search')
        refreshed = self.choose_existing(line)
        if refreshed is None:
            raise NeedsAttention('Không xác minh lại được vật tư đã chọn từ kết quả danh mục.')
        return refreshed

    def resume_reserved_item(self, row, line):
        effect = self.task.effects.get(self.effect_key(line)) or {}
        if effect.get('state') != 'reserved' or not effect.get('code'):
            return None
        self.task.require_sale()
        self.desktop.ensure_tab('1. Hàng tiền')
        self.prepare_row(row)
        self.desktop.open_inventory_menu(row, new=True, action='new')
        self.desktop.inventory_action('new')
        return self.populate_new_item(effect['code'], line)

    @staticmethod
    def value_effect_key(row, field):
        return f'sale_value:{row}:{field}'

    def enter_sale_value(self, row, line, code, field, value):
        expected = format_money(value)
        locate = lambda: self.desktop.sale_item_cell(row, field, code=code)
        status = self.current_value(locate, expected, 'decimal', kind='sale', row=row, field=field)
        key = self.value_effect_key(row, field)
        if status != 'matched':
            self.task.effect(key, state='write_pending', expected=expected, code=code, row=row, field=field)
            self.invalidate(('soluong', 'thanhtien', 'dongia', 'tienthuegtgt'), row=row)
            self.invalidate(tuple(total_field for total_field, _, _ in TOTAL_FIELDS))
            self.desktop.item_edit(locate(), expected)
        self.read_line(row, line, code, (
            ('soluong', format_money(line.quantity), 'decimal'),
            ('thanhtien', format_money(line.amount), 'decimal'),
            ('dongia', format_money(line.unit_price), 'decimal')),
            required=(field,))
        self.task.effect(key, state='observed', expected=expected, code=code, row=row, field=field)
        self.read_totals()

    def read_totals(self, *, final=False):
        results = []
        self.task.require_sale()
        total_ids = tuple(field for field, _, attr in TOTAL_FIELDS
                          if getattr(self.invoice, attr) is not None)
        with self.desktop.read_scope('sale', field_ids=total_ids):
            for field, label, attr in TOTAL_FIELDS:
                expected = getattr(self.invoice, attr)
                if expected is None:
                    continue
                try:
                    result = self.read(self.desktop.field((field,), label), format_money(expected),
                                       'decimal', field=field)
                    results.append(result.status)
                except Cancelled:
                    raise
                except NeedsAttention as error:
                    self.unavailable(field, error)
                    results.append('unverified')
        if final and (self.invoice.tax is None or any(status != 'matched' for status in results)):
            raise NeedsAttention('Tổng tiền/thuế chưa khớp hoặc chưa đọc được. Xem tab Đối chiếu; không tự điều chỉnh số tiền.')

    def read_line(self, row, line, code, fields, *, required=()):
        for field, expected, rule in fields:
            if expected is None or expected == '':
                continue
            if field == 'dvt':
                effect = self.task.effects.get(self.effect_key(line)) or {}
                proof = effect.get('catalog_unit_proof') or {}
                if (proof.get('status') == 'matched'
                        and proof.get('actual_normalized') == normalized(expected, 'unit')):
                    result = dict(proof, field=field, row=row,
                                  reason='dvt: Đã xác minh từ dòng kết quả danh mục hiện tại')
                    self.state['readbacks'][comparison_key(field, row)] = result
                    self.task.publish(persist=False)
                    continue
            locate = lambda field=field: self.desktop.sale_item_cell(row, field, code=code)
            if field in required:
                self.require_value(locate, expected, rule, kind='sale', row=row, field=field)
            else:
                try:
                    with self.desktop.read_scope('sale', field_ids=(field,)):
                        self.read(locate(), expected, rule, row=row, field=field)
                except Cancelled:
                    raise
                except NeedsAttention as error:
                    self.unavailable(field, error, row=row)

    def prepare_row(self, row):
        previous = None
        for _ in range(30):
            # Prefer the semantic add-row DataItem. Its UIA resolver checks the
            # complete zero-based row sequence and the `-1` add-row identity.
            if self.desktop._uia_sale_item_cell(
                    row, 'mahang', new=True, semantic_only=True) is not None:
                return
            with self.desktop.read_scope('sale', tab='1. Hàng tiền'):
                try:
                    data = self.desktop._item_grid_data(required_columns=('mahang',))
                except NeedsAttention:
                    if self.desktop._uia_sale_item_cell(
                            row, 'mahang', new=True, semantic_only=True) is not None:
                        return
                    raise
                _, _, count, groups, prompt = data
                if count != row - 1:
                    raise NeedsAttention('Số dòng hiện tại không khớp tiến độ; không tự nhập lại.')
                if prompt is not None:
                    self.desktop._item_row_proof.update(row=row)
                    return
                self.desktop._item_row_proof.update(row=row)
                uia = self.desktop._uia_sale_item_cell(row, 'mahang', new=True)
                if uia is not None:
                    return
            signature = tuple(tuple(w.text for w in group) for _, group in groups)
            if signature == previous:
                raise NeedsAttention('Chưa đọc được dòng Bấm vào đây để thêm mới ở cuối bảng.')
            previous = signature
            self.desktop.scroll_inventory(dict(type='item_grid_scroll'), -5)
        raise NeedsAttention('Chưa tới được dòng nhập mới sau 30 lần cuộn.')

    def run(self, *, resume=None):
        self.state['item_mappings'] = dict(self.state.get('item_mappings') or {})
        self.state['line_total'] = len(self.invoice.lines)
        resume = resume or {}
        start_row = int(resume.get('active_row') or 1)
        resume_step = str(resume.get('active_step') or '')
        resume_query = resume.get('active_query') if isinstance(resume.get('active_query'), dict) else None
        if (resume_step.endswith(('.values', '.tax', '.totals'))
                and not self.state['item_mappings'].get(str(start_row))):
            raise NeedsAttention('Thiếu mã vật tư đã xác minh cho checkpoint tiếp tục; không nhập lại mù.')
        for row, line in enumerate(self.invoice.lines, 1):
            if row < start_row:
                continue
            self.state['active_row'] = row
            resume_current = row == start_row
            mapping = self.state['item_mappings'].get(str(row))
            mapped_search = (resume_current and resume_step == f'add.items.{row}.search' and mapping
                             and self.desktop.is_window('sale'))
            if not (resume_current and (resume_step.endswith(('.values', '.tax', '.totals'))
                                        or mapped_search) and mapping):
                with self.task.step(f'add.items.{row}.search', f'Dòng {row}/{len(self.invoice.lines)} · {line.name}') as proof:
                    continuing_search = (resume_current and resume_step == f'add.items.{row}.search'
                                         and self.desktop.is_window('find'))
                    continuing_new_item = (resume_current and resume_step == f'add.items.{row}.search'
                                            and self.desktop.is_window('new_item'))
                    continuing_sale = (resume_current and resume_step == f'add.items.{row}.search'
                                       and self.desktop.is_window('sale'))
                    if continuing_search:
                        self.desktop.sync(kind='find')
                    elif continuing_new_item:
                        self.desktop.sync(kind='new_item')
                    elif continuing_sale:
                        self.desktop.sync(kind='sale')
                    else:
                        self.task.require_sale()
                        self.desktop.ensure_tab('1. Hàng tiền')
                        self.prepare_row(row)
                        self.invalidate(tuple(field for field, _, _ in TOTAL_FIELDS))
                        self.desktop.open_inventory_menu(row, new=True)
                        self.desktop.inventory_action('search')
                    active_query = resume_query if continuing_search else None
                    if continuing_new_item:
                        mapping = self.resume_new_item(line)
                    elif continuing_sale:
                        mapping = self.resume_sale_mapping(row, line)
                        if mapping is None:
                            mapping = self.resume_reserved_item(row, line)
                        if mapping is None:
                            self.prepare_row(row)
                            self.invalidate(tuple(field for field, _, _ in TOTAL_FIELDS))
                            self.desktop.open_inventory_menu(row, new=True)
                            self.desktop.inventory_action('search')
                            mapping = self.choose_existing(line)
                    elif active_query and str(active_query.get('purpose', '')).startswith('reserve_'):
                        mapping = self.create_item(row, line, resume_query=active_query)
                    else:
                        mapping = self.choose_existing(line, resume_query=active_query)
                    if mapping is None:
                        self.task.publish(next_step='Không tìm thấy; thêm vật tư ' + line.name)
                        mapping = self.create_item(row, line)
                    self.state['item_mappings'][str(row)] = mapping
                    self.task.publish(active_query=None)
                    self.read_line(row, line, mapping['code'], (
                        ('mahang', mapping['code'], 'exact'), ('tenhang', line.name, 'text'), ('dvt', line.unit, 'unit')),
                        required=('mahang', 'dvt'))
                    proof['verified'] = True
            elif mapped_search:
                self.task.require_sale()
                self.desktop.ensure_tab('1. Hàng tiền')
                effect = self.task.effects.get(self.effect_key(line)) or {}
                if not effect.get('catalog_unit_proof'):
                    mapping = self.reverify_existing_mapping(row, line, mapping)
                    self.state['item_mappings'][str(row)] = mapping
                self.read_line(row, line, mapping['code'], (
                    ('mahang', mapping['code'], 'exact'), ('tenhang', line.name, 'text'),
                    ('dvt', line.unit, 'unit')), required=('mahang', 'dvt'))
            code = mapping['code']
            if not (resume_current and resume_step.endswith(('.tax', '.totals'))):
                with self.task.step(f'add.items.{row}.values', 'Nhập số lượng và thành tiền · ' + code) as proof:
                    for field, value in (('soluong', line.quantity), ('thanhtien', line.amount)):
                        self.enter_sale_value(row, line, code, field, value)
                    self.read_line(row, line, code, (
                        ('soluong', format_money(line.quantity), 'decimal'),
                        ('thanhtien', format_money(line.amount), 'decimal')),
                        required=('soluong', 'thanhtien'))
                    proof['verified'] = True
            if not (resume_current and resume_step.endswith('.totals')):
                with self.task.step(f'add.items.{row}.tax', 'Đọc lại thuế GTGT · ' + code) as proof:
                    self.desktop.ensure_tab('2. Thuế')
                    locate_rate = lambda: self.desktop.sale_item_cell(row, 'thuesuatgtgt', code=code)
                    rate = normalized(line.tax_rate, 'tax_rate')
                    if self.current_value(locate_rate, rate, 'tax_rate', kind='sale',
                                          row=row, field='thuesuatgtgt') != 'matched':
                        self.task.effect(self.value_effect_key(row, 'thuesuatgtgt'),
                                         state='write_pending', expected=rate, code=code,
                                         row=row, field='thuesuatgtgt')
                        self.invalidate(('thuesuatgtgt', 'tienthuegtgt'), row=row)
                        self.invalidate(tuple(field for field, _, _ in TOTAL_FIELDS))
                        self.desktop.item_edit(locate_rate(), rate)
                    self.read_line(row, line, code, (
                        ('thuesuatgtgt', line.tax_rate, 'tax_rate'),
                        ('tienthuegtgt', format_money(line.tax) if line.tax is not None else None, 'decimal')),
                        required=('thuesuatgtgt',))
                    self.read_totals()
                    proof['verified'] = True
            self.state['lines_completed'] = row
            self.task.publish()
        with self.task.step('add.items.totals', 'Đối chiếu tổng tiền hàng, thuế GTGT và thanh toán…') as proof:
            self.desktop.ensure_tab('1. Hàng tiền')
            self.read_totals(final=True)
            proof['verified'] = True
        self.state['active_row'] = None
