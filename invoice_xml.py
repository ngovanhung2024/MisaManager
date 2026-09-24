"""Read one Vietnamese invoice as data; preserve accounting values."""
from dataclasses import dataclass
from datetime import date, datetime
from decimal import Decimal, localcontext
from pathlib import Path
import re
import zipfile
from xml.etree import ElementTree as StdET
from defusedxml import ElementTree as ET


class InvoiceError(ValueError):
    pass


def sum_amounts(values):
    # XML scalars are bounded to 48 characters; products and accumulated totals
    # must not inherit Decimal's default 28-digit rounding.
    with localcontext() as context:
        context.prec = 128
        return sum(values, Decimal(0))


def amount_matches(quantity, price, amount, tolerance=Decimal(1)):
    with localcontext() as context:
        context.prec = 128
        return abs(quantity * price - amount) <= tolerance


@dataclass(frozen=True)
class Party:
    name: str
    tax_code: str
    address: str
    code: str = ''
    buyer_person: str = ''


@dataclass(frozen=True)
class InvoiceLine:
    kind: str
    code: str
    name: str
    unit: str
    quantity: Decimal | None
    unit_price: Decimal | None
    amount: Decimal | None
    discount: Decimal | None
    tax_rate: str
    tax: Decimal | None
    discount_rate: Decimal | None = None
    tax_source: str = ''


@dataclass(frozen=True)
class TaxSubtotal:
    rate: str
    amount: Decimal | None
    tax: Decimal | None


@dataclass(frozen=True)
class Invoice:
    source: str
    number: str
    series: str
    template: str
    issued: str
    invoice_type: str
    currency: str
    payment_method: str
    seller: Party
    buyer: Party
    lines: tuple[InvoiceLine, ...]
    notes: tuple[str, ...]
    total: Decimal
    before_tax: Decimal | None
    tax: Decimal | None
    warnings: tuple[str, ...]
    exchange_rate: Decimal | None = None
    total_discount: Decimal | None = None
    tax_subtotals: tuple[TaxSubtotal, ...] = ()
    tax_authority_code: str = ''
    related_invoice: bool = False


# Excel exports are not guaranteed to use a fixed column order or spelling.
# Keep aliases here so the UI can accept the common Vietnamese accounting names.
EXCEL_COLUMN_ALIASES = {
    'number': ('số hóa đơn', 'so hoa don', 'số hđ', 'so hd', 'invoice no', 'invoice number'),
    'issued': ('ngày hóa đơn', 'ngay hoa don', 'ngày lập', 'ngay lap', 'invoice date', 'date'),
    'buyer': ('người mua', 'nguoi mua', 'tên người mua', 'ten nguoi mua', 'customer', 'buyer'),
    'tax_code': ('mst người mua', 'mst nguoi mua', 'mã số thuế người mua', 'ma so thue nguoi mua', 'buyer tax code'),
    'name': ('tên hàng hóa', 'ten hang hoa', 'tên hàng', 'ten hang', 'hàng hóa', 'hang hoa', 'item name', 'description'),
    'unit': ('đơn vị tính', 'don vi tinh', 'dvt', 'unit', 'uom'),
    'unit_price': ('đơn giá', 'don gia', 'unit price', 'price'),
    'quantity': ('số lượng', 'so luong', 'quantity', 'qty'),
    'amount': ('tổng tiền chưa thuế', 'tong tien chua thue', 'thành tiền', 'thanh tien', 'amount before tax', 'subtotal'),
    'tax_rate': ('thuế hàng hóa', 'thue hang hoa', 'thuế suất', 'thue suat', 'tax rate', 'vat rate'),
    'tax': ('tiền thuế chi tiết', 'tien thue chi tiet', 'tiền thuế', 'tien thue', 'tax amount', 'vat amount'),
    'total': ('tổng tiền có thuế', 'tong tien co thue', 'tổng tiền thanh toán', 'tong tien thanh toan', 'total amount', 'grand total'),
}


def _excel_key(value):
    import unicodedata
    value = unicodedata.normalize('NFD', str(value or '')).casefold()
    value = ''.join(char for char in value if unicodedata.category(char) != 'Mn')
    return ' '.join(value.replace('_', ' ').replace('-', ' ').split())


def _excel_number(value, label='Excel'):
    if value is None or str(value).strip() == '':
        return None
    if isinstance(value, (int, float, Decimal)):
        return Decimal(str(value))
    raw = str(value).strip().replace('\u00a0', '').replace(' ', '')
    # Vietnamese exports commonly use dots for groups and commas for decimals.
    if ',' in raw and '.' in raw:
        raw = raw.replace('.', '').replace(',', '.')
    elif ',' in raw:
        raw = raw.replace(',', '.')
    elif raw.count('.') > 1:
        raw = raw.replace('.', '')
    try:
        return Decimal(raw)
    except Exception as error:
        raise InvoiceError(f'Giá trị số Excel không hợp lệ tại {label}: {value}') from error


def _xlsx_rows(path):
    """Read worksheet cell values without requiring a third-party Excel package."""
    try:
        archive = zipfile.ZipFile(path)
        shared = []
        if 'xl/sharedStrings.xml' in archive.namelist():
            root = StdET.fromstring(archive.read('xl/sharedStrings.xml'))
            shared = [''.join(node.itertext()) for node in root]
        workbook = StdET.fromstring(archive.read('xl/workbook.xml'))
        rels = StdET.fromstring(archive.read('xl/_rels/workbook.xml.rels'))
        rel_map = {item.attrib.get('Id'): item.attrib.get('Target') for item in rels}
        sheet = next(iter(workbook.findall('.//{*}sheet')), None)
        if sheet is None:
            raise InvoiceError('Excel không có worksheet.')
        target = rel_map.get(sheet.attrib.get('{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id'))
        if not target:
            raise InvoiceError('Excel không xác định được worksheet.')
        target = target.lstrip('/')
        if not target.startswith('xl/'):
            target = 'xl/' + target
        root = StdET.fromstring(archive.read(target))
        rows = []
        for row in root.findall('.//{*}sheetData/{*}row'):
            values = {}
            for cell in row.findall('{*}c'):
                ref = cell.attrib.get('r', '')
                match = re.match(r'([A-Z]+)', ref)
                if not match:
                    continue
                column = 0
                for char in match[1]:
                    column = column * 26 + ord(char) - 64
                value = cell.find('{*}v')
                text = value.text if value is not None else ''
                if cell.attrib.get('t') == 's' and text.isdigit():
                    text = shared[int(text)] if int(text) < len(shared) else ''
                elif cell.attrib.get('t') == 'inlineStr':
                    text = ''.join(cell.itertext())
                values[column] = text
            if values:
                width = max(values)
                rows.append([values.get(index, '') for index in range(1, width + 1)])
        archive.close()
        return rows
    except InvoiceError:
        raise
    except (OSError, KeyError, ValueError, StdET.ParseError, zipfile.BadZipFile) as error:
        raise InvoiceError('Excel không hợp lệ hoặc không đọc được worksheet.') from error


def read_excel(path):
    """Read an .xlsx export and return one Invoice per distinct invoice number."""
    source = Path(path).resolve()
    rows = _xlsx_rows(source)
    if len(rows) < 2:
        raise InvoiceError('Excel cần có hàng tiêu đề và ít nhất một dòng dữ liệu.')
    headers = [_excel_key(value) for value in rows[0]]
    mapped = {}
    for field, aliases in EXCEL_COLUMN_ALIASES.items():
        choices = {_excel_key(alias) for alias in aliases}
        found = [index for index, header in enumerate(headers) if header in choices]
        if found:
            mapped[field] = found[0]
    missing = [field for field in ('number', 'name', 'unit', 'unit_price', 'quantity', 'amount', 'tax', 'total')
               if field not in mapped]
    if missing:
        raise InvoiceError('Excel thiếu cột bắt buộc: ' + ', '.join(missing))
    grouped = {}
    current_number = ''
    for values in rows[1:]:
        get = lambda field: values[mapped[field]] if field in mapped and mapped[field] < len(values) else ''
        raw_number = str(get('number') or '').strip()
        if raw_number:
            current_number = raw_number
        if not current_number:
            continue
        name = str(get('name') or '').strip()
        if not name:
            continue
        grouped.setdefault(current_number, []).append(values)
    if not grouped:
        raise InvoiceError('Excel không có dòng hàng hóa hợp lệ.')

    result = []
    for number, records in grouped.items():
        lines, buyers, totals = [], [], []
        tax_codes = []
        for values in records:
            def get(field):
                return values[mapped[field]] if field in mapped and mapped[field] < len(values) else ''
            buyer = str(get('buyer') or '').strip()
            if buyer:
                buyers.append(buyer)
            code = str(get('tax_code') or '').strip()
            if code:
                tax_codes.append(code)
            tax_rate = str(get('tax_rate') or '').strip()
            tax_value = _excel_number(get('tax'), 'tiền thuế chi tiết')
            lines.append(InvoiceLine('1', '', str(get('name')).strip(), str(get('unit') or '').strip(),
                                     _excel_number(get('quantity'), 'số lượng'),
                                     _excel_number(get('unit_price'), 'đơn giá'),
                                     _excel_number(get('amount'), 'tổng tiền chưa thuế'), None,
                                     tax_rate, tax_value))
            total = _excel_number(get('total'), 'tổng tiền có thuế')
            if total is not None:
                totals.append(total)
        before_tax = sum_amounts([line.amount for line in lines if line.amount is not None])
        tax = sum_amounts([line.tax for line in lines if line.tax is not None])
        if not totals:
            total = before_tax + tax
        elif len(set(totals)) == 1:
            # Some exports repeat the invoice total on every detail row.
            total = totals[0]
        else:
            # Other exports provide the line total including tax.
            total = sum_amounts(totals)
        buyer_name = buyers[0] if buyers else 'BÁN CHO NGƯỜI TIÊU DÙNG'
        issued = (str(records[0][mapped['issued']] or '').strip()
                  if 'issued' in mapped and mapped['issued'] < len(records[0]) else '')
        if issued:
            try:
                if re.fullmatch(r'\d{2}/\d{2}/\d{4}', issued):
                    issued = datetime.strptime(issued, '%d/%m/%Y').date().isoformat()
            except ValueError:
                issued = ''
        source_id = f'{source}#invoice:{number}'
        result.append(Invoice(source_id, number, '', '', issued or date.today().isoformat(), 'Hóa đơn bán hàng', 'VND', '',
                              Party('', '', ''), Party(buyer_name, '', ''),
                              tuple(lines), (), total, before_tax, tax, (), None, None, (), '', False))
    return result

def text_at(node, path, default=''):
    values = node.findall(path)
    if len(values) > 1:
        raise InvoiceError(f'XML có trường trùng lặp: {path}.')
    if values and len(values[0]):
        raise InvoiceError(f'Trường dữ liệu XML phải là văn bản đơn: {path}.')
    value = values[0].text if values else None
    return value.strip() if value else default


def number_at(node, path, required=False):
    raw = text_at(node, path)
    if not raw:
        if required:
            raise InvoiceError(f'XML thiếu số tiền/số lượng: {path}.')
        return None
    return parse_number(raw, path)


def parse_number(raw, label):
    if len(raw) > 48 or not re.fullmatch(r'-?[0-9]+(?:\.[0-9]+)?', raw):
        raise InvoiceError(f'Giá trị số không hợp lệ tại {label}: {raw[:60]}')
    return Decimal(raw)


def line_tax(node):
    """Read only an explicit TThue, never infer tax from a provider's MThue."""
    direct = number_at(node, 'TThue')
    extra = [item for item in node.findall('TTKhac/TTin')
             if text_at(item, 'TTruong') == 'TThue']
    if len(extra) > 1:
        raise InvoiceError('XML có trường thuế bổ sung TThue trùng lặp.')
    raw = text_at(extra[0], 'DLieu') if extra else ''
    supplemental = parse_number(raw, 'TTKhac/TThue') if raw else None
    if direct is not None and supplemental is not None and direct != supplemental:
        raise InvoiceError('Thuế TThue trực tiếp và thuế bổ sung của dòng hàng không khớp.')
    if direct is not None:
        return direct, 'HHDVu/TThue'
    if supplemental is not None:
        return supplemental, 'HHDVu/TTKhac/TTin[TTruong=TThue]/DLieu'
    return None, ''


def read_invoice(path):
    source = Path(path)
    with source.open('rb') as stream:
        data = stream.read(10 * 1024 * 1024 + 1)
    if len(data) > 10 * 1024 * 1024:
        raise InvoiceError('File XML vượt quá 10 MB.')
    try:
        root = ET.fromstring(data, forbid_dtd=True, forbid_entities=True, forbid_external=True)
    except Exception as error:
        raise InvoiceError('XML không hợp lệ hoặc chứa DTD/thực thể không được hỗ trợ.') from error
    for node in root.iter():
        node.tag = node.tag.rsplit('}', 1)[-1]
    invoices = list(root.iter('DLHDon'))
    if len(invoices) != 1:
        raise InvoiceError('Cần XML chứa đúng một DLHDon (một hóa đơn).')
    doc = invoices[0]
    for path in ('TTChung', 'NDHDon', 'NDHDon/NBan', 'NDHDon/NMua',
                 'NDHDon/DSHHDVu', 'NDHDon/TToan', 'NDHDon/TToan/THTTLTSuat'):
        if len(doc.findall(path)) > 1:
            raise InvoiceError('XML có khối dữ liệu trùng lặp: ' + path)
    number = text_at(doc, 'TTChung/SHDon')
    issued = text_at(doc, 'TTChung/NLap')
    if not re.fullmatch(r'[0-9]{1,32}', number):
        raise InvoiceError('XML thiếu hoặc sai số hóa đơn.')
    try:
        if not re.fullmatch(r'[0-9]{4}-[0-9]{2}-[0-9]{2}', issued):
            raise ValueError('Invalid date format')
        date.fromisoformat(issued)
    except ValueError as error:
        raise InvoiceError('Ngày hóa đơn phải có dạng YYYY-MM-DD.') from error

    def party(tag):
        base = f'NDHDon/{tag}/'
        return Party(*(text_at(doc, base + key) for key in ('Ten', 'MST', 'DChi', 'MKHang', 'HVTNMHang')))

    seller, buyer = party('NBan'), party('NMua')
    if not seller.name:
        raise InvoiceError('XML thiếu tên người bán.')
    lines, notes, warnings = [], [], []
    for node in doc.findall('NDHDon/DSHHDVu/HHDVu'):
        kind = text_at(node, 'TChat', '1')
        name = text_at(node, 'THHDVu')
        if kind == '4':
            notes.append(name)
            continue
        if kind not in ('1', '2', '3'):
            raise InvoiceError(f'Chưa hỗ trợ tính chất dòng hàng {kind}.')
        if not name:
            raise InvoiceError('Có dòng hàng thiếu tên.')
        tax_value, tax_source = line_tax(node)
        lines.append(InvoiceLine(kind, text_at(node, 'MHHDVu'), name, text_at(node, 'DVTinh'),
                                 number_at(node, 'SLuong'), number_at(node, 'DGia'),
                                 number_at(node, 'ThTien'), number_at(node, 'STCKhau'),
                                 text_at(node, 'TSuat'), tax_value,
                                 number_at(node, 'TLCKhau'), tax_source))
    if not lines:
        raise InvoiceError('XML không có dòng hàng hóa/dịch vụ.')
    total = number_at(doc, 'NDHDon/TToan/TgTTTBSo', required=True)
    before_tax = number_at(doc, 'NDHDon/TToan/TgTCThue')
    tax = number_at(doc, 'NDHDon/TToan/TgTThue')
    tax_subtotals = tuple(TaxSubtotal(text_at(item, 'TSuat'), number_at(item, 'ThTien'),
                                     number_at(item, 'TThue'))
                         for item in doc.findall('NDHDon/TToan/THTTLTSuat/LTSuat'))
    if not buyer.tax_code:
        warnings.append('XML không có mã số thuế người mua.')
    if any(x.kind == '3' for x in lines):
        warnings.append('Có dòng chiết khấu; giữ nguyên dữ liệu XML để đối chiếu.')
    if notes:
        warnings.append('Dòng ghi chú được tách riêng, không tính là mặt hàng.')
    if all(x.amount is not None and x.kind in ('1', '2') for x in lines) and before_tax is not None:
        delta = sum_amounts([*(x.amount for x in lines), before_tax.copy_negate()])
        if delta:
            warnings.append(f'Tổng thành tiền các dòng lệch tổng trước thuế {format_money(delta)}. '
                            'Giữ nguyên số liệu và ghi chú, chưa tự phân bổ khoản giảm.')
    warnings.append('Mã hàng/khách hàng trong XML chưa được ghép với danh mục MISA.')
    return Invoice(str(source.resolve()), number, text_at(doc, 'TTChung/KHHDon'),
                   text_at(doc, 'TTChung/KHMSHDon'), issued, text_at(doc, 'TTChung/THDon'),
                   text_at(doc, 'TTChung/DVTTe', 'VND'), text_at(doc, 'TTChung/HTTToan'),
                   seller, buyer, tuple(lines), tuple(notes), total, before_tax, tax, tuple(warnings),
                   number_at(doc, 'TTChung/TGia'), number_at(doc, 'NDHDon/TToan/TTCKTMai'),
                   tax_subtotals, text_at(root, 'MCCQT'),
                   bool(doc.findall('TTHDLQuan') or doc.findall('TTChung/TTHDLQuan')))


def format_money(value):
    if value is None:
        return '—'
    integer, _, fraction = format(value, 'f').partition('.')
    grouped = f'{int(integer):,}'.replace(',', '.')
    if integer == '-0':
        grouped = '-0'
    fraction = fraction.rstrip('0')
    return grouped + (',' + fraction if fraction else '')
