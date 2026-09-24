# MISA XML Manager: native workflow

Cap nhat 2026-09-24. Runtime dung UIA3 va Win32 native; MCP co control plane
file-backed duoc xu ly tren Tk/main thread cua app.

## Pham vi

Manager doc XML, mo luong Them, nhap thong tin chung va hang hoa, sau do doc lai
control MISA de doi chieu voi XML. UIA3 la nguon semantic chinh; Win32 la nguon
process/window/focus/text va input. Anh man hinh chi la evidence, khong phai nguon
chu.

## Quan sat va dieu khien

- MCP co the quan sat va gui lenh semantic/toa do qua control plane trong
  `actual` hoac `codex_test`; server MCP khong tu goi native input.
- App dang ky `MisaInvoiceTool/misa-control/session.json`, doc request trong
  `requests/` va ghi response atomically.
- `click`, `type`, `press_key`, `scroll`, `select_tab`, `execute_workflow` va
  `save_or_discard` duoc thuc thi tren main thread cua app.
- `edit_xml` thay mot doan van ban exact trong XML da nap, ghi atomic va doc lai invoice.

## Direct control

- Workflow tu dong van giu native readback khi co the.
- Lenh direct MCP co the dung semantic target hoac toa do, tu activate cua so MISA,
  bo qua checkpoint/foreground/identity guard va tra ve native readback sau lenh.
- Khi cua so MISA dang thu nho, direct control tu dong dung UIA background cho
  target semantic (`Invoke`, `Selection`, `Toggle`, `ExpandCollapse`, `Scroll`,
  `Value`) ma khong khoi phuc cua so. Target theo toa do va `press_key` van can
  foreground va se duoc tu choi khi cua so dang thu nho.
- `Ctrl+A`, Unicode text, `Enter`, `Tab`, save/discard va retry input duoc phep
  theo yeu cau hien tai; `InputUncertain` khong khoa session.
- Anh chi la chan doan; UIA3/Win32 readback la ket qua uu tien khi app cung cap.
- Manager Tk hien panel theo mac dinh trong ca `actual` va `codex_test`;
  `--headless` moi an panel. Moi vong test khong can `--show-ui`.
- Direct semantic control dat `allow_minimized=true` mac dinh va dung UIA3
  background cho cua so bi che/thu nho. UIA target duoc tim lai theo HWND/PID,
  AutomationId, ten, pattern va parent token; khong phu thuoc toa do man hinh.
- Neu nguoi dung di chuyen cua so trong workflow guarded, frame tu dong chup lai
  theo origin moi va doc lai tab/target. Toa do/SendInput van yeu cau foreground.
- `inspect_live_misa` tao mot quan sat moi kem PNG (tuy chon) va hai cay doc thuan
  cung HWND/PID/frame: UIA3 (token, AutomationId, pattern, parent, bounds) va
  Win32 `EnumChildWindows` (HWND, class, control id, text, parent, bounds).
  `maximum_nodes` gioi han 1..8000; lenh khong activate, click, type hay save.

## Checkpoint

`restart.ready` -> `add.open` -> `add.customer` -> `add.dates` -> `add.invoice` ->
`add.items.query` -> `add.items.select` -> `add.items.enter` -> `add.totals`.

Workflow tu dong van dung checkpoint/readback; control plane co the dieu huong
va thay doi truong khi nguoi dung yeu cau.

## Readback va input

- Scalar field uu tien UIA3 ValuePattern/TextPattern.
- Grid uu tien DataItem/GridItem va editor UIA3; keyboard navigation duoc guard
  bang row token, row bounds va current tab.
- Neu UIA3 khong co pattern, Win32 tim child edit/combo duy nhat trong bounds native.
- Click, scroll va key co the di qua native workflow guard hoac direct-control
  fallback tuy theo loai lenh.
- Sau input, workflow doc lai tren frame native moi; input khong duoc coi la thanh
  cong chi vi Windows da nhan phim.
- Metrics `driver_metrics` ghi `uia_query_cache_hits/misses` va
  `uia_bind_reuses` de live-test cho thay so
  vong UIA da tiet kiem va van theo doi fallback.

## Stop, incident va reload

Stop huy cancellation event, giu cua so va du lieu chua luu. Incident, XML, anh va
log lich su khong duoc xem la UI hien tai. Repair handoff khong tu dong thao tac
MISA; app validate source revision, diff, compile va reload.

## Kiem tra source

Source checks van la py_compile/hash/package/build; user co the yeu cau live
control check qua MCP sau khi source da compile.

