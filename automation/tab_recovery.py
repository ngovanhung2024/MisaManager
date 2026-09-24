"""Close run-owned forms and resume on the sales page selected by the user."""
from copy import deepcopy
import time

from .domain import Cancelled, NeedsAttention, normalize
from .incident_evidence import now
from .label_matching import label_candidates, label_key, word_evidence
from .observation_wait import read_until
from .performance import record

DISCARD_TEXT = ('Không lưu', 'Không cất', 'Bỏ thay đổi', 'Không', 'No')
CONFIRM_DISCARD_TEXT = ('Đồng ý', 'Có', 'Bỏ thay đổi', 'Hủy bỏ thay đổi', 'Yes')
DIALOG_BUTTON_TEXT = (*DISCARD_TEXT, *CONFIRM_DISCARD_TEXT, 'Cancel', 'Hủy bỏ', 'Hủy')
SAVE_QUESTIONS = ('comuonluu', 'comuoncat', 'luucacthaydoi', 'luuthaydoi', 'catchungtu')
DISCARD_QUESTIONS = ('huybothaydoi', 'bothaydoi', 'loaibothaydoi', 'khongluuthaydoi',
                     'boquanhungthaydoi', 'boquacacthaydoi', 'huybodulieu', 'loaibodulieu')


class DialogNotReady(NeedsAttention):
    """The owned dialog needs a fresh observation; no input is authorized."""


def identity(window):
    from . import win32
    return dict(hwnd=window.hwnd, pid=window.pid, title=window.title,
                process_birth=win32.process_birth(window.pid))


def scoped_caption(desktop, captions, words):
    found = [desktop._label(caption, words, scoped=True) for caption in captions
             if label_candidates((caption,), words)]
    return desktop.unique(found, ' / '.join(captions))


class TabLedger:
    def __init__(self, data=None, persist=None):
        self.data = deepcopy(data or {})
        self.persist = persist or (lambda value: None)

    def save(self):
        self.persist(deepcopy(self.data))

    def baseline(self, desktop):
        from . import win32
        if self.data:
            return
        if not desktop.is_window('main'):
            raise NeedsAttention('Thêm cần trang chính MISA; không được nhận tab có sẵn là tab của lượt chạy.')
        # Baseline ownership is native evidence only.  The main-page native scan
        # used to happen before every first Add click, even though it only
        # recorded pre-existing HWNDs and never supplied an input target.
        # Fixed controls are resolved from the fresh client-relative map in
        # prepare_sales(); data captions are read after the voucher opens.
        desktop._check()
        desktop.sync(kind='main')
        native = win32.window_lifecycle(desktop.window)
        if not native['visible'] or not native['enabled'] or not native['foreground']:
            raise NeedsAttention('Trang chính MISA chưa ở trạng thái foreground/enabled để bắt đầu Thêm.')
        self.data = dict(main=identity(desktop.window),
            baseline=[identity(w) for w in win32.misa_windows() if w.pid == desktop.pid],
            owned=[], pending=None, closing=None,
            baseline_frame=None, baseline_native=native, started_at=now())
        self.save()

    def open_surfaces(self):
        # Older sessions tracked module tabs; preserve the user's selected workspace.
        return [entry for entry in self.data.get('owned', [])
                if entry['kind'] != 'tab' and entry['state'] != 'closed']

    def check_process(self, desktop):
        from . import win32
        main = self.data.get('main')
        if main and (desktop.pid != main['pid'] or win32.process_birth(desktop.pid) != main['process_birth']):
            raise NeedsAttention('Phiên MISA đã thay đổi; không áp dụng quyền đóng tab của phiên cũ.')

    def require_surface(self, desktop, kind):
        self.check_process(desktop)
        owned = any(entry['kind'] == kind and entry['state'] != 'closed'
                    and entry['hwnd'] == desktop.window.hwnd for entry in self.data.get('owned', []))
        if not owned:
            # MISA can switch to an already-open document instead of creating
            # another HWND. Reconcile the pending Add transition before
            # rejecting the surface as belonging to an unrelated run.
            self.observe(desktop)
            owned = any(entry['kind'] == kind and entry['state'] != 'closed'
                        and entry['hwnd'] == desktop.window.hwnd for entry in self.data.get('owned', []))
        if not owned:
            raise NeedsAttention('C\u1eeda s\u1ed5 ' + kind + ' ch\u01b0a đ\u01b0\u1ee3c ghi nh\u1eadn do l\u01b0\u1ee3t Th\u00eam m\u1edf; kh\u00f4ng nh\u1eadp v\u00e0o tab c\u00f3 s\u1eb5n.')
    def claim_current_surface(self, desktop, kind):
        """Recover a newly opened non-baseline surface after a source reload."""
        from . import win32
        self.check_process(desktop)
        if any(entry['kind'] == kind and entry['state'] != 'closed'
               and entry['hwnd'] == desktop.window.hwnd for entry in self.data.get('owned', [])):
            return
        if not desktop.is_window(kind):
            raise NeedsAttention('Cửa sổ hiện tại không khớp loại cần khôi phục: ' + kind)
        if desktop.window.hwnd in {entry['hwnd'] for entry in self.data.get('baseline', [])}:
            raise NeedsAttention('Cửa sổ hiện tại thuộc baseline; không nhận là tab của lượt chạy.')
        lifecycle = win32.window_lifecycle(desktop.window)
        if not lifecycle['visible'] or not lifecycle['enabled'] or not lifecycle['foreground']:
            raise NeedsAttention('Cửa sổ hiện tại chưa foreground/enabled để khôi phục quyền sở hữu.')
        opener = next((entry['hwnd'] for entry in reversed(self.open_surfaces())
                       if entry['kind'] == 'sale'), self.data['main']['hwnd'])
        entry = dict(identity(desktop.window), kind=kind, opener=opener,
                     opened_at=now(), origin_frame=desktop._frame_id,
                     state='open', run_id=self.data.get('run_id'))
        self.data['owned'].append(entry)
        self.data['pending'] = None
        self.save()

    def surface_state(self, desktop, entry):
        from . import win32
        self.check_process(desktop)
        main = self.data['main']
        baseline_hwnd = entry['hwnd'] in {w['hwnd'] for w in self.data.get('baseline', [])}
        if (entry['pid'] != main['pid'] or entry['process_birth'] != main['process_birth']
                or (baseline_hwnd and not entry.get('switched_existing'))):
            raise NeedsAttention('Định danh cửa sổ không thuộc lượt chạy này.')
        return win32.window_lifecycle(win32.MisaWindow(entry['hwnd'], entry['pid'], entry['title']))

    def mark_closed(self, entry, observation):
        if (observation['hwnd'] != entry['hwnd'] or observation['pid'] != entry['pid']
                or observation['visible']):
            raise NeedsAttention('Trạng thái Windows chưa xác nhận cửa sổ của lượt chạy đã đóng.')
        entry.update(state='closed', closed_at=now(), close_observation=deepcopy(observation))
        if (self.data.get('closing') or {}).get('hwnd') == entry['hwnd']:
            self.data['closing'] = None
        self.save()
        record('tab_closed', tab=deepcopy(entry), verification_source='native_window_lifecycle',
               observation=deepcopy(observation))

    def reconcile_closed(self, desktop):
        for entry in self.open_surfaces():
            observation = self.surface_state(desktop, entry)
            if not observation['visible']:
                self.mark_closed(entry, observation)

    def before_open(self, desktop, kind):
        from . import win32
        self.check_process(desktop)
        self.reconcile_closed(desktop)
        before = [w.hwnd for w in win32.misa_windows() if w.pid == desktop.pid]
        foreground = win32.foreground_identity()
        self.data['pending'] = dict(kind=kind, opener=desktop.window.hwnd,
            before=before, before_foreground=foreground.get('hwnd'),
            frame_id=desktop._frame_id, at=now())
        self.save()
    def observe(self, desktop):
        if not self.data or desktop.window is None:
            return
        self.check_process(desktop)
        pending = self.data.get('pending')
        if not pending:
            return
        existing = desktop.window.hwnd in pending.get('before', ())
        # A matching pre-existing surface is valid only when Add moved focus
        # away from it and MISA brought it forward as the selected document.
        # This keeps unrelated open tabs out of the run while supporting the
        # native window-switch behavior of the sales screen.
        if existing and desktop.window.hwnd == pending.get('before_foreground'):
            return
        if desktop.is_window(pending['kind']):
            if existing:
                from . import win32
                lifecycle = win32.window_lifecycle(desktop.window)
                if (not lifecycle['visible'] or not lifecycle['enabled']
                        or not lifecycle['foreground']):
                    return
            entry = dict(identity(desktop.window), kind=pending['kind'], opener=pending['opener'],
                         opened_at=now(), origin_frame=pending['frame_id'], state='open',
                         run_id=self.data.get('run_id'), switched_existing=bool(existing))
            if not any(x.get('hwnd') == entry['hwnd'] and x.get('state') != 'closed'
                       for x in self.data['owned']):
                self.data['owned'].append(entry)
            self.data['pending'] = None
            self.save()


class TabRecovery:
    def __init__(self, desktop, ledger, notify=None):
        self.desktop, self.ledger = desktop, ledger
        self.notify = notify or (lambda phase, message: None)

    def _select_window(self, preferred=None):
        from . import win32
        d = self.desktop
        d._check()
        candidate = next((w for w in win32.misa_windows() if w.hwnd == preferred
                          and w.pid == self.ledger.data.get('main', {}).get('pid')), None)
        if candidate is not None and win32.user32.IsWindowEnabled(candidate.hwnd):
            # A pre-existing modeless form must not prevent selecting the requested main window.
            window = candidate
        else:
            window = win32.recover_window()
        d.window, d.pid = window, window.pid
        self.ledger.check_process(d)
        if not win32.is_foreground(window):
            win32.activate(window)
        self.ledger.observe(d)
        d._invalidate()
        return window

    def observe(self, preferred=None):
        self._select_window(preferred)
        d = self.desktop
        d._observe(kind='main' if d.is_window('main') else None, force=True)
        return d._frame[3]

    def _caption(self, captions, words=None):
        d = self.desktop
        pool = d._frame[3] if words is None else words
        return scoped_caption(d, captions, pool)

    def _button(self, word, intent):
        control = self.desktop._control_for_word(word, kind=50000)
        control.target = dict(control.target, type='recovery', intent=intent, text=word.text)
        if intent == 'close':
            control.target['close_action'] = 'native_close_button'
        return control

    def resolve(self, target):
        """Called again by ScreenDesktop immediately before each actual input."""
        d = self.desktop
        intent = target['intent']
        if intent in ('discard', 'confirm_discard'):
            return self.discard_button(intent, expected=target['text'])
        if intent == 'close':
            entry = self.require_owned()
            # The fixed Cancel control is safe only after the current native frame
            # has proved this exact run-owned dialog. Prefer the semantic UIA
            # button; stop safely when the native button is not exposed.
            if target['text'] == 'Hủy bỏ' and entry['kind'] in ('find', 'new_item'):
                mapped = self.desktop._uia_static_target({
                    'type': 'item_dialog_button', 'text': target['text'],
                    'window_kind': entry['kind']})
                if mapped is None:
                    raise NeedsAttention('Không định vị được nút Hủy bỏ bằng native UIA hiện tại.')
                self.ledger.check_process(d)
                if d.window.hwnd != entry['hwnd'] or not d.is_window(entry['kind']):
                    raise NeedsAttention('Cửa sổ đóng tab đã đổi trước khi dùng map nút Hủy bỏ.')
                return mapped
            if target['text'] == 'Đóng' and entry['kind'] == 'sale':
                semantic = d._uia_static_target({
                    'type': 'fixed_button', 'name': 'Đóng',
                    'window_kind': 'sale'})
                if semantic is not None:
                    semantic.target = dict(type='recovery', intent='close',
                                           text='Đóng', close_action='uia_close',
                                           uia=True,
                                           uia_element=semantic.target.get('uia_element'),
                                           uia_automation_id=semantic.target.get('uia_automation_id', ''))
                    return semantic
            word = self._caption((target['text'],))
            return self._button(word, intent)
        raise NeedsAttention('Unknown recovery intent: ' + intent)

    def require_owned(self):
        d = self.desktop
        self.ledger.check_process(d)
        entry = next((x for x in reversed(self.ledger.open_surfaces())
                      if x['hwnd'] == d.window.hwnd), None)
        if entry is None:
            raise NeedsAttention('Cửa sổ/tab hiện tại không thuộc lượt chạy; không được bỏ dữ liệu.')
        if not d.is_window(entry['kind']):
            raise NeedsAttention('Cửa sổ đã đổi loại; cần đọc lại trước khi đóng.')
        # Require current native identity/UIA evidence as well as identity.
        # Some MISA dialogs expose the caption only as the HWND title; their
        # client UIA tree starts at the first field label.
        fragments = {'sale': ('Bán hàng hóa, dịch vụ trong nước', 'Chứng từ bán hàng'),
                     'find': ('Tìm nhanh', 'Tìm nhanh vật tư'), 'new_item': ('Thêm vật tư',)}
        height = d._frame[1][1] if d._frame else 0
        frame_title = normalize(' '.join(w.text for w in d._frame[3]
                                         if w.cy < height * .3))
        window_title = normalize(d.window.title)
        if not any(normalize(label) in frame_title or normalize(label) in window_title
                   for label in fragments.get(entry['kind'], ())):
            raise NeedsAttention('native chưa xác nhận nội dung cửa sổ cần đóng.')
        return entry

    def _require_discard_dialog(self):
        from . import win32
        d = self.desktop
        closing = self.ledger.data.get('closing')
        if not closing or closing.get('stage') not in ('requested', 'discard_sent'):
            raise NeedsAttention('Không có thao tác đóng tab được cho phép để bỏ thay đổi.')
        self.ledger.check_process(d)
        if (d.window.hwnd in {x['hwnd'] for x in self.ledger.data.get('baseline', [])}
                and not closing.get('switched_existing')):
            raise NeedsAttention('Không chọn bỏ thay đổi trên cửa sổ có sẵn.')
        if d.window.hwnd == closing['hwnd'] or not win32.is_owned_by(d.window, closing['hwnd']):
            raise NeedsAttention('Hộp thoại không thuộc cửa sổ đang đóng.')
        d._guard_frame()
        return closing

    def _question_line(self, word, evidence):
        """Accept one current native body line without guessing its text."""
        evidence.update(source=word_evidence(word), accepted=False)
        if not word.text.strip():
            raise DialogNotReady('Native dialog text is unavailable; read a fresh frame.')
        evidence.update(accepted=True, acceptance_rule='current_native_text')
        return word.text

    def _discard_from_frame(self, intent, expected, evidence):
        d = self.desktop
        client = d._frame_context['client_bounds']
        evidence['client_bounds'] = list(client)
        words = d._frame[3]
        candidates = label_candidates(DIALOG_BUTTON_TEXT,
                                      [w for w in words if w.cy > client[1] + client[3] * .55])
        if not candidates:
            return self._discard_from_native(intent, expected, evidence)
        anchor = max(candidates, key=lambda w: w.cy)
        row_height = anchor.box[3] - anchor.box[1]
        buttons = [w for w in words if abs(w.cy - anchor.cy) <= row_height * .6]
        body = sorted((w for w in words if client[1] <= w.cy < min(b.box[1] for b in buttons)
                       and normalize(w.text)), key=lambda w: (round(w.cy / 4), w.cx))
        evidence.update(body=[word_evidence(w) for w in body],
                        buttons=[word_evidence(w) for w in buttons])
        if not body:
            raise DialogNotReady('Chưa đọc được câu hỏi của hộp thoại không lưu.')
        lines = []
        evidence['question_lines'] = []
        for word in body:
            line_evidence = {}
            evidence['question_lines'].append(line_evidence)
            lines.append(self._question_line(word, line_evidence))
        question = normalize(' '.join(lines))
        evidence['question'] = question
        save = any(token in question for token in SAVE_QUESTIONS) and any(
            token in question for token in ('thaydoi', 'chungtu', 'dulieu', 'vattu', 'luulai'))
        discard = any(token in question for token in DISCARD_QUESTIONS) and any(
            token in question for token in ('chacchan', 'comuon', 'xacnhan'))
        if save and not discard:
            actual_intent, captions = 'discard', DISCARD_TEXT
        elif discard and not save:
            actual_intent, captions = 'confirm_discard', CONFIRM_DISCARD_TEXT
        else:
            raise DialogNotReady('Nội dung hộp thoại không xác nhận duy nhất việc không lưu/bỏ thay đổi khi đóng tab.')
        if intent and intent != actual_intent:
            raise NeedsAttention('Hộp thoại đã thay đổi trước thao tác không lưu.')
        try:
            word = self._caption(captions, buttons)
        except Cancelled:
            raise
        except NeedsAttention as error:
            self._require_discard_dialog()
            # A text-only UIA caption may lack an action pattern even when the
            # same dialog exposes a real native button HWND.
            try:
                return self._discard_from_native(intent, expected, evidence)
            except DialogNotReady as native_error:
                raise DialogNotReady('Chưa đọc rõ nút không lưu: ' + str(error)
                                     + '; native: ' + str(native_error)) from native_error
        if expected and word.text != expected:
            raise NeedsAttention('Nút bỏ thay đổi đã đổi; cần đọc lại hộp thoại.')
        evidence.update(intent=actual_intent, button=word_evidence(word), status='ready')
        return self._button(word, actual_intent)

    def _discard_from_native(self, intent, expected, evidence):
        from . import win32
        from .screen_win32 import ScreenControl
        d = self.desktop
        children = win32.dialog_child_controls(d.window)
        buttons = [child for child in children if 'button' in child['class_name'].casefold()]
        body = [child for child in children if child not in buttons]
        evidence.update(native_body=body, native_buttons=buttons)
        if not buttons or not body:
            raise DialogNotReady('Chưa đọc được chữ và nút native của hộp thoại không lưu.')
        question = normalize(' '.join(child['text'] for child in body))
        evidence['question'] = question
        save = any(token in question for token in SAVE_QUESTIONS) and any(
            token in question for token in ('thaydoi', 'chungtu', 'dulieu', 'vattu', 'luulai'))
        discard = any(token in question for token in DISCARD_QUESTIONS) and any(
            token in question for token in ('chacchan', 'comuon', 'xacnhan'))
        if save and not discard:
            actual_intent, captions = 'discard', DISCARD_TEXT
        elif discard and not save:
            actual_intent, captions = 'confirm_discard', CONFIRM_DISCARD_TEXT
        else:
            raise DialogNotReady('Nội dung native chưa xác nhận duy nhất hộp thoại không lưu.')
        if intent and intent != actual_intent:
            raise NeedsAttention('Hộp thoại đã thay đổi trước thao tác không lưu.')
        choices = [child for child in buttons if label_key(child['text']) in
                   {label_key(caption) for caption in captions}]
        if len(choices) != 1:
            raise DialogNotReady('Nút native không lưu chưa duy nhất.')
        chosen = choices[0]
        if expected and chosen['text'] != expected:
            raise NeedsAttention('Nút bỏ thay đổi đã đổi caption.')
        x, y, width, height = chosen['bounds']
        evidence.update(intent=actual_intent, button=chosen, status='ready')
        return ScreenControl(
            d, name=chosen['text'], kind=50000, bounds=chosen['bounds'],
            enabled=True, role='button',
            target={'type': 'recovery', 'intent': actual_intent, 'text': chosen['text'],
                    'coordinate_fallback': True, 'coordinate': [x + width // 2, y + height // 2],
                    'coordinate_space': 'window', 'frame_id': d._frame_id,
                    'native_button_hwnd': chosen['hwnd']})

    def discard_button(self, intent=None, expected=None):
        d = self.desktop
        closing = self._require_discard_dialog()
        sent = closing.get('discard_dialog')
        if sent and (sent['hwnd'], sent['pid']) == (d.window.hwnd, d.window.pid):
            raise NeedsAttention('Đã gửi thao tác không lưu cho hộp thoại này; không bấm lại.')
        evidence = dict(frame_id=d._frame_id, checkpoint=d.checkpoint,
                        hwnd=d.window.hwnd, pid=d.window.pid,
                        captured_at=d._captured_at.isoformat(), input_sent=False, status='reading')
        try:
            button = self._discard_from_frame(intent, expected, evidence)
            self._require_discard_dialog()
            return button
        except DialogNotReady as error:
            evidence.update(status='waiting', error=str(error))
            raise
        except NeedsAttention as error:
            evidence.update(status='blocked', error=str(error))
            raise
        finally:
            record('recovery_dialog_observation', observation=evidence)

    def close_owned(self):
        from . import win32
        d = self.desktop
        d.checkpoint = 'restart.close'
        self.notify('cleaning_tabs', 'Đang đóng các tab của lượt chạy và chọn không lưu…')
        self.ledger.check_process(d)
        pending = self.ledger.data.get('pending')
        if pending:
            candidates = [w for w in win32.misa_windows() if w.pid == d.pid
                          and w.hwnd not in pending['before'] and d.matches_window(w, pending['kind'])]
            if len(candidates) == 1:
                self._select_window(preferred=candidates[0].hwnd)
            if self.ledger.data.get('pending'):
                raise NeedsAttention('Thao tác mở cửa sổ chưa rõ kết quả; không mở chứng từ khác để bỏ qua.')
        while True:
            entry = next(reversed(self.ledger.open_surfaces()), None)
            if entry is None:
                # Binding is native-only; restart.ready owns the fresh sales-page native.
                self._select_window(preferred=self.ledger.data['main']['hwnd'])
                if self.ledger.open_surfaces():
                    continue
                break
            observation = self.ledger.surface_state(d, entry)
            if not observation['visible']:
                d._invalidate()
                self.ledger.mark_closed(entry, observation)
                continue
            pending_close = self.ledger.data.get('closing')
            if self._replan_legacy_close(entry, pending_close):
                continue
            if pending_close and pending_close['hwnd'] == entry['hwnd'] and pending_close['stage'] != 'planned':
                self._finish_close(entry)
                continue
            d.checkpoint = 'restart.close'
            self.observe(preferred=entry['hwnd'])
            # Select only a recorded run-owned surface, never the entire MISA process.
            if d.window.hwnd != entry['hwnd']:
                closing = self.ledger.data.get('closing')
                if not closing or closing['hwnd'] != entry['hwnd']:
                    raise NeedsAttention('Có hộp thoại/cửa sổ chưa được nhận diện trước khi đóng tab.')
                self._finish_close(entry)
                continue
            self.require_owned()
            closing = dict(hwnd=entry['hwnd'], kind=entry['kind'], caption=entry.get('caption'),
                           switched_existing=bool(entry.get('switched_existing')),
                           stage='planned', at=now(), frame_id=d._frame_id)
            if pending_close and pending_close['hwnd'] == entry['hwnd'] and pending_close.get('legacy_replan'):
                closing['legacy_replan'] = pending_close['legacy_replan']
            self.ledger.data['closing'] = closing
            self.ledger.save()
            if entry['kind'] in ('find', 'new_item'):
                # Re-resolve the mapped Cancel button through the same recovery
                # target so click-time ownership and close-stage checks still run.
                from .screen_win32 import ScreenControl
                d.click(ScreenControl(d, name='Hủy bỏ', kind=50000,
                                      target={'type': 'recovery', 'intent': 'close',
                                              'text': 'Hủy bỏ', 'close_action': 'mapped_cancel'}))
            else:
                # The observed sale toolbar exposes mnuClose/Invoke. Prefer
                # that semantic target; native caption or Ctrl+F4 remains guarded
                # fallback for builds that do not expose the button.
                try:
                    semantic = d._uia_static_target({
                        'type': 'fixed_button', 'name': 'Đóng',
                        'window_kind': 'sale'})
                    if semantic is not None:
                        semantic.target.update(type='recovery', intent='close',
                                              close_action='uia_close', text='Đóng')
                        d.click(semantic)
                    else:
                        word = next((self._caption((label,)) for label in ('Đóng',)
                                     if label_candidates((label,), d._frame[3])), None)
                        if word is not None:
                            d.click(self._button(word, 'close'))
                        else:
                            # Ctrl+F4 targets the document proved by native, never Alt+F4 on MISA.
                            d.close_document_key(entry)
                except NeedsAttention as error:
                    # UIA may report an uncertain Invoke after MISA already
                    # opened its native save/discard dialog. Let the recovery
                    # readback decide instead of sending a second close.
                    if 'UIA action may have reached MISA' not in str(error):
                        raise
                    d._invalidate()
            self._finish_close(entry)

    def _replan_legacy_close(self, entry, closing):
        """Replan only a legacy close request that cannot prove any input was sent."""
        from . import win32
        if (not closing or closing.get('hwnd') != entry['hwnd']
                or closing.get('stage') != 'requested'
                or closing.get('close_action')
                or closing.get('discard_dialog')):
            return False
        d = self.desktop
        observation = self.ledger.surface_state(d, entry)
        if not (observation['visible'] and observation['enabled'] and observation['foreground']):
            raise NeedsAttention('Cửa sổ đóng tab cũ chưa foreground/enabled; không lập lại thao tác đóng.')
        window = win32.foreground_for_process(d.pid)
        if window.hwnd != entry['hwnd']:
            raise NeedsAttention('Đã có hộp thoại/cửa sổ khác phía trước; không lập lại thao tác đóng.')
        closing.update(stage='planned', legacy_replan=dict(
            at=now(), reason='requested_without_close_action', observation=deepcopy(observation)))
        self.ledger.save()
        record('recovery_legacy_close_replanned', closing=deepcopy(closing))
        d._invalidate()
        return True

    def _finish_close(self, entry):
        from . import win32
        d = self.desktop
        last_pending = None
        sent_dialog_since = None

        def next_state():
            nonlocal last_pending, sent_dialog_since
            d.checkpoint = 'restart.discard'
            observation = self.ledger.surface_state(d, entry)
            if not observation['visible']:
                return 'closed', observation
            # Input has already been sent. Poll lifecycle only until a real owned
            # dialog appears; never activate the closing form or native its parent.
            try:
                window = win32.foreground_for_process(d.pid)
            except win32.WindowError:
                # The user may have clicked another application while the
                # discard prompt was opening. Find the run-owned dialog by
                # HWND ownership and bring only that dialog forward.
                candidates = [candidate for candidate in win32.misa_windows()
                              if candidate.pid == d.pid
                              and candidate.hwnd not in (entry['hwnd'], entry.get('opener'),
                                                         self.ledger.data['main']['hwnd'])
                              and win32.is_owned_by(candidate, entry['hwnd'])]
                if len(candidates) != 1:
                    d._invalidate()
                    return None
                window = candidates[0]
                win32.activate(window)
            if window.hwnd in (entry['hwnd'], entry.get('opener'), self.ledger.data['main']['hwnd']):
                d._invalidate()
                return None
            if not win32.is_owned_by(window, entry['hwnd']):
                raise NeedsAttention('Cửa sổ phía trước không thuộc phiếu đang đóng.')
            state = win32.window_lifecycle(window)
            if not state['visible'] or not state['enabled'] or not state['foreground']:
                d._invalidate()
                return None
            sent = (self.ledger.data.get('closing') or {}).get('discard_dialog')
            if sent and (sent['hwnd'], sent['pid']) == (window.hwnd, window.pid):
                # A persistent prompt means the recorded input did not close it.
                # Stop with an explicit native result instead of polling 25 seconds.
                if sent_dialog_since is None:
                    sent_dialog_since = time.monotonic()
                if time.monotonic() - sent_dialog_since >= 2:
                    raise NeedsAttention('Hộp thoại MISA vẫn mở sau nút không cất; '
                                         'cần đọc lại native trước khi gửi input khác.')
                d._invalidate()
                return None
            sent_dialog_since = None
            d.window, d.pid = window, window.pid
            d._invalidate()
            d._observe(force=True)
            try:
                button = self.discard_button()
            except DialogNotReady as error:
                self._require_discard_dialog()
                last_pending = error
                d._invalidate()
                return None
            return 'button', button

        deadline = time.monotonic() + 25
        while True:
            try:
                observation = read_until(next_state, lambda value: value is not None, d.cancel,
                    timeout=max(.01, deadline - time.monotonic()), label='đóng tab không lưu')
            except Cancelled:
                raise
            except NeedsAttention as error:
                if last_pending is not None:
                    raise NeedsAttention(str(error) + '; lần đọc hộp thoại cuối: '
                                         + str(last_pending)) from error
                raise
            if observation[0] == 'closed':
                d._invalidate()
                self.ledger.mark_closed(entry, observation[1])
                break
            d.click(observation[1])
            last_pending = None

    def prepare_restart(self):
        self.close_owned()
        d = self.desktop
        d.checkpoint = 'restart.ready'
        self.notify('checking_sales', 'Đang kiểm tra frame và map nút Thêm trên trang Bán hàng bạn đã chọn…')

        self._wait_sales_ready(timeout=30)
        self.ledger.data['sales_verified'] = dict(frame_id=d._frame_id, at=now())
        self.ledger.save()

    def prepare_sales(self):
        self.desktop.checkpoint = 'add.open'
        return self._wait_sales_ready(timeout=15)

    def _wait_sales_ready(self, *, timeout):
        """Both initial entry and recovery wait by reading, never by clicking again."""
        d = self.desktop
        d.sync(kind='main')
        expected_window = (d.window.hwnd, d.window.pid)
        last_error = None

        def guard():
            d.sync(kind='main')
            d.guard()
            if (d.window.hwnd, d.window.pid) != expected_window:
                raise NeedsAttention('Cửa sổ MISA đã đổi khi chờ danh sách Bán hàng.')

        def observe():
            nonlocal last_error
            guard()
            try:
                button = d.sales_add_button() if d.checkpoint == 'add.open' else d.sales_list_add_button()
                guard()
                d._guard_frame()
                return button
            except Cancelled:
                raise
            except NeedsAttention as error:
                last_error = error
                guard()
                return None

        try:
            return read_until(observe, lambda value: value is not None, d.cancel,
                              timeout=timeout, label='danh sách Bán hàng và nút Thêm sẵn sàng')
        except Cancelled:
            raise
        except NeedsAttention as error:
            detail = str(last_error or error)
            raise NeedsAttention('Chưa xác nhận được danh sách Bán hàng và nút Thêm: '
                                 + detail + '. ' + str(error)) from (last_error or error)


