# MISA Excel Manager

## Trang thai

Manager doc Excel va nhap du lieu vao MISA bang UIA3 va Win32 native. Cac dong
trung so hoa don duoc gom thanh mot hoa don de doi chieu.
Runtime chi nap backend native, khong co worker GPU va khong doc chu tu anh man hinh.

Luong `actual` la che do mac dinh va nhan lenh MCP qua control plane file-backed.
`codex_test` van danh cho phien tuong thich. MCP server khong goi native input;
app xu ly lenh tren Tk/main thread. Panel manager hien mac dinh trong ca
`actual` va `codex_test`, de nguoi dung tuong tac trong moi vong test.
Chi dung `--headless` khi muon chay an qua MCP.

## Luong Them

1. Nguoi dung chon san trang Ban hang trong MISA va chon file Excel `.xlsx`.
2. Manager doc cua so, HWND/PID, process birth, DPI, client bounds va checkpoint.
3. UIA3 tim control theo AutomationId, control type, pattern va parent token.
4. Win32 doc native text, focus, bounds va gui input khi UIA3 khong cung cap pattern.
5. Moi target phai den tu UIA3 identity hoac Win32 child HWND proof hien tai.
6. Workflow input tu dong van doc lai native. Direct MCP input co the dung semantic
   target/toa do cu, bo qua guard va tra ve frame/readback hien tai sau lenh.

Control plane co cac lenh `observe`, `click`, `type`, `press_key`, `scroll`,
`select_tab`, `execute_workflow` va `save_or_discard` theo yeu cau nguoi dung;
`edit_xml` va retry input; khong can control token trong actual mode.

Live-test co them MCP read-only `inspect_live_misa(session_id, control_token,
include_image=true, maximum_nodes=5000, query='', maximum_results=120)`. Ket qua gom anh PNG hien tai va cay
UIA3/Win32 bounded cua cung cua so, dung de xem HWND, parent, class, text,
AutomationId, pattern, token va bounds ma khong gui input.

Direct control co the dung target semantic UIA cho `click`, `type`, `select_tab`
va cac thao tac `Scroll/Invoke/Selection/Toggle` khi MISA dang bi che hoac thu nho,
khong goi `SendInput`, khong dua cua so len truoc va khong phu thuoc vi tri man hinh.
Target theo toa do va `press_key` van yeu cau cua so foreground. Gui
`capture=true` cho `observe` neu can anh chan doan; mac dinh observe dung UIA nen.

## Cau truc native

- `automation/screen_win32.py`: orchestration UIA3, Win32, map va input guards.
- `automation/uia_bridge.py`, `automation/uia_readback.py`: engine UIA3 semantic.
- `automation/win32.py`: HWND, process, focus, native text, capture va SendInput.
- `automation/control_plane.py`: file-backed MCP requests and main-thread dispatch.
- Guarded workflow target resolution uses current UIA3/Win32 identities; direct
  MCP control also accepts semantic labels and coordinate fallback.
- `automation/add_sale.py`, `automation/sale_items.py`: checkpoint workflow.
- `automation/recovery.py`, `automation/runtime_reload.py`: stop, handoff va reload.
- `src/`: engine C# UIA3/Input/App/Inspector.

Anh man hinh chi la bang chung chan doan lich su; khong phai nguon doc text va
khong duoc dung thay cho UIA3/Win32 hien tai.

## Mo source va build

Shortcut `MISA XML Manager - Source.lnk` chay `app.py` bang Python 3.10.
`requirements.txt` chi chua dependency cua native manager. `build-engine.ps1`
build engine C# UIA3/Win32; `MISA XML Manager.spec` dong goi source snapshot.

Kiem tra source duoc phep:

```powershell
.venv\Scripts\python.exe -m py_compile app.py invoice_xml.py launcher.py source_updates.py incident_mcp.py automation\*.py
```

Live control check duoc thuc hien qua MCP sau khi source checks hoan tat.

## Recovery va handoff

Chi app tao incident va giu workflow lease. Repair incident bat dau bang
`begin_repair`, sau checks co the `complete_repair` roi tiep tuc observe/control,
reload va resume qua MCP. Xem `RECOVERY.md` va `APP_WORKFLOW.md`.

Trong mode Develop, handoff source giu nguyen session Codex va checkpoint MISA.
Sau khi source duoc nap lai, cung conversation tiep tuc poll va chay tu checkpoint
that bai; khong can dong tab Codex hay goi lai workflow tu dau.

## Tai lieu cu

Thu muc `artifacts/` va log trong `%LOCALAPPDATA%` co the con du lieu lich su tu
truoc dot chuyen doi. Chung khong duoc nap vao runtime va khong phai chi dan.


