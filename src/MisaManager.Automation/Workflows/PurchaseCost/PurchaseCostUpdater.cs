using MisaManager.Core;

namespace MisaManager.Automation;

public sealed record PurchaseCostUpdateResult(int Requested, int Updated, IReadOnlyList<string> Failed);

/// <summary>Updates the CPBH.XANG cost item on existing gasoline purchase vouchers.</summary>
public sealed class PurchaseCostUpdater
{
    private const string CostItem = "CPBH.XANG";
    private readonly MisaWindowDiscovery windows;
    private readonly NativeInputService input;
    private readonly WorkflowProfile profile;

    public PurchaseCostUpdater(MisaWindowDiscovery windows, NativeInputService input, WorkflowProfile profile)
    {
        this.windows = windows;
        this.input = input;
        this.profile = profile;
    }

    public async Task<PurchaseCostUpdateResult> UpdateAsync(IReadOnlyList<InvoiceDocument> invoices,
        FuelPurchaseCompany rule, CancellationToken token = default, Action<string>? progress = null)
    {
        if (!profile.IsPurchase) throw new InvalidOperationException("CP xăng chỉ áp dụng cho chứng từ mua hàng.");
        var targets = invoices.Where(invoice => FuelPurchases.IsGasolineOnly(invoice, rule))
            .DistinctBy(invoice => invoice.InvoiceId).ToArray();
        var failed = new List<string>();
        var updated = 0;
        foreach (var invoice in targets)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                progress?.Invoke("Đang cập nhật CPBH.XANG cho hóa đơn " + invoice.Identity.Number + "...");
                await UpdateOneAsync(invoice.Identity.Number, token);
                updated++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failed.Add(invoice.Identity.Number + ": " + error.Message);
            }
        }
        return new(targets.Length, updated, failed);
    }

    private async Task UpdateOneAsync(string number, CancellationToken token)
    {
        var window = await FindSessionAsync(token);
        input.UseProfile(profile);
        var listProfile = profile.Screen(WorkflowProfile.PurchaseList);
        var voucherProfile = profile.Screen(WorkflowProfile.PurchaseVoucher);
        var list = await MisaScreen.OpenAsync(input, window, listProfile, token, profile.Culture);
        var listGrid = list.Grid("vouchers");
        list.Track(await input.WriteGridFilterAsync(list.Frame, listGrid.Profile.AutomationId, listGrid.Column("number"), number, token));
        var rows = await listGrid.ReadRowsAsync(["number"], token);
        if (rows.Count != 1 || !string.Equals(rows[0].GetValueOrDefault("number")?.Trim(), number.Trim(), StringComparison.Ordinal))
            throw new AutomationException("PURCHASE_NOT_FOUND", $"Không lọc được duy nhất hóa đơn số '{number}'.");
        list.Track(await input.DoubleClickGridRowAsync(list.Frame, listGrid.Profile.AutomationId, listGrid.Column("number"), number, token));

        var voucher = await MisaScreen.OpenAsync(input, window, voucherProfile, token, profile.Culture);
        await voucher.ClickAsync("edit", token: token);
        await voucher.SelectTabAsync("statistics", token);
        var detail = voucher.Grid("items");
        var detailRows = await detail.ReadRowsAsync(["code", "name"], token);
        if (detailRows.Count == 0) throw new AutomationException("PURCHASE_LINES_MISSING", "Hóa đơn không có dòng hàng để gán khoản mục CP.");
        for (var row = 0; row < detailRows.Count; row++)
        {
            token.ThrowIfCancellationRequested();
            var code = detailRows[row].GetValueOrDefault("code")?.Trim() ?? "";
            var name = detailRows[row].GetValueOrDefault("name")?.Trim() ?? "";
            if (code.Length == 0 && name.Length == 0) continue;
            await detail.SelectOptionAsync(row, "cost_item", "code", code, CostItem, token: token);
        }
        var verify = await detail.ReadRowsAsync(["code", "name", "cost_item"], token);
        if (verify.Any(row => (row.GetValueOrDefault("name")?.Trim().Length ?? 0) > 0
                && !string.Equals(row.GetValueOrDefault("cost_item")?.Trim(), CostItem, StringComparison.Ordinal)))
            throw new AutomationException("COST_ITEM_READBACK_MISMATCH", $"MISA chưa đọc lại đủ khoản mục '{CostItem}'.");

        await voucher.ClickAsync("save", token: token);
        var remains = await NativeWait.UntilAsync(async () =>
        {
            if (!await input.WorkflowScopeExistsAsync(window, profile.EditorId, token)) return false;
            var frame = await input.TryBindWorkflowScopeAsync(window, profile.EditorId, null, token, waitForEnabled: true);
            return frame is not null && await input.CommandEnabledNowAsync(voucherProfile.Element("edit").Selector, token);
        }, ready: saved => saved, "PURCHASE_SAVE_PENDING", "Chưa xác nhận được Cất hóa đơn mua hàng.", token,
            timeoutMilliseconds: 15000, returnLastMismatch: true);
        if (!remains) return;
        voucher = await MisaScreen.OpenAsync(input, window, voucherProfile, token, profile.Culture);
        await voucher.ClickAsync("close", leavesScope: true, token: token);
        await NativeWait.UntilAsync(() => input.WorkflowScopeExistsAsync(window, profile.EditorId, token),
            exists => !exists, "PURCHASE_CLOSE_PENDING", "Hóa đơn chưa đóng sau khi Cất.", token, timeoutMilliseconds: 15000);
    }

    private async Task<WindowIdentity> FindSessionAsync(CancellationToken token)
    {
        var candidates = await Task.Run(windows.FindCandidates, token);
        var sessions = candidates.Where(window => window.Title.StartsWith(profile.VersionTitle + " - ", StringComparison.Ordinal)).ToArray();
        if (sessions.Length != 1)
            throw new AutomationException("NOT_CONNECTED", sessions.Length == 0
                ? "Không thấy cửa sổ MISA đang mở dữ liệu kế toán."
                : "Có nhiều cửa sổ MISA cùng phiên; chỉ giữ một cửa sổ dữ liệu đang dùng.");
        return sessions[0];
    }
}
