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
    // The imported result without invoices already posted in MISA: what the list and its issues show.
    private ImportResult? shown;
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
    // Bán hàng or Mua hàng: decides the invoice source, the posted register and the MISA voucher.
    private VoucherKind mode;
    private bool Purchase => mode == VoucherKind.Purchase;
    private bool switchingMode;

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
        workflow.SessionReset += (_, _) =>
        {
            lastWorkflowError = null;
            ErrorPopup.Visibility = Visibility.Collapsed;
        };
        ((App)Application.Current).Services.ItemCatalog.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            RefreshCatalogLabel();
            RefreshHolds();
        });
        ((App)Application.Current).Services.PartyCatalog.Changed += (_, _) => Dispatcher.BeginInvoke(RefreshPartyLabel);
        workflow.ErrorRaised += Workflow_ErrorRaised;
        workflow.CheckedChanged += (_, _) => { RefreshChecked(); SaveTicks(); };
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
        try
        {
            var workArea = SystemParameters.WorkArea;
            PositionRail(workArea);
            mode = ((App)Application.Current).Services.Mode;
            ApplyMode();
            RefreshCatalogLabel();
            RefreshPartyLabel();
            await workflow.RestoreAsync();
            await RestoreImportSessionAsync();
            _ = UpdatePrompt.CheckAsync(this, ((App)Application.Current).Services.Updates, interactive: false, status => StatusLabel.Text = status,
                workflow.ReleaseCheckpointForUpdateAsync);
        }
        catch (Exception error) { StatusLabel.Text = "Không khôi phục được phiên trước: " + error.Message; }
    }

    internal void ReportUnexpectedError(Exception error, string? logPath)
        => Workflow_ErrorRaised(this, new WorkflowErrorEventArgs("Lỗi không mong muốn trong tool (đã ghi log, tool vẫn chạy): " + error.Message,
            (logPath is null ? "" : "Log: " + logPath + Environment.NewLine) + error, DateTimeOffset.Now));

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RememberRailBounds();
        if (IsLoaded && !positioningRail) WindowPairing.KeepOnScreen(this);
    }

    // Last bounds seen while really shown (not minimized). Minimizing must never change the rail: if anything
    // resized/moved it meanwhile (MISA 0.2.53 did, which squeezed out the invoice list), restore puts it back.
    private System.Windows.Rect? railBounds;

    private void RememberRailBounds()
    {
        if (!IsLoaded || positioningRail || WindowState != WindowState.Normal || WindowPairing.IsMinimized(this)) return;
        railBounds = new(Left, Top, ActualWidth, ActualHeight);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        RememberRailBounds();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Normal || railBounds is not { } saved) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (WindowState != WindowState.Normal || WindowPairing.IsMinimized(this)) return;
            if (new System.Windows.Rect(Left, Top, ActualWidth, ActualHeight) != saved)
            {
                positioningRail = true;
                try { (Left, Top, Width, Height) = (saved.Left, saved.Top, saved.Width, saved.Height); }
                finally { positioningRail = false; }
            }
            WindowPairing.KeepOnScreen(this);
            pairing?.Place();
        }, System.Windows.Threading.DispatcherPriority.Background);
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
        railBounds = new(Left, Top, Width, Height);
    }

    private async void ChooseWorkbook_Click(object sender, RoutedEventArgs e)
    {
        var dialog = Purchase
            ? new OpenFileDialog { Filter = "Hóa đơn mua vào (*.zip;*.html;*.xlsx)|*.zip;*.html;*.htm;*.xlsx", CheckFileExists = true, Multiselect = false,
                Title = "Bước 1/2: Chọn file zip hóa đơn mua vào (các file HTML tải từ cổng thuế) hoặc file Excel đã chuyển đổi" }
            : new OpenFileDialog { Filter = "Hóa đơn bán ra (*.zip;*.html;*.xlsx)|*.zip;*.html;*.htm;*.xlsx", CheckFileExists = true, Multiselect = false,
                Title = "Bước 1/2: Chọn file zip hóa đơn bán ra (các file HTML tải từ cổng thuế) hoặc file Excel hóa đơn" };
        if (dialog.ShowDialog(this) != true) return;
        var services = ((App)Application.Current).Services;
        var workbookPath = dialog.FileName;
        if (!workbookPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            if (await ConvertArchiveAsync(workbookPath, Purchase) is not { } converted) return;
            workbookPath = converted;
        }
        var postedStore = PostedStore;
        var postedDialog = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", CheckFileExists = true, Multiselect = false,
            Title = (Purchase ? "Bước 2/2: Chọn file danh sách mua hàng đã ghi sổ xuất từ MISA (Mua_hang_hoa__dich_vu.xlsx)"
                    : "Bước 2/2: Chọn file danh sách bán hàng đã ghi sổ xuất từ MISA (Ban_hang.xlsx)")
                + (postedStore.Count > 0 ? $" – Hủy để dùng danh sách đã nạp ({postedStore.Count:N0} hóa đơn)" : " – Hủy nếu MISA chưa có hóa đơn nào") };
        if (postedDialog.ShowDialog(this) == true)
        {
            StatusLabel.Text = "Đang đọc file hóa đơn đã ghi sổ...";
            try
            {
                var entries = await PostedInvoiceWorkbook.ReadAsync(services.WorkbookReader, postedDialog.FileName);
                await Task.Run(() => postedStore.Replace(entries, postedDialog.FileName));
            }
            catch (Exception error)
            {
                StatusLabel.Text = "Không nạp được file hóa đơn đã ghi sổ: " + error.Message;
                return;
            }
        }
        var work = BeginImport();
        ResetMapping();
        updatingSheet = true;
        SheetChoice.ItemsSource = null;
        updatingSheet = false;
        WorkbookLabel.Text = Path.GetFileName(workbookPath);
        WorkbookLabel.ToolTip = workbookPath;
        StatusLabel.Text = "Đang đọc Excel... Nhấn Esc để dừng.";
        try
        {
            var loaded = await importModel.ReadAsync(workbookPath, work.Token);
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
            if (SheetChoice.SelectedIndex < 0) OpenSourceSettings();
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { return; }
        catch (Exception error) { if (IsCurrent(work)) ShowImportError(error); return; }
        finally { EndImport(work); }
        try
        {
            if (workbook is not null && SheetChoice.SelectedIndex >= 0 && PrepareMapping())
                await ImportSelectedSheetAsync();
        }
        catch (Exception error) { ShowImportError(error); }
    }

    private PostedInvoiceStore PostedStore => Purchase ? ((App)Application.Current).Services.PostedPurchases
        : ((App)Application.Current).Services.PostedInvoices;

    /// <summary>
    /// Reads the portal's HTML invoices (zip, folder or one page), keeps one per seller/series/number and writes them sorted
    /// from the start of the year to an Excel file next to the source, which is then imported like any workbook. Sales use
    /// the layout of the portal's old Excel export, purchases the purchase sheet.
    /// </summary>
    private async Task<string?> ConvertArchiveAsync(string source, bool purchase)
    {
        StatusLabel.Text = "Đang đọc và chuyển hóa đơn HTML sang Excel...";
        void Write(string path, IReadOnlyList<PurchaseHtmlInvoice> invoices)
        {
            if (purchase) PurchaseInvoiceArchive.WriteWorkbook(path, invoices);
            else SalesInvoiceArchive.WriteWorkbook(path, invoices);
        }
        try
        {
            var archive = await Task.Run(() => PurchaseInvoiceArchive.Read(source));
            if (archive.Invoices.Count == 0)
            {
                StatusLabel.Text = "Không đọc được hóa đơn nào: " + string.Join("; ", archive.Skipped.Take(3));
                return null;
            }
            var name = Path.GetFileNameWithoutExtension(source) + (purchase ? "_hoa_don_mua_vao.xlsx" : "_hoa_don_ban_ra.xlsx");
            var target = Path.Combine(Path.GetDirectoryName(source)!, name);
            try { await Task.Run(() => Write(target, archive.Invoices)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Read-only or locked folder (e.g. the file is open in Excel): keep the converted file with the app data.
                target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.TechnicalName,
                    purchase ? "purchase" : "sales", Path.GetFileNameWithoutExtension(name) + "_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".xlsx");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await Task.Run(() => Write(target, archive.Invoices));
            }
            StatusLabel.Text = $"Đã chuyển {archive.Invoices.Count:N0} hóa đơn ({archive.Invoices[0].Issued:dd/MM/yyyy} → {archive.Invoices[^1].Issued:dd/MM/yyyy}) sang {target}"
                + (archive.Skipped.Count > 0 ? $" · bỏ {archive.Skipped.Count:N0} file" : "");
            return target;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or WorkbookImportException
            or NotSupportedException)
        {
            StatusLabel.Text = (purchase ? "Không chuyển được hóa đơn mua vào: " : "Không chuyển được hóa đơn bán ra: ") + error.Message;
            return null;
        }
    }

    private async void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || switchingMode) return;
        var next = PurchaseMode.IsChecked == true ? VoucherKind.Purchase : VoucherKind.Sales;
        if (next == mode) return;
        if (!workflow.CanEditSource)
        {
            ApplyMode();
            return;
        }
        try
        {
            CancelImport();
            importModel.Reset();
            ClearImportResult();
            workflow.SetCheckedMany([]);
            ResetMapping();
            updatingSheet = true;
            SheetChoice.ItemsSource = null;
            updatingSheet = false;
            mode = next;
            ((App)Application.Current).Services.Mode = next;
            ApplyMode();
            RefreshPartyLabel();
            await RestoreImportSessionAsync();
            if (invoices.Count == 0)
                StatusLabel.Text = Purchase
                    ? "Chế độ Mua hàng: bấm ••• → Mở hóa đơn mua vào và chọn file zip HTML tải từ cổng thuế."
                    : "Chế độ Bán hàng: bấm ••• → Mở Excel hóa đơn.";
        }
        catch (Exception error) { StatusLabel.Text = "Không chuyển được chế độ: " + error.Message; }
    }

    /// <summary>Shows the current mode on the switch, menu and labels.</summary>
    private void ApplyMode()
    {
        switchingMode = true;
        try
        {
            SalesMode.IsChecked = !Purchase;
            PurchaseMode.IsChecked = Purchase;
        }
        finally { switchingMode = false; }
        importModel.Purchase = Purchase;
        ModeHint.Text = Purchase ? "Hóa đơn mua vào → Mua hàng nhập kho (KHH · 1561 · 331)" : "Hóa đơn bán ra → Chứng từ bán hàng";
        OpenInvoicesItem.Header = Purchase ? "Mở hóa đơn mua vào (zip HTML)…" : "Mở Excel hóa đơn…";
        OpenInvoicesItem.ToolTip = Purchase
            ? "Chọn file zip các hóa đơn HTML tải từ cổng thuế (tool chuyển sang Excel, sắp xếp từ đầu năm), rồi file Mua hàng hóa, dịch vụ đã ghi sổ xuất từ MISA"
            : "Chọn file Excel hóa đơn, rồi file danh sách bán hàng đã ghi sổ xuất từ MISA: danh sách chỉ hiện hóa đơn chưa nhập";
        ServicesTab.Visibility = Purchase ? Visibility.Visible : Visibility.Collapsed;
        if (!Purchase && ServicesTab.IsSelected) WorkspaceTabs.SelectedIndex = 0;
        if (invoices.Count == 0)
        {
            WorkbookLabel.Text = Purchase ? "Chưa chọn hóa đơn mua vào" : "Chưa chọn file Excel hóa đơn";
            WorkbookLabel.ToolTip = null;
        }
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
            var total = await Task.Run(() => services.ItemCatalog.Import(items, dialog.FileName));
            RefreshCatalogLabel();
            StatusLabel.Text = $"Đã xóa danh mục cũ và nạp {total:N0} mã hàng từ {Path.GetFileName(dialog.FileName)}.";
            ShowCatalogAudit(onlyWhenFindings: true);
        }
        catch (Exception error) { StatusLabel.Text = "Không nạp được file mã hàng: " + error.Message; }
    }

    private async void ChooseItemGroups_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", CheckFileExists = true, Multiselect = false,
            Title = "Chọn file danh sách nhóm vật tư, hàng hóa, dịch vụ xuất từ MISA" };
        if (dialog.ShowDialog(this) != true) return;
        var services = ((App)Application.Current).Services;
        StatusLabel.Text = "Đang đọc file nhóm vật tư, hàng hóa...";
        try
        {
            var groups = await ItemGroupWorkbook.ReadAsync(services.WorkbookReader, dialog.FileName);
            var count = await Task.Run(() => services.ItemGroups.Replace(groups, dialog.FileName));
            RefreshCatalogLabel();
            StatusLabel.Text = $"Đã nạp {count:N0} nhóm VTHH ({string.Join(", ", services.ItemGroups.Snapshot().Select(group => group.Code))}); "
                + "mã hàng mới sẽ bắt đầu bằng mã nhóm.";
            if (services.ItemCatalog.Count > 0) ShowCatalogAudit(onlyWhenFindings: true);
        }
        catch (Exception error) { StatusLabel.Text = "Không nạp được file nhóm vật tư, hàng hóa: " + error.Message; }
    }

    private void AuditCatalog_Click(object sender, RoutedEventArgs e) => ShowCatalogAudit(onlyWhenFindings: false);

    private void ShowCatalogAudit(bool onlyWhenFindings)
    {
        var services = ((App)Application.Current).Services;
        if (services.ItemCatalog.Count == 0)
        {
            if (!onlyWhenFindings) StatusLabel.Text = "Chưa nạp danh mục mã hàng để kiểm tra.";
            return;
        }
        var findings = CatalogAudit.Items(services.ItemCatalog.Snapshot(), services.ItemGroups);
        if (findings.Count == 0 && onlyWhenFindings) return;
        var groups = services.ItemGroups.Count == 0 ? "Chưa nạp nhóm VTHH: mã hàng mới sẽ theo dạng HH_n.\n\n" : "";
        MessageBox.Show(this, groups + (findings.Count == 0 ? "Không phát hiện vấn đề trong danh mục mã hàng."
                : string.Join("\n\n", findings.Select(finding => "• " + finding))),
            "Kiểm tra danh mục mã hàng", MessageBoxButton.OK, findings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void RefreshCatalogLabel()
    {
        var services = ((App)Application.Current).Services;
        var catalog = services.ItemCatalog;
        CatalogLabel.Text = catalog.Count == 0 ? "Chưa nạp danh mục mã hàng"
            : $"{catalog.Count:N0} mã · {Path.GetFileName(catalog.SourceFile ?? catalog.FilePath)}"
                + (services.ItemGroups.Count == 0 ? "" : $" · {services.ItemGroups.Count} nhóm VTHH");
        CatalogLabel.ToolTip = catalog.SourceFile;
        var names = invoices.SelectMany(row => row.Invoice.Lines)
            .Where(line => line.ItemCode.Trim().Length == 0)
            .GroupBy(line => ItemCatalogStore.NameKey(line.Name)).Select(group => group.First()).ToArray();
        if (catalog.Count == 0 || names.Length == 0) { CatalogDetail.Visibility = Visibility.Collapsed; return; }
        var missing = new List<string>();
        var ambiguous = new List<string>();
        foreach (var line in names)
        {
            // Shows the group prefix the new code will get, so a wrong group is caught before the run.
            try { if (catalog.Find(line.Name, line.Unit) is null) missing.Add($"{line.Name} → {services.ItemGroups.CodePrefix(line.Name, line.Unit)}"); }
            catch (AutomationException) { ambiguous.Add(line.Name); }
        }
        CatalogDetail.Text = (missing.Count == 0 ? $"Đủ mã cho {names.Length} tên hàng trong file hóa đơn."
                : $"{missing.Count}/{names.Length} tên hàng chưa có mã, sẽ tự tạo mới: {string.Join("; ", missing.Take(4))}{(missing.Count > 4 ? "…" : "")}")
            + (ambiguous.Count == 0 ? "" : $" Trùng tên nhiều mã: {string.Join("; ", ambiguous)}.");
        CatalogDetail.ToolTip = string.Join(Environment.NewLine, missing.Concat(ambiguous));
        CatalogDetail.Visibility = Visibility.Visible;
    }

    private void ChooseCustomers_Click(object sender, RoutedEventArgs e) => ChooseParties(PartyRole.Customer);

    private void ChooseSuppliers_Click(object sender, RoutedEventArgs e) => ChooseParties(PartyRole.Supplier);

    private async void ChooseParties(PartyRole role)
    {
        var label = PartyCatalogStore.Label(role);
        var dialog = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", CheckFileExists = true, Multiselect = false,
            Title = $"Chọn file danh sách {label} xuất từ MISA (Mã, Tên, Mã số thuế)" };
        if (dialog.ShowDialog(this) != true) return;
        var services = ((App)Application.Current).Services;
        StatusLabel.Text = $"Đang đọc file mã {label}...";
        try
        {
            var entries = await PartyCatalogWorkbook.ReadAsync(services.WorkbookReader, dialog.FileName, role);
            var count = await Task.Run(() => services.PartyCatalog.Replace(role, entries, dialog.FileName));
            RefreshPartyLabel();
            presentationInitialized = false;
            RefreshWorkflowPanel();
            StatusLabel.Text = $"Đã xóa danh sách cũ và nạp {count:N0} mã {label} từ {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception error) { StatusLabel.Text = $"Không nạp được file mã {label}: " + error.Message; }
    }

    private void RefreshPartyLabel()
    {
        var parties = ((App)Application.Current).Services.PartyCatalog;
        if (parties.CustomerCount + parties.SupplierCount == 0) { PartyRow.Visibility = Visibility.Collapsed; return; }
        PartyRow.Visibility = Visibility.Visible;
        PartyLabel.Text = $"{parties.CustomerCount:N0} mã khách hàng · {parties.SupplierCount:N0} mã nhà cung cấp";
        PartyLabel.ToolTip = string.Join(Environment.NewLine, new[] { parties.CustomerSource, parties.SupplierSource }.OfType<string>());
        if (Purchase)
        {
            // The tool types the listed supplier code (MST only without a list); list the suppliers the loaded list does not have.
            var suppliers = invoices.Select(row => row.Invoice.Seller).DistinctBy(seller => PartyCatalogStore.TaxKey(seller.TaxCode)).ToArray();
            var absent = parties.SupplierCount == 0 ? [] : suppliers.Where(seller => parties.FindByTax(seller.TaxCode).Count == 0).ToArray();
            PartyDetail.Visibility = suppliers.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            PartyDetail.Text = parties.SupplierCount == 0 ? "Chưa nạp danh sách nhà cung cấp: MISA tự tìm nhà cung cấp theo MST."
                : absent.Length == 0 ? $"Đủ {suppliers.Length} nhà cung cấp trong danh sách đã nạp."
                : $"{absent.Length}/{suppliers.Length} nhà cung cấp chưa có trong MISA (thêm trước khi nhập): "
                    + string.Join("; ", absent.Take(3).Select(seller => $"{seller.Name} ({seller.TaxCode})")) + (absent.Length > 3 ? "…" : "");
            PartyDetail.ToolTip = string.Join(Environment.NewLine, absent.Select(seller => $"{seller.Name} ({seller.TaxCode})"));
            return;
        }
        var buyers = invoices.Select(row => row.Invoice.Buyer)
            .Where(buyer => buyer.Code.Trim().Length == 0 && !BuyerCodes.IsConsumer(buyer.Name))
            .GroupBy(buyer => PartyCatalogStore.NameKey(buyer.Name)).Select(group => group.First()).ToArray();
        if (buyers.Length == 0) { PartyDetail.Visibility = Visibility.Collapsed; return; }
        var missing = new List<string>();
        var ambiguous = new List<string>();
        foreach (var buyer in buyers)
        {
            try { if (parties.FindCode(buyer.Name, buyer.TaxCode) is null) missing.Add(buyer.Name); }
            catch (AutomationException) { ambiguous.Add(buyer.Name); }
        }
        PartyDetail.Text = (missing.Count == 0 ? $"Đủ mã cho {buyers.Length} người mua chưa ghi mã trong Excel."
                : $"{missing.Count}/{buyers.Length} người mua chưa có mã{(parties.CustomerCount > 0 ? " (sẽ tạo mới trong MISA khi nhập)" : "")}: "
                    + $"{string.Join("; ", missing.Take(3))}{(missing.Count > 3 ? "…" : "")}")
            + (ambiguous.Count == 0 ? "" : $" Trùng tên nhiều mã: {string.Join("; ", ambiguous)}.");
        PartyDetail.ToolTip = string.Join(Environment.NewLine, missing.Concat(ambiguous));
        PartyDetail.Visibility = Visibility.Visible;
    }

    private void RefreshHolds()
    {
        workflow.RefreshHolds();
        foreach (var row in invoices) row.Refresh();
        if (InvoiceList.SelectedItem is InvoiceRow selected) ShowIssues(selected.Invoice);
    }

    private void RefreshChecked()
    {
        foreach (var row in invoices) row.RefreshChecked();
        CheckedCounter.Text = $"Đã chọn {workflow.CheckedCount:N0}/{invoices.Count:N0}";
    }

    private void CheckAll_Click(object sender, RoutedEventArgs e)
        => workflow.SetCheckedMany(invoices.Where(row => !row.Invoice.IsBlocked && !workflow.IsHeld(row.Invoice)).Select(row => row.Invoice.Fingerprint));

    private void UncheckAll_Click(object sender, RoutedEventArgs e) => workflow.SetCheckedMany([]);

    private void CheckPending_Click(object sender, RoutedEventArgs e)
        => workflow.SetCheckedMany(workflow.PendingInvoices.Select(invoice => invoice.Fingerprint));

    private InvoiceRow? contextRow;

    private void InvoiceList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        contextRow = null;
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(InvoiceList, source) is ListBoxItem { DataContext: InvoiceRow row } item)
        {
            contextRow = row;
            item.IsSelected = true;
        }
    }

    private void InvoiceList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (contextRow is not { } row || workflow.IsRunning) { e.Handled = true; return; }
        var canMark = workflow.CanMarkSaved(row.Invoice);
        MarkSavedItem.IsEnabled = canMark;
        MarkSavedItem.ToolTip = canMark
            ? "Dùng khi bạn đã tự nhập hóa đơn này vào MISA: tool sẽ không nhập lại và xóa checkpoint cũ của hóa đơn"
            : "Hóa đơn này đã lưu hoặc đã có trong MISA.";
        var ticked = TickedMarkable();
        MarkTickedSavedItem.Header = $"Đánh dấu đã lưu cho {ticked.Length} hóa đơn đang tick";
        MarkTickedSavedItem.Visibility = ticked.Length > 1 || ticked.Length == 1 && !ReferenceEquals(ticked[0], row.Invoice)
            ? Visibility.Visible : Visibility.Collapsed;
        var canReset = workflow.CanResetToReady(row.Invoice);
        ResetReadyItem.IsEnabled = canReset;
        ResetReadyItem.ToolTip = canReset
            ? "Dùng khi bạn đã tự xóa chứng từ của hóa đơn này trong MISA: tool xóa lịch sử đã lưu và cho nhập lại"
            : ((App)Application.Current).Services.IsPosted(row.Invoice)
                ? "Hóa đơn nằm trong danh sách đã ghi sổ nạp từ MISA; nạp lại danh sách đó sau khi xóa."
                : "Hóa đơn này chưa lưu.";
        var tickedReset = TickedResettable();
        ResetTickedReadyItem.Header = $"Chuyển về sẵn sàng nhập cho {tickedReset.Length} hóa đơn đang tick";
        ResetTickedReadyItem.Visibility = tickedReset.Length > 1 || tickedReset.Length == 1 && !ReferenceEquals(tickedReset[0], row.Invoice)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private InvoiceDocument[] TickedMarkable() => [.. invoices
        .Where(row => workflow.IsChecked(row.Invoice.Fingerprint) && workflow.CanMarkSaved(row.Invoice)).Select(row => row.Invoice)];

    private InvoiceDocument[] TickedResettable() => [.. invoices
        .Where(row => workflow.IsChecked(row.Invoice.Fingerprint) && workflow.CanResetToReady(row.Invoice)).Select(row => row.Invoice)];

    private async void ResetReady_Click(object sender, RoutedEventArgs e)
    {
        if (contextRow is { } row) await ResetToReadyAsync([row.Invoice]);
    }

    private async void ResetTickedReady_Click(object sender, RoutedEventArgs e) => await ResetToReadyAsync(TickedResettable());

    private async Task ResetToReadyAsync(IReadOnlyList<InvoiceDocument> targets)
    {
        if (workflow.IsRunning)
        {
            StatusLabel.Text = "Đang chạy Thêm vào MISA. Bấm Dừng ngay, chờ dừng hẳn rồi mới chuyển hóa đơn về sẵn sàng nhập.";
            return;
        }
        var saved = targets.Where(workflow.CanResetToReady).ToArray();
        if (saved.Length == 0) return;
        var numbers = string.Join(", ", saved.Take(8).Select(invoice => invoice.Identity.Number)) + (saved.Length > 8 ? "…" : "");
        if (MessageBox.Show(this,
                $"Chuyển {saved.Length} hóa đơn ({numbers}) về SẴN SÀNG NHẬP?\n\n"
                + "Chỉ làm khi bạn đã tự xóa chứng từ của hóa đơn này trong MISA.\n\n"
                + "• Lịch sử lưu của hóa đơn này trong tool bị xóa; lần chạy sau tool sẽ nhập lại.\n"
                + "• Trước khi nhập, tool vẫn kiểm tra số hóa đơn trong MISA và bỏ qua nếu chứng từ còn tồn tại.\n"
                + "• Không hoàn tác được.",
                "Chuyển về sẵn sàng nhập", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var (reset, error) = await workflow.ResetToReadyAsync(saved);
        foreach (var row in invoices) row.Refresh();
        if (InvoiceList.SelectedItem is InvoiceRow selected) ShowIssues(selected.Invoice);
        RefreshWorkflowPanel();
        RefreshRunSummary();
        StatusLabel.Text = error is null
            ? $"Đã chuyển {reset} hóa đơn về sẵn sàng nhập."
            : $"Đã chuyển {reset}/{saved.Length} hóa đơn. Lỗi: {error}";
    }

    private async void MarkSaved_Click(object sender, RoutedEventArgs e)
    {
        if (contextRow is { } row) await MarkSavedAsync([row.Invoice]);
    }

    private async void MarkTickedSaved_Click(object sender, RoutedEventArgs e) => await MarkSavedAsync(TickedMarkable());

    private async Task MarkSavedAsync(IReadOnlyList<InvoiceDocument> targets)
    {
        if (workflow.IsRunning)
        {
            StatusLabel.Text = "Đang chạy Thêm vào MISA. Bấm Dừng ngay, chờ dừng hẳn rồi mới đánh dấu hóa đơn đã lưu.";
            return;
        }
        var pending = targets.Where(workflow.CanMarkSaved).ToArray();
        if (pending.Length == 0) return;
        var numbers = string.Join(", ", pending.Take(8).Select(invoice => invoice.Identity.Number)) + (pending.Length > 8 ? "…" : "");
        if (MessageBox.Show(this,
                $"Đánh dấu {pending.Length} hóa đơn ({numbers}) là ĐÃ LƯU?\n\n"
                + "Chỉ làm khi bạn đã tự nhập hóa đơn này vào MISA bằng tay.\n\n"
                + "• Tool sẽ không nhập lại hóa đơn này nữa.\n"
                + "• Checkpoint và lịch sử chạy cũ của hóa đơn này (chưa hoàn thành, chưa rõ, lỗi…) bị xóa.\n"
                + "• Tool không kiểm tra lại trong MISA. Không hoàn tác được.",
                "Đánh dấu đã lưu", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var (marked, error) = await workflow.MarkSavedManuallyAsync(pending);
        foreach (var row in invoices) row.Refresh();
        if (InvoiceList.SelectedItem is InvoiceRow selected) ShowIssues(selected.Invoice);
        RefreshWorkflowPanel();
        RefreshRunSummary();
        StatusLabel.Text = error is null
            ? $"Đã đánh dấu {marked} hóa đơn là đã lưu (nhập tay) và xóa checkpoint cũ."
            : $"Đã đánh dấu {marked}/{pending.Length} hóa đơn. Lỗi: {error}";
    }

    private void RefreshRunSummary()
    {
        InvoiceHeader.Text = invoices.Count == 0 ? "Hóa đơn" : $"Hóa đơn ({invoices.Count:N0})";
        RefreshStateBadge();
    }

    private void RefreshStateBadge()
    {
        var state = workflow.State;
        if (state == shownState) return;
        shownState = state;
        var (fg, bg) = StatePalette.Of(state.Kind);
        RunBadge.Background = bg;
        RunDot.Fill = fg;
        RunBadgeText.Foreground = fg;
        RunBadgeText.Text = state.Label;
        RunBadge.ToolTip = string.Join(Environment.NewLine, new[] { state.Label, state.Detail, state.Hint }.Where(text => text.Length > 0));
        // Errors are shown by the error popup and kept in Hồ sơ; the footer badge tooltip carries the hint.
        Motion.Pulse(RunDot, state.IsBusy);
    }

    private void InvoiceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        workflow.SelectedInvoice = (InvoiceList.SelectedItem as InvoiceRow)?.Invoice;
        if (InvoiceList.SelectedItem is InvoiceRow row) { SelectedTitle.Text = row.DisplayIdentity; ShowIssues(row.Invoice); }
        else ShowWorkbookIssues();
        RefreshWorkflowPanel();
    }

    private void ShowIssues(InvoiceDocument invoice)
        => SetVisibleIssues(invoice.Issues.Concat(workflow.HoldIssues(invoice)).Concat(workflow.UnfinishedIssues(invoice)));

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
        try
        {
            if (SheetChoice.SelectedIndex >= 0 && PrepareMapping()) await ImportSelectedSheetAsync();
            else ResetMapping();
        }
        catch (Exception error) { ShowImportError(error); }
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
            var summary = ShowImported(imported, null);
            SaveImportSession(source.FilePath, sheet, imported);
            if (imported.Issues.Any(issue => issue.Code is "COLUMN_MISSING" or "TOTAL_MEANING_REQUIRED" or "COLUMN_AMBIGUOUS"))
            {
                MappingExpander.IsExpanded = true;
                OpenSourceSettings();
            }
            StatusLabel.Text = summary;
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception error) { if (IsCurrent(work)) ShowImportError(error); }
        finally { EndImport(work); }
    }

    /// <summary>Lists the invoices not yet posted in MISA and returns a status line saying what was left out.</summary>
    private string ShowImported(ImportResult imported, IReadOnlyList<string>? ticked)
    {
        var posted = PostedStore;
        var (pending, hidden) = posted.WithoutPosted(imported);
        ShowServices(imported.Services ?? []);
        shown = pending;
        // MISA numbers vouchers in entry order, so enter oldest first: issued date, then invoice number.
        var ordered = pending.Invoices
            .OrderBy(invoice => invoice.Issued ?? DateOnly.MaxValue)
            .ThenBy(invoice => long.TryParse(invoice.Identity.Number, out var number) ? number : long.MaxValue)
            .ThenBy(invoice => invoice.Identity.Number, StringComparer.Ordinal)
            .ToArray();
        invoices.Clear();
        foreach (var invoice in ordered) invoices.Add(new(invoice, workflow));
        workflow.InvoiceQueue = ordered;
        if (ticked is not null) workflow.SetCheckedMany(ticked);
        else workflow.KeepCheckedIn(ordered.Select(invoice => invoice.Fingerprint).ToHashSet(StringComparer.Ordinal));
        RefreshChecked();
        if (invoices.Count > 0) InvoiceList.SelectedIndex = 0;
        FitInvoiceList();
        RefreshCatalogLabel();
        RefreshPartyLabel();
        RefreshRunSummary();
        ShowWorkbookIssues();
        var blocked = ordered.Count(invoice => invoice.IsBlocked);
        var held = ordered.Count(workflow.IsHeld);
        return $"{ordered.Length:N0} hóa đơn chưa nhập"
            + (posted.Count == 0 ? " (chưa nạp danh sách đã ghi sổ)" : $" · bỏ {hidden:N0} hóa đơn đã ghi sổ")
            + (imported.Services is { Count: > 0 } services ? $" · {services.Count:N0} hóa đơn dịch vụ không nhập (tab Dịch vụ)" : "")
            + (imported.SkippedUnnumberedRows > 0 ? $" · bỏ {imported.SkippedUnnumberedRows:N0} dòng chưa có số" : "")
            + (imported.SkippedNegativeInvoices > 0 ? $" · bỏ {imported.SkippedNegativeInvoices:N0} hóa đơn số lượng âm" : "")
            + (blocked > 0 ? $" · {blocked:N0} hóa đơn cần bổ sung dữ liệu" : "")
            + (held > 0 ? $" · {held:N0} hóa đơn tạm hoãn (mặt hàng trùng tên nhiều mã)" : "");
    }

    private ImportSessionStore ImportSessions => Purchase ? ((App)Application.Current).Services.PurchaseImportSessions
        : ((App)Application.Current).Services.ImportSessions;

    private sealed record ServiceRow(string Title, string Subtitle, string Reason, string Items);

    private void ShowServices(IReadOnlyList<ServiceInvoice> services)
    {
        var vi = System.Globalization.CultureInfo.GetCultureInfo("vi-VN");
        ServiceList.ItemsSource = services.OrderBy(item => item.Issued ?? DateOnly.MaxValue).Select(item => new ServiceRow(
            $"HĐ {item.Number} · {FormatDay(item.Issued)} · {item.GrandTotal?.ToString("N0", vi) ?? "?"} đ",
            $"{item.Seller} · MST {item.SellerTaxCode}", item.Reason,
            string.Join("; ", item.Lines.Select(line => line.Name).Take(6)) + (item.Lines.Count > 6 ? "…" : ""))).ToArray();
        ServicesTab.Header = services.Count == 0 ? "Dịch vụ" : $"Dịch vụ ({services.Count:N0})";
        ServicesSummary.Text = services.Count == 0
            ? "Không có hóa đơn dịch vụ trong file: mọi hóa đơn là hàng hóa."
            : $"{services.Count:N0} hóa đơn dịch vụ/chi phí (phí ngân hàng, vé, xăng dầu, điện nước, ăn uống, phần mềm, thanh toán hợp đồng…) "
                + "không nhập vào Mua hàng nhập kho. Nếu nhận nhầm, sửa cột 'Phân loại' trong file Excel thành 'Hàng hóa' rồi mở lại file.";
    }

    private async void SaveImportSession(string filePath, string sheet, ImportResult imported)
    {
        try { await ImportSessions.SaveAsync(filePath, sheet, imported, workflow.InvoiceQueue.Select(invoice => invoice.Fingerprint).Where(workflow.IsChecked)); }
        catch (Exception error) { StatusLabel.Text = "Không lưu được danh sách hóa đơn để mở lại lần sau: " + error.Message; }
    }

    // Ticks change as invoices are saved during a run; keep them for the next start.
    private async void SaveTicks()
    {
        if (invoices.Count == 0) return;
        try { await ImportSessions.SaveCheckedAsync(workflow.InvoiceQueue.Select(invoice => invoice.Fingerprint).Where(workflow.IsChecked)); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Saving ticks failed: {0}", error.Message); }
    }

    /// <summary>Shows the invoices of the last import again after a restart or update, without re-opening the workbook.</summary>
    private async Task RestoreImportSessionAsync()
    {
        if (invoices.Count > 0 || importModel.IsBusy) return;
        var saved = await ImportSessions.LoadAsync();
        if (saved is not { Result.Invoices.Count: > 0 } || invoices.Count > 0 || importModel.IsBusy) return;
        importModel.Restore(saved.Result);
        WorkbookLabel.Text = Path.GetFileName(saved.FilePath);
        WorkbookLabel.ToolTip = $"{saved.FilePath} · trang tính {saved.Sheet} (khôi phục từ lần nhập trước)";
        var summary = ShowImported(saved.Result, saved.Checked);
        StatusLabel.Text = $"Đã khôi phục từ {Path.GetFileName(saved.FilePath)} (lần nhập trước): {summary}.";
    }

    private CancellationTokenSource BeginImport()
    {
        ClearImportResult();
        return importModel.BeginWork();
    }

    // Exactly four invoice cards are visible; the rest scroll. Measured after layout so font/DPI changes keep it exact.
    private void FitInvoiceList()
        => Dispatcher.BeginInvoke(() =>
        {
            if (InvoiceList.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement first || first.ActualHeight <= 0) return;
            InvoiceList.MaxHeight = 4 * (first.ActualHeight + first.Margin.Top + first.Margin.Bottom) + 2;
        }, DispatcherPriority.Loaded);
    private bool IsCurrent(CancellationTokenSource work) => importModel.IsCurrent(work);
    private void EndImport(CancellationTokenSource work) => importModel.EndWork(work);

    private void ClearImportResult()
    {
        importModel.ClearResult();
        shown = null;
        invoices.Clear();
        workflow.InvoiceQueue = [];
        workflow.SelectedInvoice = null;
        ShowServices([]);
        RefreshWorkflowPanel();
        SetVisibleIssues([]);
        SelectedTitle.Text = "Chọn một hóa đơn";
    }

    private void ShowWorkbookIssues()
    {
        SetVisibleIssues(shown?.Issues ?? []);
        SelectedTitle.Text = shown is { IsBlocked: true } ? "Thông tin cần bổ sung" : "Chọn một hóa đơn";
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
        try { await ImportSelectedSheetAsync(); }
        catch (Exception error) { ShowImportError(error); }
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
        "buyerCompany" => "DN mua (đơn vị mua)",
        "buyerTaxCode" => "MST người mua",
        "buyerAddress" => "Địa chỉ người mua",
        "buyerCode" => "Mã khách hàng",
        "buyerTaxPresence" => "Trạng thái MST",
        "buyerKind" => "Loại người mua",
        "buyerContact" => "Người liên hệ",
        "itemCode" => "Mã hàng",
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

    // Works without any Excel loaded: the voucher open in MISA is always read; the Excel side is the invoice with its number.
    private async void Reconcile_Click(object sender, RoutedEventArgs e)
    {
        if (workflow.IsRunning) return;
        if (workflow.SelectedInvoice is null && workflow.CurrentContext is { } current && !WorkflowRunner.IsTerminal(current.Snapshot.Status))
            workflow.SelectedInvoice = current.Invoice;
        try
        {
            editorOpen = null;
            popupDismissedIdentity = null;
            var response = await workflow.ReconcileAsync();
            StatusLabel.Text = response.Message;
            RefreshWorkflowPanel();
            UpdateComparisonPopup();
        }
        catch (Exception error) { StatusLabel.Text = error.Message; }
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

    // Runs every 220 ms: any failure is reported once in the status line and backs the probes off.
    private async void UiTimer_Tick(object? sender, EventArgs e)
    {
        try { await UiTickAsync(); }
        catch (Exception error)
        {
            var now = DateTimeOffset.UtcNow;
            editorProbeDue = connectionProbeDue = now.AddSeconds(3);
            StatusLabel.Text = "Lỗi cập nhật trạng thái (sẽ tự thử lại): " + error.Message;
        }
    }

    private async Task UiTickAsync()
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
            // Each probe is a UIA search over MISA's whole tree, which runs on MISA's UI thread. Polling it every
            // second while idle after a batch made MISA (and the PC) unresponsive, so probe only while the answer
            // can change the comparison popup: it is shown, or the editor state is still unknown.
            if (now >= editorProbeDue && workflow.Connection == MisaConnection.Connected && IsComparisonBound()
                && (comparisonWindow is not null || editorOpen is null))
            {
                editorProbeDue = now.AddMilliseconds(2000);
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
        var code = ErrorPrompt.Code(error.Message, error.Details);
        var hint = ErrorHints.For(code);
        ErrorPopupTitle.Text = code is null ? "Lỗi khi thêm vào MISA" : "Lỗi khi thêm vào MISA · " + code;
        ErrorPopupMessage.Text = error.Message;
        ErrorPopupHint.Text = hint ?? "";
        ErrorPopupHintBox.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorPopupDetails.Text = ErrorPrompt.DiagnosticSummary(error.Message, error.Details, workflow.PerformanceEntries);
        ErrorPopupDetailsBox.Visibility = Visibility.Visible;
        ErrorPopup.Visibility = Visibility.Visible;
        Motion.FadeIn(ErrorPopup, fromY: -6);
    }

    private void StopNow_Click(object sender, RoutedEventArgs e)
    {
        if (!workflow.IsRunning) { StatusLabel.Text = "Không có bước nào đang chạy."; return; }
        workflow.RequestStop();
        RefreshWorkflowPanel();
        RefreshStateBadge();
    }

    private void CloseErrorPopup_Click(object sender, RoutedEventArgs e)
        => ErrorPopup.Visibility = Visibility.Collapsed;

    private void CopyError_Click(object sender, RoutedEventArgs e)
    {
        if (lastWorkflowError is null) return;
        var text = workflow.ExportLastError(lastWorkflowError.Message, lastWorkflowError.Details, lastWorkflowError.OccurredAt);
        StatusLabel.Text = TryCopy(text, "loi") ?? "Đã copy lỗi; dán vào AI để được hỗ trợ sửa.";
    }

    private async void ExportErrorReport_Click(object sender, RoutedEventArgs e)
    {
        if (lastWorkflowError is not { } error || sender is not Button button) return;
        button.IsEnabled = false;
        try
        {
            var path = await ((App)Application.Current).Services.ExportFailureReportAsync(
                error.Message, error.Details, workflow.ExportPerformance());
            StatusLabel.Text = TryCopy(path, "bao-cao") ?? "Đã lưu báo cáo ZIP và copy đường dẫn: " + path;
        }
        catch (Exception failure)
        {
            StatusLabel.Text = "Không xuất được báo cáo: " + failure.Message;
        }
        finally { button.IsEnabled = true; }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        => await UpdatePrompt.CheckAsync(this, ((App)Application.Current).Services.Updates, interactive: true, status => StatusLabel.Text = status,
            workflow.ReleaseCheckpointForUpdateAsync);

    private void UpdateSettings_Click(object sender, RoutedEventArgs e)
        => UpdatePrompt.EditSettings(this, ((App)Application.Current).Services.Updates, status => StatusLabel.Text = status);

    private void CopyPerformance_Click(object sender, RoutedEventArgs e)
        => StatusLabel.Text = TryCopy(workflow.ExportPerformance(), "hieu-nang") ?? "Đã copy báo cáo hiệu năng vào clipboard.";

    // The clipboard is often held by RDP or clipboard managers (CLIPBRD_E_CANT_OPEN); retry, then fall back to a file.
    private static string? TryCopy(string text, string name)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try { Clipboard.SetDataObject(text, true); return null; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60 * attempt); }
        }
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"MisaManager-{name}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, text);
            return "Clipboard đang bị chương trình khác giữ; đã lưu nội dung vào " + path;
        }
        catch (Exception error) { return "Không copy được: " + error.Message; }
    }

    private bool IsComparisonBound()
    {
        var current = workflow.CurrentContext;
        var invoice = workflow.SelectedInvoice ?? current?.Invoice;
        return current is not null && invoice is not null && current.Invoice.Fingerprint == invoice.Fingerprint;
    }

    /// <summary>
    /// Mirrors the tool's data into the comparison window. It opens only once every value has been entered
    /// (items done, before Cất) or a reconciliation exists, closes as soon as the MISA tab closes, and
    /// refreshes whenever a new reconciliation arrives.
    /// </summary>
    private void UpdateComparisonPopup()
    {
        var current = workflow.CurrentContext;
        var state = workflow.State;
        var bound = IsComparisonBound() && current is not null;
        var wanted = bound && IsVisible && state.Kind != ToolStateKind.Disconnected && EntryFinished(current!)
            && (workflow.IsRunning || editorOpen == true || (editorOpen is null && comparisonWindow is not null));
        // A standalone "Đối chiếu hóa đơn" result stays up until closed or another invoice is selected.
        var check = !workflow.IsRunning && IsVisible && workflow.OpenVoucherCheck is { } latest
            && latest.Selection == workflow.SelectedInvoice?.Fingerprint ? latest : null;
        if (check is null && (!wanted || current is null))
        {
            CloseComparisonWindow();
            return;
        }
        var identity = check is not null ? $"open:{check.At.UtcTicks}"
            : $"{current!.Snapshot.RunId}:{current.Snapshot.Status}:{current.Reconciliations?.Count}:{state.Kind}:{state.Label}";
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
            comparisonWindow.UpdateComparison(check is not null ? BuildOpenVoucherModel(check) : BuildComparisonModel(current!, state));
        }
        comparisonWindow.SetReconcileEnabled(!workflow.IsRunning);
    }

    private static bool EntryFinished(WorkflowContext context)
    {
        if (context.Reconciliations is { Count: > 0 }) return true;
        var checkpoint = context.Snapshot.PausedFromStatus ?? context.Snapshot.Status;
        return checkpoint is >= WorkflowStatus.ItemsVerified and <= WorkflowStatus.Saved;
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
        var subtitle = $"{invoice.Partner().Name} · {FormatDay(invoice.Issued)} · {invoice.GrandTotal?.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("vi-VN")) ?? "?"} {invoice.Currency}";
        var observed = latest?.Result is { } result ? "đọc lúc " + result.ObservedAt.ToLocalTime().ToString("HH:mm:ss") : "";
        return new($"Hóa đơn {invoice.Identity.Number}", subtitle, kind, label, summary, observed, rows, !workflow.IsRunning);
    }

    private ComparisonModel BuildOpenVoucherModel(OpenVoucherCheck check)
    {
        var vi = System.Globalization.CultureInfo.GetCultureInfo("vi-VN");
        var read = check.Comparison;
        var result = read?.Result;
        var voucher = read is { VoucherNumber.Length: > 0 } ? "chứng từ " + read.VoucherNumber : "chứng từ đang mở";
        var observed = result is null ? "" : $"{char.ToUpperInvariant(voucher[0])}{voucher[1..]} · đọc lúc {result.ObservedAt.ToLocalTime():HH:mm:ss}";
        if (check.Invoice is not { } invoice)
        {
            var number = read?.InvoiceNumber is { Length: > 0 } shown ? shown : "?";
            var title = read is null ? "Đối chiếu hóa đơn" : $"Hóa đơn {number} (chỉ có trong MISA)";
            var misaSubtitle = read is null ? "" : string.Join(" · ", new[] { read.CustomerName, read.IssuedDate, read.GrandTotal }.Where(text => text.Length > 0));
            var rows = result is null ? [] : BuildComparisonRows(null, result);
            return read is null
                ? new(title, misaSubtitle, ToolStateKind.NeedsAction, "Chưa đọc được MISA",
                    check.Error + (ErrorHints.For(ErrorHints.FindCode(check.Error)) is { } hint ? " " + hint : ""), observed, rows, !workflow.IsRunning)
                : new(title, misaSubtitle, ToolStateKind.Ready, "Chỉ đọc MISA",
                    invoices.Count == 0 ? "Chưa nạp Excel hóa đơn: cột Excel để trống, cột MISA là toàn bộ dữ liệu đọc được."
                        : $"Excel đang nạp không có hóa đơn {number}: cột Excel để trống, cột MISA là toàn bộ dữ liệu đọc được.",
                    observed, rows, !workflow.IsRunning);
        }
        var compared = BuildComparisonRows(invoice, result);
        var mismatched = compared.Count(row => row.Kind == "mismatch");
        var (kind, label) = result is null ? (ToolStateKind.NeedsAction, "Chưa đọc được MISA")
            : mismatched == 0 ? (ToolStateKind.Saved, "Khớp với MISA") : (ToolStateKind.Error, $"Có {mismatched} trường lệch");
        var summary = result is null
            ? check.Error + (ErrorHints.For(ErrorHints.FindCode(check.Error)) is { } errorHint ? " " + errorHint : "")
            : mismatched == 0 ? $"Toàn bộ thông tin của {voucher} trong MISA khớp với hóa đơn Excel."
            : $"{mismatched} trường lệch giữa Excel và {voucher}. Dòng đỏ là chỗ cần kiểm tra.";
        var subtitle = $"{invoice.Partner().Name} · {FormatDay(invoice.Issued)} · {invoice.GrandTotal?.ToString("N0", vi) ?? "?"} {invoice.Currency}";
        return new($"Hóa đơn {invoice.Identity.Number}", subtitle, kind, label, summary, observed, compared, !workflow.IsRunning);
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

    private async void ClearImportedData_Click(object sender, RoutedEventArgs e)
    {
        if (!workflow.CanEditSource) return;
        if (MessageBox.Show(this,
                "Xóa toàn bộ dữ liệu đã nạp từ Excel?\n\n"
                + "• Danh sách hóa đơn và các chỉnh sửa trên hóa đơn\n"
                + "• Toàn bộ danh mục mã hàng (kể cả mã do tool đã tạo)\n"
                + "• Danh sách mã khách hàng và nhà cung cấp\n"
                + "• Danh sách nhóm vật tư, hàng hóa\n"
                + "• Danh sách hóa đơn đã ghi sổ\n\n"
                + "Sau khi xóa, hãy nạp lại danh mục mới nhất xuất từ MISA. Lịch sử nhập (chống nhập trùng) và cấu hình cột "
                + "được giữ lại. Không hoàn tác được.",
                "Xóa tất cả dữ liệu", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var services = ((App)Application.Current).Services;
        try
        {
            importModel.Reset();
            ClearImportResult();
            workflow.SetCheckedMany([]);
            ResetMapping();
            updatingSheet = true;
            SheetChoice.ItemsSource = null;
            updatingSheet = false;
            SourcePopup.IsOpen = false;
            WorkbookLabel.Text = Purchase ? "Chưa chọn hóa đơn mua vào" : "Chưa chọn file Excel hóa đơn";
            WorkbookLabel.ToolTip = null;
            services.ImportSessions.Clear();
            services.PurchaseImportSessions.Clear();
            await services.Drafts.ClearAsync();
            var removedItems = await Task.Run(services.ItemCatalog.Clear);
            await Task.Run(services.PartyCatalog.Clear);
            await Task.Run(services.PostedInvoices.Clear);
            await Task.Run(services.PostedPurchases.Clear);
            await Task.Run(services.ItemGroups.Clear);
            RefreshCatalogLabel();
            RefreshPartyLabel();
            presentationInitialized = false;
            RefreshWorkflowPanel();
            RefreshRunSummary();
            StatusLabel.Text = $"Đã xóa sạch dữ liệu: danh sách hóa đơn, {removedItems:N0} mã hàng, nhóm VTHH, mã khách hàng, nhà cung cấp và hóa đơn đã ghi sổ.";
        }
        catch (Exception error) { StatusLabel.Text = "Không xóa hết được dữ liệu Excel: " + error.Message; }
    }

    // Deferred so the closing context menu does not immediately dismiss the StaysOpen=False popup.
    private void OpenSourceSettings()
        => Dispatcher.BeginInvoke(() => SourcePopup.IsOpen = true, System.Windows.Threading.DispatcherPriority.Background);

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            RefreshLoadedMenuItems();
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void RefreshLoadedMenuItems()
    {
        var services = ((App)Application.Current).Services;
        var posted = services.PostedInvoices.Count == 0 ? "" : $" · {services.PostedInvoices.Count:N0} HĐ đã ghi sổ";
        MarkLoaded(OpenInvoicesItem, "Mở Excel hóa đơn…",
            invoices.Count == 0 ? null : $"{invoices.Count:N0} hóa đơn · {WorkbookLabel.Text}{posted}");
        var catalog = services.ItemCatalog;
        MarkLoaded(CatalogMenuItem, "Nạp mã hàng…",
            catalog.Count == 0 ? null : $"{catalog.Count:N0} mã{FileSuffix(catalog.SourceFile)}");
        var groups = services.ItemGroups;
        MarkLoaded(ItemGroupsMenuItem, "Nạp nhóm vật tư, hàng hóa…", groups.Count == 0 ? null
            : $"{groups.Count:N0} nhóm ({string.Join(", ", groups.Snapshot().Select(group => group.Code).Take(6))}"
                + $"{(groups.Count > 6 ? "…" : "")}){FileSuffix(groups.Source)}");
        var parties = services.PartyCatalog;
        MarkLoaded(CustomersMenuItem, "Nạp mã khách hàng…",
            parties.CustomerCount == 0 ? null : $"{parties.CustomerCount:N0} mã{FileSuffix(parties.CustomerSource)}");
        MarkLoaded(SuppliersMenuItem, "Nạp mã nhà cung cấp…",
            parties.SupplierCount == 0 ? null : $"{parties.SupplierCount:N0} mã{FileSuffix(parties.SupplierSource)}");
    }

    private static string FileSuffix(string? path) => string.IsNullOrWhiteSpace(path) ? "" : " · " + Path.GetFileName(path);

    private void MarkLoaded(MenuItem item, string title, string? detail)
    {
        if (detail is null)
        {
            item.Header = title;
            item.Icon = null;
            item.ClearValue(ForegroundProperty);
            return;
        }
        var success = (System.Windows.Media.Brush)FindResource("Success");
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Đã nạp: " + detail, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 });
        item.Header = header;
        item.Icon = new TextBlock { Text = "✓", Foreground = success, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        item.Foreground = success;
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
            PropertyChanged?.Invoke(this, new(nameof(CanCheck)));
            PropertyChanged?.Invoke(this, new(nameof(CheckTip)));
        }
        /// <summary>Held invoices (duplicate item names in the catalog) cannot be ticked.</summary>
        public bool CanCheck => !selection.IsHeld(Invoice);
        public string CheckTip => selection.HoldIssues(Invoice) is { Count: > 0 } issues
            ? string.Join(Environment.NewLine, issues.Select(issue => issue.Message))
            : "Tick để đưa hóa đơn này vào danh sách chạy";
        public InvoiceDocument Invoice { get; }
        public InvoiceRow(InvoiceDocument invoice, WorkflowViewModel selection)
        {
            Invoice = invoice;
            this.selection = selection;
        }
        public string DisplayIdentity => (Invoice.Identity.Series.Length == 0 ? "HĐ " : Invoice.Identity.Series + " ")
            + $"{Invoice.Identity.Number} · {Invoice.Partner().Name}";
        public string DisplayDate => Invoice.Issued?.ToString("dd/MM/yyyy") ?? "Thiếu";
        public int LineCount => Invoice.Lines.Length;
        private bool IsPosted => ((App)Application.Current).Services.IsPosted(Invoice);
        public string SourceRange => $"dòng {Invoice.Source.FirstRow}-{Invoice.Source.LastRow}";
        public string DisplaySummary => $"{DisplayDate} · {LineCount} mặt hàng · {Invoice.GrandTotal?.ToString("N0") ?? "?"} {Invoice.Currency}";
        public string DisplayStatus
        {
            get
            {
                // MISA's register outranks an earlier failed/discarded run; a tool Cất keeps its own label.
                if (IsPosted && LatestRun()?.Snapshot.Status != WorkflowStatus.Saved) return RunOutcome.PostedLabel;
                if (Invoice.IsBlocked) return "Thiếu dữ liệu";
                if (selection.IsHeld(Invoice)) return "Tạm hoãn";
                if (LatestRun() is { } record) return ProfileRecord.StatusLabel(record.Snapshot);
                return "Sẵn sàng nhập";
            }
        }
        /// <summary>Drives the list color: saved green, discarded rose, failed red, uncertain violet, blocked amber.</summary>
        public string StatusKind
        {
            get
            {
                if (IsPosted) return "saved";
                if (Invoice.IsBlocked || selection.IsHeld(Invoice)) return "attention";
                var snapshot = LatestRun()?.Snapshot;
                if (snapshot is not null && RunOutcome.IsExisting(snapshot)) return "existing";
                if (snapshot is not null && RunOutcome.IsUnfinished(snapshot)) return "attention";
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
            var history = (Application.Current.MainWindow as MainWindow)?.workflow.History;
            return current?.Invoice.Fingerprint == Invoice.Fingerprint ? current
                : history?.FirstOrDefault(run => run.Invoice.Fingerprint == Invoice.Fingerprint)
                    ?? history?.FirstOrDefault(run => WorkflowRunner.IsManualSavedFor(run, Invoice));
        }
    }
}





