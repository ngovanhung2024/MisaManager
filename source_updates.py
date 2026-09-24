"""Local source bundles and in-process reload for the stable MISA desktop host.

Only the developer's publish command writes current.json. XML files never select
or supply code. Hashes detect incomplete/corrupt local packages, not authorship.
"""
import hashlib
import base64
from datetime import datetime, timezone
import importlib.abc
import importlib.util
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import sys
import threading
import time
import traceback
import uuid
import zipfile

HOST_API = 4
ROOTS = frozenset(('app', 'invoice_xml', 'automation'))
MAX_PACKAGE = 8 * 1024 * 1024


def digest(data):
    return hashlib.sha256(data).hexdigest()


def runtime_requirements_hash(data):
    """Hash requirements canonically so line endings do not mimic a runtime change."""
    if isinstance(data, str):
        data = data.encode('utf-8')
    data = data.replace(b'\r\n', b'\n').replace(b'\r', b'\n')
    return digest(data)


def source_files(root):
    """The complete repair surface; business XML and generated outputs are excluded."""
    root = Path(root).resolve()
    paths = list(root.glob('*.py')) + list(root.glob('*.md'))
    paths += [root / name for name in ('requirements.txt', 'requirements-observer.txt',
                                      'build-engine.ps1', 'Directory.Build.props', 'MisaAutomation.sln')]
    paths += list(root.glob('*.spec')) + list((root / 'packaging_hooks').glob('*.py'))
    paths += list((root / 'automation').rglob('*.py'))
    paths += list((root / 'locators').glob('*.json'))
    for suffix in ('*.cs', '*.csproj', 'packages.lock.json'):
        paths += [p for p in (root / 'src').rglob(suffix)
                  if not {'bin', 'obj'}.intersection(p.relative_to(root).parts)]
    return {p.relative_to(root).as_posix(): p.read_bytes() for p in sorted(set(paths)) if p.is_file()}


def manifest_revision(files):
    return digest(json.dumps(files, sort_keys=True, separators=(',', ':')).encode('utf-8'))


class DirectSource:
    """Compile a pinned source snapshot without creating a ZIP or importing the app."""
    def __init__(self, root, *, contents=None, expected=None):
        self.root = Path(root).resolve()
        self.contents = source_files(self.root) if contents is None else contents
        self.files = {name: digest(content) for name, content in self.contents.items()}
        self.revision = manifest_revision(self.files)
        if expected and self.revision != expected:
            raise ValueError('Source đã đổi sau thông báo sửa hoàn tất.')
        self.requirements_hash = runtime_requirements_hash(self.contents['requirements.txt'])
        self.modules = {}
        for name, content in self.contents.items():
            if name in ('app.py', 'invoice_xml.py') or name.startswith('automation/'):
                module = module_name(name)
                code = compile(content, str(self.root / name), 'exec')
                self.modules[module] = (name, code, name.endswith('/__init__.py'))
        for name in ('source_updates.py', 'launcher.py', 'incident_mcp.py'):
            compile(self.contents[name], str(self.root / name), 'exec')
        if not ROOTS.issubset(self.modules):
            raise ValueError('Source thiếu module bắt buộc.')

    def export(self, path):
        atomic_json(path, {'schema': 1, 'root': str(self.root), 'revision': self.revision,
                          'files': {name: base64.b64encode(data).decode('ascii')
                                    for name, data in self.contents.items()}})

    @classmethod
    def read(cls, path, expected):
        data = json.loads(Path(path).read_text(encoding='utf-8'))
        return cls(data['root'], contents={name: base64.b64decode(value, validate=True)
                                          for name, value in data['files'].items()}, expected=expected)


def loaded_source():
    finder = next((f for f in sys.meta_path if getattr(f, 'misa_source_finder', False)), None)
    return finder.bundle if finder else None


def runtime_identity(*, strict=True):
    source = loaded_source()
    if source is None:
        raise RuntimeError('Runtime không có source snapshot đã được ghim.')
    loaded, mismatches = {}, []
    for name, module in tuple(sys.modules.items()):
        if name.split('.')[0] in ROOTS:
            if (name not in source.modules or getattr(module, '__misa_revision__', '') != source.revision
                    or getattr(module, '__misa_sha256__', '') != source.files[source.modules[name][0]]):
                mismatches.append(name)
                if strict:
                    raise RuntimeError('Module cũ/chưa được xác nhận còn trong runtime: ' + name)
            loaded[name] = getattr(module, '__misa_sha256__', '')
    return {'revision': source.revision, 'files': source.files, 'loaded_modules': loaded,
            'module_mismatches': mismatches,
            'pinned_modules': sorted(source.modules), 'pid': os.getpid(),
            'host_sha256': getattr(sys.modules[__name__], '__misa_host_sha256__', '')}


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        temp.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
        os.replace(temp, path)
    finally:
        temp.unlink(missing_ok=True)


def module_name(name):
    path = PurePosixPath(name)
    if (str(path) != name or '\\' in name or path.is_absolute()
            or any(not re.fullmatch(r'[A-Za-z_]\w*', p) for p in path.with_suffix('').parts)
            or path.suffix != '.py'):
        raise ValueError('Đường dẫn source không hợp lệ: ' + name)
    parts = list(path.with_suffix('').parts)
    if parts[-1] == '__init__':
        parts.pop()
    if not parts or parts[0] not in ROOTS or (len(parts) > 1 and parts[0] != 'automation'):
        raise ValueError('Module ngoài phạm vi ứng dụng: ' + name)
    return '.'.join(parts)


class Bundle:
    def __init__(self, raw, *, requirements_hash, expected=None):
        if len(raw) > MAX_PACKAGE:
            raise ValueError('Gói cập nhật quá lớn.')
        self.revision = digest(raw)
        if expected and self.revision != expected:
            raise ValueError('Mã kiểm tra gói cập nhật không khớp.')
        self.modules = {}
        with zipfile.ZipFile(io.BytesIO(raw)) as archive:
            entries = archive.infolist()
            names = [entry.filename for entry in entries]
            if len(names) != len(set(names)) or sum(e.file_size for e in entries) > MAX_PACKAGE:
                raise ValueError('Gói cập nhật trùng file hoặc vượt giới hạn.')
            if any((e.external_attr >> 16) & 0o170000 == 0o120000 for e in entries):
                raise ValueError('Gói cập nhật chứa liên kết không hợp lệ.')
            meta = json.loads(archive.read('manifest.json'))
            if (meta.get('host_api') != HOST_API
                    or meta.get('python') != list(sys.version_info[:2])
                    or meta.get('requirements_sha256') != requirements_hash):
                raise ValueError('Cập nhật thay đổi runtime/thư viện; cần đóng gói lại EXE.')
            files = meta['files']
            self.files = files
            if set(names) != set(files) | {'manifest.json'}:
                raise ValueError('Danh sách file cập nhật không khớp.')
            for name, expected_hash in files.items():
                module = module_name(name)
                content = archive.read(name)
                if digest(content) != expected_hash or module in self.modules:
                    raise ValueError('Nội dung module cập nhật không hợp lệ: ' + name)
                # Compile all files before any running module is replaced.
                code = compile(content, 'misa-source/' + self.revision[:12] + '/' + name, 'exec')
                self.modules[module] = (name, code, name.endswith('/__init__.py'))
        if not ROOTS.issubset(self.modules):
            raise ValueError('Gói cập nhật thiếu module ứng dụng.')
        for name in self.modules:
            parent = name.rpartition('.')[0]
            if parent and (parent not in self.modules or not self.modules[parent][2]):
                raise ValueError('Gói cập nhật thiếu package: ' + parent)
        self.raw = raw


def build_bundle(root):
    root = Path(root)
    paths = [root / (name + '.py') for name in sorted(ROOTS - {'automation'})]
    paths += sorted((root / 'automation').rglob('*.py'))
    files = {p.relative_to(root).as_posix(): p.read_bytes() for p in paths}
    requirements_hash = runtime_requirements_hash((root / 'requirements.txt').read_bytes())
    meta = {'host_api': HOST_API, 'python': list(sys.version_info[:2]),
            'requirements_sha256': requirements_hash,
            'files': {name: digest(content) for name, content in files.items()}}
    output = io.BytesIO()
    with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        # Fixed timestamps make the revision depend only on source content.
        for name, content in sorted({**files, 'manifest.json': json.dumps(meta, sort_keys=True).encode()}.items()):
            info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, content)
    return Bundle(output.getvalue(), requirements_hash=requirements_hash)


def publish(root, destination):
    bundle = build_bundle(root)
    destination = Path(destination)
    destination.mkdir(parents=True, exist_ok=True)
    filename = 'payload-' + bundle.revision + '.zip'
    temp = destination / (filename + '.' + uuid.uuid4().hex + '.tmp')
    try:
        temp.write_bytes(bundle.raw)
        os.replace(temp, destination / filename)
    finally:
        temp.unlink(missing_ok=True)
    atomic_json(destination / 'current.json', {'revision': bundle.revision, 'file': filename})
    return bundle


class SourceFinder(importlib.abc.MetaPathFinder, importlib.abc.Loader):
    misa_source_finder = True
    def __init__(self, bundle):
        self.bundle = bundle

    def find_spec(self, fullname, path=None, target=None):
        if fullname in self.bundle.modules:
            return importlib.util.spec_from_loader(fullname, self,
                                                  is_package=self.bundle.modules[fullname][2])
        if fullname.split('.')[0] in ROOTS:
            raise ModuleNotFoundError('Module không nằm trong gói source: ' + fullname)
        return None

    def create_module(self, spec):
        return None

    def exec_module(self, module):
        name, code, package = self.bundle.modules[module.__name__]
        module.__file__ = code.co_filename
        if package:
            module.__path__ = [code.co_filename.rpartition('/')[0]]
        exec(code, module.__dict__)
        module.__misa_revision__ = self.bundle.revision
        module.__misa_sha256__ = self.bundle.files[name]


def install_bundle(bundle):
    # Only reload when the XML importer is idle.
    sys.meta_path[:] = [f for f in sys.meta_path if not getattr(f, 'misa_source_finder', False)]
    for name in tuple(sys.modules):
        if name.split('.')[0] in ROOTS:
            del sys.modules[name]
    sys.meta_path.insert(0, SourceFinder(bundle))


class ReloadStopControl:
    """Keep Stop responsive without creating a Tcl interpreter on a worker."""
    def __init__(self, cancel, user_stop):
        self.cancel = cancel
        self.user_stop = user_stop
        self.closed = threading.Event()
        self.ready = threading.Event()
        self.error = None
        self.thread = threading.Thread(target=self.run, name='misa-reload-stop', daemon=True)
        self.thread.start()
        self.ready.wait()
        if self.error:
            raise RuntimeError('Không giữ được nút Dừng trong lúc reload.') from self.error

    def run(self):
        import ctypes as C
        from ctypes import wintypes as W

        window = None
        registered = False
        try:
            # A destroyed worker Tk can be garbage-collected on another thread,
            # which makes Tcl_AsyncDelete abort the entire manager process.
            user = C.WinDLL('user32', use_last_error=True)
            kernel = C.WinDLL('kernel32', use_last_error=True)
            proc_type = C.WINFUNCTYPE(C.c_ssize_t, W.HWND, W.UINT, W.WPARAM, W.LPARAM)

            class WindowClass(C.Structure):
                _fields_ = [('style', W.UINT), ('procedure', proc_type),
                            ('class_extra', C.c_int), ('window_extra', C.c_int),
                            ('instance', W.HINSTANCE), ('icon', W.HICON),
                            ('cursor', W.HANDLE), ('background', W.HBRUSH),
                            ('menu_name', W.LPCWSTR), ('class_name', W.LPCWSTR)]

            def bind(library, name, args, result):
                function = getattr(library, name)
                function.argtypes, function.restype = args, result
                return function

            bind(kernel, 'GetModuleHandleW', [W.LPCWSTR], W.HMODULE)
            bind(user, 'RegisterClassW', [C.POINTER(WindowClass)], W.ATOM)
            bind(user, 'UnregisterClassW', [W.LPCWSTR, W.HINSTANCE], W.BOOL)
            bind(user, 'CreateWindowExW', [W.DWORD, W.LPCWSTR, W.LPCWSTR, W.DWORD,
                C.c_int, C.c_int, C.c_int, C.c_int, W.HWND, W.HMENU, W.HINSTANCE, W.LPVOID], W.HWND)
            bind(user, 'DefWindowProcW', [W.HWND, W.UINT, W.WPARAM, W.LPARAM], C.c_ssize_t)
            bind(user, 'DestroyWindow', [W.HWND], W.BOOL)
            bind(user, 'IsWindow', [W.HWND], W.BOOL)
            bind(user, 'ShowWindow', [W.HWND, C.c_int], W.BOOL)
            bind(user, 'SetWindowTextW', [W.HWND, W.LPCWSTR], W.BOOL)
            bind(user, 'EnableWindow', [W.HWND, W.BOOL], W.BOOL)
            bind(user, 'SetTimer', [W.HWND, C.c_size_t, W.UINT, W.LPVOID], C.c_size_t)
            bind(user, 'GetMessageW', [C.POINTER(W.MSG), W.HWND, W.UINT, W.UINT], W.BOOL)
            bind(user, 'IsDialogMessageW', [W.HWND, C.POINTER(W.MSG)], W.BOOL)
            bind(user, 'TranslateMessage', [C.POINTER(W.MSG)], W.BOOL)
            bind(user, 'DispatchMessageW', [C.POINTER(W.MSG)], C.c_ssize_t)
            bind(user, 'PostQuitMessage', [C.c_int], None)
            bind(user, 'LoadCursorW', [W.HINSTANCE, W.LPCWSTR], W.HANDLE)
            bind(user, 'AdjustWindowRectEx', [C.POINTER(W.RECT), W.DWORD, W.BOOL, W.DWORD], W.BOOL)
            bind(user, 'GetDpiForSystem', [], W.UINT)
            message = button = None
            stopped = False

            def stop():
                self.user_stop.set()
                self.cancel.set()

            @proc_type
            def procedure(hwnd, event, wparam, lparam):
                nonlocal stopped
                try:
                    if event == 0x0010:  # WM_CLOSE requests Stop; only the host closes this window.
                        stop()
                        return 0
                    if event == 0x0111 and wparam in (1, 2):  # Button click or dialog Cancel.
                        stop()
                        return 0
                    if event == 0x0113:  # WM_TIMER
                        if self.closed.is_set():
                            if not user.DestroyWindow(hwnd):
                                raise C.WinError(C.get_last_error())
                        elif self.cancel.is_set() and not stopped:
                            if not user.SetWindowTextW(message, 'Đã nhận Dừng. Không khởi động lại workflow.'):
                                raise C.WinError(C.get_last_error())
                            user.EnableWindow(button, False)
                            stopped = True
                        return 0
                    if event == 0x0002:  # WM_DESTROY
                        user.PostQuitMessage(0)
                        return 0
                    return user.DefWindowProcW(hwnd, event, wparam, lparam)
                except BaseException as error:
                    self.error = error
                    stop()
                    user.PostQuitMessage(1)
                    return 0

            instance = kernel.GetModuleHandleW(None)
            class_name = 'MisaReloadStop_' + uuid.uuid4().hex
            cursor = user.LoadCursorW(None, C.cast(C.c_void_p(32512), W.LPCWSTR))
            definition = WindowClass(0, procedure, 0, 0, instance, None, cursor, 16, None, class_name)
            if not user.RegisterClassW(C.byref(definition)):
                raise C.WinError(C.get_last_error())
            registered = True
            scale = user.GetDpiForSystem() / 96
            width, height = round(360 * scale), round(130 * scale)
            style, extended = 0x00C80000, 0x00000008  # Caption/system menu; topmost.
            bounds = W.RECT(0, 0, width, height)
            if not user.AdjustWindowRectEx(C.byref(bounds), style, False, extended):
                raise C.WinError(C.get_last_error())
            window = user.CreateWindowExW(extended, class_name, 'Thêm - Nạp source', style,
                -2147483648, -2147483648, bounds.right - bounds.left, bounds.bottom - bounds.top,
                None, None, instance, None)
            if not window:
                raise C.WinError(C.get_last_error())
            message = user.CreateWindowExW(0, 'STATIC',
                'Đang nạp source. Bạn có thể Dừng toàn bộ chu trình.', 0x50000000,
                round(16 * scale), round(16 * scale), width - round(32 * scale), round(50 * scale),
                window, None, instance, None)
            button = user.CreateWindowExW(0, 'BUTTON', 'Dừng', 0x50010001,
                round(16 * scale), round(78 * scale), width - round(32 * scale), round(32 * scale),
                window, 1, instance, None)
            if not message or not button or not user.SetTimer(window, 1, 50, None):
                raise C.WinError(C.get_last_error())
            user.ShowWindow(window, 5)
            self.ready.set()
            event = W.MSG()
            while True:
                result = user.GetMessageW(C.byref(event), None, 0, 0)
                if result == -1:
                    raise C.WinError(C.get_last_error())
                if result == 0:
                    break
                if not user.IsDialogMessageW(window, C.byref(event)):
                    user.TranslateMessage(C.byref(event))
                    user.DispatchMessageW(C.byref(event))
        except BaseException as error:
            self.error = error
            self.user_stop.set()
            self.cancel.set()
        finally:
            if window and user.IsWindow(window):
                user.DestroyWindow(window)
            if registered:
                user.UnregisterClassW(class_name, instance)
            self.ready.set()

    def close(self):
        self.closed.set()
        self.thread.join()


class UpdateHost:
    def __init__(self, folder, baseline, requirements_hash):
        self.folder = Path(folder)
        self.requirements_hash = requirements_hash
        self.current = self.last_good = baseline
        self.next_bundle = None
        self.session = None
        self.rejected = set()
        self.error = ''
        self.last_error = None
        self._candidate_cache = None
        self.runtime = None
        self.reload_error = None
        self.reload_stop_control = None
        self.codex_test_authorized = False
        self.recovery_user_stop = threading.Event()
        self.host_hash = digest(Path(__file__).read_bytes())
        sys.modules[__name__].__misa_host_sha256__ = self.host_hash

    def candidate(self, *, force=False):
        try:
            pointer_path = self.folder / 'current.json'
            if not pointer_path.exists():
                self._candidate_cache = None
                self.error = ''
                return None
            if pointer_path.stat().st_size > 4096:
                raise ValueError('Thông tin cập nhật quá lớn.')
            pointer = json.loads(pointer_path.read_text(encoding='utf-8'))
            revision = pointer['revision']
            if not isinstance(revision, str) or not re.fullmatch('[a-f0-9]{64}', revision):
                raise ValueError('Mã phiên bản cập nhật không hợp lệ.')
            if not force and (revision == self.current.revision or revision in self.rejected):
                self._candidate_cache = None
                return None
            filename = 'payload-' + revision + '.zip'
            if pointer['file'] != filename:
                raise ValueError('Tên gói cập nhật không hợp lệ.')
            path = self.folder / filename
            stat = path.stat()
            if stat.st_size > MAX_PACKAGE:
                raise ValueError('Gói cập nhật quá lớn.')
            cache_key = (revision, stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns)
            if not force and self._candidate_cache and self._candidate_cache[0] == cache_key:
                return self._candidate_cache[1]
            bundle = Bundle(path.read_bytes(), requirements_hash=self.requirements_hash, expected=revision)
            self._candidate_cache = (cache_key, bundle)
            self.error = ''
            return bundle
        except Exception as error:
            self._candidate_cache = None
            message = str(error)
            if message != self.error:
                self.error = message
                self.remember_error(error, 'Kiểm tra cập nhật source')
                self.status('update_rejected', error=message)
            return None

    def remember_error(self, error, operation):
        self.last_error = {
            'type': type(error).__name__, 'message': str(error),
            'traceback': ''.join(traceback.format_exception(type(error), error, error.__traceback__)),
            'at': datetime.now(timezone.utc).isoformat(), 'operation': operation,
            'source_revision': self.current.revision,
        }

    def status(self, state, **extra):
        try:
            atomic_json(self.folder / 'runtime-state.json', {
                'pid': os.getpid(), 'revision': self.current.revision,
                'state': state, 'at': time.time(), 'saved': False, **extra})
        except OSError:
            pass  # Reporting must not interrupt XML input.

    def ready(self):
        self.runtime = runtime_identity()
        self.last_good = self.current
        self.status('ready', error=self.error, runtime=self.runtime,
                    message='Source loaded; native UIA/Win32 runtime is ready on demand.')

    def request(self, bundle, session):
        if (session.get('recovery', {}).get('active')
                and getattr(self, 'recovery_cancel', None) is not None
                and self.recovery_cancel.is_set()):
            raise RuntimeError('Đã dừng trước khi bàn giao source cho host.')
        if isinstance(bundle, DirectSource) and bundle.requirements_hash != self.requirements_hash:
            raise ValueError('Runtime dependency change requires preparation before reload.')
        self.session = json.loads(json.dumps(session))
        self.next_bundle = bundle
        self.status('reloading', next_revision=bundle.revision)

    def keep_stop_available(self, cancel):
        if self.reload_stop_control is None:
            self.reload_stop_control = ReloadStopControl(cancel, self.recovery_user_stop)

    def close_reload_stop(self):
        if self.reload_stop_control is not None:
            self.reload_stop_control.close()
            self.reload_stop_control = None

    def run(self, argv, *, prefer_baseline=False):
        import gc
        import importlib
        if not prefer_baseline:
            self.current = self.candidate() or self.current
        while True:
            install_bundle(self.current)
            gc.collect()
            self.next_bundle = None
            try:
                result = importlib.import_module('app').main(argv, host=self, session=self.session)
            except Exception as error:
                self.error = str(error)
                self.remember_error(error, 'Khởi động source')
                self.reload_error = dict(self.last_error, attempted_revision=self.current.revision)
                self.rejected.add(self.current.revision)
                self.status('startup_failed', error=self.error, error_details=self.last_error)
                # Clean up a partially constructed Tk window before rollback.
                tk = sys.modules.get('tkinter')
                if tk and tk._default_root:
                    try:
                        tk._default_root.destroy()
                    except Exception:
                        pass
                if self.current.revision == self.last_good.revision:
                    raise
                self.current = self.last_good
                self.session = dict(self.session or {})
                self.status('rollback', error=self.error)
                continue
            if self.next_bundle is None:
                self.close_reload_stop()
                self.status('closed')
                return result
            self.current = self.next_bundle
            # The old UI, timers and workers have all returned before replacing the host.
            if isinstance(self.current, DirectSource):
                data = self.current.contents['source_updates.py']
                if digest(data) != self.host_hash:
                    import types
                    previous = sys.modules['source_updates']
                    fresh = types.ModuleType('source_updates')
                    fresh.__file__ = str(self.current.root / 'source_updates.py')
                    try:
                        exec(compile(data, fresh.__file__, 'exec'), fresh.__dict__)
                        sys.modules['source_updates'] = fresh
                        snapshot = fresh.DirectSource(self.current.root, contents=self.current.contents,
                                                      expected=self.current.revision)
                        replacement = fresh.UpdateHost(self.folder, snapshot, snapshot.requirements_hash)
                        replacement.session = self.session
                        replacement.last_good = (fresh.DirectSource(self.last_good.root,
                            contents=self.last_good.contents, expected=self.last_good.revision)
                            if hasattr(self.last_good, 'contents') else self.last_good)
                        replacement.workflow_lease = getattr(self, 'workflow_lease', None)
                        replacement.recovery_cancel = getattr(self, 'recovery_cancel', None)
                        replacement.recovery_user_stop = self.recovery_user_stop
                        replacement.reload_stop_control = self.reload_stop_control
                        replacement.codex_console = getattr(self, 'codex_console', None)
                        replacement.codex_test_authorized = getattr(
                            self, 'codex_test_authorized', False)
                        return replacement.run(argv, prefer_baseline=True)
                    except Exception as error:
                        sys.modules['source_updates'] = previous
                        self.reload_error = {'message': str(error), 'attempted_revision': self.current.revision}
                        self.current = self.last_good
