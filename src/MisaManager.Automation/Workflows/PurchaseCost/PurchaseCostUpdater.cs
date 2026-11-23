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

    /// <summary>Called before the failed voucher is closed so diagnostics capture the real popup and focus.</summary>
    public Func<string, Exception, CancellationToken, Task>? FailureObserver { get; set; }

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
                var code = error is AutomationException automation ? automation.Code + ": " : "";
                failed.Add(invoice.Identity.Number + ": " + code + error.Message);
                // A failed filter/open leaves the MISA list in an unknown state.
                // Do not replace the number with the next invoice and risk
                // editing the wrong voucher.
                break;
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
        var editorOpened = false;
        var saveAttempted = false;
        var saved = false;
        var closed = false;
        try
        {
            var list = await MisaScreen.OpenAsync(input, window, listProfile, token, profile.Culture);
            var listGrid = list.Grid("vouchers");
            list.Track(await input.WriteGridFilterAsync(list.Frame, listGrid.Profile.AutomationId, listGrid.Column("number"), number, token));
            // DoubleClickGridRowAsync performs the authoritative row readback
            // against the filtered column. Avoid a second generic table read,
            // which can map R20's custom-grid providers to the wrong cell even
            // while the visible row is already the requested invoice.
            list.Track(await input.DoubleClickGridRowAsync(list.Frame, listGrid.Profile.AutomationId, listGrid.Column("number"), number, token));

            var voucher = await MisaScreen.OpenAsync(input, window, voucherProfile, token, profile.Culture);
            editorOpened = true;
            await voucher.SelectTabAsync("invoice", token);
            var openedNumber = (await voucher.ReadAsync("number", token))?.Trim() ?? "";
            if (!string.Equals(openedNumber, number.Trim(), StringComparison.Ordinal))
                throw new AutomationException("PURCHASE_IDENTITY_MISMATCH",
                    $"Đã mở chứng từ nhưng số hóa đơn đọc được '{openedNumber}', cần '{number.Trim()}'.");
            var editEnabled = voucherProfile.Elements.ContainsKey("edit")
                && await input.CommandEnabledNowAsync(voucherProfile.Element("edit").Selector, token);
            if (!editEnabled && voucherProfile.Elements.ContainsKey("unpost"))
            {
                await voucher.ClickAsync("unpost", token: token);
                if (!await input.CommandEnabledNowAsync(voucherProfile.Element("post").Selector, token))
                    throw new AutomationException("UNPOST_READBACK_MISMATCH", "MISA chưa xác nhận Bỏ ghi chứng từ.");
            }
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

            saveAttempted = true;
            await voucher.ClickAsync("post", token: token);
            if (profile.Screens.TryGetValue("purchase_posting_warning", out var warning))
                await input.ClickOwnedMessageBoxButtonAsync(window, warning.Element("yes").Selector,
                    warning.Setting("messageContains", ""), token);
            var remains = await NativeWait.UntilAsync(async () =>
            {
                if (!await input.WorkflowScopeExistsAsync(window, profile.EditorId, token)) return false;
                var frame = await input.TryBindWorkflowScopeAsync(window, profile.EditorId, null, token, waitForEnabled: true);
                return frame is not null && await input.CommandEnabledNowAsync(voucherProfile.Element("edit").Selector, token);
            }, ready: savedEditor => savedEditor, "PURCHASE_POST_PENDING", "Chưa xác nhận được Ghi sổ hóa đơn mua hàng.", token,
                timeoutMilliseconds: 15000, returnLastMismatch: true);
            if (!remains)
            {
                closed = !await input.WorkflowScopeExistsAsync(window, profile.EditorId, token);
                return;
            }
            saved = true;
            voucher = await MisaScreen.OpenAsync(input, window, voucherProfile, token, profile.Culture);
            await voucher.ClickAsync("close", leavesScope: true, token: token);
            closed = true;
            await NativeWait.UntilAsync(() => input.WorkflowScopeExistsAsync(window, profile.EditorId, token),
                exists => !exists, "PURCHASE_CLOSE_PENDING", "Hóa đơn chưa đóng sau khi Cất.", token, timeoutMilliseconds: 15000);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            try { if (FailureObserver is not null) await FailureObserver(number, error, token); }
            catch (Exception observeError) when (observeError is not OperationCanceledException)
            { System.Diagnostics.Trace.TraceWarning("Không chụp được UI lỗi CPBH.XANG {0}: {1}", number, observeError.Message); }
            throw;
        }
        finally
        {
            if (editorOpened && !closed && !(saveAttempted && !saved))
            {
                try
                {
                    var frame = await input.CaptureAsync(token);
                    await input.InvokeWorkflowAsync(frame, voucherProfile.Element("close").Selector, token,
                        leavesScope: true, discardChanges: !saved);
                    closed = true;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    System.Diagnostics.Trace.TraceWarning("Không đóng được hóa đơn mua hàng {0} sau lỗi: {1}", number, error.Message);
                }
            }
        }
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
