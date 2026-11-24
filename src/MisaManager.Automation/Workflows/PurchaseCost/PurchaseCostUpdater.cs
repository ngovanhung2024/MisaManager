using MisaManager.Core;

namespace MisaManager.Automation;

public sealed record PurchaseCostUpdateResult(int Requested, int Updated, IReadOnlyList<string> Failed);
public sealed record PurchaseCostTraceEntry(DateTimeOffset At, long ElapsedMilliseconds, string Step,
    string Outcome, string Details, string? Exception = null);

/// <summary>Updates the CPBH.XANG cost item on existing gasoline purchase vouchers.</summary>
public sealed class PurchaseCostUpdater
{
    private const string CostItem = "CPBH.XANG";
    private readonly MisaWindowDiscovery windows;
    private readonly NativeInputService input;
    private readonly WorkflowProfile profile;
    private readonly object traceGate = new();
    private readonly List<PurchaseCostTraceEntry> trace = [];
    private DateTimeOffset traceStartedAt;

    /// <summary>Called before the failed voucher is closed so diagnostics capture the real popup and focus.</summary>
    public Func<string, Exception, CancellationToken, Task>? FailureObserver { get; set; }

    public PurchaseCostUpdater(MisaWindowDiscovery windows, NativeInputService input, WorkflowProfile profile)
    {
        this.windows = windows;
        this.input = input;
        this.profile = profile;
    }

    public IReadOnlyList<PurchaseCostTraceEntry> TraceSnapshot()
    {
        lock (traceGate) return trace.ToArray();
    }

    private void ResetTrace()
    {
        lock (traceGate)
        {
            trace.Clear();
            traceStartedAt = DateTimeOffset.UtcNow;
        }
    }

    private void RecordTrace(string step, string outcome, string details, Exception? error = null)
    {
        var at = DateTimeOffset.UtcNow;
        lock (traceGate)
        {
            trace.Add(new(at, traceStartedAt == default ? 0 : (long)(at - traceStartedAt).TotalMilliseconds,
                step, outcome, details, error?.ToString()));
            if (trace.Count > 4000) trace.RemoveAt(0);
        }
    }

    public async Task<PurchaseCostUpdateResult> UpdateAsync(IReadOnlyList<InvoiceDocument> invoices,
        FuelPurchaseCompany rule, CancellationToken token = default, Action<string>? progress = null,
        Func<InvoiceDocument, Task>? onAssigned = null)
    {
        ResetTrace();
        RecordTrace("batch", "started", $"Nhận {invoices.Count} hóa đơn nguồn; bắt đầu lọc hóa đơn chỉ có xăng/nhớt.");
        if (!profile.IsPurchase) throw new InvalidOperationException("CP xăng chỉ áp dụng cho chứng từ mua hàng.");
        var targets = invoices.Where(invoice => FuelPurchases.IsGasolineOnly(invoice, rule))
            .DistinctBy(invoice => invoice.InvoiceId).ToArray();
        RecordTrace("batch.targets", "readback", $"Có {targets.Length} hóa đơn mục tiêu: {string.Join(", ", targets.Select(invoice => invoice.Identity.Number))}.");
        var failed = new List<string>();
        var updated = 0;
        foreach (var invoice in targets)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                RecordTrace($"invoice:{invoice.Identity.Number}", "started", "Bắt đầu cập nhật CPBH.XANG.");
                progress?.Invoke("Đang cập nhật CPBH.XANG cho hóa đơn " + invoice.Identity.Number + "...");
                await UpdateOneAsync(invoice.Identity.Number, token,
                    onAssigned is null ? null : () => onAssigned(invoice), checkForOpenVoucher: updated == 0);
                updated++;
                RecordTrace($"invoice:{invoice.Identity.Number}", "completed", "Đã gán CPBH.XANG, Cất và đóng chứng từ.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                var code = error is AutomationException automation ? automation.Code + ": " : "";
                failed.Add(invoice.Identity.Number + ": " + code + error.Message);
                RecordTrace($"invoice:{invoice.Identity.Number}", "failed", error.Message, error);
                // A failed filter/open leaves the MISA list in an unknown state.
                // Do not replace the number with the next invoice and risk
                // editing the wrong voucher.
                break;
            }
        }
        RecordTrace("batch", failed.Count == 0 ? "completed" : "failed",
            $"Kết thúc CPBH.XANG: cập nhật {updated}/{targets.Length}; lỗi {failed.Count}.");
        return new(targets.Length, updated, failed);
    }

    private async Task UpdateOneAsync(string number, CancellationToken token, Func<Task>? onAssigned, bool checkForOpenVoucher)
    {
        RecordTrace($"invoice:{number}:session", "started", "Tìm cửa sổ MISA theo profile mua hàng.");
        var window = await FindSessionAsync(token);
        RecordTrace($"invoice:{number}:session", "readback", $"Đã bind phiên MISA HWND={window.WindowHandle}, PID={window.ProcessId}.");
        input.UseProfile(profile);
        var listProfile = profile.Screen(WorkflowProfile.PurchaseList);
        var voucherProfile = profile.Screen(WorkflowProfile.PurchaseVoucher);
        var editorOpened = false;
        var saveAttempted = false;
        var closed = false;
        try
        {
            if (checkForOpenVoucher && await input.WorkflowScopeExistsAsync(window, profile.EditorId, token))
                throw new AutomationException("EDITOR_STILL_OPEN", "Đóng chứng từ mua hàng đang mở trước khi lọc số hóa đơn.");
            RecordTrace($"invoice:{number}:list", "started", "Mở danh sách Mua hàng hóa, dịch vụ.");
            var list = await MisaScreen.OpenAsync(input, window, listProfile, token, profile.Culture);
            RecordTrace($"invoice:{number}:list", "readback", "Danh sách chứng từ đã mở.");
            var listGrid = list.Grid("vouchers");
            var filter = await input.WriteGridFilterAsync(list.Frame, listGrid.Profile.AutomationId, listGrid.Column("number"), number, token);
            list.Track(filter);
            RecordTrace($"invoice:{number}:filter", filter.Status,
                $"Đã xóa/gõ/Enter ô '{listGrid.Column("number")}'. value='{filter.Value}', source='{filter.Source}', reason='{filter.Reason ?? ""}'.");
            // DoubleClickGridRowAsync performs the authoritative row readback
            // against the filtered column. Avoid a second generic table read,
            // which can map R20's custom-grid providers to the wrong cell even
            // while the visible row is already the requested invoice.
            var selected = await input.DoubleClickGridRowAsync(list.Frame, listGrid.Profile.AutomationId, listGrid.Column("number"), number, token);
            list.Track(selected);
            RecordTrace($"invoice:{number}:row", "readback",
                $"Đã chọn dòng bằng chuột. value='{selected.Value}', source='{selected.Source}', reason='{selected.Reason ?? ""}'.");

            RecordTrace($"invoice:{number}:voucher", "started", "Chờ editor chứng từ mở.");
            var voucher = await MisaScreen.OpenAsync(input, window, voucherProfile, token, profile.Culture);
            editorOpened = true;
            RecordTrace($"invoice:{number}:voucher", "readback", "Editor chứng từ đã mở.");
            await voucher.SelectTabAsync("invoice", token);
            var openedNumber = (await voucher.ReadAsync("number", token))?.Trim() ?? "";
            RecordTrace($"invoice:{number}:identity", "readback", $"Số hóa đơn trong editor='{openedNumber}', cần='{number}'.");
            if (!string.Equals(openedNumber, number.Trim(), StringComparison.Ordinal))
                throw new AutomationException("PURCHASE_IDENTITY_MISMATCH",
                    $"Đã mở chứng từ nhưng số hóa đơn đọc được '{openedNumber}', cần '{number.Trim()}'.");
            var editEnabled = voucherProfile.Elements.ContainsKey("edit")
                && await input.CommandEnabledNowAsync(voucherProfile.Element("edit").Selector, token);
            RecordTrace($"invoice:{number}:commands", "readback", $"Nút Sửa bật={editEnabled}.");
            if (!editEnabled && voucherProfile.Elements.ContainsKey("unpost"))
            {
                RecordTrace($"invoice:{number}:unpost", "input", "Bấm Bỏ ghi vì editor đang ở trạng thái đã ghi.");
                await voucher.ClickAsync("unpost", token: token);
                var unposted = await input.CommandEnabledNowAsync(voucherProfile.Element("post").Selector, token);
                RecordTrace($"invoice:{number}:unpost", unposted ? "readback" : "failed", $"Sau Bỏ ghi, nút Ghi sổ bật={unposted}.");
                if (!unposted)
                    throw new AutomationException("UNPOST_READBACK_MISMATCH", "MISA chưa xác nhận Bỏ ghi chứng từ.");
            }
            RecordTrace($"invoice:{number}:edit", "input", "Bấm Sửa để mở chế độ chỉnh sửa.");
            await voucher.ClickAsync("edit", token: token);
            RecordTrace($"invoice:{number}:edit", "readback", "Đã gửi thao tác Sửa.");
            await voucher.SelectTabAsync("statistics", token);
            RecordTrace($"invoice:{number}:statistics", "readback", "Đã chọn tab 3. Thống kê.");
            var detail = voucher.Grid("items");
            var detailRows = await detail.ReadRowsAsync(["code", "name"], token);
            RecordTrace($"invoice:{number}:rows", "readback", $"Đọc {detailRows.Count} dòng hàng từ tab Thống kê.");
            if (detailRows.Count == 0) throw new AutomationException("PURCHASE_LINES_MISSING", "Hóa đơn không có dòng hàng để gán khoản mục CP.");
            for (var row = 0; row < detailRows.Count; row++)
            {
                token.ThrowIfCancellationRequested();
                var code = detailRows[row].GetValueOrDefault("code")?.Trim() ?? "";
                var name = detailRows[row].GetValueOrDefault("name")?.Trim() ?? "";
                if (code.Length == 0 && name.Length == 0) continue;
                var written = await detail.WriteCellAsync(row, "cost_item", "code", code, CostItem, CostItem,
                    NativeValueKind.Text, "Tab", profile.Culture, token, keyboardText: true);
                RecordTrace($"invoice:{number}:line:{row + 1}:cost_item", "readback",
                    $"Dòng mã='{code}', tên='{name}', đã nhập '{CostItem}', MISA đọc lại '{written}'.");
            }
            var verify = await detail.ReadRowsAsync(["code", "name", "cost_item"], token);
            RecordTrace($"invoice:{number}:cost_item.verify", "readback",
                "Đọc lại khoản mục: " + string.Join(" | ", verify.Select((line, index) =>
                    $"dòng {index + 1}: mã='{line.GetValueOrDefault("code")}', tên='{line.GetValueOrDefault("name")}', CP='{line.GetValueOrDefault("cost_item")}'")));
            if (verify.Any(row => (row.GetValueOrDefault("name")?.Trim().Length ?? 0) > 0
                    && !string.Equals(row.GetValueOrDefault("cost_item")?.Trim(), CostItem, StringComparison.Ordinal)))
                throw new AutomationException("COST_ITEM_READBACK_MISMATCH", $"MISA chưa đọc lại đủ khoản mục '{CostItem}'.");

            saveAttempted = true;
            var editorHandle = await input.BoundScopeHandleAsync(token);
            RecordTrace($"invoice:{number}:save", "input", "Bấm Cất.");
            await voucher.ClickAsync("save", leavesScope: true, token: token);
            var warningAnswered = false;
            await NativeWait.UntilAsync(async () =>
            {
                // Use the same owned-dialog probe as invoice entry. When no prompt is present it only checks native windows.
                if (!warningAnswered && await input.AnswerCostItemPromptAsync(editorHandle, token) is { } warning)
                {
                    warningAnswered = true;
                    RecordTrace($"invoice:{number}:save.warning", "readback", $"Đã trả lời cảnh báo: {warning.Text}");
                    return false;
                }
                var bound = await input.TryBindWorkflowScopeAsync(window, profile.EditorId, null, token, waitForEnabled: true);
                if (bound is not null) return await ShowsSavedAsync(token);
                return closed = !await input.WorkflowScopeExistsAsync(window, profile.EditorId, token);
            }, ready => ready, "SAVE_UNCERTAIN", "Chưa xác nhận được Cất hóa đơn mua hàng.", token,
                timeoutMilliseconds: 15000);
            RecordTrace($"invoice:{number}:save", "readback", closed ? "Editor đã tự đóng sau Cất." : "Đã xác nhận trạng thái đã Cất.");
            Exception? closeError = null;
            try
            {
                if (!closed) await CloseSavedVoucherAsync(window, voucherProfile, number, token);
                closed = true;
                RecordTrace($"invoice:{number}:close", "readback", "Đã xác nhận editor biến mất trước khi chuyển hóa đơn.");
            }
            catch (Exception error)
            {
                closeError = error;
                throw;
            }
            finally
            {
                try { if (onAssigned is not null) await onAssigned(); }
                catch (Exception error) when (closeError is not null)
                {
                    RecordTrace($"invoice:{number}:history", "failed", "Không lưu được lịch sử gán sau khi MISA đã Cất.", error);
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordTrace($"invoice:{number}", "failed", error.Message, error);
            try { if (FailureObserver is not null) await FailureObserver(number, error, token); }
            catch (Exception observeError) when (observeError is not OperationCanceledException)
            { System.Diagnostics.Trace.TraceWarning("Không chụp được UI lỗi CPBH.XANG {0}: {1}", number, observeError.Message); }
            throw;
        }
        finally
        {
            if (editorOpened && !closed && !saveAttempted)
            {
                try
                {
                    var frame = await input.CaptureAsync(token);
                    await input.InvokeWorkflowAsync(frame, voucherProfile.Element("close").Selector, token,
                        leavesScope: true, discardChanges: true);
                    closed = true;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    System.Diagnostics.Trace.TraceWarning("Không đóng được hóa đơn mua hàng {0} sau lỗi: {1}", number, error.Message);
                }
            }
        }
    }

    private async Task CloseSavedVoucherAsync(WindowIdentity window, ScreenProfile voucherProfile, string number, CancellationToken token)
    {
        var frame = await input.CaptureAsync(token);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            RecordTrace("close", "input", $"Bấm Đóng, lần thử {attempt + 1}.");
            await input.InvokeWorkflowAsync(frame, voucherProfile.Element("close").Selector, token, leavesScope: true);
            try
            {
                await NativeWait.UntilAsync(() => input.WorkflowScopeExistsAsync(window, profile.EditorId, token),
                    exists => !exists, "PURCHASE_CLOSE_PENDING", "Hóa đơn chưa đóng sau khi Cất.", token,
                    timeoutMilliseconds: 5000);
                RecordTrace("close", "readback", $"Scope editor đã biến mất sau lần thử {attempt + 1}.");
                return;
            }
            catch (AutomationException error) when (error.Code == "PURCHASE_CLOSE_PENDING" && attempt == 0)
            {
                // Same saved-state guard as the normal sales workflow: retry Đóng only after reading the editor again.
                var voucher = await MisaScreen.TryOpenAsync(input, window, voucherProfile, token, profile.Culture);
                if (voucher is null) return;
                await voucher.SelectTabAsync("invoice", token);
                var actual = (await voucher.ReadAsync("number", token))?.Trim();
                var saved = actual == number.Trim() && await ShowsSavedAsync(token);
                RecordTrace("close", saved ? "retry" : "failed",
                    $"Lần đóng đầu chưa làm scope biến mất; savedReadback={saved}.", error);
                if (!saved) throw;
                frame = voucher.Frame;
            }
        }
        throw new AutomationException("PURCHASE_CLOSE_PENDING", "Hóa đơn mua hàng chưa đóng sau khi Cất.");
    }

    private async Task<bool> ShowsSavedAsync(CancellationToken token)
    {
        if (profile.UnpostVoucher is { } unpost && await input.CommandEnabledNowAsync(unpost, token))
            return true;
        try
        {
            await input.RequireDisabledCommandAsync(await input.CaptureAsync(token), profile.SaveVoucher, token);
            return true;
        }
        catch (AutomationException error) when (error.Code == "COMMAND_STATE_MISMATCH") { return false; }
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
