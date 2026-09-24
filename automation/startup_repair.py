"""The same Codex repair channel when the main application cannot start."""
from .domain import Cancelled


def repair_startup_error(error, report, cancel, notify, *, console=None):
    """Report a startup error locally; automatic Codex launch is gone.

    The ``console`` argument remains for compatibility with older callers, but
    it is never used. Startup recovery is owned by the explicit live-test
    control plane, not by this helper.
    """
    if cancel.is_set():
        raise Cancelled('Startup repair cancelled.')
    details = dict(status='manual_attention', error=str(error), report=str(report),
                   message='Startup error recorded locally; no automatic Codex session was launched.')
    notify('manual_attention', details)
    return details
