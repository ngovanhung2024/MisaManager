using System.IO;
using System.Windows;
using Microsoft.Win32;
using MisaManager.Automation;
using MisaManager.Core;

namespace MisaManager.App;

public partial class MainWindow
{
    private Dictionary<string, DateTimeOffset> purchaseCostHistory = new(StringComparer.Ordinal);

    private async Task SwitchModeAsync(VoucherKind next, bool cost)
    {
        if (next == mode && cost == fuelCostMode) return;
        if (!workflow.CanEditSource || importModel.IsBusy) { ApplyMode(); return; }
        var services = ((App)Application.Current).Services;
        workflow.SetPurchaseCostMode(cost, busy: true);
        try
        {
            importModel.Reset();
            ClearImportResult();
            workflow.SetCheckedMany([]);
            ResetMapping();
            updatingSheet = true;
            SheetChoice.ItemsSource = null;
            updatingSheet = false;
            mode = next;
            fuelCostMode = cost;
            services.SetMode(next, cost);
            ApplyMode();
            RefreshPartyLabel();
            await RestoreImportSessionAsync();
            if (invoices.Count == 0)
                StatusLabel.Text = cost ? "Mở danh sách hóa đơn xăng từ menu •••, rồi tick hóa đơn cần gán."
                    : Purchase ? "Mở hóa đơn mua vào từ menu •••." : "Mở Excel hóa đơn từ menu •••.";
        }
        catch (Exception error) { StatusLabel.Text = "Không chuyển được chế độ: " + error.Message; }
        finally
        {
            workflow.SetPurchaseCostMode(fuelCostMode);
            RefreshChecked();
        }
    }

    private async void FuelCost_Click(object sender, RoutedEventArgs e)
    {
        if (!workflow.CanEditSource || ((App)Application.Current).Services.Company.FuelRule() is null) return;
        await SwitchModeAsync(VoucherKind.Purchase, cost: true);
        if (fuelCostMode && invoices.Count == 0) await ChoosePurchaseCostSourceAsync();
    }

    private async Task ChoosePurchaseCostSourceAsync()
    {
        if (!workflow.CanEditSource || importModel.IsBusy) return;
        var dialog = new OpenFileDialog
        {
            Filter = "Hóa đơn mua vào (*.zip;*.html;*.htm;*.xlsx)|*.zip;*.html;*.htm;*.xlsx",
            CheckFileExists = true,
            Title = "Mở hóa đơn mua vào để lọc hóa đơn xăng/nhớt"
        };
        if (dialog.ShowDialog(this) != true) return;
        workflow.SetPurchaseCostMode(true, busy: true);
        RefreshChecked();
        try
        {
            var path = dialog.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? dialog.FileName : await ConvertArchiveAsync(dialog.FileName, purchase: true);
            if (path is not null) await OpenWorkbookAsync(path);
        }
        catch (Exception error) { ShowImportError(error); }
        finally
        {
            workflow.SetPurchaseCostMode(true);
            RefreshChecked();
        }
    }

    private async void StartPurchaseCost_Click(object sender, RoutedEventArgs e)
    {
        if (!fuelCostMode || !workflow.CanEditSource || importModel.IsBusy) return;
        var targets = invoices.Where(row => row.CanCheck && row.IsChecked).Select(row => row.Invoice).ToArray();
        if (targets.Length == 0) return;

        var services = ((App)Application.Current).Services;
        services.BeginUiBatch(targets[0].Identity.Number);
        ErrorPopup.Visibility = Visibility.Collapsed;
        workflow.SetPurchaseCostMode(true, busy: true);
        RefreshChecked();
        var outcome = "Lỗi";
        try
        {
            var result = await services.UpdatePurchaseCostsAsync(targets,
                progress: message => Dispatcher.Invoke(() => StatusLabel.Text = message),
                assigned: (invoice, at) => Dispatcher.InvokeAsync(() =>
                {
                    purchaseCostHistory[invoice.Identity.ToString()] = at;
                    foreach (var row in invoices.Where(row => row.Invoice.Identity == invoice.Identity))
                    {
                        row.IsChecked = false;
                        row.Refresh();
                    }
                    presentationInitialized = false;
                    RefreshWorkflowPanel();
                }).Task);
            var skipped = result.Skipped.Count == 0 ? "" : $" Bỏ qua {result.Skipped.Count:N0} hóa đơn chưa tìm thấy trong MISA.";
            if (result.Failed.Count > 0)
            {
                var message = "Gán CPBH.XANG thất bại: " + result.Failed[0];
                Workflow_ErrorRaised(this, new WorkflowErrorEventArgs(message,
                    services.PurchaseCostErrorDetails(string.Join(Environment.NewLine, result.Failed)), DateTimeOffset.Now));
                StatusLabel.Text = $"Đã gán {result.Updated:N0}/{result.Requested:N0} hóa đơn.{skipped} Đã dừng tại {result.Failed[0]}";
            }
            else
            {
                outcome = "Hoàn tất";
                StatusLabel.Text = $"Đã gán CPBH.XANG cho {result.Updated:N0} hóa đơn.{skipped}";
            }
            await ImportSessions.SaveCheckedAsync(CheckedFingerprints());
        }
        catch (Exception error)
        {
            Workflow_ErrorRaised(this, new WorkflowErrorEventArgs("Gán CPBH.XANG thất bại: " + error.Message,
                error.ToString(), DateTimeOffset.Now));
            StatusLabel.Text = "Không hoàn tất được lượt gán CPBH.XANG: " + error.Message;
        }
        finally
        {
            services.EndUiBatch(outcome);
            workflow.SetPurchaseCostMode(true);
            RefreshChecked();
        }
    }
}
