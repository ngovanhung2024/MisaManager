# MISA Excel Manager (MisaManager)

Tool Windows (WPF, .NET 10) nhập hóa đơn bán hàng từ Excel vào **MISA SME 2023 R20** bằng UI Automation:
đọc file Excel → kiểm tra dữ liệu → tự mở chứng từ bán hàng trong MISA, điền khách hàng/ngày/hóa đơn/dòng hàng/thuế →
đọc lại toàn bộ từ MISA để đối chiếu → **Cất** khi khớp, **Bỏ** khi lệch. Mọi bước đều có checkpoint, nên nếu bị gián đoạn
thì không nhập trùng.

## Kiến trúc

```
src/
  MisaManager.Core/        Domain hóa đơn, kiểm tra dữ liệu, so sánh giá trị, state machine (WorkflowRunner), lưu state JSON
  MisaManager.Import/      Đọc .xlsx (OpenXML), map cột tiếng Việt, danh mục mã hàng
  MisaManager.Automation/  Điều khiển MISA qua UIA3 (FlaUI) + Win32 SendInput
    Discovery/             Tìm đúng tiến trình/cửa sổ MISA (MisaWindowDiscovery)
    Uia/                   Hạ tầng: luồng STA riêng (NativeDispatcher), truy vấn có cache, bàn phím/chuột, chờ có timeout
    Input/                 Primitive có kiểm soát: bind scope, đọc/ghi field có readback, chọn tab, bấm lệnh, grid tùy biến,
                           kiểm tra selector (NativeScreenCheck), chụp cây UIA khi lỗi
    Profiles/              profiles/*.json → ScreenProfile (màn hình/tab/element/grid) + WorkflowProfile (view cho luồng bán hàng)
    Screens/               API dùng lại cho mọi màn hình: MisaScreen, MisaGrid, ScreenCheck; Dialogs/ (thêm vật tư, hộp thoại Cất)
    Workflows/SalesVoucher/ Luồng "Chứng từ bán hàng": NativeWorkflowGateway (+ cảnh báo chênh lệch thuế)
  MisaManager.App/         Giao diện WPF; AppServices (composition root), Diagnostics/ (báo cáo lỗi), Updates/ (tự cập nhật)
profiles/misa-sme2023-r20.json  ID control của MISA R20, khai báo theo màn hình
tests/
  MisaManager.Core.Tests/        Unit test (không cần MISA, chạy được cả Linux)
  MisaManager.FakeMisa/          App giả lập MISA R20 (WinForms + cây UIA tùy biến cho grid), đặt tên control theo profile
  MisaManager.Integration.Tests/ Chạy luồng nhập thật (Automation + WorkflowRunner) trên MISA giả lập
installer/                 WiX 4 MSI per-user (%LOCALAPPDATA%\Programs\MisaManager)
scripts/                   release.ps1 (build/publish/MSI/ký số), install/rollback, chứng chỉ ký số
.github/workflows/         ci.yml (mỗi PR), release.yml (tag v*.*.*)
```

Luồng một hóa đơn (`src/MisaManager.Core/Workflow.cs`): `Ready → PreflightPassed → SalesSurface → VoucherCreated →
CustomerVerified → DatesVerified → InvoiceVerified → ItemsVerified → TotalsVerified → AwaitingSave → Saved/Discarded`.
Mỗi bước gửi input rồi đọc lại từ MISA; input không xác nhận được sẽ chuyển sang `InputUncertain`/`SaveUncertain` và
không bao giờ được gửi lại tự động.

## Cài đặt & cập nhật (không cần script)

1. Mở GitHub → **Releases** của repo, tải `MisaManager-<version>.msi` bản mới nhất và nhấp đúp để cài (cài cho tài khoản
   Windows hiện tại, không cần quyền admin). Windows có thể cảnh báo vì file chưa ký số: **More info → Run anyway**.
2. Mở app từ **Start menu** hoặc biểu tượng **MISA Excel Manager** trên Desktop.
3. Repo đang private: lần đầu vào **••• → Cài đặt cập nhật…** và dán GitHub token (xem mục Phát hành). Từ đó mỗi lần mở
   app sẽ tự hỏi khi có bản mới → bấm cài → app tự đóng, cài và mở lại.

Mỗi lần merge PR vào `master`, GitHub Actions tự tạo bản phát hành mới (không cần tag hay chạy script). Muốn thử một PR
trước khi merge: mở tab **Checks** của PR → workflow **Release** → tải artifact `MisaManager-<version>-pr<số>` (file zip
chứa MSI). Bản thử của PR luôn có version thấp hơn bản phát hành sau khi merge, nên app vẫn tự cập nhật lên bản chính
thức. Dữ liệu (lịch sử chống nhập trùng, danh mục mã hàng/khách hàng, cài đặt) nằm ở `%LOCALAPPDATA%\MisaManager` và
được giữ nguyên qua mọi lần cập nhật.

Thư mục `scripts/` chỉ dùng cho CI và lập trình viên; người dùng không cần chạy script nào.

## Build & chạy

Dành cho lập trình viên. Yêu cầu: Windows 10/11 x64, .NET SDK theo `global.json` (10.0.401).

```powershell
dotnet build MisaManager.sln -c Release
dotnet run --project src/MisaManager.App
```

Trên Linux/macOS chỉ biên dịch được (không chạy WPF/UIA): `dotnet build MisaManager.sln -p:EnableWindowsTargeting=true`.

## Test

```powershell
# Unit test – không cần MISA, chạy mọi nơi
dotnet test tests/MisaManager.Core.Tests

# Integration test trên MISA giả lập – cần Windows có desktop (không chạy dưới service/session 0)
$env:MISAMANAGER_UI_TESTS = "1"
dotnet test tests/MisaManager.Integration.Tests
```

Integration test khởi động `MISA.SME.exe` giả lập (tests/MisaManager.FakeMisa), nối đúng stack như app
(`NativeWorkflowGateway` + `WorkflowRunner`) rồi kiểm tra cả kết quả workflow lẫn dữ liệu chứng từ mà app giả lập đã lưu.
Không còn chế độ "live test" trên MISA thật. Khi đổi hành vi automation, hãy thêm kịch bản vào app giả lập trước.

CI (`.github/workflows/ci.yml`) chạy build + unit test + integration test trên `windows-latest` cho mỗi PR.

## Phát hành & tự cập nhật

1. Mỗi push/merge vào `master` (trừ thay đổi chỉ ở `*.md`), `release.yml` tự phát hành bản
   `<major>.<minor>.<số commit của master>`; `major.minor` lấy từ `<Version>` trong `Directory.Build.props` (đổi ở đó để
   lên 0.3, 1.0…). Vẫn có thể phát hành version cụ thể bằng tag `v1.2.3` hoặc chạy tay workflow với ô version.
2. `release.yml` chạy unit test, `scripts/release.ps1 -Stage -Installer` (build self-contained, health-check, MSI có
   shortcut Start menu/Desktop), ký số (nếu có secret), rồi tạo GitHub Release gồm `MisaManager-<version>.msi`,
   `SHA256SUMS.txt`, `release-manifest.json`. Với PR, workflow chỉ build MSI và đính kèm làm artifact.
3. App tự kiểm tra khi mở (tắt được) hoặc qua **••• → Kiểm tra cập nhật…**: tải MSI, kiểm tra SHA-256, đóng app, chạy
   `msiexec /passive` (per-user, không cần quyền admin) rồi mở lại app. App từ chối cập nhật khi đang có hóa đơn dở.

Repo đang **private** nên GitHub API cần token. Chọn một trong hai:
- Mỗi máy: **••• → Cài đặt cập nhật…** → dán fine-grained token (quyền *Contents: Read-only* cho repo này). Token được mã hóa
  bằng DPAPI theo tài khoản Windows; hoặc đặt biến môi trường `MISAMANAGER_GITHUB_TOKEN`.
- Hoặc phát hành lên một repo **public** riêng chỉ chứa Releases và build với `-p:UpdateRepository=owner/repo`
  (hoặc đổi repo trong Cài đặt cập nhật) – khi đó không cần token.

## Ký số

`scripts/release.ps1` ký `MisaManager.*.exe/dll` và MSI khi có chứng chỉ:
- CI: thêm secret `CODE_SIGNING_PFX_BASE64` (PFX dạng base64) và `CODE_SIGNING_PASSWORD`. Không có secret → bản phát hành
  ghi `unsigned_internal`.
- Nội bộ (miễn phí): `scripts/new-signing-certificate.ps1` tạo chứng chỉ tự ký; trên từng máy chạy
  `scripts/trust-signing-certificate.ps1 -Path MisaManager-signing.cer`. Chứng chỉ tự ký **không** qua được SmartScreen trên
  máy lạ; để phân phối rộng cần chứng chỉ OV/EV hoặc Azure Trusted Signing (chỉ cần thay secret, không đổi script).
- Local: `.\scripts\release.ps1 -Stage -Installer -Version 0.3.0 -CertificatePath C:\path\cert.pfx`
  (mật khẩu đọc từ `$env:MISAMANAGER_SIGNING_PASSWORD`) hoặc `-CertificateThumbprint <sha1>` cho chứng chỉ trong store.

## Kiểm tra giao diện MISA & cập nhật profile

Toàn bộ selector nằm trong `profiles/misa-sme2023-r20.json`, chia theo màn hình (`screens`): `scope` (vùng/cửa sổ),
`tabs`, `elements` (`automationId` ưu tiên, `name`, `controlType`, `tab`, `required`) và `grids` (ID + tiêu đề cột).
AutomationId của MISA (WinForms) chính là tên control, nên **không phụ thuộc DPI, độ phân giải hay theme** – chạy trên
nhiều máy vẫn nhận đúng. Trước mỗi lượt nhập, app kiểm tra selector của các màn hình đang mở; có thể chạy tay qua
**••• → Kiểm tra giao diện MISA**.
Element không nằm trong `scope` của màn hình (ví dụ thanh tab phân hệ `module_tab` của R20 nằm phía trên vùng Bán hàng)
được tìm tiếp trong toàn cửa sổ MISA.

Khi MISA đổi phiên bản:
1. Mở MISA mới đến màn hình cần kiểm tra (Bán hàng, mở một chứng từ, mở từng tab), chạy **Kiểm tra giao diện MISA** hoặc
   **Xuất báo cáo lỗi**. `screen-check.json` liệt kê selector `Missing`/`Ambiguous` và danh sách AutomationId đang hiển thị
   (`visibleAutomationIds`) của màn hình đó.
2. Sửa ID/caption trong profile (không cần sửa C#). Nếu đổi hành vi (thứ tự bước, hộp thoại mới) thì tăng `revision`.
3. Cập nhật app giả lập nếu giao diện thay đổi cấu trúc (control đọc tên từ profile nên đổi ID là tự khớp), chạy test, phát hành.
4. `versionTitle` phải khớp đầu tiêu đề cửa sổ MISA (`MISA SME 2023 R20 Enterprise - …`).

## Mã khách hàng / nhà cung cấp

**••• → Thêm mã khách hàng…** / **Thêm mã nhà cung cấp…** nạp file danh sách xuất từ MISA (cột Mã, Tên, Mã số thuế; bỏ
dòng "Ngừng theo dõi" và dòng "Số dòng = N"). Lưu tại `%LOCALAPPDATA%\MisaManager\party-catalog.json`; mỗi lần nạp thay
toàn bộ danh sách cùng loại. Khi Excel không có mã khách hàng, tool tìm mã theo thứ tự: trùng tên → trùng mã số thuế →
tên gần đúng (bỏ phần trong ngoặc, viết đầy đủ CTY/CP/TNHH/MTV…), khách hàng trước nhà cung cấp. Mọi so sánh dùng
`TextKey` (cách bỏ dấu cũ/mới, chữ Đ giả, khoảng trắng, ký tự ẩn, hoa/thường). Bán cho người tiêu dùng luôn dùng `NTD`;
mã trong Excel luôn được ưu tiên. Không tìm được hoặc trùng nhiều mã → hóa đơn dừng **trước khi mở chứng từ**
(`CUSTOMER_CODE_MISSING` / `CUSTOMER_AMBIGUOUS`).

## Đối chiếu hóa đơn

Nút **Đối chiếu hóa đơn** so hóa đơn đang chọn với chứng từ bán hàng **đang mở trong MISA** (không cần tool vừa nhập):
đọc tab Phiếu thu/Hóa đơn, toàn bộ dòng của tab Hàng tiền và Thuế, tổng tiền, rồi mở cửa sổ đối chiếu. Chỉ chuyển tab,
không gõ/Cất/Bỏ gì. Dòng hàng ghép theo mã hàng (Excel hoặc danh mục mã hàng), rồi theo tên đã chuẩn hóa, rồi theo vị
trí khi số dòng bằng nhau. Đang có phiên nhập dở của chính hóa đơn đó thì dùng đối chiếu theo checkpoint như trước.

## Báo cáo lỗi

Khi có lỗi, bấm **Xuất báo cáo lỗi** (trong popup lỗi hoặc menu •••). App tạo
`%LOCALAPPDATA%\MisaManager\logs\reports\misa-report-*.zip` gồm: `README-AI.md` (hướng dẫn cho AI), `summary.json`
(phiên bản, mã lỗi + exception, bước lỗi, checkpoint, dữ liệu hóa đơn mong đợi, giá trị đọc từ MISA, kết quả đối chiếu,
timeline), `ui-at-failure.json` + `screen-at-failure.png` (chụp **ngay lúc lỗi**, trước khi tool Bỏ chứng từ), trạng thái
hiện tại, kết quả kiểm tra selector và log gần nhất. Gửi nguyên file zip cho AI/lập trình viên. Ảnh có thể chứa dữ liệu
kế toán thật – không đăng công khai.
Tên công ty trên tiêu đề cửa sổ MISA (công ty bản quyền/dữ liệu) và dòng "Đang làm việc trên: …" được thay bằng
`[tên công ty]` trong mọi file văn bản của báo cáo; kết quả kiểm tra giao diện chỉ ghi tên sản phẩm (`MISA SME 2023 R20
Enterprise`). Ảnh chụp màn hình không được che.

## Mở rộng sang màn hình/tab khác

Khai báo màn hình mới trong profile rồi dùng `MisaScreen`:

```csharp
var profile = WorkflowProfile.Load();
var list = await MisaScreen.TryOpenAsync(input, window, profile.Screen("sales_list"))
    ?? throw new InvalidOperationException("Hãy mở phân hệ Bán hàng");
await list.ClickAsync("add");
var voucher = await MisaScreen.OpenAsync(input, window, profile.Screen("sales_voucher"));
await voucher.WriteAsync("customer", "KL", commitKey: "Enter");
await voucher.SelectTabAsync("invoice");
var rows = await voucher.Grid("items").ReadRowsAsync(["code", "quantity", "amount"]);
```

Mỗi lệnh tự tìm control theo profile, chặn khi cửa sổ/scope đổi, có hộp thoại lạ chặn, hoặc giá trị đọc lại không khớp.
Luồng nghiệp vụ mới (ví dụ chứng từ mua hàng) đặt trong `src/MisaManager.Automation/Workflows/<Tên>/`.
