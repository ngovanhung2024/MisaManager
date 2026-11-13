# MISA Excel Manager (MisaManager)

Tool Windows (WPF, .NET 10) nhập hóa đơn bán hàng từ Excel vào **MISA SME 2023 R20** bằng UI Automation:
đọc file Excel → kiểm tra dữ liệu → tự mở chứng từ bán hàng trong MISA, điền khách hàng/ngày/hóa đơn/dòng hàng/thuế →
đọc lại toàn bộ từ MISA để đối chiếu → **Cất** khi khớp, **Bỏ** khi lệch. Mọi bước đều có checkpoint, nên nếu bị gián đoạn
thì không nhập trùng. Gặp **lỗi** ở một hóa đơn thì tool không hiện popup: lỗi được lưu ở tab **Hiệu năng** (nút **Copy lỗi**), chứng từ dở
được **Bỏ** (không Cất, đã chứng minh chưa lưu) rồi chạy hóa đơn kế tiếp; hóa đơn lỗi không tự nhập lại trong lượt đó. Nếu
không Bỏ được (ví dụ MISA đang mở hộp thoại lạ, hoặc Cất chưa rõ) thì tool dừng cả lượt, chứng từ để nguyên để kiểm tra.

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
.github/workflows/         ci.yml (test thủ công) và release.yml (mỗi merge vào master, chỉ build MSI)
```

Luồng một hóa đơn (`src/MisaManager.Core/Workflow.cs`): `Ready → PreflightPassed → SalesSurface → VoucherCreated →
CustomerVerified → DatesVerified → InvoiceVerified → ItemsVerified → TotalsVerified → AwaitingSave → Saved/Discarded`.
Mỗi bước gửi input rồi đọc lại từ MISA; input không xác nhận được sẽ chuyển sang `InputUncertain`/`SaveUncertain` và
không bao giờ được gửi lại tự động.

Bằng chứng đã Cất: nút **Bỏ ghi** bật, nút **Cất** bị khóa, hoặc MISA tự đóng chứng từ. Chưa thấy bằng chứng trong 15 giây
thì tool xác minh lại một lần (chỉ đọc chứng từ đang mở rồi danh sách chứng từ, bấm Đóng khi đã chứng minh đã Cất), không
bấm Cất lần hai; vẫn không chứng minh được mới để `SaveUncertain`.

## Cài đặt & cập nhật (không cần script)

1. Mở GitHub → **Releases** của repo, tải `MisaManager-<version>.msi` bản mới nhất và nhấp đúp để cài (cài cho tài khoản
   Windows hiện tại, không cần quyền admin). Windows có thể cảnh báo vì file chưa ký số: **More info → Run anyway**.
2. Mở app từ **Start menu** hoặc biểu tượng **MISA Excel Manager** trên Desktop.
3. Repo đang private: lần đầu vào **••• → Cài đặt cập nhật…** và dán GitHub token (xem mục Phát hành). Từ đó mỗi lần mở
   app sẽ tự hỏi khi có bản mới → bấm cài → app tự đóng, cài và mở lại.

Mỗi lần merge PR vào `master`, GitHub Actions build MSI rồi tự tạo bản phát hành mới; app tự tìm thấy và người dùng chỉ
cần bấm cập nhật. Dữ liệu (lịch sử chống nhập trùng, danh mục mã hàng/khách hàng, cài đặt) nằm ở
`%LOCALAPPDATA%\MisaManager` và được giữ nguyên qua mọi lần cập nhật.

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

CI (`.github/workflows/ci.yml`) chỉ chạy khi bấm **Actions → CI → Run workflow**. PR và merge không tự chạy test;
`release.yml` không gọi CI và không chờ kết quả test. Test được chạy riêng trên local khi lập trình viên kiểm tra mã.

## Phát hành & tự cập nhật

1. Mỗi merge vào `master` (trừ thay đổi chỉ ở `*.md`) chạy `release.yml`: chỉ có job `release`, chạy
   `scripts/release.ps1 -Stage -Installer` (build self-contained, health-check, MSI, ký số nếu có secret) rồi tạo GitHub
   Release dạng draft gồm `MisaManager-<version>.msi`, `SHA256SUMS.txt`, `release-manifest.json` và công bố khi đủ file.
   Workflow không chạy unit test hoặc integration test và không chờ CI.
   PR vào `master` (cùng repo) chạy sẵn job này cho kết quả merge (squash), không phát hành, lưu artifact `prebuilt`.
   Khi merge, `release.yml` chỉ công bố MSI đó nếu cây mã (`HEAD^{tree}`), version và trạng thái ký trùng khớp; PR build
   còn đang chạy thì chờ tối đa 8 phút; mọi trường hợp khác build đầy đủ như cũ. Tag trỏ vào commit master; mã commit
   ghi trong app là commit merge tạm của PR (cùng cây mã).
2. Version là `<major>.<minor>.<số commit của master>`; `major.minor` lấy từ `Directory.Build.props`. Chạy tay
   **Actions → Release → Run workflow** để phát hành lại hoặc với version cụ thể (không chạy test).
   `scripts/publish-local.ps1 -Publish` (Windows có desktop) vẫn dùng được khi GitHub Actions không chạy được; không chạy
   nó cùng lúc với workflow cho cùng một version.
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
nhiều máy vẫn nhận đúng. Trước mỗi lượt nhập, app tự kiểm tra selector của các màn hình đang mở (menu không còn mục
kiểm tra tay); selector không khớp thì dừng và hiện popup lỗi kèm chi tiết.
Element không nằm trong `scope` của màn hình (ví dụ thanh tab phân hệ `module_tab` của R20 nằm phía trên vùng Bán hàng)
được tìm tiếp trong toàn cửa sổ MISA.

Khi MISA đổi phiên bản:
1. Mở MISA mới đến màn hình Bán hàng rồi bấm **Thêm vào MISA**: bước kiểm tra giao diện báo selector `Missing`/`Ambiguous`
   trong popup lỗi; **Copy lỗi** lấy chi tiết (kèm danh sách AutomationId đang hiển thị của màn hình đó).
2. Sửa ID/caption trong profile (không cần sửa C#). Nếu đổi hành vi (thứ tự bước, hộp thoại mới) thì tăng `revision`.
3. Cập nhật app giả lập nếu giao diện thay đổi cấu trúc (control đọc tên từ profile nên đổi ID là tự khớp), chạy test, phát hành.
4. `versionTitle` phải khớp đầu tiêu đề cửa sổ MISA (`MISA SME 2023 R20 Enterprise - …`).

## Công ty (Lá Việt, Lộc Thủy…)

Ô **Công ty** ở góc trên chọn công ty đang nhập liệu. Mỗi công ty có thư mục riêng `%LOCALAPPDATA%\MisaManager\companies\<id>`
(danh mục mã hàng, khách hàng/nhà cung cấp, nhóm VTHH, tồn đầu kỳ, hóa đơn đã ghi sổ, danh sách hóa đơn đã nạp, chế độ
Bán/Mua) và cài đặt riêng (MST, quy tắc chỉ nhập xăng dầu) trong `companies.json`. Đổi công ty thì tool mở lại để nạp dữ
liệu của công ty đó. Lần đầu, Lá Việt và Lộc Thủy cùng được chép từ dữ liệu cũ (file gốc giữ nguyên), nên nạp lại danh mục
cho từng công ty; công ty thêm sau bắt đầu trống. Lịch sử chống nhập trùng dùng chung. Hóa đơn có MST người mua (mua vào)
hoặc người bán (bán ra) là công ty khác với công ty đang chọn dừng trước khi nhập với lỗi `COMPANY_MISMATCH`.

## Mã khách hàng / nhà cung cấp

Khi nhiều mã trong danh sách có cùng một MST (ví dụ THANH LƯƠNG và TL), hóa đơn dừng trước khi nhập (`SUPPLIER_AMBIGUOUS`
khi mua, `CUSTOMER_AMBIGUOUS` khi bán) cho đến khi chọn mã ở **••• → Chọn mã khách hàng/nhà cung cấp trùng MST…**. Lựa chọn
được giữ khi nạp lại danh sách; mã đã chọn không còn trong danh sách thì phải chọn lại.

Danh mục mã hàng ưu tiên mã từ file Excel đã nạp hơn mã phát sinh còn giữ trong database cục bộ. Khi đã có bằng chứng
nguồn từ file Excel, dòng không có mã chỉ được tra bằng các mã hiện có trong lần xuất đó; mã cache cũ không được tự chọn
vì MISA có thể đã đổi tên hoặc cấp lại mã. Mã do tool tạo chỉ được tái sử dụng sau khi lần Cất đã xác nhận và ghi bằng chứng.
Tên hàng trong
hóa đơn và danh mục được so cùng quy tắc: bỏ dấu câu, khoảng trắng, chữ "bao" trong quy cách dạng `(bao 25kg)`,
tag nhà phân phối đứng riêng (`(ĐN)`, `ĐN` cuối tên), chữ "bón" của "phân bón"; "khuyến mãi" = "khuyến mại";
vẫn giữ khối lượng và đặc tính hàng nên 10kg/25kg, HC-15/HC-16 hoặc `(ĐN - chai tím …)` không bị ghép chung. Database cũ tự đọc lại file
danh mục gốc một lần để ghi nguồn mã; nếu file mất thì cần nạp lại. Mã đã tạo và bằng chứng Cất chưa rõ vẫn được giữ.
Mọi lần nạp file (hóa đơn, mã hàng, nhóm VTHH, khách hàng, nhà cung cấp, hóa đơn đã ghi sổ) xóa dữ liệu cũ cùng loại rồi
thay bằng toàn bộ dữ liệu trong file, không chèn thêm; mã hàng/khách hàng tool tạo trước đó cũng bị bỏ nên hãy nạp file xuất
mới nhất từ MISA. Chỉ mã hàng đang chờ xác nhận Cất bắt đầu sau thời điểm xuất file được giữ lại để không tạo trùng.
Khi danh mục có nhiều mã cùng ĐVT trùng tên hoặc gần trùng theo quy tắc trên (`Phân kali` = HH_03/HH_50,
`FUJI ONE 40EC 100CC PET` / `FUJI ONE 40EC-100CC PET`), dòng không ghi mã dùng **mã số nhỏ nhất** (HH_03, HH_120).
**••• → Chọn mã dùng khi trùng tên**: mỗi nhóm chọn mã khác nếu cần (ví dụ FUJI ONE → HH_203). Lựa chọn lưu trong
`item-catalog.json` (`preferredCodes`), giữ khi nạp lại hoặc xóa danh mục; chọn hai mã trong cùng nhóm thì hóa đơn có dòng
dùng tên đó bị **Tạm hoãn** ở tab Hóa đơn (không tick được, lý do hiện khi chọn hóa đơn) cho đến khi chỉ giữ một mã.
File hóa đơn có thể thêm cột **Mã hàng** (không bắt buộc): dòng có mã dùng thẳng mã đó, không tra theo tên — cách xử lý
tên trùng nhiều mã (`ITEM_AMBIGUOUS`) hoặc khác ĐVT (`ITEM_UNIT_CONFLICT`).
Luồng nhập không mở F3/kính lúp; không tìm thấy hàng trong danh mục thì mở F9 để thêm, mã/tên readback lệch thì dừng.

**••• → Nạp mã khách hàng…** / **Nạp mã nhà cung cấp…** nạp file danh sách xuất từ MISA (cột Mã, Tên, Mã số thuế; bỏ
dòng "Ngừng theo dõi" và dòng "Số dòng = N"). Lưu tại `%LOCALAPPDATA%\MisaManager\party-catalog.json`; mỗi lần nạp thay
toàn bộ danh sách cùng loại. Khi Excel không có mã khách hàng, tool tìm mã theo thứ tự: trùng tên → trùng mã số thuế →
tên gần đúng (bỏ phần trong ngoặc, viết đầy đủ CTY/CP/TNHH/MTV…), khách hàng trước nhà cung cấp. Mọi so sánh dùng
`TextKey` (cách bỏ dấu cũ/mới, chữ Đ giả, khoảng trắng, ký tự ẩn, hoa/thường). Bán cho người tiêu dùng luôn dùng `NTD`;
mã trong Excel luôn được ưu tiên. Trùng nhiều mã → hóa đơn dừng **trước khi mở chứng từ** (`CUSTOMER_AMBIGUOUS`).

**Tạo khách hàng mới:** đã nạp danh mục khách hàng mà vẫn không tìm được mã thì tool tự tạo khách hàng trong MISA: ở bước
khách hàng của chứng từ, tool nhấn F9 trên ô Khách hàng (tương đương nút **+**) để mở hộp **Thêm Khách hàng**, chọn
**1. Tổ chức** hoặc **2. Cá nhân** (cá nhân = người mua không mang dấu hiệu tổ chức, xem `BUYER_TAX_OPTIONAL` bên dưới), điền
Mã, Tên/Họ và tên, Địa chỉ (cột Địa chỉ người mua) và MST (cột MST người mua), đọc lại toàn bộ rồi bấm **Cất** của hộp. MISA
đưa khách hàng mới vào chứng từ (nếu không, tool gõ mã), sau đó luồng chạy tiếp như bình thường. Quy tắc mã theo danh sách
khách hàng của kế toán (không bao giờ dùng MST): tổ chức dùng **phần tên riêng** viết hoa có dấu, bỏ loại hình và ngành nghề ở
đầu tên (`CÔNG TY TNHH MỘT THÀNH VIÊN LINH NGỌC` → `LINH NGỌC`, `TRUNG TÂM DỊCH VỤ SỰ NGHIỆP CÔNG XÃ PHÚ LỘC` → `PHÚ LỘC`);
phần còn lại dài hơn 3 từ hoặc 20 ký tự thì dùng chữ cái đầu của phần đó. Cá nhân dùng **chữ cái đầu** của tên không dấu
(`Hoàng Trọng Tiếp` → `HTT`). Mã đã có trong danh mục thì thêm số (`HTT2`, `PHÚ LỘC 2`). MST 12 số của cá nhân
là số CCCD nên được điền vào ô **Số CMND**, không vào ô Mã số thuế. Mã được ghi vào checkpoint trước khi bấm Cất; vì mã cố
định theo người mua, nếu kết quả Cất không rõ thì lần chạy sau MISA báo trùng mã (`CUSTOMER_CODE_TAKEN`) thay vì tạo thêm
khách hàng. Khách hàng đã tạo được lưu trong `party-catalog.json` (mục `created`) để các hóa đơn sau của cùng người mua dùng
lại mã, đến khi file danh sách khách hàng nạp lại có mã đó. Chưa nạp danh mục khách hàng thì tool không tạo (mọi người mua
sẽ bị coi là mới) và hóa đơn dừng trước khi mở chứng từ (`CUSTOMER_CODE_MISSING`, **Chưa hoàn thành**). Ô trong hộp Thêm
Khách hàng được tìm theo **nhãn** bên trái. Hai lựa chọn 1. Tổ chức/2. Cá nhân của R20 không phải RadioButton trong UIA nên
được tìm theo chữ; layout đúng được xác nhận bằng nhãn ô tên đang hiện (`Tên (*)` / `Họ và tên (*)`), layout mặc định
(Tổ chức) thì không bấm gì. Lỗi xảy ra trước khi bấm Cất của hộp thì tool bấm **Hủy bỏ** để đóng hộp rồi dừng (chứng từ vẫn mở).
Ô được tìm theo nhãn bên trái (`"label": "Mã (*)"` trong `screens.customer_new` của profile) vì chưa có
AutomationId; khi biết AutomationId thật, thêm `automationId` vào profile là được ưu tiên.
Tên người mua lấy từ cột **DN mua** (đơn vị mua) của file hóa đơn nếu ô có dữ liệu; khi đó **Người mua** chỉ là người liên hệ.
DN mua trống thì dùng Người mua; file chỉ có cột DN mua cũng nhập được. Người mua "Khách hàng không cung cấp thông tin"
không cần MST và nhập vào khách hàng **Khách lẻ** của danh mục khách hàng (như các chứng từ đã ghi sổ); danh mục không có
Khách lẻ → `CUSTOMER_CODE_MISSING`. Người mua cá nhân không có MST là hợp lệ (thông tin `BUYER_TAX_OPTIONAL`), mã khách
hàng tìm theo tên trong danh mục. Chỉ người mua là tổ chức (loại Organization hoặc tên có Công ty/CTY/Hợp tác xã/HTX/Doanh
nghiệp/Chi nhánh/Trung tâm/Vườn quốc gia/UBND/Ngân hàng…) thiếu MST mới bị chặn (`BUYER_TAX_STATUS`).
Dòng không có số hóa đơn (hóa đơn chưa được cấp số) bị bỏ qua, chỉ báo một thông tin `INVOICE_NUMBER_MISSING_SKIPPED`.
Hóa đơn có dòng số lượng hoặc thành tiền âm (điều chỉnh/trả hàng) bị bỏ cả hóa đơn, báo thông tin `INVOICE_NEGATIVE_SKIPPED`.
Mã số thuế người mua được đối chiếu ở bước đối chiếu trước khi Cất (tab Hóa đơn). Nếu bản R20 không hiện ô MST khớp
selector của profile, tool lấy MST của đúng mã khách hàng đã chọn trong danh mục khách hàng làm bằng chứng (nguồn
`PartyCatalog:<mã>`); MST trong danh mục khác Excel hoặc chưa nạp danh mục → không Cất.

## Nhóm vật tư, hàng hóa & quy tắc mã hàng mới

**••• → Nạp nhóm vật tư, hàng hóa…** nạp file danh sách nhóm VTHH xuất từ MISA (cột Mã nhóm, Tên nhóm; bỏ dòng "Ngừng
theo dõi" và "Số dòng = N"), lưu tại `%LOCALAPPDATA%\MisaManager\item-groups.json`. Mã hàng mới tool tạo (F9) bắt đầu
bằng mã nhóm + `_` và đánh số riêng theo từng nhóm: `HH_337`, `DV_01`, `NVL_01`. Nhóm đoán theo tên/ĐVT: ĐVT Lần/Giờ/
Ngày/Phút/Tháng/Chuyến/Ca/Công hoặc tên bắt đầu bằng Dịch vụ/Chi phí/Phí/Lệ phí/Cước/Vận chuyển/Thuê/Sửa chữa… → `DV`;
tên bắt đầu bằng Nguyên (vật) liệu/Vật liệu → `NVL`; còn lại → `HH`. Nhóm đoán ra không có trong file thì dùng `HH`;
chưa nạp file nhóm thì mọi mã mới vẫn là `HH_n` như cũ. Dòng chi tiết dưới danh mục mã hàng hiện tên hàng sẽ tạo mới kèm
tiền tố (ví dụ `Phí vận chuyển → DV_`) để phát hiện sai nhóm trước khi chạy. Tool chỉ đặt mã, chưa chọn ô **Nhóm VTHH**
trong hộp thoại thêm mới của MISA.

**••• → Kiểm tra danh mục mã hàng** (tự hiện sau khi nạp mã hàng/nhóm nếu có vấn đề) báo trước: mã không bắt đầu bằng
mã nhóm nào (khoảng trắng trước `_` như `HH _CAI` vẫn tính là nhóm HH, không cần sửa), và tên trùng/gần trùng nhiều mã
cùng ĐVT (tool dùng mã số nhỏ nhất, trừ khi đã chọn mã khác).

## Cân đối tồn kho bán ra

Mục tiêu: sau khi nhập hết bán ra, **không mã hàng nào âm kho** ở bất kỳ ngày nào. Thứ tự dùng:

1. **Mua hàng trước**: nhập hết hóa đơn mua vào (tool tự tạo mã cho hàng chưa có), xuất lại danh mục vật tư từ MISA và
   **••• → Nạp mã hàng…** (danh mục có cột Đơn vị chuyển đổi, Tỷ lệ chuyển đổi, Phép tính).
2. **••• → Nạp tồn kho đầu kỳ…** (một lần): báo cáo **Tổng hợp tồn kho** xuất từ MISA từ ngày đầu kỳ (ví dụ 01/01/2026); tool lấy cột
   Đầu kỳ (Số lượng) theo mã và ngày "Từ ngày", lưu tại `opening-stock.json`.
3. Ở **Bán hàng**, mở Excel bán ra như bình thường: tool **tự cân đối** (không cần chọn menu) với hóa đơn mua vào của phiên Mua
   hàng gần nhất, ghi cạnh file bán ra `<tên>_can_doi_kho.xlsx` (thêm cột **Mã hàng**, các cột "… trên hóa đơn" giữ giá trị
   gốc khi phải đổi) và `<tên>_bao_cao_ton_kho.xlsx` (tồn theo mã, dòng đổi mã, nhóm cùng mặt hàng, cảnh báo), rồi nạp luôn
   file đã cân đối vào danh sách. Dòng trạng thái báo số mã còn âm; rê chuột lên đó để xem tóm tắt. Thiếu danh mục, tồn đầu kỳ
   hoặc hóa đơn mua vào thì tool nạp file gốc và ghi rõ lý do chưa cân đối.

Cách chọn mã cho từng dòng bán (theo ngày; cùng ngày thì hàng mua vào tính trước; không tách dòng):

- **Cùng mặt hàng** = tên giống nhau khi bỏ chữ "Phân (bón)" đầu tên, ghi chú ngắn trong ngoặc không có số (`(ĐN)`,
  `(Nifit)`, `(ĐN Nicotex)`, `(BM-BV)`), đuôi ngắn (`-TN`, `PET-M`) và, với hàng tính theo kg/tấn/gói có ghi kg, cỡ bao
  (`(Bao 25kg)` = `(Bao 50kg)`). Ghi chú dài (`(ĐN - chai tím - nhãn tím - nước trong)`), số khác (100ml/450ml) hay ĐVT
  đếm khác (Chai/Thùng) là hàng khác.
- **Mã còn tồn đầu kỳ dùng trước cho đến hết** (mã đúng tên của dòng trước, rồi mã nhỏ nhất), sau đó mới đến mã mới (mã của
  dòng, rồi mã nhỏ nhất). Dòng sang mã có tồn đủ cả dòng đầu tiên.
- Hết cả nhóm thì mới dùng mã **tên gần giống** (cùng các con số, lệch 1–2 chữ: `Newcat`/`Newcate`, `Fuamy`/`Fu-army`,
  `PET`/`PEPA`). Vẫn không đủ thì dùng hàng mua sau ngày bán (**âm tạm thời**), hoặc để ở mã của dòng và báo **thiếu tồn**.
- **Quy đổi ĐVT**: kg ↔ tấn, gói/bao ghi kg trong tên (`Newcate 2.5WP - 1kg` theo Gói = 1 kg); ĐVT chuyển đổi đã khai trong
  MISA thì MISA tự quy đổi theo tỷ lệ của nó (tỷ lệ kg/tấn khác 1000 bị báo để sửa trong MISA). Dòng chuyển sang mã khác
  ĐVT được ghi lại ĐVT, số lượng, đơn giá theo mã đó (thành tiền giữ nguyên); Kg sang mã Tấn giữ Kg vì tool tự quy đổi.
- Dòng đổi sang mã có tên khác hẳn được ghi tên của mã đó (MISA vẫn hiện tên theo danh mục); dòng đã có mã trong Excel giữ
  nguyên mã.
- Hóa đơn đã ghi sổ trong MISA vẫn được tính vào cân đối theo mã tool chọn (tool không biết MISA đã dùng mã nào), nên cân
  đối trước khi nhập bán ra. File bán ra có ô công thức/ô lỗi thì không cân đối được: xuất giá trị tĩnh trước.

## Hóa đơn đã ghi sổ

File hóa đơn bán ra có thể là **zip các hóa đơn HTML tải từ cổng thuế** (hoặc một file HTML) thay cho Excel cũ: tool đọc
từng trang, bỏ file trùng/đã hủy, ghi ra `<tên zip>_hoa_don_ban_ra.xlsx` cạnh file zip theo đúng cột của Excel "Danh sách
hóa đơn" cũ (người mua/DN mua, MST hoặc CCCD người mua, địa chỉ, dòng hàng, thuế chia theo dòng như Mua hàng), sắp xếp từ
đầu năm, rồi nhập như Excel.

**Mở Excel** (hoặc **••• → Mở Excel hóa đơn…**) hỏi hai file liên tiếp: file hóa đơn, rồi file danh sách chứng từ bán
hàng xuất từ MISA (`Ban_hang.xlsx`: cột Số hóa đơn, Ký hiệu HĐ, Số chứng từ; bỏ dòng "Số dòng = N"). Bấm Hủy ở file thứ hai
thì dùng danh sách đã nạp lần trước. Tab Hóa đơn chỉ hiện hóa đơn **chưa có trong MISA**: hóa đơn đã ghi sổ bị ẩn cùng lỗi
dữ liệu của nó (kể cả khi thiếu/sai số liệu), dòng trạng thái báo số hóa đơn đã bỏ. Hóa đơn đã ghi sổ vẫn bị **Thêm vào
MISA** bỏ qua nếu còn trong hàng đợi. Ghép theo số hóa đơn (bỏ số 0
đầu) và ký hiệu (bỏ chữ số mẫu số đầu, `1C26MLV` = `C26MLV`); một bên không có ký hiệu thì chỉ cần trùng số. Lưu tại
`%LOCALAPPDATA%\MisaManager\posted-invoices.json`; mỗi lần nạp thay toàn bộ danh sách cũ, **Xóa tất cả dữ liệu Excel đã
nạp** xóa danh sách này. Lịch sử nhập của tool không bị ảnh hưởng.

## Mua hàng (hóa đơn mua vào)

Nút **Bán hàng / Mua hàng** ở đầu cửa sổ chọn chế độ (nhớ qua lần mở sau; mỗi chế độ có danh sách hóa đơn, danh sách đã
ghi sổ `posted-purchases.json` và phiên nhập `import-purchase` riêng). Ở **Mua hàng**, **••• → Mở hóa đơn mua vào** hỏi:

1. File zip các hóa đơn HTML tải từ cổng thuế (hoặc file Excel đã chuyển trước đó). Tool đọc từng trang, bỏ file trùng/đã
   hủy, ghi ra `<tên zip>_hoa_don_mua_vao.xlsx` cạnh file zip, **sắp xếp từ đầu năm đến cuối năm**. Thuế chỉ có theo từng
   thuế suất nên được chia cho các dòng (làm tròn đồng, phần lệch dồn vào dòng lớn nhất). Dòng chiết khấu thương mại/điều
   chỉnh giảm ghi số âm nên hóa đơn đó bị bỏ như hóa đơn âm.
2. File `Mua_hang_hoa__dich_vu.xlsx` xuất từ MISA: ghép theo số hóa đơn **và tên nhà cung cấp** (nhà cung cấp khác nhau có
   thể trùng số).

Chỉ **hàng hóa** được nhập. Hóa đơn dịch vụ/chi phí (phí, cước, vé, xăng dầu, điện nước, ăn uống, điện thoại/máy tính,
phần mềm, chữ ký số, thanh toán theo hợp đồng…, hoặc dòng không có ĐVT và số lượng) hiện ở tab **Dịch vụ** kèm lý do; hóa
đơn có cả hàng hóa lẫn một phần nhỏ dịch vụ cũng không nhập (nhập tay). Sửa cột **Phân loại** trong file Excel thành
`Hàng hóa`/`Dịch vụ` rồi mở lại file nếu tool nhận nhầm.

Chứng từ: **Thêm** trên tab Mua hàng hóa, dịch vụ (Mua hàng trong nước nhập kho chưa thanh toán) → ô Nhà cung cấp gõ
**MST** rồi Enter → ngày hạch toán/chứng từ/hóa đơn → tab Hóa đơn điền số hóa đơn, Ký hiệu HĐ và Mẫu số HĐ → mã hàng như
bán hàng (thiếu thì F9 tạo mã, kho ngầm định KHH) → Kho, TK Kho, TK công nợ để MISA tự điền theo mã hàng (tool không gõ
và không đọc các cột này) → tiền thuế từng dòng đúng nguồn → đối chiếu → Cất. Đã nạp danh sách nhà cung cấp mà thiếu MST thì
dừng trước khi nhập. Selector màn hình mua hàng (`purchase_list`, `purchase_voucher` trong profile) theo quy ước ID của bán
hàng; nếu **Kiểm tra giao diện** báo lệch, **Copy lỗi** gửi lại để cập nhật profile.

**Công ty chỉ nhập xăng dầu** (cài ở **••• → Cài đặt công ty đang chọn…**, mặc định công ty Lộc Thủy MST 3301661802;
`CompanyProfile.Fuel` trong `src/MisaManager.Core/CompanyProfiles.cs`): khi công ty đó đang được chọn, chỉ hóa đơn toàn xăng
dầu có người mua là công ty đó được nhập, mọi hóa đơn khác (bia, sửa chữa, điện…) và
hóa đơn có cả dầu diesel lẫn xăng/nhớt vào tab **Dịch vụ**.
- Dầu diesel (Dầu Điêzen/Diesel/DO…) → **1. Mua hàng trong nước nhập kho**, Kho **CTY**, TK kho **152** (tool sửa nếu MISA
  đề xuất khác).
- Xăng, nhớt, dầu nhờn/thủy lực → chọn **2. Mua hàng trong nước không qua kho** (ô cạnh tiêu đề "Chứng từ mua hàng", tab
  "Chứng từ ghi nợ"), TK chi phí **154**. Khi Cất, hộp "TK 154 chi tiết theo <Khoản mục chi phí>… Bạn có muốn ghi sổ
  không?" được bấm **Yes**.
- Mã hàng mới (F9) dùng kho ngầm định CTY. Tên xăng dầu so khớp cả cách viết khác: "Xăng RON 95 Mức 3" = "Xăng Ron 95-III",
  "Dầu DO/diezel" = "Dầu Điêzen" (khác Mức là mặt hàng khác).
- Hóa đơn mua vào thuế suất KKKNT/KCT (tiền thuế 0) được nhập; hóa đơn bán ra vẫn chặn.

## Đối chiếu hóa đơn

Nút **Đối chiếu hóa đơn** đọc chứng từ bán hàng **đang mở trong MISA** (không cần tool vừa nhập, không cần nạp Excel):
tab Phiếu thu/Hóa đơn, toàn bộ dòng của tab Hàng tiền và Thuế, tổng tiền, rồi mở cửa sổ đối chiếu. Chỉ chuyển tab,
không gõ/Cất/Bỏ gì. Hóa đơn Excel dùng để so là hóa đơn **có cùng số hóa đơn với MISA** (bỏ số 0 đầu; trùng số thì xét ký
hiệu), không phải hóa đơn đang chọn trong danh sách; tool tự chọn hóa đơn đó trong danh sách. Excel không có hóa đơn đó
(hoặc chưa nạp Excel) thì cửa sổ vẫn hiện mọi trường đọc từ MISA, cột Excel để trống. Dòng hàng ghép theo mã hàng (Excel
hoặc danh mục mã hàng), rồi theo tên đã chuẩn hóa, rồi theo vị trí khi số dòng bằng nhau; dòng MISA không có trong Excel
hiện là "Dòng MISA n". Đang có phiên nhập dở của chính hóa đơn đó thì dùng đối chiếu theo checkpoint như trước.
Cửa sổ đối chiếu nằm ở mép đối diện của màn hình (tool bên phải → đối chiếu bên trái), cùng chiều ngang và chiều cao
với tool, để không che chứng từ MISA (nút Đóng/Cất) ở giữa. Thu nhỏ tool thì cửa sổ đối chiếu ẩn theo.

## Chạy hóa đơn & lần chạy dở

**Thêm vào MISA** chỉ chạy các hóa đơn đã tick; chưa tick hóa đơn nào thì không chạy. Sau khi **Dừng**, bấm Thêm bắt đầu
một phiên mới, không tiếp tục checkpoint cũ. Hồ sơ đã Cất và bằng chứng thao tác chưa rõ được giữ để chống nhập trùng. Khi chạy lại
chính hóa đơn còn kết quả chưa rõ, tool chỉ đọc toàn bộ danh sách trong đúng phiên MISA đã nhập sau khi chứng từ được
đóng: có số chứng từ + số hóa đơn → bỏ qua, không nhập lại; không có → cho phép chạy từ đầu. Hóa đơn khác dùng guard
riêng của phiên mới; checkpoint cũ không chặn. Nếu vẫn có chứng từ đang mở thì guard báo cần xử lý editor đó.

**Đánh dấu đã lưu (nhập tay):** đã tự nhập một hóa đơn vào MISA thì bấm chuột phải vào hóa đơn đó trong danh sách và chọn
**Đánh dấu đã lưu (đã nhập tay trong MISA)**; có thể tick nhiều hóa đơn rồi chọn "Đánh dấu đã lưu cho … hóa đơn đang tick".
Tool xóa checkpoint và lịch sử chạy cũ của hóa đơn (chưa hoàn thành, chưa rõ cần xác minh, lỗi…), ghi một bản ghi Saved
(`MANUAL_SAVED`) hiện là **Đã lưu (nhập tay)** và không bao giờ nhập lại hóa đơn này. Tool không kiểm tra lại trong MISA và
không hoàn tác được; không dùng được khi đang chạy Thêm vào MISA. Hóa đơn đã lưu/đã có trong MISA không cần đánh dấu.

**Dừng ngay** không hủy giữa chừng: tool làm xong thao tác hiện tại (hóa đơn đang nhập vẫn được đối chiếu rồi Cất/Bỏ như
bình thường, nên không để chứng từ dở và không báo lỗi), dừng trước hóa đơn kế tiếp, bỏ liên kết checkpoint và đồng hồ
hiệu năng dừng theo. Các chức năng khác không còn dùng workflow đã dừng. **Thêm vào MISA** chạy một phiên mới;
**Tiếp tục** chỉ chạy phần danh sách đã tick còn lại, không replay checkpoint; thời gian hiệu năng cộng dồn các đoạn đã chạy.
Sau mỗi hóa đơn và khi kết thúc, nếu cửa sổ MISA bị thu nhỏ thì tool mở lại (giữ kích thước phóng to); tool luôn nằm trên
cùng ở mép phải nên MISA không che tool.

Danh sách hóa đơn đã nhập từ Excel (kèm lỗi nguồn và các ô đã tick) được lưu ở `%LOCALAPPDATA%\MisaManager\import` sau mỗi
lần nhập và mỗi lần tick thay đổi (kể cả khi tool tự bỏ tick hóa đơn đã Cất). Tắt/mở lại hoặc cập nhật app thì danh sách
hiện lại ngay, không cần mở lại Excel. File Excel gốc vẫn phải còn nguyên: trước khi nhập tool kiểm tra SHA-256, file đã
sửa thì phải mở lại Excel.

Dòng Excel ĐVT kg mà mã hàng trong MISA chỉ có ĐVT Tấn: nếu danh mục mã hàng (xuất có cột "Đơn vị chuyển đổi") cho thấy mã
đó không có kg, tool đổi thẳng sang Tấn (số lượng / 1000) mà không mở danh sách ĐVT để dò (tiết kiệm 2–3 giây/dòng); bằng
chứng `ItemCatalog:<mã>:[các ĐVT]` được lưu vào checkpoint. Danh mục không đủ thông tin thì vẫn mở danh sách ĐVT như cũ.

## Chạy trên VPS (Remote Desktop)

Tool gửi phím/chuột thật vào MISA, nên phiên Windows phải có màn hình nhập liệu. Đóng Remote Desktop bằng nút X (phiên
"Disconnected"), khóa màn hình, hoặc thu nhỏ cửa sổ Remote Desktop đều làm Windows từ chối đưa MISA lên trước và từ chối
SendInput. Khi đó tool báo `SESSION_NOT_INTERACTIVE`, **không gửi input nào** và tự chờ; màn hình hoạt động lại thì chạy
tiếp đúng bước đang dở (bấm Dừng ngay để thôi chờ). Chỉ chờ khi bước đó chưa gửi giá trị nào vào MISA; input có thể đã gửi
thì vẫn dừng ở InputUncertain/SaveUncertain như cũ.

Thu nhỏ cửa sổ Remote Desktop làm client gửi "suppress output": Windows trên VPS vẫn báo phiên Active nhưng bỏ qua
SendInput và không cho đổi foreground. Không có API phát hiện trực tiếp; trong phiên Remote Desktop, kích hoạt MISA thất bại
liên tục (10 lần) được coi là trường hợp này. Cách ẩn VPS mà tool vẫn chạy (menu **••• → VPS / Remote Desktop**):
- **Tự giữ MISA chạy khi đóng Remote Desktop** (bật mặc định, lưu trong `auto-create.txt` là `console=on|off`):
  có hiệu lực ngay khi mở tool (`DesktopSession.StartGuard` trong `App.OnStartup`), không chờ bấm Thêm vào MISA. Khi
  Remote Desktop bị đóng (nút X, mất mạng; phiên "Disconnected"), tool lập tức chạy `tscon <session> /dest:console` (app
  chạy quyền Administrator nên không hỏi UAC; 1 lần mỗi lần ngắt, và bước đang chờ thử lại tối đa 1 lần/20 giây) rồi trả
  lại độ phân giải lúc còn kết nối. Tool **không bao giờ** tự chạy tscon khi Remote Desktop còn kết nối (kể cả đang thu
  nhỏ hay khóa màn hình), vì tscon ngắt kết nối của người dùng ("another connection was made to the remote computer").
- Thu nhỏ Remote Desktop: phía VPS không có cách nào giữ input mà không ngắt kết nối; tool chờ (không gửi input) cho đến
  khi mở lại, trừ khi máy kết nối đã chạy lệnh dưới đây.
- **Ngắt Remote Desktop ngay, giữ MISA chạy**: như trên nhưng làm ngay, thay cho nút X.
- **Copy lệnh cho máy tính của bạn**: `reg add` DWORD `RemoteDesktop_SuppressWhenMinimized = 2` vào
  `HKCU\Software\Microsoft\Terminal Server Client` (và nhánh `Wow6432Node`) trên **máy dùng để kết nối**, rồi kết nối lại:
  thu nhỏ Remote Desktop không còn làm mất input và không bị ngắt kết nối (mstsc và app dùng ActiveX của nó).
- Trong lúc chạy, tool giữ màn hình VPS không tắt/ngủ (`SetThreadExecutionState`); nếu VPS có chính sách tự khóa màn hình
  (Group Policy "Machine inactivity limit"), hãy tắt nó.

## Báo cáo lỗi

Popup lỗi hiển thị báo cáo chẩn đoán theo từng mục: mã lỗi, checkpoint sau và lúc lỗi, bước/dòng hàng, chuỗi exception,
file:dòng trong stack trace, input disposition và guard không replay, runtime/profile/window, nguồn mã hàng, các mã ứng viên,
giá trị MISA đọc lại, đối chiếu lệch, timeline và cách xử lý. Nút **Copy lỗi** copy toàn bộ prompt cho AI; nút **Xuất báo cáo**
tạo ZIP có `summary.json`, `diagnostic.txt`, snapshot UI trước khi Bỏ, ảnh màn hình, kiểm tra selector và log. Lỗi không còn
hiện dưới nút **Thêm vào MISA**. Tab **Hồ sơ** lưu cùng chẩn đoán của từng lần xử lý, kèm nút **Copy lỗi** để xem lại sau.
Menu ••• không còn mục **Xuất báo cáo lỗi**, **Kiểm tra giao diện MISA** và **Trang tính & cột dữ liệu** (bảng chọn trang
tính/cột vẫn tự mở khi file có nhiều trang tính hoặc thiếu cột). Nút **Xuất báo cáo** trong popup gọi
`FailureReports` để tạo
`%LOCALAPPDATA%\MisaManager\logs\reports\misa-report-*.zip` gồm: `README-AI.md` (hướng dẫn cho AI), `summary.json`
(phiên bản, mã lỗi + exception, bước lỗi, checkpoint, dữ liệu hóa đơn mong đợi, giá trị đọc từ MISA, kết quả đối chiếu,
timeline), `ui-at-failure.json` + `screen-at-failure.png` (chụp **ngay lúc lỗi**), trạng thái
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
