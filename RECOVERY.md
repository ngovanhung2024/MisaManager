# Recovery native workflow

## Nguyen tac

Recovery giu lease cua workflow Them; MCP gui lenh qua control plane va app la
noi thuc thi UIA/Win32. Codex co the sua source trong `actual` hoac `codex_test`.
Runtime chi dung UIA3/Win32 native, khong co image-text fallback.

## Che do

- `actual`: MCP co the observe/control workflow hien tai qua session cua app.
- `codex_test`: MCP authenticated claim van duoc ho tro cho phien tach biet.

## Vong doi

1. `RecoveryController` giu lock va snapshot state hien tai.
2. Moi poll doc native UIA3/Win32 moi; state cu, anh cu va XML cu chi la lich su.
3. Loi ghi incident; MCP co the tiep tuc dieu khien neu nguoi dung yeu cau.
4. Repair source bat dau bang `begin_repair(incident_id=...)`.
5. Sau syntax/hash/package checks, `complete_repair(...)` ghi handoff; MCP co the
   tiep tuc observe/control, reload va chay workflow trong cung conversation.
6. App reload source va doc lai native state khi duoc yeu cau.

Trong `codex_test`/Develop, `submit_live_test_handoff` giu nguyen live-test
session, control token, invoice va `TabLedger`. Sau hot reload, workflow tiep tuc
tu checkpoint loi tren cua so MISA dang mo; Codex tiep tuc poll trong conversation
hien tai, khong can dong tab hoac goi lai `start_live_test`.

## Fresh native state

Workflow target tu dong van dung native frame moi. Direct target co the la semantic
hoac toa do cu va khong bat buoc foreground/checkpoint/identity validation; ket qua
native sau lenh phai duoc doc lai neu can xac nhan.

## Safety

Lenh save/discard, sua XML va thay doi truong duoc gui khi nguoi dung yeu cau
trong session hien tai. Control plane cho phep retry sau input khong chac.

## Verification

Syntax, hash, package va build checks la gate truoc live control check. Ket qua
live duoc doc tu observation/readback cua app.
