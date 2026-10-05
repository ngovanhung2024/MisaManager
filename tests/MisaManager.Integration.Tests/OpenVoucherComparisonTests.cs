using MisaManager.Core;

namespace MisaManager.Integration.Tests;

/// <summary>"Đối chiếu hóa đơn" outside a run: reads whichever sales voucher is open in the fake R20.</summary>
public sealed class OpenVoucherComparisonTests : UiTest
{
    private static readonly ItemCatalogEntry[] Catalog = [new("HH_07", "Hesman 60WG 18g", "Gói")];

    private static string Mismatches(ReconciliationResult result)
        => string.Join(Environment.NewLine, result.Values.Where(value => !value.Matched)
            .Select(value => $"{value.Field}: Excel '{value.Expected}' ≠ MISA '{value.Actual}' ({value.Source})"));

    [Fact]
    public Task OpenVoucherIsComparedFieldByFieldWithTheInvoice() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1011", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 22, 10_000, "5%"),
            Invoices.Line(1, "", "Hesman 60WG 18g", "Gói", 3, 16_000, "8%"));
        var entered = await harness.EnterWithoutSavingAsync(invoice, misa, token);
        Assert.Equal(WorkflowStatus.AwaitingSave, entered.Context.Snapshot.Status);

        var same = await harness.Gateway.CompareOpenVoucherAsync(invoice, misa.Window, token);

        Assert.True(same.Result.Matched, Mismatches(same.Result));
        Assert.StartsWith("PT0", same.VoucherNumber);
        Assert.Contains(same.Result.Values, value => value.Field == "item_row_count" && value.Actual == "2");
        Assert.Contains(same.Result.Values, value => value.Field == "line:" + invoice.Lines[1].LineId + ":code" && value.Actual == "HH_07");

        // Another Excel invoice against the same open voucher: different number and quantity are reported, nothing is typed.
        var other = Invoices.Build(TempDirectory(misa), "1012", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 20, 10_000, "5%"),
            Invoices.Line(1, "", "Hesman 60WG 18g", "Gói", 3, 16_000, "8%"));
        var inputs = harness.Input.ValueInputSequence;

        var different = await harness.Gateway.CompareOpenVoucherAsync(other, misa.Window, token);

        Assert.False(different.Result.Matched);
        var failed = different.Result.Values.Where(value => !value.Matched).Select(value => value.Field).ToArray();
        Assert.Contains("number", failed);
        Assert.Contains("line:" + other.Lines[0].LineId + ":quantity", failed);
        Assert.DoesNotContain("line:" + other.Lines[1].LineId + ":quantity", failed);
        Assert.Equal(inputs, harness.Input.ValueInputSequence);
        Assert.Empty(misa.State().Vouchers);
    }, catalog: Catalog);

    [Fact]
    public Task OpenVoucherIsPairedWithTheExcelInvoiceCarryingItsNumber() => WithFakeAsync(async (misa, harness, token) =>
    {
        var entered = Invoices.Build(TempDirectory(misa), "1014", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 22, 10_000, "5%"));
        Assert.Equal(WorkflowStatus.AwaitingSave, (await harness.EnterWithoutSavingAsync(entered, misa, token)).Context.Snapshot.Status);
        var selected = Invoices.Build(TempDirectory(misa), "1015", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 20, 10_000, "5%"));
        var inputs = harness.Input.ValueInputSequence;

        // The invoice selected in the tool (1015) is not the one open in MISA (1014): the number decides.
        var paired = await harness.Gateway.CompareOpenVoucherAsync([selected, entered], misa.Window, token);

        Assert.Same(entered, paired.Invoice);
        Assert.True(paired.Result.Matched, Mismatches(paired.Result));

        // No Excel invoice with that number: every MISA value is still read, with an empty Excel side.
        var misaOnly = await harness.Gateway.CompareOpenVoucherAsync([selected], misa.Window, token);

        Assert.Null(misaOnly.Invoice);
        Assert.Equal("1014", misaOnly.InvoiceNumber);
        Assert.All(misaOnly.Result.Values, value => Assert.Equal("", value.Expected));
        Assert.Contains(misaOnly.Result.Values, value => value.Field == "line:misa:0:code" && value.Actual == "HH_06");
        Assert.Contains(misaOnly.Result.Values, value => value.Field == "line:misa:0:quantity" && value.Actual is { Length: > 0 });
        Assert.Contains(misaOnly.Result.Values, value => value.Field == "grand_total" && value.Actual is { Length: > 0 });
        Assert.Equal(inputs, harness.Input.ValueInputSequence);
        Assert.Empty(misa.State().Vouchers);
    });

    [Fact]
    public Task WithoutAnOpenVoucherTheUserIsToldToOpenOne() => WithFakeAsync(async (misa, harness, token) =>
    {
        var invoice = Invoices.Build(TempDirectory(misa), "1013", Invoices.AV,
            Invoices.Line(0, "HH_06", "Rồng đỏ 200SL - Chai 100ml", "Chai", 1, 10_000, "5%"));

        var error = await Assert.ThrowsAsync<AutomationException>(() => harness.Gateway.CompareOpenVoucherAsync(invoice, misa.Window, token));

        Assert.Equal("EDITOR_NOT_FOUND", error.Code);
    });
}
