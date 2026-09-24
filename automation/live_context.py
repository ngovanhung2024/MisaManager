"""Current MISA observation and persisted workflow goals for the repair agent."""
from dataclasses import asdict
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import threading
import time
import uuid


def read_json(path):
    value = json.loads(Path(path).read_text(encoding='utf-8'))
    if not isinstance(value, dict):
        raise ValueError('Workflow/incident phải là đối tượng JSON.')
    return value

def _manager_running(saved):
    """Confirm the saved manager identity still belongs to this live run."""
    from . import win32

    pid = saved.get('manager_pid')
    executable = saved.get('manager_executable', '')
    try:
        return bool(type(pid) is int and pid > 0 and executable
                    and os.path.normcase(win32.process_path(pid)) == os.path.normcase(executable)
                    and (not saved.get('manager_birth')
                         or win32.process_birth(pid) == saved['manager_birth']))
    except Exception:
        return False


def _same_manager(saved, session):
    if not isinstance(session, dict) or not session.get('manager_pid'):
        return True
    if not isinstance(saved, dict):
        return False
    if (saved.get('manager_pid') != session.get('manager_pid')
            or os.path.normcase(str(saved.get('manager_executable', '')))
            != os.path.normcase(str(session.get('manager_executable', '')))):
        return False
    return (not saved.get('manager_birth') or not session.get('manager_birth')
            or saved.get('manager_birth') == session.get('manager_birth'))


def live_workflow_context(session=None):
    """Build current workflow metadata without requiring an incident folder.

    A Codex test can begin before the first incident exists.  The MISA window
    and process identity are still checked live; saved workflow fields are
    explicitly labelled as progress metadata, never as screen truth.
    """
    from .paths import state_dir

    session = session if isinstance(session, dict) else {}
    result = {
        'source': 'live_test', 'read_at': datetime.now(timezone.utc).isoformat(),
        'incident_id': session.get('incident_id'), 'same_run_as_incident': False,
        'run_id': None, 'updated_at': session.get('updated_at'), 'manager_pid': session.get('manager_pid'),
        'manager_running': False, 'state': session.get('state', 'ready'), 'active_step': None,
        'blocked_checkpoint': None, 'ui_checkpoint': None, 'restart_phase': session.get('phase'),
        'next_step': None, 'invoice_target': None, 'invoice_number': None,
        'invoice_source': session.get('selected_path'), 'receipt_number': None,
        'completed': [], 'next_goal': None, 'remaining': [], 'chosen': {},
        'error': '', 'source_revision': session.get('source_revision'),
        'screen_matches_workflow': 'unverified', 'warnings': [],
        'instruction': 'Tiến độ lưu chỉ là metadata; phải dùng ảnh/đọc hiện tại để xác nhận MISA.',
    }
    saved, recovery = {}, {}
    try:
        saved = read_json(state_dir() / 'add-sale-state.json')
    except FileNotFoundError:
        pass
    except (OSError, ValueError) as error:
        result['warnings'].append('Không đọc được tiến độ Thêm: ' + str(error))
    try:
        recovery = read_json(state_dir() / 'add-recovery.json')
    except FileNotFoundError:
        pass
    except (OSError, ValueError) as error:
        result['warnings'].append('Không đọc được bộ điều phối: ' + str(error))
    if session.get('manager_pid'):
        if saved and not _same_manager(saved, session):
            result['warnings'].append('Bỏ qua tiến độ Add cũ của manager khác.')
            saved = {}
        if recovery and not _same_manager(recovery, session):
            result['warnings'].append('Bỏ qua bộ điều phối cũ của manager khác.')
            recovery = {}
    if recovery.get('active'):
        current = recovery.get('workflow') or {}
        if current:
            saved = dict(saved, **current)
        saved.update(manager_pid=recovery.get('manager_pid'),
                     manager_executable=recovery.get('manager_executable'),
                     manager_birth=recovery.get('manager_birth'),
                     updated_at=recovery.get('updated_at'),
                     source_revision=recovery.get('source_revision', saved.get('source_revision')))
        result['state'] = 'running' if recovery.get('phase') not in ('collecting', 'console_open',
            'incomplete', 'technical_error') else 'blocked'
        result['restart_phase'] = recovery.get('phase')
    if saved:
        for key in ('run_id', 'updated_at', 'manager_pid', 'state', 'active_step',
                    'next_step', 'invoice_number', 'invoice_source', 'completed',
                    'error', 'source_revision'):
            if key in saved:
                result[key] = saved[key]
        result['ui_checkpoint'] = saved.get('active_step')
        result['blocked_checkpoint'] = saved.get('active_step') if saved.get('state') == 'blocked' else None
        result['next_goal'] = saved.get('next_step')
        result['chosen'] = saved.get('expected', {})
        result['readbacks'] = saved.get('readbacks', {})
        result['recent_actions'] = [saved['last_action']] if saved.get('last_action') else []
        result['window'] = saved.get('window')
        result['invoice_target'] = {key: saved.get(key) for key in
                                    ('invoice_number', 'invoice_series', 'invoice_issued', 'seller', 'buyer')}
    session_running = _manager_running(session) if session.get('manager_pid') else False
    result['manager_running'] = _manager_running(saved) if saved else session_running
    if session_running and not result['manager_running']:
        # Stale workflow progress must not make a current session look dead.
        result['manager_running'] = True
    if session_running:
        for key in ('manager_pid', 'manager_executable', 'manager_birth'):
            if key in session:
                result[key] = session[key]
    if not result['manager_running']:
        result['warnings'].append('Không xác nhận được tiến trình manager đang chạy.')
    if session.get('manager_pid') and result.get('manager_pid') not in (None, session.get('manager_pid')):
        result['warnings'].append('Tiến độ lưu thuộc PID khác; không dùng làm trạng thái UI.')
    return result


def workflow_context(folder):
    """Read saved progress, distinguishing tool errors from an Add workflow."""
    from .paths import state_dir

    incident = read_json(Path(folder) / 'incident.json')
    result = {
        'source': 'add_sale_not_started', 'read_at': datetime.now(timezone.utc).isoformat(),
        'incident_id': incident['id'], 'same_run_as_incident': False,
        'run_id': None, 'updated_at': None, 'manager_pid': None, 'manager_running': False,
        'state': 'not_started', 'active_step': None, 'blocked_checkpoint': None,
        'ui_checkpoint': None, 'restart_phase': None, 'next_step': None,
        'invoice_target': None, 'invoice_number': None, 'invoice_source': None,
        'receipt_number': None, 'completed': [], 'next_goal': None, 'remaining': [],
        'recent_actions': [], 'chosen': {}, 'error': '', 'source_revision': None,
        'screen_matches_workflow': 'not_applicable', 'warnings': [],
        'instruction': 'Chưa có lượt Thêm được lưu. Người dùng tự bấm Thêm để chạy. '
                       'Checklist trong incident cũ chỉ là lịch sử.',
    }
    saved, recovery = {}, {}
    try:
        saved = read_json(state_dir() / 'add-sale-state.json')
    except FileNotFoundError:
        pass
    except (OSError, ValueError) as error:
        result['warnings'].append('Không đọc được tiến độ Thêm: ' + str(error))
    try:
        recovery = read_json(state_dir() / 'add-recovery.json')
    except FileNotFoundError:
        pass
    except (OSError, ValueError) as error:
        result['warnings'].append('Không đọc được bộ điều phối phục hồi: ' + str(error))
    coordinating = bool(recovery.get('active'))
    if incident.get('workflow') == 'tool_error':
        saved = read_json(Path(folder) / 'checklist.json')
        coordinating = coordinating and recovery.get('session_id') == incident.get('run_id')
    if coordinating:
        current = recovery.get('workflow') or {}
        if not current or current.get('run_id') != saved.get('run_id'):
            # A runtime failure may precede AddSale construction. Do not borrow an older invoice/run.
            invoice = recovery.get('invoice') or {}
            saved = dict(current) if current else dict(workflow=recovery.get('mode', 'add_sale'),
                run_id=recovery.get('session_id'), invoice_number=invoice.get('number'),
                invoice_series=invoice.get('series'), invoice_issued=invoice.get('issued'),
                invoice_source=invoice.get('source'), seller=(invoice.get('seller') or {}).get('name'),
                buyer=(invoice.get('buyer') or {}).get('name'), completed=[])
        saved.update(manager_pid=recovery.get('manager_pid'),
                     manager_executable=recovery.get('manager_executable'),
                     manager_birth=recovery.get('manager_birth'), updated_at=recovery.get('updated_at'),
                     source_revision=recovery.get('source_revision', saved.get('source_revision')))
    if not saved:
        return result
    if saved.get('workflow') not in ('add_sale', 'tool_error') or not saved.get('run_id'):
        result.update(source='add_sale_state_unavailable', state='unknown',
                      error='Không nhận diện được trạng thái Thêm đã lưu.')
        return result
    from . import win32

    running = _manager_running(saved)
    for key in ('run_id', 'updated_at', 'manager_pid', 'state', 'active_step', 'next_step',
                'invoice_number', 'invoice_source', 'completed', 'error', 'source_revision'):
        result[key] = saved.get(key, result.get(key))
    result.update(source='saved_add_sale', manager_running=running,
                  same_run_as_incident=saved['run_id'] == incident.get('run_id'),
                  ui_checkpoint=saved.get('active_step'), window=saved.get('window'),
                  invoice_target={key: saved.get(key) for key in
                                  ('invoice_number', 'invoice_series', 'invoice_issued', 'seller', 'buyer')},
                  chosen=saved.get('expected', {}), readbacks=saved.get('readbacks', {}),
                  performance=saved.get('performance'),
                  next_goal=saved.get('next_step'),
                  blocked_checkpoint=saved.get('active_step') if saved.get('state') == 'blocked' else None,
                  recent_actions=[saved['last_action']] if saved.get('last_action') else [],
                  instruction='Đây là tiến độ Thêm đã lưu, không phải quan sát UI trực tiếp. '
                              'Việc chạy lại thuộc bộ điều phối app sau lần người dùng bấm Thêm.')
    if coordinating:
        phase = recovery.get('phase')
        result['source'] = 'saved_add_recovery'
        result['recovery'] = {key: recovery.get(key) for key in
            ('session_id', 'phase', 'active', 'attempt', 'incident', 'parent_incident',
             'stop_requested', 'updated_at', 'repair', 'collection_error')}
        result['restart_phase'] = phase
        result['ui_busy'] = phase in ('preparing_runtime', 'running', 'cleaning_tabs', 'resuming',
            'checking_sales', 'returning_home', 'restarting', 'stopping', 'quiescing', 'reloading', 'verifying_runtime')
        result['state'] = 'running' if result['ui_busy'] else 'blocked'
        result['next_goal'] = result['next_step'] = recovery.get('message') or phase
    if not running:
        result['warnings'].append('Ứng dụng ghi tiến độ này không còn chạy; dữ liệu chỉ là lịch sử.')
    if saved.get('workflow') == 'tool_error':
        result.update(source='saved_tool_error', workflow='tool_error',
                  instruction='Đây là hồ sơ sửa lỗi tool, không phải lượt Thêm hay quan sát UI trực tiếp. '
                                  'Không khởi động workflow hoặc thao tác MISA khi sửa lỗi này.')
    return result


class LiveMisaObserver:
    def __init__(self):
        self._lock = threading.Lock()

    def observe(self, folder, *, context_override=None,
                context_reader=None, expected_identity=None, deep=False,
                maximum_nodes=5000, query='', maximum_results=120):
        from . import win32
        from .elements import build_map, kind_of_title, region_bands
        from .screen_win32 import ScreenDesktop

        with self._lock:
            reader = context_reader or (lambda: workflow_context(folder))
            context = context_override if context_override is not None else reader()
            result = {'observation_id': uuid.uuid4().hex,
                      'started_at': datetime.now(timezone.utc).isoformat(),
                      'workflow': context, 'status': 'unavailable', 'native_status': 'not_requested',
                      'words': [], 'evidence': 'unavailable'}
            picture = None
            desktop = None
            try:
                win32.enable_dpi_awareness()
                windows = win32.misa_windows()
                result['windows'] = [dict(asdict(w), foreground=win32.is_foreground(w),
                                          minimized=bool(win32.user32.IsIconic(w.hwnd)),
                                          size=list(win32.window_size(w)),
                                          readable=win32.readable_window(w),
                                          elevated=win32.elevated(w.pid)) for w in windows]
                result['elevation_mismatch'] = bool(windows) and win32.elevated(windows[0].pid) \
                    and not win32.elevated(os.getpid())
                window = win32.recover_window(restore=False)
                result['window'] = asdict(window)
                if win32.user32.IsIconic(window.hwnd) or not win32.is_foreground(window):
                    raise win32.WindowError('MISA khong o phia truoc; chi focus_misa_window khi workflow da dung.')
                cancel = threading.Event()
                desktop = ScreenDesktop(cancel)
                desktop.window, desktop.pid = window, window.pid
                desktop.checkpoint = 'live.observe'
                kind = next((name for name in ('main', 'sale', 'find', 'new_item')
                             if desktop.is_window(name)), None)
                desktop._observe(kind='sale_tabs' if kind == 'sale' else kind, force=True)
                desktop._guard_frame()
                picture = desktop._frame[4]
                box = desktop._frame[0]
                words = desktop._frame[3]
                result['captured_at'] = desktop._captured_at.isoformat()
                result['capture_box'] = list(box)
                result['masked_regions'] = picture.info.get('misa_masked_regions', [])
                result['image_size'] = list(picture.size)
                result['frame_id'] = desktop._frame_id
                result['native_status'] = 'observed' if words else 'unreadable'
                result['words'] = [dict(text=word.text, box=list(word.box))
                                   for word in words]
                window_kind = kind_of_title(window.title)
                bands = region_bands(window_kind, picture.width, picture.height)
                element_map = build_map((), words, window=window.title,
                                        width=picture.width, height=picture.height,
                                        source='native', observed_at=result['captured_at'],
                                        bands=bands)
                result['elements'] = element_map.to_dict(include_text=False)
                result['element_text'] = element_map.render_text(interactive_only=True, limit=60)
                # Keep the optional deep trees tied to this same HWND/frame;
                # tree failures do not hide the screenshot or compact map.
                if deep:
                    result['native_inspection'] = desktop.deep_native_observe(
                        maximum_nodes=maximum_nodes, query=query,
                        maximum_results=maximum_results)
                current = win32.bound_window(window)
                win32.ensure_foreground(current)
                rect = win32.W.RECT()
                if (not win32.user32.GetWindowRect(current.hwnd, win32.C.byref(rect))
                        or (rect.left, rect.top, rect.right, rect.bottom) != box
                        or current.title != window.title):
                    raise win32.WindowError('Cửa sổ đổi trong lúc scan; cần quan sát lại.')
                latest = reader()
                if (latest['run_id'], latest['updated_at']) != (context['run_id'], context['updated_at']):
                    result['workflow'] = latest
                    raise win32.WindowError('Workflow đổi trong lúc scan; cần quan sát lại.')
                if expected_identity:
                    if not _manager_running(expected_identity):
                        raise win32.WindowError('Manager đổi trong lúc quan sát phiên Codex.')
                result.update(status='observed', evidence='fresh_native_uia_win32',
                              native_targeting='native identities only; screenshot bounds are evidence')
            except Exception as error:
                # Discard native evidence if the window or workflow changed during capture.
                for key in ('elements', 'element_text', 'native_inspection'):
                    result.pop(key, None)
                result['words'] = []
                result['error'] = f'{type(error).__name__}: {error}'
                if 'foreground' not in result:
                    try:
                        result['foreground'] = win32.foreground_identity()
                    except Exception:
                        pass
            result['finished_at'] = datetime.now(timezone.utc).isoformat()
            if desktop is not None:
                desktop.close()
            return result, picture

    def observe_session(self, session, *, deep=False, maximum_nodes=5000,
                        query='', maximum_results=120):
        """Observe a Codex test session before an incident folder exists."""
        session = dict(session or {})
        session_id = session.get('session_id')

        def current_context():
            latest = session
            if session_id:
                try:
                    from .live_test import read_session
                    latest = read_session(session_id)
                except (RuntimeError, ValueError):
                    pass
            return live_workflow_context(latest)

        context = current_context()
        return self.observe(None, context_override=context,
                            context_reader=current_context,
                            expected_identity=session, deep=deep,
                            maximum_nodes=maximum_nodes, query=query,
                            maximum_results=maximum_results)

    def focus(self, folder):
        from . import win32

        context = workflow_context(folder)
        if (context['source'] not in ('saved_add_sale', 'saved_add_recovery') or not context['manager_running']
                or context.get('ui_busy') or context['state'] not in ('paused', 'blocked', 'complete')
                or not context.get('window')):
            raise ValueError('Chỉ đưa MISA lên trước khi ứng dụng còn chạy và lượt Thêm đã dừng.')
        saved = context['window']
        window = win32.bound_window(win32.MisaWindow(saved['hwnd'], saved['pid'], saved['title']))
        if window.title != saved['title'] or window not in win32.misa_windows():
            raise ValueError('Cửa sổ MISA đã đổi từ lượt Thêm đã lưu.')
        latest = workflow_context(folder)
        if any(latest[key] != context[key] for key in ('run_id', 'updated_at', 'state', 'manager_running')):
            raise ValueError('Lượt Thêm đã thay đổi; cần đọc lại trạng thái.')
        win32.check_permission(window)
        win32.activate(window)
        return {'status': 'focused', 'window': asdict(window), 'run_id': context['run_id']}


