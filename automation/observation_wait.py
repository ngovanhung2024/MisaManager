"""Bounded retries for observation only; callers must keep input outside observe."""
import time

from .domain import Cancelled, NeedsAttention


def read_until(observe, accepted, cancel, *, timeout=5.0, interval=.016, label='MISA'):
    """Repeat a read at a bounded start-to-start cadence.

    Native reads and guarded captures often take longer than ``interval`` themselves.
    Sleeping for the full interval after such a read adds idle latency without
    giving MISA more time than it already had.  Preserve the requested cadence
    for cheap native checks, but start the next read immediately when the
    previous observation already consumed that budget.
    """
    started = time.monotonic()
    deadline = started + timeout
    attempts = 0

    def check_cancelled():
        if cancel.is_set():
            raise Cancelled('Da dung khi cho doc lai ' + label)

    while True:
        check_cancelled()
        if time.monotonic() >= deadline:
            elapsed = time.monotonic() - started
            raise NeedsAttention(f'Het thoi han doc lai {label} sau {attempts} lan doc '
                                 f'({elapsed:.1f}s); unverified')
        attempts += 1
        attempt_started = time.monotonic()
        value = observe()
        check_cancelled()
        # The budget limits new reads. A read already in flight can finish;
        # the caller still checks frame freshness, window identity and values.
        if accepted(value):
            return value
        remaining_cadence = interval - (time.monotonic() - attempt_started)
        cancel.wait(min(max(0, remaining_cadence), max(0, deadline - time.monotonic())))
