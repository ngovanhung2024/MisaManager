"""Inventory actions resolved through current UIA/Win32 state."""
import re

from .domain import Cancelled, NeedsAttention
from .verification import text_key
from .performance import tracked
from .observation_wait import read_until
from . import win32


class InventoryScreen:
    def _item_control(self, **kwargs):
        from .screen_win32 import ScreenControl
        return ScreenControl(self, **kwargs)

    def _item_step(self):
        if not self.checkpoint.startswith('add.items.'):
            raise NeedsAttention('Thao tác vật tư không thuộc bước hiện tại.')

    def item_count(self, sale=True):
        self._item_step() if sale else None
        count = self._uia_sale_item_count() if sale else self._uia_find_count()
        if count is None:
            raise NeedsAttention('UIA khong cung cap so dong hien tai; dung de tranh suy dien tu man hinh.')
        return count

    def _uia_item_grid_data(self, *, resolve_prompt=False, required_tab='1. Hàng tiền',
                            required_columns=('mahang', 'tenhang')):
        """Build row structure from one complete current UIA grid snapshot."""
        if (resolve_prompt or required_tab not in ('1. Hàng tiền', '2. Thuế')
                or not self.is_window('sale')):
            return None
        supported = (('thuesuatgtgt', 'tienthuegtgt') if required_tab == '2. Thuế'
                     else ('mahang', 'tenhang', 'tktienchiphi', 'tkdoanhthu',
                           'dvt', 'soluong', 'dongia', 'thanhtien'))
        columns = tuple(dict.fromkeys(required_columns))
        if not columns or any(column not in supported for column in columns):
            return None
        count = self._uia_sale_item_count(required_tab=required_tab)
        if count is None or count < 0:
            return None
        if count == 0:
            # A new invoice has a verified empty grid plus its native add-row
            # prompt; this is the valid starting state for item row 1.
            self._item_row_proof = {
                'count': 0, 'row': None, 'frame_id': self._frame_id,
                'input_epoch': self.input_epoch, 'checkpoint': self.checkpoint,
                'window': (self.window.hwnd, self.window.pid),
                'tab': required_tab, 'prompt': True, 'source': 'UIA3.GridPattern',
            }
            return {}, {}, 0, [], True
        rows = []
        for logical in range(count):
            cells = {}
            for column in columns:
                control = self._uia_sale_item_cell(
                    logical + 1, column, semantic_only=True,
                    expected_total=count)
                if control is None:
                    return None
                cells[column] = control
            if any(column not in cells for column in required_columns):
                return None
            rows.append(cells)
        first = rows[0]
        column_bounds = {}
        headers = {}
        from types import SimpleNamespace
        for column in columns:
            x, y, width, height = first[column].bounds
            if width <= 0 or height <= 0:
                return None
            for row in rows[1:]:
                rx, _, rw, rh = row[column].bounds
                if (abs(rx - x) > 2 or abs(rw - width) > 2
                        or abs(rh - height) > 2):
                    return None
            header_top = max(0, y - height - 3)
            column_bounds[column] = (x, x + width)
            headers[column] = SimpleNamespace(
                box=(x, header_top, x + width, header_top + height),
                cx=x + width / 2, cy=header_top + height / 2)
        anchor_column = 'mahang' if 'mahang' in columns else columns[0]
        groups = [(round(row[anchor_column].bounds[1]
                         + row[anchor_column].bounds[3] / 2), [])
                  for row in rows]
        self._item_row_proof = {
            'count': count, 'row': None, 'frame_id': self._frame_id,
            'input_epoch': self.input_epoch, 'checkpoint': self.checkpoint,
            'window': (self.window.hwnd, self.window.pid),
            'tab': required_tab, 'prompt': False, 'source': 'UIA3.GridPattern',
        }
        return headers, column_bounds, count, groups, None

    def _item_grid_data(self, *, resolve_prompt=False, required_tab='1. Hàng tiền',
                        required_columns=('mahang', 'tenhang')):
        result = self._uia_item_grid_data(resolve_prompt=resolve_prompt,
                                          required_tab=required_tab,
                                          required_columns=required_columns)
        if result is None:
            raise NeedsAttention('UIA khong cung cap day du du lieu luoi hien tai.')
        return result

    def sale_item_cell(self, row, column, *, code='', new=False):
        self._item_step()
        control = self._uia_sale_item_cell(row, column, code=code, new=new,
                                           semantic_only=False)
        if control is not None:
            return control
        raise NeedsAttention('UIA/Win32 khong cung cap o hang hien tai.')

    def _inventory_menu(self, action):
        self._item_step()
        row = int(getattr(self, '_active_item_row', 0) or 0)
        if not row:
            raise NeedsAttention('Chua co dong vat tu native hien tai.')
        control = self._uia_inventory_menu_current(action, row)
        if control is not None:
            return control
        raise NeedsAttention('UIA/Win32 khong cung cap nut danh muc hien tai.')

    def inventory_menu_open(self):
        row = int(getattr(self, '_active_item_row', 0) or 0)
        proof = getattr(self, '_popup_tab_proof', None) or {}
        if not row or not self.is_window('sale'):
            return False
        try:
            self._native_frame('sale', fresh=True, )
            tab = self._uia_tab_state('1. Hàng tiền')
            if not tab or not tab.get('selected'):
                return False
            if self._uia_inventory_menu_current('new', row) is not None:
                self._inventory_menu_open = True
                self.last_action['inventory_popup_source'] = 'UIA3.ButtonPair'
                return True
        except Cancelled:
            raise
        except (NeedsAttention, win32.WindowError):
            return False
        return False

    def open_inventory_menu(self, row, *, new, action='search'):
        self._item_step()
        if action not in ('new', 'search'):
            raise NeedsAttention('Thao tac danh muc khong hop le.')
        if (getattr(self, 'resume_auto_focus', False)
                and not getattr(self, 'direct_control', False)
                and not win32.is_foreground(self.window)):
            # Resume is an explicit continuation of this bound voucher. Focus
            # only that recorded MISA window before rebuilding native targets.
            win32.activate(self.window)
            self._invalidate()
        self._active_item_row = row
        self._popup_tab_proof = None
        cell = self.sale_item_cell(row, 'mahang', new=new)
        proof = dict(self._item_row_proof or {})
        proof.update(anchor_bounds=tuple(cell.bounds),
                     anchor_capture_box=tuple(self._frame[0]),
                     anchor_frame_id=self._frame_id,
                     anchor_row_token=((cell.target or {}).get('uia_row_parent') or {}).get('token'),
                     anchor_row_bounds=((cell.target or {}).get('uia_row_parent') or {}).get('bounds'),
                     anchor_source='UIA3' if (cell.target or {}).get('uia_element') else 'Win32')
        self._popup_tab_proof = proof

        def opened():
            return self.inventory_menu_open()

        popup_open = opened()
        if not popup_open:
            if new:
                self.click(cell)
            else:
                arrow = self._item_control(name='Mo danh muc ma hang', kind=50000,
                                            bounds=cell.bounds,
                                            target={'type': 'arrow', 'parent': cell.target})
                self.click(arrow)
            try:
                popup_open = read_until(opened, bool, self.cancel, timeout=5.0,
                                        interval=.02, label='menu vat tu')
            except NeedsAttention:
                if not new and action != 'search':
                    raise
                popup_open = False
        if not popup_open and new:
            # Some MISA builds expose ExpandCollapse but only open this editor
            # through its native mouse surface. Retry once after a fresh proof
            # that the first action did not open the popup.
            self._native_frame('sale', fresh=True)
            current_cell = self.sale_item_cell(row, 'mahang', new=new)
            current_row = ((current_cell.target or {}).get('uia_row_parent') or {})
            if (proof.get('anchor_row_token')
                    and current_row.get('token') != proof['anchor_row_token']):
                raise NeedsAttention('Dòng vật tư đã đổi sau khi đọc lại; không gửi lại input.')
            anchor = tuple(current_cell.bounds)
            try:
                win32.direct_click(
                    self.window,
                    (anchor[0] + anchor[2] / 2, anchor[1] + anchor[3] / 2))
            except Cancelled:
                raise
            except win32.WindowError as error:
                if isinstance(error, win32.InputUncertain):
                    self._input_sent()
                raise NeedsAttention(str(error)) from error
            self._input_sent(surface_unchanged=False)
            self.last_action.update(status='clicked', input_sent=True,
                                    input_method='Win32.SendInput',
                                    fallback='sale_item_cell_native_click')
            self._remember_action()
            self._invalidate()
            try:
                popup_open = read_until(opened, bool, self.cancel, timeout=5.0,
                                        interval=.02, label='menu vat tu')
            except NeedsAttention:
                popup_open = False
        if not popup_open and (action == 'search' or (new and action == 'new')):
            # Some MISA rows do not expose a stable inline button pair after
            # ExpandCollapse. Use the documented F3/F9 shortcut on the same
            # proven cell instead of guessing from partial button geometry.
            current_cell = self.sale_item_cell(row, 'mahang', new=new)
            if not new:
                # UIA Focus can stop at the grid's ComboBox wrapper. A guarded
                # native click on the proven cell creates the editor/caret that
                # the function-key focus check requires.
                capture_box = tuple(self._frame[0])
                anchor = tuple(current_cell.bounds)
                self._guard_frame()
                try:
                    win32.direct_click(
                        self.window,
                        (anchor[0] + anchor[2] / 2, anchor[1] + anchor[3] / 2))
                except Cancelled:
                    raise
                except win32.WindowError as error:
                    if isinstance(error, win32.InputUncertain):
                        self._input_sent()
                    raise NeedsAttention(str(error)) from error
                self._input_sent(surface_unchanged=False)
                self.last_action.update(status='clicked', input_sent=True,
                                        input_method='Win32.SendInput',
                                        fallback='existing_sale_item_cell_focus')
                self._remember_action()
                self._invalidate()
                current_cell = self.sale_item_cell(row, 'mahang', new=new)
            if self.ledger:
                # The shortcut opens the dialog without an inventory_menu target,
                # so register its run-owned surface before sending the key.
                self.ledger.before_open(self, 'find' if action == 'search' else 'new_item')
            shortcut = 0x72 if action == 'search' else 0x78
            capture_box = tuple(self._frame[0])
            self.key(shortcut, focus_guard=lambda: self._require_input_focus(
                current_cell, capture_box, label='Ma hang'))
            self._shortcut_inventory_action = action
            self._inventory_menu_open = True
            return
        if not popup_open:
            raise NeedsAttention('Chua xac nhan menu vat tu bang native UIA/Win32.')
        self._inventory_menu_open = True

    def inventory_action(self, action):
        if getattr(self, '_shortcut_inventory_action', None) == action:
            self._shortcut_inventory_action = None
            self._inventory_menu_open = False
            self._popup_tab_proof = None
            self.wait_window('find' if action == 'search' else 'new_item', seconds=12)
            return
        try:
            self.click(self._inventory_menu(action), inventory_menu=True)
        finally:
            self._inventory_menu_open = False
            self._popup_tab_proof = None
        self.wait_window('find' if action == 'search' else 'new_item', seconds=12)

    def find_filter(self, label):
        if not self._locating:
            uia = self._uia_find_filter(label)
            if uia is not None:
                return uia
        self.sync(kind='find')
        return self.unique([c for c in self._find_grid_controls()
                            if c.role == 'find_filter' and c.name == label], label)

    def find_candidates(self, *, fresh=True):
        # Reuse the frame pinned by read_scope during a guarded read. Standalone
        # callers keep the fresh-frame behavior so stale result rows are never
        # accepted after an input transition.
        self._native_frame('find', fresh=fresh)
        rows = self._uia_find_rows()
        count = self._uia_find_count()
        if rows is None:
            raise NeedsAttention('UIA khong cung cap day du ket qua tim vat tu hien tai.')
        # Some MISA builds omit the footer count for a single result. The
        # native row reader already proves contiguous indexes and every cell;
        # use that complete row sequence when the optional count label is absent.
        if count is None:
            count = len(rows)
        if len(rows) != count:
            raise NeedsAttention('UIA khong cung cap day du ket qua tim vat tu hien tai.')
        if count > 12:
            raise NeedsAttention(f'Co {count} ket qua; ten tim kiem chua du cu the.')
        return rows

    def _catalog_options(self, item_id):
        parent = self.field((item_id,), self.FIELD_LAYOUT[item_id][0])
        return parent, None

    def _catalog_option(self, item_id, text):
        self._item_step()
        self._native_frame('new_item', fresh=True, )
        parent = self.field((item_id,), self.FIELD_LAYOUT[item_id][0])
        control = self._uia_catalog_option_by_parent(text, None, parent)
        if control is None:
            raise NeedsAttention('UIA khong cung cap lua chon danh muc hien tai.')
        control.target.update(id=item_id)
        return control

    def select_catalog(self, item_id, value):
        self._item_step()
        parent = self.field((item_id,), self.FIELD_LAYOUT[item_id][0])
        # These catalog editors accept a scalar value and only persist it
        # after the editor commits. Their popup selection can remain merely
        # highlighted or be reset by the next combo interaction.
        if item_id in ('cboUnitID', 'cboStockID'):
            try:
                rule = 'unit' if item_id == 'cboUnitID' else 'exact'
                fresh, capture_box = self._type_value(parent, value, readback_rule=rule)
                self.key(0x09, focus_guard=lambda: self._require_input_focus(
                    fresh, capture_box, label=fresh.name))
                self.last_action.update(status='committed_unverified',
                                        commit_key=0x09, catalog_commit='Win32.SendInput:Tab')
                self._remember_action()
                self._invalidate()
                return
            except NeedsAttention:
                # Continue to the guarded popup path only when the scalar
                # editor rejected the write before sending input.
                if self.last_action.get('input_sent') is not False:
                    raise
                self._invalidate()
                parent = self.field((item_id,), self.FIELD_LAYOUT[item_id][0])
        self.click(self._item_control(name='Mo ' + parent.name, kind=50000,
                                      bounds=parent.bounds,
                                      target={'type': 'arrow', 'parent': self._target_for(parent)}))
        for _ in range(40):
            self._check()
            self._native_frame('new_item', fresh=True, )
            parent = self.field((item_id,), self.FIELD_LAYOUT[item_id][0])
            target = self._uia_catalog_option_by_parent(value, None, parent)
            if target is not None:
                target.target.update(id=item_id)
                self.click(target)
                # MISA's value-list SelectionItem can move the highlighted
                # row without committing the combo's scalar value.  A native
                # click on the selected row performs the popup's default
                # combo commit; only use it while the owned popup remains
                # foreground and the option bounds are still available.
                try:
                    foreground = win32.foreground_for_process(self.pid)
                    if (foreground.hwnd != self.window.hwnd
                            and win32.is_owned_by(foreground, self.window.hwnd)):
                        saved = (target.target or {}).get('uia_element') or {}
                        box = saved.get('bounds') or {}
                        values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                        if not all(isinstance(value, (int, float)) for value in values):
                            raise NeedsAttention('Catalog option lost native bounds before commit.')
                        x, y, width, height = values
                        win32.direct_click(
                            foreground, (x + width / 2, y + height / 2),
                            coordinate_space='screen')
                        self._input_sent(surface_unchanged=False)
                        self._metrics['input_clicks'] += 1
                        self.last_action.update(catalog_commit='Win32.SendInput:click')
                        # SelectionItem can leave a unit highlighted without
                        # committing the combo's scalar value.
                        if item_id == 'cboUnitID':
                            self.key(0x0D)
                            self.last_action['catalog_commit'] = 'Win32.SendInput:click+Enter'
                        self._remember_action()
                        self._invalidate()
                except win32.InputUncertain as error:
                    self._input_sent(surface_unchanged=False)
                    raise NeedsAttention(str(error)) from error
                except win32.WindowError:
                    # The popup can close between selection and the guarded
                    # commit; the following scalar readback remains decisive.
                    pass
                return
            scroll_target = self._uia_catalog_scroll_target(
                None, {'type': 'catalog_scroll', 'id': item_id}, parent)
            if scroll_target is None:
                raise NeedsAttention('UIA khong cung cap danh sach danh muc de cuon.')
            self.scroll_inventory({'type': 'catalog_scroll', 'id': item_id}, -3)
        raise NeedsAttention('Da cuon 40 lan nhung chua thay danh muc ' + str(value))

    def select_sale_tax_rate(self, control, value):
        """Select a sale-row VAT option; the editor's ValuePattern is not a commit path."""
        self._item_step()
        if not self.is_window('sale') or control.target.get('type') != 'sale_item_cell' \
                or control.target.get('column') != 'thuesuatgtgt' \
                or not self.checkpoint.endswith('.tax'):
            raise NeedsAttention('O thue GTGT khong thuoc buoc nhap thue hien tai.')
        from .uia_bridge import EngineError

        fresh = self._prepare_target(control)
        element = (fresh.target or {}).get('uia_element') or {}
        if 'ExpandCollapse' not in set(element.get('patterns') or ()):
            raise NeedsAttention('UIA khong cung cap popup thue GTGT hien tai.')
        if self._uia_readback is None:
            from .uia_readback import UiaReadback
            self._uia_readback = UiaReadback(self.cancel)
        try:
            strategy = self._uia_readback.invoke(self, fresh, action='expand')
        except EngineError as error:
            self.last_action.update(status='blocked', input_sent=False,
                                    input_method='UIA3.' + str(error))
            self._remember_action()
            self._invalidate()
            raise NeedsAttention(str(error)) from error
        self._input_sent(surface_unchanged=False)
        self.last_action.update(status='expanded', input_sent=True,
                                input_method=strategy)
        self._remember_action()
        self._invalidate()

        row = control.target.get('row')
        code = control.target.get('code', '')
        native_open_attempted = False
        for _ in range(20):
            self._check()
            self._native_frame('sale', fresh=True)
            parent = self.sale_item_cell(row, 'thuesuatgtgt', code=code)
            target = self._uia_catalog_option_by_parent(value, None, parent)
            if target is None:
                if not native_open_attempted:
                    native_open_attempted = True
                    self._guard_frame()
                    try:
                        anchor = tuple(parent.bounds)
                        win32.direct_click(
                            self.window,
                            (anchor[0] + anchor[2] / 2, anchor[1] + anchor[3] / 2))
                    except win32.InputUncertain as error:
                        self._input_sent(surface_unchanged=False)
                        raise NeedsAttention(str(error)) from error
                    except win32.WindowError as error:
                        raise NeedsAttention(str(error)) from error
                    self._input_sent(surface_unchanged=False)
                    self.last_action.update(status='clicked', input_sent=True,
                                            input_method='Win32.SendInput',
                                            fallback='sale_tax_combo_native_click')
                    self._remember_action()
                    self._invalidate()
                continue
            self._click_resolved(target)
            # SelectionItem.Select can only highlight this custom combo's row;
            # a guarded native click on the same popup option performs commit.
            try:
                foreground = win32.foreground_for_process(self.pid)
                saved = (target.target or {}).get('uia_element') or {}
                box = saved.get('bounds') or {}
                values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
                owned_popup = (foreground.hwnd != self.window.hwnd
                               and win32.is_owned_by(foreground, self.window.hwnd))
                if owned_popup:
                    if not all(isinstance(item, (int, float)) for item in values):
                        raise NeedsAttention('Popup thue GTGT mat bounds native truoc khi commit.')
                    x, y, width, height = values
                    win32.direct_click(
                        foreground, (x + width / 2, y + height / 2),
                        coordinate_space='screen')
                    self._input_sent(surface_unchanged=False)
                    self._metrics['input_clicks'] += 1
                    self.last_action.update(catalog_commit='Win32.SendInput:click')
            except win32.InputUncertain as error:
                self._input_sent(surface_unchanged=False)
                raise NeedsAttention(str(error)) from error
            except win32.WindowError:
                # The popup may close immediately after SelectionItem.Select;
                # the workflow's fresh rate and tax readback remains decisive.
                pass
            self.last_action.update(status='committed_unverified', input_sent=True,
                                    tax_rate_selection=value)
            self._remember_action()
            self._invalidate()
            return
        # Some revisions paint the VAT popup as a native list that UIA does not
        # expose. Fall back to the same guarded child-editor typing path used
        # by the working grid cells, committing with Enter so MISA resolves the
        # typed rate instead of retaining the old selection.
        self._native_frame('sale', fresh=True)
        editor = self.sale_item_cell(row, 'thuesuatgtgt', code=code)
        anchor = tuple(editor.bounds)
        try:
            win32.direct_click(
                self.window,
                (anchor[0] + anchor[2] / 2, anchor[1] + anchor[3] / 2))
        except win32.InputUncertain as error:
            self._input_sent(surface_unchanged=False)
            raise NeedsAttention(str(error)) from error
        except win32.WindowError as error:
            raise NeedsAttention(str(error)) from error
        self._input_sent(surface_unchanged=False)
        self.last_action.update(status='clicked', input_sent=True,
                                input_method='Win32.SendInput',
                                fallback='sale_tax_editor_native_click')
        self._remember_action()
        self._invalidate()
        self._native_frame('sale', fresh=True)
        editor = self.sale_item_cell(row, 'thuesuatgtgt', code=code)
        capture_box = tuple(self._frame[0])
        focus_guard = lambda: self._require_input_focus(editor, capture_box, label=editor.name)
        self._require_input_focus(editor, capture_box, label=editor.name)
        try:
            with win32.prepared_clipboard_text(str(value)) as sequence:
                self.key(0x41, ctrl=True, focus_guard=focus_guard)
                win32.paste_text(self.window, sequence, focus_guard=focus_guard)
            self.key(0x0D, focus_guard=focus_guard)
        except win32.WindowError as error:
            if isinstance(error, win32.InputUncertain):
                self._input_sent(surface_unchanged=False)
            raise NeedsAttention(str(error)) from error
        self.last_action.update(status='committed_unverified', input_sent=True,
                                input_method='Win32.SendInput:paste+Enter',
                                tax_rate_selection=value)
        self._remember_action()
        self._invalidate()

    def _inventory_scroll_target(self, target):
        self._item_step()
        if target['type'] == 'catalog_scroll':
            self._native_frame('new_item', fresh=True, )
            parent = self.field((target['id'],), self.FIELD_LAYOUT[target['id']][0])
            control = self._uia_catalog_scroll_target(None, target, parent)
            if control is None:
                raise NeedsAttention('UIA khong cung cap vung cuon danh muc hien tai.')
            return control
        control = self._uia_sale_scroll_target_current(target)
        if control is not None:
            return control
        self._native_frame('sale', fresh=True, )
        grid = self._uia_grid(True)
        if grid is None:
            raise NeedsAttention('UIA khong cung cap vung cuon bang hang hien tai.')
        element = (grid.target or {}).get('uia_element')
        grid.name = 'Cuon bang hang hien tai'
        grid.target = dict(target, uia=True, uia_element=element,
                          uia_automation_id=(element or {}).get('id') or 'grdDetail')
        return grid

    def scroll_inventory(self, target, ticks):
        control = self._inventory_scroll_target(target)
        fresh = self._prepare_target(control)
        uia_strategy = None
        from .uia_bridge import EngineError
        try:
            if (fresh.target.get('type') in ('item_grid_scroll', 'catalog_scroll')
                    and fresh.target.get('uia_element')):
                if self._uia_readback is None:
                    from .uia_readback import UiaReadback
                    self._uia_readback = UiaReadback(self.cancel)
                direction = 'down' if ticks < 0 else 'up'
                try:
                    uia_strategy = self._uia_readback.invoke(
                        self, fresh, action='scroll',
                        value=f'{direction}:{max(1, min(abs(int(ticks)), 20))}')
                except EngineError as error:
                    if error.uncertain:
                        raise
                    self.last_action['uia_scroll_fallback'] = error.code + ': ' + str(error)
                    uia_strategy = None
            if uia_strategy is None:
                raise NeedsAttention('UIA không cung cấp Scroll pattern cho danh sách hiện tại.')
        except (win32.WindowError, EngineError) as error:
            uncertain = (isinstance(error, win32.InputUncertain)
                         or bool(getattr(error, 'uncertain', False)))
            if uncertain:
                self._input_sent()
            self.last_action.update(status='uncertain' if uncertain else 'blocked',
                                    input_sent=None if uncertain else False, error=str(error))
            raise NeedsAttention(str(error)) from error
        else:
            self._input_sent()
            self._metrics['input_scrolls'] = self._metrics.get('input_scrolls', 0) + 1
            if uia_strategy:
                self._metrics['uia_scrolls'] = self._metrics.get('uia_scrolls', 0) + 1
            self.last_action.update(status='scrolled', input_sent=True, scroll_ticks=ticks,
                                    input_method=uia_strategy)
        finally:
            self._remember_action()
            self._invalidate()

    def item_edit(self, control, value):
        self._item_step()
        kind = control.target.get('type')
        allowed = ((self.is_window('find') and control.role == 'find_filter')
                   or (self.is_window('new_item') and control.id in
                       ('txtInventoryItemCode', 'txtInventoryItemName'))
                   or (self.is_window('sale') and kind == 'sale_item_cell'
                       and (control.target['column'] in ('soluong', 'thanhtien')
                            or (control.target['column'] == 'thuesuatgtgt'
                                and self.checkpoint.endswith('.tax')))))
        if not allowed:
            raise NeedsAttention('Ô không thuộc phần hàng hóa được phép nhập.')
        try:
            if control.target.get('column') == 'thuesuatgtgt':
                self.select_sale_tax_rate(control, value)
                return
            fresh, capture_box = self._type_value(control, value)
            if self.last_action.get('status') == 'already_matches':
                self._click_resolved(fresh)
            self.key(0x09, focus_guard=lambda: self._require_input_focus(
                fresh, capture_box, label=fresh.name))
            self.last_action.update(status='committed_unverified', commit_key=0x09)
        finally:
            self._remember_action()
            self._invalidate()

    def item_button(self, name, kind):
        self._item_step()
        control = self._uia_static_target(dict(type='item_dialog_button', text=name,
                                                window_kind=kind))
        if control is not None:
            return control
        raise NeedsAttention('UIA/Win32 khong cung cap nut dialog hien tai.')

    def resolve_inventory_target(self, target):
        kind = target['type']
        if kind == 'sale_item_cell':
            return self.sale_item_cell(target['row'], target['column'],
                                       code=target['code'], new=target['new'])
        if kind == 'inventory_menu':
            return self._inventory_menu(target['action'])
        if kind == 'catalog_option':
            return self._catalog_option(target['id'], target['text'])
        if kind in ('catalog_scroll', 'item_grid_scroll'):
            return self._inventory_scroll_target(target)
        if kind == 'item_dialog_button':
            return self.item_button(target['text'], target['window_kind'])
        return None
