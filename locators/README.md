# Locator provenance

JSON files are semantic selector candidates extracted from the existing Python
port. The live 2026-09-22 elevated inspection also produced observed controls in
`misa_uia_controls.json`; those observations are authoritative only for the
captured window/form and must still be re-resolved at runtime.

ControlType values are native Windows UIA identifiers (Button 50000, Edit 50004,
DataGrid 50028). The legacy Python synthetic grid used 50025; that value means
Custom in native UIA and must not be copied as a DataGrid selector.

Use Inspector to capture controls/hierarchy and confirm uniqueness in the current
window. Save reviewed candidates explicitly. Fallback success logs a suggestion;
it never rewrites these production files. Do not add a sales voucher Save locator.
`inventory.save` is limited to the new inventory item dialog. `txtJournalMemo` is
read-only regardless of its locator key.
