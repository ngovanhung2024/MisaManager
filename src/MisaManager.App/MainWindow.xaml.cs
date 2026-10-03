using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using MisaManager.Automation;
using MisaManager.Core;
using MisaManager.Import;

namespace MisaManager.App;

public sealed record MappingChoice(int? Column, string Display);

public sealed class MappingRow : INotifyPropertyChanged
{
    private MappingChoice selectedChoice;

    public MappingRow(string key, string label, bool required, int? selectedColumn, IReadOnlyList<MappingChoice> choices)
    {
        Key = key;
        Label = required ? label + " *" : label;
        SuggestedColumn = selectedColumn;
        Choices = choices;
        selectedChoice = choices.Single(choice => choice.Column == selectedColumn);
    }

    public string Key { get; }
    public string Label { get; }
    public int? SuggestedColumn { get; }
    public int? SelectedColumn => selectedChoice.Column;
    public IReadOnlyList<MappingChoice> Choices { get; }
    public MappingChoice SelectedChoice
    {
        get => selectedChoice;
        set
        {
            if (value is null || selectedChoice == value || !Choices.Contains(value)) return;
            selectedChoice = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedChoice)));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SelectionChanged;
}

public partial class MainWindow : Window
{
    private readonly ImportViewModel importModel;
    private WorkbookMappingSchema? mappingSchema => importModel.Mapping;
    private readonly ObservableCollection<InvoiceRow> invoices = [];
    private readonly ObservableCollection<MappingRow> mappingRows = [];
    private readonly ObservableCollection<EntryFieldRow> entryFields = [];
    private readonly ObservableCollection<ProfileRecord> profileRecords = [];
    private readonly DispatcherTimer uiTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private ImportResult? result => importModel.Result;
    private WorkbookData? workbook => importModel.Workbook;
    private bool positioningRail;
    private bool updatingMapping;
    private bool mappingEdited { get => importModel.MappingEdited; set => importModel.MappingEdited = value; }
    private bool updatingSheet;
    private readonly WorkflowViewModel workflow;
    private ComparisonWindow? comparisonWindow;
    private WindowPairing? pairing;
    private bool closingPopup;
    private bool probeBusy;
    private bool? editorOpen;
    private string? popupIdentity;
    private string? popupDismissedIdentity;
    private DateTimeOffset editorProbeDue = DateTimeOffset.MinValue;
    private DateTimeOffset connectionProbeDue = DateTimeOffset.MinValue;
    private ToolState? shownState;
    private WorkflowErrorEventArgs? lastWorkflowError;

    public MainWindow()
    {
        InitializeComponent();
        var services = ((App)Application.Current).Services;
        importModel = new(new ImportSession(services.WorkbookReader, services.Profiles, services.Drafts));
        workflow = new(((App)Application.Current).Services);
        DataContext = workflow;
        workflow.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(workflow.SelectedInvoice))
            {
                InvoiceList.SelectedItem = invoices.FirstOrDefault(row => row.Invoice.Fingerprint == workflow.SelectedInvoice?.Fingerprint);
                if (InvoiceList.SelectedItem is { } shown) InvoiceList.ScrollIntoView(shown);
            }
            if (args.PropertyName != nameof(workflow.Status)) return;
            StatusLabel.Text = workflow.Status;
            foreach (var row in invoices) row.Refresh();
            RefreshWorkflowPanel();
        };
        InvoiceList.ItemsSource = invoices;
        EntryList.ItemsSource = entryFields;
        ProfileList.ItemsSource = profileRecords;
        MappingItems.ItemsSource = mappingRows;
        PerformanceList.ItemsSource = workflow.PerformanceEntries;
        workflow.WorkflowChanged += (_, _) => { RefreshWorkflowPanel(); RefreshRunSummary(); };
        ((App)Application.Current).Services.ItemCatalog.Changed += (_, _) => Dispatcher.BeginInvoke(RefreshCatalogLabel);
        workflow.ErrorRaised += Workflow_ErrorRaised;
        workflow.CheckedChanged += (_, _) => RefreshChecked();
        uiTimer.Tick += UiTimer_Tick;
        uiTimer.Start();
        PreviewKeyDown += Window_PreviewKeyDown;
        Closed += (_, _) =>
        {
            uiTimer.Stop();
            CloseComparisonWindow();
            CancelImport();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        PositionRail(workArea);
        RefreshCatalogLabel();
        await workflow.RestoreAsync();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded && !positioningRail) WindowPairing.KeepOnScreen(this);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(() => { WindowPairing.KeepOnScreen(this); pairing?.Place(); });
    }

    private void PositionRail(System.Windows.Rect workArea)
    {
        if (positioningRail) return;
        positioningRail = true;
        try
        {
            var maxRailWidth = Math.Max(MinWidth, Math.Min(MaxWidth, workArea.Width * 0.30));
            Width = Math.Clamp(Width, MinWidth, maxRailWidth);
            MaxHeight = workArea.Height;
            Height = workArea.Height;
            Left = Math.Max(workArea.Left, workArea.Right - Width);
            Top = workArea.Top;
        }
        finally { positioningRail = false; }
    }

    private async void ChooseWorkbook_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var work = BeginImport();
        ResetMapping();
        updatingSheet = true;
        SheetChoice.ItemsSource = null;
        updatingSheet = false;
        WorkbookLabel.Text = Path.GetFileName(dialog.FileName);
        WorkbookLabel.ToolTip = dialog.FileName;
        StatusLabel.Text = "Đang đọc Excel... Nhấn Esc để dừng.";
        try
        {
            var loaded = await importModel.ReadAsync(dialog.FileName, work.Token);
            if (!IsCurrent(work)) return;
            WorkbookLabel.Text = Path.GetFileName(loaded.FilePath);
            WorkbookLabel.ToolTip = loaded.FilePath;
            updatingSheet = true;
            try
            {
                SheetChoice.ItemsSource = loaded.Sheets.Select(sheet => sheet.Name).ToArray();
                SheetChoice.SelectedIndex = loaded.Sheets.Length == 1 ? 0 : -1;
            }
            finally { updatingSheet = false; }
            StatusLabel.Text = "Chọn trang tính dữ liệu để tiếp tục.";
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { return; }
        catch (Exception error) { if (IsCurrent(work)) ShowImportError(error); return; }
        finally { EndImport(work); }
        if (workbook is not null && SheetChoice.SelectedIndex >= 0 && PrepareMapping())
            await ImportSelectedSheetAsync();
    }

    private async void ChooseCatalog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", CheckFileExists = true, Multiselect = false,
            Title = "Chọn file danh mục vật tư, hàng hóa (Mã, Tên, ĐVT)" };
        if (dialog.ShowDialog(this) != true) return;
        var services = ((App)Application.Current).Services;
        StatusLabel.Text = "Đang đọc file mã hàng...";
        try
        {
            var items = await ItemCatalogWorkbook.ReadAsync(services.WorkbookReader, dialog.FileName);
            var total = await Task.Run(() => services.ItemCatalog.Merge(items, dialog.FileName));
            RefreshCatalogLabel();
            StatusLabel.Text = $"Đã nạp {items.Count:N0} mã hàng từ {Path.GetFileName(dialog.FileName)}; database có {total:N0} mã.";
        }
        catch (Exception error) { StatusLabel.Text = "Không nạp được file mã hàng: " + error.Message; }
    }

    private void RefreshCatalogLabel()
    {
        var catalog = ((App)Application.Current).Services.ItemCatalog;
        CatalogLabel.Text = catalog.Count == 0 ? "Chưa nạp danh mục mã hàng"
            : $"{catalog.Count:N0} mã · {Path.GetFileName(catalog.SourceFile ?? catalog.FilePath)}";
        CatalogLabel.ToolTip = catalog.SourceFile;
        var names = invoices.SelectMany(row => row.Invoice.Lines)
            .Where(line => line.ItemCode.Trim().Length == 0)
            .GroupBy(line => ItemCatalogStore.NameKey(line.Name)).Select(group => group.First()).ToArray();
        if (catalog.Count == 0 || names.Length == 0) { CatalogDetail.Visibility = Visibility.Collapsed; return; }
        var missing = new List<string>();
        var ambiguous = new List<string>();
        foreach (var line in names)
        {
            try { if (catalog.Find(line.Name, line.Unit) is null) missing.Add(line.Name); }
            catch (AutomationException) { ambiguous.Add(line.Name); }
        }
        CatalogDetail.Text = (missing.Count == 0 ? $"Đủ mã cho {names.Length} tên hàng trong file hóa đơn."
                : $"{missing.Count}/{names.Length} tên hàng chưa có mã, sẽ tự tạo mới: {string.Join("; ", missing.Take(4))}{(missing.Count > 4 ? "…" : "")}")
            + (ambiguous.Count == 0 ? "" : $" Trùng tên nhiều mã: {string.Join("; ", ambiguous)}.");
        CatalogDetail.ToolTip = string.Join(Environment.NewLine, missing.Concat(ambiguous));
        CatalogDetail.Visibility = Visibility.Visible;
    }

    private void RefreshChecked()
    {
        foreach (var row in invoices) row.RefreshChecked();
        CheckedCounter.Text = $"Đã chọn {workflow.CheckedCount:N0}/{invoices.Count:N0}";
    }

    private void CheckAll_Click(object sender, RoutedEventArgs e)
        => workflow.SetCheckedMany(invoices.Where(row => !row.Invoice.IsBlocked).Select(row => row.Invoice.Fingerprint));

    private void UncheckAll_Click(object sender, RoutedEventArgs e) => workflow.SetCheckedMany([]);

    private void CheckPending_Click(object sender, RoutedEventArgs e)
        => workflow.SetCheckedMany(workflow.PendingInvoices.Select(invoice => invoice.Fingerprint));

    private void RefreshRunSummary()
    {
        var kinds = invoices.Select(row => row.StatusKind).ToArray();
        SavedCount.Text = kinds.Count(kind => kind == "saved").ToString("N0");
        DiscardedCount.Text = kinds.Count(kind => kind == "discarded").ToString("N0");
        ExistingCount.Text = kinds.Count(kind => kind == "existing").ToString("N0");
        AttentionCount.Text = kinds.Count(kind => kind is "uncertain" or "attention" or "failed" or "paused").ToString("N0");
        PendingCount.Text = kinds.Count(kind => kind is "ready" or "running").ToString("N0");
        InvoiceHeader.Text = invoices.Count == 0 ? "Hóa đơn" : $"Hóa đơn ({invoices.Count:N0})";
        RefreshStateBadge();
    }

    private void RefreshStateBadge()
    {
        var state = workflow.State;
        if (state == shownState) return;
        var wasHint = shownState?.Hint.Length > 0;
        shownState = state;
        var (fg, bg) = StatePalette.Of(state.Kind);
        RunBadge.Background = bg;
        RunDot.Fill = fg;
        RunBadgeText.Foreground = fg;
        RunBadgeText.Text = state.Label;
        Motion.Pulse(RunDot, state.IsBusy);
        if (state.Hint.Length == 0) { HintPanel.Visibility = Visibility.Collapsed; return; }
        HintPanel.Background = bg;
        HintPanel.BorderBrush = fg;
        HintTitle.Foreground = fg;
        HintTitle.Text = state.Detail.Length == 0 ? state.Label : $"{state.Label} · {state.Detail}";
        HintText.Text = state.Hint;
        HintPanel.Visibility = Visibility.Visible;
        if (!wasHint) Motion.FadeIn(HintPanel, fromY: -4);
    }

    private void InvoiceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        workflow.SelectedInvoice = (InvoiceList.SelectedItem as InvoiceRow)?.Invoice;
        if (InvoiceList.SelectedItem is InvoiceRow row) { SelectedTitle.Text = row.DisplayIdentity; ShowIssues(row.Invoice); }
        else ShowWorkbookIssues();
        RefreshWorkflowPanel();
    }

    private void ShowIssues(InvoiceDocument invoice) => SetVisibleIssues(invoice.Issues);

    private void SetVisibleIssues(IEnumerable<ImportIssue> issues)
    {
        var visible = issues.Where(issue => issue.Severity == IssueSeverity.Blocking).ToArray();
        IssueGrid.ItemsSource = visible;
        IssuePanel.Visibility = visible.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        IssueRow.Height = GridLength.Auto;
        IssueRow.MinHeight = 0;
    }

    private async void SheetChoice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsInitialized || updatingSheet) return;
        CancelImport();
        ClearImportResult();
        if (SheetChoice.SelectedIndex >= 0 && PrepareMapping()) await ImportSelectedSheetAsync();
        else ResetMapping();
    }

    private bool PrepareMapping()
    {
        if (workbook is null || SheetChoice.SelectedItem is not string sheet)
        {
            ResetMapping();
            return false;
        }
        try
        {
            var schema = importModel.Describe(sheet);
            ResetMapping();
            updatingMapping = true;
            foreach (var field in schema.Fields.OrderByDescending(field => field.Required))
            {
                var choices = new List<MappingChoice> { new(null, "(không chọn)") };
                choices.AddRange(schema.Columns.Select(column => new MappingChoice(column.Column, column.Display)));
                var row = new MappingRow(field.Key, FieldLabel(field.Key), field.Required, field.SuggestedColumn, choices);
                row.SelectionChanged += MappingRow_SelectionChanged;
                mappingRows.Add(row);
            }
            mappingEdited = false;
            MappingPanel.Visibility = Visibility.Visible;
            return true;
        }
        catch (Exception error)
        {
            ResetMapping();
            ShowImportError(error);
            return false;
        }
        finally { updatingMapping = false; }
    }

    private void ResetMapping()
    {
        updatingMapping = true;
        foreach (var row in mappingRows) row.SelectionChanged -= MappingRow_SelectionChanged;
        mappingRows.Clear();
        mappingEdited = false;
        MappingPanel.Visibility = Visibility.Collapsed;
        MappingExpander.IsExpanded = false;
        updatingMapping = false;
    }

    private async Task ImportSelectedSheetAsync()
    {
        if (workbook is null || SheetChoice.SelectedItem is not string sheet) return;
        var source = workbook;
        var work = BeginImport();
        StatusLabel.Text = "Đang đọc dữ liệu... Nhấn Esc để dừng.";
        try
        {
            var schema = mappingSchema ?? throw new InvalidOperationException("Chưa có cấu hình cột.");
            if (!mappingEdited)
            {
                var saved = await importModel.LoadProfileAsync(sheet, work.Token);
                if (!IsCurrent(work)) return;
                if (saved?.Columns is { } savedColumns)
                {
                    updatingMapping = true;
                    try
                    {
                        foreach (var row in mappingRows)
                        {
                            // Keep the current header suggestion for fields added after an older profile.
                            var column = savedColumns.TryGetValue(row.Key, out var number) ? (int?)number : row.SuggestedColumn;
                            row.SelectedChoice = row.Choices.Single(choice => choice.Column == column);
                        }
                        mappingEdited = true;
                    }
                    finally { updatingMapping = false; }
                }
            }
            var columnMap = mappingEdited ? mappingRows.Where(row => row.SelectedColumn.HasValue)
                .ToImmutableDictionary(row => row.Key, row => row.SelectedColumn!.Value, StringComparer.Ordinal) : null;
            var imported = await importModel.ImportAsync(sheet, columnMap, work.Token);
            if (!IsCurrent(work)) return;
            // MISA numbers vouchers in entry order, so enter oldest first: issued date, then invoice number.
            var ordered = imported.Invoices
                .OrderBy(invoice => invoice.Issued ?? DateOnly.MaxValue)
                .ThenBy(invoice => long.TryParse(invoice.Identity.Number, out var number) ? number : long.MaxValue)
                .ThenBy(invoice => invoice.Identity.Number, StringComparer.Ordinal)
                .ToArray();
            invoices.Clear();
            foreach (var invoice in ordered) invoices.Add(new(invoice, workflow));
            workflow.InvoiceQueue = ordered;
            workflow.KeepCheckedIn(ordered.Select(invoice => invoice.Fingerprint).ToHashSet(StringComparer.Ordinal));
            RefreshChecked();
            if (invoices.Count > 0) InvoiceList.SelectedIndex = 0;
            RefreshCatalogLabel();
            RefreshRunSummary();
            ShowWorkbookIssues();
            if (imported.Issues.Any(issue => issue.Code is "COLUMN_MISSING" or "TOTAL_MEANING_REQUIRED" or "COLUMN_AMBIGUOUS"))
                MappingExpander.IsExpanded = true;
            StatusLabel.Text = imported.IsBlocked ? "Cần bổ sung dữ liệu trước khi nhập." :
                $"{imported.Invoices.Count:N0} hóa đơn · {imported.ReadRows:N0} dòng dữ liệu";
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception error) { if (IsCurrent(work)) ShowImportError(error); }
        finally { EndImport(work); }
    }

    private CancellationTokenSource BeginImport()
    {
        ClearImportResult();
        return importModel.BeginWork();
    }
    private bool IsCurrent(CancellationTokenSource work) => importModel.IsCurrent(work);
    private void EndImport(CancellationTokenSource work) => importModel.EndWork(work);

    private void ClearImportResult()
    {
        importModel.ClearResult();
        invoices.Clear();
        workflow.InvoiceQueue = [];
        workflow.SelectedInvoice = null;
        RefreshWorkflowPanel();
        SetVisibleIssues([]);
        SelectedTitle.Text = "Chọn một hóa đơn";
    }

    private void ShowWorkbookIssues()
    {
        SetVisibleIssues(result?.Issues ?? []);
        SelectedTitle.Text = result is { IsBlocked: true } ? "Thông tin cần bổ sung" : "Chọn một hóa đơn";
    }

    private void ShowImportError(Exception error)
    {
        ClearImportResult();
        workflow.SetCheckedMany([]);
        SetVisibleIssues(new[] { new ImportIssue(
            error is WorkbookImportException fault ? fault.Code : "IMPORT_ERROR", IssueSeverity.Blocking,
            error.Message, Resolution: "Kiểm tra file Excel và các cột đã chọn.") });
        SelectedTitle.Text = "Chưa đọc được dữ liệu";
        StatusLabel.Text = error.Message;
    }

    private async void MappingRow_SelectionChanged(object? sender, EventArgs e)
    {
        if (updatingMapping || !IsInitialized) return;
        mappingEdited = true;
        await ImportSelectedSheetAsync();
    }

    private void CancelImport() => importModel.Cancel();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && importModel.IsBusy)
        {
            CancelImport();
            StatusLabel.Text = "Đã dừng đọc Excel.";
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ErrorPopup.Visibility == Visibility.Visible)
        {
            ErrorPopup.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private static string FieldLabel(string key) => key switch
    {
        "series" => "Ký hiệu hóa đơn",
        "template" => "Mẫu số",
        "invoiceType" => "Loại hóa đơn",
        "seller" => "Tên người bán",
        "sellerTaxCode" => "MST người bán",
        "sellerAddress" => "Địa chỉ người bán",
        "sellerCode" => "Mã người bán",
        "buyer" => "Tên người mua",
        "buyerTaxCode" => "MST người mua",
        "buyerAddress" => "Địa chỉ người mua",
        "buyerCode" => "Mã khách hàng",
        "buyerTaxPresence" => "Trạng thái MST",
        "buyerKind" => "Loại người mua",
        "buyerContact" => "Người liên hệ",
        "discount" => "Chiết khấu dòng",
        "discountRate" => "Tỷ lệ chiết khấu",
        "lineKind" => "Tính chất dòng",
        "lineTotal" => "Tổng tiền có thuế",
        "invoiceDiscount" => "Chiết khấu hóa đơn",
        "currency" => "Tiền tệ",
        "payment" => "Hình thức thanh toán",
        "exchangeRate" => "Tỷ giá",
        "voucherDate" => "Ngày chứng từ",
        "postedDate" => "Ngày hạch toán",
        "notes" => "Ghi chú",
        "number" => "S\u1ED1 h\u00F3a \u0111\u01A1n",
        "issued" => "Ngày lập",
        "signed" => "Ngày ký (trống = ngày lập)",
        "name" => "T\u00EAn h\u00E0ng h\u00F3a",
        "unit" => "\u0110\u01A1n v\u1ECB t\u00EDnh",
        "quantity" => "S\u1ED1 l\u01B0\u1EE3ng",
        "unitPrice" => "\u0110\u01A1n gi\u00E1",
        "amount" => "Th\u00E0nh ti\u1EC1n d\u00F2ng",
        "taxRate" => "Thu\u1EBF su\u1EA5t",
        "taxAmount" => "Tiền thuế chi tiết",
        _ => key
    };

    private async void Reconcile_Click(object sender, RoutedEventArgs e)
    {
        if (workflow.IsRunning) return;
        if (workflow.SelectedInvoice is null && workflow.CurrentContext is { } current)
            workflow.SelectedInvoice = current.Invoice;
        if (workflow.SelectedInvoice is not null)
        {
            try
            {
                editorOpen = null;
                var response = await workflow.ReconcileAsync();
                StatusLabel.Text = response.Message;
                RefreshWorkflowPanel();
                WorkspaceTabs.SelectedIndex = 0;
                UpdateComparisonPopup();
            }
            catch (Exception error) { StatusLabel.Text = error.Message; }
            return;
        }
        StatusLabel.Text = result?.IsBlocked == true
            ? "Đối chiếu nguồn: workbook còn lỗi chặn."
            : "Đối chiếu nguồn hoàn tất; chọn một hóa đơn để xem chi tiết.";
        ShowWorkbookIssues();
    }

    private async void ComparisonWindow_ReconcileRequested(object? sender, EventArgs e)
    {
        if (workflow.IsRunning) return;
        try
        {
            editorOpen = null;
            var response = await workflow.ReconcileAsync();
            StatusLabel.Text = response.Message;
            RefreshWorkflowPanel();
            UpdateComparisonPopup();
        }
        catch (Exception error) { StatusLabel.Text = error.Message; }
    }

    private async void UiTimer_Tick(object? sender, EventArgs e)
    {
        workflow.RefreshPerformance();
        RefreshWorkflowPanel();
        RefreshStateBadge();
        if (workflow.IsRunning) editorOpen = null;
        UpdateComparisonPopup();
        // Probes share the single UIA thread with data entry, so they only run while idle and never overlap.
        if (workflow.IsRunning || probeBusy || importModel.IsBusy) return;
        var now = DateTimeOffset.UtcNow;
        probeBusy = true;
        try
        {
            if (now >= editorProbeDue && workflow.Connection == MisaConnection.Connected && IsComparisonBound())
            {
                editorProbeDue = now.AddMilliseconds(1000);
                var editor = await workflow.ObserveVoucherEditorAsync();
                if (workflow.IsRunning) return;
                editorOpen = editor.IsOpen;
                if (!editor.IsOpen && popupIdentity is not null) popupDismissedIdentity = popupIdentity;
                UpdateComparisonPopup();
            }
            else if (now >= connectionProbeDue)
            {
                await workflow.ProbeConnectionAsync();
                connectionProbeDue = DateTimeOffset.UtcNow.AddMilliseconds(workflow.Connection == MisaConnection.Connected ? 4000 : 1500);
                RefreshStateBadge();
            }
        }
        finally { probeBusy = false; }
    }

    private void Workflow_ErrorRaised(object? sender, WorkflowErrorEventArgs error)
    {
        lastWorkflowError = error;
        var code = ErrorHints.FindCode(error.Message, error.Details);
        var hint = ErrorHints.For(code);
        ErrorPopupTitle.Text = code is null ? "Lỗi khi thêm vào MISA" : "Lỗi khi thêm vào MISA · " + code;
        ErrorPopupMessage.Text = error.Message;
        ErrorPopupHint.Text = hint ?? "";
        ErrorPopupHintBox.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorPopupDetails.Text = error.Details;
        ErrorPopup.Visibility = Visibility.Visible;
        Motion.FadeIn(ErrorPopup, fromY: -6);
    }

    private void Add_Click(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedIndex = 0;

    private async void StopNow_Click(object sender, RoutedEventArgs e)
    {
        if (!workflow.IsRunning) { StatusLabel.Text = "Không có bước nào đang chạy."; return; }
        if (MessageBox.Show(this, "Dừng ngay sẽ hủy bước đang chạy và có thể để chứng từ dở dang trong MISA. Sau đó có thể bấm Tiếp tục để chạy lại bước hiện tại. Vẫn dừng ngay?",
            "Dừng ngay", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await workflow.StopNowAsync();
        StatusLabel.Text = workflow.Status;
    }

    private void CloseErrorPopup_Click(object sender, RoutedEventArgs e)
        => ErrorPopup.Visibility = Visibility.Collapsed;

    private void CopyError_Click(object sender, RoutedEventArgs e)
    {
        if (lastWorkflowError is null) return;
        Clipboard.SetText(workflow.ExportLastError(lastWorkflowError.Message, lastWorkflowError.Details, lastWorkflowError.OccurredAt));
        StatusLabel.Text = "Đã copy thông tin lỗi và hiệu năng vào clipboard.";
    }

    private void CopyPerformance_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(workflow.ExportPerformance());
        StatusLabel.Text = "Đã copy báo cáo hiệu năng vào clipboard để gửi ChatGPT.";
    }

    private bool IsComparisonBound()
    {
        var current = workflow.CurrentContext;
        var invoice = workflow.SelectedInvoice ?? current?.Invoice;
        return current is not null && invoice is not null && current.Invoice.Fingerprint == invoice.Fingerprint;
    }

    /// <summary>
    /// Mirrors the tool's data into the comparison window: shown while a run is active or the MISA voucher tab is open,
    /// closed as soon as the tab closes, refreshed whenever a new reconciliation arrives.
    /// </summary>
    private void UpdateComparisonPopup()
    {
        var current = workflow.CurrentContext;
        var state = workflow.State;
        var bound = IsComparisonBound() && current is not null;
        var wanted = bound && IsVisible && state.Kind != ToolStateKind.Disconnected
            && (workflow.IsRunning || editorOpen == true || (editorOpen is null && comparisonWindow is not null));
        if (!wanted || current is null)
        {
            CloseComparisonWindow();
            return;
        }
        var identity = $"{current.Snapshot.RunId}:{current.Snapshot.Status}:{current.Reconciliations?.Count}:{state.Kind}:{state.Label}";
        if (identity == popupDismissedIdentity)
        {
            CloseComparisonWindow();
            return;
        }
        if (comparisonWindow is null)
        {
            var window = new ComparisonWindow { Owner = this };
            window.ReconcileRequested += ComparisonWindow_ReconcileRequested;
            window.Closed += (_, _) =>
            {
                if (!ReferenceEquals(comparisonWindow, window)) return;
                if (!closingPopup) popupDismissedIdentity = popupIdentity;
                pairing?.Dispose();
                pairing = null;
                comparisonWindow = null;
                popupIdentity = null;
            };
            pairing = new WindowPairing(this, window);
            comparisonWindow = window;
            pairing.Place();
            window.SlideFrom = pairing.PopupOnRight ? -14 : 14;
            window.ContentRendered += (_, _) => pairing?.Place();
            if (pairing.CanShowPopup) window.Show();
            popupIdentity = null;
        }
        else if (pairing is { CanShowPopup: true } && !comparisonWindow.IsVisible) comparisonWindow.Show();
        if (identity != popupIdentity)
        {
            popupIdentity = identity;
            comparisonWindow.UpdateComparison(BuildComparisonModel(current, state));
        }
        comparisonWindow.SetReconcileEnabled(!workflow.IsRunning);
    }

    private ComparisonModel BuildComparisonModel(WorkflowContext current, ToolState state)
    {
        var invoice = current.Invoice;
        var latest = current.Reconciliations?.LastOrDefault();
        var rows = BuildComparisonRows(invoice, latest?.Result);
        var mismatched = rows.Count(row => row.Kind == "mismatch");
        var kind = state.Kind;
        var label = state.Label;
        if (kind == ToolStateKind.Ready && latest?.Result is not null)
        {
            kind = mismatched == 0 ? ToolStateKind.Saved : ToolStateKind.Error;
            label = mismatched == 0 ? "Đã đối chiếu: khớp" : $"Có {mismatched} dòng lệch";
        }
        var summary = RunOutcome.IsExisting(current.Snapshot) ? "Số hóa đơn đã tồn tại trong MISA: tool đã chọn Không, đóng tab và không nhập lại."
            : latest is null ? "Chưa đối chiếu. Dữ liệu bên MISA sẽ hiện sau khi nhập hoặc bấm Đọc lại."
            : latest.Result is null ? "Không đọc đủ thông tin từ MISA: " + latest.Error
                + (ErrorHints.For(ErrorHints.FindCode(latest.Error)) is { } hint ? " " + hint : "")
            : mismatched == 0 ? "Tất cả thông tin khớp với nguồn Excel."
            : $"{mismatched} trường lệch giữa Excel và MISA. Dòng đỏ là chỗ cần kiểm tra.";
        var subtitle = $"{invoice.Buyer.Name} · {FormatDay(invoice.Issued)} · {invoice.GrandTotal?.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("vi-VN")) ?? "?"} {invoice.Currency}";
        var observed = latest?.Result is { } result ? "đọc lúc " + result.ObservedAt.ToLocalTime().ToString("HH:mm:ss") : "";
        return new($"Hóa đơn {invoice.Identity.Number}", subtitle, kind, label, summary, observed, rows, !workflow.IsRunning);
    }

    private void CloseComparisonWindow()
    {
        if (comparisonWindow is not { } window) return;
        closingPopup = true;
        try { window.Close(); }
        finally { closingPopup = false; }
    }

    private async void ClearComparison_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var deleted = await workflow.DeleteAllProfileHistoryAsync();
            popupDismissedIdentity = null;
            CloseComparisonWindow();
            workflow.ClearPerformance();
            StatusLabel.Text = deleted == 0
                ? "Đã xóa dữ liệu hiệu năng; hồ sơ không có phiên lịch sử cũ để xóa."
                : $"Đã xóa toàn bộ lịch sử hồ sơ ({deleted} phiên) và dữ liệu hiệu năng.";
        }
        catch (Exception error) { StatusLabel.Text = "Không xóa được lịch sử hồ sơ: " + error.Message; }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private sealed class InvoiceRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private readonly WorkflowViewModel selection;
        public bool IsChecked
        {
            get => selection.IsChecked(Invoice.Fingerprint);
            set => selection.SetChecked(Invoice.Fingerprint, value);
        }
        public void RefreshChecked() => PropertyChanged?.Invoke(this, new(nameof(IsChecked)));
        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new(nameof(DisplayStatus)));
            PropertyChanged?.Invoke(this, new(nameof(StatusKind)));
            PropertyChanged?.Invoke(this, new(nameof(DisplaySummary)));
        }
        public InvoiceDocument Invoice { get; }
        public InvoiceRow(InvoiceDocument invoice, WorkflowViewModel selection)
        {
            Invoice = invoice;
            this.selection = selection;
        }
        public string DisplayIdentity => (Invoice.Identity.Series.Length == 0 ? "HĐ " : Invoice.Identity.Series + " ")
            + $"{Invoice.Identity.Number} · {Invoice.Buyer.Name}";
        public string DisplayDate => Invoice.Issued?.ToString("dd/MM/yyyy") ?? "Thiếu";
        public int LineCount => Invoice.Lines.Length;
        public string SourceRange => $"dòng {Invoice.Source.FirstRow}-{Invoice.Source.LastRow}";
        public string DisplaySummary => $"{DisplayDate} · {LineCount} mặt hàng · {Invoice.GrandTotal?.ToString("N0") ?? "?"} {Invoice.Currency}";
        public string DisplayStatus
        {
            get
            {
                if (Invoice.IsBlocked) return "Thiếu dữ liệu";
                if (LatestRun() is { } record) return ProfileRecord.StatusLabel(record.Snapshot);
                return "Sẵn sàng nhập";
            }
        }
        /// <summary>Drives the list color: saved green, discarded rose, failed red, uncertain violet, blocked amber.</summary>
        public string StatusKind
        {
            get
            {
                if (Invoice.IsBlocked) return "attention";
                var snapshot = LatestRun()?.Snapshot;
                if (snapshot is not null && RunOutcome.IsExisting(snapshot)) return "existing";
                return snapshot?.Status switch
                {
                    null or WorkflowStatus.Ready or WorkflowStatus.Cancelled => "ready",
                    WorkflowStatus.Saved => "saved",
                    WorkflowStatus.Discarded => "discarded",
                    WorkflowStatus.Failed => "failed",
                    WorkflowStatus.InputUncertain or WorkflowStatus.SaveUncertain => "uncertain",
                    WorkflowStatus.Paused => "paused",
                    WorkflowStatus.Blocked => "attention",
                    _ => "running"
                };
            }
        }
        private WorkflowContext? LatestRun()
        {
            var current = ((App)Application.Current).Services.Workflow.Current;
            return current?.Invoice.Fingerprint == Invoice.Fingerprint ? current
                : (Application.Current.MainWindow as MainWindow)?.workflow.History
                    .FirstOrDefault(run => run.Invoice.Fingerprint == Invoice.Fingerprint);
        }
    }
}





