"""Shared text, numeric parsing and cancellation helpers."""
from decimal import Decimal, InvalidOperation
import re
import unicodedata


class NeedsAttention(RuntimeError):
    pass


class Cancelled(NeedsAttention):
    pass


def normalize(value):
    value = unicodedata.normalize('NFD', str(value).lower().replace('đ', 'd'))
    return re.sub(r'[^a-z0-9]', '', ''.join(c for c in value if unicodedata.category(c) != 'Mn'))


def decimal_vi(value):
    """MISA's displayed Vietnamese format, never a float or a guessed locale."""
    raw = str(value).strip().replace('\u00a0', '')
    if len(raw) > 64 or not re.fullmatch(r'-?(?:[0-9]+|[0-9]{1,3}(?:\.[0-9]{3})+)(?:,[0-9]+)?', raw):
        raise NeedsAttention('Không đọc được số theo định dạng MISA: ' + raw)
    try:
        return Decimal(raw.replace('.', '').replace(',', '.'))
    except InvalidOperation as error:
        raise NeedsAttention('Số MISA không hợp lệ: ' + raw) from error


def scalar_text(value):
    text = str(value)
    if len(text) > 32768 or any(unicodedata.category(c) in ('Cc', 'Cs') for c in text):
        raise NeedsAttention('Giá trị ô chứa ký tự điều khiển hoặc vượt giới hạn nhập liệu.')
    return text
