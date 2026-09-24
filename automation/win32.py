"""Windows implementation for the standalone app (not a Codex helper)."""
import ctypes as C
from ctypes import wintypes as W
from contextlib import contextmanager
import math
import os
import re
import sys
import time
from datetime import datetime, timezone
from dataclasses import dataclass
from PIL import ImageGrab


user32 = C.WinDLL('user32', use_last_error=True)
kernel32 = C.WinDLL('kernel32', use_last_error=True)
advapi32 = C.WinDLL('advapi32', use_last_error=True)


def bind(lib, name, args, result):
    fn = getattr(lib, name)
    fn.argtypes, fn.restype = args, result
    return fn


ENUMPROC = C.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)
bind(user32, 'EnumWindows', [ENUMPROC, W.LPARAM], W.BOOL)
bind(user32, 'EnumChildWindows', [W.HWND, ENUMPROC, W.LPARAM], W.BOOL)
bind(user32, 'IsWindowEnabled', [W.HWND], W.BOOL)
bind(user32, 'GetClassNameW', [W.HWND, W.LPWSTR, C.c_int], C.c_int)
bind(user32, 'GetDlgCtrlID', [W.HWND], C.c_int)
bind(user32, 'GetParent', [W.HWND], W.HWND)
bind(user32, 'GetWindow', [W.HWND, W.UINT], W.HWND)
bind(user32, 'GetWindowLongW', [W.HWND, C.c_int], W.LONG)
bind(user32, 'SendMessageTimeoutW',
     [W.HWND, W.UINT, W.WPARAM, W.LPARAM, W.UINT, W.UINT, C.POINTER(C.c_size_t)], C.c_ssize_t)
bind(user32, 'IsWindowVisible', [W.HWND], W.BOOL)
bind(user32, 'IsWindow', [W.HWND], W.BOOL)
bind(user32, 'IsChild', [W.HWND, W.HWND], W.BOOL)
bind(user32, 'GetWindowTextLengthW', [W.HWND], C.c_int)
bind(user32, 'GetWindowTextW', [W.HWND, W.LPWSTR, C.c_int], C.c_int)
bind(user32, 'GetWindowThreadProcessId', [W.HWND, C.POINTER(W.DWORD)], W.DWORD)
bind(user32, 'GetWindowRect', [W.HWND, C.POINTER(W.RECT)], W.BOOL)
if hasattr(user32, 'GetDpiForWindow'):
    bind(user32, 'GetDpiForWindow', [W.HWND], W.UINT)
bind(user32, 'GetForegroundWindow', [], W.HWND)
bind(user32, 'SetForegroundWindow', [W.HWND], W.BOOL)
bind(user32, 'ShowWindow', [W.HWND, C.c_int], W.BOOL)
bind(user32, 'IsIconic', [W.HWND], W.BOOL)
bind(user32, 'GetAncestor', [W.HWND, W.UINT], W.HWND)
bind(kernel32, 'OpenProcess', [W.DWORD, W.BOOL, W.DWORD], W.HANDLE)
bind(kernel32, 'CloseHandle', [W.HANDLE], W.BOOL)
bind(kernel32, 'QueryFullProcessImageNameW',
     [W.HANDLE, W.DWORD, W.LPWSTR, C.POINTER(W.DWORD)], W.BOOL)
bind(advapi32, 'OpenProcessToken', [W.HANDLE, W.DWORD, C.POINTER(W.HANDLE)], W.BOOL)
bind(advapi32, 'GetTokenInformation',
     [W.HANDLE, C.c_int, W.LPVOID, W.DWORD, C.POINTER(W.DWORD)], W.BOOL)


class GUITHREADINFO(C.Structure):
    _fields_ = [('cbSize', W.DWORD), ('flags', W.DWORD),
                ('hwndActive', W.HWND), ('hwndFocus', W.HWND),
                ('hwndCapture', W.HWND), ('hwndMenuOwner', W.HWND),
                ('hwndMoveSize', W.HWND), ('hwndCaret', W.HWND), ('rcCaret', W.RECT)]


bind(user32, 'GetGUIThreadInfo', [W.DWORD, C.POINTER(GUITHREADINFO)], W.BOOL)
bind(user32, 'ClientToScreen', [W.HWND, C.POINTER(W.POINT)], W.BOOL)
bind(user32, 'GetClientRect', [W.HWND, C.POINTER(W.RECT)], W.BOOL)
bind(user32, 'CreateWindowExW', [W.DWORD, W.LPCWSTR, W.LPCWSTR, W.DWORD,
     C.c_int, C.c_int, C.c_int, C.c_int, W.HWND, W.HMENU, W.HINSTANCE, W.LPVOID], W.HWND)
bind(user32, 'DestroyWindow', [W.HWND], W.BOOL)
bind(user32, 'OpenClipboard', [W.HWND], W.BOOL)
bind(user32, 'EmptyClipboard', [], W.BOOL)
bind(user32, 'CloseClipboard', [], W.BOOL)
bind(user32, 'SetClipboardData', [W.UINT, W.HANDLE], W.HANDLE)
bind(user32, 'GetClipboardSequenceNumber', [], W.DWORD)
bind(kernel32, 'GlobalAlloc', [W.UINT, C.c_size_t], W.HGLOBAL)
bind(kernel32, 'GlobalLock', [W.HGLOBAL], W.LPVOID)
bind(kernel32, 'GlobalUnlock', [W.HGLOBAL], W.BOOL)
bind(kernel32, 'GlobalFree', [W.HGLOBAL], W.HGLOBAL)


class WindowError(RuntimeError):
    pass


def process_birth(pid):
    process = kernel32.OpenProcess(0x1000, False, pid)
    if not process:
        raise WindowError('Không đọc được định danh phiên MISA.')
    try:
        values = [W.FILETIME() for _ in range(4)]
        get_times = bind(kernel32, 'GetProcessTimes', [W.HANDLE] + [C.POINTER(W.FILETIME)] * 4, W.BOOL)
        if not get_times(process, *(C.byref(value) for value in values)):
            raise WindowError('Không đọc được thời điểm tạo tiến trình MISA.')
        return (values[0].dwHighDateTime << 32) | values[0].dwLowDateTime
    finally:
        kernel32.CloseHandle(process)


class ElevationRequired(WindowError):
    pass


class MisaNotRunning(WindowError):
    pass


class AmbiguousMisa(WindowError):
    pass


class InputUncertain(WindowError):
    pass


def enable_dpi_awareness():
    try:
        fn = bind(user32, 'SetProcessDpiAwarenessContext', [C.c_void_p], W.BOOL)
        fn(C.c_void_p(-4))
    except AttributeError:
        user32.SetProcessDPIAware()


def process_path(pid):
    process = kernel32.OpenProcess(0x1000, False, pid)
    if not process:
        return ''
    try:
        size = W.DWORD(32768)
        buffer = C.create_unicode_buffer(size.value)
        if kernel32.QueryFullProcessImageNameW(process, 0, buffer, C.byref(size)):
            return buffer.value
        return ''
    finally:
        kernel32.CloseHandle(process)


def elevated(pid):
    process = kernel32.OpenProcess(0x1000, False, pid)
    if not process:
        raise WindowError('Không đọc được quyền của tiến trình MISA.')
    token = W.HANDLE()
    try:
        if not advapi32.OpenProcessToken(process, 8, C.byref(token)):
            raise WindowError('Không đọc được mức quyền của MISA.')
        value, size = W.DWORD(), W.DWORD()
        if not advapi32.GetTokenInformation(token, 20, C.byref(value),
                                            C.sizeof(value), C.byref(size)):
            raise WindowError('Không kiểm tra được mức quyền Windows.')
        return bool(value.value)
    finally:
        if token:
            kernel32.CloseHandle(token)
        kernel32.CloseHandle(process)


@dataclass(frozen=True)
class MisaWindow:
    hwnd: int
    pid: int
    title: str


def find_misa():
    found = []

    @ENUMPROC
    def visit(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        title = C.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, title, len(title))
        if not title.value.startswith('MISA SME'):
            return True
        # Save questions share the MISA SME title but have an owner window.
        if user32.GetWindow(hwnd, 4):
            return True
        pid = W.DWORD()
        user32.GetWindowThreadProcessId(hwnd, C.byref(pid))
        name = os.path.basename(process_path(pid.value)).lower()
        if name.startswith('misa.sme') and name.endswith('.exe'):
            found.append(MisaWindow(hwnd, pid.value, title.value))
        return True

    user32.EnumWindows(visit, 0)
    if not found:
        raise MisaNotRunning('Chưa có cửa sổ chính MISA.')
    if len(found) != 1:
        raise AmbiguousMisa(f'Có {len(found)} cửa sổ chính MISA. Hãy giữ lại một cửa sổ cần xử lý.')
    return found[0]


def check_permission(window):
    if elevated(window.pid) and not elevated(os.getpid()):
        raise ElevationRequired(
            'MISA đang chạy quyền quản trị. Hãy đóng tool rồi bấm chuột phải '
            'MISA XML Manager.exe → Run as administrator để chạy cùng mức quyền.')


def foreground_identity():
    """Describe the window that currently owns the foreground."""
    hwnd = user32.GetForegroundWindow()
    pid = W.DWORD()
    if hwnd:
        user32.GetWindowThreadProcessId(hwnd, C.byref(pid))
    size = user32.GetWindowTextLengthW(hwnd) if hwnd else 0
    title = C.create_unicode_buffer(size + 1)
    if hwnd:
        user32.GetWindowTextW(hwnd, title, len(title))
    return {'hwnd': hwnd, 'pid': pid.value, 'title': title.value,
            'process': os.path.basename(process_path(pid.value)) if pid.value else '',
            'own_process': pid.value == os.getpid()}

def _press_alt():
    """Send our own key event so Windows allows this thread to set foreground."""
    keybd = bind(user32, 'keybd_event', [W.BYTE, W.BYTE, W.DWORD, C.c_void_p], None)
    keybd(0x12, 0, 0, None)
    keybd(0x12, 0, 2, None)

def _bring_to_top(hwnd):
    bind(user32, 'BringWindowToTop', [W.HWND], W.BOOL)(hwnd)
    setpos = bind(user32, 'SetWindowPos',
                  [W.HWND, W.HWND, C.c_int, C.c_int, C.c_int, C.c_int, W.UINT], W.BOOL)
    setpos(hwnd, None, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040)

def _set_foreground(hwnd):
    """SetForegroundWindow with the input-queue attach Windows requires."""
    current_thread = bind(kernel32, 'GetCurrentThreadId', [], W.DWORD)()
    foreground = user32.GetForegroundWindow()
    foreground_thread = user32.GetWindowThreadProcessId(foreground, None) if foreground else 0
    attach = bind(user32, 'AttachThreadInput', [W.DWORD, W.DWORD, W.BOOL], W.BOOL)
    attached = bool(foreground_thread and current_thread != foreground_thread
                    and attach(current_thread, foreground_thread, True))
    try:
        user32.SetForegroundWindow(hwnd)
        bind(user32, 'SetActiveWindow', [W.HWND], W.HWND)(hwnd)
    finally:
        if attached:
            attach(current_thread, foreground_thread, False)

def _switch_to(hwnd):
    try:
        bind(user32, 'SwitchToThisWindow', [W.HWND, W.BOOL], None)(hwnd, True)
    except AttributeError:
        pass

def activate(window):
    """Make this MISA window the foreground window and verify it stayed there.

    Restore and raise the window, then escalate only as far as needed: a plain
    SetForegroundWindow, the input-queue attach, a real key event from this
    thread to unlock the foreground lock, and SwitchToThisWindow. The result is
    verified after every attempt; a window that never becomes foreground raises.
    """
    if user32.IsIconic(window.hwnd):
        user32.ShowWindow(window.hwnd, 9)
    user32.ShowWindow(window.hwnd, 5)
    _bring_to_top(window.hwnd)
    # The topmost checklist may still be finishing its WS_EX_NOACTIVATE
    # transition, so retry the whole activation instead of trusting one call.
    # The plain call succeeds while our own panel still owns the foreground
    # (the usual case right after a click). Only later attempts need the
    # input-queue attach and the ALT unlock, which can touch a foreign app.
    for attempt in range(4):
        if user32.GetForegroundWindow() == window.hwnd:
            return
        if attempt == 0:
            user32.SetForegroundWindow(window.hwnd)
        elif attempt == 1:
            _set_foreground(window.hwnd)
        elif attempt == 2:
            _press_alt()
            _set_foreground(window.hwnd)
        else:
            _switch_to(window.hwnd)
            _set_foreground(window.hwnd)
        # Foreground switches are usually immediate. Poll only until this
        # attempt's 200 ms ceiling instead of always paying the full delay.
        deadline = time.monotonic() + .2
        while user32.GetForegroundWindow() != window.hwnd:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            time.sleep(min(.01, remaining))
        if user32.GetForegroundWindow() == window.hwnd:
            return
    ensure_foreground(window)

def ensure_foreground(window):
    if user32.GetForegroundWindow() != window.hwnd:
        raise WindowError('Đã chuyển cửa sổ. Tool dừng nhập để tránh gõ nhầm. '
                          'Đưa phiếu MISA đang nhập lên trước rồi bấm Tiếp tục.')


def bound_window(window):
    """Validate only this handle/process, without following the active app."""
    if not user32.IsWindow(window.hwnd):
        raise WindowError('Cửa sổ MISA được chọn đã đóng.')
    pid = W.DWORD()
    user32.GetWindowThreadProcessId(window.hwnd, C.byref(pid))
    if pid.value != window.pid:
        raise WindowError('Mã cửa sổ đã đổi tiến trình; cần chọn lại MISA.')
    if not user32.IsWindowVisible(window.hwnd):
        raise WindowError('Cửa sổ MISA được chọn không còn hiển thị.')
    size = user32.GetWindowTextLengthW(window.hwnd)
    title = C.create_unicode_buffer(size + 1)
    user32.GetWindowTextW(window.hwnd, title, len(title))
    return MisaWindow(window.hwnd, window.pid, title.value)


def window_rect(window):
    """Return the current native screen rectangle for a bound window."""
    current = bound_window(window)
    rect = W.RECT()
    if not user32.GetWindowRect(current.hwnd, C.byref(rect)):
        raise WindowError('Không đọc được kích thước cửa sổ MISA hiện tại.')
    return (rect.left, rect.top, rect.right, rect.bottom)


def window_lifecycle(window):
    """Read native presence before input; a disabled/covered window is not closed."""
    state = dict(hwnd=window.hwnd, pid=window.pid,
                 observed_at=datetime.now(timezone.utc).isoformat(),
                 exists=False, visible=False, enabled=False, foreground=False)
    if not user32.IsWindow(window.hwnd):
        return state
    pid = W.DWORD()
    if not user32.GetWindowThreadProcessId(window.hwnd, C.byref(pid)):
        if not user32.IsWindow(window.hwnd):
            return state
        raise WindowError('Không đọc được tiến trình của cửa sổ đang chuyển trạng thái.')
    if pid.value != window.pid:
        raise WindowError('Mã cửa sổ đã đổi tiến trình trong lúc chuyển trạng thái.')
    state.update(exists=True, visible=bool(user32.IsWindowVisible(window.hwnd)),
                 enabled=bool(user32.IsWindowEnabled(window.hwnd)),
                 foreground=user32.GetForegroundWindow() == window.hwnd)
    return state


def is_owned_by(window, owner_hwnd):
    """Follow native owners, never infer dialog ownership from its title."""
    current, seen = window.hwnd, set()
    while current and current not in seen:
        if current == owner_hwnd:
            return True
        seen.add(current)
        current = user32.GetWindow(current, 4)  # GW_OWNER
    return False


def _native_field_geometry(box, target):
    """Match an inset native editor to the painted field it represents."""
    a, b, c, d = box
    left, top, right, bottom = target
    target_width, target_height = right - left, bottom - top
    editor_width, editor_height = c - a, d - b
    if min(target_width, target_height, editor_width, editor_height) <= 0:
        return False
    overlap_width = max(0, min(c, right) - max(a, left))
    overlap_height = max(0, min(d, bottom) - max(b, top))
    center_x, center_y = (a + c) / 2, (b + d) / 2
    return (
        overlap_width >= max(8, min(target_width, editor_width) * .45)
        and overlap_height >= max(4, min(target_height, editor_height) * .35)
        and left - 24 <= center_x <= right + 24
        and top - 12 <= center_y <= bottom + 12
    )


def read_control_text(window, bounds, capture_box, *, allow_background=False):
    """Read WM_GETTEXT from the visible child at a current native field rectangle."""
    result = dict(status='unavailable', value=None, valueSource='WM_GETTEXTW',
                  observedAt=datetime.now(timezone.utc).isoformat(), reason='')
    x, y, width, height = bounds
    if (not all(math.isfinite(v) for v in bounds) or width <= 0 or height <= 0
            or x < 0 or y < 0 or x + width > capture_box[2] - capture_box[0]
            or y + height > capture_box[3] - capture_box[1]):
        raise WindowError('Invalid native field bounds for native text observation.')
    left, top = capture_box[0] + x, capture_box[1] + y
    right, bottom = left + width, top + height

    def guard_window():
        current = bound_window(window)
        if not allow_background:
            ensure_foreground(current)
        rectangle = W.RECT()
        if (current.title != window.title
                or not user32.GetWindowRect(window.hwnd, C.byref(rectangle))
                or (rectangle.left, rectangle.top, rectangle.right, rectangle.bottom) != capture_box):
            raise WindowError('Window moved during native field observation.')

    def candidates():
        guard_window()
        found = []

        @ENUMPROC
        def visit(hwnd, _):
            pid, box = W.DWORD(), W.RECT()
            user32.GetWindowThreadProcessId(hwnd, C.byref(pid))
            if (pid.value != window.pid or not user32.IsWindowVisible(hwnd)
                    or not user32.IsChild(window.hwnd, hwnd)
                    or not user32.GetWindowRect(hwnd, C.byref(box))):
                return True
            # WinForms editors can be inset inside the painted field. Match
            # meaningful overlap and a bounded centre, never a loose point.
            if not _native_field_geometry(
                    (box.left, box.top, box.right, box.bottom),
                    (left, top, right, bottom)):
                return True
            name = C.create_unicode_buffer(256)
            user32.GetClassNameW(hwnd, name, len(name))
            if not name.value.casefold().startswith(('edit', 'richedit', 'combobox',
                    'windowsforms10.edit.', 'windowsforms10.combobox.', 'windowsforms10.window.')):
                return True
            found.append((int(hwnd), name.value, user32.GetDlgCtrlID(hwnd),
                          (box.left, box.top, box.right, box.bottom)))
            return True

        user32.EnumChildWindows(window.hwnd, visit, 0)
        return sorted(found)

    found = candidates()
    leaves = [item for item in found if not any(
        other[0] != item[0] and user32.IsChild(item[0], other[0]) for other in found)]
    if len(leaves) != 1:
        result.update(status='ambiguous' if found else 'unavailable',
                      reason='Multiple native fields at target.' if found
                             else 'No native text control matches the field geometry.')
        return result
    # An editor and its wrappers may share the painted field. Prefer the editor;
    # an empty/internal value may fall back only to a wrapper of that same child.
    chain = sorted(found, key=lambda item: sum(user32.IsChild(item[0], other[0]) for other in found))
    if len(chain) > 4 or any(item != leaves[0] and not user32.IsChild(item[0], leaves[0][0]) for item in chain):
        result.update(status='ambiguous', reason='Native controls do not form one bounded editor chain.')
        return result
    empty_observation = None
    for chosen in chain:
        values = []
        for _ in range(2):
            buffer = C.create_unicode_buffer(32768)
            count = C.c_size_t()
            C.set_last_error(0)
            # WM_GETTEXT is read-only and marshalled by Windows across processes.
            if not user32.SendMessageTimeoutW(chosen[0], 0x000D, len(buffer),
                                              C.addressof(buffer), 0x0003, 250, C.byref(count)):
                result['reason'] = 'WM_GETTEXT unavailable or timed out; Win32 error ' + str(C.get_last_error())
                return result
            if count.value >= len(buffer) - 1:
                result['reason'] = 'Native field text may be truncated.'
                return result
            values.append(buffer.value)
        if candidates() != found or values[0] != values[1]:
            result.update(status='ambiguous', reason='Native field identity, bounds or text changed during readback.')
            return result
        value = values[0]
        if re.fullmatch(r'\{?[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\}?', value.strip(), re.I):
            result['reason'] = 'Native editor chain exposes an internal GUID, not displayed text.'
            continue
        if not value.strip():
            # Keep looking because a wrapper in the same verified chain may
            # expose non-empty display text even when the leaf editor is empty.
            empty_observation = chosen
            continue
        result.update(status='supported', reason='', value=value,
                      observedAt=datetime.now(timezone.utc).isoformat(),
                      valueSource='WM_GETTEXTW; hwnd=' + str(chosen[0]) + '; class=' + chosen[1])
        return result
    if empty_observation is not None:
        # Empty is authoritative for callers verifying that a filter was
        # cleared; identity and two stable reads already proved this field.
        result.update(status='supported', reason='', value='',
                      observedAt=datetime.now(timezone.utc).isoformat(),
                      valueSource='WM_GETTEXTW; hwnd=' + str(empty_observation[0])
                                  + '; class=' + empty_observation[1])
        return result
    result['reason'] = result.get('reason') or 'Native editor chain exposes no displayed text.'
    return result


def child_window_texts(window):
    """Read visible Win32 child captions from the currently bound window."""
    check_permission(window)
    values = []
    seen = set()
    def visit(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        if length <= 0:
            return True
        text = C.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, text, len(text))
        value = ' '.join(text.value.split())
        if value and value not in seen:
            seen.add(value)
            values.append(value)
        return True
    callback = ENUMPROC(visit)
    user32.EnumChildWindows(window.hwnd, callback, 0)
    return tuple(values)


def child_window_tree(window, maximum_nodes=5000):
    """Return a bounded read-only Win32 child HWND tree for the bound MISA window."""
    if type(maximum_nodes) is not int or not 1 <= maximum_nodes <= 20000:
        raise WindowError('Win32 child tree limit must be between 1 and 20000.')
    current = bound_window(window)
    outer = W.RECT()
    if not user32.GetWindowRect(current.hwnd, C.byref(outer)):
        raise WindowError('Cannot read MISA window bounds.')
    nodes = []

    @ENUMPROC
    def visit(hwnd, _):
        if len(nodes) >= maximum_nodes:
            return False
        pid, box = W.DWORD(), W.RECT()
        thread_id = user32.GetWindowThreadProcessId(hwnd, C.byref(pid))
        if (pid.value != current.pid or not user32.IsWindow(hwnd)
                or not user32.GetWindowRect(hwnd, C.byref(box))):
            return True
        class_name = C.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, class_name, len(class_name))
        length = user32.GetWindowTextLengthW(hwnd)
        title = C.create_unicode_buffer(length + 1)
        if length:
            user32.GetWindowTextW(hwnd, title, len(title))
        nodes.append({
            'hwnd': int(hwnd), 'pid': pid.value, 'thread_id': int(thread_id),
            'parent_hwnd': int(user32.GetParent(hwnd) or 0),
            'root_hwnd': int(user32.GetAncestor(hwnd, 2) or 0),
            'control_id': int(user32.GetDlgCtrlID(hwnd)),
            'class_name': class_name.value,
            'text': ' '.join(title.value.split()),
            'bounds': [box.left - outer.left, box.top - outer.top,
                       box.right - box.left, box.bottom - box.top],
            'visible': bool(user32.IsWindowVisible(hwnd)),
            'enabled': bool(user32.IsWindowEnabled(hwnd)),
        })
        return True

    user32.EnumChildWindows(current.hwnd, visit, 0)
    return {
        'window_hwnd': current.hwnd, 'pid': current.pid,
        'window_title': current.title,
        'bounds': [outer.left, outer.top, outer.right, outer.bottom],
        'node_count': len(nodes), 'truncated': len(nodes) >= maximum_nodes,
        'nodes': nodes,
        'source': 'Win32.EnumChildWindows',
        'observed_at': datetime.now(timezone.utc).isoformat(),
    }


def dialog_child_controls(window):
    """Read visible native dialog children with their current HWND and bounds."""
    current = bound_window(window)
    ensure_foreground(current)
    outer = W.RECT()
    if not user32.GetWindowRect(window.hwnd, C.byref(outer)):
        raise WindowError('Cannot read dialog bounds.')
    result = []

    @ENUMPROC
    def visit(hwnd, _):
        pid, box = W.DWORD(), W.RECT()
        user32.GetWindowThreadProcessId(hwnd, C.byref(pid))
        if (pid.value != window.pid or not user32.IsWindowVisible(hwnd)
                or not user32.GetWindowRect(hwnd, C.byref(box))):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        if not length:
            return True
        value = C.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, value, len(value))
        name = C.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, name, len(name))
        text = ' '.join(value.value.split())
        if text and outer.left <= box.left < box.right <= outer.right and outer.top <= box.top < box.bottom <= outer.bottom:
            result.append(dict(hwnd=int(hwnd), pid=pid.value, text=text,
                               class_name=name.value,
                               bounds=(box.left - outer.left, box.top - outer.top,
                                       box.right - box.left, box.bottom - box.top)))
        return True

    user32.EnumChildWindows(window.hwnd, visit, 0)
    ensure_foreground(bound_window(window))
    return result


def input_focus(window, bounds, capture_box):
    """Observe keyboard focus at a native field; never move focus or type."""
    current = bound_window(window)
    ensure_foreground(current)
    rectangle = W.RECT()
    if (current.title != window.title or not user32.IsWindowEnabled(window.hwnd)
            or not user32.GetWindowRect(window.hwnd, C.byref(rectangle))):
        raise WindowError('Cửa sổ MISA đã đổi trước khi kiểm tra ô nhận bàn phím.')
    x, y, width, height = bounds
    if (not all(math.isfinite(value) for value in bounds) or min(width, height) <= 0
            or x < 0 or y < 0 or x + width > rectangle.right - rectangle.left
            or y + height > rectangle.bottom - rectangle.top):
        raise WindowError('Vùng ô native không hợp lệ trước khi nhập.')
    left, top = rectangle.left + x, rectangle.top + y
    right, bottom = left + width, top + height
    result = dict(matched=False, reason='', observed_at=datetime.now(timezone.utc).isoformat(),
                  window_hwnd=window.hwnd, pid=window.pid, target_bounds=[left, top, right, bottom])
    pid = W.DWORD()
    thread = user32.GetWindowThreadProcessId(window.hwnd, C.byref(pid))
    info = GUITHREADINFO(cbSize=C.sizeof(GUITHREADINFO))
    if pid.value != window.pid or not thread or not user32.GetGUIThreadInfo(thread, C.byref(info)):
        raise WindowError('Không đọc được ô đang nhận bàn phím của MISA.')
    focus = int(info.hwndFocus or 0)
    result.update(focus_hwnd=focus, active_hwnd=int(info.hwndActive or 0), flags=int(info.flags))
    if not focus or info.hwndActive != window.hwnd or info.flags & 0x1E:
        result['reason'] = 'MISA đang chuyển focus hoặc có menu/thao tác cửa sổ.'
        return result
    focused = _window_identity(focus)
    result['focus'] = focused
    if (focused.get('pid') != window.pid or focused.get('root_hwnd') != window.hwnd
            or not focused.get('visible') or not focused.get('enabled') or not focused.get('rect')):
        result['reason'] = 'Focus chưa thuộc ô nhập trong cửa sổ đã chọn.'
        return result
    native_edit = focused['class_name'].casefold().startswith(('edit', 'richedit', 'windowsforms10.edit.'))
    edit_readonly_style = 1 << 11  # ES_READONLY on native Edit controls.
    if native_edit and user32.GetWindowLongW(focus, -16) & edit_readonly_style:
        result['reason'] = 'Ô đang nhận focus chỉ cho phép đọc.'
        return result
    # WinForms often creates a real EDIT child inset inside the painted field.
    # Keep the native identity/owner checks above, then apply
    # the same bounded geometry standard used by native readback.
    target_width, target_height = right - left, bottom - top
    field_matches = _native_field_geometry(focused['rect'], (left, top, right, bottom))
    caret_matches = False
    caret_hwnd = int(info.hwndCaret or 0)
    if (caret_hwnd and (caret_hwnd == focus or user32.IsChild(focus, caret_hwnd))
            and user32.GetAncestor(caret_hwnd, 2) == window.hwnd
            and info.rcCaret.bottom > info.rcCaret.top):
        start = W.POINT(info.rcCaret.left, info.rcCaret.top)
        end = W.POINT(info.rcCaret.right, info.rcCaret.bottom)
        if user32.ClientToScreen(caret_hwnd, C.byref(start)) and user32.ClientToScreen(caret_hwnd, C.byref(end)):
            result['caret_bounds'] = [start.x, start.y, end.x, end.y]
            # Some DevExpress grids retain container focus; only a caret in
            # the current field can establish their active editor. The
            # caret may be inset or a few pixels above the painted wrapper.
            caret_width = max(1, end.x - start.x)
            caret_height = max(1, end.y - start.y)
            caret_overlap_width = max(0, min(end.x, right + 6) - max(start.x, left - 6))
            caret_overlap_height = max(0, min(end.y, bottom + 6) - max(start.y, top - 6))
            caret_matches = (
                caret_overlap_width >= min(caret_width, max(1, target_width * .02))
                and caret_overlap_height >= min(caret_height, max(1, target_height * .25))
                and left - 6 <= (start.x + end.x) / 2 <= right + 6
                and top - 6 <= (start.y + end.y) / 2 <= bottom + 6
            )
    ensure_foreground(current)
    result.update(matched=field_matches or caret_matches,
                  source='native_field_bounds' if field_matches else 'native_caret' if caret_matches else '',
                  reason='' if field_matches or caret_matches else 'Focus/caret chưa ở ô native vừa định vị.')
    return result


def _overlay_masks(window, box):
    """Return visible foreign top-level windows layered above ``window``.

    ``ImageGrab`` captures the composited desktop, so the tool's own floating
    checklist is excluded by the compositor on supported Windows versions.
    Other overlays above MISA are still masked and cannot supply native targets.
    """
    left, top, right, bottom = box
    masks = []

    @ENUMPROC
    def visit(hwnd, _):
        if hwnd == window.hwnd:
            return False  # EnumWindows is front-to-back; windows below do not cover MISA.
        if not user32.IsWindowVisible(hwnd):
            return True
        pid = W.DWORD()
        user32.GetWindowThreadProcessId(hwnd, C.byref(pid))
        if pid.value == window.pid:
            return True
        if pid.value == os.getpid() and sys.getwindowsversion().build >= 19041:
            affinity = W.DWORD()
            get_affinity = bind(user32, 'GetWindowDisplayAffinity', [W.HWND, C.POINTER(W.DWORD)], W.BOOL)
            if get_affinity(hwnd, C.byref(affinity)) and affinity.value == 0x11:
                return True
        rect = W.RECT()
        if not user32.GetWindowRect(hwnd, C.byref(rect)):
            return True
        x1, y1 = max(left, rect.left), max(top, rect.top)
        x2, y2 = min(right, rect.right), min(bottom, rect.bottom)
        if x1 < x2 and y1 < y2:
            masks.append((x1 - left, y1 - top, x2 - left, y2 - top))
        return True

    user32.EnumWindows(visit, 0)
    return masks


def capture(window, *, evidence_context=None):
    ensure_foreground(window)
    rect = W.RECT()
    if not user32.GetWindowRect(window.hwnd, C.byref(rect)):
        raise WindowError('Không đọc được kích thước cửa sổ MISA.')
    box = (rect.left, rect.top, rect.right, rect.bottom)
    if not readable_window(window):
        raise WindowError('Cửa sổ MISA quá nhỏ để đọc nội dung.')
    picture = ImageGrab.grab(bbox=box, all_screens=True).convert('RGB')
    masks = _overlay_masks(window, box)
    if masks and evidence_context:
        from .performance import record_image
        original_id = record_image(picture, role='window_original',
            **dict(evidence_context, capture_box=list(box), window_size=list(picture.size)))
        picture.info['misa_original_image_id'] = original_id
    for mask in masks:
        picture.paste((0, 0, 0), mask)
    picture.info['misa_masked_regions'] = [list(mask) for mask in masks]
    return picture, box


ULONG_PTR = C.c_size_t


class KEYBDINPUT(C.Structure):
    _fields_ = [('wVk', W.WORD), ('wScan', W.WORD), ('dwFlags', W.DWORD),
                ('time', W.DWORD), ('dwExtraInfo', ULONG_PTR)]


class MOUSEINPUT(C.Structure):
    _fields_ = [('dx', W.LONG), ('dy', W.LONG), ('mouseData', W.DWORD),
                ('dwFlags', W.DWORD), ('time', W.DWORD),
                ('dwExtraInfo', ULONG_PTR)]


class HARDWAREINPUT(C.Structure):
    _fields_ = [('uMsg', W.DWORD), ('wParamL', W.WORD), ('wParamH', W.WORD)]


class INPUTUNION(C.Union):
    _fields_ = [('mi', MOUSEINPUT), ('ki', KEYBDINPUT), ('hi', HARDWAREINPUT)]


class INPUT(C.Structure):
    _anonymous_ = ('data',)
    _fields_ = [('type', W.DWORD), ('data', INPUTUNION)]


bind(user32, 'SendInput', [W.UINT, C.POINTER(INPUT), C.c_int], W.UINT)


def _window_identity(hwnd):
    """Record window relationships without reading another app's text."""
    hwnd = int(hwnd or 0)
    if not hwnd:
        return {'hwnd': 0}
    pid = W.DWORD()
    thread_id = user32.GetWindowThreadProcessId(hwnd, C.byref(pid))
    name = C.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, name, len(name))
    rect = W.RECT()
    has_rect = user32.GetWindowRect(hwnd, C.byref(rect))
    owners, current = [], hwnd
    for _ in range(16):
        current = int(user32.GetWindow(current, 4) or 0)  # GW_OWNER
        if not current or current in owners:
            break
        owners.append(current)
    return {'hwnd': hwnd, 'pid': pid.value, 'thread_id': int(thread_id),
            'class_name': name.value, 'parent_hwnd': int(user32.GetParent(hwnd) or 0),
            'root_hwnd': int(user32.GetAncestor(hwnd, 2) or 0),
            'root_owner_hwnd': int(user32.GetAncestor(hwnd, 3) or 0),
            'owner_chain': owners, 'visible': bool(user32.IsWindowVisible(hwnd)),
            'enabled': bool(user32.IsWindowEnabled(hwnd)),
            'rect': [rect.left, rect.top, rect.right, rect.bottom] if has_rect else None}


KEYEVENTF_KEYUP = 0x0002


def _send_keyboard(window, inputs, *, focus_guard=None):
    """Show MISA and send a bounded keyboard batch at one input boundary."""
    check_permission(window)
    if user32.GetForegroundWindow() != window.hwnd:
        activate(window)
    ensure_foreground(window)
    batch = (INPUT * len(inputs))(*inputs)
    if focus_guard is not None:
        focus_guard()
    if user32.SendInput(len(batch), batch, C.sizeof(INPUT)) != len(batch):
        # Release the keys in this batch only; never replay an uncertain write.
        releases = [item for item in inputs if item.ki.dwFlags & KEYEVENTF_KEYUP]
        if releases:
            cleanup = (INPUT * len(releases))(*releases)
            user32.SendInput(len(cleanup), cleanup, C.sizeof(INPUT))
        raise InputUncertain('Windows chưa xác nhận thao tác bàn phím hoàn tất.')


def keypress(window, vk, *, ctrl=False, shift=False, alt=False, focus_guard=None):
    """Press one virtual key through Win32 SendInput."""
    modifiers = []
    if ctrl:
        modifiers.append(0x11)
    if shift:
        modifiers.append(0x10)
    if alt:
        modifiers.append(0x12)
    events = [INPUT(type=1, ki=KEYBDINPUT(vk, 0, 0, 0, 0)) for vk in modifiers]
    events.append(INPUT(type=1, ki=KEYBDINPUT(vk, 0, 0, 0, 0)))
    events.append(INPUT(type=1, ki=KEYBDINPUT(vk, 0, KEYEVENTF_KEYUP, 0, 0)))
    events.extend(INPUT(type=1, ki=KEYBDINPUT(modifier, 0, KEYEVENTF_KEYUP, 0, 0))
                  for modifier in reversed(modifiers))
    _send_keyboard(window, events, focus_guard=focus_guard)


def direct_click(window, point, *, coordinate_space='window'):
    """Activate MISA and send a click from window- or screen-relative coords."""
    activate(window)
    rect = W.RECT()
    if not user32.GetWindowRect(window.hwnd, C.byref(rect)):
        raise WindowError('Không đọc được vị trí cửa sổ MISA.')
    x, y = int(point[0]), int(point[1])
    if coordinate_space != 'screen':
        x, y = rect.left + x, rect.top + y
    vx, vy = user32.GetSystemMetrics(76), user32.GetSystemMetrics(77)
    vw, vh = user32.GetSystemMetrics(78), user32.GetSystemMetrics(79)
    events = (INPUT * 3)(
        INPUT(type=0, mi=MOUSEINPUT(round((x - vx) * 65535 / max(1, vw - 1)),
                                     round((y - vy) * 65535 / max(1, vh - 1)),
                                     0, 0x8000 | 0x4000 | 0x0001, 0, 0)),
        INPUT(type=0, mi=MOUSEINPUT(0, 0, 0, 0x0002, 0, 0)),
        INPUT(type=0, mi=MOUSEINPUT(0, 0, 0, 0x0004, 0, 0)))
    if user32.SendInput(3, events, C.sizeof(INPUT)) != 3:
        raise InputUncertain('Windows không xác nhận click trực tiếp.')


def click_dialog_button(window, button_hwnd, caption):
    """Invoke one verified native dialog button without coordinate guessing."""
    current = bound_window(window)
    ensure_foreground(current)
    button_hwnd = int(button_hwnd or 0)
    if (not button_hwnd or not user32.IsWindow(button_hwnd)
            or not user32.IsChild(current.hwnd, button_hwnd)):
        raise WindowError('Native button no longer belongs to the current MISA dialog.')
    if not user32.IsWindowVisible(button_hwnd) or not user32.IsWindowEnabled(button_hwnd):
        raise WindowError('Native MISA button is not ready.')
    length = user32.GetWindowTextLengthW(button_hwnd)
    text = C.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(button_hwnd, text, len(text))
    class_name = C.create_unicode_buffer(256)
    user32.GetClassNameW(button_hwnd, class_name, len(class_name))
    if 'button' not in class_name.value.casefold() or ' '.join(text.value.split()) != caption:
        raise WindowError('Native MISA button identity changed before input.')
    result = C.c_size_t()
    # BM_CLICK lets the dialog dispatch its own command without DPI offsets.
    delivered = user32.SendMessageTimeoutW(
        button_hwnd, 0x00F5, 0, 0, 0x0002, 1000, C.byref(result))
    if not delivered:
        raise InputUncertain('Windows did not confirm the native MISA button click.')


def direct_keypress(window, vk, *, ctrl=False, shift=False, alt=False):
    """Activate MISA and send a key without workflow checkpoint checks."""
    activate(window)
    modifiers = [value for enabled, value in ((ctrl, 0x11), (shift, 0x10), (alt, 0x12)) if enabled]
    events = [INPUT(type=1, ki=KEYBDINPUT(value, 0, 0, 0, 0)) for value in modifiers]
    events.extend((INPUT(type=1, ki=KEYBDINPUT(vk, 0, 0, 0, 0)),
                   INPUT(type=1, ki=KEYBDINPUT(vk, 0, KEYEVENTF_KEYUP, 0, 0))))
    events.extend(INPUT(type=1, ki=KEYBDINPUT(value, 0, KEYEVENTF_KEYUP, 0, 0))
                  for value in reversed(modifiers))
    batch = (INPUT * len(events))(*events)
    if user32.SendInput(len(batch), batch, C.sizeof(INPUT)) != len(batch):
        raise InputUncertain('Windows không xác nhận phím trực tiếp.')


def direct_text(window, text):
    """Replace the active editor value with bounded Unicode text."""
    with prepared_clipboard_text(str(text)) as sequence:
        # Direct control intentionally does not require editor identity;
        # Ctrl+A makes the requested value replace the current field.
        direct_keypress(window, 0x41, ctrl=True)
        if not sequence:
            direct_keypress(window, 0x2e)
            return
        direct_keypress(window, 0x56, ctrl=True)


def direct_scroll(window, point, ticks):
    """Scroll at a screen point without workflow hit-test checks."""
    activate(window)
    rect = W.RECT()
    if not user32.GetWindowRect(window.hwnd, C.byref(rect)):
        raise WindowError('Không đọc được vị trí cửa sổ MISA.')
    x, y = int(point[0]), int(point[1])
    vx, vy = user32.GetSystemMetrics(76), user32.GetSystemMetrics(77)
    vw, vh = user32.GetSystemMetrics(78), user32.GetSystemMetrics(79)
    move = INPUT(type=0, mi=MOUSEINPUT(round((x - vx) * 65535 / max(1, vw - 1)),
                                         round((y - vy) * 65535 / max(1, vh - 1)),
                                         0, 0x8000 | 0x4000 | 0x0001, 0, 0))
    wheel = INPUT(type=0, mi=MOUSEINPUT(0, 0, int(ticks) * 120, 0x0800, 0, 0))
    batch = (INPUT * 2)(move, wheel)
    if user32.SendInput(2, batch, C.sizeof(INPUT)) != 2:
        raise InputUncertain('Windows không xác nhận cuộn trực tiếp.')


@contextmanager
def prepared_clipboard_text(text):
    """Publish one scalar before selection, keeping its data until MISA reads it."""
    if len(text) > 32768 or any(ord(char) < 32 or 0x7f <= ord(char) <= 0x9f for char in text):
        raise WindowError('Clipboard input must be a bounded scalar without control characters.')
    if not text:
        yield None
        return
    data = text.encode('utf-16-le') + b'\0\0'
    memory = owner = None
    opened = False
    try:
        memory = kernel32.GlobalAlloc(0x0002, len(data))  # GMEM_MOVEABLE
        if not memory:
            raise WindowError('Cannot allocate clipboard text.')
        pointer = kernel32.GlobalLock(memory)
        if not pointer:
            raise WindowError('Cannot lock clipboard text.')
        try:
            C.memmove(pointer, data, len(data))
        finally:
            C.set_last_error(0)
            if not kernel32.GlobalUnlock(memory) and C.get_last_error():
                raise WindowError('Cannot unlock clipboard text.')
        # A private message-only owner avoids assigning clipboard ownership to
        # MISA or activating a window. Text is supplied now, never delay-rendered.
        owner = user32.CreateWindowExW(0, 'STATIC', None, 0, 0, 0, 0, 0,
                                      -3, None, None, None)  # HWND_MESSAGE
        if not owner:
            raise WindowError('Cannot create clipboard owner.')
        if not user32.OpenClipboard(owner):
            raise WindowError('Clipboard is busy; no text was sent.')
        opened = True
        if not user32.EmptyClipboard():
            raise WindowError('Cannot prepare clipboard text.')
        if not user32.SetClipboardData(13, memory):  # CF_UNICODETEXT
            raise WindowError('Cannot publish clipboard text.')
        memory = None  # Windows owns the allocation after SetClipboardData.
        if not user32.CloseClipboard():
            raise WindowError('Cannot release clipboard for MISA.')
        opened = False
        sequence = int(user32.GetClipboardSequenceNumber())
        if not sequence:
            raise WindowError('Cannot identify the prepared clipboard value.')
        yield sequence
    finally:
        errors = []
        if opened and not user32.CloseClipboard():
            errors.append('close clipboard')
        if memory and kernel32.GlobalFree(memory):
            errors.append('free clipboard text')
        if owner and not user32.DestroyWindow(owner):
            errors.append('destroy clipboard owner')
        if errors:
            raise WindowError('Clipboard cleanup failed: ' + ', '.join(errors))
    # Do not restore clipboard contents: SendInput returning does not mean the
    # target has consumed Ctrl+V. Fresh field readback verifies the result.


def paste_text(window, sequence, *, focus_guard):
    """Send one guarded paste; never retry a changed clipboard or partial input."""
    def guard():
        focus_guard()
        if not sequence or int(user32.GetClipboardSequenceNumber()) != sequence:
            raise WindowError('Clipboard changed before paste; no text was sent.')

    keypress(window, 0x56, ctrl=True, focus_guard=guard)
    if int(user32.GetClipboardSequenceNumber()) != sequence:
        raise InputUncertain('Clipboard changed during paste; inspect the current field value.')


def replace_text(window, sequence, *, focus_guard):
    """Replace the focused editor in one keyboard batch.

    Foreground/focus are checked once, immediately before SendInput. Combining
    Ctrl+A and Ctrl+V removes the gap in which a user move or click could
    invalidate an otherwise valid edit.
    """
    if not sequence or int(user32.GetClipboardSequenceNumber()) != sequence:
        raise WindowError('Clipboard changed before paste; no text was sent.')
    def guard():
        focus_guard()
        if int(user32.GetClipboardSequenceNumber()) != sequence:
            raise WindowError('Clipboard changed before paste; no text was sent.')
    events = [
        INPUT(type=1, ki=KEYBDINPUT(0x11, 0, 0, 0, 0)),
        INPUT(type=1, ki=KEYBDINPUT(0x41, 0, 0, 0, 0)),
        INPUT(type=1, ki=KEYBDINPUT(0x41, 0, KEYEVENTF_KEYUP, 0, 0)),
        INPUT(type=1, ki=KEYBDINPUT(0x56, 0, 0, 0, 0)),
        INPUT(type=1, ki=KEYBDINPUT(0x56, 0, KEYEVENTF_KEYUP, 0, 0)),
        INPUT(type=1, ki=KEYBDINPUT(0x11, 0, KEYEVENTF_KEYUP, 0, 0)),
    ]
    _send_keyboard(window, events, focus_guard=guard)
    if int(user32.GetClipboardSequenceNumber()) != sequence:
        raise InputUncertain('Clipboard changed during paste; inspect the current field value.')


def foreground_for_process(pid):
    hwnd = user32.GetForegroundWindow()
    current_pid = W.DWORD()
    user32.GetWindowThreadProcessId(hwnd, C.byref(current_pid))
    if current_pid.value != pid:
        raise WindowError('Đã chuyển sang ứng dụng khác. Tool dừng thao tác.')
    length = user32.GetWindowTextLengthW(hwnd)
    title = C.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, title, len(title))
    return MisaWindow(hwnd, pid, title.value)


def select_misa(*, restore=True):
    """Prefer the active form; don't activate a parent behind an owned dialog."""
    main = find_misa()

    def titled_owner(window):
        """Bind transient untitled popups to their titled MISA owner."""
        current, seen = window.hwnd, set()
        while current and current not in seen:
            seen.add(current)
            pid = W.DWORD()
            user32.GetWindowThreadProcessId(current, C.byref(pid))
            if pid.value != main.pid:
                return window
            length = user32.GetWindowTextLengthW(current)
            title = C.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(current, title, len(title))
            if title.value:
                return MisaWindow(current, main.pid, title.value)
            current = user32.GetWindow(current, 4)  # GW_OWNER
        return window

    preferred = []
    try:
        current = foreground_for_process(main.pid)
        if current.hwnd != main.hwnd:
            preferred.append(titled_owner(current))
    except WindowError:
        pass
    # GetLastActivePopup can return a dialog even while the tool has focus.
    fn = bind(user32, 'GetLastActivePopup', [W.HWND], W.HWND)
    popup = fn(main.hwnd)
    if popup and popup != main.hwnd and user32.IsWindowVisible(popup):
        pid = W.DWORD()
        user32.GetWindowThreadProcessId(popup, C.byref(pid))
        if pid.value == main.pid:
            length = user32.GetWindowTextLengthW(popup)
            title = C.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(popup, title, len(title))
            preferred.append(titled_owner(MisaWindow(popup, main.pid, title.value)))
    # SME also opens modeless vouchers that aren't owned popups of its main
    # frame. Do not inspect the main frame through/behind an open voucher.
    others = [w for w in misa_windows() if w.pid == main.pid and w.hwnd != main.hwnd]
    for window in preferred:
        if readable_window(window):
            return window
    usable_others = [window for window in others if readable_window(window)]
    if len(usable_others) == 1:
        return usable_others[0]
    if len(usable_others) > 1:
        raise AmbiguousMisa('MISA có nhiều cửa sổ phụ; chọn Phiếu thu cần nhập rồi Tiếp tục.')
    # Background callers must keep an explicitly selected minimized child even
    # when the MISA main frame is still restored behind it.
    ordered = [*preferred, *others, main]
    if not restore:
        # In background mode prefer the last-active minimized child (for
        # example a voucher) before falling back to a restored main frame.
        minimized_preferred = [window for window in preferred
                               if user32.IsIconic(window.hwnd) and user32.IsWindowVisible(window.hwnd)]
        if len(minimized_preferred) == 1:
            return minimized_preferred[0]
        if len(minimized_preferred) > 1:
            raise AmbiguousMisa('MISA có nhiều cửa sổ phụ đang thu nhỏ; hãy chọn đúng cửa sổ cần xử lý.')
        minimized = [window for window in ordered
                     if user32.IsIconic(window.hwnd) and user32.IsWindowVisible(window.hwnd)]
        if len(minimized) == 1:
            return minimized[0]
        if len(minimized) > 1:
            raise AmbiguousMisa('MISA có nhiều cửa sổ đang thu nhỏ; hãy chọn đúng cửa sổ cần xử lý.')
    if readable_window(main):
        return main
    # Foreground callers can restore a minimized window before deciding it is unusable.
    restored = False
    for window in ordered:
        if restore and user32.IsIconic(window.hwnd):
            user32.ShowWindow(window.hwnd, 9)
            restored = True
    if restored:
        for window in ordered:
            if readable_window(window):
                return window
    details = ', '.join(f'{window.title[:40]} {window_size(window)[0]}x{window_size(window)[1]}'
                        + (' (thu nhỏ)' if user32.IsIconic(window.hwnd) else '')
                        for window in ordered)
    raise WindowError('Cửa sổ MISA quá nhỏ để đọc bảng: ' + details + '. Mở rộng cửa sổ MISA rồi thử lại.')


def window_size(window):
    """Current width and height of a window, in screen pixels."""
    rect = W.RECT()
    if not user32.GetWindowRect(window.hwnd, C.byref(rect)):
        return (0, 0)
    return (max(0, rect.right - rect.left), max(0, rect.bottom - rect.top))


def readable_window(window, *, minimum=None):
    """A window the native driver can actually read: restored and large enough."""
    if user32.IsIconic(window.hwnd):
        return False
    if minimum is None:
        minimum = (200, 100) if user32.GetWindow(window.hwnd, 4) else (300, 200)
    width, height = window_size(window)
    # MISA can report a transient bounds pair while its frame is being raised;
    # confirm one insufficient read before treating the live window as unusable.
    if width < minimum[0] or height < minimum[1]:
        width, height = window_size(window)
    return width >= minimum[0] and height >= minimum[1]


def misa_windows():
    """Includes splash/login windows whose titles do not start with MISA SME."""
    found, paths = [], {}

    @ENUMPROC
    def visit(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        pid = W.DWORD()
        user32.GetWindowThreadProcessId(hwnd, C.byref(pid))
        if pid.value not in paths:
            paths[pid.value] = os.path.basename(process_path(pid.value)).lower()
        name = paths[pid.value]
        if name.startswith('misa.sme') and name.endswith('.exe'):
            size = user32.GetWindowTextLengthW(hwnd)
            title = C.create_unicode_buffer(size + 1)
            user32.GetWindowTextW(hwnd, title, len(title))
            if title.value:
                found.append(MisaWindow(hwnd, pid.value, title.value))
        return True
    user32.EnumWindows(visit, 0)
    return found


def recover_window(*, restore=True):
    try:
        return select_misa(restore=restore)
    except MisaNotRunning:
        windows = misa_windows()
        if not windows:
            raise
        if len({w.pid for w in windows}) != 1:
            raise AmbiguousMisa('Có nhiều phiên MISA. Hãy chọn một phiên để tiếp tục.')
        foreground = user32.GetForegroundWindow()
        active = next((w for w in windows if w.hwnd == foreground), None)
        if active:
            return active
        if len(windows) == 1:
            return windows[0]
        raise WindowError('MISA đang mở nhiều cửa sổ khởi động. Đang chờ cửa sổ chính.')


def client_bounds(window, capture_box):
    """Client rectangle in the current window capture."""
    rect, origin = W.RECT(), W.POINT(0, 0)
    if (not user32.GetClientRect(window.hwnd, C.byref(rect))
            or not user32.ClientToScreen(window.hwnd, C.byref(origin))):
        raise WindowError('Cannot read the current MISA client rectangle.')
    x, y = origin.x - capture_box[0], origin.y - capture_box[1]
    width, height = rect.right - rect.left, rect.bottom - rect.top
    if not (0 <= x < x + width <= capture_box[2] - capture_box[0]
            and 0 <= y < y + height <= capture_box[3] - capture_box[1]):
        raise WindowError('MISA client rectangle changed during capture.')
    return x, y, width, height


def is_foreground(window):
    return user32.GetForegroundWindow() == window.hwnd





