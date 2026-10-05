using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MisaManager.FakeMisa;

public sealed class ItemLookupWindow : Window, IGridSource
{
    private readonly FakeStore store;

    public ItemLookupWindow(FakeProfile profile, FakeStore store)
    {
        this.store = store;
        FakeLog.Write("F3 item lookup opened");
        Title = "Tìm nhanh vật tư, hàng hóa";
        Ui.Id(this, profile.ScopeId("item_lookup"));
        Width = 900;
        Height = 740;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var search = Ui.Combo(profile.Id("item_lookup", "search"), []);
        var grid = new FakeGrid(profile.GridId("item_lookup", "items"), 630)
            { FooterCountOffset = store.IncompleteItemLookup ? 1 : 0 };
        string Column(string key) => store.ItemLookupUsesVoucherColumns
            ? profile.Column("sales_voucher", "items", key) : profile.Column("item_lookup", "items", key);
        grid.Bind([
            new("code", Column("code"), 130),
            new("name", Column("name"), 550),
            new("unit", Column("unit"), 90)
        ], this);
        search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            FakeLog.Write("catalog query <- '" + search.Text + "'");
            grid.Refresh(force: true);
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            DialogResult = false;
        };
        Content = new StackPanel { Children = { search, grid } };
    }

    int IGridSource.RowCount => store.Items.Count;
    string IGridSource.Value(int row, string key) => key switch
    {
        "code" => store.Items[row].Code,
        "name" => store.Items[row].Name,
        "unit" => store.Items[row].Unit,
        _ => ""
    };
}
