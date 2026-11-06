using System.Text.RegularExpressions;
using System.Windows.Media;
using MisaManager.Core;

namespace MisaManager.App;

public enum ToolStateKind
{
    Connecting, Ready, Disconnected, Entering, Reconciling, Saving, PauseRequested, Paused,
    Saved, Discarded, Existing, Uncertain, NeedsAction, Error
}

/// <summary>What the user should see about the run: short label, one-line detail and a remediation hint.</summary>
public sealed record ToolState(ToolStateKind Kind, string Label, string Detail = "", string Hint = "")
{
    public bool IsBusy => Kind is ToolStateKind.Connecting or ToolStateKind.Entering or ToolStateKind.Reconciling
        or ToolStateKind.Saving or ToolStateKind.PauseRequested;
    public string KindName => Kind.ToString().ToLowerInvariant();

    public static string LabelOf(ToolStateKind kind) => kind switch
    {
        ToolStateKind.Connecting => "Đang kết nối…",
        ToolStateKind.Ready => "Sẵn sàng",
        ToolStateKind.Disconnected => "Mất kết nối MISA",
        ToolStateKind.Entering => "Đang nhập",
        ToolStateKind.Reconciling => "Đang đối chiếu",
        ToolStateKind.Saving => "Đang lưu",
        ToolStateKind.PauseRequested => "Đang tạm dừng…",
        ToolStateKind.Paused => "Đã tạm dừng",
        ToolStateKind.Saved => "Đã lưu",
        ToolStateKind.Discarded => "Đã bỏ",
        ToolStateKind.Existing => RunOutcome.ExistingLabel,
        ToolStateKind.Uncertain => "Chưa rõ",
        ToolStateKind.NeedsAction => "Cần xử lý",
        _ => "Lỗi"
    };
}

public static class StatePalette
{
    private static Brush Make(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private static readonly Dictionary<ToolStateKind, (Brush Fg, Brush Bg)> Colors = new()
    {
        [ToolStateKind.Connecting] = (Make("#2563EB"), Make("#EFF4FF")),
        [ToolStateKind.Entering] = (Make("#2563EB"), Make("#EFF4FF")),
        [ToolStateKind.Reconciling] = (Make("#0E7490"), Make("#ECFEFF")),
        [ToolStateKind.Saving] = (Make("#2563EB"), Make("#EFF4FF")),
        [ToolStateKind.PauseRequested] = (Make("#B45309"), Make("#FEF3C7")),
        [ToolStateKind.Paused] = (Make("#475569"), Make("#F1F5F9")),
        [ToolStateKind.Ready] = (Make("#15803D"), Make("#ECFDF3")),
        [ToolStateKind.Saved] = (Make("#15803D"), Make("#D1FADF")),
        [ToolStateKind.Discarded] = (Make("#BE123C"), Make("#FFE4E6")),
        [ToolStateKind.Existing] = (Make(RunOutcome.ExistingColor), Make("#E3ECF6")),
        [ToolStateKind.Uncertain] = (Make("#6D28D9"), Make("#EDE9FE")),
        [ToolStateKind.NeedsAction] = (Make("#B45309"), Make("#FEF3C7")),
        [ToolStateKind.Disconnected] = (Make("#B91C1C"), Make("#FEE2E2")),
        [ToolStateKind.Error] = (Make("#B91C1C"), Make("#FEE2E2"))
    };

    public static (Brush Fg, Brush Bg) Of(ToolStateKind kind) => Colors[kind];
}

public static partial class ErrorHints
{
    private static readonly Dictionary<string, string> Hints = new(StringComparer.Ordinal)
    {
        ["DIALOG_REQUIRES_ATTENTION"] = "MISA đang hiện hộp thoại cảnh báo. Đọc và đóng hộp thoại đó trong MISA, kiểm tra chứng từ rồi mới chạy lại.",
        ["SCOPE_IDENTITY_CHANGED"] = "Tab hoặc dialog của MISA đã đổi giữa chừng (ví dụ dialog Thêm vật tư mở chậm). Giữ nguyên tab Bán hàng, đóng dialog đang mở rồi chạy lại.",
        ["STALE_NATIVE_FRAME"] = "Màn hình MISA vừa thay đổi nên dữ liệu đọc đã cũ. Chờ MISA ổn định, không thao tác thêm, rồi bấm Đọc lại.",
        ["EDITOR_NOT_FOUND"] = "Không thấy form chứng từ bán hàng. Mở lại chứng từ 'Bán hàng hóa, dịch vụ trong nước' trong MISA rồi chạy lại.",
        ["SALES_SURFACE_MISSING"] = "MISA chưa ở màn hình danh sách chứng từ: chế độ Bán hàng cần Bán hàng > Chứng từ bán hàng, chế độ Mua hàng cần Mua hàng > Mua hàng hóa, dịch vụ. Mở đúng màn hình rồi chạy lại.",
        ["WINDOW_IDENTITY_CHANGED"] = "Phiên MISA đã đóng/mở lại hoặc đổi dữ liệu. Đăng nhập đúng dữ liệu rồi bấm Thêm vào MISA để chạy lại hóa đơn từ đầu.",
        ["ITEM_CATALOG_MISSING"] = "Chưa nạp danh mục mã hàng. Bấm nút Mã hàng và chọn file danh mục vật tư xuất từ MISA.",
        ["ITEM_AMBIGUOUS"] = "Tên hàng trùng nhiều mã trong danh mục. Chọn mã dùng ở ••• → Chọn mã dùng khi trùng tên; hoặc thêm cột 'Mã hàng' vào file Excel hóa đơn, ghi đúng mã cho dòng này rồi nạp lại file; hoặc ngừng theo dõi mã thừa trong MISA rồi xuất lại danh mục.",
        ["ITEM_UNIT_CONFLICT"] = "Tên hàng đã có nhưng khác đơn vị tính. Ghi đúng mã hàng theo đơn vị vào cột 'Mã hàng' của file Excel hóa đơn rồi nạp lại; tool không tự tạo mã mới.",
        ["ITEM_CATALOG_STALE"] = "Mã tra từ danh mục cục bộ đang trỏ tới tên khác trong MISA. Xuất lại danh mục vật tư từ MISA và nạp lại file mã hàng; tool không mở Tìm nhanh/kính lúp để tự đổi mã.",
        ["ITEM_CATALOG_SOURCE_UNAVAILABLE"] = "Database cũ chưa phân biệt mã từ Excel và mã đã tạo. Nạp lại file danh mục mã hàng để xác định nguồn mã trước khi nhập.",
        ["ITEM_CREATE_UNCERTAIN"] = "Chưa rõ mã hàng mới đã được tạo hay chưa. Mở Danh mục vật tư trong MISA để tìm mã vừa tạo, nạp lại danh mục rồi chạy lại; không tạo tay để tránh trùng mã.",
        ["ITEM_UNIT_NOT_REGISTERED"] = "Đơn vị tính chưa có trong MISA. Thêm đơn vị trong Danh mục hoặc sửa đơn vị trong Excel.",
        ["ITEM_STOCK_NOT_FOUND"] = "Không tìm thấy kho mặc định (ví dụ KHH). Kiểm tra mã kho trong MISA có bị đổi tên hoặc xóa không.",
        ["ITEM_TAX_NOT_SET"] = "Thuế suất của vật tư chưa đặt hoặc không hợp lệ. Sửa thuế suất trong Excel (5, 5% hoặc 5,0000 đều được).",
        ["GRID_COMPLETENESS_UNAVAILABLE"] = "Chưa đọc đủ các dòng của bảng hàng tiền (đang cuộn hoặc có dòng ẩn). Đưa bảng về đầu rồi bấm Đọc lại.",
        ["SAVE_UNCERTAIN"] = "Chưa rõ chứng từ đã được Cất hay chưa. KHÔNG nhập tay lại; kiểm tra chứng từ trong MISA, đóng nó rồi bấm Thêm vào MISA: tool tự đọc danh sách MISA để biết đã Cất hay chưa.",
        ["INPUT_UNCERTAIN"] = "Chưa rõ thao tác nhập đã vào MISA hay chưa. Kiểm tra chứng từ đang mở, đóng nó rồi bấm Thêm vào MISA: tool tự đọc danh sách MISA để biết đã Cất hay chưa.",
        ["DETACHED_INVOICE_UNCERTAIN"] = "Chính hóa đơn này còn kết quả chưa rõ trong hồ sơ. Kiểm tra và đóng chứng từ trong đúng phiên MISA đã nhập rồi chạy lại; tool chỉ đọc danh sách để xác nhận, không tự gửi lại input.",
        ["EXISTING_EDITOR"] = "Đang có chứng từ bán hàng mở trong MISA. Kiểm tra, Cất hoặc đóng chứng từ đó rồi bấm Thêm vào MISA.",
        ["RECONCILIATION_FAILED"] = "Không đọc/đối chiếu được dữ liệu MISA. Giữ chứng từ đang mở, bấm Đọc lại; nếu vẫn lỗi hãy kiểm tra tay trên MISA.",
        ["NOT_CONNECTED"] = "Chưa kết nối được MISA. Mở MISA SME 2023 R20 và đăng nhập vào dữ liệu kế toán; tool tự kết nối lại.",
        ["CUSTOMER_CODE_MISSING"] = "Chưa có mã khách hàng. Nạp danh mục khách hàng (menu •••; người mua không có trong danh mục sẽ được tự tạo mới) hoặc ghi mã vào cột Mã khách hàng của Excel, rồi chạy lại.",
        ["CUSTOMER_CODE_TAKEN"] = "MISA báo mã khách hàng tool định tạo đã tồn tại. Bấm Hủy bỏ trên hộp Thêm Khách hàng, xuất lại danh sách khách hàng từ MISA, nạp lại ở menu ••• rồi chạy lại.",
        ["CUSTOMER_SAVE_BLOCKED"] = "MISA cảnh báo khi Cất khách hàng mới. Xử lý cảnh báo / bấm Hủy bỏ trên hộp Thêm Khách hàng rồi chạy lại.",
        ["CUSTOMER_CREATE_READBACK_FAILED"] = "Thông tin trong hộp Thêm Khách hàng không khớp hóa đơn. Bấm Hủy bỏ trên hộp rồi chạy lại.",
        ["NEW_CUSTOMER_DIALOG_MISSING"] = "Không mở được hộp Thêm Khách hàng (F9 / nút + ở ô Khách hàng). Kiểm tra MISA rồi chạy lại.",
        ["CUSTOMER_AMBIGUOUS"] = "Tên khách hàng trùng nhiều mã trong danh mục. Ghi đúng mã vào cột Mã khách hàng của Excel rồi chạy lại.",
        ["DISCARD_UNCERTAIN"] = "Chưa rõ chứng từ đã được Bỏ hay chưa. Kiểm tra chứng từ trong MISA trước khi làm tiếp.",
        ["TEST_DISCARD_UNCERTAIN"] = "Chưa rõ chứng từ thử nghiệm đã được Bỏ hay chưa. Kiểm tra chứng từ trong MISA trước khi làm tiếp.",
        ["LIVE_RUN_CANCELLED"] = "Lượt chạy bị dừng giữa chừng. Kiểm tra chứng từ đang mở trong MISA rồi chạy lại.",
        ["LIVE_RUN_FAILED"] = "Lượt chạy lỗi. Xem chi tiết lỗi, kiểm tra chứng từ đang mở trong MISA rồi chạy lại từ đầu."
    };

    [GeneratedRegex("[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+")]
    private static partial Regex CodePattern();

    public static string? For(string? code)
        => code is not null && Hints.TryGetValue(code, out var hint) ? hint : null;

    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal) { "LIVE_RUN_FAILED", "LIVE_RUN_CANCELLED" };

    /// <summary>Finds the most specific known error code mentioned in the texts (generic run failures last).</summary>
    public static string? FindCode(params string?[] texts)
    {
        string? generic = null;
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (Match match in CodePattern().Matches(text))
            {
                if (!Hints.ContainsKey(match.Value)) continue;
                if (!Generic.Contains(match.Value)) return match.Value;
                generic ??= match.Value;
            }
        }
        return generic;
    }

    public static string ForStatus(WorkflowStatus status) => status switch
    {
        WorkflowStatus.SaveUncertain => Hints["SAVE_UNCERTAIN"],
        WorkflowStatus.InputUncertain => Hints["INPUT_UNCERTAIN"],
        WorkflowStatus.Blocked => "Workflow bị chặn để bảo vệ dữ liệu. Đọc thông báo bên dưới, xử lý trên MISA (đóng chứng từ dở) rồi bấm Thêm vào MISA.",
        WorkflowStatus.Failed => "Lượt chạy lỗi. Xem chi tiết lỗi, kiểm tra chứng từ đang mở trong MISA rồi chạy lại từ đầu.",
        _ => ""
    };
}
