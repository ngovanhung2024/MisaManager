"""FlaUI readback and guarded date input at native UIA targets."""
from datetime import datetime
import math
import re
import uuid

from .domain import Cancelled
from .uia_bridge import EngineError, UiaBridge
from .verification import Reading, utc_now


def _parse_observed_at(value):
    try:
        match = re.fullmatch(
            r'([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})'
            r'(?:\.([0-9]{1,7}))?(Z|[+-][0-9]{2}:[0-5][0-9])', value)
        if match is None:
            raise ValueError('Expected a .NET ISO timestamp with a UTC offset.')
        seconds, fraction, offset = match.groups()
        # .NET emits 0-7 fractional digits; Python 3.10 accepts only 0, 3 or 6.
        # Truncate sub-microsecond ticks so the observation is never made newer.
        if fraction:
            seconds += '.' + fraction[:6].ljust(6, '0')
        return datetime.fromisoformat(seconds + ('+00:00' if offset == 'Z' else offset))
    except (TypeError, ValueError) as error:
        raise EngineError('OBSERVATION_TIMESTAMP', 'Invalid UIA observedAt: ' + repr(value)) from error


class UiaReadback:
    def __init__(self, cancel, bridge=None):
        self.cancel = cancel
        self.bridge = bridge
        self.owns_bridge = bridge is None
        self.window = None
        self._query_cache_frame = ''
        self._query_cache = {}
        self._grid_cache_frame = ''
        self._grid_cache = {}

    def _bind(self, desktop):
        if self.owns_bridge and self.bridge is not None and self.bridge.closed:
            self.close()
        if self.bridge is None:
            self.bridge = UiaBridge(self.cancel)
        identity = (desktop.window.hwnd, desktop.window.pid)
        if self.window == identity and not self.bridge.closed:
            desktop._metrics['uia_bind_reuses'] = desktop._metrics.get('uia_bind_reuses', 0) + 1
            return
        if self.window is None and self.owns_bridge:
            bound = self.bridge.call('connect', {'windowHandle': identity[0], 'processId': identity[1]})
        else:
            bound = self.bridge.call('bind', {'windowHandle': identity[0]})
        if (bound['handle'], bound['processId']) != identity:
            raise EngineError('WRONG_WINDOW', 'UIA bound to another window')
        self.window = identity

    def read(self, desktop, control, context, *, header=False):
        base = dict(context, source='UIA3', observed_at=utc_now())
        try:
            if desktop._frame_window != (desktop.window.hwnd, desktop.window.pid):
                return Reading(**base, usable=False, reason='Native frame belongs to a previous window')
            self._bind(desktop)
            if not header and 'observe_value' not in self.bridge.capabilities:
                raise EngineError('ENGINE_VERSION', 'Engine needs observe_value; rebuild the engine.')
            x, y, width, height = control.bounds
            left, top = desktop._frame[0][:2]
            bounds = dict(x=x + left, y=y + top, width=width, height=height)
            if header:
                observed = self._observe_header(desktop, bounds)
            else:
                observed = None
                saved = control.target.get('uia_element') if control.target else None
                if isinstance(saved, dict) and saved.get('token'):
                    try:
                        observed = self._read_current_token(desktop, control, saved)
                    except EngineError as error:
                        if error.code != 'PATTERN_UNSUPPORTED':
                            raise
                if observed is None:
                    automation_id = control.target.get('uia_automation_id')
                    if not automation_id:
                        automation_id = (control.id if control.id
                                         and not control.id.startswith('find-filter:') else None)
                    observed = self.bridge.call('observeValue', {
                        'bounds': bounds,
                        'automationId': automation_id if control.role in ('field', 'find_filter', 'cell') else None,
                        'row': context['row']})
            base.update(observed_at=_parse_observed_at(observed.get('observedAt')),
                        hwnd=observed['windowId'], pid=observed['processId'])
            if header and observed.get('bounds') is not None:
                box = observed['bounds']
                base['bounds'] = (box['x'] - left, box['y'] - top, box['width'], box['height'])
            status = observed['status']
            reason = observed.get('reason', '')
            return Reading(**base, actual_raw=observed.get('value'), usable=status == 'supported',
                           source_detail=observed.get('valueSource', ''),
                           reason=('unavailable:' if status == 'unavailable' else '') + reason)
        except Cancelled:
            raise
        except EngineError as error:
            optional = error.code in ('ENGINE_MISSING', 'ENGINE_VERSION', 'PATTERN_UNSUPPORTED',
                                      'ENGINE_UNAVAILABLE', 'ENGINE_CLOSED')
            return Reading(**base, usable=False,
                           reason=('unavailable:' if optional else '') + error.code + ': ' + str(error))

    def _read_current_token(self, desktop, control, saved):
        """Read the exact current UIA element before geometry re-resolution."""
        element = self.bridge.call('read', {'token': saved['token']})
        read_only_find_cell = (control.role == 'cell' and desktop.is_window('find'))
        if (element.get('windowId') != desktop.window.hwnd
                or (not element.get('enabled') and not read_only_find_cell)
                or element.get('offscreen')
                or (saved.get('kind') and element.get('kind') != saved.get('kind'))
                or (saved.get('id') and element.get('id') != saved.get('id'))
                or (saved.get('name') and element.get('name') != saved.get('name'))
                or (saved.get('parentToken')
                    and element.get('parentToken') != saved.get('parentToken'))):
            raise EngineError('STALE_VALUE_TARGET', 'UIA value target identity changed before readback.')
        if element.get('bounds') != saved.get('bounds'):
            raise EngineError('UIA_TARGET_MISMATCH', 'UIA value target bounds changed before readback.')
        value = element.get('value')
        if value is None:
            raise EngineError('UIA_TARGET_UNAVAILABLE', 'UIA value target returned no current value.')
        patterns = set(element.get('patterns') or ())
        source = 'ValuePattern' if 'Value' in patterns else 'TextPattern' if 'Text' in patterns else ''
        if not source:
            raise EngineError('PATTERN_UNSUPPORTED', 'UIA value target has no readable value pattern.')
        if re.fullmatch(r'\{?[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\}?',
                        str(value).strip(), re.I):
            # Cached cell tokens can expose only a selected-item GUID. Re-read
            # the same native bounds so the provider can inspect its displayed
            # text descendant before declaring the value unavailable.
            try:
                observed = self.bridge.call('observeValue', {
                    'bounds': element.get('bounds'),
                    'automationId': element.get('id') or None,
                    'row': (control.target or {}).get('row')})
                if observed.get('status') == 'supported':
                    return observed
            except EngineError:
                pass
            return dict(status='unavailable', value=value, valueSource=source,
                        observedAt=utc_now().isoformat(), windowId=element.get('windowId'),
                        processId=desktop.window.pid, bounds=element.get('bounds'),
                        reason='Internal GUID is not displayed text.')
        return dict(status='supported', value=value, valueSource=source,
                    observedAt=utc_now().isoformat(), windowId=element.get('windowId'),
                    processId=desktop.window.pid, bounds=element.get('bounds'), reason='')

    def write_date(self, desktop, control, value):
        """Send one full date; the caller must commit and read fresh evidence."""
        if (control.role != 'field' or not desktop.is_window('sale')
                or desktop.HEADER_VALUE_RULES.get(control.id) != 'date'
                or control.observed_frame_id != desktop._frame_id):
            raise EngineError('INVALID_DATE_TARGET', 'Date input requires the current native date field.')
        desktop.guard(control)
        desktop._guard_frame()
        self._bind(desktop)
        if 'set_observed_date' not in self.bridge.capabilities:
            raise EngineError('ENGINE_VERSION', 'Engine needs set_observed_date; rebuild the engine.')
        x, y, width, height = control.bounds
        left, top, right, bottom = desktop._frame[0]
        target = dict(automationId=control.id,
                      bounds=dict(x=x + left, y=y + top, width=width, height=height),
                      windowBounds=dict(x=left, y=top, width=right - left, height=bottom - top),
                      windowId=desktop.window.hwnd, processId=desktop.window.pid,
                      observedAt=desktop._captured_at.isoformat())
        desktop._require_input_focus(control, desktop._frame[0], label=control.name)
        desktop._guard_frame()
        return self.bridge.execute('set_date', value=value, checkpoint=desktop.checkpoint,
                                   dateTarget=target)

    def write_value(self, desktop, control, value, purpose):
        """Set one current native editor through UIA without replay fallback."""
        if (control.observed_frame_id != desktop._frame_id
                or desktop._frame_window != (desktop.window.hwnd, desktop.window.pid)):
            raise EngineError('STALE_VALUE_TARGET', 'Value input requires the current native field.')
        desktop.guard(control)
        desktop._guard_frame()
        self._bind(desktop)
        if 'set_observed_value' not in self.bridge.capabilities:
            raise EngineError('ENGINE_VERSION', 'Engine needs set_observed_value; rebuild the engine.')
        x, y, width, height = control.bounds
        left, top, right, bottom = desktop._frame[0]
        automation_id = control.target.get('uia_automation_id')
        if not automation_id:
            automation_id = (control.id if control.id
                             and not control.id.startswith('find-filter:') else None)
        target = dict(purpose=purpose, automationId=automation_id,
                      bounds=dict(x=x + left, y=y + top, width=width, height=height),
                      windowBounds=dict(x=left, y=top, width=right - left, height=bottom - top),
                      windowId=desktop.window.hwnd, processId=desktop.window.pid,
                      row=control.target.get('row'), observedAt=desktop._captured_at.isoformat())
        desktop._guard_frame()
        return self.bridge.call('setObservedValue', {
            'target': target, 'value': value, 'workflow': desktop.checkpoint,
        })

    @staticmethod
    def _can_refresh_action(error):
        return (not error.uncertain
                and ('expired' in str(error).casefold() or error.code in {
                    'ELEMENT_UNAVAILABLE', 'STALE_ACTION_TARGET', 'UIA_TARGET_UNAVAILABLE',
                    'FOCUS_FAILED',
                }))

    def _refresh_action_element(self, desktop, control, saved):
        """Resolve the same semantic control again without sending input."""
        window_kind = next((name for name in ('main', 'sale', 'find', 'new_item')
                            if desktop.is_window(name)), None)
        locator = {
            'name': control.name or saved.get('name', ''),
            'controlType': int(saved.get('kind') or control.kind),
            'windowKind': window_kind,
            'visibleOnly': True,
            'readOnly': False,
        }
        automation_id = control.target.get('uia_automation_id')
        if automation_id:
            locator['automationId'] = automation_id
        try:
            element = self.bridge.call('find', {
                'key': 'migration.refresh.' + uuid.uuid4().hex,
                'locator': locator,
            })
        except EngineError:
            locator.pop('automationId', None)
            element = self.bridge.call('find', {
                'key': 'migration.refresh.' + uuid.uuid4().hex,
                'locator': locator,
            })
        control.target['uia_element'] = element
        return element

    def invoke(self, desktop, control, action='click', value=None, *, _refresh_attempted=False):
        """Invoke a named UIA control after matching it to the current frame."""
        if (control.observed_frame_id != desktop._frame_id
                or desktop._frame_window != (desktop.window.hwnd, desktop.window.pid)):
            raise EngineError('STALE_ACTION_TARGET', 'UIA action requires the current frame.')
        desktop.guard(control)
        desktop._guard_frame()
        self._bind(desktop)
        left, top = desktop._frame[0][:2]
        x, y, width, height = control.bounds
        target_type = control.target.get('type')
        if target_type in ('sales_add', 'fixed_button', 'word', 'tab', 'item_dialog_button', 'recovery',
                           'find_checkbox', 'inventory_menu', 'arrow', 'catalog_option',
                           'catalog_scroll', 'item_grid_scroll', 'sale_item_cell', 'sale_row',
                           'field', 'find_filter', 'native_control'):
            saved = control.target.get('uia_element')
            if isinstance(saved, dict) and saved.get('token'):
                try:
                    element = self.bridge.call('read', {'token': saved['token']})
                except EngineError as error:
                    # Checkpoint persistence can outlive the UIA token lifetime. Rebind the
                    # same semantic control in the current window before validating identity.
                    if not self._can_refresh_action(error):
                        raise
                    element = self._refresh_action_element(desktop, control, saved)
                    saved = element
                expected_kind = {
                    'find_checkbox': 50002,
                    'catalog_option': int(saved.get('kind') or 50007),
                    'catalog_scroll': int(saved.get('kind') or 50025),
                    'item_grid_scroll': 50018,
                    'sale_item_cell': int(saved.get('kind') or 50004),
                    'sale_row': 50029,
                    'tab': 50019,
                    'field': int(saved.get('kind') or control.kind),
                    'find_filter': int(saved.get('kind') or control.kind),
                    'native_control': int(saved.get('kind') or control.kind),
                }.get(target_type, (50003 if target_type == 'arrow'
                                    and 'ExpandCollapse' in set(saved.get('patterns') or ())
                                    else int(saved.get('kind') or 50000)))
                if (element.get('windowId') != desktop.window.hwnd
                        or (element.get('offscreen') and target_type != 'sale_row')
                        or not element.get('enabled')
                        or element.get('kind') != expected_kind
                        or element.get('bounds') != saved.get('bounds')
                        or (saved.get('id') and element.get('id') != saved.get('id'))
                        or (saved.get('name') and element.get('name') != saved.get('name'))
                        or (saved.get('parentToken')
                            and element.get('parentToken') != saved.get('parentToken'))):
                    raise EngineError('STALE_ACTION_TARGET', 'UIA action token changed before input.')
                if target_type in ('catalog_scroll', 'item_grid_scroll') \
                        and not ({'Scroll', 'ScrollItem'} & set(element.get('patterns') or ())):
                    raise EngineError('PATTERN_UNSUPPORTED',
                                      'The current scroll target has no Scroll or ScrollItem pattern.')
                if target_type == 'arrow' and expected_kind == 50003 \
                        and 'ExpandCollapse' not in set(element.get('patterns') or ()):
                    raise EngineError('PATTERN_UNSUPPORTED',
                                      'The current ComboBox has no ExpandCollapse pattern.')
                if target_type == 'sale_item_cell' and expected_kind == 50003 \
                    and 'ExpandCollapse' not in set(element.get('patterns') or ()):
                    raise EngineError('PATTERN_UNSUPPORTED',
                                      'The current item ComboBox has no ExpandCollapse pattern.')
                if target_type in ('field', 'find_filter') and action == 'focus' \
                        and not element.get('token'):
                    raise EngineError('UIA_TARGET_UNAVAILABLE',
                                      'The current field has no UIA token.')
                if target_type == 'sale_row' \
                        and not ({'Scroll', 'ScrollItem'} & set(element.get('patterns') or ())):
                    raise EngineError('PATTERN_UNSUPPORTED',
                                      'The current sale row has no Scroll or ScrollItem pattern.')
                if target_type == 'catalog_option':
                    if (element.get('name') != saved.get('name')
                            or not ({'SelectionItem', 'Invoke'}
                                    & set(element.get('patterns') or ()) )):
                        raise EngineError('STALE_ACTION_TARGET',
                                          'Catalog option text or capability changed before input.')
                    saved_parent = saved.get('catalogParent') or {}
                    if not saved_parent.get('token'):
                        raise EngineError('STALE_ACTION_TARGET',
                                          'Catalog option parent changed before input.')
                    parent = self.bridge.call('read', {'token': saved_parent['token']})
                    if (parent.get('windowId') != desktop.window.hwnd
                            or parent.get('offscreen') or not parent.get('enabled')
                            or parent.get('bounds') != saved_parent.get('bounds')):
                        raise EngineError('STALE_ACTION_TARGET',
                                          'Catalog option parent geometry changed before input.')
                    if element.get('parentToken') != saved_parent.get('token'):
                        # Warehouse combo rows may be exposed as standalone
                        # DataItems with no UIA parent token.  The active
                        # combo geometry still provides the required scope.
                        if (int(element.get('kind') or 0) == 50029
                                and not element.get('parentToken')):
                            pbox = parent.get('bounds') or {}
                            ebox = element.get('bounds') or {}
                            pvalues = tuple(pbox.get(key) for key in ('x', 'y', 'width', 'height'))
                            evalues = tuple(ebox.get(key) for key in ('x', 'y', 'width', 'height'))
                            if (not all(isinstance(value, (int, float)) and math.isfinite(value)
                                        for value in pvalues + evalues)
                                    or evalues[0] < pvalues[0] - 24
                                    or evalues[0] + evalues[2] > pvalues[0] + pvalues[2] + 80
                                    or evalues[1] < pvalues[1] + pvalues[3] - 3
                                    or evalues[1] > pvalues[1] + pvalues[3] + 240):
                                raise EngineError('STALE_ACTION_TARGET',
                                                  'Catalog grid row geometry changed before input.')
                            desktop._guard_frame()
                            return self.bridge.execute(action, token=element['token'], value=value,
                                                       checkpoint=desktop.checkpoint).get('strategyUsed') or action
                        popup = saved.get('catalogPopupParent') or {}
                        if (popup.get('token') != element.get('parentToken')
                                or popup.get('windowId') != desktop.window.hwnd
                                or popup.get('offscreen') or not popup.get('enabled')):
                            raise EngineError('STALE_ACTION_TARGET',
                                              'Catalog option popup parent changed before input.')
                        pbox = parent.get('bounds') or {}
                        qbox = popup.get('bounds') or {}
                        ebox = element.get('bounds') or {}
                        values = tuple(qbox.get(key) for key in ('x', 'y', 'width', 'height'))
                        evalues = tuple(ebox.get(key) for key in ('x', 'y', 'width', 'height'))
                        if (not all(isinstance(value, (int, float)) and math.isfinite(value)
                                    for value in values + evalues)
                                or values[2] <= 0 or values[3] <= 0
                                or evalues[2] <= 0 or evalues[3] <= 0
                                or values[0] > evalues[0] or values[1] > evalues[1]
                                or evalues[0] + evalues[2] > values[0] + values[2]
                                or evalues[1] + evalues[3] > values[1] + values[3]
                                or abs((values[0] + values[2] / 2)
                                       - (pbox.get('x', 0) + pbox.get('width', 0) / 2))
                                   > max(32, pbox.get('width', 0) * .5)
                                or values[1] < pbox.get('y', 0) + pbox.get('height', 0) - 3):
                            raise EngineError('STALE_ACTION_TARGET',
                                              'Catalog option popup geometry changed before input.')
                if target_type == 'sale_item_cell':
                    saved_row = control.target.get('uia_row_parent') or {}
                    if saved_row.get('token'):
                        row_parent = self.bridge.call('read', {'token': saved_row['token']})
                        if (row_parent.get('windowId') != desktop.window.hwnd
                                or row_parent.get('kind') != 50029
                                or row_parent.get('id') != saved_row.get('id')
                                or row_parent.get('bounds') != saved_row.get('bounds')
                                or row_parent.get('offscreen') or not row_parent.get('enabled')):
                            raise EngineError('STALE_ACTION_TARGET',
                                              'Sale editor row identity changed before input.')
                desktop._guard_frame()
            else:
                if target_type in ('sales_add', 'fixed_button', 'word', 'tab', 'item_dialog_button', 'recovery',
                                   'catalog_option', 'catalog_scroll', 'item_grid_scroll',
                                   'sale_item_cell', 'sale_row', 'native_control'):
                    raise EngineError('UIA_TARGET_UNAVAILABLE',
                                      'This dynamic target requires a current geometry-backed UIA token.')
                target_kind = 50002 if target_type == 'find_checkbox' else 50000
                element = self.locate_near(
                    desktop, bounds=control.bounds, kind=target_kind,
                    automation_id=None, vertical_only=True)
            if target_type == 'find_checkbox' and control.toggle in (0, 1) \
                    and element.get('toggle') != control.toggle:
                raise EngineError('STALE_ACTION_TARGET', 'Checkbox state changed after the current native read.')
        else:
            locator = {
                'name': control.name,
                'controlType': control.kind,
                'windowKind': next((kind for kind in ('main', 'sale', 'find', 'new_item')
                                    if desktop.is_window(kind)), None),
                'visibleOnly': True,
                'readOnly': False,
            }
            if control.id and not control.id.startswith(('find-filter:', 'field:')):
                locator['automationId'] = control.id
            element = self.bridge.call('find', {
                'key': 'migration.action.' + uuid.uuid4().hex,
                'locator': locator,
            })
        if element.get('windowId') != desktop.window.hwnd or not element.get('enabled'):
            raise EngineError('ELEMENT_UNAVAILABLE', 'UIA action target is not enabled in the bound MISA window.')
        box = element.get('bounds') or {}
        ex, ey = box.get('x'), box.get('y')
        ew, eh = box.get('width'), box.get('height')
        cx, cy = x + left + width / 2, y + top + height / 2
        if (not all(isinstance(value, (int, float)) and math.isfinite(value)
                    for value in (ex, ey, ew, eh))
                or ew <= 0 or eh <= 0 or cx < ex or cx > ex + ew or cy < ey or cy > ey + eh
                or ew > width + 80 or eh > height + 60):
            raise EngineError('UIA_TARGET_MISMATCH', 'UIA action target does not match the current frame geometry.')
        desktop._guard_frame()
        try:
            result = self.bridge.execute(action, token=element['token'], value=value,
                                         checkpoint=desktop.checkpoint)
        except EngineError as error:
            # A WinForms editor can recreate its UIA provider between the final read and
            # Execute. Refresh once only when the engine confirms no input was sent.
            if _refresh_attempted or not self._can_refresh_action(error):
                raise
            self._refresh_action_element(desktop, control, element)
            desktop._guard_frame()
            return self.invoke(desktop, control, action=action, value=value,
                               _refresh_attempted=True)
        return result.get('strategyUsed') or action

    def locate(self, desktop, *, name, kind, window_kind=None, automation_id=None):
        """Resolve one semantic control and bind its bounds to the current frame."""
        desktop._guard_frame()
        self._bind(desktop)
        locator = {'name': name, 'controlType': kind, 'windowKind': window_kind,
                   # Older UIA engines cannot query IsOffscreen for TabItem;
                   # geometry and window guards below still reject unusable targets.
                   'visibleOnly': False if int(kind) == 50019 else True, 'readOnly': False}
        if automation_id:
            locator['automationId'] = automation_id
        element = self.bridge.call('find', {
            'key': 'migration.locate.' + uuid.uuid4().hex,
            'locator': locator,
        })
        if (element.get('windowId') != desktop.window.hwnd
                or not element.get('enabled')
                or (element.get('offscreen') and int(kind) != 50019)):
            raise EngineError('ELEMENT_UNAVAILABLE', 'UIA control is not enabled and visible in the bound MISA window.')
        if int(element.get('kind') or 0) != int(kind):
            raise EngineError('UIA_TARGET_MISMATCH', 'UIA control type disagrees with the requested semantic target.')
        if automation_id and str(element.get('id') or '') != str(automation_id):
            raise EngineError('UIA_TARGET_MISMATCH', 'UIA AutomationId disagrees with the requested semantic target.')
        box = element.get('bounds') or {}
        values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
        if (not all(isinstance(value, (int, float)) and math.isfinite(value) for value in values)
                or values[2] <= 0 or values[3] <= 0):
            raise EngineError('UIA_TARGET_MISMATCH', 'UIA control returned invalid bounds.')
        left, top, right, bottom = desktop._frame[0]
        x, y, width, height = values
        if x < left or y < top or x + width > right or y + height > bottom:
            raise EngineError('UIA_TARGET_MISMATCH', 'UIA control is outside the current MISA frame.')
        desktop._guard_frame()
        return element

    def query(self, desktop, *, kinds=(), ids=(), parent_token=None, cache=False):
        """Query current UIA elements after binding the live MISA window."""
        desktop._guard_frame()
        self._bind(desktop)
        if cache and self._query_cache_frame != desktop._frame_id:
            self._query_cache_frame = desktop._frame_id
            self._query_cache.clear()
        cache_key = (tuple(kinds), tuple(ids), parent_token or '')
        if cache:
            cached = self._query_cache.get(cache_key)
            if cached is not None:
                desktop._metrics['uia_query_cache_hits'] = desktop._metrics.get('uia_query_cache_hits', 0) + 1
                return cached
        query = {}
        if kinds:
            query['kinds'] = list(kinds)
        if ids:
            query['ids'] = list(ids)
        if parent_token:
            query['parentToken'] = parent_token
        result = self.bridge.call('query', query)
        if cache:
            self._query_cache[cache_key] = result
            desktop._metrics['uia_query_cache_misses'] = desktop._metrics.get('uia_query_cache_misses', 0) + 1
        desktop._guard_frame()
        return result

    def dump_tree(self, desktop, *, maximum_nodes=5000):
        """Read the current UIA3 hierarchy, bounded to keep live diagnostics usable."""
        if type(maximum_nodes) is not int or not 1 <= maximum_nodes <= 8000:
            raise EngineError('INVALID_TREE_LIMIT', 'UIA tree limit must be between 1 and 8000.')
        self._bind(desktop)
        return self.bridge.call('dumpTree', {'maximumNodes': maximum_nodes})

    def grid(self, desktop, control, *, start_row=0, maximum_rows=100):
        """Read a current UIA GridPattern page without using screen geometry."""
        desktop._guard_frame()
        self._bind(desktop)
        element = (control.target or {}).get('uia_element') if control is not None else None
        token = element.get('token') if isinstance(element, dict) else None
        if not token:
            raise EngineError('UIA_TARGET_UNAVAILABLE', 'Grid target has no current UIA token.')
        if self._grid_cache_frame != desktop._frame_id:
            self._grid_cache_frame = desktop._frame_id
            self._grid_cache.clear()
        cache_key = (token, int(start_row), int(maximum_rows))
        page = self._grid_cache.get(cache_key)
        if page is None:
            page = self.bridge.call('grid', {
                'token': token, 'startRow': cache_key[1],
                'maximumRows': cache_key[2],
            })
            self._grid_cache[cache_key] = page
        desktop._guard_frame()
        return page

    def locate_near(self, desktop, *, bounds, kind, automation_id=None, vertical_only=False):
        """Resolve one visible UIA element whose geometry corroborates a live target."""
        desktop._guard_frame()
        self._bind(desktop)
        kinds = list(kind) if isinstance(kind, (tuple, list)) else [kind]
        query = {'kinds': kinds}
        if automation_id:
            query['ids'] = [automation_id]
        if self._query_cache_frame != desktop._frame_id:
            self._query_cache_frame = desktop._frame_id
            self._query_cache.clear()
        cache_key = (tuple(kinds), tuple(query.get('ids') or ()))
        elements = self._query_cache.get(cache_key)
        if elements is None:
            elements = self.bridge.call('query', query)
            self._query_cache[cache_key] = elements
        left, top = desktop._frame[0][:2]
        x, y, width, height = (float(value) for value in bounds)
        target = (x + left, y + top, width, height)

        def finite_box(element):
            box = element.get('bounds') or {}
            values = tuple(box.get(key) for key in ('x', 'y', 'width', 'height'))
            return (element.get('windowId') == desktop.window.hwnd
                    and element.get('enabled') and not element.get('offscreen')
                    and all(isinstance(value, (int, float)) and math.isfinite(value)
                            for value in values)
                    and values[2] > 0 and values[3] > 0)

        def matches(element):
            if not finite_box(element):
                return False
            ex, ey, ew, eh = (element['bounds'][key]
                              for key in ('x', 'y', 'width', 'height'))
            tx, ty, tw, th = target
            if vertical_only:
                return (max(ex, tx) < min(ex + ew, tx + tw)
                        and abs((ey + eh / 2) - (ty + th / 2)) <= max(10, th))
            ecx, ecy = ex + ew / 2, ey + eh / 2
            tcx, tcy = tx + tw / 2, ty + th / 2
            return (ex <= tcx <= ex + ew and ey <= tcy <= ey + eh
                    and tx <= ecx <= tx + tw
                    and ew <= tw + 48 and eh <= th + 18)

        candidates = [element for element in elements if matches(element)]
        unique = {}
        for element in candidates:
            box = element['bounds']
            key = (element.get('id', ''), element.get('kind'),
                   tuple(round(box[name], 2) for name in ('x', 'y', 'width', 'height')))
            unique[key] = element
        candidates = list(unique.values())
        if len(candidates) != 1:
            code = 'UIA_TARGET_UNAVAILABLE' if not candidates else 'UIA_TARGET_AMBIGUOUS'
            raise EngineError(code, 'UIA geometry did not resolve one current visible control.')
        desktop._guard_frame()
        return candidates[0]

    def _observe_header(self, desktop, bounds):
        """Read the unique HeaderItem covering the native text, without a name hint."""
        observed_at = utc_now().isoformat()
        left, top, right, bottom = desktop._frame[0]

        def overlaps_text(element):
            if (element['windowId'] != desktop.window.hwnd or element['kind'] != 50035
                    or element['offscreen']):
                return False
            box = element['bounds']
            x, y, width, height = (box[key] for key in ('x', 'y', 'width', 'height'))
            if (not all(math.isfinite(value) for value in (x, y, width, height))
                    or width <= 0 or height <= 0 or height > bounds['height'] + 16
                    or x < left or y < top or x + width > right or y + height > bottom):
                return False
            return (x - 3 <= bounds['x'] and y - 3 <= bounds['y']
                    and x + width + 3 >= bounds['x'] + bounds['width']
                    and y + height + 3 >= bounds['y'] + bounds['height'])

        def result(status, reason='', element=None):
            return dict(status=status, reason=reason, observedAt=observed_at,
                        windowId=desktop.window.hwnd, processId=desktop.window.pid,
                        value=element['name'] if element is not None else None,
                        valueSource='NameProperty; HeaderItem; query+read',
                        bounds=element['bounds'] if element is not None else None)

        desktop._guard_frame()
        candidates = [element for element in self.bridge.call('query', {'kinds': [50035]})
                      if overlaps_text(element)]
        if not candidates:
            return result('unavailable', 'No visible HeaderItem covers the current native caption.')
        if len(candidates) != 1:
            return result('ambiguous', 'Multiple HeaderItems cover the current native caption.')
        candidate = candidates[0]
        current = self.bridge.call('read', {'token': candidate['token']})
        desktop._guard_frame()
        if (not overlaps_text(current) or any(current[key] != candidate[key]
                for key in ('id', 'name', 'kind', 'bounds', 'windowId'))):
            return result('ambiguous', 'Header identity or bounds changed during readback.')
        if not current['name'].strip():
            return result('unavailable', 'HeaderItem does not expose its displayed caption.')
        return result('supported', element=current)

    def close(self):
        if self.bridge is not None and self.owns_bridge:
            self.bridge.close()
        self.bridge = None
        self.window = None
        self._query_cache.clear()
