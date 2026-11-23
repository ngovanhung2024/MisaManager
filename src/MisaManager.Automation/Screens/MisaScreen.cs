using MisaManager.Core;

namespace MisaManager.Automation;

/// <summary>
/// Profile-driven handle to one open MISA screen (voucher editor, dialog, module list).
/// New workflows (purchase vouchers, receipts, catalogs…) compose these calls instead of
/// raw UIA: every call resolves its element from <see cref="ScreenProfile"/>, verifies the
/// frame is still the same window/scope, and refreshes the frame after input.
/// </summary>
public sealed class MisaScreen
{
    private readonly NativeInputService input;
    private readonly string culture;

    private MisaScreen(NativeInputService input, WindowIdentity window, ScreenProfile profile, NativeFrame frame, string culture)
    {
        this.input = input;
        this.culture = culture;
        Window = window;
        Profile = profile;
        Frame = frame;
    }

    public WindowIdentity Window { get; }
    public ScreenProfile Profile { get; }
    public NativeFrame Frame { get; private set; }

    /// <summary>Binds the screen's scope inside the MISA session; null when the screen is not open.</summary>
    public static async Task<MisaScreen?> TryOpenAsync(NativeInputService input, WindowIdentity window, ScreenProfile profile,
        CancellationToken token = default, string culture = "vi-VN", bool waitForEnabled = false)
    {
        NativeFrame? frame;
        if (profile.Scope is { AutomationId.Length: > 0 } scope)
            frame = await input.TryBindWorkflowScopeAsync(window, scope.AutomationId, null, token, waitForEnabled);
        else
            frame = await input.BindAsync(window, token);
        return frame is null ? null : new MisaScreen(input, window, profile, frame, culture);
    }

    /// <summary>Waits until the screen is open and enabled (e.g. after clicking "Thêm").</summary>
    public static async Task<MisaScreen> OpenAsync(NativeInputService input, WindowIdentity window, ScreenProfile profile,
        CancellationToken token = default, string culture = "vi-VN", TimeSpan? timeout = null)
    {
        MisaScreen? screen = null;
        await NativeWait.UntilAsync(async () => screen = await TryOpenAsync(input, window, profile, token, culture, waitForEnabled: true),
            value => value is not null, "SCREEN_NOT_OPEN", $"Màn hình '{profile.Title}' chưa mở.", token, (int)(timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds);
        return screen!;
    }

    public async Task<string?> ReadAsync(string element, CancellationToken token = default)
        => Track(await input.ReadFieldAsync(Frame, Profile.Element(element).Selector, token)).Value;

    public async Task<IReadOnlyDictionary<string, string?>> ReadManyAsync(IEnumerable<string> elements, CancellationToken token = default)
    {
        var selectors = elements.Distinct(StringComparer.Ordinal).ToDictionary(key => key, key => Profile.Element(key).Selector, StringComparer.Ordinal);
        var fields = await input.ReadFieldsAsync(Frame, selectors, token);
        Frame = fields.Frame;
        return fields.Values.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal);
    }

    /// <summary>Types a value, commits it with <paramref name="commitKey"/> and verifies MISA's readback.</summary>
    public async Task<string?> WriteAsync(string element, string text, string? expected = null,
        NativeValueKind kind = NativeValueKind.Text, string? commitKey = "Tab", CancellationToken token = default)
        => Track(await input.WriteFieldAsync(Frame, Profile.Element(element).Selector, text, expected ?? text, kind, culture, commitKey, token)).Value;

    /// <summary>Clicks a command. Set <paramref name="leavesScope"/> when it closes this screen.</summary>
    public async Task ClickAsync(string element, bool leavesScope = false, CancellationToken token = default)
    {
        var readback = await input.InvokeWorkflowAsync(Frame, Profile.Element(element).Selector, token, leavesScope);
        if (!leavesScope) Frame = readback.Frame;
    }

    public async Task SelectTabAsync(string tab, CancellationToken token = default)
        => Track(await input.SelectTabAsync(Frame, Profile.Tab(tab), token));

    public MisaGrid Grid(string key) => new(this, input, Profile.Grid(key));

    internal Readback Track(Readback readback)
    {
        Frame = readback.Frame;
        return readback;
    }

    internal void Track(NativeFrame frame) => Frame = frame;
}

/// <summary>A profile-declared grid of a <see cref="MisaScreen"/>; columns are addressed by profile key.</summary>
public sealed class MisaGrid
{
    private readonly MisaScreen screen;
    private readonly NativeInputService input;

    internal MisaGrid(MisaScreen screen, NativeInputService input, ScreenGrid profile)
    {
        this.screen = screen;
        this.input = input;
        Profile = profile;
    }

    public ScreenGrid Profile { get; }

    public string Column(string key) => Profile.Columns.TryGetValue(key, out var caption) ? caption
        : throw new InvalidDataException($"Grid '{Profile.AutomationId}' thiếu cột '{key}'.");

    public Task<int> RowCountAsync(CancellationToken token = default) => input.GridRowCountAsync(screen.Frame, Profile.AutomationId, token);

    /// <summary>Reads the given columns (profile keys) of every data row, keyed by column key.</summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> ReadRowsAsync(IReadOnlyList<string> columns,
        CancellationToken token = default)
    {
        var captions = columns.Select(Column).ToArray();
        var table = await input.ReadTableAsync(screen.Frame, Profile.AutomationId, captions, token);
        screen.Track(table.Frame);
        return table.Rows.Select(row => (IReadOnlyDictionary<string, string>)columns.ToDictionary(key => key,
            key => row.TryGetValue(Column(key), out var value) ? value : "", StringComparer.Ordinal)).ToArray();
    }

    public async Task<string?> ReadCellAsync(int row, string column, string codeColumn, string expectedCode, CancellationToken token = default)
        => screen.Track(await input.ReadCellAsync(screen.Frame, new NativeCell(Profile.AutomationId, row, Column(column), Column(codeColumn), expectedCode), token)).Value;

    public async Task<string?> WriteCellAsync(int row, string column, string codeColumn, string expectedCode, string text,
        string? expected = null, NativeValueKind kind = NativeValueKind.Text, string commitKey = "Tab", string culture = "vi-VN",
        CancellationToken token = default, bool keyboardText = false)
        => screen.Track(await input.WriteCellAsync(screen.Frame,
            new NativeCell(Profile.AutomationId, row, Column(column), Column(codeColumn), expectedCode),
            text, expected ?? text, kind, culture, token, commitKey, keyboardText)).Value;

    public async Task<string?> SelectOptionAsync(int row, string column, string codeColumn, string expectedCode, string option,
        string culture = "vi-VN", CancellationToken token = default)
        => screen.Track(await input.SelectCellOptionAsync(screen.Frame,
            new NativeCell(Profile.AutomationId, row, Column(column), Column(codeColumn), expectedCode),
            option, culture, token)).Value;
}
