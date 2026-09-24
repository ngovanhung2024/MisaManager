"""Live timings and bounded, asynchronous evidence for user-run workflows."""
from collections import deque
from contextlib import contextmanager
from datetime import datetime, timezone
from functools import wraps
import hashlib
import io
import json
import os
import platform
from pathlib import Path
import queue
import threading
import time
import uuid


_LOCAL = threading.local()


def source_fingerprint():
    import source_updates
    root = Path(source_updates.__file__).resolve().parent
    paths = [root / name for name in ('app.py', 'invoice_xml.py', 'source_updates.py', 'requirements.txt')]
    paths += sorted((root / 'automation').glob('*.py'))
    checksum = hashlib.sha256()
    for path in paths:
        checksum.update(path.relative_to(root).as_posix().encode('utf-8') + b'\0')
        checksum.update(path.read_bytes())
        checksum.update(b'\0')
    return checksum.hexdigest()


def current_trace():
    return getattr(_LOCAL, 'trace', None)


@contextmanager
def span(kind, label, **details):
    trace = current_trace()
    if trace is None:
        yield details
    else:
        with trace.operation(kind, label, **details) as record:
            yield record


def tracked(kind, label):
    def decorate(function):
        @wraps(function)
        def call(*args, **kwargs):
            title = label(*args, **kwargs) if callable(label) else label
            with span(kind, title):
                return function(*args, **kwargs)
        return call
    return decorate


def record(event, **details):
    trace = current_trace()
    if trace is not None:
        trace.record(event, **details)


def record_image(picture, *, frame_id, role, **details):
    from .incident_evidence import current_evidence
    journal = current_evidence()
    if journal is not None:
        with span('evidence', 'Encode/write mandatory PNG evidence', role=role):
            return journal.image(picture, frame_id=frame_id, role=role, **details)
    trace = current_trace()
    return trace.image(picture, frame_id=frame_id, role=role, **details) if trace else None


class PerformanceTrace:
    """One attempt, with a UI-readable clock that never waits for disk."""

    def __init__(self, folder, *, run_id, source_revision='', context=None, capture_images=True):
        self.session_id = uuid.uuid4().hex
        self.folder = Path(folder) / run_id / self.session_id
        self.run_id = run_id
        self.source_revision = source_revision
        self.context = dict(context or {})
        self.capture_images = capture_images
        self.started_at = datetime.now(timezone.utc).isoformat()
        self.started = time.perf_counter()
        self.finished = None
        self.finished_at = None
        self.state = 'running'
        self._lock = threading.RLock()
        self._stack = []
        self._recent = deque(maxlen=12)
        self._steps = deque(maxlen=100)
        self._slowest_step = None
        self._totals = {}
        self._last_observation = {}
        self._sequence = 0
        self._queue = queue.Queue(maxsize=2048)
        self._image_slots = threading.BoundedSemaphore(8)
        self._closing = threading.Event()
        self._written_bytes = 0
        self._dropped_images = 0
        self._dropped_events = 0
        self._error = ''
        self._writer_done = False
        try:
            limit_mb = int(os.environ.get('MISA_PERF_MAX_MB', '16'))
            if not 16 <= limit_mb <= 8192:
                raise ValueError('MISA_PERF_MAX_MB must be between 16 and 8192.')
            self._max_bytes = limit_mb * 1024 * 1024
        except ValueError as error:
            self._max_bytes = 16 * 1024 * 1024
            self._error = str(error) + ' Using 16 MiB.'
        self._writer = threading.Thread(target=self._write, name='misa-performance-log', daemon=True)
        self._writer.start()
        self.record('session_start', source_revision=source_revision,
                    verification_status='pending_live', image_limit_bytes=self._max_bytes,
                    capture_images=capture_images, context=self.context,
                    python=platform.python_version(), manager_pid=os.getpid(),
                    settings={'MISA_PERF_MAX_MB': os.environ.get('MISA_PERF_MAX_MB', '16')})

    @contextmanager
    def bind(self):
        previous = current_trace()
        _LOCAL.trace = self
        try:
            yield self
        finally:
            _LOCAL.trace = previous

    @contextmanager
    def operation(self, kind, label, **details):
        started = time.perf_counter()
        with self._lock:
            entry = dict(operation_id=uuid.uuid4().hex,
                         parent_id=self._stack[-1]['operation_id'] if self._stack else None,
                         kind=kind, label=label, started=started, _child_ms=0.0, **details)
            self._stack.append(entry)
        self.record('operation_start', **{k: v for k, v in entry.items() if k not in ('started', '_child_ms')})
        result = {}
        status, error_text = 'ok', ''
        try:
            yield result
        except BaseException as error:
            status = 'cancelled' if type(error).__name__ == 'Cancelled' else 'error'
            error_text = f'{type(error).__name__}: {error}'
            raise
        finally:
            elapsed_raw = (time.perf_counter() - started) * 1000
            elapsed = round(elapsed_raw, 2)
            end = dict(entry)
            end.update(result)
            end.update(elapsed_ms=elapsed, status=status, error=error_text)
            end.pop('started', None)
            end['self_ms'] = round(max(0.0, elapsed_raw - end.pop('_child_ms')), 2)
            compact = {key: value for key, value in end.items() if key not in ('words', 'raw_results')}
            with self._lock:
                self._stack.remove(entry)
                parent = next((s for s in self._stack if s['operation_id'] == entry['parent_id']), None)
                if parent is not None:
                    parent['_child_ms'] += elapsed_raw
                total = self._totals.setdefault(kind, dict(count=0, elapsed_ms=0.0, self_ms=0.0,
                                                           max_ms=0.0, errors=0, cancelled=0))
                total['count'] += 1
                total['elapsed_ms'] += elapsed
                total['self_ms'] += end['self_ms']
                total['max_ms'] = max(total['max_ms'], elapsed)
                total['errors'] += status == 'error'
                total['cancelled'] += status == 'cancelled'
                if kind == 'step':
                    self._steps.append(compact)
                    if self._slowest_step is None or elapsed > self._slowest_step['elapsed_ms']:
                        self._slowest_step = compact
                if kind == 'observation':
                    self._last_observation = compact
                if kind in ('action', 'step', 'observation', 'startup'):
                    self._recent.append({key: value for key, value in compact.items()
                                         if key in ('kind', 'label', 'elapsed_ms', 'status', 'error')})
            self.record('operation_end', **end)

    def _enqueue(self, item):
        if self._writer_done or self._closing.is_set():
            with self._lock:
                self._dropped_events += 1
            return False
        try:
            self._queue.put_nowait(item)
            return True
        except queue.Full:
            with self._lock:
                self._dropped_events += 1
            return False

    def record(self, event, **details):
        with self._lock:
            self._sequence += 1
            row = dict(schema=1, event=event, sequence=self._sequence, at=time.time(),
                       since_start_ms=round((time.perf_counter() - self.started) * 1000, 2),
                       run_id=self.run_id, session_id=self.session_id,
                       operation_id=self._stack[-1]['operation_id'] if self._stack else None,
                       checkpoint=next((s.get('checkpoint', '') for s in reversed(self._stack)
                                        if s['kind'] == 'step'), ''))
            row.update(details)
        from .incident_evidence import current_evidence
        journal = current_evidence()
        if journal is not None:
            journal.record(event, **{k: v for k, v in row.items() if k != 'event'})
        self._enqueue(('event', row))

    def image(self, picture, *, frame_id, role, **details):
        if not self.capture_images:
            return None
        image_id = uuid.uuid4().hex
        meta = dict(image_id=image_id, frame_id=frame_id, role=role,
                    size=list(picture.size), mode=picture.mode, **details)
        if not self._image_slots.acquire(blocking=False):
            with self._lock:
                self._dropped_images += 1
            self.record('image_skipped', reason='image_queue_full', **meta)
            return image_id
        queued = False
        copied = None
        try:
            copied = picture.copy()
            self.record('image_queued', **meta)
            queued = self._enqueue(('image', meta, copied))
            if queued:
                return image_id
            self.record('image_skipped', reason='writer_unavailable', **meta)
            with self._lock:
                self._dropped_images += 1
        finally:
            if not queued:
                if copied is not None:
                    copied.close()
                self._image_slots.release()
        return image_id

    def snapshot(self):
        with self._lock:
            now = self.finished or time.perf_counter()
            active = [dict({key: value for key, value in s.items() if key not in ('started', '_child_ms')},
                           elapsed_ms=round((now - s['started']) * 1000, 1)) for s in self._stack]
            recent = list(self._recent)
            return dict(session_id=self.session_id, run_id=self.run_id, state=self.state,
                        source_revision=self.source_revision, context=dict(self.context),
                        started_at=self.started_at, finished_at=self.finished_at,
                        elapsed_ms=round((now - self.started) * 1000, 1), active=active,
                        steps=[dict(step) for step in self._steps],
                        slowest_step=dict(self._slowest_step) if self._slowest_step else None,
                        totals={key: dict(value) for key, value in self._totals.items()},
                        capture_images=self.capture_images,
                        last_observation=dict(self._last_observation), recent=recent,
                        log_folder=str(self.folder),
                        log_error=self._error, dropped_images=self._dropped_images,
                        dropped_events=self._dropped_events, written_bytes=self._written_bytes,
                        pending_writes=self._queue.qsize(), writer_done=self._writer_done)

    def finish(self, state):
        with self._lock:
            if self.finished is not None:
                return
            self.state = state
            self.finished = time.perf_counter()
            self.finished_at = datetime.now(timezone.utc).isoformat()
        self.record('session_end', state=state)
        self._closing.set()
        self._writer.join(timeout=3)

    def _write(self):
        from source_updates import atomic_json
        stream = None

        def write_summary():
            snapshot = self.snapshot()
            atomic_json(self.folder / 'summary.json', snapshot)
            (self.folder / 'summary.txt').write_text(render_performance(snapshot), encoding='utf-8')

        try:
            self.folder.mkdir(parents=True, exist_ok=True)
            if self.capture_images:
                (self.folder / 'images').mkdir(exist_ok=True)
            stream = (self.folder / 'events.jsonl').open('a', encoding='utf-8', buffering=1)

            def write(event):
                row = dict(schema=1, run_id=self.run_id, session_id=self.session_id, at=time.time())
                row.update(event)
                text = json.dumps(row, ensure_ascii=False) + '\n'
                size = len(text.encode('utf-8'))
                with self._lock:
                    if self._written_bytes + size > self._max_bytes:
                        self._dropped_events += 1
                        self._error = 'Log reached MISA_PERF_MAX_MB; further events were omitted.'
                        return
                stream.write(text)
                with self._lock:
                    self._written_bytes += size

            atomic_json(self.folder / 'manifest.json', dict(schema=1, run_id=self.run_id,
                        session_id=self.session_id, source_revision=self.source_revision,
                        evidence='historical_after_capture', verification_status='pending_live',
                        events='events.jsonl', summary='summary.json', report='summary.txt',
                        context=self.context, started_at=self.started_at,
                        capture_images=self.capture_images, log_limit_bytes=self._max_bytes))
            next_summary = 0.0
            while not self._closing.is_set() or not self._queue.empty():
                if time.monotonic() >= next_summary:
                    write_summary()
                    next_summary = time.monotonic() + 1.0
                try:
                    item = self._queue.get(timeout=.1)
                except queue.Empty:
                    continue
                if item[0] == 'event':
                    write(item[1])
                    continue
                _, meta, picture = item
                try:
                    started = time.perf_counter()
                    encoded = io.BytesIO()
                    picture.save(encoded, format='PNG', compress_level=1)
                    data = encoded.getvalue()
                    encoded_ms = (time.perf_counter() - started) * 1000
                    if self._written_bytes + len(data) > self._max_bytes:
                        with self._lock:
                            self._dropped_images += 1
                        write(dict(event='image_skipped', reason='session_size_limit', **meta))
                        continue
                    name = 'images/' + meta['image_id'] + '.png'
                    saved = time.perf_counter()
                    (self.folder / name).write_bytes(data)
                    with self._lock:
                        self._written_bytes += len(data)
                    write(dict(event='image_written', path=name, bytes=len(data),
                               sha256=hashlib.sha256(data).hexdigest(),
                               encode_ms=round(encoded_ms, 2),
                               write_ms=round((time.perf_counter() - saved) * 1000, 2), **meta))
                except Exception as error:
                    with self._lock:
                        self._error = f'{type(error).__name__}: {error}'
                        self._dropped_images += 1
                    write(dict(event='image_error', error=self._error, **meta))
                finally:
                    picture.close()
                    self._image_slots.release()
            write(dict(event='log_closed', dropped_images=self._dropped_images,
                       dropped_events=self._dropped_events, written_bytes=self._written_bytes))
        except Exception as error:
            with self._lock:
                self._error = f'{type(error).__name__}: {error}'
        finally:
            if stream:
                try:
                    stream.close()
                except OSError as error:
                    with self._lock:
                        self._error = f'{type(error).__name__}: {error}'
            with self._lock:
                self._writer_done = True
            while True:
                try:
                    item = self._queue.get_nowait()
                except queue.Empty:
                    break
                if item[0] == 'image':
                    item[2].close()
                    self._image_slots.release()
                with self._lock:
                    self._dropped_events += 1
            try:
                write_summary()
            except Exception as error:
                with self._lock:
                    self._error = self._error or f'{type(error).__name__}: {error}'


def render_performance(snapshot):
    """A copyable report built from memory; rendering never reads MISA or disk."""
    if not snapshot:
        return 'Chưa có lượt Thêm trong phiên này.'
    context = snapshot.get('context', {})
    state = {'running': 'Đang chạy', 'complete': 'Hoàn tất', 'blocked': 'Gặp lỗi',
             'paused': 'Đã dừng'}.get(snapshot.get('state'), snapshot.get('state', ''))
    lines = [
        'HIỆU SUẤT LƯỢT THÊM',
        'Hóa đơn: ' + str(context.get('invoice_number', '')),
        'XML: ' + str(context.get('invoice_source', '')),
        'Run ID: ' + str(snapshot.get('run_id', '')),
        'Source: ' + str(snapshot.get('source_revision', '')),
        'Bắt đầu (UTC): ' + str(snapshot.get('started_at', '')),
        'Kết thúc (UTC): ' + str(snapshot.get('finished_at') or 'Đang chạy'),
        f"Trạng thái: {state} | Tổng thời gian: {snapshot.get('elapsed_ms', 0) / 1000:.2f} s",
        'Số dòng XML: ' + str(context.get('line_count', 'Chưa có')),
        'Thư mục log: ' + str(snapshot.get('log_folder', '')),
        'Đo thời gian không xác nhận chứng từ được nhập đúng.',
        'Nguồn số đo: ' + ('phiên trước đã lưu' if snapshot.get('historical') else 'lượt người dùng đang/đã chạy'),
        '', 'ĐANG THỰC HIỆN',
    ]
    active = snapshot.get('active', [])
    lines.extend(f"{item['label']}: {item['elapsed_ms'] / 1000:.2f} s" for item in active)
    if not active:
        lines.append('Không có thao tác đang chạy.')
    lines += ['', 'THỜI GIAN TỪNG BƯỚC']
    steps = snapshot.get('steps', [])
    if steps:
        slowest = snapshot.get('slowest_step') or max(steps, key=lambda item: item['elapsed_ms'])
        lines.append(f"Bước lâu nhất: {slowest['label']} ({slowest['elapsed_ms'] / 1000:.2f} s)")
    for item in steps:
        lines.append(f"{item.get('checkpoint', '')} | {item['elapsed_ms'] / 1000:.2f} s | "
                     f"{item['status']} | {item['label']}")
        if item.get('error'):
            lines.append('  ' + item['error'])
    lines += ['', 'TỔNG HỢP THAO TÁC',
              'Loại | Số lần | Tổng ms | Riêng ms | Lớn nhất ms | Lỗi | Hủy',
              'Tổng bao gồm thao tác con; Riêng đã trừ thao tác con.']
    ranked = sorted(snapshot.get('totals', {}).items(), key=lambda pair: pair[1]['self_ms'], reverse=True)
    for kind, item in ranked:
        lines.append(f"{kind} | {item['count']} | {item['elapsed_ms']:.1f} | {item['self_ms']:.1f} | "
                     f"{item['max_ms']:.1f} | {item['errors']} | {item['cancelled']}")
    observation = snapshot.get('last_observation')
    if observation:
        lines += ['', 'QUAN SÁT NATIVE GẦN NHẤT', json.dumps(observation, ensure_ascii=False, default=str)]
    lines += ['', 'THAO TÁC GẦN NHẤT']
    for item in snapshot.get('recent', []):
        lines.append(f"{item['kind']} | {item['elapsed_ms']:.1f} ms | {item['status']} | {item['label']}")
    lines += ['', f"Sự kiện bị lược: {snapshot.get('dropped_events', 0)} | "
              f"Đang chờ ghi: {snapshot.get('pending_writes', 0)}",
              'Lỗi ghi log: ' + (snapshot.get('log_error') or 'Không có'),
              'Chi tiết: events.jsonl; báo cáo: summary.txt và summary.json.']
    return '\n'.join(lines)
