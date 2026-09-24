"""Start the installed interactive Codex CLI in an owned CMD console."""
import ctypes as C
from ctypes import wintypes as W
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import uuid

from source_updates import atomic_json
from .managed_process import OwnedProcess
from .domain import Cancelled


REPAIR_REASONING_EFFORT = os.environ.get('MISA_CODEX_REPAIR_REASONING', 'high')


def configured_model_arguments(root=None, *, reasoning_effort=REPAIR_REASONING_EFFORT):
    """Use the user's current router configuration for a fresh repair conversation."""
    try:
        import tomllib
    except ModuleNotFoundError:
        try:
            import tomli as tomllib
        except ModuleNotFoundError:
            # The pinned Python 3.10 repair runtime may not have the optional
            # top-level tomli distribution even though pip vendors the same
            # parser.  Keep reconnecting independent of that installation
            # detail; do not silently use a different parser or config.
            from pip._vendor import tomli as tomllib
    home = Path(os.environ.get('CODEX_HOME') or Path.home() / '.codex').expanduser().resolve()
    path = home / 'config.toml'
    try:
        config = tomllib.loads(path.read_text(encoding='utf-8'))
    except (OSError, ValueError) as error:
        raise RuntimeError(f'Cannot read Codex router configuration: {path} ({type(error).__name__}).') from None
    if root is not None:
        project_path = Path(root).resolve() / '.codex' / 'config.toml'
        if project_path.is_file():
            try:
                project = tomllib.loads(project_path.read_text(encoding='utf-8'))
            except (OSError, ValueError) as error:
                raise RuntimeError(f'Cannot read project Codex configuration: {project_path} ({type(error).__name__}).') from None
            for key in ('model', 'model_provider', 'model_reasoning_effort', 'service_tier'):
                if key in project:
                    config[key] = project[key]
    model = config.get('model')
    if not isinstance(model, str) or not model.strip():
        raise ValueError(f'Configure a model in 9router/Codex before repairing MISA: {path}.')
    if reasoning_effort not in ('minimal', 'low', 'medium', 'high', 'xhigh'):
        raise ValueError('Codex reasoning effort must be minimal, low, medium, high, or xhigh.')
    config['model_reasoning_effort'] = reasoning_effort
    arguments = ['--model', model]
    for key in ('model_provider', 'model_reasoning_effort', 'service_tier'):
        value = config.get(key)
        if value is not None:
            if not isinstance(value, str) or not value.strip():
                raise ValueError(f'Invalid {key} in Codex router configuration: {path}.')
            arguments.extend(['-c', key + '=' + json.dumps(value, ensure_ascii=False)])
    return arguments


def _cli_command(path):
    path = Path(path).expanduser().resolve()
    if path.is_file() and path.suffix.lower() == '.exe':
        return [str(path)]
    if path.is_file() and path.suffix.lower() == '.cmd':
        # Run npm's JS entry directly so CMD never reparses model/config arguments.
        entry = path.parent / 'node_modules' / '@openai' / 'codex' / 'bin' / 'codex.js'
        node = path.with_name('node.exe')
        if not node.is_file():
            node = Path(shutil.which('node.exe') or '')
        if entry.is_file() and node.is_file():
            return [str(node.resolve()), str(entry)]
    raise FileNotFoundError('Codex CLI must be an installed codex.exe or npm codex.cmd: ' + str(path))


def codex_command():
    configured = os.environ.get('MISA_CODEX_CLI')
    if configured:
        return _cli_command(configured)
    candidates = [shutil.which(name) for name in ('codex.exe', 'codex.cmd')]
    local = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'OpenAI' / 'Codex' / 'bin'
    candidates.append(local / 'codex.exe')
    candidates.extend(sorted(local.glob('*/codex.exe'), key=lambda p: p.stat().st_mtime, reverse=True))
    candidates.append(Path(os.environ.get('APPDATA', str(Path.home()))) / 'npm' / 'codex.cmd')
    for path in candidates:
        if path:
            try:
                return _cli_command(path)
            except FileNotFoundError:
                continue
    raise FileNotFoundError('Codex CLI not found. Install Codex or set MISA_CODEX_CLI.')


def read_json_object(path, label):
    """Read console metadata written by either Python or Windows PowerShell."""
    path = Path(path)
    try:
        data = json.loads(path.read_text(encoding='utf-8-sig'))
    except (OSError, ValueError) as error:
        raise ValueError(f'Cannot read {label}: {path} ({type(error).__name__}).') from None
    if not isinstance(data, dict):
        raise ValueError(f'{label} must be a JSON object: {path}.')
    return data


_CONSOLE_RUNNER = '''import json
import ctypes
from ctypes import wintypes
import os
from pathlib import Path
import subprocess
import sys
import time


def send_initial_input(value):
    """Enter a slash command after the interactive TUI has initialized."""
    if not value:
        return
    # MCP startup can take a few seconds before the composer accepts input.
    time.sleep(15.0)
    # GetConsoleWindow belongs to kernel32; the foreground API belongs to
    # user32.  Looking up both on user32 silently leaves the CLI at its empty
    # composer, so the requested /goal is never submitted.
    kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel32.GetConsoleWindow.restype = wintypes.HWND
    user32 = ctypes.WinDLL('user32', use_last_error=True)
    user32.SetForegroundWindow.argtypes = [wintypes.HWND]
    hwnd = kernel32.GetConsoleWindow()
    if not hwnd or not user32.SetForegroundWindow(hwnd):
        raise RuntimeError('Cannot focus the Codex console for the initial goal.')
    clip = Path(os.environ.get('SystemRoot', r'C:\\Windows')) / 'System32' / 'clip.exe'
    subprocess.run([str(clip)], input=value, text=True, encoding='utf-8', check=True)
    keybd_event = user32.keybd_event
    keybd_event.argtypes = [ctypes.c_ubyte, ctypes.c_ubyte, ctypes.c_ulong, ctypes.c_void_p]
    keybd_event.restype = None
    keybd_event(0x11, 0, 0, None)
    keybd_event(0x56, 0, 0, None)
    keybd_event(0x56, 0, 2, None)
    keybd_event(0x11, 0, 2, None)
    keybd_event(0x0D, 0, 0, None)
    keybd_event(0x0D, 0, 2, None)


def require_administrator():
    shell32 = ctypes.WinDLL('shell32', use_last_error=True)
    shell32.IsUserAnAdmin.restype = wintypes.BOOL
    if not shell32.IsUserAnAdmin():
        raise PermissionError('Codex CLI requires an Administrator console.')

folder = Path(__file__).resolve().parent
status = {"state": "failed", "exit_code": None}
try:
    launch = json.loads((folder / "console-launch.json").read_text(encoding="utf-8"))
    if launch.get("require_administrator"):
        require_administrator()
    for key in launch.get("unset_environment", ()):
        os.environ.pop(key, None)
    process = subprocess.Popen(launch["command"], cwd=launch["cwd"])
    send_initial_input(launch.get("initial_input"))
    code = process.wait()
    status = {"state": "exited", "exit_code": code}
except Exception as error:
    status["error"] = str(error)
    print("Cannot start Codex CLI: " + str(error), file=sys.stderr)
finally:
    pending = folder / "console-exit.tmp"
    pending.write_text(json.dumps(status), encoding="utf-8")
    os.replace(pending, folder / "console-exit.json")
sys.exit(status["exit_code"] if status["exit_code"] is not None else 1)
'''


def _shell_execute_runas(file, parameters, cwd):
    """Start a visible process through UAC and raise if consent is cancelled."""
    class ShellExecuteInfo(C.Structure):
        _fields_ = [
            ('cbSize', W.DWORD),
            ('fMask', W.ULONG),
            ('hwnd', W.HWND),
            ('lpVerb', W.LPCWSTR),
            ('lpFile', W.LPCWSTR),
            ('lpParameters', W.LPCWSTR),
            ('lpDirectory', W.LPCWSTR),
            ('nShow', C.c_int),
            ('hInstApp', W.HINSTANCE),
            ('lpIDList', W.LPVOID),
            ('lpClass', W.LPCWSTR),
            ('hkeyClass', W.HKEY),
            ('dwHotKey', W.DWORD),
            ('hIconOrMonitor', W.HANDLE),
            ('hProcess', W.HANDLE),
        ]

    shell32 = C.WinDLL('shell32', use_last_error=True)
    shell32.ShellExecuteExW.argtypes = [C.POINTER(ShellExecuteInfo)]
    shell32.ShellExecuteExW.restype = W.BOOL
    kernel32 = C.WinDLL('kernel32', use_last_error=True)
    kernel32.CloseHandle.argtypes = [W.HANDLE]
    kernel32.TerminateProcess.argtypes = [W.HANDLE, W.UINT]
    advapi32 = C.WinDLL('advapi32', use_last_error=True)
    advapi32.OpenProcessToken.argtypes = [W.HANDLE, W.DWORD, C.POINTER(W.HANDLE)]
    advapi32.OpenProcessToken.restype = W.BOOL
    advapi32.GetTokenInformation.argtypes = [W.HANDLE, C.c_int, W.LPVOID,
                                              W.DWORD, C.POINTER(W.DWORD)]
    advapi32.GetTokenInformation.restype = W.BOOL

    def elevated(process):
        token = W.HANDLE()
        value = W.DWORD()
        size = W.DWORD()
        # TokenElevation (20) is authoritative for the process token.
        if not advapi32.OpenProcessToken(process, 0x0008, C.byref(token)):
            return False
        try:
            return bool(advapi32.GetTokenInformation(
                token, 20, C.byref(value), C.sizeof(value), C.byref(size))) and bool(value.value)
        finally:
            kernel32.CloseHandle(token)

    info = ShellExecuteInfo()
    info.cbSize = C.sizeof(info)
    info.fMask = 0x00000140  # SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC
    info.lpVerb = 'runas'
    info.lpFile = str(file)
    info.lpParameters = str(parameters)
    info.lpDirectory = str(cwd)
    info.nShow = 1  # SW_SHOWNORMAL
    if not shell32.ShellExecuteExW(C.byref(info)):
        error = C.get_last_error()
        if error == 1223:  # ERROR_CANCELLED
            raise PermissionError('UAC đã bị hủy; Codex CLI chưa được mở.')
        raise C.WinError(error)
    if info.hProcess:
        try:
            if not elevated(info.hProcess):
                kernel32.TerminateProcess(info.hProcess, 1)
                raise PermissionError('Codex CMD được mở nhưng không có quyền Administrator.')
        finally:
            kernel32.CloseHandle(info.hProcess)


def start_elevated_codex_console(root, command, *, session_id, initial_input=None):
    """Open the live Codex controller in an elevated CMD after UAC consent."""
    root = Path(root).resolve()
    from .paths import state_dir

    folder = state_dir() / 'live-test' / 'controllers' / str(session_id)
    folder.mkdir(parents=True, exist_ok=True)
    python = root / '.venv' / 'Scripts' / 'python.exe'
    if not python.is_file():
        python = Path(sys.executable)
        if python.name.lower() == 'pythonw.exe':
            python = python.with_name('python.exe')
        if getattr(sys, 'frozen', False) or not python.is_file():
            raise FileNotFoundError('Python console runtime not found; expected .venv/Scripts/python.exe.')
    atomic_json(folder / 'console-launch.json', {
        'command': list(command),
        'cwd': str(root),
        'mode': 'interactive',
        'require_administrator': True,
        'initial_input': initial_input,
        'unset_environment': [
            'MISA_REPAIR_INCIDENT', 'MISA_REPAIR_REQUEST',
            'MISA_CODEX_SESSION_FILE', 'MISA_CODEX_SESSION_NONCE',
            'CODEX_THREAD_ID',
        ],
    })
    bootstrap = folder / 'console-bootstrap.py'
    bootstrap.write_text(_CONSOLE_RUNNER, encoding='utf-8')
    cmd = Path(os.environ['SystemRoot']) / 'System32' / 'cmd.exe'
    command_text = subprocess.list2cmdline([str(python), str(bootstrap)])
    _shell_execute_runas(cmd, '/d /k ' + command_text, root)
    return folder


def start_codex_console(folder, root, arguments, *, session_file=None, session_nonce=None):
    folder, root = Path(folder).resolve(), Path(root).resolve()
    python = root / '.venv' / 'Scripts' / 'python.exe'
    if not python.is_file():
        python = Path(sys.executable)
        if python.name.lower() == 'pythonw.exe':
            python = python.with_name('python.exe')
        if getattr(sys, 'frozen', False) or not python.is_file():
            raise FileNotFoundError('Python console runtime not found; expected .venv/Scripts/python.exe.')
    command = codex_command() + list(arguments)
    atomic_json(folder / 'console-launch.json', dict(command=command, cwd=str(root), mode='interactive'))
    bootstrap = folder / 'console-bootstrap.py'
    bootstrap.write_text(_CONSOLE_RUNNER, encoding='utf-8')
    env = os.environ.copy()
    rg = Path(shutil.which('rg.exe') or '')
    if not rg.is_file():
        codex_bin = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))) / 'OpenAI' / 'Codex' / 'bin'
        rg = next((path for path in sorted(codex_bin.glob('*/rg.exe'),
                                           key=lambda path: path.stat().st_mtime, reverse=True)
                   if path.is_file()), Path())
    if rg.is_file():
        env['PATH'] = str(rg.parent.resolve()) + os.pathsep + env.get('PATH', '')
    # A CLI launched by another Codex must register its own conversation, not the parent's.
    env.pop('CODEX_THREAD_ID', None)
    env.update(MISA_CODEX_CONSOLE_PYTHON=str(python), MISA_CODEX_CONSOLE_BOOTSTRAP=str(bootstrap))
    if session_file is not None:
        request_folder = Path(session_file).resolve().parent
        env.update(MISA_CODEX_SESSION_FILE=str(session_file), MISA_CODEX_SESSION_NONCE=session_nonce,
                   MISA_REPAIR_REQUEST=request_folder.name,
                   MISA_REPAIR_INCIDENT=request_folder.parent.parent.name)
    cmd = Path(os.environ['SystemRoot']) / 'System32' / 'cmd.exe'
    # Only quoted paths cross CMD. The full argv and prompt stay in UTF-8 files.
    command_line = (subprocess.list2cmdline([str(cmd)]) +
                    ' /d /v:off /s /c ""%MISA_CODEX_CONSOLE_PYTHON%" "%MISA_CODEX_CONSOLE_BOOTSTRAP%""')
    return OwnedProcess(command_line, executable=str(cmd), cwd=root, env=env, visible_console=True)


def console_exit_error(folder, code):
    path = Path(folder) / 'console-exit.json'
    if path.is_file():
        status = read_json_object(path, 'Codex console exit metadata')
        if status.get('state') == 'failed':
            return status.get('error', 'Cannot start Codex CLI.')
        code = status.get('exit_code', code)
    if code not in (0, 130, -1073741510, 3221225786):
        return f'Codex CLI exited before handing off source (exit={code}).'
    return ''


class CodexConsoleSession:
    """One visible, isolated Codex CLI conversation for the current repair request."""

    def __init__(self):
        self.owned = None
        self.root = None
        self.folder = None
        self.thread_id = None
        self.nonce = None
        self.registered = False

    def configure(self, root):
        root = Path(root).resolve()
        self.close()
        self.root, self.thread_id = root, None
        self.folder = None

    def register(self):
        if self.folder is None:
            return
        path = self.folder / 'console-session.json'
        if not path.is_file():
            return
        saved = read_json_object(path, 'Codex session registration')
        if saved.get('nonce') != self.nonce:
            raise ValueError('Codex session registration does not match this console.')
        thread_id = str(uuid.UUID(saved['thread_id']))
        self.thread_id = thread_id
        self.registered = True

    def submit(self, folder, root, arguments, message, images, cancel, *, reasoning_effort=None):
        self.configure(root)
        if cancel.is_set():
            raise Cancelled('Đã dừng trước khi gửi yêu cầu vào Codex CLI.')
        model_arguments = configured_model_arguments(root, reasoning_effort=reasoning_effort or REPAIR_REASONING_EFFORT)
        images = [str(Path(path).resolve()) for path in images]
        self.folder = Path(folder)
        self.nonce = uuid.uuid4().hex
        # Never queue or resume a repair: an incident must begin with a clean conversation.
        command = list(arguments) + model_arguments
        for path in images:
            command.extend(['--image', str(path)])
        # End variadic image arguments before the fresh conversation's prompt.
        command.append('--')
        command.append(message)
        self.owned = start_codex_console(folder, root, command,
            session_file=self.folder / 'console-session.json', session_nonce=self.nonce)
        return 'opened'

    def wait_result(self, folder, cancel):
        while True:
            if cancel.is_set():
                raise Cancelled('Đã dừng yêu cầu sửa Codex.')
            try:
                self.register()
            except (OSError, ValueError, KeyError):
                self.close()
                raise
            if (Path(folder) / 'result.json').is_file():
                if not self.thread_id or not self.registered:
                    self.close()
                    raise RuntimeError('Codex chưa lưu ID hội thoại mới cho yêu cầu hiện tại.')
                return
            code = self.owned.process.poll()
            if code is not None:
                error = console_exit_error(self.folder, code)
                self.close()
                if error:
                    raise RuntimeError(error)
                raise Cancelled('Đã đóng Codex CLI trước khi bàn giao source.')
            cancel.wait(.2)

    def close(self):
        if self.owned is not None:
            self.owned.close()
            self.owned = None
        self.thread_id = None
        self.nonce = None
        self.registered = False
