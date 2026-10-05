# AGENTS.md – hướng dẫn cho AI/lập trình viên

Đọc README.md trước (kiến trúc, build, test, phát hành, profile). File này là quy tắc làm việc.

## Lệnh chuẩn
- Build: `dotnet build MisaManager.sln -c Release` (Linux: thêm `-p:EnableWindowsTargeting=true`, chỉ biên dịch).
- Unit test: `dotnet test tests/MisaManager.Core.Tests` (chạy được trên Linux với `-p:EnableWindowsTargeting=true`).
- Integration test (Windows có desktop): `$env:MISAMANAGER_UI_TESTS="1"; dotnet test tests/MisaManager.Integration.Tests`.
- Release local: `.\scripts\release.ps1 -Stage -Installer -Version x.y.z`.

## Quy tắc
- `TreatWarningsAsErrors=true`, nullable bật, version package tập trung trong `Directory.Packages.props`, có lock file.
  Không thêm package khi chưa cần; không nâng version không liên quan.
- **Không test trên MISA thật.** Mọi thay đổi automation phải có kịch bản trên MISA giả lập (`tests/MisaManager.FakeMisa`)
  và test trong `tests/MisaManager.Integration.Tests`. App giả lập đặt tên control theo `profiles/*.json`.
- **Tương thích state:** `ContractJson` dùng `UnmappedMemberHandling.Disallow`. Không xóa/đổi tên field hoặc giá trị enum đã
  được lưu (`WorkflowContext`, `WorkflowSnapshot`, `InvoiceDocument`, `WorkflowExecutionMode.LiveTest`…), nếu không lịch sử
  cũ không đọc được và tool mất khả năng chống nhập trùng. Thêm field mới phải có giá trị mặc định.
- **An toàn nhập liệu:** không bao giờ tự gửi lại input khi trạng thái là `InputUncertain`/`SaveUncertain`; mọi input phải
  đọc lại (readback) trước khi sang bước sau. Giữ nguyên các guard `GuardWorkflow`, `ThrowIfBlockingDialog`, kiểm tra
  identity cửa sổ.
- **Selector:** không hard-code AutomationId/caption trong C#. Thêm vào profile (`screens.<màn hình>.elements|tabs|grids`)
  rồi đọc qua `ScreenProfile`/`WorkflowProfile`/`MisaScreen`. Selector phải khớp đúng 1 control đang hiển thị.
- Chuỗi giao diện bằng tiếng Việt; comment code ngắn, chỉ giải thích ràng buộc không hiển nhiên.
- Không commit secret. Token cập nhật lưu bằng DPAPI (`UpdateSettings`); chứng chỉ ký số chỉ qua GitHub secrets.

## Bản đồ mã nguồn
| Việc | Nơi sửa |
|---|---|
| Luồng nhập chứng từ bán hàng | `src/MisaManager.Automation/Workflows/SalesVoucher/NativeWorkflowGateway.cs` |
| Thao tác control dùng chung (field, tab, lệnh, grid) | `src/MisaManager.Automation/Input/NativeGuardedOperations.cs`, `Input/NativeTableTraversal.cs` |
| API màn hình tái sử dụng | `src/MisaManager.Automation/Screens/MisaScreen.cs` |
| Kiểm tra selector trước khi chạy | `src/MisaManager.Automation/Input/NativeScreenCheck.cs`, `Screens/ScreenCheck.cs` |
| State machine, checkpoint, chống trùng | `src/MisaManager.Core/Workflow.cs` |
| Kiểm tra dữ liệu hóa đơn | `src/MisaManager.Core/Validation.cs` |
| Đọc Excel | `src/MisaManager.Import/InvoiceWorkbookImporter.cs` |
| Mã khách hàng/nhà cung cấp (danh mục, tìm theo tên) | `src/MisaManager.Core/PartyCatalog.cs`, `src/MisaManager.Import/PartyCatalogWorkbook.cs` |
| Đối chiếu chứng từ đang mở (ngoài phiên nhập) | `src/MisaManager.Automation/Workflows/SalesVoucher/OpenVoucherComparison.cs` |
| Báo cáo lỗi (zip cho AI) | `src/MisaManager.App/Diagnostics/FailureReports.cs` |
| Tự cập nhật từ GitHub Releases | `src/MisaManager.App/Updates/` |
| CI / phát hành | `.github/workflows/`, `scripts/release.ps1`, `installer/` |

## Cập nhật profile khi MISA đổi phiên bản
1. Lấy `screen-check.json` từ **Xuất báo cáo lỗi** (hoặc **Kiểm tra giao diện MISA**) trên máy có MISA mới; mục
   `visibleAutomationIds` liệt kê `AutomationId | ControlType | Name` đang hiển thị trên màn hình bị lỗi.
2. Sửa `profiles/misa-sme2023-r20.json`. Đổi `versionTitle` nếu tiêu đề cửa sổ đổi; tăng `revision` khi hành vi đổi.
3. Nếu cấu trúc giao diện đổi (tab/hộp thoại mới), cập nhật app giả lập theo, thêm test, rồi phát hành bằng tag.

## Xử lý báo cáo lỗi người dùng gửi
`summary.json` → `error.code`, `error.failedStep`, `steps` (timeline), `expected` vs `nativeValues`, `comparison`.
`ui-at-failure.json` → hộp thoại đang mở (`dialogs`), control đang focus. `screen-at-failure.png` là ảnh ngay lúc lỗi.
Tái hiện bằng một kịch bản trên MISA giả lập trước khi sửa.
