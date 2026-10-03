using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MisaManager.Core;

namespace MisaManager.App;

public sealed record EntryFieldRow(string Key, string Label, string Expected, string Actual = "",
    bool IsSection = false, bool IsActive = false, bool IsComplete = false, bool IsMismatch = false,
    IReadOnlyList<EntryFieldRow>? Children = null)
{
    public bool IsLineSection => Children is { Count: > 0 };
    public bool HasActual => Actual.Length > 0;
    public bool HasMismatch => MismatchCount > 0;
    public int MismatchCount => Children is null ? (IsMismatch ? 1 : 0) : Children.Count(child => child.IsMismatch);
    public string MismatchBadge => MismatchCount == 0 ? "" : $"{MismatchCount} lệch";
    public bool AllComplete => Children is { Count: > 0 } && Children.All(child => child.IsComplete);

    // Compare Children by content so unchanged rows are not replaced (which regenerates the container and replays its animations).
    public bool Equals(EntryFieldRow? other) => other is not null
        && Key == other.Key && Label == other.Label && Expected == other.Expected && Actual == other.Actual
        && IsSection == other.IsSection && IsActive == other.IsActive && IsComplete == other.IsComplete && IsMismatch == other.IsMismatch
        && (ReferenceEquals(Children, other.Children)
            || (Children is not null && other.Children is not null && Children.SequenceEqual(other.Children)));

    public override int GetHashCode() => HashCode.Combine(Key, Label, Expected, Actual, IsActive, IsComplete, IsMismatch, Children?.Count ?? -1);
}

/// <summary>Run outcome that is neither a save nor a data mismatch: MISA already had this invoice number.</summary>
public static class RunOutcome
{
    public const string ExistingCode = "INVOICE_ALREADY_EXISTS";
    public const string ExistingLabel = "Đã tồn tại trong MISA";
    public const string ExistingColor = "#3F6C9A";

    public static bool IsExisting(WorkflowSnapshot snapshot)
        => snapshot.Status == WorkflowStatus.Discarded && snapshot.LastErrorCode == ExistingCode;
}

public sealed record ComparisonRow(ValueComparison Value, string Label, string SectionName = "", bool Pending = false)
{
    public bool Matched => !Pending && Value.Matched;
    /// <summary>match, mismatch or pending; drives the row styling.</summary>
    public string Kind => Pending ? "pending" : Value.Matched ? "match" : "mismatch";
    public string ResultLabel => Pending ? "…" : Matched ? "Khớp" : "Lệch";
    public string ResultIcon => Pending ? "…" : Matched ? "✓" : "✕";
    public string ResultTip => Pending ? "Chưa đọc từ MISA" : Matched ? "Khớp với Excel" : "Lệch so với Excel";
    public string Section => SectionName.Length > 0 ? SectionName
        : Value.Field.StartsWith("line:", StringComparison.Ordinal) ? Label.Split(" · ")[0]
        : Value.Field is "subtotal" or "tax_total" or "grand_total" or "item_row_count" or "tax_row_count" ? "Tổng cộng"
        : "Thông tin chung";
    /// <summary>Short label inside a grouped section; the full label otherwise (flat lists).</summary>
    public string ShortLabel => SectionName.Length > 0 && Label.Contains(" · ", StringComparison.Ordinal)
        ? Label[(Label.IndexOf(" · ", StringComparison.Ordinal) + 3)..] : Label;
    public string ExpectedValue => Value.Expected.Length == 0 ? "—" : Value.Expected;
    public string ActualValue => Pending ? "Chưa đọc" : Value.Actual is null ? "Không đọc được" : Value.Actual.Length == 0 ? "—" : Value.Actual;
    public string ExpectedText => "Nguồn: " + (Value.Expected.Length == 0 ? "(trống)" : Value.Expected);
    public string ActualText => "MISA: " + (Value.Actual is null ? "Không đọc được" : Value.Actual.Length == 0 ? "(trống)" : Value.Actual);
    public string Evidence => Value.Source;
}

public sealed record HistoryChoice(WorkflowContext Run, ReconciliationAttempt? Attempt)
{
    public string DisplaySummary => $"{(Attempt?.OccurredAt ?? Run.Snapshot.UpdatedAt).ToLocalTime():dd/MM HH:mm:ss} · "
        + (Attempt is null ? "Chưa có đối chiếu" : Attempt.Result is null ? "Không đọc đủ" : Attempt.Result.Matched ? "Khớp" : "Lệch")
        + (Attempt is null ? "" : " · " + (Attempt.Phase == "before_save" ? "Trước Cất" : Attempt.Phase == "totals" ? "Sau nhập" : "Đọc lại"));
}

public sealed record ProfileRecord(WorkflowContext Context, IReadOnlyList<WorkflowContext> Runs)
{
    public string Title => $"{Context.Invoice.Identity.Series} {Context.Invoice.Identity.Number}";
    public string Subtitle => $"{Context.Invoice.Buyer.Name} · {Runs.Count} lần xử lý";
    public string Status => RunOutcome.IsExisting(Context.Snapshot) ? RunOutcome.ExistingLabel
        : Context.Snapshot.Status == WorkflowStatus.Discarded && Context.Reconciliations?.Any(attempt => attempt.Result is { Matched: false }) == true
        ? "Lệch · Đã bỏ" : StatusLabel(Context.Snapshot.Status);
    public string StatusColor => RunOutcome.IsExisting(Context.Snapshot) ? RunOutcome.ExistingColor : Context.Snapshot.Status switch
    {
        WorkflowStatus.Saved => "#15803D",
        WorkflowStatus.Discarded => "#BE123C",
        WorkflowStatus.Failed => "#B91C1C",
        WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain => "#6D28D9",
        WorkflowStatus.Blocked => "#B45309",
        _ => "#617B70"
    };
    public static string StatusLabel(WorkflowSnapshot snapshot)
        => RunOutcome.IsExisting(snapshot) ? RunOutcome.ExistingLabel : StatusLabel(snapshot.Status);
    public static string StatusLabel(WorkflowStatus status) => status switch
    {
        WorkflowStatus.Saved => "Đã lưu",
        WorkflowStatus.Discarded => "Đã bỏ",
        WorkflowStatus.Failed => "Lỗi",
        WorkflowStatus.Blocked => "Cần xử lý",
        WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain => "Chưa rõ · cần xác minh",
        WorkflowStatus.Paused => "Tạm dừng",
        WorkflowStatus.Cancelled => "Đã hủy phiên",
        WorkflowStatus.AwaitingSave => "Chờ Cất",
        WorkflowStatus.Ready => "Sẵn sàng",
        _ => "Đang xử lý"
    };
}

public partial class MainWindow
{
    private WorkflowContext? shownContext;
    private InvoiceDocument? shownInvoice;
    private string? shownActivity;
    private string? scrolledActiveKey;
    private bool updatingProfiles;
    private bool presentationInitialized;
    private bool showOnlyMismatch;
    private WorkflowContext? profileContext;
    private IReadOnlyList<WorkflowContext>? profileHistory;

    private static string FormatNumber(decimal? value) => value?.ToString("N2", CultureInfo.GetCultureInfo("vi-VN")) ?? "Chưa có";
    private static string FormatDay(DateOnly? value) => value?.ToString("dd/MM/yyyy") ?? "Chưa có";

    private void OnlyMismatch_Changed(object sender, RoutedEventArgs e)
    {
        showOnlyMismatch = OnlyMismatchToggle.IsChecked == true;
        presentationInitialized = false;
        RefreshWorkflowPanel();
    }

    private void RefreshWorkflowPanel()
    {
        if (workflow is null) return;
        var canResume = workflow.CanResume;
        ResumeButton.Visibility = canResume ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = canResume ? Visibility.Collapsed : Visibility.Visible;
        StopButton.IsEnabled = workflow.IsRunning;
        ReconcileButton.IsEnabled = !workflow.IsRunning;
        var current = workflow.CurrentContext;
        var invoice = workflow.SelectedInvoice
            ?? (current is not null && !WorkflowRunner.IsTerminal(current.Snapshot.Status) ? current.Invoice : null);
        var bound = current?.Invoice.Fingerprint == invoice?.Fingerprint ? current : null;
        var activity = workflow.Activity is { } work && work.RunId == bound?.Snapshot.RunId ? work.Field : null;
        RefreshProfiles();
        SheetChoice.Visibility = workbook is null ? Visibility.Collapsed : Visibility.Visible;
        InvoiceExpander.Visibility = invoices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (presentationInitialized && ReferenceEquals(shownContext, bound) && ReferenceEquals(shownInvoice, invoice) && shownActivity == activity) return;
        presentationInitialized = true;
        shownContext = bound; shownInvoice = invoice; shownActivity = activity;
        if (invoice is null)
        {
            entryFields.Clear();
            EntryHeading.Visibility = EntryList.Visibility = EntryColumns.Visibility = Visibility.Collapsed;
            EntryEmpty.Visibility = Visibility.Visible;
            EntryProgress.Value = 0;
            return;
        }
        EntryHeading.Visibility = EntryList.Visibility = EntryColumns.Visibility = Visibility.Visible;
        EntryEmpty.Visibility = Visibility.Collapsed;
        EntryTitle.Text = $"Hóa đơn {invoice.Identity.Number} · {invoice.Lines.Length} mặt hàng";
        var latest = bound?.Reconciliations?.LastOrDefault();
        var comparisons = latest?.Result?.Values.Where(value => !IsHiddenDisplayField(value.Field))
            .GroupBy(value => value.Field, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.FirstOrDefault(value => !value.Matched) ?? group.Last(), StringComparer.Ordinal);
        var fields = new List<EntryFieldRow>();
        EntryFieldRow Create(string key, string label, string? value, bool section = false)
        {
            var read = comparisons?.GetValueOrDefault(key);
            var expected = string.IsNullOrEmpty(value) ? read?.Expected : value;
            return new(key, label, string.IsNullOrEmpty(expected) ? "(trống)" : expected,
                read is null ? "" : read.Actual is null ? "Không đọc được" : read.Actual.Length == 0 ? "(trống)" : read.Actual,
                section, activity == key, read?.Matched == true, read?.Matched == false);
        }
        void Add(string key, string label, string? value, bool section = false)
            => fields.Add(Create(key, label, value, section));
        Add("customer", "Mã khách hàng", invoice.Buyer.Code);
        Add("customer_name", "Tên khách hàng", invoice.Buyer.Name);
        if (!string.IsNullOrWhiteSpace(invoice.Buyer.TaxCode)) Add("customer_tax", "Mã số thuế", invoice.Buyer.TaxCode);
        if (!string.IsNullOrWhiteSpace(invoice.Buyer.Address)) Add("customer_address", "Địa chỉ", invoice.Buyer.Address);
        Add("posted_date", "Ngày hạch toán", FormatDay(invoice.PostedDate));
        Add("voucher_date", "Ngày chứng từ", FormatDay(invoice.VoucherDate));
        Add("issued_date", "Ngày hóa đơn", FormatDay(invoice.Issued));
        Add("number", "Số hóa đơn", invoice.Identity.Number);
        for (var i = 0; i < invoice.Lines.Length; i++)
        {
            var line = invoice.Lines[i];
            var key = "line:" + line.LineId + ":";
            var children = new List<EntryFieldRow>
            {
                Create(key + "code", "Mã hàng", line.ItemCode.Trim().Length == 0 ? "(tra theo danh mục)" : line.ItemCode),
                Create(key + "unit", "Đơn vị tính", line.Unit)
            };
            if (comparisons?.ContainsKey(key + "debit_account") == true)
                children.Add(Create(key + "debit_account", "Tài khoản", null));
            children.Add(Create(key + "quantity", "Số lượng", FormatNumber(line.Quantity)));
            children.Add(Create(key + "unit_price", "Đơn giá", bound?.NativeValues?.GetValueOrDefault("price:" + line.LineId)
                ?? "MISA tự tính từ thành tiền / số lượng"));
            children.Add(Create(key + "amount", "Thành tiền", FormatNumber(line.AmountBeforeTax)));
            children.Add(Create(key + "tax_rate", "Thuế suất", line.TaxRate));
            children.Add(Create(key + "tax_amount", "Tiền thuế", FormatNumber(line.TaxAmount)));
            fields.Add(new EntryFieldRow(key + "heading", $"Hàng {i + 1}/{invoice.Lines.Length}", line.Name,
                IsSection: true, Children: children));
        }
        Add("subtotal", "Cộng tiền hàng", FormatNumber(invoice.Subtotal));
        Add("tax_total", "Tổng thuế", FormatNumber(invoice.TaxTotal));
        Add("grand_total", "Tổng thanh toán", FormatNumber(invoice.GrandTotal));
        var activeRow = fields.FirstOrDefault(field => field.IsActive || field.Children?.Any(child => child.IsActive) == true);
        var mismatches = fields.Sum(field => field.MismatchCount);
        if (showOnlyMismatch && comparisons is not null)
        {
            fields = [.. fields.Select(field => field.IsLineSection
                    ? field.MismatchCount > 0 ? field with { Children = [.. field.Children!.Where(child => child.IsMismatch)] } : null
                    : field.IsMismatch ? field : null).OfType<EntryFieldRow>()];
            if (fields.Count == 0) fields.Add(new("none", "Kết quả", "Không có dòng lệch"));
        }
        for (var i = 0; i < fields.Count; i++)
        {
            if (i >= entryFields.Count) entryFields.Add(fields[i]);
            else if (entryFields[i] != fields[i]) entryFields[i] = fields[i];
        }
        while (entryFields.Count > fields.Count) entryFields.RemoveAt(entryFields.Count - 1);
        var active = activeRow is null ? null : entryFields.FirstOrDefault(field => field.Key == activeRow.Key);
        // Only follow the active field when it moves, so the user can scroll freely in between.
        if (active is not null && active.Key != scrolledActiveKey) EntryList.ScrollIntoView(active);
        scrolledActiveKey = active?.Key;
        OnlyMismatchToggle.Content = comparisons is null ? "Chỉ dòng lệch" : mismatches == 0 ? "Chỉ dòng lệch · 0" : $"Chỉ dòng lệch · {mismatches}";
        OnlyMismatchToggle.IsEnabled = comparisons is not null;
        var activeLabel = activeRow?.Children?.FirstOrDefault(child => child.IsActive) ?? activeRow;
        ActivityLabel.Text = activity == "reconcile" ? "Đang đọc lại toàn bộ thông tin từ MISA..."
            : activity == "save" ? "Đang Cất và xác minh kết quả..."
            : activity == "discard" ? "Đang đóng bỏ và xác minh kết quả..."
            : activeLabel is not null ? $"Đang nhập: {activeLabel.Label} · {activeLabel.Expected}"
            : bound is not null ? ProfileRecord.StatusLabel(bound.Snapshot)
            : "Dữ liệu nguồn · Chưa nhập vào MISA";
        var checkpoint = bound?.Snapshot.PausedFromStatus ?? bound?.Snapshot.Status ?? WorkflowStatus.Ready;
        EntryProgress.Value = bound?.Snapshot.Status == WorkflowStatus.Saved ? 100
            : checkpoint <= WorkflowStatus.AwaitingSave ? (int)checkpoint * 10 : 0;
        if (workflow.IsRunning || activity == "reconcile" || latest is not null) SourceExpander.IsExpanded = false;
    }

    private void RefreshProfiles()
    {
        if (ReferenceEquals(profileContext, workflow.CurrentContext) && ReferenceEquals(profileHistory, workflow.History)) return;
        profileContext = workflow.CurrentContext;
        profileHistory = workflow.History;
        var runs = workflow.History.ToDictionary(record => record.Snapshot.RunId, StringComparer.Ordinal);
        if (workflow.CurrentContext is { } current) runs[current.Snapshot.RunId] = current;
        var records = runs.Values.GroupBy(run => run.Invoice.Fingerprint, StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group.OrderByDescending(run => run.Snapshot.UpdatedAt).ToArray();
                return new ProfileRecord(ordered[0], ordered);
            }).OrderByDescending(record => record.Context.Snapshot.UpdatedAt).ToArray();
        var selected = (ProfileList.SelectedItem as ProfileRecord)?.Context.Invoice.Fingerprint;
        updatingProfiles = true;
        try
        {
            profileRecords.Clear();
            foreach (var record in records) profileRecords.Add(record);
            ProfileList.SelectedItem = profileRecords.FirstOrDefault(record => record.Context.Invoice.Fingerprint == selected)
                ?? profileRecords.FirstOrDefault();
        }
        finally { updatingProfiles = false; }
        ProfileSummary.Text = records.Length == 0 ? "Chưa có hóa đơn trong hồ sơ."
            : $"{records.Length} hóa đơn · {records.Count(record => record.Context.Snapshot.Status == WorkflowStatus.Saved)} đã lưu"
                + $" · {records.Count(record => record.Context.Snapshot.Status == WorkflowStatus.Discarded && !RunOutcome.IsExisting(record.Context.Snapshot))} đã bỏ"
                + $" · {records.Count(record => RunOutcome.IsExisting(record.Context.Snapshot))} đã có trong MISA";
        foreach (var row in invoices) row.Refresh();
        RefreshRunSummary();
        ShowProfile();
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingProfiles) ShowProfile();
    }

    private void ShowProfile()
    {
        if (ProfileList.SelectedItem is not ProfileRecord record)
        {
            ProfileDetail.Visibility = Visibility.Collapsed;
            ProfileEmpty.Visibility = Visibility.Visible;
            ProfileComparisonList.ItemsSource = null;
            return;
        }
        ProfileDetail.Visibility = Visibility.Visible;
        var choices = record.Runs.SelectMany(run => (run.Reconciliations ?? [])
                .Select(attempt => new HistoryChoice(run, attempt)))
            .OrderByDescending(choice => choice.Attempt?.OccurredAt ?? choice.Run.Snapshot.UpdatedAt).ToArray();
        ProfileHistory.ItemsSource = choices;
        ProfileHistory.Visibility = choices.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (choices.Length > 0)
        {
            ProfileHistory.SelectedIndex = 0;
        }
        else
        {
            ProfileHistory.SelectedItem = null;
            ProfileOutcome.Text = record.Context.Snapshot.Status switch
            {
                WorkflowStatus.Saved => "Đã Cất thành công",
                WorkflowStatus.Discarded when RunOutcome.IsExisting(record.Context.Snapshot) => "Số hóa đơn đã tồn tại trong MISA, không nhập lại",
                WorkflowStatus.Discarded => "Hóa đơn đã được bỏ",
                _ => ProfileRecord.StatusLabel(record.Context.Snapshot)
            };
            ProfileEvidence.Text = $"{record.Context.Snapshot.UpdatedAt.ToLocalTime():dd/MM/yyyy HH:mm} · "
                + $"{System.IO.Path.GetFileName(record.Context.Invoice.Source.FilePath)} · bản dữ liệu {record.Context.Invoice.Revision}";
            ProfileComparisonList.ItemsSource = null;
            ProfileColumns.Visibility = Visibility.Collapsed;
            ProfileEmpty.Visibility = Visibility.Visible;
            ProfileEmptyTitle.Text = "Chưa có lịch sử đối chiếu";
            ProfileEmptyText.Text = "Hồ sơ này chưa có hoặc đã xóa toàn bộ các lần đối chiếu.";
        }
    }

    private void ProfileHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileHistory.SelectedItem is not HistoryChoice choice) return;
        var context = choice.Run;
        var comparison = choice.Attempt?.Result;
        ProfileOutcome.Text = context.Snapshot.Status switch
        {
            WorkflowStatus.Saved => "Đã Cất thành công",
            WorkflowStatus.Discarded when RunOutcome.IsExisting(context.Snapshot) => "Số hóa đơn đã tồn tại trong MISA, không nhập lại",
            WorkflowStatus.Discarded when comparison is { Matched: false } => "Đã bỏ vì thông tin không khớp",
            WorkflowStatus.Discarded => "Hóa đơn đã được bỏ",
            _ => ProfileRecord.StatusLabel(context.Snapshot)
        };
        ProfileEvidence.Text = $"{context.Snapshot.UpdatedAt.ToLocalTime():dd/MM/yyyy HH:mm} · "
            + $"{System.IO.Path.GetFileName(context.Invoice.Source.FilePath)} · bản dữ liệu {context.Invoice.Revision}";
        ProfileComparisonList.ItemsSource = comparison?.Values.Where(value => !IsHiddenDisplayField(value.Field))
            .Select(value => new ComparisonRow(value, ComparisonLabel(value.Field, context.Invoice))).ToArray();
        var hasComparison = comparison?.Values.Any(value => !IsHiddenDisplayField(value.Field)) == true;
        ProfileEmpty.Visibility = hasComparison ? Visibility.Collapsed : Visibility.Visible;
        ProfileColumns.Visibility = hasComparison ? Visibility.Visible : Visibility.Collapsed;
        ProfileEmptyTitle.Text = choice.Attempt is null ? "Lần này chưa có bản đối chiếu" : "Chưa đọc đủ dữ liệu từ MISA";
        ProfileEmptyText.Text = choice.Attempt?.Error
            ?? (context.Snapshot.Status is WorkflowStatus.Discarded or WorkflowStatus.Saved
                ? "Hồ sơ cũ chỉ lưu kết quả xử lý. Các lần chạy mới sẽ lưu chi tiết từng trường để xem lại."
                : context.Snapshot.LastMessage ?? "Đối chiếu sẽ xuất hiện sau khi nhập xong dữ liệu.");
        ProfileComparisonList.ToolTip = "Bằng chứng đã lưu; không phải trạng thái MISA hiện tại.";
    }

    private static string ComparisonLabel(string key, InvoiceDocument invoice)
    {
        if (key.StartsWith("line:", StringComparison.Ordinal))
        {
            var line = invoice.Lines.Select((value, index) => (value, index))
                .FirstOrDefault(item => key.StartsWith("line:" + item.value.LineId + ":", StringComparison.Ordinal));
            if (line.value is not null)
                return $"Hàng {line.index + 1} · {ComparisonLabel(key[("line:" + line.value.LineId + ":").Length..], invoice)}";
        }
        return key switch
        {
            "customer" => "Mã khách hàng", "customer_name" => "Tên khách hàng", "customer_tax" => "Mã số thuế",
            "customer_address" => "Địa chỉ", "customer_contact" => "Người liên hệ", "voucher_number" => "Số chứng từ",
            "voucher_date" => "Ngày chứng từ", "posted_date" => "Ngày hạch toán", "issued_date" => "Ngày hóa đơn",
            "number" => "Số hóa đơn", "series" => "Ký hiệu", "payment" => "Thanh toán", "currency" => "Tiền tệ",
            "code" => "Mã hàng", "name" => "Tên hàng", "unit" => "Đơn vị tính", "debit_account" => "Tài khoản",
            "quantity" => "Số lượng", "unit_price" => "Đơn giá", "amount" => "Thành tiền",
            "tax_rate" => "Thuế suất", "tax_amount" => "Tiền thuế", "subtotal" => "Cộng tiền hàng",
            "tax_total" => "Tổng thuế", "grand_total" => "Tổng thanh toán", "item_row_count" => "Số dòng hàng",
            "tax_row_count" => "Số dòng thuế", _ => key
        };
    }

    private static bool IsHiddenDisplayField(string key)
        => key is "series" or "payment" or "currency";

    private static (int Index, InvoiceLine Line)? LineOf(string key, InvoiceDocument invoice)
    {
        if (!key.StartsWith("line:", StringComparison.Ordinal)) return null;
        for (var i = 0; i < invoice.Lines.Length; i++)
            if (key.StartsWith("line:" + invoice.Lines[i].LineId + ":", StringComparison.Ordinal)) return (i, invoice.Lines[i]);
        return null;
    }

    private static string SectionOf(string key, InvoiceDocument invoice)
    {
        if (LineOf(key, invoice) is { } line) return $"Hàng {line.Index + 1}/{invoice.Lines.Length} · {line.Line.Name}";
        return key is "subtotal" or "tax_total" or "grand_total" or "item_row_count" or "tax_row_count" ? "Tổng cộng" : "Thông tin chung";
    }

    /// <summary>Rows for the comparison window: real readback when present, otherwise the source values as "chưa đọc".</summary>
    internal static IReadOnlyList<ComparisonRow> BuildComparisonRows(InvoiceDocument invoice, ReconciliationResult? result)
    {
        if (result is not null)
            return [.. result.Values.Where(value => !IsHiddenDisplayField(value.Field))
                .Select(value => new ComparisonRow(value, ComparisonLabel(value.Field, invoice), SectionOf(value.Field, invoice)))];
        var rows = new List<ComparisonRow>();
        void Add(string field, string? expected)
            => rows.Add(new(new ValueComparison(field, expected ?? "", null, false, ""), ComparisonLabel(field, invoice), SectionOf(field, invoice), true));
        Add("customer", invoice.Buyer.Code);
        Add("customer_name", invoice.Buyer.Name);
        if (!string.IsNullOrWhiteSpace(invoice.Buyer.TaxCode)) Add("customer_tax", invoice.Buyer.TaxCode);
        if (!string.IsNullOrWhiteSpace(invoice.Buyer.Address)) Add("customer_address", invoice.Buyer.Address);
        Add("posted_date", FormatDay(invoice.PostedDate));
        Add("voucher_date", FormatDay(invoice.VoucherDate));
        Add("issued_date", FormatDay(invoice.Issued));
        Add("number", invoice.Identity.Number);
        foreach (var line in invoice.Lines)
        {
            var key = "line:" + line.LineId + ":";
            Add(key + "code", line.ItemCode);
            Add(key + "name", line.Name);
            Add(key + "unit", line.Unit);
            Add(key + "quantity", FormatNumber(line.Quantity));
            Add(key + "unit_price", FormatNumber(line.UnitPrice));
            Add(key + "amount", FormatNumber(line.AmountBeforeTax));
            Add(key + "tax_rate", line.TaxRate);
            Add(key + "tax_amount", FormatNumber(line.TaxAmount));
        }
        Add("subtotal", FormatNumber(invoice.Subtotal));
        Add("tax_total", FormatNumber(invoice.TaxTotal));
        Add("grand_total", FormatNumber(invoice.GrandTotal));
        return rows;
    }
}

