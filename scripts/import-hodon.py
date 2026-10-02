"""Sequential invoice import with durable request identity and no uncertain replay."""

import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import time
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[1] / 'out'
BRIDGE = ROOT / 'bin/MisaManager.McpBridge/Release/net10.0-windows/MisaManager.McpBridge.exe'
DEST = ROOT / 'hodon-batch'


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def write_json(path, value):
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=True, sort_keys=True), encoding='utf-8')
    temporary.replace(path)


def handshake_not_sent(result):
    return (result.get('status') == 'blocked'
            and result.get('inputMayHaveBeenSent') is False
            and result.get('message', '').startswith('Control handshake failed:')
            and 'named pipe is not ready' in result.get('message', ''))


def start_manager():
    # Use the app beside this bridge, never an older installed release.
    app = ROOT / 'bin/MisaManager.App/Release/net10.0-windows/MisaManager.App.exe'
    if not app.is_file():
        raise RuntimeError(f'Matching manager executable is missing: {app}')
    # App has a single-instance mutex. Never terminate an existing owner.
    subprocess.Popen([str(app)], cwd=str(app.parent),
                     creationflags=subprocess.CREATE_NO_WINDOW)


def call(action, body, key):
    DEST.mkdir(parents=True, exist_ok=True)
    arg = DEST / (key + '-args.json')
    result = DEST / (key + '-result.json')
    if arg.exists() and read_json(arg) != body:
        raise RuntimeError(f'Idempotency key {key} has a different body; use a new attempt after inspecting the old result.')
    write_json(arg, body)
    if result.exists() and not handshake_not_sent(read_json(result)):
        # Uncertain/completed/blocked business results belong to the original
        # request. Inspection, not an automatic replay, resolves them.
        return read_json(result)
    command = [str(BRIDGE), '--call', action, '--arguments-file', str(arg),
               '--idempotency-key', key, '--output', str(result)]
    started_manager = False
    for attempt in range(12):
        if result.exists():
            result.unlink()  # Only a confirmed pre-send handshake failure reaches here.
        completed = subprocess.run(command, check=False)
        if not result.exists():
            raise RuntimeError(f'Bridge produced no response (exit {completed.returncode}); retain key {key} and inspect before continuing.')
        value = read_json(result)
        if completed.returncode not in (0, 2):
            raise RuntimeError(f'Bridge exited with {completed.returncode}; inspect {result}.')
        if not handshake_not_sent(value):
            return value
        if not started_manager:
            start_manager()
            started_manager = True
        if attempt < 11:
            time.sleep(5)
    raise RuntimeError('Manager handshake did not become ready; no business request was dispatched.')


def current_window_identity():
    result = call('diagnostics', {}, 'hodon-diagnostics-' + uuid.uuid4().hex)
    if result.get('status') != 'completed':
        raise RuntimeError('Fresh MISA diagnostics failed: ' + result.get('message', 'unknown error'))
    native = (result.get('data') or {}).get('native') or {}
    candidates = [candidate for candidate in native.get('candidates', [])
                  if re.fullmatch(r'MISA\.SME(?:\d{4})?\.exe',
                                  pathlib.PureWindowsPath(candidate.get('executablePath', '')).name,
                                  re.IGNORECASE)]
    if len(candidates) != 1:
        raise RuntimeError(f'Expected exactly one live MISA window, found {len(candidates)}.')
    candidate = candidates[0]
    return ';'.join([f"pid={candidate['processId']}", f"start={candidate['processStartUtc']}",
                     f"hwnd={candidate['windowHandle']}", f"exe={candidate['executablePath']}"])


def record(number, action, result):
    row = {'number': number, 'action': action, 'time': time.time(), 'result': result}
    with (DEST / 'results.jsonl').open('a', encoding='utf-8') as handle:
        handle.write(json.dumps(row, ensure_ascii=True) + '\n')
    print(number, action, result.get('status'),
          (result.get('data') or {}).get('checkpoint'), flush=True)


def require_current_save_checkpoint(invoice, run_id, window_identity):
    value = call('workflow_status', {}, 'hodon-status-' + uuid.uuid4().hex)
    snapshot = value.get('data') or {}
    if (value.get('status') != 'completed' or snapshot.get('runId') != run_id
            or snapshot.get('invoiceId') != invoice['invoiceId']
            or snapshot.get('invoiceRevision') != invoice['revision']
            or snapshot.get('status') != 'awaiting_save' or snapshot.get('inputPending')
            or snapshot.get('windowIdentity') != window_identity):
        raise RuntimeError('Current workflow is not the verified AwaitingSave run; no Save is sent.')


def import_invoice(invoice, attempt):
    number = invoice['identity']['number']
    key = 'hodon-' + number + '-' + attempt
    save_args = {'expectedRevision': invoice['fingerprint'], 'confirmed': True}
    save_file = DEST / (key + '-save-result.json')
    if save_file.exists():
        saved = read_json(save_file)
        if (saved.get('status') == 'completed' and (saved.get('data') or {}).get('status') == 'saved'
                and read_json(DEST / (key + '-save-args.json')) == save_args):
            return
        if not handshake_not_sent(saved):
            raise RuntimeError(f'Prior Save result for {number} needs inspection; no Save is replayed.')
    window_identity = current_window_identity()
    body = {'invoice': invoice, 'companyIdentity': invoice['seller']['name'],
            'windowIdentity': window_identity, 'executionMode': 'live_production',
            'liveValidation': True, 'allowKgToTon': True,
            'expectedRevision': invoice['fingerprint'], 'until': 'AwaitingSave', 'maxSteps': 20}
    result = call('run_workflow', body, key + '-run')
    record(number, 'run', result)
    if result.get('status') != 'completed' or (result.get('data') or {}).get('checkpoint') != 'awaiting_save':
        raise RuntimeError(f'Invoice {number} stopped before verified AwaitingSave; inspect its run result.')
    require_current_save_checkpoint(invoice, result['data']['runId'], window_identity)
    result = call('save', save_args, key + '-save')
    record(number, 'save', result)
    if result.get('status') != 'completed' or (result.get('data') or {}).get('status') != 'saved':
        raise RuntimeError(f'Invoice {number} has no verified Saved result; Save is not replayed.')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--numbers', nargs='+', required=True)
    parser.add_argument('--attempt', required=True)
    opts = parser.parse_args()
    if not re.fullmatch(r'[A-Za-z0-9_-]{1,80}', opts.attempt):
        parser.error('--attempt must contain only letters, digits, underscore or hyphen')
    invoices = read_json(ROOT / 'hodon-import.json')['invoices']
    by_number = {invoice['identity']['number']: invoice for invoice in invoices}
    if len(by_number) != len(invoices) or len(set(opts.numbers)) != len(opts.numbers):
        parser.error('Invoice numbers must be unique')
    missing = set(opts.numbers) - by_number.keys()
    if missing:
        parser.error('Unknown invoice numbers: ' + ', '.join(sorted(missing)))
    for source in {invoice['source']['filePath']: invoice['source']['sha256'] for invoice in invoices}.items():
        path, digest = source
        if hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest() != digest.lower():
            raise RuntimeError('Source workbook changed; regenerate the import before entering MISA.')
    for number in opts.numbers:
        import_invoice(by_number[number], opts.attempt)


if __name__ == '__main__':
    main()
