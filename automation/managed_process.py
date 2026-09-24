"""A cancellable Windows process tree owned by one repair/build request."""
import ctypes as C
from ctypes import wintypes as W
import subprocess


class OwnedProcess:
    def __init__(self, command, *, visible_console=False, **kwargs):
        kernel = C.WinDLL('kernel32', use_last_error=True)
        self.kernel, self.job = kernel, None
        kernel.CreateJobObjectW.argtypes = [W.LPVOID, W.LPCWSTR]
        kernel.CreateJobObjectW.restype = W.HANDLE
        kernel.SetInformationJobObject.argtypes = [W.HANDLE, C.c_int, W.LPVOID, W.DWORD]
        kernel.AssignProcessToJobObject.argtypes = [W.HANDLE, W.HANDLE]
        kernel.CloseHandle.argtypes = [W.HANDLE]

        class Basic(C.Structure):
            _fields_ = [('process_time', C.c_int64), ('job_time', C.c_int64), ('flags', W.DWORD),
                        ('min_working_set', C.c_size_t), ('max_working_set', C.c_size_t),
                        ('active_processes', W.DWORD), ('affinity', C.c_size_t),
                        ('priority', W.DWORD), ('scheduling', W.DWORD)]

        class Limits(C.Structure):
            _fields_ = [('basic', Basic), ('io', C.c_uint64 * 6),
                        ('process_memory', C.c_size_t), ('job_memory', C.c_size_t),
                        ('peak_process_memory', C.c_size_t), ('peak_job_memory', C.c_size_t)]

        limits = Limits()
        limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        self.job = kernel.CreateJobObjectW(None, None)
        if not self.job or not kernel.SetInformationJobObject(self.job, 9, C.byref(limits), C.sizeof(limits)):
            self.close_job()
            raise OSError(C.get_last_error(), 'Cannot own the repair process tree.')
        try:
            console_flags = subprocess.CREATE_NEW_CONSOLE if visible_console else subprocess.CREATE_NO_WINDOW
            self.process = subprocess.Popen(command,
                creationflags=console_flags | 0x00000004, **kwargs)
            if not kernel.AssignProcessToJobObject(self.job, W.HANDLE(int(self.process._handle))):
                self.process.kill()
                self.process.wait()
                raise OSError(C.get_last_error(), 'Cannot attach the repair to its cancellation job.')
            # The process cannot spawn descendants before it belongs to the cancellation job.
            resume = C.WinDLL('ntdll').NtResumeProcess
            resume.argtypes, resume.restype = [W.HANDLE], W.LONG
            if resume(W.HANDLE(int(self.process._handle))) != 0:
                raise OSError('Cannot resume the owned repair process.')
        except BaseException:
            self.close_job()
            if hasattr(self, 'process'):
                self.process.wait()
            raise

    def close_job(self):
        if self.job:
            self.kernel.CloseHandle(self.job)
            self.job = None

    def close(self):
        self.close_job()
        self.process.wait()
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
            if stream is not None:
                stream.close()

    def wait(self, cancel):
        while self.process.poll() is None:
            if cancel.wait(.1):
                self.close_job()
                self.process.wait()
                from .domain import Cancelled
                raise Cancelled('Đã dừng tiến trình phục hồi và các tiến trình con.')
        return self.process.returncode
