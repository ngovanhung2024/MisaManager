"""Project verification rules. Schema validation alone is never MISA evidence."""
from datetime import datetime, timezone
from decimal import Decimal
import re
import unicodedata
from typing import Literal

from pydantic import BaseModel, ConfigDict

from .domain import NeedsAttention, decimal_vi
from invoice_xml import amount_matches


Status = Literal['matched', 'mismatched', 'unverified']


def utc_now():
    return datetime.now(timezone.utc)


def text_key(value):
    return ' '.join(unicodedata.normalize('NFC', str(value)).split()).casefold()


class Reading(BaseModel):
    model_config = ConfigDict(extra='forbid', frozen=True)
    source: Literal['UIA3', 'Win32']
    source_detail: str = ''
    actual_raw: str | None = None
    observed_at: datetime
    frame_id: str
    hwnd: int
    pid: int
    document: str
    row: int | None = None  # One-based voucher row, never a visible-row offset.
    field: str
    checkpoint: str
    run_token: str
    input_epoch: int
    bounds: tuple[float, float, float, float] | None = None
    usable: bool = True
    reason: str = ''


class Verification(BaseModel):
    model_config = ConfigDict(extra='forbid', frozen=True)
    expected: str
    hwnd: int
    pid: int
    document: str
    row: int | None
    field: str
    checkpoint: str
    actual_raw: str | None
    actual_normalized: str | None
    rule: str
    status: Status
    reason: str
    evidence: list[Reading]
    evaluated_at: datetime


def normalized(value, rule, *, source=None):
    raw = str(value).strip()
    if rule == 'decimal' or rule.startswith('price_for:') or rule == 'numeric':
        number = decimal_vi(raw)
        if not number:
            return '0'
        text = format(number, 'f')
        return text.rstrip('0').rstrip('.') if '.' in text else text
    if rule == 'date':
        match = re.fullmatch(
            r'([0-9]{2}/[0-9]{2}/[0-9]{4})'
            r'(?:[ \t]+([0-9]{1,2}):([0-9]{2}):([0-9]{2})(?:[ \t]+(SA|CH|AM|PM))?)?',
            raw, re.I | re.ASCII)
        if not match or (match[2] is not None and source not in ('UIA3', 'Win32')):
            raise ValueError('Ngay khong dung dd/MM/yyyy')
        day = datetime.strptime(match[1], '%d/%m/%Y')
        if match[2] is not None:
            # Native date editors can expose a time hidden by their display format.
            # Validate the full suffix exposed by the native date editor.
            hour = int(match[2])
            if match[5]:
                if not 1 <= hour <= 12:
                    raise ValueError('Gio SA/CH/AM/PM khong hop le')
                hour = hour % 12 + (12 if match[5].upper() in ('CH', 'PM') else 0)
            day = day.replace(hour=hour, minute=int(match[3]), second=int(match[4]))
        return day.date().isoformat()
    if rule == 'invoice_number':
        if not re.fullmatch(r'[0-9]+', raw):
            raise ValueError('So hoa don khong chi gom chu so')
        return str(int(raw))
    if rule in ('row_count', 'count_read'):
        match = re.fullmatch(r'(?:S[oố]\s*d[oò]ng\s*=?\s*)?([0-9]+)', raw, re.I)
        if not match:
            raise ValueError('So dong khong doc duoc')
        return str(int(match[1]))
    if rule == 'seller_title':
        return text_key(raw.split(' - ')[-1])
    if rule == 'unit':
        key = text_key(raw)
        return {'kilogram': 'kg', 'kilogam': 'kg', 'kgs': 'kg'}.get(key, key)
    if rule == 'tax_rate':
        key = raw.upper().replace(' ', '')
        if key in ('KCT', 'KKKNT', 'KHAC'):
            return key
        number = key.removesuffix('%')
        if not re.fullmatch(r'[0-9]+(?:[.,][0-9]+)?', number):
            raise ValueError('Thue suat khong hop le')
        return format(Decimal(number.replace(',', '.')).normalize(), 'f') + '%'
    if rule == 'text':
        return text_key(raw)
    if rule == 'nonempty':
        return 'present' if raw else ''
    if rule == 'empty':
        return raw
    if rule == 'exact':
        return unicodedata.normalize('NFC', raw)
    raise ValueError('Unknown verification rule: ' + rule)


def verify(expected, readings, *, rule='exact', hwnd, pid, document, row, field,
           checkpoint, run_token, input_epoch, not_before, max_age=30):
    """Only current, correctly scoped, agreeing observations can match."""
    now = utc_now()
    actual_raw = actual_normalized = None

    def result(status, reason):
        return Verification(expected=str(expected), actual_raw=actual_raw,
                            hwnd=hwnd, pid=pid, document=document, row=row, field=field, checkpoint=checkpoint,
                            actual_normalized=actual_normalized, rule=rule, status=status,
                            reason=field + ': ' + reason, evidence=readings, evaluated_at=now)

    values = []
    for reading in readings:
        if not reading.usable:
            # Unsupported UIA is recorded, not a second vote. Ambiguity is blocking.
            if reading.reason.startswith('unavailable:'):
                continue
            actual_raw = reading.actual_raw
            return result('unverified', reading.reason or 'Nguon doc native khong hop le')
        if ((reading.hwnd, reading.pid, reading.document, reading.row, reading.field,
             reading.checkpoint, reading.run_token, reading.input_epoch)
                != (hwnd, pid, document, row, field, checkpoint, run_token, input_epoch)):
            return result('unverified', 'Sai cua so, chung tu, dong, truong hoac checkpoint')
        if (not reading.frame_id or reading.observed_at.tzinfo is None
                or reading.observed_at < not_before or reading.observed_at > now
                or (now - reading.observed_at).total_seconds() > max_age):
            return result('unverified', 'Bang chung cu hoac thieu thoi diem quan sat')
        if reading.actual_raw is None:
            continue
        if re.fullmatch(r'\{?[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\}?',
                        reading.actual_raw.strip(), re.I):
            continue
        actual_raw = reading.actual_raw
        try:
            value = normalized(reading.actual_raw, rule, source=reading.source)
        except (ValueError, NeedsAttention):
            actual_raw = reading.actual_raw
            return result('unverified', 'Khong doc duoc dung dinh dang; khong sua chu/so')
        values.append((reading, value))
    if not values:
        return result('unverified', 'Chua doc duoc gia tri hien tai')
    actual_raw, actual_normalized = values[0][0].actual_raw, values[0][1]
    if rule == 'nonempty' and len({text_key(reading.actual_raw) for reading, _ in values}) != 1:
        return result('unverified', 'Hai lan doc native mau thuan; dung de nguoi dung kiem tra')
    if len({value for _, value in values}) != 1:
        return result('unverified', 'Hai lan doc native mau thuan; dung de nguoi dung kiem tra')
    if not actual_raw.strip() and rule != 'empty':
        return result('unverified', 'Gia tri hien tai rong')
    if rule in ('numeric', 'count_read'):
        return result('matched', 'Da doc so Decimal hop le')
    try:
        target = normalized(expected, rule)
    except (ValueError, NeedsAttention):
        return result('unverified', 'Gia tri mong doi khong dung quy tac')
    if rule.startswith('price_for:'):
        try:
            quantity = decimal_vi(rule.partition(':')[2])
        except NeedsAttention:
            return result('unverified', 'So luong doi chieu don gia khong hop le')
        if quantity <= 0:
            return result('unverified', 'So luong doi chieu don gia phai duong')
        equal = amount_matches(quantity, Decimal(actual_normalized), Decimal(target))
    else:
        equal = (Decimal(actual_normalized) == Decimal(target)) if rule == 'decimal' else actual_normalized == target
    return result('matched' if equal else 'mismatched',
                  'Da doc gia tri phu hop' if equal else 'Gia tri doc khong khop mong doi')
