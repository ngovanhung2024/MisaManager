"""Addressable element map for one MISA window.

UIA3/Win32 gives controls and text elements. They are reduced to one schema so
the read-only observer can describe a MISA form like a document:
semantic kind, region role, bounds, current value, actions that are allowed and
the effect to expect when acting. The map is observational: it never sends
input by itself.
"""
from __future__ import annotations

from .label_matching import label_key

from dataclasses import dataclass, field, replace
from datetime import datetime, timezone
import hashlib
import re

from .domain import normalize, scalar_text

NATIVE_KINDS = {
    50000: 'button', 50002: 'checkbox', 50003: 'combo', 50004: 'edit', 50007: 'menu_item',
    50013: 'radio', 50019: 'tab', 50020: 'label', 50025: 'grid', 50028: 'grid',
    50029: 'row', 50035: 'header',
}

ACTIONS = {
    'button': ('click',),
    'tab': ('click',),
    'checkbox': ('click',),
    'radio': ('click',),
    'combo': ('click', 'select', 'type'),
    'edit': ('click', 'type', 'clear'),
    'grid': ('scroll',),
    'row': ('click',),
    'cell': ('click', 'type'),
    'menu_item': ('click',),
    'label': (),
    'value': (),
}

EFFECTS = {
    'button': 'mở cửa sổ/menu hoặc chạy lệnh',
    'tab': 'đổi tab đang xem',
    'checkbox': 'bật/tắt lựa chọn',
    'radio': 'chọn một mục trong nhóm',
    'combo': 'mở danh sách để chọn giá trị',
    'edit': 'đổi giá trị ô; phải đọc lại sau khi nhập',
    'grid': 'danh sách dòng; cuộn để xem thêm',
    'row': 'chọn dòng, MISA có thể tự điền vào form',
    'cell': 'sửa giá trị ô trong bảng',
    'menu_item': 'chạy lệnh, thường mở hộp thoại mới',
}

BUTTON_WORDS = {
    'them', 'cat', 'luu', 'ghiso', 'ghi', 'dongy', 'huy', 'boqua', 'ketthuc', 'chon', 'tim',
    'timkiem', 'xoa', 'sua', 'in', 'nap', 'xuat', 'tieptuc', 'quaylai', 'dong', 'xacnhan',
    'lay', 'chuyen', 'khoiphuc', 'thaythe', 'bosung',
}
TAB_WORDS = {
    'phieuthu', 'hoadon', 'hangtien', 'thue', 'giavon', 'thongke', 'khac', 'hanghoadichvu',
    'chungtu', 'dinhkhoan', 'thanhtoan',
}
CHECKBOX_GLYPHS = ('口', '☐', '□', '▢', '冂')
RADIO_GLYPHS = ('○', '◯', '◉', '⊙')
MONEY = re.compile(r'^-?[\d.,]+$')
DATE = re.compile(r'^\d{1,2}/\d{1,2}/\d{2,4}$')

KNOWN_EFFECTS = {
    ('main', 'banhang'): 'mở phân hệ bán hàng (hoặc bung menu con)',
    ('main', 'them'): 'mở phiếu bán hàng trống (cửa sổ sale)',
    ('sale', 'kinhlup'): 'mở menu danh mục hàng: chọn hàng có sẵn hoặc thêm mới',
    ('sale', 'them'): 'mở hộp thoại tạo chứng từ/vật tư mới (cửa sổ new_item)',
    ('find', 'themvattu'): 'mở hộp thoại Thêm vật tư (cửa sổ new_item)',
    ('find', 'tim'): 'lọc lại danh sách kết quả theo từ khóa',
    ('new_item', 'cat'): 'lưu vật tư mới rồi đóng hộp thoại',
}

# UIA patterns are the real capabilities of a control; the engine reports them
# per element, so actions come from the control itself instead of a guess.
PATTERN_ACTIONS = {
    'InvokePattern': ('click',),
    'SelectionItemPattern': ('click',),
    'TogglePattern': ('click',),
    'ExpandCollapsePattern': ('expand', 'collapse'),
    'ValuePattern': ('type', 'clear'),
    'ScrollItemPattern': ('scroll',),
    'GridPattern': ('scroll',),
    'TextPattern': ('read',),
}

@dataclass(frozen=True)
class Element:
    id: str
    name: str
    kind: str
    role: str
    bounds: tuple
    value: str = ''
    selected: bool = False
    toggle: int = 0
    enabled: bool = True
    source: str = 'native'
    token: str = ''
    selector: dict | None = None
    patterns: tuple = ()
    region: str = 'khac'
    learned: str = ''
    row: int = 0
    column: int = 0
    header: str = ''

    @property
    def center(self):
        x, y, width, height = self.bounds
        return round(x + width / 2), round(y + height / 2)

    @property
    def interactive(self):
        return bool(ACTIONS.get(self.kind)) and self.enabled

    @property
    def actions(self):
        from_patterns = tuple(dict.fromkeys(
            action for pattern in self.patterns for action in PATTERN_ACTIONS.get(pattern, ())))
        return from_patterns or ACTIONS.get(self.kind, ())

    @property
    def effect(self):
        return (self.learned or KNOWN_EFFECTS.get((self.role, normalize(self.name)))
                or EFFECTS.get(self.kind, ''))

    @property
    def unknown_effect(self):
        """Interactive elements whose outcome nobody has observed or documented."""
        return self.interactive and not self.learned and not KNOWN_EFFECTS.get((self.role, normalize(self.name)))

    def to_dict(self):
        data = {'id': self.id, 'name': self.name, 'kind': self.kind, 'role': self.role,
                'region': self.region, 'bounds': [round(v, 1) for v in self.bounds],
                'actions': list(self.actions),
                'effect': self.effect, 'interactive': self.interactive, 'source': self.source,
                'value': self.value, 'selected': self.selected, 'enabled': self.enabled}
        if self.toggle:
            data['toggle'] = self.toggle
        if self.token:
            data['token'] = self.token
        if self.selector:
            data['selector'] = self.selector
        if self.patterns:
            data['patterns'] = list(self.patterns)
        if self.row or self.column or self.header:
            data['cell'] = {'row': self.row, 'column': self.column, 'header': self.header}
        return data

def element_id(kind, name, bounds):
    x, y, *_ = bounds
    key = f'{kind}|{label_key(name)}|{round(x / 16)}|{round(y / 16)}'
    return kind[:2] + hashlib.blake2s(key.encode('utf-8'), digest_size=4).hexdigest()

def classify_word(text, *, role='', selected=False, toggle=0):
    """Semantic kind for a standalone native text element."""
    key = normalize(text)
    stripped = str(text).strip()
    if stripped and stripped[0] in CHECKBOX_GLYPHS:
        return 'checkbox'
    if stripped and stripped[0] in RADIO_GLYPHS:
        return 'radio'
    if key in TAB_WORDS:
        return 'tab'
    if key in BUTTON_WORDS:
        return 'button'
    if role == 'field':
        return 'combo' if key.startswith('cbo') else 'edit'
    if role == 'grid':
        return 'grid'
    if role == 'row':
        return 'row'
    if role == 'menu':
        return 'menu_item'
    if DATE.fullmatch(stripped) or MONEY.fullmatch(key):
        return 'value'
    return 'label'

def _inside(bounds, box):
    left, top, width, height = bounds
    x, y, w, h = box
    return x - 3 <= left and top >= y - 3 and left + width <= x + w + 3 and top + height <= y + h + 3

def _overlap_ratio(left, right):
    lx, ly, lw, lh = left
    rx, ry, rw, rh = right
    overlap = max(0, min(lx + lw, rx + rw) - max(lx, rx)) * max(0, min(ly + lh, ry + rh) - max(ly, ry))
    return overlap / max(1, min(lw * lh, rw * rh))

def _dedupe(elements):
    """One entry per visible object: a bordered box and its word are the same."""
    kept = []
    for element in sorted(elements, key=lambda item: (not item.name, -(item.bounds[2] * item.bounds[3]))):
        duplicate = any(item.kind == element.kind and label_key(item.name) == label_key(element.name)
                        and item.name and element.name
                        and _overlap_ratio(item.bounds, element.bounds) >= .6
                        for item in kept)
        if not duplicate:
            kept.append(element)
    return kept

def region_bands(window, width, height):
    """Named bands of a MISA window used by the native driver."""
    width, height = int(width or 0), int(height or 0)
    if width <= 0 or height <= 0:
        return ()
    if window == 'sale':
        header_bottom = min(height, max(360, min(410, height - 98)))
        grid_top = max(70, header_bottom - 20)
        footer_top = max(grid_top, height - 98)
        return (('tieu_de', (0, 0, width, 40)),
                ('thanh_cong_cu', (0, 40, width, max(1, 70 - 40))),
                ('dau_phieu', (0, 70, width, max(1, header_bottom - 70))),
                ('luoi_hang', (0, grid_top, width, max(1, footer_top - grid_top))),
                ('chan_phieu', (0, footer_top, width, max(1, height - footer_top))))
    if window == 'main':
        bottom = min(height, max(190, height - 40))
        return (('tieu_de', (0, 0, width, 40)),
                ('thanh_cong_cu', (0, 40, width, max(1, bottom - 40))),
                ('so_nhat_ky', (0, bottom, width, max(1, height - bottom))))
    if window in ('find', 'new_item', 'dialog'):
        footer_top = max(40, height - 80)
        return (('tieu_de', (0, 0, width, 40)),
                ('noi_dung', (0, 40, width, max(1, footer_top - 40))),
                ('chan_hop_thoai', (0, footer_top, width, max(1, height - footer_top))))
    return (('tieu_de', (0, 0, width, 40)),
            ('noi_dung', (0, 40, width, max(1, height - 40))))

def kind_of_title(title):
    """Map a MISA window title to the layout kind used by the native driver."""
    name = str(title or '').lower()
    if 'tim nhanh' in name or 'tìm nhanh' in name:
        return 'find'
    if 'them vat tu' in name or 'thêm vật tư' in name:
        return 'new_item'
    if 'ban hang' in name or 'bán hàng' in name or 'chung tu' in name or 'chứng từ' in name:
        return 'sale'
    if 'misasme' in name or 'misa sme' in name:
        return 'main'
    return 'dialog'

def grid_rows(cells, *, minimum_cells=3):
    """Cluster grid cells into visual rows; short bands are not table rows."""
    rows = []
    for cell in sorted(cells, key=lambda item: (item.bounds[1], item.bounds[0])):
        top, bottom = cell.bounds[1], cell.bounds[1] + cell.bounds[3]
        height = max(8, cell.bounds[3])
        if rows and top < rows[-1]['top'] + height * .6:
            rows[-1]['bottom'] = max(rows[-1]['bottom'], bottom)
            rows[-1]['cells'].append(cell)
        else:
            rows.append({'top': top, 'bottom': bottom, 'cells': [cell]})
    return [row for row in rows if len(row['cells']) >= minimum_cells]

def annotate_grid(elements, band, picture=None, *, headers=(), row_base=1):
    """Give cells inside a grid band their row, column and column header."""
    if not band:
        return list(elements)
    x, y, width, height = band
    # Grid row and column identity comes from native UIA; screenshots do not
    # provide layout or value evidence.
    columns = []
    inside = [element for element in elements
              if element.kind in ('edit', 'cell') and x <= element.center[0] <= x + width
              and y <= element.center[1] <= y + height]
    if not inside:
        return list(elements)
    rows = grid_rows(inside)
    row_index = {element.id: index + row_base for index, row in enumerate(rows)
                 for element in row['cells']}
    first_top = min((row['top'] for row in rows), default=None)
    header_words = []
    if first_top is not None:
        header_words = [word for word in (headers or {}).values()
                        if word is not None and y <= getattr(word, 'cy', -1) < first_top]
        header_words.sort(key=lambda word: word.cx)
    if not header_words and rows:
        # MISA's find grids print the column titles inside the first grid row.
        for element in sorted(rows[0]['cells'], key=lambda item: item.center[0]):
            header_words.append(type('HeaderWord', (), {'cx': element.center[0],
                                                        'text': element.value or element.name})())
    header_by_column = {}
    for index, (left, right) in enumerate(columns, start=1):
        for word in header_words:
            if left <= word.cx <= right:
                header_by_column[index] = str(getattr(word, 'text', ''))[:24]
                break
    result = []
    header_texts = {normalize(name) for name in header_by_column.values() if name}
    for element in elements:
        row = row_index.get(element.id, 0)
        column = 0
        header = ''
        if row and columns:
            for index, (left, right) in enumerate(columns, start=1):
                if left <= element.center[0] <= right:
                    column = index
                    header = header_by_column.get(index, '')
                    break
        if row or column:
            element = replace(element, row=row, column=column, header=header)
        result.append(element)
    # Text printed on the same line as a detected grid cell is that cell's
    # value; the detected cell is the anchor, so row/column never guesses.
    anchors = [element for element in result if element.row]
    for index, element in enumerate(result):
        if element.row or element.kind not in ('label', 'value') or not anchors:
            continue
        if normalize(element.name) in header_texts:
            continue
        anchor = min(anchors, key=lambda item: abs(item.center[1] - element.center[1]))
        if abs(anchor.center[1] - element.center[1]) > max(6, anchor.bounds[3] / 2 + 2):
            continue
        result[index] = replace(element, kind='cell', role='grid', row=anchor.row,
                                column=anchor.column, header=anchor.header,
                                value=str(element.value or element.name))
    return result

def grid_from_bands(bands, window, words=()):
    """Grid band for a window kind, with the top text row of the band as headers."""
    wanted = {'sale': 'luoi_hang', 'find': 'noi_dung'}.get(window)
    for name, bounds in bands or ():
        if name != wanted:
            continue
        x, y, width, height = bounds
        rows = {}
        for word in words:
            cy, cx = getattr(word, 'cy', None), getattr(word, 'cx', None)
            if cy is None or cx is None or not (y <= cy <= y + min(64, height)):
                continue
            if not (x <= cx <= x + width):
                continue
            rows.setdefault(round(cy / 8), []).append(word)
        headers = {}
        if rows:
            first = min(rows.values(), key=lambda items: min(item.cy for item in items))
            for word in sorted(first, key=lambda item: item.cx):
                headers[f'cot{len(headers) + 1}:{str(getattr(word, "text", ""))[:24]}'] = word
        return {'bounds': bounds, 'headers': headers or None}
    return None

def region_of(bounds, bands):
    x, y, w, h = bounds
    cx, cy = x + w / 2, y + h / 2
    for name, (bx, by, bw, bh) in bands or ():
        if bx <= cx <= bx + bw and by <= cy <= by + bh:
            return name
    return 'khac'

def effect_signature(element):
    return f'{element.kind}|{normalize(element.name)[:40]}|{element.region}'

VERIFY_HINTS = {
    'button': 'chờ cửa sổ/menu mới rồi đọc lại vùng vừa đổi',
    'tab': 'đọc lại dải tab và vùng nội dung của tab mới',
    'checkbox': 'đọc lại dấu tick của ô cùng nhãn',
    'radio': 'đọc lại ô được chọn trong nhóm',
    'combo': 'mở danh sách, chọn giá trị rồi đọc lại giá trị ô',
    'edit': 'đọc lại giá trị ô sau khi nhập (MISA có thể tự sửa định dạng)',
    'grid': 'đọc lại số dòng và các dòng đang hiển thị',
    'row': 'đọc lại form sau khi chọn dòng',
    'cell': 'đọc lại giá trị ô trong bảng',
    'menu_item': 'chờ hộp thoại/màn hình mới rồi đọc lại vùng vừa đổi',
}

def plan_element(element, action='click', value=None):
    """What would happen if this action ran on this element (no input sent)."""
    data = element if isinstance(element, dict) else (element.to_dict() if element else None)
    plan = {'element': data, 'action': action, 'value': value,
            'allowed': False, 'blockers': [], 'verify': ''}
    if not data:
        plan['blockers'].append('không tìm thấy phần tử trong bản đồ hiện tại')
        return plan
    actions = tuple(data.get('actions') or ())
    if not data.get('enabled', True):
        plan['blockers'].append('phần tử đang bị vô hiệu hoá')
    if not actions:
        plan['blockers'].append(f"kind `{data.get('kind')}` chỉ để đọc, không tương tác được")
    elif action not in actions:
        plan['blockers'].append(f"`{data.get('kind')}` không hỗ trợ `{action}` (chỉ có "
                                + ', '.join(actions) + ')')
    plan['allowed'] = not plan['blockers']
    plan['effect'] = data.get('effect', '')
    plan['verify'] = VERIFY_HINTS.get(data.get('kind'), 'đọc lại vùng vừa đổi trước khi kết luận')
    if data.get('cell'):
        plan['cell'] = data['cell']
    return plan

def plan_in_map(element_map, name='', *, kind='', action='click', value=None, element_id=''):
    """Find one element in a serialized map and validate an action on it."""
    elements = list((element_map or {}).get('elements') or [])
    chosen = None
    if element_id:
        chosen = next((item for item in elements if item.get('id') == element_id), None)
    if chosen is None and name:
        wanted = label_key(name)
        def matches(field):
            return [item for item in elements
                    if (not kind or item.get('kind') == kind)
                    and (scalar_text(item.get(field, '')) == scalar_text(name) if field == 'value'
                         else label_key(item.get(field, '')) == wanted)]

        # Prefer a named control that can actually be used; a cell that merely
        # displays the text as its value comes next, and a plain label last.
        named = matches('name')
        exact = ([item for item in named if item.get('interactive')]
                 or matches('value') or named)
        chosen = exact[0] if len(exact) == 1 else None
        # A serialized map has no current checkpoint/region lease. It cannot
        # authorize fuzzy actions or choose one of several exact candidates.
    return plan_element(chosen, action, value)

def effects_path():
    import os
    from pathlib import Path

    folder = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'MisaInvoiceTool'
    try:
        folder.mkdir(parents=True, exist_ok=True)
    except OSError:
        pass
    return folder / 'effects.json'

def load_effects():
    """Effects learned from real runs, keyed by kind|name|region."""
    import json

    try:
        data = json.loads(effects_path().read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return {}
    return data if isinstance(data, dict) else {}

def element_from_control(control, window='', *, source='native'):
    """Build an Element from an observed driver control."""
    name = getattr(control, 'name', '') or getattr(control, 'id', '')
    bounds = tuple(getattr(control, 'bounds', (0, 0, 1, 1)))
    kind = NATIVE_KINDS.get(getattr(control, 'kind', 50020), 'label')
    role = getattr(control, 'target', {}) or {}
    role = role.get('type', '') or getattr(control, 'role', '') or window
    value = getattr(control, 'observed_value', None)
    if value is None:
        try:
            value = control.value
        except Exception:
            value = ''
    value = str(value or '')[:120]
    source = getattr(control, 'source', source)
    return Element(id=element_id(kind, name, bounds), name=name, kind=kind, role=role,
                   bounds=bounds, value=value, selected=bool(getattr(control, 'selected', False)),
                   toggle=int(getattr(control, 'toggle', 0) or 0),
                   enabled=bool(getattr(control, 'enabled', True)),
                   source=source,
                   token=getattr(control, 'token', '') or '',
                   selector=getattr(control, 'selector', None),
                   patterns=tuple(getattr(control, 'patterns', ()) or ()))

def _covered(bounds, controls):
    left, top, width, height = bounds
    for control in controls:
        x, y, w, h = control.bounds
        if x - 4 <= left and top >= y - 6 and left + width <= x + w + 4 and top + height <= y + h + 6:
            return True
    return False

def build_map(controls, words=(), *, window='', width=0, height=0, source='native', observed_at='',
              rectangles=(), picture=None, bands=(), knowledge=None, grid=None):
    """One element map per window frame: driver controls plus remaining text."""
    elements = [element_from_control(control, window, source=source) for control in controls]
    for word in words:
        box = tuple(getattr(word, 'box', (0, 0, 0, 0)))
        if _covered(box, elements):
            continue
        if any(_inside(box, candidate.bounds) for candidate in elements if candidate.kind == 'edit'):
            continue
        name = str(getattr(word, 'text', '')).strip()
        if not name:
            continue
        kind = classify_word(name)
        elements.append(Element(id=element_id(kind, name, box), name=name, kind=kind,
                                role=window, bounds=box, value=name, source=source))
    unique = {}
    for element in elements:
        located = replace(element, region=region_of(element.bounds, bands))
        located = replace(located, learned=(knowledge or {}).get(effect_signature(located), ''))
        unique.setdefault(located.id, located)
    elements = list(unique.values())
    elements = _dedupe(elements)
    if grid and grid.get('bounds'):
        elements = annotate_grid(elements, grid['bounds'], picture, headers=grid.get('headers') or ())
    return ElementMap(window=window, width=width, height=height,
                      elements=tuple(elements), source=source, bands=tuple(bands or ()),
                      observed_at=observed_at or datetime.now(timezone.utc).isoformat())

@dataclass(frozen=True)
class ElementMap:
    window: str
    width: int
    height: int
    elements: tuple
    source: str = 'native'
    observed_at: str = ''
    bands: tuple = ()

    @property
    def interactive(self):
        return tuple(e for e in self.elements if e.interactive)

    @property
    def digest(self):
        data = ';'.join(f'{e.id}:{e.kind}:{e.value[:24]}' for e in sorted(self.elements, key=lambda e: e.id))
        return hashlib.blake2s(data.encode('utf-8'), digest_size=8).hexdigest()

    @property
    def regions(self):
        summary = {}
        for element in self.elements:
            item = summary.setdefault(element.region, {'elements': 0, 'interactive': 0})
            item['elements'] += 1
            if element.interactive:
                item['interactive'] += 1
        return summary

    def to_dict(self, *, include_text=True):
        return {'window': self.window, 'width': self.width, 'height': self.height,
                'source': self.source, 'observed_at': self.observed_at, 'digest': self.digest,
                'counts': self.counts(), 'regions': self.regions,
                'bands': [{'name': name, 'bounds': [round(v) for v in bounds]}
                          for name, bounds in self.bands],
                'elements': [e.to_dict() for e in self.elements
                             if include_text or e.interactive]}

    def counts(self):
        counts = {}
        for element in self.elements:
            counts[element.kind] = counts.get(element.kind, 0) + 1
        counts['interactive'] = len(self.interactive)
        return counts

    def render_text(self, *, interactive_only=False, limit=120):
        """Compact text an agent can reason about (id, kind, bounds, actions, effect)."""
        rows = [f'# Bản đồ phần tử cửa sổ `{self.window}` ({self.width}x{self.height}) · '
                f'nguồn {self.source} · {len(self.interactive)} phần tử tương tác được']
        if self.regions:
            rows.append('Vùng: ' + ' · '.join(
                f'{name}: {item["interactive"]}/{item["elements"]} tương tác'
                for name, item in sorted(self.regions.items())))
        unknown = [element for element in self.elements if element.unknown_effect]
        if unknown:
            named = [element for element in unknown if element.name]
            summary = ' · '.join(f'{element.kind} “{element.name[:30]}”' for element in named[:6])
            rows.append(f'Chưa rõ hiệu ứng ({len(unknown)}): '
                        + (summary or 'toàn bộ là ô nhập/lưới chưa đặt tên'))
        for element in sorted(self.elements, key=lambda e: (e.bounds[1], e.bounds[0])):
            if interactive_only and not element.interactive:
                continue
            x, y, w, h = (round(v) for v in element.bounds)
            actions = ','.join(element.actions) or '-'
            line = (f'{element.id} {element.kind:9s} {element.region:15s} [{x:4d},{y:4d},{w:4d},{h:3d}] '
                    f'“{element.name[:60]}”')
            if element.value and element.value != element.name:
                line += f' = “{element.value[:40]}”'
            if element.interactive:
                line += f' · actions={actions} · khi tác động: {element.effect}'
            if element.row or element.column:
                line += (f' · lưới: dòng {element.row} cột {element.column}'
                         + (f' ({element.header})' if element.header else ''))
            rows.append(line)
            if len(rows) >= limit:
                rows.append(f'… còn {len(self.elements) - limit} phần tử nữa trong element-map.json')
                break
        return '\n'.join(rows)
